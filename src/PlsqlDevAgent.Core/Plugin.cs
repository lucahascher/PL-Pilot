using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using PlsqlDevAgent.Agent;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.UI;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent
{
    /// <summary>Reacts to menu clicks and IDE events. Lives on the IDE main thread.</summary>
    public sealed class Plugin
    {
        public static Plugin Instance { get; private set; }

        private readonly IIde _ide;
        private ChatForm _chat;

        private Plugin(IIde ide)
        {
            _ide = ide;
        }

        /// <summary>Called once on the main thread (by Bootstrap or by the test harness).</summary>
        public static Plugin Create(IIde ide)
        {
            if (ide.IsReal)
            {
                // Inside PL/SQL Developer there is no WinForms message loop: route async continuations
                // through our own main thread queue (which also guards the FPU state).
                WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new MainThreadSyncContext());
                try { Application.EnableVisualStyles(); } catch { }
            }
            MainThread.Initialize();
            var cfg = AgentConfig.Load();
            ApplyLanguage(ide, cfg);
            System.Threading.Tasks.Task.Run(() => LocalData.Cleanup(cfg)); // retention periods (Settings → Local data)
            Instance = new Plugin(ide);
            return Instance;
        }

        /// <summary>Sets the UI language from the setting ("auto" = PL/SQL Developer's language, then Windows).</summary>
        public static void ApplyLanguage(IIde ide, AgentConfig cfg)
        {
            string ideLanguage = "";
            try { ideLanguage = ide.IdeLanguage; } catch { }
            L.Set(L.Resolve(cfg.Language, ideLanguage));
            Log.Info($"UI language: {L.Lang} (setting '{cfg.Language}', PL/SQL Developer '{ideLanguage}', Windows '{System.Globalization.CultureInfo.CurrentUICulture.Name}')");
        }

        public void OnMenuClick(int index)
        {
            switch (index)
            {
                case Bootstrap.MiChat:
                    Chat().ShowNear();
                    break;
                case Bootstrap.MiAskSelection:
                case Bootstrap.MiPopupEditor:
                    AskAboutSelection();
                    break;
                case Bootstrap.MiPopupObject:
                    AskAboutObject();
                    break;
                case Bootstrap.MiSettings:
                    Chat().ShowSettings();
                    break;
            }
        }

        public void AskAboutSelection()
        {
            var selection = _ide.GetSelectedText();
            var ctx = _ide.GetContext();
            string prefill;
            if (!string.IsNullOrWhiteSpace(selection))
            {
                var where = ctx.LabelForPrompt();
                prefill = L.T("Regarding this code{0}:", where) + $"\n```sql\n{selection.Replace("\r\n", "\n").TrimEnd()}\n```\n";
            }
            else
            {
                prefill = L.T("Regarding the active editor ({0}): ", ctx.WindowLabelUi);
            }
            Chat().Prefill(prefill);
        }

        public void AskAboutObject()
        {
            var obj = _ide.GetPopupObject();
            if (obj == null || string.IsNullOrEmpty(obj.Name)) { Chat().ShowNear(); return; }
            Chat().Prefill(L.T("Regarding {0}: ", obj));
        }

        public void OnConnectionChange()
        {
            var txn = _ide.UsesOwnSession ? _ide.PendingTransaction : null;
            var statements = ChangeJournal.StatementsInTransaction(txn);
            _ide.ResetSession();
            if (IsOpen)
            {
                _chat.OnConnectionChanged();
                if (txn != null)
                    _chat.Notice(L.T("The connection changed while the agent session had uncommitted changes ({0}). PL/SQL Developer has closed that session: check whether they were saved.",
                        statements.Count == 0 ? txn : string.Join("; ", statements)));
            }
        }

        public void OnWindowChange()
        {
            if (IsOpen) _chat.UpdateContext();
        }

        public void OnDeactivate()
        {
            if (IsOpen) _chat.Hide();
        }

        public void OnDestroy()
        {
            try { _chat?.Shutdown(); } catch (Exception ex) { Log.Error("Shutdown", ex); }
            _chat = null;
            MainThread.Shutdown();
        }

        public bool CanClose()
        {
            string question = null;
            if (IsOpen && _chat.IsBusy) question = L.T("PL/Pilot is still working. Close PL/SQL Developer anyway?");
            else if (_ide.IsExecutingSql) question = L.T("PL/Pilot is still executing a database statement. Close PL/SQL Developer anyway?");
            if (question != null)
            {
                var answer = QuestionDialog.Ask(DialogOwner(), QuestionDialog.Dark(_ide), "PL/Pilot", question, System.Drawing.SystemIcons.Question, "stay",
                    new QuestionDialog.Choice("close", L.T("Close anyway")),
                    new QuestionDialog.Choice("stay", L.T("Do not close")));
                if (answer != "close") return false;
            }
            return EndTransactionBeforeClose();
        }

        /// <summary>The chat window if it is visible, otherwise PL/SQL Developer's main window.</summary>
        private IWin32Window DialogOwner()
        {
            if (IsOpen && _chat.Visible) return _chat;
            try
            {
                var handle = _ide.MainWindowHandle;
                return handle == IntPtr.Zero ? null : new HandleOwner(handle);
            }
            catch { return null; }
        }

        private sealed class HandleOwner : IWin32Window
        {
            public HandleOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }

        /// <summary>
        /// Uncommitted changes in the agent's own session: the user decides (commit, roll back, or do not close)
        /// instead of leaving it to whatever happens when PL/SQL Developer closes the session.
        /// </summary>
        private bool EndTransactionBeforeClose()
        {
            var txn = _ide.PendingTransaction;
            if (txn == null || !_ide.UsesOwnSession) return true;
            var statements = ChangeJournal.StatementsInTransaction(txn);
            var text = L.T("The database session of PL/Pilot has uncommitted changes:\n\n{0}\n\nCommit them or roll them back before PL/SQL Developer closes?",
                statements.Count == 0 ? L.T("(open transaction)") : "- " + string.Join("\n- ", statements.Take(15)) + (statements.Count > 15 ? "\n- …" : ""));
            var answer = QuestionDialog.Ask(DialogOwner(), QuestionDialog.Dark(_ide), "PL/Pilot", text, System.Drawing.SystemIcons.Warning, "cancel",
                new QuestionDialog.Choice("commit", L.T("Commit")),
                new QuestionDialog.Choice("rollback", L.T("Roll back")),
                new QuestionDialog.Choice("cancel", L.T("Do not close")));
            if (answer != "commit" && answer != "rollback") return false;
            try
            {
                var res = _ide.Execute(answer);
                if (!res.Ok) throw new IdeException(res.ErrorMessage);
                Log.Info($"On closing: {(answer == "commit" ? "committed" : "rolled back")} the agent's transaction {txn}");
                return true;
            }
            catch (Exception ex)
            {
                QuestionDialog.Ask(DialogOwner(), QuestionDialog.Dark(_ide), "PL/Pilot", ex.Message, System.Drawing.SystemIcons.Error, "ok",
                    new QuestionDialog.Choice("ok", "OK"));
                return false;
            }
        }

        private bool IsOpen => _chat != null && !_chat.IsDisposed;

        /// <summary>The chat window if it was opened (used by the test harness).</summary>
        public ChatForm ChatWindow => IsOpen ? _chat : null;

        internal IIde Ide => _ide;

        private ChatForm Chat()
        {
            if (!IsOpen) _chat = new ChatForm(_ide);
            return _chat;
        }
    }

    internal sealed class MainThreadSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object state) => MainThread.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object state) => MainThread.Invoke(() => d(state));
        public override SynchronizationContext CreateCopy() => this;
    }
}
