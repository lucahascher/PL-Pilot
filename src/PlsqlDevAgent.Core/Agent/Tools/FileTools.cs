using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Agent.Tools
{
    internal static class Workspace
    {
        public static string Root(ToolContext ctx)
        {
            if (!ctx.Config.HasWorkspace)
                throw new ToolArgumentException("No workspace folder is configured (Settings → Project folder).");
            return Path.GetFullPath(ctx.Config.WorkspaceDir).TrimEnd('\\');
        }

        /// <summary>Resolves a path relative to the workspace and refuses anything outside of it.</summary>
        public static string Resolve(ToolContext ctx, string path)
        {
            var root = Root(ctx);
            var full = Path.GetFullPath(Path.Combine(root, (path ?? ".").Trim().Replace('/', '\\')));
            if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ToolArgumentException($"Path '{path}' is outside of the workspace folder.");
            return full;
        }

        public static string Relative(ToolContext ctx, string full)
        {
            var root = Root(ctx);
            return full.Length > root.Length ? full.Substring(root.Length + 1) : ".";
        }

        private static readonly string[] SkipDirs = { ".git", ".svn", "node_modules", "bin", "obj", ".vs", ".idea" };

        public static IEnumerable<string> EnumerateFiles(string dir, string pattern, bool recursive)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, string.IsNullOrWhiteSpace(pattern) ? "*" : pattern); }
            catch { yield break; }
            foreach (var f in files) yield return f;
            if (!recursive) yield break;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(dir); }
            catch { yield break; }
            foreach (var d in dirs)
            {
                if (SkipDirs.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var f in EnumerateFiles(d, pattern, true)) yield return f;
            }
        }
    }

    /// <summary>Reads/writes text files keeping their encoding (UTF-8 with/without BOM or the ANSI code page).</summary>
    internal static class TextFile
    {
        public static string Read(string path, out Encoding encoding)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = new UTF8Encoding(true);
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = Encoding.Unicode;
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            try
            {
                var strict = new UTF8Encoding(false, true);
                var text = strict.GetString(bytes);
                encoding = new UTF8Encoding(false);
                return text;
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.Default; // ANSI code page, typical for older PL/SQL files
                return encoding.GetString(bytes);
            }
        }

        public static void Write(string path, string text, Encoding encoding)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        }

        public static bool LooksBinary(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var buf = new byte[Math.Min(8000, fs.Length)];
                    int n = fs.Read(buf, 0, buf.Length);
                    for (int i = 0; i < n; i++) if (buf[i] == 0) return true;
                }
            }
            catch { }
            return false;
        }
    }

    internal sealed class ListFilesTool : AgentTool
    {
        public override string Name => "list_files";
        public override string Description => "Lists files in the workspace folder (the user's project folder with SQL/PL/SQL scripts).";
        public override JObject Parameters => Schema(
            Opt("path", "string", "Folder relative to the workspace (default '.')."),
            Opt("pattern", "string", "File name pattern, e.g. '*.pkb' (default '*')."),
            Opt("recursive", "boolean", "Include sub folders (default true)."));
        public override bool NeedsWorkspace => true;
        public override string Summarize(JObject args) => $"{Str(args, "path", ".")} {Str(args, "pattern", "")}".Trim();

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var dir = Workspace.Resolve(ctx, Str(args, "path", "."));
            if (!Directory.Exists(dir)) return Task.FromResult("Folder not found.");
            var files = Workspace.EnumerateFiles(dir, Str(args, "pattern", "*"), Bool(args, "recursive", true)).Take(1001).ToList();
            var sb = new StringBuilder();
            foreach (var f in files.Take(1000))
            {
                long size = 0;
                try { size = new FileInfo(f).Length; } catch { }
                sb.Append(Workspace.Relative(ctx, f)).Append("  (").Append(size).Append(" bytes)\n");
            }
            if (files.Count > 1000) sb.Append("... more files exist, narrow the pattern.\n");
            return Task.FromResult(files.Count == 0 ? "No files found." : sb.ToString());
        }
    }

    internal sealed class ReadFileTool : AgentTool
    {
        public override string Name => "read_file";
        public override string Description => "Reads a text file from the workspace folder.";
        public override JObject Parameters => Schema(
            Req("path", "string", "File path relative to the workspace."),
            Opt("with_line_numbers", "boolean", "Prefix lines with numbers (default false)."),
            Opt("start_line", "integer", "First line (optional)."),
            Opt("end_line", "integer", "Last line (optional)."));
        public override bool NeedsWorkspace => true;
        public override string Summarize(JObject args) => Str(args, "path");

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var path = Workspace.Resolve(ctx, ReqStr(args, "path"));
            if (!File.Exists(path)) return Task.FromResult("File not found.");
            if (TextFile.LooksBinary(path)) return Task.FromResult("This is a binary file.");
            var text = TextFile.Read(path, out _);
            int start = Int(args, "start_line", 0), end = Int(args, "end_line", 0);
            var body = Bool(args, "with_line_numbers", false)
                ? TextEdit.WithLineNumbers(text, start, end)
                : (start > 0 || end > 0 ? TextEdit.Lines(text, start, end) : text.Replace("\r\n", "\n"));
            return Task.FromResult(Db.Truncate(body, ctx.Config.MaxToolOutputChars));
        }
    }

    internal sealed class SearchFilesTool : AgentTool
    {
        public override string Name => "search_files";
        public override string Description => "Searches text files in the workspace with a regular expression (case-insensitive). Returns file:line: text.";
        public override JObject Parameters => Schema(
            Req("regex", "string", ".NET regular expression."),
            Opt("path", "string", "Folder relative to the workspace (default '.')."),
            Opt("pattern", "string", "File name pattern (default '*')."),
            Opt("max_results", "integer", "Default 100."));
        public override bool NeedsWorkspace => true;
        public override string Summarize(JObject args) => "/" + Str(args, "regex") + "/";

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            Regex re;
            try { re = new Regex(ReqStr(args, "regex"), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex) { return Task.FromResult("Invalid regex: " + ex.Message); }
            var dir = Workspace.Resolve(ctx, Str(args, "path", "."));
            int max = Math.Min(Int(args, "max_results", 100), 500);
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var f in Workspace.EnumerateFiles(dir, Str(args, "pattern", "*"), true))
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                if (new FileInfo(f).Length > 5_000_000 || TextFile.LooksBinary(f)) continue;
                var lines = TextFile.Read(f, out _).Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!re.IsMatch(lines[i])) continue;
                    var line = lines[i].Trim();
                    if (line.Length > 300) line = line.Substring(0, 300) + "…";
                    sb.Append(Workspace.Relative(ctx, f)).Append(':').Append(i + 1).Append(": ").Append(line).Append('\n');
                    if (++hits >= max) { sb.Append("... more matches exist.\n"); return Task.FromResult(sb.ToString()); }
                }
            }
            return Task.FromResult(hits == 0 ? "No matches." : sb.ToString());
        }
    }

    internal sealed class WriteFileTool : AgentTool
    {
        public override string Name => "write_file";
        public override string Description => "Creates or overwrites a text file in the workspace folder. The user reviews it first.";
        public override JObject Parameters => Schema(
            Req("path", "string", "File path relative to the workspace."),
            Req("content", "string", "Complete file content."));
        public override bool Mutating => true;
        public override bool NeedsWorkspace => true;
        public override string Summarize(JObject args) => Str(args, "path");

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var path = Workspace.Resolve(ctx, ReqStr(args, "path"));
            var content = ReqStr(args, "content");
            string before = null;
            Encoding enc = null;
            if (File.Exists(path)) before = TextFile.Read(path, out enc);
            if (before != null && before.Contains("\r\n")) content = content.Replace("\r\n", "\n").Replace("\n", "\r\n");
            else if (before == null) content = content.Replace("\r\n", "\n").Replace("\n", "\r\n");
            return await EditFileTool.Commit(ctx, path, before, content, enc);
        }
    }

    internal sealed class EditFileTool : AgentTool
    {
        public override string Name => "edit_file";
        public override string Description =>
            "Edits a text file in the workspace by replacing old_string with new_string (exact match, unique unless replace_all).";
        public override JObject Parameters => Schema(
            Req("path", "string", "File path relative to the workspace."),
            Req("old_string", "string", "Exact text to replace."),
            Req("new_string", "string", "Replacement text."),
            Opt("replace_all", "boolean", "Replace every occurrence (default false)."));
        public override bool Mutating => true;
        public override bool NeedsWorkspace => true;
        public override string Summarize(JObject args) => Str(args, "path");

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var path = Workspace.Resolve(ctx, ReqStr(args, "path"));
            if (!File.Exists(path)) return "File not found. Use write_file to create it.";
            var before = TextFile.Read(path, out var enc);
            var after = TextEdit.Apply(before, ReqStr(args, "old_string"), ReqStr(args, "new_string"), Bool(args, "replace_all", false));
            return await Commit(ctx, path, before, after, enc);
        }

        internal static async Task<string> Commit(ToolContext ctx, string path, string before, string after, Encoding enc)
        {
            var rel = Workspace.Relative(ctx, path);
            if (!ctx.Config.AutoApproveFileWrites)
            {
                var approval = await ctx.ApproveAsync(new ApprovalRequest
                {
                    Category = "file",
                    Title = L.T(before == null ? "Create file {0}" : "Modify file {0}", rel),
                    Diff = before == null ? null : LineDiff.Unified(before, after),
                    Detail = before == null ? after.Replace("\r\n", "\n") : null,
                    DiffLeftCaption = before == null ? null : L.T("{0} (current)", rel),
                    DiffLeftText = before,
                    DiffRightCaption = before == null ? null : L.T("{0} (proposed)", rel),
                    DiffRightText = before == null ? null : after,
                });
                if (!approval.Approved) return approval.RejectionMessage;
            }
            TextFile.Write(path, after, enc);
            var undoId = EditJournal.Add(new EditJournal.Record { Kind = "file", Label = rel, Path = path, Before = before, After = after, Encoding = enc });
            ctx.AddUiMeta("undo", new JObject { ["id"] = undoId, ["label"] = rel });
            ctx.Changed(new ChangeRecord { Kind = ChangeRecord.File, Label = rel, Path = path, Before = before, After = after, Encoding = enc, UndoId = undoId });
            return before == null ? $"Created {rel}." : $"Updated {rel}:\n" + LineDiff.Unified(before, after, 1);
        }
    }

    internal sealed class OpenFileInIdeTool : AgentTool
    {
        public override string Name => "open_file_in_ide";
        public override string Description => "Opens a workspace file in PL/SQL Developer (window type chosen by extension). It becomes the active editor.";
        public override JObject Parameters => Schema(Req("path", "string", "File path relative to the workspace."));
        public override bool NeedsWorkspace => true;
        public override string Summarize(JObject args) => Str(args, "path");

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var path = Workspace.Resolve(ctx, ReqStr(args, "path"));
            if (!File.Exists(path)) return Task.FromResult("File not found.");
            return Task.FromResult(ctx.Ide.OpenFile(path) ? $"Opened {Workspace.Relative(ctx, path)}." : "PL/SQL Developer could not open the file.");
        }
    }
}
