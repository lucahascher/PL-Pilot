using System;
using System.Collections.Concurrent;
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

namespace PlsqlDevAgent.Agent.Mcp
{
    /// <summary>An error of an MCP server or of the connection to it (the message is meant for the user and the model).</summary>
    public sealed class McpException : Exception
    {
        public McpException(string message) : base(message) { }
    }

    /// <summary>A tool as listed by an MCP server (tools/list).</summary>
    public sealed class McpToolInfo
    {
        public string Name, Title, Description;
        public JObject InputSchema;
        public bool? ReadOnlyHint, DestructiveHint;

        public static McpToolInfo Parse(JObject t)
        {
            var ann = t["annotations"] as JObject;
            return new McpToolInfo
            {
                Name = (string)t["name"] ?? "",
                Title = (string)t["title"] ?? (string)ann?["title"],
                Description = (string)t["description"] ?? "",
                InputSchema = t["inputSchema"] as JObject,
                ReadOnlyHint = ann?["readOnlyHint"]?.Type == JTokenType.Boolean ? (bool?)ann["readOnlyHint"] : null,
                DestructiveHint = ann?["destructiveHint"]?.Type == JTokenType.Boolean ? (bool?)ann["destructiveHint"] : null,
            };
        }

        /// <summary>The mode when the user did not choose one: the server's read-only hint, otherwise "changes data".</summary>
        public string DefaultMode => ReadOnlyHint == true ? McpServerConfig.Read : McpServerConfig.Write;

        public JObject ToJson() => new JObject
        {
            ["name"] = Name, ["title"] = Title, ["description"] = Description,
            ["readOnly"] = ReadOnlyHint, ["destructive"] = DestructiveHint, ["defaultMode"] = DefaultMode,
        };
    }

    /// <summary>A prompt template of an MCP server (prompts/list); offered as the slash command /server:name.</summary>
    public sealed class McpPromptInfo
    {
        public string Name, Title, Description;
        public List<(string Name, string Description, bool Required)> Arguments = new List<(string, string, bool)>();

        public static McpPromptInfo Parse(JObject p) => new McpPromptInfo
        {
            Name = (string)p["name"] ?? "",
            Title = (string)p["title"],
            Description = (string)p["description"] ?? "",
            Arguments = (p["arguments"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(a => !string.IsNullOrEmpty((string)a["name"]))
                .Select(a => ((string)a["name"], (string)a["description"] ?? "", (bool?)a["required"] == true)).ToList(),
        };

        /// <summary>"&lt;key&gt; [focus]" for the help and the error message.</summary>
        public string Usage => string.Join(" ", Arguments.Select(a => a.Required ? $"<{a.Name}>" : $"[{a.Name}]"));

        public JObject ToJson() => new JObject
        {
            ["name"] = Name, ["title"] = Title, ["description"] = Description, ["usage"] = Usage,
        };
    }

    /// <summary>A resource (resources/list) or resource template (resources/templates/list; <see cref="Uri"/> holds the template).</summary>
    public sealed class McpResourceInfo
    {
        public string Uri, Name, Title, Description, MimeType;
        public bool IsTemplate;

        public static McpResourceInfo Parse(JObject r) => new McpResourceInfo
        {
            Uri = (string)r["uri"] ?? (string)r["uriTemplate"] ?? "",
            IsTemplate = r["uriTemplate"] != null,
            Name = (string)r["name"] ?? "",
            Title = (string)r["title"],
            Description = (string)r["description"] ?? "",
            MimeType = (string)r["mimeType"],
        };

        /// <summary>The URI without "scheme://" – what the user types after @server: ("ticket/{key}").</summary>
        public string Path
        {
            get
            {
                int i = Uri.IndexOf("://", StringComparison.Ordinal);
                return i >= 0 ? Uri.Substring(i + 3) : Uri;
            }
        }

        public JObject ToJson() => new JObject
        {
            ["uri"] = Uri, ["name"] = Name, ["title"] = Title, ["description"] = Description, ["template"] = IsTemplate, ["path"] = Path,
        };
    }

    /// <summary>
    /// Client for one MCP server over HTTP (JSON-RPC 2.0), written for this plug-in so it needs nothing but
    /// HttpClient and Newtonsoft.Json in the IDE's process. Transports:
    ///  - Streamable HTTP (MCP 2025-03-26 and later): every message is a POST; the answer is JSON or an SSE stream that
    ///    may carry progress notifications and requests of the server before the response; Mcp-Session-Id header.
    ///  - HTTP+SSE (MCP 2024-11-05), used when the POST of initialize is rejected with 4xx: a GET opens an event stream,
    ///    its "endpoint" event names the URL for the POSTs, all answers arrive on the stream.
    /// The client only offers tools (no sampling, roots or elicitation for the server).
    /// </summary>
    public sealed class McpClient : IDisposable
    {
        public const string ProtocolVersion = "2025-06-18";
        private const string LegacyProtocolVersion = "2024-11-05";

        private readonly McpServerConfig _server;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, Action<JObject>> _progress = new ConcurrentDictionary<string, Action<JObject>>();
        private readonly CancellationTokenSource _disposed = new CancellationTokenSource();
        private volatile bool _initialized;
        private string _sessionId;
        private int _nextId;

        // HTTP+SSE (legacy) transport
        private bool _legacy;
        private string _legacyPostUrl;
        private CancellationTokenSource _legacyStream;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending = new ConcurrentDictionary<string, TaskCompletionSource<JObject>>();

        public McpClient(McpServerConfig server, bool useSystemProxy, string trustedCertificates)
        {
            _server = server;
            _http = new HttpClient(HttpFactory.Handler(useSystemProxy, trustedCertificates, server.WindowsLogin)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            HttpFactory.AllowParallelRequests(server.Url);
        }

        public string Name => _server.Name;
        public JObject ServerInfo { get; private set; }
        public string Instructions { get; private set; }
        public string NegotiatedVersion { get; private set; }
        /// <summary>What the server offers (tools, prompts, resources …), from initialize.</summary>
        public JObject Capabilities { get; private set; } = new JObject();
        public bool HasPrompts => Capabilities["prompts"] != null;
        public bool HasResources => Capabilities["resources"] != null;
        public string Transport => _legacy ? "HTTP+SSE" : "Streamable HTTP";
        public bool IsInitialized => _initialized;

        /// <summary>The server announced that its tools, prompts or resources changed (notifications/…/list_changed).</summary>
        public event Action ListsChanged;

        /// <summary>"Ticket system 1.2" from serverInfo (title or name and version).</summary>
        public string ServerLabel
        {
            get
            {
                var name = (string)ServerInfo?["title"] ?? (string)ServerInfo?["name"] ?? _server.Name;
                var version = (string)ServerInfo?["version"];
                return string.IsNullOrEmpty(version) ? name : name + " " + version;
            }
        }

        // ------------------------------------------------------------------ public API

        public async Task EnsureInitializedAsync(CancellationToken ct)
        {
            if (_initialized) return;
            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_initialized) return;
                await InitializeAsync(ct).ConfigureAwait(false);
            }
            finally { _initLock.Release(); }
        }

        public async Task<List<McpToolInfo>> ListToolsAsync(CancellationToken ct) =>
            (await ListPagedAsync("tools/list", "tools", 1000, ct).ConfigureAwait(false))
                .Select(McpToolInfo.Parse).Where(t => t.Name.Length > 0).ToList();

        /// <summary>Prompt templates of the server (prompts/list); empty if it offers none.</summary>
        public async Task<List<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) =>
            !HasPrompts ? new List<McpPromptInfo>() :
            (await ListPagedAsync("prompts/list", "prompts", 500, ct).ConfigureAwait(false))
                .Select(McpPromptInfo.Parse).Where(p => p.Name.Length > 0).ToList();

        /// <summary>Resources (resources/list) and resource templates (resources/templates/list); empty if it offers none.</summary>
        public async Task<(List<McpResourceInfo> Resources, List<McpResourceInfo> Templates)> ListResourcesAsync(CancellationToken ct)
        {
            if (!HasResources) return (new List<McpResourceInfo>(), new List<McpResourceInfo>());
            var resources = (await ListPagedAsync("resources/list", "resources", 1000, ct).ConfigureAwait(false))
                .Select(McpResourceInfo.Parse).Where(r => r.Uri.Length > 0).ToList();
            List<McpResourceInfo> templates;
            try
            {
                templates = (await ListPagedAsync("resources/templates/list", "resourceTemplates", 200, ct).ConfigureAwait(false))
                    .Select(McpResourceInfo.Parse).Where(r => r.Uri.Length > 0).ToList();
            }
            catch (McpException ex) // optional for servers: some answer "method not found"
            {
                Log.Info($"MCP {_server.Name}: no resource templates ({ex.Message})");
                templates = new List<McpResourceInfo>();
            }
            return (resources, templates);
        }

        /// <summary>A prompt with its arguments filled in (prompts/get; argument values are strings).</summary>
        public Task<JObject> GetPromptAsync(string name, JObject arguments, CancellationToken ct) =>
            RequestAsync("prompts/get", new JObject { ["name"] = name, ["arguments"] = arguments ?? new JObject() }, null, TimeSpan.FromSeconds(Math.Max(10, _server.TimeoutSeconds)), ct);

        /// <summary>The contents of a resource (resources/read).</summary>
        public Task<JObject> ReadResourceAsync(string uri, CancellationToken ct) =>
            RequestAsync("resources/read", new JObject { ["uri"] = uri }, null, TimeSpan.FromSeconds(Math.Max(10, _server.TimeoutSeconds)), ct);

        /// <summary>All items of a paged list method (cursor / nextCursor), at most <paramref name="max"/>.</summary>
        private async Task<List<JObject>> ListPagedAsync(string method, string field, int max, CancellationToken ct)
        {
            var items = new List<JObject>();
            string cursor = null;
            for (int page = 0; page < 50 && items.Count < max; page++)
            {
                var p = cursor == null ? null : new JObject { ["cursor"] = cursor };
                var result = await RequestAsync(method, p, null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                items.AddRange((result[field] as JArray ?? new JArray()).OfType<JObject>());
                cursor = (string)result["nextCursor"];
                if (string.IsNullOrEmpty(cursor)) break;
            }
            if (items.Count > max)
            {
                Log.Info($"MCP {_server.Name}: {method} returned more than {max} items, the rest is ignored");
                items.RemoveRange(max, items.Count - max);
            }
            return items;
        }

        /// <summary>Calls a tool; <paramref name="progress"/> receives the server's progress messages (may be null).</summary>
        public Task<JObject> CallToolAsync(string name, JObject arguments, Action<string> progress, CancellationToken ct)
        {
            var p = new JObject { ["name"] = name, ["arguments"] = arguments ?? new JObject() };
            return RequestAsync("tools/call", p, progress, TimeSpan.FromSeconds(Math.Max(10, _server.TimeoutSeconds)), ct);
        }

        // ------------------------------------------------------------------ requests

        /// <summary>
        /// Sends a request and returns its result. If the server has forgotten the session (HTTP 404 with a session id)
        /// or the event stream of the old transport was closed, the client initializes again and repeats the request once.
        /// </summary>
        private async Task<JObject> RequestAsync(string method, JObject @params, Action<string> progress, TimeSpan timeout, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                await EnsureInitializedAsync(ct).ConfigureAwait(false);
                try
                {
                    return await SendRequestAsync(method, @params, progress, timeout, ct).ConfigureAwait(false);
                }
                catch (SessionLostException ex) when (attempt == 0)
                {
                    Log.Info($"MCP {_server.Name}: {ex.Message}, initializing again");
                    ResetSession();
                }
                catch (SessionLostException ex)
                {
                    ResetSession();
                    throw new McpException(L.T("MCP server {0}: {1}", _server.Name, ex.Message));
                }
            }
        }

        private sealed class SessionLostException : Exception
        {
            public SessionLostException(string message) : base(message) { }
        }

        private void ResetSession()
        {
            _initialized = false;
            _sessionId = null;
            try { _legacyStream?.Cancel(); } catch { }
            _legacyStream = null;
        }

        private async Task InitializeAsync(CancellationToken ct)
        {
            _sessionId = null;
            var p = new JObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "PL/Pilot", ["title"] = "PL/Pilot (PL/SQL Developer)", ["version"] = typeof(McpClient).Assembly.GetName().Version.ToString(3) },
            };
            JObject result;
            var timeout = TimeSpan.FromSeconds(30);
            if (!_legacy)
            {
                try
                {
                    result = await SendRequestAsync("initialize", p, null, timeout, ct, initializing: true).ConfigureAwait(false);
                }
                catch (FallbackToLegacyException ex)
                {
                    Log.Info($"MCP {_server.Name}: POST rejected ({ex.Message}), trying the HTTP+SSE transport");
                    _legacy = true;
                    try
                    {
                        await OpenLegacyStreamAsync(ct).ConfigureAwait(false);
                    }
                    catch (McpException)
                    {
                        _legacy = false;
                        throw new McpException(L.T("MCP server {0} rejected the connection ({1}). Check the URL: it is usually the server address followed by /mcp (or /sse for older servers).", _server.Name, ex.Message));
                    }
                    p["protocolVersion"] = LegacyProtocolVersion;
                    result = await SendRequestAsync("initialize", p, null, timeout, ct, initializing: true).ConfigureAwait(false);
                }
            }
            else
            {
                await OpenLegacyStreamAsync(ct).ConfigureAwait(false);
                p["protocolVersion"] = LegacyProtocolVersion;
                result = await SendRequestAsync("initialize", p, null, timeout, ct, initializing: true).ConfigureAwait(false);
            }

            NegotiatedVersion = (string)result["protocolVersion"] ?? ProtocolVersion;
            ServerInfo = result["serverInfo"] as JObject ?? new JObject();
            Instructions = (string)result["instructions"];
            Capabilities = result["capabilities"] as JObject ?? new JObject();
            await NotifyAsync("notifications/initialized", null, ct).ConfigureAwait(false);
            _initialized = true;
            Log.Info($"MCP {_server.Name}: connected to {ServerLabel} ({Transport}, protocol {NegotiatedVersion}{(_sessionId != null ? ", session" : "")})");
        }

        private sealed class FallbackToLegacyException : Exception
        {
            public FallbackToLegacyException(string message) : base(message) { }
        }

        private async Task<JObject> SendRequestAsync(string method, JObject @params, Action<string> progress, TimeSpan timeout, CancellationToken ct, bool initializing = false)
        {
            var id = Interlocked.Increment(ref _nextId);
            var message = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            string progressToken = null;
            if (progress != null)
            {
                progressToken = "pl-" + id;
                if (@params == null) @params = new JObject();
                @params["_meta"] = new JObject { ["progressToken"] = progressToken };
                _progress[progressToken] = n => ReportProgress(n, progress);
            }
            if (@params != null) message["params"] = @params;

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token))
            {
                timeoutCts.CancelAfter(timeout);
                var token = timeoutCts.Token;
                try
                {
                    return _legacy
                        ? await LegacyRequestAsync(id.ToString(), message, token).ConfigureAwait(false)
                        : await StreamableRequestAsync(id.ToString(), message, initializing, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_disposed.IsCancellationRequested)
                {
                    Cancelled(id, "timeout");
                    throw new McpException(L.T("MCP server {0} did not answer {1} within {2} s.", _server.Name, method, (int)timeout.TotalSeconds));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Cancelled(id, "cancelled by the user");
                    throw;
                }
                catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
                {
                    // the connection was closed (settings changed): a tool error, not a Stop of the whole run
                    throw new McpException(L.T("MCP server {0} is not connected any more (settings changed or connection lost).", _server.Name));
                }
                finally
                {
                    if (progressToken != null) _progress.TryRemove(progressToken, out _);
                }
            }
        }

        private static void ReportProgress(JObject p, Action<string> progress)
        {
            var text = (string)p["message"];
            if (string.IsNullOrWhiteSpace(text))
            {
                var done = (double?)p["progress"];
                var total = (double?)p["total"];
                if (done == null) return;
                text = total > 0 ? $"{Math.Round(100 * done.Value / total.Value)} %" : done.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            try { progress(text.Trim()); } catch { }
        }

        /// <summary>Tells the server that a request is no longer needed (best effort, not awaited).</summary>
        private void Cancelled(int id, string reason)
        {
            if (!_initialized) return;
            var p = new JObject { ["requestId"] = id, ["reason"] = reason };
            Task.Run(async () =>
            {
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        await NotifyAsync("notifications/cancelled", p, cts.Token).ConfigureAwait(false);
                }
                catch { }
            });
        }

        private async Task NotifyAsync(string method, JObject @params, CancellationToken ct)
        {
            var message = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (@params != null) message["params"] = @params;
            await PostOnlyAsync(message, ct).ConfigureAwait(false);
        }

        /// <summary>Sends a notification or a response to the server; its answer has no content (202).</summary>
        private async Task PostOnlyAsync(JObject message, CancellationToken ct)
        {
            using (var request = NewPost(_legacy ? _legacyPostUrl : _server.Url, message))
            using (var response = await SendHttpAsync(request, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404)
                    Log.Warn($"MCP {_server.Name}: {(string)message["method"] ?? "response"} answered with HTTP {(int)response.StatusCode}");
            }
        }

        private HttpRequestMessage NewPost(string url, JObject message)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(message.ToString(Formatting.None), new UTF8Encoding(false), "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.ExpectContinue = false;
            if (_sessionId != null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            if (_initialized && NegotiatedVersion != null && !_legacy) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", NegotiatedVersion);
            AddConfiguredHeaders(request);
            return request;
        }

        private void AddConfiguredHeaders(HttpRequestMessage request)
        {
            foreach (var h in _server.ParsedHeaders())
            {
                if (!request.Headers.TryAddWithoutValidation(h.Key, h.Value))
                    Log.Warn($"MCP {_server.Name}: header {h.Key} cannot be set");
            }
        }

        private async Task<HttpResponseMessage> SendHttpAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new McpException(HttpFactory.DescribeConnectionError(request.RequestUri.ToString(), ex));
            }
        }

        private async Task<string> HttpError(HttpResponseMessage response)
        {
            string body = "";
            try { body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim(); } catch { }
            if (body.Length > 300) body = body.Substring(0, 300) + " …";
            int code = (int)response.StatusCode;
            if (code == 401 || code == 403)
                return L.T("MCP server {0} refused access (HTTP {1}). Check the headers (e.g. Authorization) in the settings.", _server.Name, code) + (body.Length > 0 ? " " + body : "");
            return L.T("MCP server {0} answered with HTTP {1} {2}", _server.Name, code, response.ReasonPhrase) + (body.Length > 0 ? ": " + body : "");
        }

        // ------------------------------------------------------------------ Streamable HTTP

        private async Task<JObject> StreamableRequestAsync(string id, JObject message, bool initializing, CancellationToken ct)
        {
            using (var request = NewPost(_server.Url, message))
            using (var response = await SendHttpAsync(request, ct).ConfigureAwait(false))
            {
                int code = (int)response.StatusCode;
                if (initializing && code >= 400 && code < 500 && code != 401 && code != 403)
                    throw new FallbackToLegacyException("HTTP " + code);
                if (code == 404 && _sessionId != null)
                    throw new SessionLostException("the server no longer knows the session (HTTP 404)");
                if (!response.IsSuccessStatusCode) throw new McpException(await HttpError(response).ConfigureAwait(false));
                if (initializing && response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _sessionId = ids.FirstOrDefault();

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    JObject answer = null;
                    try
                    {
                        await ReadEventsAsync(response, ct, (evt, data) =>
                        {
                            var msg = ParseMessage(data);
                            if (msg == null) return false;
                            if (IsResponseTo(msg, id)) { answer = msg; return true; }
                            HandleServerMessage(msg);
                            return false;
                        }).ConfigureAwait(false);
                    }
                    catch (IOException ex) when (!ct.IsCancellationRequested)
                    {
                        // e.g. .NET's read timeout of 5 minutes without any event of the server
                        throw new McpException(L.T("The connection to MCP server {0} was interrupted while waiting for the answer: {1}", _server.Name, ex.GetBaseException().Message));
                    }
                    if (answer == null) throw new McpException(L.T("MCP server {0} closed the connection without an answer.", _server.Name));
                    return ResultOf(answer);
                }

                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                JToken json;
                try { json = JToken.Parse(text); }
                catch (JsonException) { throw new McpException(L.T("MCP server {0} sent an invalid answer: {1}", _server.Name, Shorten(text, 200))); }
                // a batch: our response is one of its messages
                foreach (var msg in (json is JArray arr ? arr.OfType<JObject>() : new[] { json as JObject }).Where(m => m != null))
                {
                    if (IsResponseTo(msg, id)) return ResultOf(msg);
                    HandleServerMessage(msg);
                }
                throw new McpException(L.T("MCP server {0} sent an invalid answer: {1}", _server.Name, Shorten(text, 200)));
            }
        }

        /// <summary>
        /// Reads server-sent events until <paramref name="onEvent"/> returns true or the stream ends. The stream is closed
        /// when <paramref name="ct"/> is cancelled (reading does not observe the token by itself).
        /// </summary>
        private static async Task ReadEventsAsync(HttpResponseMessage response, CancellationToken ct, Func<string, string, bool> onEvent)
        {
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (ct.Register(() => { try { stream.Dispose(); } catch { } }))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string evt = null;
                var data = new StringBuilder();
                while (true)
                {
                    string line;
                    try { line = await reader.ReadLineAsync().ConfigureAwait(false); }
                    catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                    ct.ThrowIfCancellationRequested();
                    if (line == null) return;
                    if (line.Length == 0)
                    {
                        if (data.Length > 0 && onEvent(evt ?? "message", data.ToString())) return;
                        evt = null;
                        data.Clear();
                        continue;
                    }
                    if (line.StartsWith(":")) continue; // comment / keep-alive
                    int colon = line.IndexOf(':');
                    var field = colon < 0 ? line : line.Substring(0, colon);
                    var value = colon < 0 ? "" : line.Substring(colon + 1);
                    if (value.StartsWith(" ")) value = value.Substring(1);
                    if (field == "event") evt = value;
                    else if (field == "data") { if (data.Length > 0) data.Append('\n'); data.Append(value); }
                }
            }
        }

        // ------------------------------------------------------------------ HTTP+SSE (MCP 2024-11-05)

        private async Task OpenLegacyStreamAsync(CancellationToken ct)
        {
            try { _legacyStream?.Cancel(); } catch { }
            var streamCts = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
            _legacyStream = streamCts;
            var request = new HttpRequestMessage(HttpMethod.Get, _server.Url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            AddConfiguredHeaders(request);
            HttpResponseMessage response;
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct, streamCts.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(30));
                try { response = await SendHttpAsync(request, connect.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new McpException(L.T("MCP server {0} did not open its event stream.", _server.Name)); }
            }
            if (!response.IsSuccessStatusCode)
            {
                var error = await HttpError(response).ConfigureAwait(false);
                response.Dispose();
                throw new McpException(error);
            }

            var endpoint = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var baseUri = new Uri(_server.Url);
            _ = Task.Run(async () =>
            {
                try
                {
                    using (response)
                    {
                        await ReadEventsAsync(response, streamCts.Token, (evt, data) =>
                        {
                            if (evt == "endpoint")
                            {
                                endpoint.TrySetResult(new Uri(baseUri, data.Trim()).ToString());
                                return false;
                            }
                            var msg = ParseMessage(data);
                            if (msg == null) return false;
                            var msgId = msg["id"]?.ToString();
                            if (msgId != null && msg["method"] == null && _pending.TryRemove(msgId, out var waiter)) waiter.TrySetResult(msg);
                            else HandleServerMessage(msg);
                            return false;
                        }).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { Log.Info($"MCP {_server.Name}: event stream ended: {ex.GetBaseException().Message}"); }
                catch (OperationCanceledException) { }
                finally
                {
                    endpoint.TrySetResult(null);
                    // answers that can no longer arrive: the waiting requests initialize again
                    if (ReferenceEquals(_legacyStream, streamCts) || _legacyStream == null)
                    {
                        _initialized = false;
                        foreach (var key in _pending.Keys.ToList())
                            if (_pending.TryRemove(key, out var w)) w.TrySetException(new SessionLostException("the event stream of the server was closed"));
                    }
                }
            });

            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(TimeSpan.FromSeconds(30));
                var done = await Task.WhenAny(endpoint.Task, Task.Delay(System.Threading.Timeout.Infinite, wait.Token)).ConfigureAwait(false);
                if (done != endpoint.Task || endpoint.Task.Result == null)
                {
                    try { streamCts.Cancel(); } catch { }
                    ct.ThrowIfCancellationRequested();
                    throw new McpException(L.T("MCP server {0} did not send the endpoint event of the HTTP+SSE transport.", _server.Name));
                }
                _legacyPostUrl = endpoint.Task.Result;
            }
        }

        private async Task<JObject> LegacyRequestAsync(string id, JObject message, CancellationToken ct)
        {
            var waiter = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = waiter;
            try
            {
                using (var request = NewPost(_legacyPostUrl, message))
                using (var response = await SendHttpAsync(request, ct).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 404) throw new SessionLostException("the server no longer knows the session (HTTP 404)");
                    if (!response.IsSuccessStatusCode) throw new McpException(await HttpError(response).ConfigureAwait(false));
                }
                using (ct.Register(() => waiter.TrySetCanceled()))
                    return ResultOf(await waiter.Task.ConfigureAwait(false));
            }
            finally { _pending.TryRemove(id, out _); }
        }

        // ------------------------------------------------------------------ messages of the server

        private static JObject ParseMessage(string data)
        {
            try { return JToken.Parse(data) as JObject; }
            catch (JsonException) { return null; }
        }

        private static bool IsResponseTo(JObject msg, string id) =>
            msg["method"] == null && (msg["result"] != null || msg["error"] != null) && msg["id"]?.ToString() == id;

        private JObject ResultOf(JObject response)
        {
            if (response["error"] is JObject error)
            {
                var text = (string)error["message"] ?? error.ToString(Formatting.None);
                throw new McpException(L.T("MCP server {0} reported an error: {1}", _server.Name, text) + (error["code"] != null ? $" ({error["code"]})" : ""));
            }
            return response["result"] as JObject ?? new JObject();
        }

        /// <summary>Notifications and requests the server sends while it works on a request.</summary>
        private void HandleServerMessage(JObject msg)
        {
            var method = (string)msg["method"];
            if (method == null) return; // a response nobody waits for any more
            var p = msg["params"] as JObject ?? new JObject();
            if (msg["id"] != null)
            {
                // a request of the server: only ping is supported (no sampling, roots or elicitation)
                var reply = new JObject { ["jsonrpc"] = "2.0", ["id"] = msg["id"] };
                if (method == "ping") reply["result"] = new JObject();
                else reply["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not supported by this client: " + method };
                Task.Run(async () =>
                {
                    try { using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await PostOnlyAsync(reply, cts.Token).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Warn($"MCP {_server.Name}: could not answer {method}: {ex.GetBaseException().Message}"); }
                });
                return;
            }
            switch (method)
            {
                case "notifications/progress":
                    var token = p["progressToken"]?.ToString();
                    if (token != null && _progress.TryGetValue(token, out var report)) report(p);
                    break;
                case "notifications/tools/list_changed":
                case "notifications/prompts/list_changed":
                case "notifications/resources/list_changed":
                    Log.Info($"MCP {_server.Name}: {method}");
                    try { ListsChanged?.Invoke(); } catch { }
                    break;
                case "notifications/message":
                    Log.Info($"MCP {_server.Name} [{(string)p["level"]}]: {Shorten(p["data"]?.ToString(Formatting.None) ?? "", 300)}");
                    break;
            }
        }

        private static string Shorten(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + " …";

        public void Dispose()
        {
            var session = _sessionId;
            bool wasLegacy = _legacy;
            try { _disposed.Cancel(); } catch { }
            try { _legacyStream?.Cancel(); } catch { }
            // ends the session on the server (best effort)
            if (session != null && !wasLegacy)
            {
                var http = _http;
                Task.Run(async () =>
                {
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Delete, _server.Url))
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        {
                            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
                            AddConfiguredHeaders(request);
                            (await http.SendAsync(request, cts.Token).ConfigureAwait(false)).Dispose();
                        }
                    }
                    catch { }
                    finally { http.Dispose(); }
                });
            }
            else _http.Dispose();
        }
    }
}
