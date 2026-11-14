using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>
    /// Runs a statement and measures it: rows, elapsed and CPU time, and – with access to the V$ views – the actual
    /// execution plan (ALLSTATS LAST) and the session statistics. Compares two statements (original and rewrite).
    /// <para>
    /// The statement runs through DBMS_SQL inside an anonymous block, so a query is fetched completely in the
    /// database (no rows travel to the IDE) and its text stays exactly as written. A comment with a unique tag in
    /// front of it finds the cursor in V$SQL afterwards – DISPLAY_CURSOR(NULL) would show the plug-in's own
    /// statements that run after every Execute.
    /// </para>
    /// </summary>
    internal sealed class MeasureSqlTool : AgentTool
    {
        public override string Name => "measure_sql";
        public override string Description =>
            "Executes a statement and measures it: use it to find out why a statement is slow and to PROVE that a rewrite " +
            "is faster. Reports rows, elapsed and CPU time and, if the user may read the V$ views, the ACTUAL execution plan " +
            "(DBMS_XPLAN.DISPLAY_CURSOR ALLSTATS LAST: estimated E-Rows vs actual A-Rows, Starts and Buffers per step) and " +
            "the session statistics (logical and physical reads, redo, sorts). With compare_sql it measures a second " +
            "statement (e.g. your rewrite) the same way, shows both side by side and checks that two queries return the same " +
            "rows. Queries are fetched completely inside the database. INSERT/UPDATE/DELETE/MERGE need the user's approval " +
            "and are rolled back afterwards. The statement runs to completion: not for statements that take minutes.";

        public override JObject Parameters => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sql"] = new JObject { ["type"] = "string", ["description"] = "The statement to measure (SELECT/WITH, INSERT, UPDATE, DELETE, MERGE), without trailing semicolon." },
                ["compare_sql"] = new JObject { ["type"] = "string", ["description"] = "Optional second statement, e.g. the rewritten version, measured the same way." },
                ["binds"] = new JObject
                {
                    ["type"] = "object",
                    ["description"] = "Values for bind variables, by name without colon, e.g. {\"dno\": 10, \"name\": \"KING\"}. Numbers are bound as NUMBER, " +
                                      "strings as VARCHAR2 (convert dates in the SQL, e.g. to_date(:d, 'YYYY-MM-DD')). Binds keep the plan realistic: prefer them to literals.",
                },
                ["runs"] = new JObject { ["type"] = "integer", ["description"] = "Executions per statement (default 1, maximum 5); the statistics of the last run are reported. Use 2 for a fair comparison with a warm cache (the first run includes the hard parse)." },
                ["check_same_rows"] = new JObject { ["type"] = "boolean", ["description"] = "With compare_sql and two queries: check with MINUS in both directions that they return the same rows (default true)." },
            },
            ["required"] = new JArray("sql"),
        };

        public override string Summarize(JObject args) =>
            RunQueryTool.OneLine(Str(args, "sql")) + (string.IsNullOrWhiteSpace(Str(args, "compare_sql")) ? "" : "  ⇄  " + RunQueryTool.OneLine(Str(args, "compare_sql")));

        private static readonly HashSet<string> DmlKeywords = new HashSet<string> { "INSERT", "UPDATE", "DELETE", "MERGE" };
        private const int MaxStatementLength = 30000;
        private static int _savepoints;

        /// <summary>Session statistics shown (V$STATNAME names) with their labels.</summary>
        private static readonly (string Name, string Label)[] Statistics =
        {
            ("session logical reads", "logical reads"),
            ("consistent gets", "consistent gets"),
            ("db block gets", "db block gets"),
            ("physical reads", "physical reads"),
            ("redo size", "redo bytes"),
            ("sorts (memory)", "sorts in memory"),
            ("sorts (disk)", "sorts on disk"),
            ("workarea executions - onepass", "work areas one-pass"),
            ("workarea executions - multipass", "work areas multi-pass"),
            // not shown: "recursive calls" counts every DBMS_SQL fetch and "parse count (hard)" includes the
            // measuring block itself – artifacts of the measurement, not properties of the statement
        };

        private sealed class Access
        {
            public bool Plan, Stats;
            public string StatisticsLevel;
        }

        private sealed class Measurement
        {
            public string Sql, Error;
            public bool Query;
            public long Rows;
            public List<double> Elapsed = new List<double>();
            public double Cpu;
            public Dictionary<string, long> Stats;
            public string Plan;
        }

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var statements = new List<string> { PrepareStatement(ReqStr(args, "sql")).Replace("\r\n", "\n") };
            var compare = Str(args, "compare_sql");
            if (!string.IsNullOrWhiteSpace(compare)) statements.Add(PrepareStatement(compare).Replace("\r\n", "\n"));
            int runs = Math.Max(1, Math.Min(Int(args, "runs", 1), 5));
            var binds = Binds(args?["binds"]);

            bool changesData = false;
            string sensitive = null;
            foreach (var s in statements)
            {
                if (s.Length == 0) return "Nothing to measure.";
                if (s.Length > MaxStatementLength) return $"Not executed: measure_sql handles statements up to {MaxStatementLength} characters.";
                var kw = FirstKeyword(s);
                if (IsReadOnlyQuery(s, out var reason)) continue;
                bool forUpdate = (kw == "SELECT" || kw == "WITH") && Regex.IsMatch(StripStringsAndComments(s), @"\bFOR\s+UPDATE\b", RegexOptions.IgnoreCase);
                if (!DmlKeywords.Contains(kw) && !forUpdate)
                    return "Not executed: measure_sql measures SELECT / WITH queries and INSERT, UPDATE, DELETE and MERGE statements" +
                           (kw == "SELECT" || kw == "WITH" ? " (" + reason + ")" : "") + ".";
                changesData = true;
                sensitive = sensitive ?? SensitiveReason(s);
            }

            if (changesData)
            {
                var blocked = ctx.ChangeBlockedReason();
                if (blocked != null)
                    return $"Not executed: {blocked}. measure_sql would run the INSERT/UPDATE/DELETE/MERGE (and roll it back). Use explain_plan for the estimated plan.";
                if (!ctx.Config.AutoApproveSql || sensitive != null)
                {
                    var approval = await ctx.ApproveAsync(new ApprovalRequest
                    {
                        Category = sensitive != null ? "sql-destructive" : "sql",
                        Title = L.T("Measure in database ({0}), changes are rolled back", ctx.Ide.GetContext().ConnectionLabel),
                        Detail = string.Join("\n/\n", statements),
                        Warning = (sensitive != null ? sensitive + " " : "") +
                                  L.T("The statement is executed and rolled back afterwards. Effects outside the transaction remain, e.g. used sequence values and autonomous transactions in triggers."),
                        AlwaysAsk = sensitive != null,
                    });
                    if (!approval.Approved) return approval.RejectionMessage;
                }
            }

            var access = ProbeAccess(ctx);
            var results = new List<Measurement>();
            foreach (var s in statements)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                results.Add(Measure(ctx, s, binds, runs, access));
            }

            string sameRows = null;
            if (results.Count == 2 && results.All(r => r.Query && r.Error == null) && Bool(args, "check_same_rows", true)
                && statements.All(s => IsReadOnlyQuery(s, out _)))
                sameRows = SameRows(ctx, statements[0], statements[1], binds, results[0].Rows, results[1].Rows);

            return Db.Truncate(Report(results, runs, access, sameRows), ctx.Config.MaxToolOutputChars);
        }

        // ---------------------------------------------------------------- measuring

        private static Access ProbeAccess(ToolContext ctx)
        {
            var a = new Access();
            bool Readable(string sql)
            {
                try { ctx.Ide.Query(sql, 1); return true; }
                catch (OracleSqlException) { return false; }
            }
            a.Plan = Readable("select 1 from v$sql where 1 = 0") && Readable("select 1 from v$sql_plan_statistics_all where 1 = 0");
            a.Stats = Readable("select 1 from v$mystat s join v$statname n on n.statistic# = s.statistic# where 1 = 0");
            if (a.Plan)
            {
                try
                {
                    var r = ctx.Ide.Query("select value from v$parameter where name = 'statistics_level'", 1);
                    if (r.Rows.Count > 0) a.StatisticsLevel = r.Rows[0][0];
                }
                catch (OracleSqlException) { }
            }
            return a;
        }

        private static Measurement Measure(ToolContext ctx, string sql, Dictionary<string, JToken> binds, int runs, Access access)
        {
            var m = new Measurement { Sql = sql, Query = FirstKeyword(sql) == "SELECT" || FirstKeyword(sql) == "WITH" };
            bool changesData = !IsReadOnlyQuery(sql, out _);
            // the DBA views setting: measure what run_query would execute
            var executed = sql;
            List<string> dbaViews = null;
            var dbaIde = ctx.Ide as DbaViewsIde;
            if (dbaIde != null) executed = dbaIde.Rewrite(sql, out dbaViews);

            var tag = "PDA_MEASURE_" + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();
            // runtime statistics per plan step for this session only (the same as the GATHER_PLAN_STATISTICS hint, but
            // without touching the statement: an existing hint comment or a WITH clause stay as they are)
            bool statisticsAll = access.Plan && !string.Equals(access.StatisticsLevel, "ALL", StringComparison.OrdinalIgnoreCase);
            if (statisticsAll && !ctx.Ide.Execute("alter session set statistics_level = all").Ok) statisticsAll = false;
            try
            {
                for (int run = 1; run <= runs && m.Error == null; run++)
                {
                    var before = access.Stats ? Snapshot(ctx) : null;
                    string savepoint = null;
                    if (changesData && ctx.Ide.PendingTransaction != null)
                    {
                        savepoint = "PDA_MS_" + System.Threading.Interlocked.Increment(ref _savepoints);
                        ctx.Ide.Execute("savepoint " + savepoint);
                    }
                    ExecResult res;
                    try
                    {
                        res = ctx.Ide.Execute(Block("/* " + tag + " */ " + executed, binds, m.Query));
                        if (!res.Ok && dbaIde != null && !ReferenceEquals(executed, sql) && DbaViewsIde.IsFallbackError(res.ErrorCode))
                        {
                            dbaIde.MarkUnreadable(dbaViews, res.ErrorCode, executed);
                            executed = sql;
                            res = ctx.Ide.Execute(Block("/* " + tag + " */ " + executed, binds, m.Query));
                        }
                    }
                    finally
                    {
                        if (changesData) ctx.Ide.Execute(savepoint != null ? "rollback to savepoint " + savepoint : "rollback");
                    }
                    if (!res.Ok)
                    {
                        // the ORA-06512 lines point into the measuring block, not into the user's code
                        m.Error = string.Join("\n", (res.ErrorMessage ?? "").Replace("\r\n", "\n").Split('\n')
                            .Where(l => !l.StartsWith("ORA-06512") && !l.StartsWith("Help:") && l.Trim().Length > 0));
                        break;
                    }
                    var after = before != null ? Snapshot(ctx) : null;
                    var out_ = Regex.Match(res.DbmsOutput ?? "", @"PDA_MS rows=(\d+) ela=([\d.]+) cpu=(-?\d+)");
                    if (out_.Success)
                    {
                        m.Rows = long.Parse(out_.Groups[1].Value);
                        m.Elapsed.Add(double.Parse(out_.Groups[2].Value, CultureInfo.InvariantCulture));
                        m.Cpu = long.Parse(out_.Groups[3].Value) / 100.0;
                    }
                    if (before != null && after != null)
                        m.Stats = Statistics.ToDictionary(s => s.Name, s => (after.TryGetValue(s.Name, out var a) ? a : 0) - (before.TryGetValue(s.Name, out var b) ? b : 0));
                }
            }
            finally
            {
                if (statisticsAll) ctx.Ide.Execute("alter session set statistics_level = " + (Regex.IsMatch(access.StatisticsLevel ?? "", @"^(BASIC|TYPICAL)$", RegexOptions.IgnoreCase) ? access.StatisticsLevel : "typical"));
            }

            if (m.Error == null && access.Plan)
            {
                try
                {
                    var cursor = ctx.Ide.Query($"select sql_id, child_number from v$sql where sql_text like '/* {tag} */%' order by last_active_time desc", 1);
                    if (cursor.Rows.Count > 0)
                    {
                        var plan = ctx.Ide.Query(
                            $"select plan_table_output from table(dbms_xplan.display_cursor({Lit(cursor.Rows[0][0])}, {int.Parse(cursor.Rows[0][1])}, 'ALLSTATS LAST +PEEKED_BINDS'))", 400);
                        m.Plan = string.Join("\n", plan.Rows.Select(r => r[0]));
                    }
                    else m.Plan = "(the cursor was no longer in the shared pool)";
                }
                catch (OracleSqlException ex) { m.Plan = "(DBMS_XPLAN.DISPLAY_CURSOR failed: " + ex.Message.Split('\n')[0] + ")"; }
            }
            return m;
        }

        private static Dictionary<string, long> Snapshot(ToolContext ctx)
        {
            try
            {
                var names = string.Join(", ", Statistics.Select(s => Lit(s.Name)));
                var r = ctx.Ide.Query($"select n.name, s.value from v$mystat s join v$statname n on n.statistic# = s.statistic# where n.name in ({names})", 50);
                return r.Rows.ToDictionary(x => x[0], x => long.TryParse(x[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0);
            }
            catch (OracleSqlException) { return null; }
        }

        /// <summary>
        /// Anonymous block that runs the statement with DBMS_SQL: binds, executes, fetches all rows of a query without
        /// transferring them, and prints rows, elapsed seconds and CPU centiseconds.
        /// </summary>
        private static string Block(string sql, Dictionary<string, JToken> binds, bool query)
        {
            var sb = new StringBuilder(
                "declare\n" +
                "  c integer := dbms_sql.open_cursor;\n" +
                "  n integer := 0;\n" +
                "  t0 timestamp with time zone;\n" +
                "  cpu0 number;\n" +
                "  d interval day(9) to second(6);\n" +
                "begin\n" +
                $"  dbms_sql.parse(c, {QuoteLiteral(sql)}, dbms_sql.native);\n");
            var code = StripStringsAndComments(sql);
            foreach (var b in binds)
            {
                if (!Regex.IsMatch(code, ":" + Regex.Escape(b.Key) + @"(?![\w$#])", RegexOptions.IgnoreCase)) continue;
                sb.Append($"  dbms_sql.bind_variable(c, {Lit(b.Key)}, {BindValue(b.Value)});\n");
            }
            sb.Append(
                "  t0 := systimestamp;\n" +
                "  cpu0 := dbms_utility.get_cpu_time;\n" +
                "  n := dbms_sql.execute(c);\n");
            if (query)
                sb.Append(
                    "  n := 0;\n" +
                    "  loop\n" +
                    "    exit when dbms_sql.fetch_rows(c) = 0;\n" +
                    "    n := n + 1;\n" +
                    "  end loop;\n");
            sb.Append(
                "  d := systimestamp - t0;\n" +
                "  dbms_output.put_line('PDA_MS rows=' || n || ' ela=' || to_char(extract(day from d) * 86400 + extract(hour from d) * 3600 + " +
                "extract(minute from d) * 60 + extract(second from d), 'FM999999990.000000', 'nls_numeric_characters=.,') || ' cpu=' || (dbms_utility.get_cpu_time - cpu0));\n" +
                "  dbms_sql.close_cursor(c);\n" +
                "exception\n" +
                "  when others then\n" +
                "    if dbms_sql.is_open(c) then dbms_sql.close_cursor(c); end if;\n" +
                "    raise;\n" +
                "end;");
            return sb.ToString();
        }

        /// <summary>A q'…' literal with a delimiter that does not occur in the text.</summary>
        private static string QuoteLiteral(string text)
        {
            foreach (var d in new[] { '~', '!', '^', '#', '|', '@', '%', '`' })
                if (!text.Contains(d + "'")) return "q'" + d + text + d + "'";
            return Lit(text);
        }

        private static string BindValue(JToken v)
        {
            switch (v?.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return v.ToString(Newtonsoft.Json.Formatting.None); // JSON number = valid Oracle numeric literal
                case null:
                case JTokenType.Null:
                    return "to_char(null)";
                default:
                    return Lit(v.Type == JTokenType.String ? (string)v : v.ToString());
            }
        }

        private static Dictionary<string, JToken> Binds(JToken token)
        {
            var result = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
            if (token is JObject o)
                foreach (var p in o.Properties())
                {
                    var name = p.Name.Trim().TrimStart(':');
                    if (Regex.IsMatch(name, @"^[\w$#]+$")) result[name] = p.Value;
                }
            return result;
        }

        /// <summary>Do two queries return the same rows? MINUS in both directions (as sets).</summary>
        private static string SameRows(ToolContext ctx, string a, string b, Dictionary<string, JToken> binds, long rowsA, long rowsB)
        {
            long? Count(string x, string y)
            {
                var res = ctx.Ide.Execute(Block($"select * from (\n{x}\n) minus select * from (\n{y}\n)", binds, true));
                if (!res.Ok) throw new OracleSqlException(res.ErrorCode, res.ErrorMessage);
                var m = Regex.Match(res.DbmsOutput ?? "", @"PDA_MS rows=(\d+)");
                return m.Success ? long.Parse(m.Groups[1].Value) : (long?)null;
            }
            try
            {
                var onlyA = Count(a, b);
                var onlyB = Count(b, a);
                if (onlyA == null || onlyB == null) return "could not be checked (no result)";
                if (onlyA == 0 && onlyB == 0)
                    return rowsA == rowsB
                        ? $"YES – both return the same {rowsA} rows (compared with MINUS in both directions; the order is not compared)"
                        : $"the same distinct rows, but a different number of rows ({rowsA} vs {rowsB}): duplicates differ";
                return $"NO – {onlyA} distinct rows only in statement 1, {onlyB} only in statement 2. The rewrite is not equivalent.";
            }
            catch (OracleSqlException ex)
            {
                return "could not be checked: " + ex.Message.Split('\n')[0] + " (MINUS needs the same column list and no LOB columns)";
            }
        }

        // ---------------------------------------------------------------- report

        private static string Report(List<Measurement> results, int runs, Access access, string sameRows)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < results.Count; i++)
            {
                var m = results[i];
                if (results.Count > 1) sb.Append($"Statement {i + 1}: {RunQueryTool.OneLine(m.Sql)}\n");
                if (m.Error != null)
                {
                    sb.Append("FAILED: ").Append(m.Error.Trim()).Append("\n\n");
                    continue;
                }
                sb.Append($"{Count(m.Rows)} {(m.Rows == 1 ? "row" : "rows")} {(m.Query ? "fetched" : "processed (rolled back)")}");
                if (m.Elapsed.Count > 0)
                {
                    sb.Append($", elapsed {Seconds(m.Elapsed.Last())}, CPU {Seconds(m.Cpu)}");
                    if (m.Elapsed.Count > 1) sb.Append($" (runs: {string.Join(", ", m.Elapsed.Select(Seconds))}; statistics of the last run)");
                }
                sb.Append('\n');
                if (m.Stats != null)
                    sb.Append("Session statistics: ").Append(string.Join(" | ", Statistics.Where(s => m.Stats[s.Name] != 0 || s.Name == "session logical reads" || s.Name == "physical reads")
                        .Select(s => $"{s.Label} {Count(m.Stats[s.Name])}"))).Append('\n');
                if (results.Count == 1 && m.Plan != null) sb.Append('\n').Append(PlanHeader).Append(m.Plan).Append('\n');
                sb.Append('\n');
            }

            if (results.Count == 2 && results.All(r => r.Error == null))
            {
                sb.Append("Comparison (statement 1 → statement 2):\n");
                Row(sb, "rows", results[0].Rows, results[1].Rows, false);
                Row(sb, "elapsed (s)", results[0].Elapsed.LastOrDefault(), results[1].Elapsed.LastOrDefault(), true);
                Row(sb, "CPU (s)", results[0].Cpu, results[1].Cpu, true);
                if (results[0].Stats != null && results[1].Stats != null)
                    foreach (var s in Statistics)
                        if (results[0].Stats[s.Name] != 0 || results[1].Stats[s.Name] != 0)
                            Row(sb, s.Label, results[0].Stats[s.Name], results[1].Stats[s.Name], false);
                if (sameRows != null) sb.Append("Same rows: ").Append(sameRows).Append('\n');
                if (runs == 1) sb.Append("Note: one run each – statement 2 may profit from blocks statement 1 read into the cache; runs=2 compares warm runs.\n");
                for (int i = 0; i < 2; i++)
                    if (results[i].Plan != null) sb.Append($"\n{PlanHeader.Replace("Actual execution plan", $"Actual execution plan of statement {i + 1}")}{results[i].Plan}\n");
            }

            if (!access.Plan && !access.Stats)
                sb.Append("\nNo actual execution plan and no session statistics: this user cannot read the V$ views (V$SQL, " +
                          "V$SQL_PLAN_STATISTICS_ALL, V$MYSTAT, V$STATNAME; SELECT_CATALOG_ROLE – the DBA can grant it). explain_plan shows the estimated plan.");
            else if (!access.Plan)
                sb.Append("\nNo actual execution plan: this user cannot read V$SQL / V$SQL_PLAN_STATISTICS_ALL (needs SELECT_CATALOG_ROLE or " +
                          "SELECT on these views – the DBA can grant it). explain_plan shows the estimated plan.");
            else if (!access.Stats)
                sb.Append("\nNo session statistics (logical / physical reads): this user cannot read V$MYSTAT / V$STATNAME (role PLUSTRACE or SELECT_CATALOG_ROLE).");
            return sb.ToString().TrimEnd();
        }

        private const string PlanHeader =
            "Actual execution plan (ALLSTATS LAST: Starts = executions of the step, E-Rows = estimated rows per start, A-Rows = actual rows " +
            "of all starts, Buffers = logical reads; A-Rows far from E-Rows × Starts means wrong estimates – check the statistics):\n";

        private static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

        private static string Seconds(double s) => s.ToString(s < 10 ? "0.000" : "0.0", CultureInfo.InvariantCulture) + " s";

        private static void Row(StringBuilder sb, string label, double a, double b, bool seconds)
        {
            string Fmt(double v) => seconds ? v.ToString("0.000", CultureInfo.InvariantCulture) : v.ToString("N0", CultureInfo.InvariantCulture);
            var change = a == b ? "same" : a == 0 ? "new" : ((b - a) / a * 100).ToString("+0.#;-0.#", CultureInfo.InvariantCulture) + "%";
            sb.Append($"  {label,-22} {Fmt(a),14} → {Fmt(b),-14} {change}\n");
        }
    }
}
