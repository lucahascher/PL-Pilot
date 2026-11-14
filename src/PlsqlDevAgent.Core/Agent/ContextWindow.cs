using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PlsqlDevAgent.Agent
{
    /// <summary>The context window the LLM server reports for a model.</summary>
    public sealed class ContextWindowInfo
    {
        /// <summary>The size the server really uses for the model (null = not known).</summary>
        public int? Tokens;
        /// <summary>The most the model supports; the server may use less.</summary>
        public int? Maximum;
        /// <summary>Where the numbers come from, e.g. "Ollama, model loaded".</summary>
        public string Source;
        /// <summary>Extra hint for the user (e.g. why only the maximum is known).</summary>
        public string Note;

        public bool Known => Tokens != null || Maximum != null;

        /// <summary>
        /// The value for the setting "Context Window": the size the server uses; if only the maximum is known, the
        /// current value as long as it does not exceed it (a too large setting lets the server cut off the start of
        /// the conversation, the system prompt included). Null = keep the current value.
        /// </summary>
        public int? SettingFor(int current)
        {
            if (Tokens is int t) return t == current ? (int?)null : t;
            if (Maximum is int m && current > m) return m;
            return null;
        }

        /// <summary>For the settings page, texts in the UI language.</summary>
        public JObject ToJson() => new JObject
        {
            ["tokens"] = Tokens, ["maximum"] = Maximum,
            ["source"] = Source == null ? null : Util.L.T(Source), ["note"] = Note == null ? null : Util.L.T(Note),
        };
    }

    /// <summary>
    /// Asks the LLM server how large the model's context window is. There is no standard for it in the
    /// OpenAI-compatible API, so this tries what the common servers offer: extra fields in the model list (vLLM,
    /// OpenRouter, LM Studio, llama.cpp), Ollama's /api/ps (size of the loaded model) and /api/show, llama.cpp's
    /// /props and LM Studio's /api/v0/models.
    /// </summary>
    public static class ContextWindowDetector
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

        public static async Task<ContextWindowInfo> DetectAsync(OpenAiClient client, AgentConfig cfg, string model, CancellationToken ct)
        {
            model = (model ?? cfg.Model ?? "").Trim();
            var info = new ContextWindowInfo();
            if (model.Length == 0) return info;

            // 1. extra fields in the OpenAI-compatible model list
            var list = await client.TryJsonAsync(cfg, HttpMethod.Get, cfg.ModelsUrl, null, Timeout, ct).ConfigureAwait(false);
            var entry = ((list?["data"] ?? list?["models"]) as JArray)?.OfType<JObject>()
                .FirstOrDefault(m => SameModel((string)m["id"] ?? (string)m["name"] ?? (string)m["model"], model));
            if (entry != null)
            {
                if (Int(entry["max_model_len"]) is int vllm) return Exact(vllm, "vLLM (max_model_len)");
                if (Int(entry["loaded_context_length"]) is int lmLoaded) return Exact(lmLoaded, "LM Studio (model loaded)");
                if ((Int(entry["context_length"]) ?? Int(entry["context_window"]) ?? Int(entry["max_input_tokens"])) is int listed)
                    return Exact(listed, "model list of the server");
                info.Maximum = Int(entry["max_context_length"]) ?? Int(entry["meta"]?["n_ctx_train"]);
                if (info.Maximum != null) info.Source = "model list of the server (maximum)";
            }

            var root = RootUrl(cfg);
            // 2. Ollama
            var version = await client.TryJsonAsync(cfg, HttpMethod.Get, root + "/api/version", null, Timeout, ct).ConfigureAwait(false);
            if (version?["version"] != null)
            {
                var ps = await client.TryJsonAsync(cfg, HttpMethod.Get, root + "/api/ps", null, Timeout, ct).ConfigureAwait(false);
                var loaded = (ps?["models"] as JArray)?.OfType<JObject>().FirstOrDefault(m => SameModel((string)m["name"] ?? (string)m["model"], model));
                var show = await client.TryJsonAsync(cfg, HttpMethod.Post, root + "/api/show", new JObject { ["model"] = model }, Timeout, ct).ConfigureAwait(false);
                var maximum = (show?["model_info"] as JObject)?.Properties()
                    .Where(p => p.Name.EndsWith(".context_length", StringComparison.Ordinal)).Select(p => Int(p.Value)).FirstOrDefault(v => v != null);
                if (Int(loaded?["context_length"]) is int used)
                    return new ContextWindowInfo { Tokens = used, Maximum = maximum, Source = "Ollama, model loaded" };
                var numCtx = Regex.Match((string)show?["parameters"] ?? "", @"(?m)^\s*num_ctx\s+(\d+)");
                if (numCtx.Success)
                    return new ContextWindowInfo { Tokens = int.Parse(numCtx.Groups[1].Value), Maximum = maximum, Source = "Ollama, num_ctx of the model" };
                return new ContextWindowInfo
                {
                    Maximum = maximum ?? info.Maximum, Source = "Ollama, model not loaded",
                    Note = "Ollama sets the size when it loads the model (OLLAMA_CONTEXT_LENGTH or its default); PL/Pilot checks it again after the model's first answer.",
                };
            }

            // 3. llama.cpp server
            var props = await client.TryJsonAsync(cfg, HttpMethod.Get, root + "/props", null, Timeout, ct).ConfigureAwait(false);
            if ((Int(props?["default_generation_settings"]?["n_ctx"]) ?? Int(props?["n_ctx"])) is int nctx)
                return new ContextWindowInfo { Tokens = nctx, Maximum = info.Maximum, Source = "llama.cpp server (n_ctx)" };

            // 4. LM Studio's own API
            var lms = await client.TryJsonAsync(cfg, HttpMethod.Get, root + "/api/v0/models/" + Uri.EscapeDataString(model), null, Timeout, ct).ConfigureAwait(false);
            if (lms is JObject lm)
            {
                if (Int(lm["loaded_context_length"]) is int lmUsed) return new ContextWindowInfo { Tokens = lmUsed, Maximum = Int(lm["max_context_length"]), Source = "LM Studio, model loaded" };
                if (Int(lm["max_context_length"]) is int lmMax)
                    return new ContextWindowInfo
                    {
                        Maximum = lmMax, Source = "LM Studio",
                        Note = "LM Studio sets the size when it loads the model; PL/Pilot checks it again after the model's first answer.",
                    };
            }
            return info;
        }

        private static ContextWindowInfo Exact(int tokens, string source) => new ContextWindowInfo { Tokens = tokens, Source = source };

        /// <summary>The base URL without /v1 and /chat/completions (where Ollama's and llama.cpp's own endpoints are).</summary>
        internal static string RootUrl(AgentConfig cfg)
        {
            var url = (cfg.BaseUrl ?? "").Trim().TrimEnd('/');
            if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - "/chat/completions".Length).TrimEnd('/');
            if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - 3);
            return url;
        }

        /// <summary>"gemma4" and "gemma4:latest" are the same Ollama model.</summary>
        private static bool SameModel(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string Norm(string s) => s.Contains(":") ? s : s + ":latest";
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }

        private static int? Int(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) { var v = (double)t; return v >= 1024 && v <= int.MaxValue ? (int)v : (int?)null; }
            return int.TryParse(t.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 1024 ? i : (int?)null;
        }
    }
}
