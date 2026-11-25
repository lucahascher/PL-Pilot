using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.Util;
using static PlsqlDevAgent.Agent.SqlUtil;

namespace PlsqlDevAgent.Agent.Tools
{
    /// <summary>
    /// Runs the read-only queries the model writes (run_query, SELECT in execute_sql). A statement cannot be stopped
    /// once it runs, and it keeps the agent's one database session busy, so:
    /// <list type="bullet">
    /// <item>Oracle's estimate (EXPLAIN PLAN, not executed) is read first; above the setting ConfirmSlowQuerySeconds
    /// the user is asked;</item>
    /// <item>the row limit is passed into the query, so Oracle can stop early (a sorted result: top-n sort);</item>
    /// <item>an identical query that is already running for a parallel helper is not run twice;</item>
    /// <item>a runtime of a second or more is reported to the model.</item>
    /// </list>
    /// </summary>
    internal static class QueryRunner
    {
        /// <summary>Errors that only the row limit wrapper can cause (e.g. ORA-00918 for two columns with the same name).</summary>
        private static readonly int[] WrapperErrors = { 918, 907, 933, 936, 32034 };

        public static async Task<string> RunAsync(ToolContext ctx, string sql, int maxRows)
        {
            var wrapped = WithRowLimit(sql, maxRows + 1);
            int confirmAt = ctx.Config.ConfirmSlowQuerySeconds;
            // the identical query of a parallel helper already runs (it was estimated and, if needed, approved): wait
            // for it instead of queueing an estimate behind it in the one session, which would come too late to share
            if (confirmAt > 0 && !ctx.IsQueryRunning(wrapped, maxRows))
            {
                var estimate = await Task.Run(() => Estimate(ctx, sql)).ConfigureAwait(false);
                if (estimate != null && estimate.Seconds >= confirmAt)
                {
                    var about = estimate.Describe();
                    if (ctx.IsHelper)
                        return $"Not executed: Oracle estimates {about} for this query, and helper agents do not run slow queries. " +
                               "Narrow it (selective filters, fewer rows or tables) or report it so the main agent can run it.";
                    var approval = await ctx.ApproveAsync(new ApprovalRequest
                    {
                        Category = "slow-query",
                        Title = L.T("Run a slow query? Oracle estimates {0}", estimate.DescribeForUser()),
                        Detail = sql.Replace("\r\n", "\n"),
                        Warning = L.T("Once it runs, the plug-in cannot stop the statement: the agent's database session stays busy until it finishes."),
                    }).ConfigureAwait(false);
                    if (!approval.Approved)
                        return $"Not executed: Oracle estimates {about} for this query. " + approval.RejectionMessage +
                               " Narrow the query (selective filters, fewer rows or tables) before trying again.";
                }
            }

            async Task<(QueryResult Result, bool Shared)> Run(string query) =>
                await Task.Run(() => { var res = ctx.SharedQuery(query, maxRows, out var s); return (res, s); }).ConfigureAwait(false);

            var watch = Stopwatch.StartNew();
            (QueryResult Result, bool Shared) r;
            try
            {
                try { r = await Run(wrapped).ConfigureAwait(false); }
                catch (OracleSqlException ex) when (!ReferenceEquals(wrapped, sql) && WrapperErrors.Contains(ex.Code))
                {
                    Log.Info($"Row limit wrapper not possible (ORA-{ex.Code:00000}), running the query as written");
                    r = await Run(sql).ConfigureAwait(false);
                }
            }
            catch (OracleSqlException ex)
            {
                return "Query failed: " + ex.Message;
            }
            var text = FormatResult(r.Result, ctx.Config.ToolOutputLimit);
            var seconds = watch.Elapsed.TotalSeconds;
            if (r.Shared) text += "\n(Same query as a parallel helper agent: its result is shared, the query ran once.)";
            if (seconds >= 1) text += $"\n(The query took {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s.)";
            return text;
        }

        /// <summary>
        /// The query wrapped in <c>select * from (…) where rownum &lt;= n</c>: Oracle knows that only the first rows
        /// are fetched (top-n sort instead of sorting everything). Not for PL/SQL in the WITH clause or FOR UPDATE.
        /// </summary>
        public static string WithRowLimit(string sql, int rows)
        {
            var stripped = StripStringsAndComments(sql);
            if (Regex.IsMatch(stripped, @"\bFOR\s+UPDATE\b|^\s*WITH\s+(FUNCTION|PROCEDURE)\b", RegexOptions.IgnoreCase)) return sql;
            return $"select * from (\n{sql}\n) where rownum <= {rows}";
        }

        /// <summary>Oracle's estimate for a statement (from PLAN_TABLE row 0).</summary>
        public sealed class QueryEstimate
        {
            public long Seconds, Cost, Rows;

            public string Describe() =>
                $"about {Duration(Seconds)} (cost {Cost.ToString("N0", CultureInfo.InvariantCulture)}, {Rows.ToString("N0", CultureInfo.InvariantCulture)} rows)";

            public string DescribeForUser() => L.T("about {0}", Duration(Seconds));

            public static string Duration(long seconds) =>
                seconds < 120 ? seconds + " s" :
                seconds < 7200 ? (seconds + 30) / 60 + " min" :
                $"{seconds / 3600} h {(seconds % 3600 + 30) / 60} min";
        }

        /// <summary>
        /// EXPLAIN PLAN in an autonomous transaction: the statement is not run, and the PLAN_TABLE rows are rolled back
        /// there, so neither the agent's nor the user's transaction is touched (also in PL/SQL Developer's main
        /// session). Null if Oracle cannot explain it (e.g. dictionary views without privileges, ORA-01039).
        /// </summary>
        public static QueryEstimate Estimate(ToolContext ctx, string sql)
        {
            try
            {
                if (ctx.DbaViewsIde is DbaViewsIde dba) sql = dba.Rewrite(sql, out _); // estimate what will run
                sql = ctx.Serial(sql);
                if (sql.Length > 30000) return null;
                var id = "PDA" + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();
                var block =
                    "declare\n  pragma autonomous_transaction;\n  t number; c number; r number;\nbegin\n" +
                    $"  execute immediate 'explain plan set statement_id = ''{id}'' for ' || '{sql.Replace("'", "''")}';\n" +
                    $"  select time, cost, cardinality into t, c, r from plan_table where statement_id = '{id}' and id = 0;\n" +
                    "  rollback;\n" +
                    "  dbms_output.put_line('PDA_ESTIMATE ' || t || '|' || c || '|' || r);\n" +
                    "exception when others then rollback; dbms_output.put_line('PDA_ESTIMATE_FAILED ' || sqlcode);\nend;";
                var res = ctx.Ide.Execute(block);
                var m = Regex.Match(res.DbmsOutput ?? "", @"PDA_ESTIMATE (\d*)\|(\d*)\|(\d*)");
                if (!res.Ok || !m.Success || m.Groups[1].Value.Length == 0) return null;
                long Num(int g) => long.TryParse(m.Groups[g].Value, out var v) ? v : 0;
                return new QueryEstimate { Seconds = Num(1), Cost = Num(2), Rows = Num(3) };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("Query estimate failed: " + ex.Message);
                return null;
            }
        }
    }
}
