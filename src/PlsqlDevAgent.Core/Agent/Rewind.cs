using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PlsqlDevAgent.Agent.Tools;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Agent
{
    /// <summary>One change the agent (or an Insert button in the chat) made, with what is needed to undo it.</summary>
    public sealed class ChangeRecord
    {
        public const string Editor = "editor", File = "file", Database = "db", NotUndoable = "sql";

        public int Seq;
        /// <summary>User message the change belongs to.</summary>
        public string Checkpoint;
        /// <summary>Tool call that made the change (null: Insert button in the chat).</summary>
        public string CallId;
        public string Kind;
        public string Label;
        /// <summary>Editor/file text or database source before and after the change (null: file / object did not exist).</summary>
        public string Before, After;
        public string Path;
        public Encoding Encoding;
        /// <summary>Id of the single "Revert" button (EditJournal) or of the database backup.</summary>
        public string UndoId, BackupId;
        public string Connection, ObjectType, Owner, Name;
        public volatile bool Reverted;
    }

    /// <summary>
    /// The changes of one chat in the order they were made, so they can be undone back to the start of a user
    /// message or to any tool call ("rewind"). Kept in memory until PL/SQL Developer is closed.
    /// </summary>
    public sealed class ChangeJournal
    {
        private static readonly ConcurrentDictionary<string, ChangeJournal> ByChat = new ConcurrentDictionary<string, ChangeJournal>();

        public static ChangeJournal For(string chatId) => ByChat.GetOrAdd(chatId ?? "", _ => new ChangeJournal());

        private readonly object _lock = new object();
        private readonly List<ChangeRecord> _records = new List<ChangeRecord>();
        private readonly Dictionary<string, int> _checkpointStart = new Dictionary<string, int>();
        private int _nextSeq = 1;

        /// <summary>Marks where the changes of a user message begin.</summary>
        public void StartCheckpoint(string checkpoint)
        {
            if (checkpoint == null) return;
            lock (_lock) if (!_checkpointStart.ContainsKey(checkpoint)) _checkpointStart[checkpoint] = _nextSeq;
        }

        public void Add(ChangeRecord r)
        {
            lock (_lock)
            {
                r.Seq = _nextSeq++;
                _records.Add(r);
            }
        }

        /// <summary>First change position of a user message (null: unknown, e.g. a chat from an earlier IDE session).</summary>
        public int? StartOfCheckpoint(string checkpoint)
        {
            lock (_lock) return checkpoint != null && _checkpointStart.TryGetValue(checkpoint, out var s) ? s : (int?)null;
        }

        /// <summary>
        /// First change of a tool call (null: the call changed nothing that is recorded). The changes of one call are
        /// consecutive; if an id was used again (after "edit the message" removed a conversation part), the latest call counts.
        /// </summary>
        public int? StartOfCall(string callId)
        {
            if (callId == null) return null;
            lock (_lock)
            {
                int i = _records.FindLastIndex(r => r.CallId == callId);
                if (i < 0) return null;
                while (i > 0 && _records[i - 1].CallId == callId) i--;
                return _records[i].Seq;
            }
        }

        /// <summary>Changes from position <paramref name="seq"/> on that are not undone yet, newest first.</summary>
        public List<ChangeRecord> OpenFrom(int seq)
        {
            lock (_lock) return _records.Where(r => r.Seq >= seq && !r.Reverted).OrderByDescending(r => r.Seq).ToList();
        }

        /// <summary>All changes of the tool call are undone (its "Undo from here" button is no longer useful).</summary>
        public bool IsFullyReverted(string callId)
        {
            lock (_lock) return callId != null && _records.Where(r => r.CallId == callId && r.Kind != ChangeRecord.NotUndoable).All(r => r.Reverted);
        }

        /// <summary>The single "Revert" / "Restore previous" button of a change was used.</summary>
        public void MarkReverted(string undoOrBackupId)
        {
            if (string.IsNullOrEmpty(undoOrBackupId)) return;
            lock (_lock)
                foreach (var r in _records.Where(r => r.UndoId == undoOrBackupId || r.BackupId == undoOrBackupId)) r.Reverted = true;
        }
    }

    public sealed class RewindStep
    {
        public ChangeRecord Record;
        /// <summary>What will be done.</summary>
        public string Text;
        /// <summary>Why this change cannot be undone (null = it can).</summary>
        public string Problem;
    }

    public sealed class RewindPlan
    {
        /// <summary>In execution order (newest change first).</summary>
        public List<RewindStep> Steps = new List<RewindStep>();
        /// <summary>Data changes and other statements that cannot be undone automatically.</summary>
        public List<ChangeRecord> NotUndoable = new List<ChangeRecord>();
        public int Undoable => Steps.Count(s => s.Problem == null);
    }

    /// <summary>
    /// Undoes recorded changes newest first. A change is only undone while the current state is still what the
    /// agent left (editor text, file content, database source); otherwise it is skipped, so later work of the
    /// user is never overwritten.
    /// </summary>
    public static class Rewinder
    {
        public static RewindPlan Plan(ChangeJournal journal, int fromSeq, ToolContext ctx, GuardState guard)
        {
            var plan = new RewindPlan();
            var produced = new List<string>();   // editor texts that earlier steps of this rewind put back
            List<string> windows = null;         // texts of the open editor windows
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var objects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string connection = null;

            foreach (var r in journal.OpenFrom(fromSeq))
            {
                if (r.Kind == ChangeRecord.NotUndoable) { plan.NotUndoable.Add(r); continue; }
                var step = new RewindStep { Record = r };
                plan.Steps.Add(step);
                try
                {
                    switch (r.Kind)
                    {
                        case ChangeRecord.Editor:
                            step.Text = L.T("Editor {0}: put the previous text back", r.Label);
                            if (Take(produced, r.After)) produced.Add(r.Before);
                            else
                            {
                                if (windows == null) windows = ctx.Ide.GetAllEditorTexts();
                                if (Take(windows, r.After)) produced.Add(r.Before);
                                else step.Problem = L.T("the editor text was changed afterwards or the window was closed");
                            }
                            break;

                        case ChangeRecord.File:
                            step.Text = r.Before == null ? L.T("File {0}: delete (created by the agent)", r.Label) : L.T("File {0}: restore the previous content", r.Label);
                            if (!files.TryGetValue(r.Path, out var content)) content = ReadFile(r.Path);
                            if (content == r.After) files[r.Path] = r.Before;
                            else { files[r.Path] = content; step.Problem = L.T("the file was changed afterwards"); }
                            break;

                        case ChangeRecord.Database:
                            step.Text = r.Before == null ? L.T("{0}: drop (created by the agent)", r.Label)
                                      : r.After == null ? L.T("{0}: create again (dropped by the agent)", r.Label)
                                      : L.T("{0}: compile the previous version", r.Label);
                            if (connection == null) connection = ctx.ConnectionLabel;
                            var key = $"{r.ObjectType}|{r.Owner}|{r.Name}";
                            if (!string.Equals(r.Connection, connection, StringComparison.OrdinalIgnoreCase))
                                step.Problem = L.T("it belongs to connection {0}", r.Connection);
                            else if (guard != null && guard.ReadOnly)
                                step.Problem = L.T("the agent is read-only on this connection (unlock it in the chat header)");
                            else
                            {
                                if (!objects.TryGetValue(key, out var source)) source = Db.CurrentSource(ctx, r.ObjectType, r.Owner, r.Name);
                                if (SameSource(source, r.After)) objects[key] = r.Before;
                                else { objects[key] = source; step.Problem = L.T("it was changed in the database afterwards"); }
                            }
                            break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    step.Problem = ex.Message;
                }
            }
            return plan;
        }

        /// <summary>Undoes the steps of the plan that can be undone (checking each one again) and describes the result.</summary>
        public static List<string> Execute(RewindPlan plan, ToolContext ctx)
        {
            var lines = new List<string>();
            foreach (var step in plan.Steps)
            {
                var r = step.Record;
                if (step.Problem != null) { lines.Add("✗ " + step.Text + " – " + L.T("not undone: {0}", step.Problem)); continue; }
                try
                {
                    string extra = null;
                    switch (r.Kind)
                    {
                        case ChangeRecord.Editor:
                            if (!ctx.Ide.ActivateEditorWithText(r.After)) throw new IdeException(L.T("the editor text was changed afterwards or the window was closed"));
                            ctx.Ide.ReplaceEditorText(r.After, r.Before);
                            EditJournal.Remove(r.UndoId);
                            break;

                        case ChangeRecord.File:
                            if (ReadFile(r.Path) != r.After) throw new IOException(L.T("the file was changed afterwards"));
                            if (r.Before == null) System.IO.File.Delete(r.Path);
                            else TextFile.Write(r.Path, r.Before, r.Encoding);
                            EditJournal.Remove(r.UndoId);
                            break;

                        case ChangeRecord.Database:
                            if (!SameSource(Db.CurrentSource(ctx, r.ObjectType, r.Owner, r.Name), r.After))
                                throw new IdeException(L.T("it was changed in the database afterwards"));
                            extra = r.Before == null ? Drop(ctx, r) : CompilePlsqlTool.Compile(ctx, SqlUtil.SplitOnSlash(r.Before), "rewind");
                            if (extra.IndexOf("FAILED", StringComparison.Ordinal) >= 0) throw new IdeException(extra);
                            break;
                    }
                    r.Reverted = true;
                    lines.Add("✓ " + step.Text + (extra != null && extra.IndexOf("WITH ERRORS", StringComparison.Ordinal) >= 0 ? " – " + extra : ""));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lines.Add("✗ " + step.Text + " – " + L.T("not undone: {0}", ex.Message));
                }
            }
            foreach (var r in plan.NotUndoable)
                lines.Add("• " + L.T("Not undone (cannot be undone automatically): {0}", r.Label));
            return lines;
        }

        private static string Drop(ToolContext ctx, ChangeRecord r)
        {
            Db.BeforeChange(ctx, r.ObjectType, r.Owner, r.Name, "rewind", null); // keep a backup of what is dropped
            var res = ctx.Ide.Execute($"drop {r.ObjectType.ToLowerInvariant()} \"{r.Owner}\".\"{r.Name}\"");
            if (!res.Ok) return "FAILED: " + res.ErrorMessage;
            ctx.Ide.RefreshObject(r.ObjectType, r.Owner, r.Name, 3);
            return "dropped";
        }

        private static string ReadFile(string path) => System.IO.File.Exists(path) ? TextFile.Read(path, out _) : null;

        private static bool SameSource(string a, string b) =>
            a == null || b == null ? a == null && b == null : Db.NormalizeSource(a) == Db.NormalizeSource(b);

        private static string NormalizeText(string s) => (s ?? "").Replace("\r\n", "\n").TrimEnd();

        /// <summary>Removes one entry equal to <paramref name="text"/> (line ends and trailing blanks ignored).</summary>
        private static bool Take(List<string> list, string text)
        {
            var n = NormalizeText(text);
            int i = list.FindIndex(t => NormalizeText(t) == n);
            if (i < 0) return false;
            list.RemoveAt(i);
            return true;
        }
    }
}
