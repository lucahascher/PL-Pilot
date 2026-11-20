using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Agent.Mcp
{
    /// <summary>
    /// MCP servers configured for the whole team, in the format other MCP clients use:
    /// { "mcpServers": { "name": { "url": …, "headers": { … }, "tools": { … } } } } ("servers" is accepted too).
    /// Read from mcp.json next to the plug-in (shipped with it) and from a file or folder configured in the settings
    /// (e.g. a network share; %VARIABLES% allowed); the configured file wins for servers of the same name.
    /// Secrets do not belong into the file: header values can use ${env:NAME}, or each user adds the header in the
    /// settings. Users may change everything per server: enabled, extra headers and the mode of every tool.
    /// </summary>
    public static class McpTeamConfig
    {
        public const string FileName = "mcp.json";

        public static string PluginFile =>
            Path.Combine(Path.GetDirectoryName(typeof(McpTeamConfig).Assembly.Location) ?? "", FileName);

        /// <summary>The configured file: a folder means mcp.json in it ("" if nothing is configured).</summary>
        public static string ConfiguredFile(string configured)
        {
            var p = Environment.ExpandEnvironmentVariables((configured ?? "").Trim().Trim('"'));
            if (p.Length == 0) return "";
            if (Directory.Exists(p) || p.EndsWith("\\") || p.EndsWith("/")) return Path.Combine(p, FileName);
            return p;
        }

        public sealed class LoadResult
        {
            public List<McpServerConfig> Servers = new List<McpServerConfig>();
            /// <summary>One line per file for the settings page ("…\mcp.json: 2 servers (a, b)", "not found" …).</summary>
            public List<string> Lines = new List<string>();
            /// <summary>Problems worth a notice in the chat (file not readable, invalid JSON).</summary>
            public List<string> Problems = new List<string>();
        }

        public static LoadResult Load(AgentConfig cfg)
        {
            var result = new LoadResult();
            var byName = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
            LoadFile(PluginFile, false, byName, result);
            var configured = ConfiguredFile(cfg.McpConfigPath);
            bool samePath;
            try { samePath = string.Equals(Path.GetFullPath(configured), Path.GetFullPath(PluginFile), StringComparison.OrdinalIgnoreCase); }
            catch { samePath = false; } // an invalid path: LoadFile reports it
            if (configured.Length > 0 && !samePath) LoadFile(configured, true, byName, result);
            result.Servers = byName.Values.ToList();
            return result;
        }

        private static void LoadFile(string path, bool configured, Dictionary<string, McpServerConfig> byName, LoadResult result)
        {
            try
            {
                if (!File.Exists(path))
                {
                    if (configured)
                    {
                        result.Lines.Add(L.T("{0}: not found", path));
                        result.Problems.Add(L.T("The team MCP configuration {0} was not found.", path));
                    }
                    else result.Lines.Add(L.T("{0}: none (optional)", path));
                    return;
                }
                var root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                var servers = root["mcpServers"] as JObject ?? root["servers"] as JObject;
                if (servers == null)
                {
                    result.Lines.Add(L.T("{0}: no \"mcpServers\" object", path));
                    return;
                }
                var names = new List<string>();
                var skipped = new List<string>();
                foreach (var p in servers.Properties())
                {
                    if (!(p.Value is JObject o)) continue;
                    var server = McpServerConfig.FromTeamJson(p.Name, o, out var why);
                    if (server == null) { skipped.Add(why); continue; }
                    var invalid = server.Validate();
                    if (invalid != null) { skipped.Add(invalid); continue; }
                    var missing = server.MissingEnvironmentVariables();
                    if (missing.Count > 0) skipped.Add(L.T("{0}: environment variable {1} is not set", p.Name, string.Join(", ", missing)));
                    byName[server.Name] = server;
                    names.Add(server.Name);
                }
                var line = L.T("{0}: {1} server(s)", path, names.Count) + (names.Count > 0 ? " (" + string.Join(", ", names) + ")" : "");
                foreach (var s in skipped) line += "\n  " + L.T("skipped or incomplete: {0}", s);
                result.Lines.Add(line);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is NotSupportedException)
            {
                var msg = L.T("{0}: cannot be read: {1}", path, ex.Message);
                result.Lines.Add(msg);
                result.Problems.Add(L.T("The team MCP configuration could not be read: {0}", msg));
                Log.Warn("Team MCP configuration: " + msg);
            }
        }

        /// <summary>
        /// The servers to connect: team servers with the user's settings for them (enabled, extra headers, tool modes;
        /// the user's choice wins), then the user's own servers. Own servers with the name of a team server are ignored.
        /// URLs get their ${env:NAME} variables replaced.
        /// </summary>
        public static List<McpServerConfig> Effective(AgentConfig cfg, LoadResult team)
        {
            var user = cfg.McpServers ?? new List<McpServerConfig>();
            var overrides = user.Where(s => s.Team).GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var result = new List<McpServerConfig>();
            foreach (var t in team.Servers)
            {
                var eff = t.Copy();
                if (overrides.TryGetValue(t.Name, out var o))
                {
                    eff.Enabled = o.Enabled;
                    eff.Headers = McpServerConfig.MergeHeaders(t.Headers, o.Headers);
                    foreach (var kv in o.Tools ?? new Dictionary<string, string>()) eff.Tools[kv.Key] = kv.Value;
                }
                eff.Url = McpServerConfig.ExpandEnv(eff.Url);
                result.Add(eff);
            }
            var teamNames = new HashSet<string>(team.Servers.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var s in user.Where(s => !s.Team && !teamNames.Contains(s.Name)))
            {
                var eff = s.Copy();
                eff.Url = McpServerConfig.ExpandEnv(eff.Url);
                result.Add(eff);
            }
            return result;
        }

        /// <summary>The team servers for the settings page: no header values (they may contain secrets).</summary>
        public static JObject ToUiJson(LoadResult team) => new JObject
        {
            ["servers"] = new JArray(team.Servers.Select(s => new JObject
            {
                ["Name"] = s.Name, ["Url"] = s.Url, ["Enabled"] = s.Enabled, ["TimeoutSeconds"] = s.TimeoutSeconds,
                ["WindowsLogin"] = s.WindowsLogin, ["HeaderNames"] = new JArray(s.HeaderNames()), ["Tools"] = JObject.FromObject(s.Tools),
            })),
            ["info"] = string.Join("\n", team.Lines),
            ["pluginFile"] = PluginFile,
        };
    }
}
