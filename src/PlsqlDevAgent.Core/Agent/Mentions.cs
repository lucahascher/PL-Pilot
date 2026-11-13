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
    /// description or the file's content to the message, so the model does not have to look them up.
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

        /// <summary>Builds the attachment block for the LLM and a list of chips for the UI.</summary>
        public static async Task<(string Text, JArray Items)> ResolveAsync(string text, ToolContext ctx)
        {
            var mentions = Parse(text);
            var items = new JArray();
            if (mentions.Count == 0) return ("", items);

            var sb = new StringBuilder("\n\n<attachments>\n");
            int budget = ctx.Config.MaxToolOutputChars * 3;
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
            try
            {
                if (ctx.Ide.GetContext().Connected && !query.Contains("/") && !query.Contains("\\"))
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
