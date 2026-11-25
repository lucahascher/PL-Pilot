using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.Util;
using static PlsqlDevAgent.Agent.SqlUtil;

namespace PlsqlDevAgent.Agent.Tools
{
    internal sealed class ExplainPlanTool : AgentTool
    {
        public override string Name => "explain_plan";
        public override string Description =>
            "Shows the optimizer's execution plan (EXPLAIN PLAN + DBMS_XPLAN) for a SELECT, INSERT, UPDATE, DELETE or MERGE " +
            "statement WITHOUT executing it: operations, access paths, indexes used, join order, estimated rows and cost. " +
            "Use it to analyse and tune performance. Bind variables (:name) are allowed.";
        public override JObject Parameters => Schema(
            Req("sql", "string", "The statement to explain (without trailing semicolon)."),
            Opt("format", "string", "DBMS_XPLAN format (default TYPICAL).", "BASIC", "TYPICAL", "ALL"));
        public override string Summarize(JObject args) => RunQueryTool.OneLine(Str(args, "sql"));

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var sql = PrepareStatement(ReqStr(args, "sql"));
            var original = sql;
            List<string> dbaViews = null;
            var dbaIde = ctx.DbaViewsIde;
            if (dbaIde != null) sql = dbaIde.Rewrite(sql, out dbaViews); // explain what run_query would execute
            var kw = FirstKeyword(sql);
            if (!new[] { "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "MERGE" }.Contains(kw))
                return Task.FromResult("Not executed: explain_plan supports SELECT, WITH, INSERT, UPDATE, DELETE and MERGE statements.");
            var format = (Str(args, "format", "TYPICAL") ?? "TYPICAL").ToUpperInvariant();
            if (format != "BASIC" && format != "ALL") format = "TYPICAL";
            var id = "PDA" + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();

            // EXPLAIN PLAN only writes into PLAN_TABLE (a session private temporary table); the statement is not run.
            // Those rows open a transaction: if none was open before, it is rolled back after reading the plan.
            bool cleanUp = ctx.Ide.UsesOwnSession && ctx.Ide.PendingTransaction == null;
            try
            {
                return Task.FromResult(Explain(ctx, sql, original, id, format, dbaIde, dbaViews));
            }
            finally
            {
                if (cleanUp && ctx.Ide.PendingTransaction != null) ctx.Ide.Execute("rollback");
            }
        }

        private static string Explain(ToolContext ctx, string sql, string original, string id, string format, DbaViewsIde dbaIde, List<string> dbaViews)
        {
            var res = ctx.Ide.Execute($"explain plan set statement_id = '{id}' for {ctx.Serial(sql)}");
            if (!res.Ok && dbaIde != null && !ReferenceEquals(sql, original) && DbaViewsIde.IsFallbackError(res.ErrorCode))
            {
                dbaIde.MarkUnreadable(dbaViews, res.ErrorCode, sql);
                res = ctx.Ide.Execute($"explain plan set statement_id = '{id}' for {ctx.Serial(original)}");
            }
            if (!res.Ok) return "EXPLAIN PLAN failed: " + res.ErrorMessage;
            var plan = ctx.Ide.Query($"select plan_table_output from table(dbms_xplan.display('PLAN_TABLE', '{id}', '{format}'))", 1000);
            var text = string.Join("\n", plan.Rows.Select(r => r[0]));
            return Db.Truncate(text.Length == 0 ? "(no plan returned)" : text, ctx.Config.ToolOutputLimit);
        }
    }

    internal sealed class GetResultGridTool : AgentTool
    {
        public override string Name => "get_result_grid";
        public override string Description =>
            "Returns the rows currently displayed in the result grid of the SQL or Test window of this request (the window " +
            "the user had active when sending it), i.e. the result of the query the user ran in PL/SQL Developer, together " +
            "with the window's SQL text. Use it when the user asks about 'these results' or 'my query output'.";
        public override JObject Parameters => Schema(Opt("max_rows", "integer", "Maximum rows to return (default 50)."));

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var c = ctx.EditorContext(out var window);
            if (ctx.ExcludedWindowMessage(c) is string excluded) return Task.FromResult(excluded);
            if (c.WindowType != IdeWindowType.Sql && c.WindowType != IdeWindowType.Test)
                return Task.FromResult($"The window of this request is not a SQL or Test window ({c.WindowLabel}).");
            var grid = ctx.Ide.GetResultGrid(Math.Max(1, Math.Min(Int(args, "max_rows", 50), 500)), window);
            var sb = new StringBuilder();
            var sqlText = ctx.Ide.GetEditorText(window);
            sb.Append("SQL in the window:\n").Append(Db.Truncate(sqlText.Replace("\r\n", "\n").Trim(), 4000)).Append("\n\n");
            if (grid.Columns.Count == 0 && grid.Rows.Count == 0)
                sb.Append("The result grid is empty (the query has not been executed or returned no rows).");
            else
                sb.Append("Result grid:\n").Append(FormatResult(grid, ctx.Config.ToolOutputLimit));
            return Task.FromResult(sb.ToString());
        }
    }

    internal sealed class ListBackupsTool : AgentTool
    {
        public override string Name => "list_backups";
        public override string Description =>
            "Lists the local backups of database source code. Before the agent replaces or drops a PL/SQL unit or view, " +
            "the previous version is saved automatically. Use restore_backup to bring one back.";
        public override JObject Parameters => Schema(
            Opt("name", "string", "Filter by object name (substring)."),
            Opt("all_connections", "boolean", "Include backups from other connections (default false)."));
        public override string Summarize(JObject args) => Str(args, "name", "");

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var list = BackupStore.List(Bool(args, "all_connections", false) ? null : ctx.ConnectionLabel, Str(args, "name"), 50);
            if (list.Count == 0) return Task.FromResult("No backups found.");
            return Task.FromResult("Backups (newest first):\n" + string.Join("\n", list.Select(e => e.ToString())));
        }
    }

    internal sealed class RestoreBackupTool : AgentTool
    {
        public override string Name => "restore_backup";
        public override string Description =>
            "Restores a backed-up version of a PL/SQL unit or view into the database (compiles it). The current version " +
            "is backed up first. The user must approve.";
        public override JObject Parameters => Schema(Req("id", "string", "Backup id from list_backups."));
        public override bool Mutating => true;
        public override string Summarize(JObject args) => Str(args, "id");

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var entry = BackupStore.Get(ReqStr(args, "id"));
            if (entry == null) return "Backup not found. Use list_backups.";
            if (!string.Equals(entry.Connection, ctx.ConnectionLabel, StringComparison.OrdinalIgnoreCase))
                return $"This backup belongs to connection {entry.Connection}, but the agent is connected to {ctx.ConnectionLabel}. Not restored.";
            var source = BackupStore.ReadSource(entry);
            var current = Db.CurrentSource(ctx, entry.ObjectType, entry.Owner, entry.Name) ?? "";
            var approval = await ctx.ApproveAsync(new ApprovalRequest
            {
                Category = "sql",
                Title = L.T("Restore {0} from backup {1} ({2})", entry.Label, entry.Id, entry.Time.ToString("yyyy-MM-dd HH:mm")),
                Diff = LineDiff.Unified(current, source),
                DiffLeftCaption = L.T("Current database version"),
                DiffLeftText = current,
                DiffRightCaption = L.T("Backup {0}", entry.Id),
                DiffRightText = source,
            });
            if (!approval.Approved) return approval.RejectionMessage;
            return RestoreNow(ctx, entry);
        }

        /// <summary>Restores without asking (the caller handled approval).</summary>
        internal static string RestoreNow(ToolContext ctx, BackupEntry entry)
        {
            var source = BackupStore.ReadSource(entry);
            return "Restored from backup " + entry.Id + ":\n" + CompilePlsqlTool.Compile(ctx, SplitOnSlash(source), "restore");
        }
    }
}
