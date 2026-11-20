using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlsqlDevAgent.Agent.Mcp
{
    /// <summary>
    /// One MCP server the agent may use (Settings → External tools). Only the HTTP transports are supported: the server
    /// runs elsewhere (e.g. on a team server), the plug-in starts no processes.
    /// </summary>
    public sealed class McpServerConfig
    {
        /// <summary>Short name: prefix of the tool names the model sees ("tickets" → tickets__get_ticket).</summary>
        public string Name { get; set; } = "";
        /// <summary>
        /// In the user's settings: this entry holds the user's own settings for a server of the team configuration
        /// (enabled, extra headers, tool modes); its URL and defaults come from the team file. In the effective list:
        /// the server comes from the team configuration.
        /// </summary>
        public bool Team { get; set; }
        /// <summary>MCP endpoint (Streamable HTTP, e.g. http://server:8000/mcp; an old HTTP+SSE endpoint …/sse works too).</summary>
        public string Url { get; set; } = "";
        public bool Enabled { get; set; } = true;
        /// <summary>Extra HTTP headers, one "Name: value" per line (e.g. Authorization: Bearer …). Stored encrypted (DPAPI).</summary>
        [JsonIgnore] public string Headers { get; set; } = "";
        /// <summary>Send the Windows login (NTLM/Kerberos) to this server. Off by default: only for servers that require it.</summary>
        public bool WindowsLogin { get; set; } = false;
        /// <summary>How long one tool call may take; servers that use an LLM themselves can be slow.</summary>
        public int TimeoutSeconds { get; set; } = 300;
        /// <summary>
        /// Mode per tool name: "read" (runs without asking, also in plan mode and on read-only connections), "write"
        /// (asks first; not available in plan mode and on read-only connections) or "off" (not offered to the model).
        /// Tools not listed follow the server's hints: readOnlyHint → read, otherwise write.
        /// </summary>
        public Dictionary<string, string> Tools { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        [JsonProperty("HeadersProtected")]
        private string HeadersProtected
        {
            get => AgentConfig.Protect(Headers);
            set => Headers = AgentConfig.Unprotect(value);
        }

        public const string Read = "read", Write = "write", Off = "off";

        /// <summary>Allowed server names: they become part of the tool names (letters, digits, _ and -).</summary>
        public static readonly Regex NamePattern = new Regex("^[A-Za-z][A-Za-z0-9_-]{0,23}$");

        /// <summary>Lower case name used as tool name prefix.</summary>
        [JsonIgnore] public string Prefix => (Name ?? "").Trim().ToLowerInvariant();

        /// <summary>The configured mode of a tool, or null (then the server's hints decide).</summary>
        public string ModeOf(string tool) => Tools != null && tool != null && Tools.TryGetValue(tool, out var m) && (m == Read || m == Write || m == Off) ? m : null;

        /// <summary>The headers as (name, value) pairs with ${env:NAME} replaced; lines without a colon are ignored.</summary>
        public List<KeyValuePair<string, string>> ParsedHeaders() => ParseHeaderLines(Headers)
            .Select(h => new KeyValuePair<string, string>(h.Key, ExpandEnv(h.Value))).ToList();

        private static List<KeyValuePair<string, string>> ParseHeaderLines(string headers) =>
            (headers ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#") && l.IndexOf(':') > 0)
                .Select(l => new KeyValuePair<string, string>(l.Substring(0, l.IndexOf(':')).Trim(), l.Substring(l.IndexOf(':') + 1).Trim()))
                .ToList();

        /// <summary>The header names (for the settings page: values of team headers may be secrets).</summary>
        public List<string> HeaderNames() => ParseHeaderLines(Headers).Select(h => h.Key).ToList();

        private static readonly Regex EnvPattern = new Regex(@"\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}");

        /// <summary>Replaces ${env:NAME} with the environment variable (user or process; empty if not set).</summary>
        public static string ExpandEnv(string value) =>
            EnvPattern.Replace(value ?? "", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");

        /// <summary>The ${env:NAME} variables used in the URL or the headers that are not set.</summary>
        public List<string> MissingEnvironmentVariables() =>
            EnvPattern.Matches((Url ?? "") + "\n" + (Headers ?? "")).Cast<Match>().Select(m => m.Groups[1].Value)
                .Where(n => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n))).Distinct().ToList();

        /// <summary>
        /// Headers of the team file with the user's own lines: a user line replaces the team line of the same header
        /// name (e.g. the user's own Authorization token).
        /// </summary>
        public static string MergeHeaders(string team, string user)
        {
            var own = ParseHeaderLines(user);
            var names = new HashSet<string>(own.Select(h => h.Key), StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", ParseHeaderLines(team).Where(h => !names.Contains(h.Key)).Concat(own).Select(h => h.Key + ": " + h.Value));
        }

        public McpServerConfig Copy()
        {
            var c = (McpServerConfig)MemberwiseClone();
            c.Tools = new Dictionary<string, string>(Tools ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            return c;
        }

        /// <summary>
        /// A server entry of a team file (mcp.json). The common format of MCP clients is accepted:
        /// { "url": …, "type": "http" | "streamable-http" | "sse", "headers": { … }, "timeout": 300, "windowsLogin": false,
        ///   "tools": { "tool": "read" | "write" | "off" }, "disabled": false }.
        /// Returns null with <paramref name="skipped"/> set for entries the plug-in cannot use (stdio servers, no URL).
        /// </summary>
        public static McpServerConfig FromTeamJson(string name, JObject o, out string skipped)
        {
            skipped = null;
            var type = ((string)o["type"] ?? "").ToLowerInvariant();
            if (o["command"] != null || type == "stdio")
            {
                skipped = Util.L.T("{0}: a program started by the client (stdio) – the plug-in only connects to HTTP servers", name);
                return null;
            }
            var url = (string)o["url"] ?? (string)o["serverUrl"];
            if (string.IsNullOrWhiteSpace(url))
            {
                skipped = Util.L.T("{0}: no url", name);
                return null;
            }
            var cfg = new McpServerConfig
            {
                Name = name, Url = url.Trim(), Team = true,
                Enabled = !((bool?)o["disabled"] ?? false) && ((bool?)o["enabled"] ?? true),
                WindowsLogin = (bool?)o["windowsLogin"] ?? false,
                TimeoutSeconds = Math.Max(10, Math.Min(3600, (int?)o["timeout"] ?? (int?)o["timeoutSeconds"] ?? 300)),
            };
            if (o["headers"] is JObject headers)
                cfg.Headers = string.Join("\n", headers.Properties().Select(p => p.Name + ": " + (string)p.Value));
            if (o["tools"] is JObject tools)
                foreach (var p in tools.Properties())
                {
                    var mode = ((string)p.Value ?? "").ToLowerInvariant();
                    if (mode == Read || mode == Write || mode == Off) cfg.Tools[p.Name] = mode;
                }
            return cfg;
        }

        /// <summary>Identifies the connection: a change of one of these values needs a new connection.</summary>
        [JsonIgnore] public string ConnectionKey => string.Join("\n", Url?.Trim(), Headers, WindowsLogin, TimeoutSeconds);

        /// <summary>For the settings page: everything including the plain headers.</summary>
        public JObject ToUiJson()
        {
            var o = JObject.FromObject(this);
            o.Remove("HeadersProtected");
            o["Headers"] = Headers ?? "";
            return o;
        }

        public static McpServerConfig FromUiJson(JObject o)
        {
            var headers = (string)o["Headers"] ?? "";
            var copy = (JObject)o.DeepClone();
            copy.Remove("Headers");
            copy.Remove("HeadersProtected");
            var cfg = copy.ToObject<McpServerConfig>() ?? new McpServerConfig();
            cfg.Headers = headers;
            cfg.Name = (cfg.Name ?? "").Trim();
            cfg.Url = (cfg.Url ?? "").Trim();
            if (cfg.Tools == null) cfg.Tools = new Dictionary<string, string>(StringComparer.Ordinal);
            return cfg;
        }

        /// <summary>Why this server cannot be saved (null = valid). Team entries of the user have no URL of their own.</summary>
        public string Validate()
        {
            if (!NamePattern.IsMatch(Name ?? ""))
                return Util.L.T("MCP server name \"{0}\": use 1–24 letters, digits, _ or -, starting with a letter.", Name);
            if (!Team && (!Uri.TryCreate(ExpandEnv(Url), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")))
                return Util.L.T("MCP server {0}: the URL must be an http(s) URL, e.g. http://server:8000/mcp", Name);
            foreach (var line in (Headers ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var l = line.Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                if (l.IndexOf(':') <= 0) return Util.L.T("MCP server {0}: every header line must look like \"Name: value\".", Name);
            }
            return null;
        }
    }
}
