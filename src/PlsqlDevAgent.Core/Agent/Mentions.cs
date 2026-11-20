using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Agent.Tools;

namespace PlsqlDevAgent.Agent
{
    /// <summary>
    /// @-mentions in the prompt: "@EMP_API", "@SCOTT.EMP" or "@sql/pkg_orders.pkb" attach the object's source /
    /// description or the file's content to the message, so the model does not have to look them up; "@server:name"
    /// attaches a resource of an MCP server.
    /// </summary>
    public static class Mentions
    {
        // A path needs at least one slash; everything else is an (OWNER.)NAME or a file name in the workspace root.
        private static readonly Regex MentionRegex = new Regex(
            @"(?<![\w@])@(?<m>(?:[\w.\-]+[/\\])+[\w.\-]+|[A-Za-z][\w$#]*(?:\.[A-Za-z][\w$#]*)?)",
            RegexOptions.Compiled);

        private static readonly HashSet<string> SourceTypes = new HashSet<string>
        {
            "PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "TYPE BODY", "VIEW", "JAVA SOURCE",
        };

        public static List<string> Parse(string text) =>
            MentionRegex.Matches(text ?? "").Cast<Match>().Select(m => m.Groups["m"].Value.TrimEnd('.')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // "@server:reference": a resource of an MCP server (only for configured servers; otherwise "@name" as before)
        private static readonly Regex McpMentionRegex = new Regex(@"(?<![\w@])@(?<s>[A-Za-z][A-Za-z0-9_-]{0,23}):(?<r>[^\s]+)", RegexOptions.Compiled);

        /// <summary>The "@server:reference" mentions of configured MCP servers, and the text without them.</summary>
        public static List<(string Server, string Reference)> ParseMcp(string text, Func<string, bool> isServer, out string rest)
        {
            var list = new List<(string, string)>();
            var sb = new StringBuilder(text ?? "");
            foreach (var m in McpMentionRegex.Matches(text ?? "").Cast<Match>().Reverse()) // from the end: earlier indexes stay valid
            {
                if (!isServer(m.Groups["s"].Value)) continue;
                var reference = m.Groups["r"].Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '"', '\'');
                if (reference.Length == 0) continue;
                list.Insert(0, (m.Groups["s"].Value, reference));
                sb.Remove(m.Index, m.Length).Insert(m.Index, " ");
            }
            rest = sb.ToString();
            return list.Distinct().ToList();
        }

        /// <summary>Builds the attachment block for the LLM and a list of chips for the UI.</summary>
        public static async Task<(string Text, JArray Items)> ResolveAsync(string text, ToolContext ctx)
        {
            var mcp = new List<(string Server, string Reference)>();
            if (ctx.Mcp != null)
            {
                mcp = ParseMcp(text, ctx.Mcp.IsServer, out var rest);
                text = rest;
            }
            var mentions = Parse(text);
            var items = new JArray();
            if (mentions.Count == 0 && mcp.Count == 0) return ("", items);

            var sb = new StringBuilder("\n\n<attachments>\n");
            int budget = ctx.Config.MaxToolOutputChars * 3;
            foreach (var (server, reference) in mcp.Take(10))
            {
                var label = server + ":" + reference;
                try
                {
                    var (uri, content) = await ctx.Mcp.ReadMentionAsync(server, reference, ctx.Cancellation).ConfigureAwait(false);
                    content = Db.Truncate(content.Replace("\r\n", "\n").TrimEnd(), Math.Max(2000, Math.Min(ctx.Config.MaxToolOutputChars, budget)));
                    budget -= content.Length;
                    items.Add(new JObject { ["label"] = label, ["ok"] = true, ["detail"] = Util.L.T("{0}, {1} lines", Util.L.T("MCP resource"), content.Split('\n').Length) });
                    sb.Append($"<attachment name=\"{label}\" kind=\"MCP resource\">\n{Mcp.McpTool.WrapExternal(server, "resource", uri, content)}\n</attachment>\n");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var message = ex.GetBaseException().Message;
                    Util.Log.Warn($"Resolving @{label} failed: {message}");
                    items.Add(new JObject { ["label"] = "@" + label, ["ok"] = false, ["detail"] = message.Length > 120 ? message.Substring(0, 117) + "…" : message });
                    sb.Append($"<attachment name=\"{label}\">(could not be read: {message})</attachment>\n");
                }
            }
            foreach (var mention in mentions.Take(10))
            {
                string kind = null, label = mention, content = null;
                try
                {
                    var file = ResolveFile(ctx, mention);
                    if (file != null)
                    {
                        kind = "file";
                        label = Workspace.Relative(ctx, file);
                        content = TextFile.Read(file, out _);
                    }
                    else if (ctx.Ide.GetContext().Connected)
                    {
                        var obj = Db.Resolve(ctx, mention, null).FirstOrDefault();
                        if (obj == null && mention.Contains('.'))
                            obj = Db.Resolve(ctx, mention.Split('.')[0], null).FirstOrDefault(); // PACKAGE.PROCEDURE
                        if (obj != null)
                        {
                            kind = obj.Type;
                            label = $"{obj.Owner}.{obj.Name}";
                            var args = new JObject { ["name"] = label, ["object_type"] = obj.Type };
                            AgentTool tool = SourceTypes.Contains(obj.Type) ? (AgentTool)new GetObjectSourceTool() : new DescribeObjectTool();
                            content = await tool.ExecuteAsync(args, ctx);
                            if (obj.Type == "VIEW") content = await new DescribeObjectTool().ExecuteAsync(args, ctx) + "\n\n" + content;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Util.Log.Warn($"Resolving @{mention} failed: {ex.Message}");
                }

                if (content == null)
                {
                    items.Add(new JObject { ["label"] = "@" + mention, ["ok"] = false, ["detail"] = Util.L.T("not found") });
                    sb.Append($"<attachment name=\"{mention}\">(could not be resolved: no such file or database object)</attachment>\n");
                    continue;
                }
                content = Db.Truncate(content, Math.Max(2000, Math.Min(ctx.Config.MaxToolOutputChars, budget)));
                budget -= content.Length;
                int lines = content.Split('\n').Length;
                items.Add(new JObject { ["label"] = label, ["ok"] = true, ["detail"] = Util.L.T("{0}, {1} lines", kind.ToLowerInvariant(), lines) });
                sb.Append($"<attachment name=\"{label}\" kind=\"{kind}\">\n{content.Replace("\r\n", "\n").TrimEnd()}\n</attachment>\n");
            }
            sb.Append("</attachments>");
            return (sb.ToString(), items);
        }

        private static string ResolveFile(ToolContext ctx, string mention)
        {
            if (!ctx.Config.HasWorkspace) return null;
            try
            {
                var full = Workspace.Resolve(ctx, mention);
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        /// <summary>Autocomplete for the input box.</summary>
        public static JArray Suggest(string query, ToolContext ctx)
        {
            var items = new JArray();
            query = (query ?? "").Trim();
            // resources of MCP servers ("@server:…"), from the server's list in memory: no network access
            var mcp = ctx.Mcp?.SuggestMentions(query) ?? new JArray();
            int colon = query.IndexOf(':');
            if (colon > 0 && ctx.Mcp != null && ctx.Mcp.IsServer(query.Substring(0, colon))) return mcp;
            foreach (var item in mcp) items.Add(item);
            try
            {
                // setting off: no database traffic while the user types (workspace files are still suggested)
                if (ctx.Config.MentionSuggestions && !query.Contains("/") && !query.Contains("\\") && ctx.Ide.GetContext().Connected)
                {
                    string owner = null, prefix = query;
                    if (query.Contains('.')) { owner = SqlUtil.Ident(query.Split('.')[0]); prefix = query.Split('.')[1]; }
                    var sql = "select owner, object_name, object_type from (select owner, object_name, object_type from all_objects " +
                              $"where owner = {SqlUtil.Lit(owner ?? ctx.CurrentSchema)} and object_name like {SqlUtil.Lit(prefix.ToUpperInvariant().Replace("_", "\\_") + "%")} escape '\\' " +
                              "and object_type in ('TABLE','VIEW','PACKAGE','PROCEDURE','FUNCTION','TRIGGER','TYPE','SEQUENCE','MATERIALIZED VIEW','SYNONYM') " +
                              "order by object_name, object_type) where rownum <= 15";
                    foreach (var r in ctx.Ide.Query(sql, 15).Rows)
                        items.Add(new JObject { ["label"] = owner != null ? $"{r[0]}.{r[1]}" : r[1], ["detail"] = r[2].ToLowerInvariant(), ["insert"] = owner != null ? $"{r[0]}.{r[1]}" : r[1] });
                }
            }
            catch (Exception ex) { Util.Log.Warn("Mention suggestions (database) failed: " + ex.Message); }

            try
            {
                if (ctx.Config.HasWorkspace)
                {
                    var root = Workspace.Root(ctx);
                    foreach (var f in Workspace.EnumerateFiles(root, "*", true)
                                 .Select(f => Workspace.Relative(ctx, f).Replace('\\', '/'))
                                 .Where(f => !f.StartsWith(".") && f.IndexOf(query.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) >= 0)
                                 .Take(10))
                        items.Add(new JObject { ["label"] = f, ["detail"] = "file", ["insert"] = f });
                }
            }
            catch (Exception ex) { Util.Log.Warn("Mention suggestions (files) failed: " + ex.Message); }
            return items;
        }
    }
}
