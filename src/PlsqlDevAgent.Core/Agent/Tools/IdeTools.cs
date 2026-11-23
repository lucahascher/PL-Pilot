using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Agent.Tools
{
    internal sealed class GetIdeContextTool : AgentTool
    {
        public override string Name => "get_ide_context";
        public override string Description =>
            "Returns the current PL/SQL Developer state: database connection, active window type, the object or file " +
            "it shows, cursor position and whether it has unsaved changes.";
        public override JObject Parameters => Schema();

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var c = ctx.Ide.GetContext();
            var schema = c.Connected ? ctx.CurrentSchema : "";
            return Task.FromResult(
                $"Connection: {c.ConnectionLabel}" + (c.Connected ? $" (current schema {schema})" : "") + "\n" +
                (ctx.ExcludedWindowMessage(c) != null
                    ? "Active window: excluded by the user (not part of the request; do not read or change it)"
                    : $"Active window: {c.WindowLabel}"));
        }
    }

    internal sealed class GetEditorTextTool : AgentTool
    {
        public override string Name => "get_editor_text";
        public override string Description =>
            "Returns the text of the active editor window (SQL, Program, Test or Command window). " +
            "Use line numbers when you need to reference compile errors; never copy the line number prefixes into edits.";
        public override JObject Parameters => Schema(
            Opt("with_line_numbers", "boolean", "Prefix each line with its line number (default false)."),
            Opt("start_line", "integer", "First line to return (1-based, optional)."),
            Opt("end_line", "integer", "Last line to return (optional)."));
        public override string Summarize(JObject args) => Int(args, "start_line", 0) > 0 ? $"lines {Int(args, "start_line", 0)}-{Int(args, "end_line", 0)}" : "";

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var c = ctx.Ide.GetContext();
            if (ctx.ExcludedWindowMessage(c) is string excluded) return Task.FromResult(excluded);
            var text = ctx.Ide.GetEditorText();
            if (string.IsNullOrEmpty(text)) return Task.FromResult($"[{c.WindowLabel}]\n(the editor is empty)");
            int start = Int(args, "start_line", 0), end = Int(args, "end_line", 0);
            var body = Bool(args, "with_line_numbers", false)
                ? TextEdit.WithLineNumbers(text, start, end)
                : (start > 0 || end > 0 ? TextEdit.Lines(text, start, end) : text.Replace("\r\n", "\n"));
            int lineCount = text.Split('\n').Length;
            return Task.FromResult($"[{c.WindowLabel}; {lineCount} lines]\n{body}");
        }
    }

    internal sealed class GetSelectedTextTool : AgentTool
    {
        public override string Name => "get_selected_text";
        public override string Description => "Returns the text currently selected by the user in the active editor (empty if nothing is selected).";
        public override JObject Parameters => Schema();

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            if (ctx.ExcludedWindowMessage(ctx.Ide.GetContext()) is string excluded) return Task.FromResult(excluded);
            var sel = ctx.Ide.GetSelectedText();
            return Task.FromResult(string.IsNullOrEmpty(sel) ? "(nothing selected)" : sel.Replace("\r\n", "\n"));
        }
    }

    internal sealed class EditEditorTool : AgentTool
    {
        public override string Name => "edit_editor";
        public override string Description =>
            "Edits the text in the active editor window by replacing old_string with new_string. old_string must match " +
            "the current editor text exactly (copy it from get_editor_text, without line numbers) and be unique unless " +
            "replace_all is true. The user reviews a diff before it is applied. This changes only the editor, not the " +
            "database: compile afterwards with compile_plsql if needed.";
        public override JObject Parameters => Schema(
            Req("old_string", "string", "Exact text to replace (include enough lines to be unique)."),
            Req("new_string", "string", "Replacement text."),
            Opt("replace_all", "boolean", "Replace every occurrence (default false)."));
        public override bool Mutating => true;

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            if (ctx.ExcludedWindowMessage(ctx.Ide.GetContext()) is string excluded) return excluded;
            var before = ctx.Ide.GetEditorText();
            var after = TextEdit.Apply(before, ReqStr(args, "old_string"), ReqStr(args, "new_string"), Bool(args, "replace_all", false));
            var (proceed, localNote) = await LocalCheck(ctx);
            if (!proceed) return localNote;
            var result = await ApplyToEditor(ctx, before, after, "Edit editor text");
            return localNote == null ? result : result.TrimEnd() + "\n\n" + localNote;
        }

        /// <summary>
        /// A window showing a database object (not a file) is changed to be compiled into the database: for objects that
        /// also have local source files the user decides once per chat where changes go (see <see cref="LocalChanges"/>).
        /// </summary>
        internal static Task<(bool Proceed, string Message)> LocalCheck(ToolContext ctx)
        {
            var c = ctx.Ide.GetContext();
            if (string.IsNullOrEmpty(c.ObjectName) || !string.IsNullOrEmpty(c.FileName)) return Task.FromResult<(bool, string)>((true, null));
            return LocalChanges.CheckAsync(ctx, new[] { ((c.ObjectType ?? "").ToUpperInvariant(), (c.ObjectOwner ?? "").ToUpperInvariant(), c.ObjectName.ToUpperInvariant()) });
        }

        internal static async Task<string> ApplyToEditor(ToolContext ctx, string before, string after, string title)
        {
            var c = ctx.Ide.GetContext();
            var approval = ctx.Config.AutoApproveEditorEdits
                ? ApprovalResult.Yes()
                : await ctx.ApproveAsync(new ApprovalRequest
                {
                    Category = "editor",
                    Title = $"{L.T(title)} — {c.WindowNameUi}",
                    Diff = LineDiff.Unified(before, after),
                    DiffLeftCaption = L.T("Current editor text"),
                    DiffLeftText = before,
                    DiffRightCaption = L.T("Proposed by agent"),
                    DiffRightText = after,
                });
            if (!approval.Approved) return approval.RejectionMessage;
            ctx.Ide.ReplaceEditorText(before, after);
            var undoId = EditJournal.Add(new EditJournal.Record { Kind = "editor", Label = c.WindowLabel, Before = before, After = after });
            ctx.AddUiMeta("undo", new JObject { ["id"] = undoId, ["label"] = "editor" });
            ctx.Changed(new ChangeRecord { Kind = ChangeRecord.Editor, Label = c.WindowNameUi, Before = before, After = after, UndoId = undoId });
            return "Editor updated:\n" + LineDiff.Unified(before, after, 1);
        }
    }

    internal sealed class SetEditorTextTool : AgentTool
    {
        public override string Name => "set_editor_text";
        public override string Description =>
            "Replaces the COMPLETE text of the active editor window. Prefer edit_editor for partial changes; use this " +
            "only for an empty editor or a full rewrite. The user reviews a diff first.";
        public override JObject Parameters => Schema(Req("text", "string", "The complete new editor content."));
        public override bool Mutating => true;

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            if (ctx.ExcludedWindowMessage(ctx.Ide.GetContext()) is string excluded) return excluded;
            var before = ctx.Ide.GetEditorText();
            var after = ReqStr(args, "text");
            if (before.Contains("\r\n")) after = after.Replace("\r\n", "\n").Replace("\n", "\r\n");
            var (proceed, localNote) = await EditEditorTool.LocalCheck(ctx);
            if (!proceed) return localNote;
            var result = await EditEditorTool.ApplyToEditor(ctx, before, after, "Replace editor text");
            return localNote == null ? result : result.TrimEnd() + "\n\n" + localNote;
        }
    }

    internal sealed class OpenNewWindowTool : AgentTool
    {
        public override string Name => "open_new_window";
        public override string Description =>
            "Opens a new PL/SQL Developer window containing the given text, e.g. a script for the user to review and " +
            "run, or a Test window with a PL/SQL block. Nothing is executed.";
        public override JObject Parameters => Schema(
            Req("text", "string", "Content of the new window."),
            Opt("window_type", "string", "Kind of window (default sql).", "sql", "test", "command"));
        public override string Summarize(JObject args) => Str(args, "window_type", "sql") + " window";

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var type = Str(args, "window_type", "sql");
            var wt = type == "test" ? IdeWindowType.Test : type == "command" ? IdeWindowType.Command : IdeWindowType.Sql;
            ctx.Ide.NewWindow(wt, ReqStr(args, "text"));
            return Task.FromResult($"Opened a new {wt} window. Note: it is now the active window.");
        }
    }

    internal sealed class OpenObjectTool : AgentTool
    {
        public override string Name => "open_object";
        public override string Description =>
            "Opens a database object in PL/SQL Developer (e.g. a package in a Program window for editing). " +
            "Afterwards it is the active editor, so get_editor_text / edit_editor work on it.";
        public override JObject Parameters => Schema(
            Req("name", "string", "Object name, optionally OWNER.NAME."),
            Req("object_type", "string", "Object type, e.g. PACKAGE, PACKAGE BODY, PROCEDURE, FUNCTION, TRIGGER, TYPE, VIEW, TABLE."),
            Opt("action", "string", "What to do (default EDIT).", "EDIT", "VIEW", "EDITSPECANDBODY", "VIEWSPECANDBODY", "QUERYDATA", "TEST"));
        public override string Summarize(JObject args) => $"{Str(args, "object_type")} {Str(args, "name")}";

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            SqlUtil.SplitName(ReqStr(args, "name"), out var owner, out var name);
            owner = owner ?? ctx.CurrentSchema;
            var type = ReqStr(args, "object_type").ToUpperInvariant();
            var action = Str(args, "action", "EDIT").ToUpperInvariant();
            var ok = ctx.Ide.ObjectAction(action, type, owner, name);
            if (!ok)
                return Task.FromResult($"PL/SQL Developer could not {action} {type} {owner}.{name}. Check the name and type (list_objects).");
            return Task.FromResult($"Opened {type} {owner}.{name} ({action})." + StaleWarning(ctx, type, owner, name));
        }

        /// <summary>
        /// If the object was already open (e.g. restored from the last session or edited by the user), PL/SQL
        /// Developer just activates that window – its text can differ from what is compiled in the database.
        /// </summary>
        private static string StaleWarning(ToolContext ctx, string type, string owner, string name)
        {
            try
            {
                if (type != "PACKAGE BODY" && type != "PROCEDURE" && type != "FUNCTION" && type != "TRIGGER" && type != "TYPE BODY")
                    return "";
                var c = ctx.Ide.GetContext();
                if (!c.HasEditor) return "";
                var editor = Normalize(ctx.Ide.GetEditorText());
                var src = Db.ReadSource(ctx, owner, name, type);
                if (src.FirstLine == 0) return "";
                var db = Normalize(src.Text);
                if (editor == db) return " The editor content matches the version compiled in the database.";
                return "\nWARNING: the text in this editor window DIFFERS from the version compiled in the database" +
                       (c.Modified ? " (the window has unsaved changes)" : " (the window was probably already open with an older or newer version)") +
                       ". Do not compile the editor text blindly: compare it with get_object_source and ask the user which version is current.";
            }
            catch (System.Exception ex)
            {
                Util.Log.Warn("Stale check failed: " + ex.Message);
                return "";
            }
        }

        private static string Normalize(string s)
        {
            var lines = (s ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
            var text = string.Join("\n", lines).Trim();
            return System.Text.RegularExpressions.Regex.Replace(text, @"^create\s+(or\s+replace\s+)?((non)?editionable\s+)?", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).TrimEnd('/', '\n', ' ').ToUpperInvariant();
        }
    }
}
