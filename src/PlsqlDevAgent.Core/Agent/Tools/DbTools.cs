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

        /// <summary>Schemas of Oracle itself (left out of searches over all schemas, described by name only in dossiers).</summary>
        public static readonly HashSet<string> SystemOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SYS", "SYSTEM", "XDB", "MDSYS", "CTXSYS", "ORDSYS", "WMSYS", "OLAPSYS", "LBACSYS", "DVSYS", "GSMADMIN_INTERNAL",
            "APEX_PUBLIC_USER", "APEX_040000", "APEX_050000", "OUTLN", "DBSNMP", "APPQOSSYS", "ORDDATA", "AUDSYS", "OJVMSYS",
        };

        /// <summary><see cref="SystemOwners"/> as an SQL list of literals.</summary>
        public static string SystemOwnersSql => string.Join(",", SystemOwners.OrderBy(o => o, StringComparer.Ordinal).Select(o => "'" + o + "'"));

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

        /// <summary>Lines of one PL/SQL unit as read by <see cref="ReadSource"/>.</summary>
        public sealed class SourceText
        {
            /// <summary>The lines as stored (each ends with a line feed), joined.</summary>
            public string Text = "";
            /// <summary>First and last line read (0 if none), and the number of lines of the whole unit (0 if unknown).</summary>
            public int FirstLine, LastLine, TotalLines;
            /// <summary>All requested lines were read (false: stopped at <c>maxChars</c>).</summary>
            public bool Complete = true;
        }

        /// <summary>Bytes per row when <see cref="ReadSource"/> joins source lines on the server (VARCHAR2 limit 4,000).</summary>
        private const int SourceChunkBytes = 3000;

        /// <summary>
        /// Reads lines <paramref name="fromLine"/>..<paramref name="toLine"/> (0 = to the end) of a PL/SQL unit. The
        /// lines are joined into rows of about 3,000 bytes on the server: PL/SQL Developer fetches only about 25 rows
        /// per network round trip, so a 3,000-line body needs 3 round trips instead of about 120. With
        /// <paramref name="maxChars"/> &gt; 0 it stops after at least that many characters (the rest is not transferred).
        /// Falls back to one row per line if a row would exceed the VARCHAR2 limit (ORA-01489, very long lines) or the
        /// database has no LISTAGG.
        /// </summary>
        public static SourceText ReadSource(ToolContext ctx, string owner, string name, string type, int fromLine = 1, int toLine = 0, int maxChars = 0)
        {
            fromLine = Math.Max(1, fromLine);
            string unit = $"select line, text, count(*) over () total from all_source where owner = {Lit(owner)} and name = {Lit(name)} and type = {Lit(type)}";
            string range = $"line >= {fromLine}" + (toLine > 0 ? $" and line <= {toLine}" : "");
            var result = new SourceText();
            var sb = new StringBuilder();
            QueryResult r;
            try
            {
                r = ctx.Ide.Query(
                    "select min(line), max(line), max(total), listagg(text) within group (order by line) from (" +
                    $"select line, text, total, trunc(sum(lengthb(text)) over (order by line) / {SourceChunkBytes}) bucket " +
                    $"from ({unit}) where {range}) group by bucket order by bucket",
                    maxChars > 0 ? maxChars / 1000 + 2 : 100000);
            }
            catch (OracleSqlException ex)
            {
                Log.Info($"Joined source read failed ({ex.Message.Split('\n')[0]}), reading {type} {owner}.{name} line by line");
                r = ctx.Ide.Query($"select line, line last_line, total, text from ({unit}) where {range} order by line", maxChars > 0 ? maxChars / 10 + 1000 : 200000);
            }
            foreach (var row in r.Rows)
            {
                if (result.FirstLine == 0) result.FirstLine = int.Parse(row[0]);
                result.LastLine = int.Parse(row[1]);
                result.TotalLines = int.Parse(row[2]);
                sb.Append(row[3]);
                if (maxChars > 0 && sb.Length >= maxChars) break;
            }
            result.Text = sb.ToString();
            result.Complete = !r.Truncated && (maxChars <= 0 || result.LastLine >= (toLine > 0 ? Math.Min(toLine, result.TotalLines) : result.TotalLines));
            if (r.Rows.Count == 0 && fromLine > 1)
            {
                // no line in the range: how long is the unit?
                var count = ctx.Ide.Query($"select count(*) from all_source where owner = {Lit(owner)} and name = {Lit(name)} and type = {Lit(type)}", 1);
                result.TotalLines = count.Rows.Count > 0 ? int.Parse(count.Rows[0][0]) : 0;
            }
            return result;
        }

        /// <summary>Current CREATE OR REPLACE source of a PL/SQL unit or view, or null if it does not exist.</summary>
        public static string CurrentSource(ToolContext ctx, string type, string owner, string name)
        {
            type = (type ?? "").ToUpperInvariant();
            if (PlsqlSourceTypes.Contains(type))
            {
                var src = ReadSource(ctx, owner, name, type);
                if (src.FirstLine == 0) return null;
                return "CREATE OR REPLACE " + src.Text.TrimEnd('\n', '\r', ' ');
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

        /// <summary>Compiler errors and warnings of a unit (ALL_ERRORS), formatted separately.</summary>
        public sealed class CompileDiagnostics
        {
            public string Errors, Warnings;
            public int WarningCount;
        }

        public static CompileDiagnostics Diagnostics(ToolContext ctx, string owner, string name, string type, int maxWarnings = 25)
        {
            var r = ctx.Ide.Query(
                "select type, line, position, attribute, asciistr(text) from all_errors where owner = " + Lit(owner) +
                " and name = " + Lit(name) + (type != null ? " and type = " + Lit(type) : "") + " order by type, sequence", 300);
            var errors = new StringBuilder();
            var warnings = new StringBuilder();
            int count = 0;
            foreach (var row in r.Rows)
            {
                var line = $"{row[0]} line {row[1]}, col {row[2]}: {UnAsciiStr(row[4]).Trim()}\n";
                if (row[3] != "WARNING") errors.Append(line);
                else if (++count <= maxWarnings) warnings.Append(line);
            }
            if (count > maxWarnings) warnings.Append($"... and {count - maxWarnings} more warnings (get_compile_errors lists all).\n");
            return new CompileDiagnostics
            {
                Errors = errors.Length > 0 ? errors.ToString() : null,
                Warnings = warnings.Length > 0 ? warnings.ToString() : null,
                WarningCount = count,
            };
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
            "Use it to inspect data and the data dictionary (ALL_TABLES, ALL_TAB_COLUMNS, ALL_DEPENDENCIES, V$ views…); " +
            "restrict data queries (WHERE, FETCH FIRST n ROWS ONLY). Not for reading PL/SQL source code: get_object_source " +
            "returns a whole unit or a large line range in one call, search_source finds lines.";
        public override JObject Parameters => Schema(
            Req("sql", "string", "A single SELECT statement, without trailing semicolon."),
            Opt("max_rows", "integer", "Maximum rows to return (default 50)."));
        public override string Summarize(JObject args) => OneLine(Str(args, "sql"));

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var sql = PrepareStatement(ReqStr(args, "sql"));
            if (!IsReadOnlyQuery(sql, out var reason)) return "Not executed: " + reason;
            int max = Math.Max(1, Math.Min(Int(args, "max_rows", 50), ctx.Config.MaxQueryRows));
            var result = await QueryRunner.RunAsync(ctx, sql, max).ConfigureAwait(false);
            if (ReadsSourceText(sql)) result += "\n\n" + SourceReadNote;
            return result;
        }

        /// <summary>
        /// Models tend to page through ALL_SOURCE / DBA_SOURCE with run_query, 20 lines per call – every call a slow round
        /// trip to the model. A query that reads the TEXT of *_SOURCE (not a search or a count) gets this note.
        /// </summary>
        internal const string SourceReadNote =
            "Note: do not read PL/SQL source with run_query on ALL_SOURCE / DBA_SOURCE / USER_SOURCE. Use get_object_source: " +
            "it returns the whole unit in one call, or – for a very large unit – a large range with start_line / end_line " +
            "(e.g. a whole subprogram). Every call is a slow round trip to the model, so read source in as few calls as possible.";

        internal static bool ReadsSourceText(string sql)
        {
            var s = StripStringsAndComments(sql);
            return Regex.IsMatch(s, @"\b(ALL|DBA|USER|CDB)_SOURCE\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(s, @"\bTEXT\b", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(s, @"\bLIKE\b|\bINSTR\s*\(|\bREGEXP_(LIKE|INSTR|COUNT)\b|\bCOUNT\s*\(", RegexOptions.IgnoreCase);
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
            else sql.Append(" and owner not in (").Append(Db.SystemOwnersSql).Append(")");
            var like = Str(args, "name_like");
            if (!string.IsNullOrWhiteSpace(like)) sql.Append(" and object_name like ").Append(Lit(like.Trim().ToUpperInvariant()));
            var type = Str(args, "object_type");
            if (!string.IsNullOrWhiteSpace(type)) sql.Append(" and object_type = ").Append(Lit(Regex.Replace(type.Trim().ToUpperInvariant(), @"[\s_]+", " ")));
            if (Bool(args, "only_invalid", false)) sql.Append(" and status = 'INVALID'");
            sql.Append(" order by owner, object_type, object_name");
            var r = ctx.Ide.Query(sql.ToString(), Math.Min(Int(args, "max_rows", 100), 500));
            return Task.FromResult(r.Rows.Count == 0 ? "No matching objects." : FormatResult(r, ctx.Config.ToolOutputLimit));
        }
    }

    internal sealed class DescribeObjectTool : AgentTool
    {
        public override string Name => "describe_object";
        public override string Description =>
            "Describes a table or view (columns with types, nullability, comments, primary/unique/foreign keys, indexes) " +
            "or the subprograms and parameters of a package, procedure, function or type. Use this before writing SQL " +
            "against a table or calling a package. For performance work set include_statistics.";
        public override JObject Parameters => Schema(
            Req("name", "string", "Object name, optionally OWNER.NAME."),
            Opt("object_type", "string", "Optional type to disambiguate."),
            Opt("include_statistics", "boolean", "Tables and materialized views: also the optimizer statistics – row count, " +
                "last analyzed, stale or locked statistics, partitioning, per column distinct values / nulls / histogram, per " +
                "index status, height, clustering factor. Default false."));
        public override string Summarize(JObject args) => Str(args, "name") + (Bool(args, "include_statistics", false) ? " (statistics)" : "");

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var obj = Db.ResolveOne(ctx, ReqStr(args, "name"), Str(args, "object_type"));
            var sb = new StringBuilder($"{obj}\n");
            switch (obj.Type)
            {
                case "TABLE":
                case "VIEW":
                case "MATERIALIZED VIEW":
                    DescribeTable(ctx, obj, sb, obj.Type != "VIEW" && Bool(args, "include_statistics", false));
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
            return Task.FromResult(Db.Truncate(sb.ToString(), ctx.Config.ToolOutputLimit));
        }

        internal static void DescribeTable(ToolContext ctx, Db.ObjRef obj, StringBuilder sb, bool statistics)
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

            if (statistics)
            {
                AppendStatistics(ctx, obj, sb);
                return;
            }
            var idx = ctx.Ide.Query(
                "select i.index_name, i.uniqueness, (select listagg(ic.column_name, ', ') within group (order by ic.column_position) " +
                "from all_ind_columns ic where ic.index_owner = i.owner and ic.index_name = i.index_name) columns " +
                $"from all_indexes i where i.table_owner = {o} and i.table_name = {n} order by i.index_name", 100);
            if (idx.Rows.Count > 0) sb.Append("\nIndexes:\n").Append(FormatResult(idx, 10000)).Append('\n');
        }

        /// <summary>Optimizer statistics of a table: what the optimizer believes about its size and selectivity.</summary>
        private static void AppendStatistics(ToolContext ctx, Db.ObjRef obj, StringBuilder sb)
        {
            string o = Lit(obj.Owner), n = Lit(obj.Name);
            var table = Section(ctx, sb, "Table statistics (optimizer statistics as of LAST_ANALYZED, not live counts)",
                "select s.num_rows, s.blocks, s.avg_row_len, to_char(s.last_analyzed, 'YYYY-MM-DD HH24:MI') last_analyzed, " +
                "s.stale_stats, s.stattype_locked locked, t.partitioned, t.temporary, t.iot_type, trim(t.degree) degree " +
                "from all_tables t left join all_tab_statistics s on s.owner = t.owner and s.table_name = t.table_name and s.object_type = 'TABLE' " +
                $"where t.owner = {o} and t.table_name = {n}", 1);
            if (table != null && table.Rows.Count > 0 && table.Rows[0][6] == "YES")
            {
                Section(ctx, sb, "Partitioning",
                    "select p.partitioning_type, p.subpartitioning_type, p.partition_count, " +
                    "(select listagg(k.column_name, ', ') within group (order by k.column_position) from all_part_key_columns k " +
                    "  where k.owner = p.owner and k.name = p.table_name and k.object_type = 'TABLE') partition_key " +
                    $"from all_part_tables p where p.owner = {o} and p.table_name = {n}", 1);
            }

            Section(ctx, sb, "Column statistics",
                "select c.column_name, s.num_distinct, s.num_nulls, s.histogram, s.num_buckets " +
                "from all_tab_columns c left join all_tab_col_statistics s on s.owner = c.owner and s.table_name = c.table_name and s.column_name = c.column_name " +
                $"where c.owner = {o} and c.table_name = {n} order by c.column_id", 1000);

            const string indexColumns =
                "(select listagg(ic.column_name, ', ') within group (order by ic.column_position) " +
                "from all_ind_columns ic where ic.index_owner = i.owner and ic.index_name = i.index_name) columns";
            var idx = Section(ctx, sb, "Indexes with statistics",
                $"select i.index_name, i.index_type, i.uniqueness, {indexColumns}, i.status, i.visibility, i.partitioned, " +
                "i.blevel, i.leaf_blocks, i.distinct_keys, i.clustering_factor, i.num_rows, to_char(i.last_analyzed, 'YYYY-MM-DD') last_analyzed " +
                $"from all_indexes i where i.table_owner = {o} and i.table_name = {n} order by i.index_name", 100,
                // before 11g there is no VISIBILITY column
                $"select i.index_name, i.index_type, i.uniqueness, {indexColumns}, i.status, i.partitioned, " +
                "i.blevel, i.leaf_blocks, i.distinct_keys, i.clustering_factor, i.num_rows, to_char(i.last_analyzed, 'YYYY-MM-DD') last_analyzed " +
                $"from all_indexes i where i.table_owner = {o} and i.table_name = {n} order by i.index_name");
            sb.Append("\nHints: STALE_STATS = YES or no LAST_ANALYZED means missing or outdated statistics. A CLUSTERING_FACTOR near BLOCKS " +
                      "means the table is ordered like the index (range scans are cheap), near NUM_ROWS that it is not. " +
                      "A column histogram (FREQUENCY, HYBRID, HEIGHT BALANCED, TOP-FREQUENCY) lets the optimizer see skewed values. " +
                      "STATUS UNUSABLE: the index is not used and DML on the table may fail.\n");
            if (idx != null && idx.Rows.Count == 0) sb.Append("The table has no indexes.\n");
        }

        /// <summary>Appends a titled query result; null (and a short note) if the query fails, e.g. on an old Oracle version.</summary>
        private static QueryResult Section(ToolContext ctx, StringBuilder sb, string title, string sql, int maxRows, string fallbackSql = null)
        {
            QueryResult r;
            try { r = ctx.Ide.Query(sql, maxRows); }
            catch (OracleSqlException ex)
            {
                if (fallbackSql == null)
                {
                    sb.Append($"\n{title}: not available ({ex.Message.Split('\n')[0]})\n");
                    return null;
                }
                try { r = ctx.Ide.Query(fallbackSql, maxRows); }
                catch (OracleSqlException ex2)
                {
                    sb.Append($"\n{title}: not available ({ex2.Message.Split('\n')[0]})\n");
                    return null;
                }
            }
            if (r.Rows.Count > 0) sb.Append($"\n{title}:\n").Append(FormatResult(r, 30000)).Append('\n');
            return r;
        }

        internal static void DescribeProgram(ToolContext ctx, Db.ObjRef obj, StringBuilder sb)
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
            "spec and body), views, triggers, types, and DDL for tables, indexes, sequences etc. This is THE way to read " +
            "stored source: one call returns the whole unit (about 1,000 lines fit). Large units are cut at the output limit " +
            "with a note where to continue; read further parts with start_line / end_line (for a package body use object_type " +
            "PACKAGE BODY). Read large ranges – a whole subprogram or more – not a few lines per call.";
        public override JObject Parameters => Schema(
            Req("name", "string", "Object name, optionally OWNER.NAME."),
            Opt("object_type", "string", "e.g. PACKAGE, PACKAGE BODY, PROCEDURE, FUNCTION, TRIGGER, TYPE, VIEW, TABLE. For PACKAGE both spec and body are returned."),
            Opt("with_line_numbers", "boolean", "Prefix lines with line numbers (useful together with compile errors)."),
            Opt("start_line", "integer", "First line to return (PL/SQL units; applies to each returned unit)."),
            Opt("end_line", "integer", "Last line to return (PL/SQL units)."));
        public override string Summarize(JObject args) =>
            $"{Str(args, "object_type", "")} {Str(args, "name")}".Trim() +
            (Int(args, "start_line", 0) > 0 || Int(args, "end_line", 0) > 0 ? $" lines {Math.Max(1, Int(args, "start_line", 1))}-{(Int(args, "end_line", 0) > 0 ? Int(args, "end_line", 0).ToString() : "")}" : "");

        private static readonly HashSet<string> SourceTypes = new HashSet<string>
        {
            "PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "TYPE BODY", "JAVA SOURCE",
        };

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var obj = Db.ResolveOne(ctx, ReqStr(args, "name"), Str(args, "object_type"));
            bool numbers = Bool(args, "with_line_numbers", false);
            int from = Math.Max(1, Int(args, "start_line", 1)), to = Math.Max(0, Int(args, "end_line", 0));
            if (to > 0 && to < from) return Task.FromResult($"end_line ({to}) is before start_line ({from}).");
            var sb = new StringBuilder();

            if (SourceTypes.Contains(obj.Type))
            {
                // room for the header line and the notes; each unit is cut at a line boundary
                int budget = ctx.Config.ToolOutputLimit - 400;
                AppendSource(ctx, obj, obj.Type, numbers, from, to, ref budget, sb);
                if (obj.Type == "PACKAGE" || obj.Type == "TYPE")
                {
                    var bodyType = obj.Type + " BODY";
                    if (Db.Resolve(ctx, obj.Owner + "." + obj.Name, bodyType).Count > 0)
                    {
                        sb.Append("\n/\n\n");
                        AppendSource(ctx, obj, bodyType, numbers, from, to, ref budget, sb);
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
            return Task.FromResult(Db.Truncate($"-- {obj}\n" + sb, ctx.Config.ToolOutputLimit));
        }

        /// <summary>
        /// Appends lines from..to of one unit, at most <paramref name="budget"/> characters (cut at a line boundary,
        /// with a note where to continue). Only the lines that fit are transferred from the database.
        /// </summary>
        internal static void AppendSource(ToolContext ctx, Db.ObjRef obj, string type, bool numbers, int from, int to, ref int budget, StringBuilder sb)
        {
            string unit = $"{type} {obj.Owner}.{obj.Name}";
            if (budget < 200)
            {
                sb.Append($"-- [{unit} not shown: output limit reached. Read it with get_object_source, object_type '{type}'.]\n");
                return;
            }
            var src = Db.ReadSource(ctx, obj.Owner, obj.Name, type, from, to, budget);
            if (src.FirstLine == 0)
            {
                sb.Append(src.TotalLines > 0
                    ? $"-- ({unit} has only {src.TotalLines} lines)\n"
                    : $"-- (no source visible for {unit})\n");
                return;
            }
            bool range = from > 1 || to > 0;
            if (range) sb.Append($"-- {unit}, lines {src.FirstLine}-{src.LastLine} of {src.TotalLines}\n");

            // the stored lines end with a line feed; the first line starts after CREATE OR REPLACE
            var lines = src.Text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            if (src.FirstLine == 1) lines[0] = "CREATE OR REPLACE " + lines[0];
            int width = (src.FirstLine + lines.Length - 1).ToString().Length;
            var outLines = lines.Select((line, i) => numbers ? (src.FirstLine + i).ToString().PadLeft(width) + ": " + line : line).ToList();
            int end = to > 0 ? Math.Min(to, src.TotalLines) : src.TotalLines;
            // too long: keep room for the list of the subprograms that are not shown
            bool cut = !src.Complete || src.FirstLine + lines.Length - 1 < end || outLines.Sum(l => l.Length + 1) > budget;
            int lineBudget = cut ? Math.Max(budget / 2, budget - OutlineChars - 300) : budget;
            int shown = 0, used = 0;
            var text = new StringBuilder();
            foreach (var outLine in outLines)
            {
                if (used + outLine.Length + 1 > lineBudget && shown > 0) break;
                text.Append(outLine).Append('\n');
                used += outLine.Length + 1;
                shown++;
            }
            int last = src.FirstLine + shown - 1;
            if (last < end)
                sb.Append($"-- [{unit} has {src.TotalLines} lines, too long for one answer: only lines {src.FirstLine}-{last} follow. " +
                          $"Lines {last + 1}-{end} are NOT shown; the note at the end lists their subprograms.]\n");
            // without line numbers the whole unit ends without trailing blanks (as compiled)
            sb.Append(numbers ? text.ToString() : text.ToString().TrimEnd('\n', '\r', ' ') + "\n");
            budget -= used;
            if (last < end)
            {
                var outline = Outline(ctx, obj, type, last + 1, end);
                sb.Append($"-- [{unit}: output limit reached after line {last} of {src.TotalLines}. NOT shown: lines {last + 1}-{end}" +
                          (outline.Length > 0 ? $", with the subprograms {outline}" : "") +
                          $". Read them with get_object_source name '{obj.Owner}.{obj.Name}', object_type '{type}', start_line {last + 1} " +
                          "(or the line of a subprogram listed here), optionally end_line.]\n");
                budget = Math.Min(budget, 0); // the next unit only gets a note
            }
        }

        /// <summary>Most characters for the list of subprograms that a cut unit does not show.</summary>
        private const int OutlineChars = 3000;

        /// <summary>"close_quarter (line 2313), …": procedures and functions declared in lines from..to (one small query).</summary>
        private static string Outline(ToolContext ctx, Db.ObjRef obj, string type, int fromLine, int toLine)
        {
            try
            {
                var r = ctx.Ide.Query(
                    $"select line, text from all_source where owner = {Lit(obj.Owner)} and name = {Lit(obj.Name)} and type = {Lit(type)} " +
                    $"and line between {fromLine} and {toLine} and regexp_like(text, '^\\s*(procedure|function)\\s', 'i') order by line", 2000);
                var sb = new StringBuilder();
                foreach (var row in r.Rows)
                {
                    var m = Regex.Match(row[1] ?? "", @"^\s*(procedure|function)\s+(""[^""]+""|[\w$#]+)", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    var item = $"{m.Groups[2].Value} (line {row[0]})";
                    if (sb.Length + item.Length + 2 > OutlineChars) { sb.Append(", …"); break; }
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(item);
                }
                return sb.ToString();
            }
            catch (OracleSqlException) { return ""; }
        }
    }

    internal sealed class SearchSourceTool : AgentTool
    {
        public override string Name => "search_source";
        public override string Description =>
            "Searches PL/SQL source code in the database (ALL_SOURCE) for a text, case-insensitive. Returns matching " +
            "lines with object name and line number. Good for finding usages of a table, column or procedure. To read the " +
            "code around a match, call get_object_source with start_line / end_line covering the whole subprogram.";
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
            else sql.Append(" and owner not in (").Append(Db.SystemOwnersSql).Append(")");
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
            return Task.FromResult(Db.Truncate(sb.ToString(), ctx.Config.ToolOutputLimit));
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
                return Task.FromResult(r.Rows.Count == 0 ? "No compilation errors in schema " + ctx.CurrentSchema + "." : FormatResult(r, ctx.Config.ToolOutputLimit, 500));
            }
            SplitName(name, out var owner, out var n);
            owner = owner ?? ctx.CurrentSchema;
            var given = Str(args, "object_type");
            var type = string.IsNullOrWhiteSpace(given) ? null : Regex.Replace(given.Trim().ToUpperInvariant(), @"\s+", " ");
            // a package or type means specification and body: the errors are usually in the body
            var errors = type == "PACKAGE" || type == "TYPE"
                ? (Db.Errors(ctx, owner, n, type) ?? "") + (Db.Errors(ctx, owner, n, type + " BODY") ?? "")
                : Db.Errors(ctx, owner, n, type);
            if (!string.IsNullOrEmpty(errors)) return Task.FromResult(errors);

            // no errors recorded, but the object can still be INVALID (invalidated by a change of an object it uses)
            var status = ctx.Ide.Query(
                $"select object_type, status from all_objects where owner = {Lit(owner)} and object_name = {Lit(n)} " +
                "and object_type in ('PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'FUNCTION', 'TRIGGER', 'TYPE', 'TYPE BODY', 'VIEW') order by object_type", 10);
            if (status.Rows.Count == 0) return Task.FromResult($"No object {owner}.{n} found (check the name with list_objects).");
            var invalid = status.Rows.Where(r => r[1] == "INVALID").Select(r => r[0]).ToList();
            if (invalid.Count == 0) return Task.FromResult("No compilation errors; " + string.Join(", ", status.Rows.Select(r => r[0])) + " VALID.");
            return Task.FromResult($"No compilation errors are recorded, but {string.Join(" and ", invalid)} {owner}.{n} is INVALID: an object it uses " +
                                   "was changed or is invalid itself. recompile_invalid compiles it again and shows the real errors.");
        }
    }

    internal sealed class CompilePlsqlTool : AgentTool
    {
        public override string Name => "compile_plsql";
        public override string Description =>
            "Compiles PL/SQL into the database by executing CREATE OR REPLACE statements (package spec/body, procedure, " +
            "function, trigger, type, view). Several units may be given separated by a line containing only '/'. " +
            "Returns compilation errors and compiler warnings (PLW-) with line numbers, and dependent objects that became " +
            "invalid. Each unit keeps the compiler settings stored with it (PLSQL_CCFLAGS, optimize level, native " +
            "compilation, debug information). The user must approve. Typical use: compile the code you wrote in the " +
            "editor (pass the full editor text) and fix errors until it compiles cleanly.";
        public override JObject Parameters => Schema(
            Req("source", "string", "Complete CREATE OR REPLACE source of one or more units ('/' line between units)."),
            Opt("keep_settings", "boolean", "Default true. false: compile with the compiler settings of your session instead " +
                "(only after you changed them on purpose, e.g. ALTER SESSION SET PLSQL_CCFLAGS)."));
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

            // objects that also have local source files: the database, the local files or both (asked once per chat)
            var changed = new List<(string Type, string Owner, string Name)>();
            foreach (var p in parts)
                if (IsPlsqlUnit(PrepareStatement(p), out var t, out var o, out var n)) changed.Add((t, o ?? ctx.CurrentSchema, n));
            var (proceed, localNote) = await LocalChanges.CheckAsync(ctx, changed);
            if (!proceed) return localNote;

            var sensitive = parts.Select(SensitiveUnitReason).FirstOrDefault(r => r != null);
            if (!ctx.Config.AutoApproveSql || sensitive != null)
            {
                var approval = await ctx.ApproveAsync(new ApprovalRequest
                {
                    Category = sensitive != null ? "sql-destructive" : "sql",
                    Title = L.T("Compile into database ({0}): {1}", ctx.Ide.GetContext().ConnectionLabel, Summarize(args)),
                    Detail = source.Replace("\r\n", "\n"),
                    Warning = sensitive != null ? L.T(sensitive) : null,
                    AlwaysAsk = sensitive != null,
                });
                if (!approval.Approved) return approval.RejectionMessage;
            }

            var result = Compile(ctx, parts, "compile", Bool(args, "keep_settings", true));
            return localNote == null ? result : result.TrimEnd() + "\n\n" + localNote;
        }

        /// <summary>
        /// Executes CREATE OR REPLACE statements (backing up the previous versions, keeping the units' compiler
        /// settings) and reports errors, warnings and dependent objects that became invalid.
        /// </summary>
        internal static string Compile(ToolContext ctx, List<string> parts, string reason, bool keepSettings = true)
        {
            var units = new List<(string Stmt, string Type, string Owner, string Name)>();
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
                units.Add((stmt, type, owner ?? ctx.CurrentSchema, name));
            }

            // a changed specification, type or view can invalidate the objects that use it
            var watch = new Dependencies.InvalidationWatch(ctx, units.Select(u => (u.Owner, u.Name, u.Type)));
            var sb = new StringBuilder();
            using (var settings = new CompilerSettingsScope(ctx, keepSettings))
            {
                foreach (var (stmt, type, owner, name) in units)
                {
                    var pending = Db.BeforeChange(ctx, type, owner, name, reason, stmt);
                    if (type != "VIEW") settings.Prepare(type, owner, name);
                    var res = ctx.Ide.Execute(stmt);
                    var backupNote = pending.Backup == null ? "" : $" (previous version backed up as {pending.Backup.Id})";
                    if (res.Ok || res.CompiledWithErrors)
                    {
                        Db.AfterChange(ctx, pending);
                        var diag = type == "VIEW" ? new Db.CompileDiagnostics() : Db.Diagnostics(ctx, owner, name, type);
                        ctx.Ide.RefreshObject(type, owner, name, 2);
                        if (diag.Errors != null)
                        {
                            sb.Append($"{type} {owner}.{name}: COMPILED WITH ERRORS{backupNote} (line numbers are relative to the CREATE statement):\n{diag.Errors}");
                            if (diag.WarningCount > 0) sb.Append($"(plus {diag.WarningCount} compiler warnings; fix the errors first)\n");
                            sb.Append('\n');
                        }
                        else if (diag.Warnings != null)
                            sb.Append($"{type} {owner}.{name}: compiled successfully{backupNote}, with {diag.WarningCount} compiler warning{(diag.WarningCount == 1 ? "" : "s")}:\n{diag.Warnings}\n");
                        else
                            sb.Append($"{type} {owner}.{name}: compiled successfully{backupNote}.\n");
                    }
                    else
                    {
                        sb.Append($"{type} {owner}.{name}: FAILED: {res.ErrorMessage}\n");
                    }
                }
            }
            sb.Append(watch.Report());
            return sb.ToString().TrimEnd();
        }
    }

    internal sealed class ExecuteSqlTool : AgentTool
    {
        public override string Name => "execute_sql";
        public override string Description =>
            "Executes ONE SQL statement or anonymous PL/SQL block (DML, DDL, BEGIN…END) in the agent's database session. " +
            "The user must approve it. DBMS_OUTPUT is returned. DML is not committed unless you execute COMMIT (only " +
            "do that if the user asked). For CREATE OR REPLACE of PL/SQL units prefer compile_plsql. The session is in the " +
            "user's schema: for objects of other schemas qualify the names (OWNER.NAME) instead of ALTER SESSION SET CURRENT_SCHEMA.";
        public override JObject Parameters => Schema(
            Req("sql", "string", "The statement or PL/SQL block."),
            Opt("purpose", "string", "Short explanation shown to the user in the approval dialog."));
        public override bool Mutating => true;
        public override string Summarize(JObject args) => RunQueryTool.OneLine(Str(args, "sql"));

        private static readonly Regex DropRegex = new Regex(
            @"^DROP\s+(?<t>PACKAGE\s+BODY|PACKAGE|PROCEDURE|FUNCTION|TRIGGER|TYPE\s+BODY|TYPE|VIEW)\s+(?:(?<o>""[^""]+""|[\w$#]+)\s*\.\s*)?(?<n>""[^""]+""|[\w$#]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Statements that change data inside the transaction (no implicit commit).</summary>
        private static readonly HashSet<string> DataKeywords = new HashSet<string> { "INSERT", "UPDATE", "DELETE", "MERGE", "BEGIN", "DECLARE", "CALL", "LOCK" };
        private static int _savepoints;

        private static readonly Regex ViewRegex = new Regex(
            @"^CREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:NO\s+)?FORCE\s+)?(?:(?:NON)?EDITIONABLE\s+)?VIEW\s+(?:(?<o>""[^""]+""|[\w$#]+)\s*\.\s*)?(?<n>""[^""]+""|[\w$#]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var sql = PrepareStatement(ReqStr(args, "sql"));
            if (sql.Length == 0) return "Nothing to execute.";
            var kw = FirstKeyword(sql);
            // several statements fail in the database anyway: say so before the user is asked to approve them
            if (!IsPlsqlBlock(sql) && kw != "WITH" && Regex.IsMatch(StripStringsAndComments(sql), @";\s*\S"))
                return "Not executed: execute_sql runs exactly ONE statement, but this text contains several (separated by ';'). " +
                       "Send each statement in its own execute_sql call (several calls in one response are fine), or put DML into one BEGIN … END block.";
            bool returnsRows = kw == "SELECT" || kw == "WITH";
            // only a plain query skips the approval: a "query" with PL/SQL in its WITH clause can change and commit data
            bool readOnlyQuery = returnsRows && IsReadOnlyQuery(sql, out _);
            // a plain query runs like run_query (estimate first, row limit in the query, runtime)
            if (readOnlyQuery) return await QueryRunner.RunAsync(ctx, sql, Math.Min(50, ctx.Config.MaxQueryRows)).ConfigureAwait(false);
            var sensitive = SensitiveReason(sql);

            if (!readOnlyQuery && (!ctx.Config.AutoApproveSql || sensitive != null))
            {
                var purpose = Str(args, "purpose");
                var approval = await ctx.ApproveAsync(new ApprovalRequest
                {
                    Category = sensitive != null ? "sql-destructive" : "sql",
                    Title = L.T("Execute in database ({0})", ctx.Ide.GetContext().ConnectionLabel) + (string.IsNullOrWhiteSpace(purpose) ? "" : ": " + purpose),
                    Detail = sql.Replace("\r\n", "\n"),
                    Warning = sensitive != null ? L.T(sensitive) : null,
                    AlwaysAsk = sensitive != null,
                });
                if (!approval.Approved) return approval.RejectionMessage;
            }

            bool mayChangeSession = (returnsRows && !readOnlyQuery) || MayChangeSession(sql, kw);
            try
            {
                return Execute(ctx, sql, kw, returnsRows, readOnlyQuery);
            }
            finally
            {
                // also after an error or Stop: the statement may have switched the schema before it failed, or still runs
                if (mayChangeSession) ctx.SessionMayHaveChanged();
            }
        }

        /// <summary>ALTER SESSION, SET ROLE, PL/SQL blocks and CALL can change the session (current schema, roles …).</summary>
        internal static bool MayChangeSession(string sql, string kw) =>
            kw == "BEGIN" || kw == "DECLARE" || kw == "CALL" || kw == "SET" ||
            (kw == "ALTER" && Regex.IsMatch(StripLeadingComments(sql), @"^ALTER\s+SESSION\b", RegexOptions.IgnoreCase));

        private static string Execute(ToolContext ctx, string sql, string kw, bool returnsRows, bool readOnlyQuery)
        {
            if (returnsRows)
            {
                var r = ctx.Ide.Query(sql, Math.Min(50, ctx.Config.MaxQueryRows));
                if (!readOnlyQuery)
                {
                    // may have changed data (WITH FUNCTION, FOR UPDATE): refresh the transaction state, list it for rewind
                    ctx.Ide.Execute("begin null; end;");
                    ctx.Changed(new ChangeRecord { Kind = ChangeRecord.NotUndoable, Label = RunQueryTool.OneLine(sql), TransactionId = ctx.Ide.PendingTransaction });
                }
                return FormatResult(r, ctx.Config.ToolOutputLimit);
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

            // Data changes (DML, PL/SQL blocks) can be rewound while uncommitted. In an open transaction a savepoint
            // marks the state before the statement; without one, the statement starts the transaction and rewinding it
            // is a plain ROLLBACK (a SAVEPOINT would open a transaction by itself, even if the statement changes nothing).
            string savepoint = null;
            bool rewindable = false;
            if (pending.Count == 0 && ctx.RecordChange != null && DataKeywords.Contains(kw) && ctx.Ide.UsesOwnSession)
            {
                rewindable = true;
                if (ctx.Ide.PendingTransaction != null)
                {
                    savepoint = "PDA_SP_" + System.Threading.Interlocked.Increment(ref _savepoints);
                    rewindable = ctx.Ide.Execute("savepoint " + savepoint).Ok;
                }
            }

            ExecResult res;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (IsPlsqlUnit(sql, out var ut, out var uo, out var un))
            {
                // like compile_plsql: the unit keeps its stored compiler settings
                using (var settings = new CompilerSettingsScope(ctx))
                {
                    settings.Prepare(ut, uo ?? ctx.CurrentSchema, un);
                    res = ctx.Ide.Execute(sql);
                }
            }
            else res = ctx.Ide.Execute(sql);
            var seconds = watch.Elapsed.TotalSeconds;
            if (res.Ok || res.CompiledWithErrors)
            {
                foreach (var p in pending) Db.AfterChange(ctx, p);
                if (pending.Count == 0)
                {
                    var txn = ctx.Ide.PendingTransaction;
                    ctx.Changed(new ChangeRecord
                    {
                        // without an open transaction afterwards there is nothing to roll back (no change, or committed inside)
                        Kind = rewindable && txn != null ? ChangeRecord.Data : ChangeRecord.NotUndoable,
                        Label = RunQueryTool.OneLine(sql), Savepoint = savepoint, TransactionId = txn,
                    });
                }
            }
            var backup = pending.Select(p => p.Backup).FirstOrDefault(b => b != null);
            var sb = new StringBuilder();
            if (backup != null) sb.Append($"(Previous version backed up as {backup.Id}.) ");
            Db.CompileDiagnostics diag = null;
            if (res.CompiledWithErrors && IsPlsqlUnit(sql, out var dt, out var dow, out var dn))
            {
                // ORA-24344 also comes for warnings only
                try { diag = Db.Diagnostics(ctx, dow ?? ctx.CurrentSchema, dn, dt); }
                catch (OracleSqlException) { }
            }
            if (res.Ok) sb.Append("Executed successfully.");
            else if (diag != null && diag.Errors == null)
                sb.Append($"Executed successfully, with {diag.WarningCount} compiler warning{(diag.WarningCount == 1 ? "" : "s")}:\n{diag.Warnings}");
            else if (res.CompiledWithErrors) sb.Append("Executed, but the object has compilation errors (use get_compile_errors).");
            else sb.Append("FAILED: ").Append(res.ErrorMessage);
            if (seconds >= 1) sb.Append($" (The statement took {seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s.)");
            if (!string.IsNullOrWhiteSpace(res.DbmsOutput)) sb.Append("\nDBMS_OUTPUT:\n").Append(Db.Truncate(res.DbmsOutput, ctx.Config.ToolOutputLimit));
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
