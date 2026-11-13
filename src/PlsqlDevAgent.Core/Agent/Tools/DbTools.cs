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
    internal static class Db
    {
        public sealed class ObjRef
        {
            public string Owner, Name, Type, Status;
            public override string ToString() => $"{Type} {Owner}.{Name}{(Status == "INVALID" ? " (INVALID)" : "")}";
        }

        /// <summary>Finds objects by (owner.)name, optionally filtered by type; follows synonyms.</summary>
        public static List<ObjRef> Resolve(ToolContext ctx, string fullName, string type)
        {
            SplitName(fullName, out var owner, out var name);
            type = string.IsNullOrWhiteSpace(type) ? null : Regex.Replace(type.Trim().ToUpperInvariant(), @"[\s_]+", " ");
            var list = Find(ctx, owner, name, type);
            if (list.Count == 0 && owner == null)
            {
                var syn = ctx.Ide.Query(
                    "select table_owner, table_name from all_synonyms where synonym_name = " + Lit(name) +
                    " and owner in (" + Lit(ctx.CurrentSchema) + ", 'PUBLIC') and table_owner is not null" +
                    " order by decode(owner, 'PUBLIC', 1, 0)", 1);
                if (syn.Rows.Count > 0) list = Find(ctx, syn.Rows[0][0], syn.Rows[0][1], type);
            }
            return list;
        }

        private static List<ObjRef> Find(ToolContext ctx, string owner, string name, string type)
        {
            var sql = new StringBuilder("select owner, object_name, object_type, status from all_objects where object_name = ")
                .Append(Lit(name));
            if (owner != null) sql.Append(" and owner = ").Append(Lit(owner));
            if (type != null) sql.Append(" and object_type = ").Append(Lit(type));
            else sql.Append(" and object_type not in ('PACKAGE BODY', 'TYPE BODY', 'INDEX PARTITION', 'TABLE PARTITION', 'LOB')");
            sql.Append(" order by decode(owner, ").Append(Lit(ctx.CurrentSchema)).Append(", 0, 'PUBLIC', 2, 1), owner, object_type");
            return ctx.Ide.Query(sql.ToString(), 20).Rows
                .Select(r => new ObjRef { Owner = r[0], Name = r[1], Type = r[2], Status = r[3] })
                .ToList();
        }

        public static ObjRef ResolveOne(ToolContext ctx, string fullName, string type)
        {
            var list = Resolve(ctx, fullName, type);
            if (list.Count == 0)
                throw new ToolArgumentException($"No object '{fullName}'{(type == null ? "" : " of type " + type)} is visible to the current user. Use list_objects to search.");
            return list[0];
        }

        public static string Truncate(string text, int max) =>
            text == null || text.Length <= max ? text : text.Substring(0, max) + $"\n... [truncated, {text.Length - max} more characters]";

        /// <summary>Reads a CLOB valued expression in chunks (the plug-in SQL interface returns strings).</summary>
        public static string ReadClob(ToolContext ctx, string clobExpression)
        {
            var sql =
                "with d as (select " + clobExpression + " c from dual) " +
                "select dbms_lob.substr(c, 1000, (level - 1) * 1000 + 1) from d " +
                "connect by level <= ceil(nvl(dbms_lob.getlength(c), 0) / 1000)";
            var r = ctx.Ide.Query(sql, 100000);
            return string.Concat(r.Rows.Select(row => row[0]));
        }

        private static readonly HashSet<string> PlsqlSourceTypes = new HashSet<string>
        {
            "PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "TYPE BODY", "JAVA SOURCE",
        };

        /// <summary>Current CREATE OR REPLACE source of a PL/SQL unit or view, or null if it does not exist.</summary>
        public static string CurrentSource(ToolContext ctx, string type, string owner, string name)
        {
            type = (type ?? "").ToUpperInvariant();
            if (PlsqlSourceTypes.Contains(type))
            {
                var r = ctx.Ide.Query(
                    $"select text from all_source where owner = {Lit(owner)} and name = {Lit(name)} and type = {Lit(type)} order by line", 200000);
                if (r.Rows.Count == 0) return null;
                return "CREATE OR REPLACE " + string.Concat(r.Rows.Select(x => x[0])).TrimEnd('\n', '\r', ' ');
            }
            if (type == "VIEW")
            {
                var exists = ctx.Ide.Query($"select 1 from all_views where owner = {Lit(owner)} and view_name = {Lit(name)}", 1);
                if (exists.Rows.Count == 0) return null;
                return ReadClob(ctx, $"dbms_metadata.get_ddl('VIEW', {Lit(name)}, {Lit(owner)})").Trim();
            }
            return null;
        }

        /// <summary>State of a PL/SQL unit or view before a statement replaces or drops it.</summary>
        public sealed class PendingChange
        {
            public string Type, Owner, Name;
            /// <summary>Source before (null = did not exist); only valid if <see cref="Known"/>.</summary>
            public string Previous;
            public bool Known;
            public BackupEntry Backup;
        }

        /// <summary>
        /// Before the agent replaces or drops a PL/SQL unit or view: remembers its current source (for rewinding) and
        /// saves a backup (setting; skipped if unchanged). Never throws except on cancellation.
        /// </summary>
        public static PendingChange BeforeChange(ToolContext ctx, string type, string owner, string name, string reason, string newSource)
        {
            var p = new PendingChange { Type = type.ToUpperInvariant(), Owner = owner, Name = name };
            try
            {
                p.Previous = CurrentSource(ctx, p.Type, owner, name);
                p.Known = true;
                if (p.Previous != null && ctx.Config.BackupBeforeChanges && !(newSource != null && NormalizeSource(p.Previous) == NormalizeSource(newSource)))
                {
                    p.Backup = BackupStore.Save(ctx.ConnectionLabel, p.Type, owner, name, p.Previous, reason);
                    ctx.AddUiMeta("backups", new JObject { ["id"] = p.Backup.Id, ["label"] = p.Backup.Label });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Util.Log.Warn($"Reading / backing up {type} {owner}.{name} failed: {ex.Message}");
            }
            return p;
        }

        /// <summary>After the statement ran: records the change for rewinding (if the source really changed).</summary>
        public static void AfterChange(ToolContext ctx, PendingChange p)
        {
            if (ctx.RecordChange == null) return;
            string after = null;
            bool afterKnown = false;
            try { after = CurrentSource(ctx, p.Type, p.Owner, p.Name); afterKnown = true; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Util.Log.Warn($"Reading {p.Type} {p.Owner}.{p.Name} after the change failed: {ex.Message}"); }

            var label = $"{p.Type} {p.Owner}.{p.Name}";
            if (!p.Known || !afterKnown)
            {
                ctx.Changed(new ChangeRecord { Kind = ChangeRecord.NotUndoable, Label = label + " (the previous version could not be read)" });
                return;
            }
            bool unchanged = p.Previous == null ? after == null : after != null && NormalizeSource(p.Previous) == NormalizeSource(after);
            if (unchanged) return;
            ctx.Changed(new ChangeRecord
            {
                Kind = ChangeRecord.Database, Label = label, Before = p.Previous, After = after, BackupId = p.Backup?.Id,
                Connection = ctx.ConnectionLabel, ObjectType = p.Type, Owner = p.Owner, Name = p.Name,
            });
        }

        public static string NormalizeSource(string s)
        {
            var text = string.Join("\n", (s ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim().TrimEnd('/').Trim();
            return Regex.Replace(text, @"^create\s+(or\s+replace\s+)?((non)?editionable\s+)?", "", RegexOptions.IgnoreCase).ToUpperInvariant();
        }

        public static string Errors(ToolContext ctx, string owner, string name, string type)
        {
            var sql = "select type, line, position, attribute, asciistr(text) from all_errors where owner = " + Lit(owner) +
                      " and name = " + Lit(name) + (type != null ? " and type = " + Lit(type) : "") +
                      " order by type, sequence";
            var r = ctx.Ide.Query(sql, 200);
            if (r.Rows.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (var row in r.Rows)
                sb.Append($"{row[0]} line {row[1]}, col {row[2]} {row[3]}: {UnAsciiStr(row[4]).Trim()}\n");
            return sb.ToString();
        }
    }

    internal sealed class RunQueryTool : AgentTool
    {
        public override string Name => "run_query";
        public override string Description =>
            "Runs a read-only SELECT (or WITH) query in the agent's database session and returns the rows as text. " +
            "Use it to inspect data and the data dictionary (ALL_TABLES, ALL_TAB_COLUMNS, ALL_SOURCE, ALL_DEPENDENCIES, " +
            "V$ views…). Always restrict the result (WHERE, FETCH FIRST n ROWS ONLY).";
        public override JObject Parameters => Schema(
            Req("sql", "string", "A single SELECT statement, without trailing semicolon."),
            Opt("max_rows", "integer", "Maximum rows to return (default 50)."));
        public override string Summarize(JObject args) => OneLine(Str(args, "sql"));

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var sql = PrepareStatement(ReqStr(args, "sql"));
            if (!IsReadOnlyQuery(sql, out var reason)) return Task.FromResult("Not executed: " + reason);
            int max = Math.Max(1, Math.Min(Int(args, "max_rows", 50), ctx.Config.MaxQueryRows));
            try
            {
                var r = ctx.Ide.Query(sql, max);
                return Task.FromResult(FormatResult(r, ctx.Config.MaxToolOutputChars));
            }
            catch (OracleSqlException ex)
            {
                return Task.FromResult("Query failed: " + ex.Message);
            }
        }

        internal static string OneLine(string s)
        {
            s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            return s.Length > 120 ? s.Substring(0, 117) + "..." : s;
        }
    }

    internal sealed class ListObjectsTool : AgentTool
    {
        public override string Name => "list_objects";
        public override string Description =>
            "Lists database objects visible to the current user (ALL_OBJECTS). Supports LIKE patterns (% and _).";
        public override JObject Parameters => Schema(
            Opt("name_like", "string", "Object name pattern, e.g. 'EMP%' or '%INVOICE%'. Case-insensitive."),
            Opt("object_type", "string", "Filter by type, e.g. TABLE, VIEW, PACKAGE, PROCEDURE, FUNCTION, TRIGGER, TYPE, SEQUENCE."),
            Opt("owner", "string", "Schema; default is the current schema. Use '*' for all schemas."),
            Opt("only_invalid", "boolean", "Only objects with status INVALID."),
            Opt("max_rows", "integer", "Default 100."));
        public override string Summarize(JObject args) =>
            $"{Str(args, "object_type", "")} {Str(args, "name_like", "")} {(Str(args, "owner") is string o ? "in " + o : "")}".Trim();

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var owner = Str(args, "owner");
            var sql = new StringBuilder(
                "select owner, object_name, object_type, status, to_char(last_ddl_time, 'YYYY-MM-DD HH24:MI') last_ddl " +
                "from all_objects where object_type not in ('INDEX PARTITION','TABLE PARTITION','LOB','TABLE SUBPARTITION','INDEX SUBPARTITION')");
            if (owner != "*") sql.Append(" and owner = ").Append(Lit(owner == null ? ctx.CurrentSchema : Ident(owner)));
            else sql.Append(" and owner not in ('SYS','SYSTEM','XDB','MDSYS','CTXSYS','ORDSYS','WMSYS','OLAPSYS','LBACSYS','DVSYS','GSMADMIN_INTERNAL','APEX_PUBLIC_USER','OUTLN','DBSNMP','APPQOSSYS','ORDDATA','AUDSYS','OJVMSYS')");
            var like = Str(args, "name_like");
            if (!string.IsNullOrWhiteSpace(like)) sql.Append(" and object_name like ").Append(Lit(like.Trim().ToUpperInvariant()));
            var type = Str(args, "object_type");
            if (!string.IsNullOrWhiteSpace(type)) sql.Append(" and object_type = ").Append(Lit(Regex.Replace(type.Trim().ToUpperInvariant(), @"[\s_]+", " ")));
            if (Bool(args, "only_invalid", false)) sql.Append(" and status = 'INVALID'");
            sql.Append(" order by owner, object_type, object_name");
            var r = ctx.Ide.Query(sql.ToString(), Math.Min(Int(args, "max_rows", 100), 500));
            return Task.FromResult(r.Rows.Count == 0 ? "No matching objects." : FormatResult(r, ctx.Config.MaxToolOutputChars));
        }
    }

    internal sealed class DescribeObjectTool : AgentTool
    {
        public override string Name => "describe_object";
        public override string Description =>
            "Describes a table or view (columns with types, nullability, comments, primary/unique/foreign keys, indexes) " +
            "or the subprograms and parameters of a package, procedure, function or type. Use this before writing SQL " +
            "against a table or calling a package.";
        public override JObject Parameters => Schema(
            Req("name", "string", "Object name, optionally OWNER.NAME."),
            Opt("object_type", "string", "Optional type to disambiguate."));
        public override string Summarize(JObject args) => Str(args, "name");

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var obj = Db.ResolveOne(ctx, ReqStr(args, "name"), Str(args, "object_type"));
            var sb = new StringBuilder($"{obj}\n");
            switch (obj.Type)
            {
                case "TABLE":
                case "VIEW":
                case "MATERIALIZED VIEW":
                    DescribeTable(ctx, obj, sb);
                    break;
                case "PACKAGE":
                case "PROCEDURE":
                case "FUNCTION":
                case "TYPE":
                    DescribeProgram(ctx, obj, sb);
                    break;
                case "SEQUENCE":
                    sb.Append(FormatResult(ctx.Ide.Query(
                        "select min_value, max_value, increment_by, cache_size, cycle_flag, order_flag, last_number from all_sequences " +
                        $"where sequence_owner = {Lit(obj.Owner)} and sequence_name = {Lit(obj.Name)}", 1), 4000));
                    break;
                default:
                    sb.Append("Use get_object_source to see the definition of this object.");
                    break;
            }
            return Task.FromResult(Db.Truncate(sb.ToString(), ctx.Config.MaxToolOutputChars));
        }

        private static void DescribeTable(ToolContext ctx, Db.ObjRef obj, StringBuilder sb)
        {
            string o = Lit(obj.Owner), n = Lit(obj.Name);
            var comment = ctx.Ide.Query($"select comments from all_tab_comments where owner = {o} and table_name = {n}", 1);
            if (comment.Rows.Count > 0 && !string.IsNullOrWhiteSpace(comment.Rows[0][0])) sb.Append($"Comment: {comment.Rows[0][0]}\n");

            var cols = ctx.Ide.Query(
                "select c.column_name, c.data_type || case " +
                "  when c.data_type in ('VARCHAR2','NVARCHAR2','CHAR','NCHAR') then '(' || c.char_length || decode(c.char_used, 'C', ' CHAR', '') || ')' " +
                "  when c.data_type = 'NUMBER' and c.data_precision is not null then '(' || c.data_precision || nullif(',' || c.data_scale, ',0') || ')' " +
                "  when c.data_type = 'RAW' then '(' || c.data_length || ')' end data_type, " +
                "decode(c.nullable, 'N', 'NOT NULL', '') nullable, cc.comments " +
                $"from all_tab_columns c left join all_col_comments cc on cc.owner = c.owner and cc.table_name = c.table_name and cc.column_name = c.column_name " +
                $"where c.owner = {o} and c.table_name = {n} order by c.column_id", 1000);
            sb.Append("\nColumns:\n").Append(FormatResult(cols, 30000)).Append('\n');

            if (obj.Type == "VIEW") return;
            try
            {
                var cons = ctx.Ide.Query(
                    "select c.constraint_name, decode(c.constraint_type, 'P', 'PRIMARY KEY', 'U', 'UNIQUE', 'R', 'FOREIGN KEY', 'C', 'CHECK') type, " +
                    "(select listagg(cc.column_name, ', ') within group (order by cc.position) from all_cons_columns cc " +
                    "  where cc.owner = c.owner and cc.constraint_name = c.constraint_name) columns, " +
                    "(select r.table_name from all_constraints r where r.owner = c.r_owner and r.constraint_name = c.r_constraint_name) references_table, " +
                    "case when c.constraint_type = 'C' then c.search_condition_vc end condition " +
                    $"from all_constraints c where c.owner = {o} and c.table_name = {n} " +
                    "and (c.constraint_type in ('P','U','R') or (c.constraint_type = 'C' and c.generated = 'USER NAME')) order by c.constraint_type, c.constraint_name", 200);
                if (cons.Rows.Count > 0) sb.Append("\nConstraints:\n").Append(FormatResult(cons, 20000)).Append('\n');
            }
            catch (OracleSqlException)
            {
                // search_condition_vc does not exist before 12c
                var cons = ctx.Ide.Query(
                    "select c.constraint_name, decode(c.constraint_type, 'P', 'PRIMARY KEY', 'U', 'UNIQUE', 'R', 'FOREIGN KEY') type, " +
                    "(select listagg(cc.column_name, ', ') within group (order by cc.position) from all_cons_columns cc " +
                    "  where cc.owner = c.owner and cc.constraint_name = c.constraint_name) columns, " +
                    "(select r.table_name from all_constraints r where r.owner = c.r_owner and r.constraint_name = c.r_constraint_name) references_table " +
                    $"from all_constraints c where c.owner = {o} and c.table_name = {n} and c.constraint_type in ('P','U','R') order by c.constraint_type", 200);
                if (cons.Rows.Count > 0) sb.Append("\nConstraints:\n").Append(FormatResult(cons, 20000)).Append('\n');
            }

            var idx = ctx.Ide.Query(
                "select i.index_name, i.uniqueness, (select listagg(ic.column_name, ', ') within group (order by ic.column_position) " +
                "from all_ind_columns ic where ic.index_owner = i.owner and ic.index_name = i.index_name) columns " +
                $"from all_indexes i where i.table_owner = {o} and i.table_name = {n} order by i.index_name", 100);
            if (idx.Rows.Count > 0) sb.Append("\nIndexes:\n").Append(FormatResult(idx, 10000)).Append('\n');
        }

        private static void DescribeProgram(ToolContext ctx, Db.ObjRef obj, StringBuilder sb)
        {
            string o = Lit(obj.Owner), n = Lit(obj.Name);
            var where = obj.Type == "PACKAGE" || obj.Type == "TYPE"
                ? $"owner = {o} and package_name = {n}"
                : $"owner = {o} and object_name = {n} and package_name is null";
            var args = ctx.Ide.Query(
                "select object_name, overload, position, argument_name, in_out, data_type, type_name, defaulted " +
                $"from all_arguments where {where} and data_level = 0 order by object_name, overload, sequence", 2000);
            if (args.Rows.Count == 0)
            {
                sb.Append("No subprogram arguments found. Use get_object_source for the full definition.");
                return;
            }
            // Columns: 0 object_name, 1 overload, 2 position, 3 argument_name, 4 in_out, 5 data_type, 6 type_name, 7 defaulted
            foreach (var group in args.Rows.GroupBy(r => r[0] + "#" + r[1]))
            {
                var first = group.First();
                var parameters = group
                    .Where(r => r[2] != "0" && !string.IsNullOrEmpty(r[3]))
                    .Select(r => $"{r[3]} {r[4]} {(string.IsNullOrEmpty(r[6]) ? r[5] : r[6])}{(r[7] == "Y" ? " DEFAULT …" : "")}");
                var ret = group.FirstOrDefault(r => r[2] == "0");
                sb.Append(ret != null ? "FUNCTION " : "PROCEDURE ").Append(first[0]);
                if (!string.IsNullOrEmpty(first[1])) sb.Append($" /* overload {first[1]} */");
                var list = string.Join(", ", parameters);
                if (list.Length > 0) sb.Append('(').Append(list).Append(')');
                if (ret != null) sb.Append(" RETURN ").Append(string.IsNullOrEmpty(ret[6]) ? ret[5] : ret[6]);
                sb.Append('\n');
            }
        }
    }

    internal sealed class GetObjectSourceTool : AgentTool
    {
        public override string Name => "get_object_source";
        public override string Description =>
            "Returns the source / DDL of a database object as stored in the database: PL/SQL units (for a package the " +
            "spec and body), views, triggers, types, and DDL for tables, indexes, sequences etc.";
        public override JObject Parameters => Schema(
            Req("name", "string", "Object name, optionally OWNER.NAME."),
            Opt("object_type", "string", "e.g. PACKAGE, PACKAGE BODY, PROCEDURE, FUNCTION, TRIGGER, TYPE, VIEW, TABLE. For PACKAGE both spec and body are returned."),
            Opt("with_line_numbers", "boolean", "Prefix lines with line numbers (useful together with compile errors)."));
        public override string Summarize(JObject args) => $"{Str(args, "object_type", "")} {Str(args, "name")}".Trim();

        private static readonly HashSet<string> SourceTypes = new HashSet<string>
        {
            "PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "TYPE BODY", "JAVA SOURCE",
        };

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var obj = Db.ResolveOne(ctx, ReqStr(args, "name"), Str(args, "object_type"));
            bool numbers = Bool(args, "with_line_numbers", false);
            var sb = new StringBuilder();

            if (SourceTypes.Contains(obj.Type))
            {
                AppendSource(ctx, obj.Owner, obj.Name, obj.Type, numbers, sb);
                if (obj.Type == "PACKAGE" || obj.Type == "TYPE")
                {
                    var bodyType = obj.Type + " BODY";
                    if (Db.Resolve(ctx, obj.Owner + "." + obj.Name, bodyType).Count > 0)
                    {
                        sb.Append("\n/\n\n");
                        AppendSource(ctx, obj.Owner, obj.Name, bodyType, numbers, sb);
                    }
                }
            }
            else
            {
                string ddl = "";
                if (obj.Type == "VIEW") ddl = ctx.Ide.GetObjectSource(obj.Type, obj.Owner, obj.Name);
                if (string.IsNullOrWhiteSpace(ddl))
                {
                    try
                    {
                        ddl = Db.ReadClob(ctx, $"dbms_metadata.get_ddl({Lit(obj.Type.Replace(' ', '_'))}, {Lit(obj.Name)}, {Lit(obj.Owner)})");
                    }
                    catch (OracleSqlException ex)
                    {
                        ddl = $"(DBMS_METADATA failed: {ex.Message}. Use describe_object instead.)";
                    }
                }
                sb.Append(numbers ? TextEdit.WithLineNumbers(ddl, 0, 0) : ddl);
            }
            return Task.FromResult(Db.Truncate($"-- {obj}\n" + sb, ctx.Config.MaxToolOutputChars));
        }

        private static void AppendSource(ToolContext ctx, string owner, string name, string type, bool numbers, StringBuilder sb)
        {
            var r = ctx.Ide.Query(
                $"select text from all_source where owner = {Lit(owner)} and name = {Lit(name)} and type = {Lit(type)} order by line", 200000);
            if (r.Rows.Count == 0)
            {
                sb.Append($"-- (no source visible for {type} {owner}.{name})\n");
                return;
            }
            var text = string.Concat(r.Rows.Select(x => x[0]));
            text = "CREATE OR REPLACE " + text.TrimEnd('\n', '\r', ' ');
            sb.Append(numbers ? TextEdit.WithLineNumbers(text, 0, 0) : text).Append('\n');
        }
    }

    internal sealed class SearchSourceTool : AgentTool
    {
        public override string Name => "search_source";
        public override string Description =>
            "Searches PL/SQL source code in the database (ALL_SOURCE) for a text, case-insensitive. Returns matching " +
            "lines with object name and line number. Good for finding usages of a table, column or procedure.";
        public override JObject Parameters => Schema(
            Req("text", "string", "Text to search for (plain text, not a pattern)."),
            Opt("owner", "string", "Schema to search; default the current schema; '*' for all accessible schemas."),
            Opt("object_type", "string", "Restrict to PACKAGE BODY, PROCEDURE, TRIGGER, …"),
            Opt("max_rows", "integer", "Default 100."));
        public override string Summarize(JObject args) => "\"" + Str(args, "text") + "\"";

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var text = ReqStr(args, "text").ToUpperInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var owner = Str(args, "owner");
            var sql = new StringBuilder("select owner, name, type, line, trim(text) text from all_source where upper(text) like ")
                .Append(Lit("%" + text + "%")).Append(" escape '\\'");
            if (owner != "*") sql.Append(" and owner = ").Append(Lit(owner == null ? ctx.CurrentSchema : Ident(owner)));
            else sql.Append(" and owner not in ('SYS','SYSTEM','XDB','MDSYS','CTXSYS','ORDSYS','WMSYS','OLAPSYS','LBACSYS','DVSYS','GSMADMIN_INTERNAL','APEX_040000','APEX_050000')");
            var type = Str(args, "object_type");
            if (!string.IsNullOrWhiteSpace(type)) sql.Append(" and type = ").Append(Lit(type.Trim().ToUpperInvariant()));
            sql.Append(" order by owner, name, type, line");
            var r = ctx.Ide.Query(sql.ToString(), Math.Min(Int(args, "max_rows", 100), 500));
            if (r.Rows.Count == 0) return Task.FromResult("No matches.");
            var sb = new StringBuilder();
            foreach (var row in r.Rows)
            {
                var line = (row[4] ?? "").TrimEnd('\r', '\n');
                if (line.Length > 400) line = line.Substring(0, 400) + "…";
                sb.Append($"{row[0]}.{row[1]} ({row[2]}) line {row[3]}: {line}\n");
            }
            if (r.Truncated) sb.Append("... more matches exist; narrow the search (owner, object_type) or raise max_rows.\n");
            return Task.FromResult(Db.Truncate(sb.ToString(), ctx.Config.MaxToolOutputChars));
        }
    }

    internal sealed class GetCompileErrorsTool : AgentTool
    {
        public override string Name => "get_compile_errors";
        public override string Description =>
            "Returns compilation errors and warnings (ALL_ERRORS) for an object, or for all invalid objects of the " +
            "current schema when no name is given.";
        public override JObject Parameters => Schema(
            Opt("name", "string", "Object name, optionally OWNER.NAME. Omit to list all errors in the current schema."),
            Opt("object_type", "string", "e.g. PACKAGE BODY."));
        public override string Summarize(JObject args) => Str(args, "name", "current schema");

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var name = Str(args, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                var r = ctx.Ide.Query(
                    "select name, type, line, position, attribute, asciistr(text) text from all_errors where owner = " + Lit(ctx.CurrentSchema) +
                    " order by name, type, sequence", 300);
                foreach (var row in r.Rows) row[5] = UnAsciiStr(row[5]);
                return Task.FromResult(r.Rows.Count == 0 ? "No compilation errors in schema " + ctx.CurrentSchema + "." : FormatResult(r, ctx.Config.MaxToolOutputChars, 500));
            }
            SplitName(name, out var owner, out var n);
            var type = Str(args, "object_type");
            var errors = Db.Errors(ctx, owner ?? ctx.CurrentSchema, n, string.IsNullOrWhiteSpace(type) ? null : type.Trim().ToUpperInvariant());
            return Task.FromResult(errors ?? "No compilation errors.");
        }
    }

    internal sealed class CompilePlsqlTool : AgentTool
    {
        public override string Name => "compile_plsql";
        public override string Description =>
            "Compiles PL/SQL into the database by executing CREATE OR REPLACE statements (package spec/body, procedure, " +
            "function, trigger, type, view). Several units may be given separated by a line containing only '/'. " +
            "Returns compilation errors with line numbers. The user must approve. Typical use: compile the code you " +
            "wrote in the editor (pass the full editor text) and fix errors until it compiles cleanly.";
        public override JObject Parameters => Schema(
            Req("source", "string", "Complete CREATE OR REPLACE source of one or more units ('/' line between units)."));
        public override bool Mutating => true;

        public override string Summarize(JObject args)
        {
            var parts = SplitOnSlash(Str(args, "source", ""));
            return string.Join(", ", parts.Select(p => IsPlsqlUnit(p, out var t, out var o, out var n) ? $"{t} {n}" : RunQueryTool.OneLine(p)).Take(4));
        }

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var source = ReqStr(args, "source");
            var parts = SplitOnSlash(source);
            if (parts.Count == 0) return "Nothing to compile.";
            foreach (var p in parts)
            {
                if (!IsPlsqlUnit(p, out _, out _, out _) && !Regex.IsMatch(StripLeadingComments(p), @"^CREATE\s+(OR\s+REPLACE\s+)?(FORCE\s+)?(EDITIONABLE\s+)?VIEW\b", RegexOptions.IgnoreCase))
                    return "Not executed: compile_plsql only accepts CREATE [OR REPLACE] PACKAGE/PACKAGE BODY/PROCEDURE/FUNCTION/TRIGGER/TYPE/VIEW statements. " +
                           "Use execute_sql for other statements. Offending part starts with: " + RunQueryTool.OneLine(p);
            }

            if (!ctx.Config.AutoApproveSql)
            {
                var approval = await ctx.ApproveAsync(new ApprovalRequest
                {
                    Category = "sql",
                    Title = L.T("Compile into database ({0}): {1}", ctx.Ide.GetContext().ConnectionLabel, Summarize(args)),
                    Detail = source.Replace("\r\n", "\n"),
                });
                if (!approval.Approved) return approval.RejectionMessage;
            }

            return Compile(ctx, parts, "compile");
        }

        /// <summary>Executes CREATE OR REPLACE statements (backing up the previous versions) and reports errors.</summary>
        internal static string Compile(ToolContext ctx, List<string> parts, string reason)
        {
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                var stmt = PrepareStatement(part);
                IsPlsqlUnit(stmt, out var type, out var owner, out var name);
                if (type == null)
                {
                    var vm = Regex.Match(StripLeadingComments(stmt), @"VIEW\s+(?:(?<o>""[^""]+""|[\w$#]+)\s*\.\s*)?(?<n>""[^""]+""|[\w$#]+)", RegexOptions.IgnoreCase);
                    type = "VIEW";
                    owner = vm.Groups["o"].Success ? Ident(vm.Groups["o"].Value) : null;
                    name = vm.Success ? Ident(vm.Groups["n"].Value) : "?";
                }
                owner = owner ?? ctx.CurrentSchema;
                var pending = Db.BeforeChange(ctx, type, owner, name, reason, stmt);
                var res = ctx.Ide.Execute(stmt);
                var backupNote = pending.Backup == null ? "" : $" (previous version backed up as {pending.Backup.Id})";
                if (res.Ok || res.CompiledWithErrors)
                {
                    Db.AfterChange(ctx, pending);
                    var errors = type == "VIEW" ? null : Db.Errors(ctx, owner, name, type);
                    ctx.Ide.RefreshObject(type, owner, name, 2);
                    sb.Append(errors == null
                        ? $"{type} {owner}.{name}: compiled successfully{backupNote}.\n"
                        : $"{type} {owner}.{name}: COMPILED WITH ERRORS{backupNote} (line numbers are relative to the CREATE statement):\n{errors}\n");
                }
                else
                {
                    sb.Append($"{type} {owner}.{name}: FAILED: {res.ErrorMessage}\n");
                }
            }
            return sb.ToString().TrimEnd();
        }
    }

    internal sealed class ExecuteSqlTool : AgentTool
    {
        public override string Name => "execute_sql";
        public override string Description =>
            "Executes ONE SQL statement or anonymous PL/SQL block (DML, DDL, BEGIN…END) in the agent's database session. " +
            "The user must approve it. DBMS_OUTPUT is returned. DML is not committed unless you execute COMMIT (only " +
            "do that if the user asked). For CREATE OR REPLACE of PL/SQL units prefer compile_plsql.";
        public override JObject Parameters => Schema(
            Req("sql", "string", "The statement or PL/SQL block."),
            Opt("purpose", "string", "Short explanation shown to the user in the approval dialog."));
        public override bool Mutating => true;
        public override string Summarize(JObject args) => RunQueryTool.OneLine(Str(args, "sql"));

        private static readonly Regex DropRegex = new Regex(
            @"^DROP\s+(?<t>PACKAGE\s+BODY|PACKAGE|PROCEDURE|FUNCTION|TRIGGER|TYPE\s+BODY|TYPE|VIEW)\s+(?:(?<o>""[^""]+""|[\w$#]+)\s*\.\s*)?(?<n>""[^""]+""|[\w$#]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ViewRegex = new Regex(
            @"^CREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:NO\s+)?FORCE\s+)?(?:(?:NON)?EDITIONABLE\s+)?VIEW\s+(?:(?<o>""[^""]+""|[\w$#]+)\s*\.\s*)?(?<n>""[^""]+""|[\w$#]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Destructive = new Regex(
            @"^\s*(DROP|TRUNCATE|DELETE|ALTER\s+\w+\s+\S+\s+DROP|PURGE|FLASHBACK)\b|\bDELETE\s+FROM\b|\bDROP\s+(TABLE|USER|TABLESPACE|PACKAGE|PROCEDURE|FUNCTION|VIEW|INDEX|SEQUENCE|TYPE|TRIGGER|SYNONYM)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var sql = PrepareStatement(ReqStr(args, "sql"));
            if (sql.Length == 0) return "Nothing to execute.";
            var kw = FirstKeyword(sql);
            bool isQuery = kw == "SELECT" || kw == "WITH";
            bool destructive = Destructive.IsMatch(StripLeadingComments(sql));

            if (!isQuery && (!ctx.Config.AutoApproveSql || destructive))
            {
                var purpose = Str(args, "purpose");
                var approval = await ctx.ApproveAsync(new ApprovalRequest
                {
                    Category = destructive ? "sql-destructive" : "sql",
                    Title = L.T("Execute in database ({0})", ctx.Ide.GetContext().ConnectionLabel) + (string.IsNullOrWhiteSpace(purpose) ? "" : ": " + purpose),
                    Detail = sql.Replace("\r\n", "\n"),
                    Warning = destructive ? L.T("This statement drops, truncates or deletes data/objects.") : null,
                });
                if (!approval.Approved) return approval.RejectionMessage;
            }

            if (isQuery)
            {
                var r = ctx.Ide.Query(sql, Math.Min(50, ctx.Config.MaxQueryRows));
                return FormatResult(r, ctx.Config.MaxToolOutputChars);
            }

            // keep the current source of PL/SQL units / views that are about to be replaced or dropped (backup + rewind)
            var pending = new List<Db.PendingChange>();
            var stripped = StripLeadingComments(sql);
            if (IsPlsqlUnit(sql, out var bt, out var bo, out var bn)) pending.Add(Db.BeforeChange(ctx, bt, bo ?? ctx.CurrentSchema, bn, "execute_sql", sql));
            else if (ViewRegex.Match(stripped) is Match vm && vm.Success)
                pending.Add(Db.BeforeChange(ctx, "VIEW", vm.Groups["o"].Success ? Ident(vm.Groups["o"].Value) : ctx.CurrentSchema, Ident(vm.Groups["n"].Value), "execute_sql", sql));
            else if (DropRegex.Match(stripped) is Match dm && dm.Success)
            {
                var dtype = Regex.Replace(dm.Groups["t"].Value.ToUpperInvariant(), @"\s+", " ");
                var downer = dm.Groups["o"].Success ? Ident(dm.Groups["o"].Value) : ctx.CurrentSchema;
                // the body first: rewinding goes backwards and must create the spec before the body
                if (dtype == "PACKAGE") pending.Add(Db.BeforeChange(ctx, "PACKAGE BODY", downer, Ident(dm.Groups["n"].Value), "drop", null));
                if (dtype == "TYPE") pending.Add(Db.BeforeChange(ctx, "TYPE BODY", downer, Ident(dm.Groups["n"].Value), "drop", null));
                pending.Add(Db.BeforeChange(ctx, dtype, downer, Ident(dm.Groups["n"].Value), "drop", null));
            }

            var res = ctx.Ide.Execute(sql);
            if (res.Ok || res.CompiledWithErrors)
            {
                foreach (var p in pending) Db.AfterChange(ctx, p);
                if (pending.Count == 0) ctx.Changed(new ChangeRecord { Kind = ChangeRecord.NotUndoable, Label = RunQueryTool.OneLine(sql) });
            }
            var backup = pending.Select(p => p.Backup).FirstOrDefault(b => b != null);
            var sb = new StringBuilder();
            if (backup != null) sb.Append($"(Previous version backed up as {backup.Id}.) ");
            if (res.Ok) sb.Append("Executed successfully.");
            else if (res.CompiledWithErrors) sb.Append("Executed, but the object has compilation errors (use get_compile_errors).");
            else sb.Append("FAILED: ").Append(res.ErrorMessage);
            if (!string.IsNullOrWhiteSpace(res.DbmsOutput)) sb.Append("\nDBMS_OUTPUT:\n").Append(Db.Truncate(res.DbmsOutput, ctx.Config.MaxToolOutputChars));
            if (res.Ok && IsDdl(sql))
            {
                if (IsPlsqlUnit(sql, out var t, out var o, out var n)) ctx.Ide.RefreshObject(t, o ?? ctx.CurrentSchema, n, 2);
            }
            if (res.Ok && (kw == "INSERT" || kw == "UPDATE" || kw == "DELETE" || kw == "MERGE"))
                sb.Append("\nNote: not committed yet.");
            return sb.ToString();
        }
    }
}
