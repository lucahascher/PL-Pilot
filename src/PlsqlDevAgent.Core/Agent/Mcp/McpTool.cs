using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Agent.Tools;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Agent.Mcp
{
    /// <summary>
    /// A tool of an MCP server, offered to the model like a built-in tool ("tickets__get_ticket"). Read tools run without
    /// asking; tools that change data ask the user first and, being <see cref="AgentTool.Mutating"/>, are not offered in
    /// plan mode, in read-only mode and on protected connections, and not to helper agents.
    /// </summary>
    public sealed class McpTool : AgentTool
    {
        private readonly McpManager _manager;
        private readonly McpToolInfo _info;
        private readonly string _name;
        private readonly bool _write;
        private readonly JObject _schema;
        private readonly string _description;

        internal McpTool(McpManager manager, string server, string name, McpToolInfo info, bool write)
        {
            _manager = manager;
            ServerName = server;
            _name = name;
            _info = info;
            _write = write;
            _schema = NormalizeSchema(info.InputSchema);
            var desc = (info.Description ?? "").Trim();
            if (desc.Length == 0) desc = info.Title ?? info.Name;
            if (desc.Length > 2000) desc = desc.Substring(0, 2000) + " …";
            _description = $"[External system {server} (MCP){(write ? ", changes data: the user is asked first" : ", read-only")}] {desc}";
        }

        public string ServerName { get; }
        public string ToolName => _info.Name;
        public override string Name => _name;
        public override string Description => _description;
        public override JObject Parameters => (JObject)_schema.DeepClone();
        public override bool Mutating => _write;

        /// <summary>Read-only external lookups may run in parallel with each other (plain HTTP, no database session).</summary>
        public bool CanRunInParallel => !_write;

        public override string Summarize(JObject args)
        {
            // the first short text argument (ticket key, search text …)
            foreach (var p in args?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                if (p.Value.Type != JTokenType.String && p.Value.Type != JTokenType.Integer) continue;
                var s = Regex.Replace(p.Value.ToString(), @"\s+", " ").Trim();
                if (s.Length == 0) continue;
                return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
            }
            return "";
        }

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            if (_write)
            {
                var blocked = ctx.ChangeBlockedReason();
                if (blocked != null) return $"Error: not executed, {blocked}. Tell the user what you would change in {ServerName} instead.";
                bool destructive = _info.DestructiveHint == true;
                var approval = await ctx.ApproveAsync(new ApprovalRequest
                {
                    Category = $"mcp:{ServerName}:{_info.Name}" + (destructive ? "-destructive" : ""),
                    Title = L.T("Call {0} on {1} (changes data in the external system)", _info.Name, ServerName),
                    Detail = (args ?? new JObject()).ToString(Formatting.Indented),
                    DetailLanguage = "json",
                    Warning = destructive ? L.T("{0} marks this tool as destructive: it may delete or overwrite data there.", ServerName) : null,
                }).ConfigureAwait(false);
                if (!approval.Approved) return approval.RejectionMessage;
            }

            string lastProgress = null;
            Action<string> progress = ctx.Progress == null ? null : (Action<string>)(text =>
            {
                if (text == lastProgress) return;
                lastProgress = text;
                ctx.Progress(ServerName + ": " + (text.Length > 200 ? text.Substring(0, 200) + "…" : text));
            });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await _manager.CallToolAsync(ServerName, _info.Name, args ?? new JObject(), progress, ctx.Cancellation).ConfigureAwait(false);
            Log.Info($"MCP {ServerName}: {_info.Name} answered in {watch.Elapsed.TotalSeconds:0.0} s{((bool?)result["isError"] == true ? " (tool error)" : "")}");
            var text = FormatResult(ServerName, _info.Name, result, Math.Max(2000, ctx.Config.ToolOutputLimit), out bool isError);
            if (isError) throw new McpException(L.T("{0} reported an error:", ServerName) + "\n" + text);
            return text;
        }

        /// <summary>
        /// The tool result as text for the model, wrapped in &lt;external_data&gt; (the system prompt tells the model that
        /// this is data, not instructions). Content cannot close the wrapper early.
        /// </summary>
        internal static string FormatResult(string server, string tool, JObject result, int maxChars, out bool isError)
        {
            isError = (bool?)result["isError"] == true;
            var sb = new StringBuilder();
            foreach (var item in (result["content"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (sb.Length > 0) sb.Append("\n\n");
                switch ((string)item["type"])
                {
                    case "text":
                        sb.Append((string)item["text"] ?? "");
                        break;
                    case "resource":
                        var res = item["resource"] as JObject ?? new JObject();
                        var resText = (string)res["text"];
                        sb.Append($"[resource {(string)res["uri"]}{MimeSuffix(res)}]");
                        if (resText != null) sb.Append('\n').Append(resText);
                        else sb.Append(" (binary content, not shown)");
                        break;
                    case "resource_link":
                        sb.Append($"[link {(string)item["name"] ?? ""}: {(string)item["uri"]}]");
                        if (!string.IsNullOrWhiteSpace((string)item["description"])) sb.Append(' ').Append((string)item["description"]);
                        break;
                    case "image":
                    case "audio":
                        sb.Append($"[{(string)item["type"]}{MimeSuffix(item)} not shown: the model only reads text]");
                        break;
                    default:
                        sb.Append(item.ToString(Formatting.None));
                        break;
                }
            }
            if (sb.Length == 0 && result["structuredContent"] != null)
                sb.Append(result["structuredContent"].ToString(Formatting.Indented));
            if (sb.Length == 0) sb.Append("(the tool returned no content)");

            var text = sb.ToString();
            int limit = Math.Max(500, maxChars - 300);
            if (text.Length > limit)
                text = text.Substring(0, limit) + $"\n… [shortened: {text.Length - limit} more characters; ask the tool for less, e.g. a narrower search]";
            return WrapExternal(server, "tool", tool, text);
        }

        /// <summary>
        /// Content of an external system for the model: &lt;external_data source="server" kind="name"&gt;…&lt;/external_data&gt;.
        /// The content cannot close the block early (the system prompt says that this is data, not instructions).
        /// </summary>
        internal static string WrapExternal(string server, string kind, string name, string text)
        {
            text = Regex.Replace(text ?? "", "</?external_data", m => m.Value.Replace("external_data", "external-data"), RegexOptions.IgnoreCase);
            string Attr(string v) => (v ?? "").Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");
            return $"<external_data source=\"{Attr(server)}\" {kind}=\"{Attr(name)}\">\n{text}\n</external_data>";
        }

        private static string MimeSuffix(JObject o) => string.IsNullOrEmpty((string)o["mimeType"]) ? "" : ", " + (string)o["mimeType"];

        /// <summary>
        /// The input schema as the function parameters for an OpenAI compatible server: always an object with properties;
        /// "$schema" is removed (some servers reject it).
        /// </summary>
        internal static JObject NormalizeSchema(JObject schema)
        {
            var s = schema == null ? new JObject() : (JObject)schema.DeepClone();
            s.Remove("$schema");
            if ((string)s["type"] != "object") s["type"] = "object";
            if (!(s["properties"] is JObject)) s["properties"] = new JObject();
            return s;
        }
    }
}
