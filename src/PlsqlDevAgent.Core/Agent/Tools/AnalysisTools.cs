using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.Util;
using static PlsqlDevAgent.Agent.SqlUtil;

namespace PlsqlDevAgent.Agent.Tools
{
    /// <summary>Dependency graph of the data dictionary (ALL_DEPENDENCIES), walked level by level.</summary>
    internal static class Dependencies
    {
        public sealed class Node
        {
            public int Depth;
            public string Owner, Name, Type, Status;
            public string Key => KeyOf(Owner, Name, Type);
            public override string ToString() => $"{Type} {Owner}.{Name}";
        }

        public static string KeyOf(string owner, string name, string type) => $"{owner}.{name}|{type}";

        /// <summary>Nothing can depend on these, so the walk does not look further from them.</summary>
        private static readonly HashSet<string> Leaves = new HashSet<string> { "PACKAGE BODY", "TYPE BODY", "TRIGGER" };

        /// <summary>Types whose change can invalidate other objects.</summary>
        public static bool CanHaveDependents(string type) => !Leaves.Contains(type ?? "");

        /// <summary>
        /// Objects that depend on the roots, directly (depth 1) or indirectly, with their status. Walks breadth first
        /// with one query per level (a CONNECT BY would enumerate every path, which explodes for central packages).
        /// The roots' own bodies are left out.
        /// </summary>
        public static List<Node> UsedBy(ToolContext ctx, IEnumerable<(string Owner, string Name, string Type)> roots, int maxDepth, int maxNodes,
            out bool truncated, out bool deeper)
        {
            truncated = deeper = false;
            var result = new List<Node>();
            var seen = new HashSet<string>();
            var frontier = new List<(string Owner, string Name, string Type)>();
            foreach (var r in roots)
            {
                seen.Add(KeyOf(r.Owner, r.Name, r.Type));
                seen.Add(KeyOf(r.Owner, r.Name, r.Type + " BODY"));
                frontier.Add(r);
                // other objects reference the container table of a materialized view
                if (r.Type == "MATERIALIZED VIEW") frontier.Add((r.Owner, r.Name, "TABLE"));
            }
            for (int depth = 1; frontier.Count > 0; depth++)
            {
                if (depth > maxDepth)
                {
                    deeper = true;
                    break;
                }
                var next = new List<(string Owner, string Name, string Type)>();
                for (int i = 0; i < frontier.Count; i += 100)
                {
                    var tuples = string.Join(", ", frontier.Skip(i).Take(100).Select(f => $"({Lit(f.Owner)}, {Lit(f.Name)}, {Lit(f.Type)})"));
                    var rows = ctx.Ide.Query(
                        "select distinct d.owner, d.name, d.type, o.status from all_dependencies d " +
                        "left join all_objects o on o.owner = d.owner and o.object_name = d.name and o.object_type = d.type " +
                        $"where (d.referenced_owner, d.referenced_name, d.referenced_type) in ({tuples}) order by 1, 3, 2", maxNodes + 1).Rows;
                    foreach (var row in rows)
                    {
                        var node = new Node { Depth = depth, Owner = row[0], Name = row[1], Type = row[2], Status = row[3] };
                        if (!seen.Add(node.Key)) continue;
                        result.Add(node);
                        if (CanHaveDependents(node.Type)) next.Add((node.Owner, node.Name, node.Type));
                        if (result.Count >= maxNodes)
                        {
                            truncated = true;
                            return result;
                        }
                    }
                }
                frontier = next;
            }
            return result;
        }

        /// <summary>
        /// Remembers which objects depending on the compiled units are invalid before compiling, so the result can
        /// name the ones the compile invalidated. Never throws: without the dictionary it reports nothing.
        /// </summary>
        internal sealed class InvalidationWatch
        {
            private const int Depth = 3, MaxNodes = 300;
            private readonly ToolContext _ctx;
            private readonly List<(string Owner, string Name, string Type)> _roots;
            private readonly HashSet<string> _compiled;
            private HashSet<string> _invalidBefore;

            public InvalidationWatch(ToolContext ctx, IEnumerable<(string Owner, string Name, string Type)> units)
            {
                _ctx = ctx;
                var list = units.ToList();
                _compiled = new HashSet<string>(list.Select(u => KeyOf(u.Owner, u.Name, u.Type)));
                _roots = list.Where(u => CanHaveDependents(u.Type)).Distinct().ToList();
                if (_roots.Count == 0) return;
                _invalidBefore = Invalid();
            }

            private HashSet<string> Invalid()
            {
                try
                {
                    return new HashSet<string>(UsedBy(_ctx, _roots, Depth, MaxNodes, out _, out _)
                        .Where(n => n.Status == "INVALID").Select(n => n.Key));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn("Dependent objects could not be checked: " + ex.Message);
                    return null;
                }
            }

            /// <summary>"\nDependent objects that became INVALID …" or "".</summary>
            public string Report()
            {
                if (_invalidBefore == null) return "";
                List<Node> after;
                try { after = UsedBy(_ctx, _roots, Depth, MaxNodes, out _, out _); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn("Dependent objects could not be checked: " + ex.Message);
                    return "";
                }
                var newly = after.Where(n => n.Status == "INVALID" && !_invalidBefore.Contains(n.Key) && !_compiled.Contains(n.Key)).ToList();
                if (newly.Count == 0) return "";
                var names = string.Join(", ", newly.Take(20).Select(n => n.ToString())) + (newly.Count > 20 ? $" and {newly.Count - 20} more" : "");
                return $"\nDependent objects that became INVALID ({newly.Count}): {names}. Oracle recompiles them automatically on " +
                       "their next use; call recompile_invalid to check now that they still compile.";
            }
        }
    }

    internal sealed class FindDependenciesTool : AgentTool
    {
        public override string Name => "find_dependencies";
        public override string Description =>
            "Impact analysis from the data dictionary (ALL_DEPENDENCIES). direction used_by (default): which objects use " +
            "this object, directly and indirectly, with their status – what can break or become invalid if you change it. " +
            "With column (tables and views): the source lines of dependent objects that mention that column. " +
            "direction uses: what this object references. Dynamic SQL is not covered: also use search_source for names " +
            "used in strings.";
        public override JObject Parameters => Schema(
            Req("name", "string", "Object name, optionally OWNER.NAME."),
            Opt("object_type", "string", "Optional type to disambiguate, e.g. TABLE, PACKAGE, VIEW, TYPE."),
            Opt("direction", "string", "used_by (default) or uses.", "used_by", "uses"),
            Opt("column", "string", "Only for used_by on a table or view: find the usages of this column."),
            Opt("max_depth", "integer", "used_by: levels of indirect dependencies (default 3, maximum 10)."));
        public override string Summarize(JObject args) =>
            $"{Str(args, "name")}{(Str(args, "column") is string c ? "." + c : "")} ({Str(args, "direction", "used_by")})";

        private const int MaxNodes = 500;

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var obj = Db.ResolveOne(ctx, ReqStr(args, "name"), Str(args, "object_type"));
            var direction = (Str(args, "direction", "used_by") ?? "used_by").Trim().ToLowerInvariant();
            string text;
            if (direction == "uses") text = Uses(ctx, obj);
            else if (!string.IsNullOrWhiteSpace(Str(args, "column"))) text = ColumnUsages(ctx, obj, Ident(Str(args, "column").Trim()));
            else text = UsedBy(ctx, obj, Math.Max(1, Math.Min(Int(args, "max_depth", 3), 10)));
            return Task.FromResult(Db.Truncate(text, ctx.Config.ToolOutputLimit));
        }

        private const string NotCovered =
            "Not covered by the data dictionary: dynamic SQL (EXECUTE IMMEDIATE, DBMS_SQL, SQL in strings), code in other " +
            "databases or schemas you cannot see, and application code outside the database. search_source finds names in strings.";

        private static string UsedBy(ToolContext ctx, Db.ObjRef obj, int maxDepth)
        {
            var sb = new StringBuilder();
            var type = obj.Type;
            if (type == "PACKAGE BODY" || type == "TYPE BODY")
            {
                type = type.Replace(" BODY", "");
                sb.Append($"Nothing can depend on a {obj.Type}: changing a body does not invalidate other objects. Showing the users of the {type} instead.\n");
            }
            var nodes = Dependencies.UsedBy(ctx, new[] { (obj.Owner, obj.Name, type) }, maxDepth, MaxNodes, out var truncated, out var deeper);
            if (nodes.Count == 0)
                return sb.Append($"No objects visible to the current user depend on {type} {obj.Owner}.{obj.Name}.\n{NotCovered}").ToString();
            sb.Append($"Objects that depend on {type} {obj.Owner}.{obj.Name} (level 1 = direct; a change can invalidate them):\n");
            foreach (var level in nodes.GroupBy(n => n.Depth))
            {
                sb.Append($"level {level.Key}:\n");
                foreach (var n in level) sb.Append($"  {n}{(n.Status == "VALID" || string.IsNullOrEmpty(n.Status) ? "" : "  " + n.Status)}\n");
            }
            int invalid = nodes.Count(n => n.Status == "INVALID");
            sb.Append($"{nodes.Count} objects{(invalid > 0 ? $", {invalid} of them INVALID already" : "")}.");
            if (truncated) sb.Append($" Stopped after {MaxNodes} objects.");
            else if (deeper) sb.Append($" More levels exist (raise max_depth, currently {maxDepth}).");
            return sb.Append('\n').Append(NotCovered).ToString();
        }

        private static string Uses(ToolContext ctx, Db.ObjRef obj)
        {
            var type = obj.Type.Replace(" BODY", "");
            var r = ctx.Ide.Query(
                "select d.referenced_type, d.referenced_owner, d.referenced_name, d.referenced_link_name " +
                $"from all_dependencies d where d.owner = {Lit(obj.Owner)} and d.name = {Lit(obj.Name)} and d.type in ({Lit(type)}, {Lit(type + " BODY")}) " +
                "and not (d.referenced_owner = 'SYS' and d.referenced_name in ('STANDARD', 'DBMS_STANDARD', 'PLITBLM', 'SYS_STUB_FOR_PURITY_ANALYSIS')) " +
                "and not (d.referenced_owner = d.owner and d.referenced_name = d.name) " +
                "group by d.referenced_type, d.referenced_owner, d.referenced_name, d.referenced_link_name " +
                "order by d.referenced_type, d.referenced_owner, d.referenced_name", MaxNodes);
            if (r.Rows.Count == 0) return $"{obj} references no other objects (besides the STANDARD package).";
            var sb = new StringBuilder($"{type} {obj.Owner}.{obj.Name} references (spec and body):\n");
            foreach (var row in r.Rows)
                sb.Append($"  {row[0]} {row[1]}.{row[2]}{(string.IsNullOrEmpty(row[3]) ? "" : "@" + row[3])}\n");
            return sb.Append("Dynamic SQL is not covered; search the source for EXECUTE IMMEDIATE / DBMS_SQL.").ToString();
        }

        private static readonly HashSet<string> SourceTypes = new HashSet<string>
        {
            "PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "TYPE BODY",
        };

        private static string ColumnUsages(ToolContext ctx, Db.ObjRef obj, string column)
        {
            if (obj.Type != "TABLE" && obj.Type != "VIEW" && obj.Type != "MATERIALIZED VIEW")
                return $"column only works for tables and views; {obj} is a {obj.Type}.";
            var exists = ctx.Ide.Query($"select 1 from all_tab_columns where owner = {Lit(obj.Owner)} and table_name = {Lit(obj.Name)} and column_name = {Lit(column)}", 1);
            if (exists.Rows.Count == 0) return $"{obj} has no column {column} (describe_object lists the columns).";

            var direct = Dependencies.UsedBy(ctx, new[] { (obj.Owner, obj.Name, obj.Type) }, 1, MaxNodes, out var truncated, out _);
            // a synonym on the table counts as a direct user: its users reference the table through it
            var synonyms = direct.Where(n => n.Type == "SYNONYM").Select(n => (n.Owner, n.Name, n.Type)).ToList();
            if (synonyms.Count > 0)
            {
                direct.AddRange(Dependencies.UsedBy(ctx, synonyms, 1, MaxNodes, out _, out _));
                direct = direct.GroupBy(n => n.Key).Select(g => g.First()).ToList();
            }
            if (direct.Count == 0) return $"No objects visible to the current user depend on {obj}.\n{NotCovered}";

            // word boundaries for Oracle identifiers: the column must not be part of a longer name
            var pattern = "(^|[^A-Z0-9_$#])" + Regex.Replace(column, @"[\\^$.|?*+()\[\]{}]", m => "\\" + m.Value) + "([^A-Z0-9_$#]|$)";
            var sb = new StringBuilder($"Usages of column {obj.Owner}.{obj.Name}.{column} in the {direct.Count} objects that depend on {obj.Type} {obj.Owner}.{obj.Name}:\n");
            var matched = new HashSet<string>();

            var units = direct.Where(n => SourceTypes.Contains(n.Type)).ToList();
            for (int i = 0; i < units.Count; i += 100)
            {
                var tuples = string.Join(", ", units.Skip(i).Take(100).Select(n => $"({Lit(n.Owner)}, {Lit(n.Name)}, {Lit(n.Type)})"));
                var r = ctx.Ide.Query(
                    "select s.owner, s.name, s.type, s.line, trim(s.text) from all_source s " +
                    $"where (s.owner, s.name, s.type) in ({tuples}) and regexp_like(upper(s.text), {Lit(pattern)}) " +
                    "order by s.owner, s.name, s.type, s.line", 400);
                foreach (var row in r.Rows)
                {
                    matched.Add(Dependencies.KeyOf(row[0], row[1], row[2]));
                    var line = (row[4] ?? "").TrimEnd('\r', '\n');
                    if (line.Length > 300) line = line.Substring(0, 300) + "…";
                    sb.Append($"{row[2]} {row[0]}.{row[1]} line {row[3]}: {line}\n");
                }
                if (r.Truncated) sb.Append("... more matches exist (use search_source for a single object).\n");
            }

            var views = direct.Where(n => n.Type == "VIEW").ToList();
            if (views.Count > 0)
            {
                try
                {
                    var tuples = string.Join(", ", views.Take(200).Select(n => $"({Lit(n.Owner)}, {Lit(n.Name)})"));
                    var r = ctx.Ide.Query(
                        $"select owner, view_name from all_views where (owner, view_name) in ({tuples}) and regexp_like(upper(text_vc), {Lit(pattern)})", 200);
                    foreach (var row in r.Rows)
                    {
                        matched.Add(Dependencies.KeyOf(row[0], row[1], "VIEW"));
                        sb.Append($"VIEW {row[0]}.{row[1]}: its query mentions {column} (get_object_source shows it)\n");
                    }
                }
                catch (OracleSqlException) { } // TEXT_VC exists from 12c on: the views are listed below
            }

            var others = direct.Where(n => !matched.Contains(n.Key) && n.Type != "SYNONYM").ToList();
            if (matched.Count == 0) sb.Append($"No dependent object mentions {column} in its source.\n");
            if (others.Count > 0)
                sb.Append($"Depend on the {obj.Type.ToLowerInvariant()} without mentioning {column} by name (SELECT *, %ROWTYPE, or a view text longer than 4000 characters): ")
                  .Append(string.Join(", ", others.Take(50).Select(n => n.ToString()))).Append(others.Count > 50 ? $" and {others.Count - 50} more" : "").Append('\n');
            if (truncated) sb.Append($"Stopped after {MaxNodes} dependent objects.\n");
            return sb.Append(NotCovered).ToString();
        }
    }

    internal sealed class RecompileInvalidTool : AgentTool
    {
        public override string Name => "recompile_invalid";
        public override string Description =>
            "Recompiles INVALID objects (ALTER … COMPILE REUSE SETTINGS, so their compiler settings stay) in dependency " +
            "order and reports which are valid now and the errors of the others. Without objects: all invalid objects " +
            "of the schema. Use it after changing a specification, type, view or table to check that the objects using " +
            "it still compile. The user must approve.";
        public override JObject Parameters => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["objects"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["description"] = "Names (NAME or OWNER.NAME) to recompile, e.g. from compile_plsql's list of invalidated objects. Omit for all invalid objects of the schema.",
                },
                ["owner"] = new JObject { ["type"] = "string", ["description"] = "Schema when objects is omitted; default the current schema." },
            },
        };
        public override bool Mutating => true;

        public override string Summarize(JObject args) =>
            args?["objects"] is JArray a && a.Count > 0 ? string.Join(", ", a.Take(5).Select(x => (string)x)) + (a.Count > 5 ? " …" : "")
                : "all invalid objects" + (Str(args, "owner") is string o ? " in " + o : "");

        private static readonly Dictionary<string, int> Order = new Dictionary<string, int>
        {
            ["TYPE"] = 0, ["PACKAGE"] = 1, ["PROCEDURE"] = 1, ["FUNCTION"] = 1, ["SYNONYM"] = 1, ["VIEW"] = 2,
            ["MATERIALIZED VIEW"] = 3, ["TYPE BODY"] = 4, ["PACKAGE BODY"] = 5, ["TRIGGER"] = 6,
        };

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var invalid = Candidates(ctx, args, out var notFound);
            if (invalid.Count == 0)
                return "No invalid objects found" + (notFound.Count > 0 ? " (not found or not invalid: " + string.Join(", ", notFound) + ")" : "") + ".";

            var statements = invalid.Select(Statement).ToList();
            if (!ctx.Config.AutoApproveSql)
            {
                var approval = await ctx.ApproveAsync(new ApprovalRequest
                {
                    Category = "sql",
                    Title = L.T("Recompile {0} invalid objects ({1})", invalid.Count, ctx.Ide.GetContext().ConnectionLabel),
                    Detail = string.Join(";\n", statements) + ";",
                });
                if (!approval.Approved) return approval.RejectionMessage;
            }

            // a few passes: an object can only compile after the objects it uses are valid
            var remaining = invalid;
            var failed = new Dictionary<string, string>();
            for (int pass = 0; pass < 3 && remaining.Count > 0; pass++)
            {
                foreach (var n in remaining)
                {
                    ctx.Cancellation.ThrowIfCancellationRequested();
                    var res = ctx.Ide.Execute(Statement(n));
                    if (!res.Ok && !res.CompiledWithErrors) failed[n.Key] = res.ErrorMessage;
                    else failed.Remove(n.Key);
                    ctx.Ide.RefreshObject(n.Type, n.Owner, n.Name, 2);
                }
                var statuses = Statuses(ctx, remaining);
                var still = remaining.Where(n => statuses.TryGetValue(n.Key, out var s) && s == "INVALID").ToList();
                if (still.Count == remaining.Count) break; // no progress
                remaining = still;
            }

            var sb = new StringBuilder($"Recompiled {invalid.Count} object{(invalid.Count == 1 ? "" : "s")}: {invalid.Count - remaining.Count} valid now");
            sb.Append(remaining.Count == 0 ? ".\n" : $", {remaining.Count} still INVALID:\n");
            foreach (var n in remaining.Take(20))
            {
                sb.Append($"{n}: ");
                if (failed.TryGetValue(n.Key, out var error)) sb.Append($"could not be compiled: {error}\n");
                else
                {
                    var diag = Db.Diagnostics(ctx, n.Owner, n.Name, n.Type, 0);
                    var lines = (diag.Errors ?? "no errors listed (an object it uses may be invalid)\n").Split('\n').Where(l => l.Length > 0).ToList();
                    sb.Append('\n').Append(string.Join("\n", lines.Take(5).Select(l => "  " + l))).Append(lines.Count > 5 ? $"\n  ... {lines.Count - 5} more errors" : "").Append('\n');
                }
            }
            if (remaining.Count > 20) sb.Append($"... and {remaining.Count - 20} more.\n");
            if (notFound.Count > 0) sb.Append("Not found or not invalid: " + string.Join(", ", notFound) + "\n");
            return sb.ToString().TrimEnd();
        }

        private static List<Dependencies.Node> Candidates(ToolContext ctx, JObject args, out List<string> notFound)
        {
            notFound = new List<string>();
            var types = string.Join(", ", Order.Keys.Select(Lit));
            var names = (args?["objects"] as JArray)?.Select(t => (string)t).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
            var sql = new StringBuilder($"select owner, object_name, object_type, status from all_objects where status = 'INVALID' and object_type in ({types}) and ");
            if (names.Count > 0)
            {
                var conditions = new List<string>();
                foreach (var full in names.Take(200))
                {
                    // "PACKAGE BODY SCOTT.X" as listed by compile_plsql works too
                    var cleaned = Regex.Replace(full.Trim(), @"^(PACKAGE BODY|TYPE BODY|MATERIALIZED VIEW|PACKAGE|PROCEDURE|FUNCTION|TRIGGER|TYPE|VIEW|SYNONYM)\s+", "", RegexOptions.IgnoreCase);
                    SplitName(cleaned, out var owner, out var name);
                    conditions.Add($"(owner = {Lit(owner ?? ctx.CurrentSchema)} and object_name = {Lit(name)})");
                }
                sql.Append('(').Append(string.Join(" or ", conditions)).Append(')');
            }
            else
            {
                var owner = Str(args, "owner");
                sql.Append("owner = ").Append(Lit(string.IsNullOrWhiteSpace(owner) ? ctx.CurrentSchema : Ident(owner)));
            }
            sql.Append(" and not (owner = 'PUBLIC')");
            var rows = ctx.Ide.Query(sql.ToString(), 300).Rows;
            var list = rows.Select(r => new Dependencies.Node { Owner = r[0], Name = r[1], Type = r[2], Status = r[3] })
                .OrderBy(n => Order.TryGetValue(n.Type, out var o) ? o : 9).ThenBy(n => n.Owner).ThenBy(n => n.Name).ToList();
            foreach (var full in names)
            {
                var cleaned = Regex.Replace(full.Trim(), @"^(PACKAGE BODY|TYPE BODY|MATERIALIZED VIEW|PACKAGE|PROCEDURE|FUNCTION|TRIGGER|TYPE|VIEW|SYNONYM)\s+", "", RegexOptions.IgnoreCase);
                SplitName(cleaned, out _, out var name);
                if (!list.Any(n => n.Name == name)) notFound.Add(full);
            }
            return list;
        }

        private static string Statement(Dependencies.Node n)
        {
            var q = $"\"{n.Owner}\".\"{n.Name}\"";
            switch (n.Type)
            {
                case "PACKAGE": return $"alter package {q} compile specification reuse settings";
                case "PACKAGE BODY": return $"alter package {q} compile body reuse settings";
                case "TYPE": return $"alter type {q} compile specification reuse settings";
                case "TYPE BODY": return $"alter type {q} compile body reuse settings";
                case "VIEW": return $"alter view {q} compile";
                case "MATERIALIZED VIEW": return $"alter materialized view {q} compile";
                case "SYNONYM": return $"alter synonym {q} compile";
                default: return $"alter {n.Type.ToLowerInvariant()} {q} compile reuse settings";
            }
        }

        private static Dictionary<string, string> Statuses(ToolContext ctx, List<Dependencies.Node> nodes)
        {
            var result = new Dictionary<string, string>();
            for (int i = 0; i < nodes.Count; i += 200)
            {
                var tuples = string.Join(", ", nodes.Skip(i).Take(200).Select(n => $"({Lit(n.Owner)}, {Lit(n.Name)}, {Lit(n.Type)})"));
                foreach (var r in ctx.Ide.Query($"select owner, object_name, object_type, status from all_objects where (owner, object_name, object_type) in ({tuples})", 400).Rows)
                    result[Dependencies.KeyOf(r[0], r[1], r[2])] = r[3];
            }
            return result;
        }
    }

    internal sealed class ResolveErrorStackTool : AgentTool
    {
        public override string Name => "resolve_error_stack";
        public override string Description =>
            "Resolves a pasted Oracle error stack to the source code: ORA-06512 lines (in any language, e.g. German " +
            "'ORA-06512: in \"X.Y\", Zeile 12'), DBMS_UTILITY.FORMAT_ERROR_BACKTRACE and FORMAT_CALL_STACK output. For every " +
            "frame it shows the stored unit, the enclosing subprogram and the source lines around the line of the error " +
            "(trigger line numbers are converted). Call it first when the user pastes an error from a log or a failed call.";
        public override JObject Parameters => Schema(
            Req("error_stack", "string", "The pasted text with the ORA- lines or the call stack."),
            Opt("context_lines", "integer", "Source lines shown before and after each error line (default 4, maximum 20)."));
        public override string Summarize(JObject args)
        {
            var first = (Str(args, "error_stack", "") ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            return first.Length > 100 ? first.Substring(0, 97) + "..." : first;
        }

        private static readonly Regex Ora06512 = new Regex(@"ORA-06512:[^""\r\n]*""(?<name>[^""]+)""\s*,\s*\D*?(?<line>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Ora06512Anonymous = new Regex(@"ORA-06512:[^""\r\n\d]*?(?<line>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CallStack = new Regex(
            @"^\s*(?:0x)?[0-9a-f]{6,}\s+(?<line>\d+)\s+(?<kind>package body|package|procedure|function|trigger|type body|type|anonymous block)(?:\s+(?<name>\S+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Message = new Regex(@"\b(?:ORA|PLS)-(?<code>\d{5})\b.*", RegexOptions.Compiled);

        internal sealed class Frame
        {
            public string Raw, Owner, Unit, Subprogram, Kind;
            public int Line;
            public bool Anonymous;
        }

        /// <summary>The frames and the error messages (not ORA-06512) of a pasted stack.</summary>
        internal static List<Frame> Parse(string text, List<string> messages)
        {
            var frames = new List<Frame>();
            var seen = new HashSet<string>();
            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                Frame f = null;
                Match m;
                if ((m = Ora06512.Match(line)).Success)
                    f = Named(m.Groups["name"].Value, int.Parse(m.Groups["line"].Value), null);
                else if ((m = Ora06512Anonymous.Match(line)).Success)
                    f = new Frame { Anonymous = true, Line = int.Parse(m.Groups["line"].Value) };
                else if ((m = CallStack.Match(line)).Success)
                {
                    var kind = Regex.Replace(m.Groups["kind"].Value.ToUpperInvariant(), @"\s+", " ");
                    f = kind == "ANONYMOUS BLOCK" || !m.Groups["name"].Success
                        ? new Frame { Anonymous = true, Line = int.Parse(m.Groups["line"].Value) }
                        : Named(m.Groups["name"].Value, int.Parse(m.Groups["line"].Value), kind);
                }
                else if ((m = Message.Match(line)).Success && m.Groups["code"].Value != "06512")
                {
                    if (messages != null && !messages.Contains(m.Value.Trim())) messages.Add(m.Value.Trim());
                    continue;
                }
                if (f == null) continue;
                f.Raw = line.Length > 160 ? line.Substring(0, 160) : line;
                // the same frames often appear twice (error stack and backtrace pasted together)
                var key = f.Anonymous ? null : $"{f.Owner}.{f.Unit}:{f.Line}";
                if (key != null && !seen.Add(key)) continue;
                frames.Add(f);
            }
            return frames;
        }

        private static Frame Named(string name, int line, string kind)
        {
            var parts = name.Trim().Trim('"').Split('.').Select(p => p.Trim('"')).ToArray();
            var f = new Frame { Line = line, Kind = kind };
            if (parts.Length == 1) f.Unit = parts[0];
            else
            {
                f.Owner = parts[0];
                f.Unit = parts[1];
                // FORMAT_CALL_STACK from 12.2 on: OWNER.PACKAGE.SUBPROGRAM
                if (parts.Length > 2) f.Subprogram = string.Join(".", parts.Skip(2));
            }
            return f;
        }

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            int context = Math.Max(0, Math.Min(Int(args, "context_lines", 4), 20));
            var messages = new List<string>();
            var frames = Parse(ReqStr(args, "error_stack"), messages);
            if (frames.Count == 0)
                return Task.FromResult("No stack frames found (lines like 'ORA-06512: at \"OWNER.UNIT\", line 12' or a " +
                                       "DBMS_UTILITY.FORMAT_CALL_STACK). Ask the user for the complete error text.");
            var sb = new StringBuilder();
            if (messages.Count > 0) sb.Append("Errors: ").Append(string.Join(" | ", messages.Take(10))).Append("\n\n");
            int n = 0;
            foreach (var f in frames.Take(25))
            {
                n++;
                if (f.Anonymous)
                {
                    sb.Append($"Frame {n}: {f.Raw}\n  anonymous block (the calling script or block, not stored in the database), line {f.Line}\n\n");
                    continue;
                }
                try { Resolve(ctx, f, n, context, sb); }
                catch (OracleSqlException ex) { sb.Append($"Frame {n}: {f.Raw}\n  could not be read: {ex.Message.Split('\n')[0]}\n\n"); }
            }
            if (frames.Count > 25) sb.Append($"... {frames.Count - 25} more frames not shown.\n");
            sb.Append("The line numbers belong to the version that was compiled when the error happened: if a unit was " +
                      "compiled after that (see 'last compiled'), the lines can be off – list_backups may have the older version.");
            return Task.FromResult(Db.Truncate(sb.ToString(), ctx.Config.ToolOutputLimit));
        }

        private static readonly string[] Preference = { "PACKAGE BODY", "TYPE BODY", "PROCEDURE", "FUNCTION", "TRIGGER", "PACKAGE", "TYPE" };

        private static void Resolve(ToolContext ctx, Frame f, int n, int context, StringBuilder sb)
        {
            var owner = f.Owner ?? ctx.CurrentSchema;
            sb.Append($"Frame {n}: {f.Raw}\n");
            // which source types exist, their length, and when they were compiled last
            var info = ctx.Ide.Query(
                "select s.type, count(*) lines, max(case when s.line = 1 then substr(s.text, 1, 200) end) first_line, " +
                "(select to_char(max(o.last_ddl_time), 'YYYY-MM-DD HH24:MI:SS') from all_objects o where o.owner = s.owner and o.object_name = s.name and o.object_type = s.type) last_ddl " +
                $"from all_source s where s.owner = {Lit(owner)} and s.name = {Lit(f.Unit)} group by s.owner, s.name, s.type", 10);
            var types = info.Rows.ToDictionary(r => r[0], r => (Lines: int.Parse(r[1]), First: r[2] ?? "", LastDdl: r[3]));
            if (types.Count == 0)
            {
                sb.Append($"  {owner}.{f.Unit} has no source visible to the current user (other database, missing privileges, or it was dropped).\n\n");
                return;
            }

            string type = null;
            if (f.Kind != null && types.ContainsKey(f.Kind)) type = f.Kind;
            else type = Preference.FirstOrDefault(t => types.TryGetValue(t, out var x) && x.Lines >= f.Line)
                        ?? Preference.FirstOrDefault(types.ContainsKey) ?? types.Keys.First();
            var meta = types[type];
            if (Regex.IsMatch(meta.First.Split('\n')[0], @"\bwrapped\s*$", RegexOptions.IgnoreCase))
            {
                sb.Append($"  {type} {owner}.{f.Unit}: the source is wrapped, line {f.Line} cannot be shown.\n\n");
                return;
            }

            // runtime line numbers of a trigger count from the start of its PL/SQL block, not from the CREATE line
            int line = f.Line;
            string triggerNote = "";
            if (type == "TRIGGER")
            {
                var start = ctx.Ide.Query(
                    $"select min(line) from all_source where owner = {Lit(owner)} and name = {Lit(f.Unit)} and type = 'TRIGGER' " +
                    "and regexp_like(text, '(^|[^a-z0-9_$#])(declare|begin|compound[[:space:]]+trigger)([^a-z0-9_$#]|$)', 'i')", 1);
                if (start.Rows.Count > 0 && int.TryParse(start.Rows[0][0], out var blockStart) && blockStart > 1)
                {
                    line = blockStart + f.Line - 1;
                    triggerNote = $" (line {f.Line} of the trigger body = source line {line})";
                }
            }

            var subprogram = f.Subprogram;
            string subprogramNote = "";
            if (subprogram == null && (type == "PACKAGE BODY" || type == "TYPE BODY"))
            {
                var decl = ctx.Ide.Query(
                    "select text from (select text from all_source " +
                    $"where owner = {Lit(owner)} and name = {Lit(f.Unit)} and type = {Lit(type)} and line <= {line} " +
                    "and regexp_like(text, '^[[:space:]]*(procedure|function|constructor[[:space:]]+function|member[[:space:]]+(procedure|function)|static[[:space:]]+(procedure|function))[[:space:]]', 'i') " +
                    "order by line desc) where rownum = 1", 1);
                if (decl.Rows.Count > 0)
                {
                    var dm = Regex.Match(decl.Rows[0][0] ?? "", @"(?:procedure|function)\s+(""[^""]+""|[\w$#]+)", RegexOptions.IgnoreCase);
                    if (dm.Success)
                    {
                        subprogram = Ident(dm.Groups[1].Value);
                        subprogramNote = " (nearest declaration above the line)";
                    }
                }
            }

            sb.Append($"  {type} {owner}.{f.Unit} line {line}{triggerNote}");
            if (subprogram != null) sb.Append($", in {subprogram}{subprogramNote}");
            if (!string.IsNullOrEmpty(meta.LastDdl)) sb.Append($", last compiled {meta.LastDdl}");
            sb.Append(":\n");
            if (line > meta.Lines)
            {
                sb.Append($"  the unit has only {meta.Lines} lines now: it was changed after the error.\n\n");
                return;
            }
            var src = ctx.Ide.Query(
                $"select line, text from all_source where owner = {Lit(owner)} and name = {Lit(f.Unit)} and type = {Lit(type)} " +
                $"and line between {Math.Max(1, line - context)} and {line + context} order by line", 2 * context + 1);
            foreach (var row in src.Rows)
            {
                var text = (row[1] ?? "").TrimEnd('\r', '\n');
                if (text.Length > 300) text = text.Substring(0, 300) + "…";
                sb.Append(row[0] == line.ToString() ? ">> " : "   ").Append(row[0].PadLeft(5)).Append(" | ").Append(text).Append('\n');
            }
            sb.Append('\n');
        }
    }
}
