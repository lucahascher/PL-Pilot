using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Agent
{
    public sealed class ToolCall
    {
        public string Id;
        public string Name;
        public string Arguments = "";
    }

    public sealed class AssistantTurn
    {
        public string Content = "";
        public string Reasoning = "";
        public List<ToolCall> ToolCalls = new List<ToolCall>();
        public string FinishReason;
        public JObject Usage;
    }

    public interface IStreamSink
    {
        void OnContent(string delta);
        void OnReasoning(string delta);
        void OnToolCallStarted(int index, string name);
    }

    /// <summary>Minimal streaming client for OpenAI compatible /chat/completions endpoints.</summary>
    public sealed class OpenAiClient : IDisposable
    {
        private HttpClient _http;
        private string _httpKey;

        static OpenAiClient() => UseModernTls();

        private static int _tlsConfigured;

        /// <summary>
        /// Inside PL/SQL Developer the CLR is hosted by a native program without a target framework, so .NET runs in its
        /// legacy TLS mode (SSL 3 + TLS 1.0 only, verified with version 17; "system default" is rejected when connecting).
        /// This turns the legacy mode off – it must run before anything reads the .NET network settings (Bootstrap does
        /// it first) – and lets Windows choose the version like a normal .NET 4.8 program: TLS 1.2, TLS 1.3 on Windows 11.
        /// If the legacy mode was already fixed by then, TLS 1.2 is used.
        /// </summary>
        public static void UseModernTls()
        {
            if (Interlocked.Exchange(ref _tlsConfigured, 1) != 0) return;
            try
            {
                AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
                AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
                var switches = typeof(ServicePointManager).Assembly.GetType("System.LocalAppContextSwitches");
                var legacy = switches?.GetProperty("DontEnableSystemDefaultTlsVersions", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                bool systemDefaultWorks = legacy != null && !(bool)legacy.GetValue(null);
                ServicePointManager.SecurityProtocol = systemDefaultWorks ? SecurityProtocolType.SystemDefault : SecurityProtocolType.Tls12;
                Util.Log.Info("TLS: " + (systemDefaultWorks ? "versions chosen by Windows" : "TLS 1.2 (the .NET legacy TLS mode could not be turned off)"));
            }
            catch (Exception ex)
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                Util.Log.Warn("TLS setup: " + ex.Message + "; using TLS 1.2");
            }
        }

        private readonly object _httpLock = new object();

        private HttpClient Http(AgentConfig cfg)
        {
            lock (_httpLock) // helper agents send requests in parallel
            {
                var key = cfg.UseSystemProxy + "|" + cfg.RequestTimeoutSeconds + "|" + cfg.TrustedCertificates;
                if (_http != null && _httpKey == key) return _http;
                // not disposed: a parallel request may still use the previous client; it is released by the GC
                // The Windows login goes to the company proxy only, never to the LLM server itself (it uses the API key):
                // otherwise a server or an attacker in between could request NTLM/Kerberos authentication and capture it.
                var handler = new HttpClientHandler { UseProxy = cfg.UseSystemProxy, UseDefaultCredentials = false };
                if (cfg.UseSystemProxy) handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
                var pins = Certificates.ParseThumbprints(cfg.TrustedCertificates);
                handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                    Certificates.Validate(request.RequestUri, cert, chain, errors, pins);
                _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(30, cfg.RequestTimeoutSeconds)) };
                _httpKey = key;
                return _http;
            }
        }

        /// <summary>A readable message for connection errors (certificate and TLS problems instead of .NET's generic text).</summary>
        private static LlmException ConnectionError(string url, HttpRequestException ex)
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
            var problem = Certificates.LastProblem(host);
            if (problem != null) return new LlmException(L.T("The HTTPS certificate of {0} is not accepted: {1}", host, problem));
            if (ex.InnerException is WebException we && we.Status == WebExceptionStatus.SecureChannelFailure)
                return new LlmException(L.T("Could not establish a secure (TLS) connection to {0}. The server may require a TLS version or cipher that Windows on this PC does not offer. ({1})",
                    url, ex.GetBaseException().Message));
            return new LlmException(L.T("Could not reach {0}: {1}", url, ex.GetBaseException().Message));
        }

        /// <summary>
        /// .NET Framework allows only 2 parallel connections per server for desktop programs, which would queue the
        /// requests of parallel helper agents. Raised for the LLM endpoint only (not process wide).
        /// </summary>
        private static void AllowParallelRequests(string url)
        {
            try
            {
                var sp = ServicePointManager.FindServicePoint(new Uri(url));
                if (sp.ConnectionLimit < 16) sp.ConnectionLimit = 16;
                sp.UseNagleAlgorithm = false; // small request bodies are sent at once instead of waiting for an ACK
            }
            catch (Exception ex) { Util.Log.Warn("Could not configure the connection to " + url + ": " + ex.Message); }
        }

        public async Task<AssistantTurn> StreamChatAsync(AgentConfig cfg, JArray messages, JArray tools, IStreamSink sink, CancellationToken ct)
        {
            var body = new JObject
            {
                ["model"] = cfg.Model,
                ["messages"] = messages,
                ["stream"] = true,
                ["temperature"] = cfg.Temperature,
            };
            if (cfg.MaxTokens > 0) body["max_tokens"] = cfg.MaxTokens;
            if (tools != null && tools.Count > 0)
            {
                body["tools"] = tools;
                body["tool_choice"] = "auto";
            }
            if (cfg.RequestUsage && !UsageUnsupported.ContainsKey(cfg.ChatCompletionsUrl))
                body["stream_options"] = new JObject { ["include_usage"] = true };
            if (!string.IsNullOrWhiteSpace(cfg.ExtraBodyJson))
            {
                try { body.Merge(JObject.Parse(cfg.ExtraBodyJson), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace }); }
                catch (Exception ex) { throw new LlmException("ExtraBodyJson in the settings is not valid JSON: " + ex.Message); }
            }

            var response = await SendAsync(cfg, body, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && body["stream_options"] != null && ((int)response.StatusCode == 400 || (int)response.StatusCode == 422))
            {
                // Some servers reject unknown fields: retry once without the usage request.
                var err = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                response.Dispose();
                Util.Log.Warn("Server rejected stream_options, retrying without: " + Truncate(ExtractError(err), 300));
                UsageUnsupported[cfg.ChatCompletionsUrl] = true;
                body.Remove("stream_options");
                response = await SendAsync(cfg, body, ct).ConfigureAwait(false);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new LlmException(L.T("LLM server returned {0} {1}: {2}", (int)response.StatusCode, response.ReasonPhrase, Truncate(ExtractError(err), 2000)));
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (mediaType.Contains("json"))
                {
                    // Server ignored "stream": parse a normal completion.
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    return ParseCompletion(json, sink);
                }

                return await ReadStreamAsync(response, sink, ct).ConfigureAwait(false);
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> UsageUnsupported =
            new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private async Task<HttpResponseMessage> SendAsync(AgentConfig cfg, JObject body, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, cfg.ChatCompletionsUrl)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            // .NET Framework would otherwise send "Expect: 100-continue" and wait (up to 350 ms) before sending the body
            request.Headers.ExpectContinue = false;
            AllowParallelRequests(cfg.ChatCompletionsUrl);
            if (!string.IsNullOrEmpty(cfg.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            try
            {
                return await Http(cfg).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LlmException(L.T("The request to {0} timed out.", cfg.ChatCompletionsUrl));
            }
            catch (HttpRequestException ex)
            {
                throw ConnectionError(cfg.ChatCompletionsUrl, ex);
            }
        }

        private static async Task<AssistantTurn> ReadStreamAsync(HttpResponseMessage response, IStreamSink sink, CancellationToken ct)
        {
            var turn = new AssistantTurn();
            var calls = new SortedDictionary<int, ToolCall>();
            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            bool inThinkTag = false;

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (ct.Register(() => { try { stream.Dispose(); } catch { } }))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while (true)
                {
                    try { line = await reader.ReadLineAsync().ConfigureAwait(false); }
                    catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                    if (line == null) break;
                    ct.ThrowIfCancellationRequested();
                    if (!line.StartsWith("data:")) continue;
                    var data = line.Substring(5).Trim();
                    if (data.Length == 0) continue;
                    if (data == "[DONE]") break;

                    JObject chunk;
                    try { chunk = JObject.Parse(data); }
                    catch { continue; }

                    if (chunk["error"] != null) throw new LlmException("LLM server error: " + ExtractError(chunk.ToString()));
                    if (chunk["usage"] is JObject usage) turn.Usage = usage;

                    var choice = (chunk["choices"] as JArray)?.FirstOrDefault() as JObject;
                    if (choice == null) continue;
                    var finish = (string)choice["finish_reason"];
                    if (!string.IsNullOrEmpty(finish)) turn.FinishReason = finish;

                    var delta = choice["delta"] as JObject;
                    if (delta == null) continue;

                    var r = (string)delta["reasoning_content"] ?? (string)delta["reasoning"];
                    if (!string.IsNullOrEmpty(r)) { reasoning.Append(r); sink?.OnReasoning(r); }

                    var c = (string)delta["content"];
                    if (!string.IsNullOrEmpty(c))
                    {
                        // Some servers inline the chain of thought as <think>...</think>.
                        SplitThink(c, ref inThinkTag, out var visible, out var thought);
                        if (thought.Length > 0) { reasoning.Append(thought); sink?.OnReasoning(thought); }
                        if (visible.Length > 0) { content.Append(visible); sink?.OnContent(visible); }
                    }

                    if (delta["tool_calls"] is JArray tcs)
                    {
                        foreach (var tcToken in tcs)
                        {
                            var tc = (JObject)tcToken;
                            int index = (int?)tc["index"] ?? calls.Count;
                            if (!calls.TryGetValue(index, out var call))
                            {
                                call = new ToolCall();
                                calls[index] = call;
                            }
                            var id = (string)tc["id"];
                            if (!string.IsNullOrEmpty(id)) call.Id = id;
                            var fn = tc["function"] as JObject;
                            if (fn != null)
                            {
                                var name = (string)fn["name"];
                                if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(call.Name))
                                {
                                    call.Name = name;
                                    sink?.OnToolCallStarted(index, name);
                                }
                                var args = fn["arguments"];
                                if (args != null)
                                    call.Arguments += args.Type == JTokenType.String ? (string)args : args.ToString(Formatting.None);
                            }
                        }
                    }
                }
            }

            turn.Content = content.ToString();
            turn.Reasoning = reasoning.ToString();
            turn.ToolCalls = calls.Values.Where(c => !string.IsNullOrEmpty(c.Name)).ToList();
            return turn;
        }

        private static AssistantTurn ParseCompletion(JObject json, IStreamSink sink)
        {
            if (json["error"] != null) throw new LlmException("LLM server error: " + ExtractError(json.ToString()));
            var turn = new AssistantTurn { Usage = json["usage"] as JObject };
            var choice = (json["choices"] as JArray)?.FirstOrDefault() as JObject;
            var msg = choice?["message"] as JObject;
            if (msg == null) throw new LlmException("Unexpected response from LLM server: " + Truncate(json.ToString(), 500));
            turn.FinishReason = (string)choice["finish_reason"];
            turn.Reasoning = (string)msg["reasoning_content"] ?? (string)msg["reasoning"] ?? "";
            bool inThink = false;
            SplitThink((string)msg["content"] ?? "", ref inThink, out var visible, out var thought);
            turn.Content = visible;
            turn.Reasoning += thought;
            if (turn.Reasoning.Length > 0) sink?.OnReasoning(turn.Reasoning);
            if (turn.Content.Length > 0) sink?.OnContent(turn.Content);
            if (msg["tool_calls"] is JArray tcs)
            {
                foreach (JObject tc in tcs.OfType<JObject>())
                {
                    var fn = tc["function"] as JObject;
                    var args = fn?["arguments"];
                    turn.ToolCalls.Add(new ToolCall
                    {
                        Id = (string)tc["id"],
                        Name = (string)fn?["name"],
                        Arguments = args == null ? "{}" : args.Type == JTokenType.String ? (string)args : args.ToString(Formatting.None),
                    });
                }
            }
            return turn;
        }

        private static void SplitThink(string text, ref bool inThink, out string visible, out string thought)
        {
            var v = new StringBuilder();
            var t = new StringBuilder();
            int pos = 0;
            while (pos < text.Length)
            {
                if (inThink)
                {
                    int end = text.IndexOf("</think>", pos, StringComparison.Ordinal);
                    if (end < 0) { t.Append(text, pos, text.Length - pos); break; }
                    t.Append(text, pos, end - pos);
                    pos = end + 8;
                    inThink = false;
                }
                else
                {
                    int start = text.IndexOf("<think>", pos, StringComparison.Ordinal);
                    if (start < 0) { v.Append(text, pos, text.Length - pos); break; }
                    v.Append(text, pos, start - pos);
                    pos = start + 7;
                    inThink = true;
                }
            }
            visible = v.ToString();
            thought = t.ToString();
        }

        /// <summary>GET /models — used by the settings dialog's "Test connection".</summary>
        public async Task<List<string>> ListModelsAsync(AgentConfig cfg, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, cfg.ModelsUrl);
            if (!string.IsNullOrEmpty(cfg.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            try
            {
                using (var response = await Http(cfg).SendAsync(request, ct).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new LlmException($"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(ExtractError(text), 500)}");
                    var json = JObject.Parse(text);
                    return (json["data"] as JArray ?? json["models"] as JArray ?? new JArray())
                        .Select(m => m.Type == JTokenType.String ? (string)m : (string)m["id"] ?? (string)m["name"])
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                }
            }
            catch (HttpRequestException ex)
            {
                throw ConnectionError(cfg.ModelsUrl, ex);
            }
        }

        private static string ExtractError(string body)
        {
            try
            {
                var j = JObject.Parse(body);
                var e = j["error"];
                if (e is JObject eo) return (string)eo["message"] ?? eo.ToString(Formatting.None);
                if (e != null) return e.ToString();
                return (string)j["message"] ?? (string)j["detail"] ?? body;
            }
            catch
            {
                return body;
            }
        }

        private static string Truncate(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "...";

        public void Dispose() => _http?.Dispose();
    }

    public sealed class LlmException : Exception
    {
        public LlmException(string message) : base(message) { }
    }
}
