using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Agent;
using PlsqlDevAgent.Agent.Tools;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.Native;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.UI
{
    /// <summary>
    /// The chat window: a WinForms form (owned by the IDE main window, running on the IDE main thread)
    /// that hosts a WebView2 with the HTML chat UI. Implements IAgentUi for the agent session.
    /// </summary>
    public sealed class ChatForm : Form, IAgentUi
    {
        private readonly IIde _ide;
        private readonly GuardedWebView _web;
        private readonly Label _fallback;
        private readonly AgentSession _session;
        private readonly OpenAiClient _testClient = new OpenAiClient();
        private readonly List<string> _pending = new List<string>();
        private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new ConcurrentDictionary<string, PendingApproval>();
        private AgentConfig _config;
        private bool _ready;
        private bool _positioned;
        private bool _shuttingDown;
        private bool _lastDark;
        private JObject _lastUsage = new JObject { ["type"] = "usage", ["tokens"] = 0, ["window"] = 0, ["estimated"] = true };

        // saved chat (history) state
        private readonly object _eventsLock = new object();
        private JArray _events = new JArray();
        private readonly Dictionary<string, JObject> _assistantEvents = new Dictionary<string, JObject>();
        private string _chatId = NewChatId();
        private DateTime _chatCreated = DateTime.Now;

        // prompt history (arrow-up in the input box)

        public bool IsBusy => _session.IsBusy;

        public ChatForm(IIde ide)
        {
            _ide = ide;
            _config = AgentConfig.Load();
            ApplyIdeSettings();
            _session = new AgentSession(ide, this, _config);
            _session.Journal = ChangeJournal.For(_chatId);
            _ide.TransactionChanged += OnTransactionChanged;
            _ide.DatabaseIdentityChanged += OnDatabaseIdentityChanged;

            Text = "PL/Pilot";
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MinimumSize = new Size(360, 300);
            KeyPreview = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _fallback = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(20), Visible = false };
            _lastDark = IsDark();
            _web = new GuardedWebView { Dock = DockStyle.Fill, DefaultBackgroundColor = BackgroundFor(_lastDark) };
            Controls.Add(_web);
            Controls.Add(_fallback);

            Load += async (s, e) => await InitWebViewAsync();
        }

        // ------------------------------------------------------------------ window handling

        public void ShowNear()
        {
            if (!_positioned) { PlaceInitially(); _positioned = true; }
            if (!Visible)
            {
                var owner = _config.StayOnTopOfIde && _ide.MainWindowHandle != IntPtr.Zero ? new Win32Owner(_ide.MainWindowHandle) : null;
                if (owner != null) Show(owner); else Show();
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            _web.Focus();
            UpdateContext();
        }

        private void PlaceInitially()
        {
            float scale = DeviceDpi / 96f;
            int w = _config.WindowWidth > 0 ? _config.WindowWidth : (int)(500 * scale);
            int h = _config.WindowHeight > 0 ? _config.WindowHeight : (int)(720 * scale);
            var saved = new Rectangle(_config.WindowX, _config.WindowY, w, h);
            if (_config.WindowX != -1 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)) || _config.WindowX < -1000)
            {
                Bounds = saved;
                return;
            }
            var ideRect = Rectangle.Empty;
            if (_ide.MainWindowHandle != IntPtr.Zero && GetWindowRect(_ide.MainWindowHandle, out var r))
                ideRect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            var area = ideRect.Width > 200 ? ideRect : Screen.PrimaryScreen.WorkingArea;
            h = Math.Min(h, area.Height - (int)(160 * scale));
            Bounds = new Rectangle(area.Right - w - (int)(24 * scale), area.Top + (int)(120 * scale), w, Math.Max(h, 300));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveBounds();
            if (e.CloseReason == CloseReason.UserClosing && !_shuttingDown)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        private void SaveBounds()
        {
            if (WindowState != FormWindowState.Normal || !Visible || Left < -1000) return;
            _config.WindowX = Left; _config.WindowY = Top; _config.WindowWidth = Width; _config.WindowHeight = Height;
            try { _config.Save(); } catch (Exception ex) { Log.Warn("Could not save window position: " + ex.Message); }
        }

        public void Shutdown()
        {
            _shuttingDown = true;
            try { SaveBounds(); } catch { }
            try { SaveChat(); } catch { }
            try { _ide.TransactionChanged -= OnTransactionChanged; } catch { }
            try { _ide.DatabaseIdentityChanged -= OnDatabaseIdentityChanged; } catch { }
            try { _session.Dispose(); } catch { }
            try { Close(); Dispose(); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            using (Fpu.Mask()) base.WndProc(ref m);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            UpdateContext();
            ApplyTheme(false); // the IDE theme may have been changed in the meantime
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBarTheme(_lastDark);
        }

        // ------------------------------------------------------------------ theme

        private bool IsDark()
        {
            switch ((_config.Theme ?? "auto").Trim().ToLowerInvariant())
            {
                case "dark": return true;
                case "light": return false;
            }
            try { return _ide.DarkMode; } catch { return false; }
        }

        private void ApplyTheme(bool force)
        {
            bool dark = IsDark();
            if (dark == _lastDark && !force) return;
            _lastDark = dark;
            ApplyTitleBarTheme(dark);
            _web.DefaultBackgroundColor = BackgroundFor(dark);
            Post(new JObject { ["type"] = "theme", ["dark"] = dark }, false);
        }

        private void ApplyTitleBarTheme(bool dark)
        {
            if (!IsHandleCreated) return;
            try
            {
                int value = dark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
            }
            catch { /* older Windows */ }
        }

        // VS Code "Dark Modern" / "Light Modern" side bar background
        private static Color BackgroundFor(bool dark) => dark ? Color.FromArgb(24, 24, 24) : Color.FromArgb(248, 248, 248);

        // ------------------------------------------------------------------ WebView2

        private async Task InitWebViewAsync()
        {
            try
            {
                var managedDir = Path.GetDirectoryName(typeof(ChatForm).Assembly.Location);
                try { CoreWebView2Environment.SetLoaderDllFolderPath(managedDir); } catch (Exception ex) { Log.Warn("SetLoaderDllFolderPath: " + ex.Message); }
                var dataDir = Path.Combine(Log.Directory, "WebView2");
                Directory.CreateDirectory(dataDir);
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir, new CoreWebView2EnvironmentOptions());
                await _web.EnsureCoreWebView2Async(env);
                var core = _web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = _config.DevTools;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = _config.DevTools;
                core.Settings.IsZoomControlEnabled = true;
                core.WebMessageReceived += OnWebMessage;
                core.NewWindowRequested += (s, e) => e.Handled = true;
                core.NavigationStarting += (s, e) => { if (_ready) e.Cancel = true; };
                core.NavigateToString(LoadHtml());
                Log.Info("WebView2 initialized, runtime " + env.BrowserVersionString);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowFallback(L.T("The Microsoft Edge WebView2 Runtime is not installed.\n\nIt is part of current Windows 10/11 installations; ask IT to install the \"Evergreen WebView2 Runtime\"."));
            }
            catch (Exception ex)
            {
                Log.Error("WebView2 init failed", ex);
                ShowFallback(L.T("Could not start the chat view:\n{0}\n\nSee {1}", ex.Message, Log.FilePath));
            }
        }

        private void ShowFallback(string text)
        {
            _web.Visible = false;
            _fallback.Text = text;
            _fallback.Visible = true;
        }

        /// <summary>The page with a fresh Content-Security-Policy nonce: only its own script tag may run.</summary>
        private static string LoadHtml()
        {
            string html;
            using (var s = typeof(ChatForm).Assembly.GetManifestResourceStream("web/index.html"))
            using (var r = new StreamReader(s))
                html = r.ReadToEnd();
            var nonce = new byte[18];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(nonce);
            return html.Replace("__CSP_NONCE__", Convert.ToBase64String(nonce));
        }

        /// <summary>Address of our page; messages from anything else (it cannot navigate, but to be sure) are ignored.</summary>
        private string _pageSource;

        /// <summary>Sends a message to the page; record = keep it for the saved chat.</summary>
        private void Post(JObject message, bool record = true)
        {
            if (record) Record(message);
            var json = message.ToString(Formatting.None);
            if (MainThread.IsMainThread) PostCore(json);
            else MainThread.Post(() => PostCore(json));
        }

        private void PostCore(string json)
        {
            if (IsDisposed) return;
            if (!_ready || _web.CoreWebView2 == null) { _pending.Add(json); return; }
            try { _web.CoreWebView2.PostWebMessageAsJson(json); }
            catch (Exception ex) { Log.Warn("PostWebMessage failed: " + ex.Message); }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            using (Fpu.Mask())
            {
                if (_pageSource == null) _pageSource = e.Source;
                else if (e.Source != _pageSource) { Log.Warn("Ignored a web message from " + e.Source); return; }
                JObject msg;
                try { msg = JObject.Parse(e.WebMessageAsJson); }
                catch { return; }
                try { HandleWebMessage(msg); }
                catch (Exception ex)
                {
                    Log.Error("Handling web message " + msg["type"] + " failed", ex);
                    Post(new JObject { ["type"] = "error", ["text"] = ex.Message });
                }
            }
        }

        private void HandleWebMessage(JObject msg)
        {
            switch ((string)msg["type"])
            {
                case "ready":
                    _ready = true;
                    Post(new JObject
                    {
                        ["type"] = "init",
                        ["dark"] = _lastDark,
                        ["context"] = ContextText(),
                        ["model"] = _config.Model,
                        ["lang"] = L.Lang,
                        ["version"] = typeof(ChatForm).Assembly.GetName().Version.ToString(3),
                        ["defaultPrompt"] = SystemPrompt.DefaultBasePrompt,
                        ["needsSetup"] = !File.Exists(AgentConfig.FilePath),
                        ["config"] = _config.ToUiJson(),
                        ["commands"] = CommandsJson(),
                        ["prompts"] = new JArray(PromptHistory.Texts(_config)),
                        ["guard"] = GuardJson(),
                        ["usage"] = _lastUsage,
                    }, false);
                    foreach (var p in _pending) _web.CoreWebView2.PostWebMessageAsJson(p);
                    _pending.Clear();
                    Post(TransactionJson(), false);
                    break;

                case "txn_commit":
                    EndTransaction(true);
                    break;

                case "txn_rollback":
                    EndTransaction(false);
                    break;

                case "send":
                    HandleSend((string)msg["text"] ?? "", (string)msg["mode"] == "plan");
                    break;

                case "command":
                    HandleAction((string)msg["name"]);
                    break;

                case "stop":
                    _session.Cancel();
                    break;

                case "new_chat":
                    NewChat();
                    break;

                case "retry":
                    lock (_eventsLock)
                    {
                        int lastUser = -1;
                        for (int i = 0; i < _events.Count; i++) if ((string)_events[i]["type"] == "user") lastUser = i;
                        while (lastUser >= 0 && _events.Count > lastUser + 1) _events.RemoveAt(_events.Count - 1);
                    }
                    if (!_session.Retry()) Post(new JObject { ["type"] = "notice", ["text"] = L.T("Nothing to retry.") });
                    break;

                case "approval_reply":
                    if (_approvals.TryRemove((string)msg["id"], out var pending))
                    {
                        bool approved = (bool?)msg["approved"] ?? false, always = (bool?)msg["always"] ?? false;
                        var feedback = (string)msg["feedback"];
                        Record(new JObject
                        {
                            ["type"] = "approval_done", ["id"] = (string)msg["id"], ["ok"] = approved,
                            ["text"] = approved ? L.T(always ? "Allowed (always for this chat)" : "Allowed") : L.T("Rejected") + (string.IsNullOrEmpty(feedback) ? "" : ": " + feedback),
                        });
                        pending.Tcs.TrySetResult(new ApprovalResult { Approved = approved, AlwaysForSession = always, Feedback = feedback });
                    }
                    break;

                case "open_diff":
                    if (_approvals.TryGetValue((string)msg["id"], out var pd) && pd.Request.DiffLeftText != null)
                    {
                        var r = pd.Request;
                        MainThread.Post(() => SafeIde(() => _ide.ShowDifferences(r.DiffLeftCaption, r.DiffLeftText, r.DiffRightCaption, r.DiffRightText)));
                    }
                    break;

                case "open_in_window":
                    var code = (string)msg["text"] ?? "";
                    MainThread.Post(() => SafeIde(() => _ide.NewWindow(IdeWindowType.Sql, code)));
                    break;

                case "insert_code":
                    var mode = (string)msg["mode"];
                    var insert = (string)msg["text"] ?? "";
                    MainThread.Post(() => InsertCode(mode, insert));
                    break;

                case "revert":
                    var undoId = (string)msg["id"];
                    MainThread.Post(() => Revert(undoId));
                    break;

                case "restore_backup":
                    RestoreBackup((string)msg["id"]);
                    break;

                case "rewind_preview":
                    RewindPreview((string)msg["checkpoint"], (string)msg["callId"]);
                    break;

                case "rewind":
                    Rewind((string)msg["checkpoint"], (string)msg["callId"], (bool?)msg["edit"] ?? false);
                    break;

                case "guard_set":
                    var allow = msg["allow"]?.Type == JTokenType.Boolean ? (bool?)msg["allow"] : null;
                    var ctx = _ide.GetContext();
                    _session.Guard.SetOverride($"{ctx.User}@{ctx.Database}", allow);
                    Log.Info($"Production guard override for {ctx.User}@{ctx.Database}: {(allow == null ? "default" : allow.Value ? "unlocked" : "locked")}");
                    UpdateContext();
                    break;

                case "list_chats":
                    PostChats();
                    break;

                case "open_chat":
                    OpenChat((string)msg["id"]);
                    break;

                case "delete_chat":
                    var delId = (string)msg["id"];
                    ChatStore.Delete(delId);
                    if (delId == _chatId) { _chatId = NewChatId(); _chatCreated = DateTime.Now; _session.Journal = ChangeJournal.For(_chatId); }
                    PostChats();
                    break;

                case "mention_query":
                    var q = (string)msg["q"] ?? "";
                    Task.Run(() =>
                    {
                        var items = Mentions.Suggest(q, _session.UserActionContext());
                        Post(new JObject { ["type"] = "mention_results", ["q"] = q, ["items"] = items }, false);
                    });
                    break;

                case "copy":
                    try { Clipboard.SetText((string)msg["text"] ?? " "); } catch (Exception ex) { Log.Warn("Clipboard: " + ex.Message); }
                    break;

                case "open_url":
                    var url = (string)msg["url"] ?? "";
                    if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        Process.Start(url);
                    break;

                case "open_log":
                    if (File.Exists(Log.FilePath)) Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
                    break;

                case "open_backups":
                    Directory.CreateDirectory(BackupStore.Directory);
                    Process.Start("explorer.exe", "\"" + BackupStore.Directory + "\"");
                    break;

                case "delete_history":
                    DeleteHistory();
                    break;

                case "get_settings":
                    PostSettings();
                    break;

                case "rules_info":
                    PostRulesInfo((string)msg["path"]);
                    break;

                case "browse_rules":
                    var currentRules = (string)msg["current"];
                    MainThread.Post(() =>
                    {
                        using (var dlg = new OpenFileDialog
                        {
                            Title = L.T("Company rules file (Markdown)"),
                            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
                            CheckFileExists = false,
                            FileName = RulesLoader.EditableFile(currentRules),
                        })
                        {
                            if (dlg.ShowDialog(this) == DialogResult.OK)
                            {
                                Post(new JObject { ["type"] = "rules_path", ["path"] = dlg.FileName }, false);
                                PostRulesInfo(dlg.FileName);
                            }
                        }
                    });
                    break;

                case "load_rules_file":
                    var editPath = RulesLoader.EditableFile((string)msg["path"]);
                    Task.Run(() =>
                    {
                        try
                        {
                            bool exists = File.Exists(editPath);
                            var template = L.German ? RulesLoader.TemplateDe : RulesLoader.Template;
                            var text = exists ? TextFile.Read(editPath, out _) : template;
                            Post(new JObject { ["type"] = "rules_file", ["path"] = editPath, ["text"] = text, ["exists"] = exists, ["template"] = template }, false);
                        }
                        catch (Exception ex)
                        {
                            Post(new JObject { ["type"] = "rules_saved", ["ok"] = false, ["error"] = $"Could not read {editPath}: {ex.Message}" }, false);
                        }
                    });
                    break;

                case "save_rules_file":
                    var savePath = (string)msg["path"];
                    var rulesText = (string)msg["text"] ?? "";
                    Task.Run(() =>
                    {
                        try
                        {
                            RulesLoader.Save(savePath, rulesText);
                            Log.Info("Company rules saved: " + savePath);
                            _session.UpdateConfig(_config); // rebuild the system prompt with the next message
                            Post(new JObject { ["type"] = "rules_saved", ["ok"] = true, ["path"] = savePath }, false);
                        }
                        catch (Exception ex)
                        {
                            Post(new JObject { ["type"] = "rules_saved", ["ok"] = false, ["error"] = $"Could not save {savePath}: {ex.Message}" }, false);
                        }
                    });
                    break;

                case "preview_prompt":
                    var previewCfg = _config.Clone();
                    previewCfg.UpdateFromUi((JObject)msg["config"]);
                    Task.Run(() =>
                    {
                        DatabaseInfo database = null;
                        try { database = _session.Database(); }
                        catch (Exception ex) { Log.Warn("Prompt preview without database facts: " + ex.Message); }
                        var text = SystemPrompt.Build(previewCfg, previewCfg.HasWorkspace, database);
                        Post(new JObject { ["type"] = "prompt_preview", ["text"] = text, ["chars"] = text.Length, ["tokens"] = (int)(text.Length / 3.5) }, false);
                    });
                    break;

                case "save_settings":
                    SaveSettings((JObject)msg["config"]);
                    break;

                case "detect_context":
                    DetectContext((JObject)msg["config"]);
                    break;

                case "test_connection":
                    TestConnection((JObject)msg["config"]);
                    break;

                case "browse_folder":
                    var current = (string)msg["current"];
                    MainThread.Post(() =>
                    {
                        using (var dlg = new FolderBrowserDialog { Description = L.T("Project folder for the agent (SQL / PL/SQL files, AGENTS.md)"), SelectedPath = current ?? "" })
                        {
                            if (dlg.ShowDialog(this) == DialogResult.OK)
                                Post(new JObject { ["type"] = "folder", ["path"] = dlg.SelectedPath }, false);
                        }
                    });
                    break;
            }
        }

        // ------------------------------------------------------------------ sending, commands

        private void HandleSend(string text, bool planMode)
        {
            if (string.IsNullOrWhiteSpace(_config.BaseUrl) || string.IsNullOrWhiteSpace(_config.Model))
            {
                Post(new JObject { ["type"] = "error", ["text"] = L.T("Please configure the LLM server and model first (Settings).") });
                return;
            }
            RememberPrompt(text);
            var cmd = Commands.Find(text, _config, out var args);
            if (cmd != null && cmd.Kind == "action") { HandleAction(cmd.Name); return; }

            var llmText = cmd != null ? Commands.Expand(cmd, args) : text;
            var checkpoint = "c" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var userEvent = new JObject { ["type"] = "user", ["text"] = text, ["mode"] = planMode ? "plan" : "agent", ["checkpoint"] = checkpoint };
            Record(userEvent); // before Send: the run starts immediately and records its own events
            if (!_session.Send(llmText, planMode, checkpoint))
            {
                lock (_eventsLock) _events.Remove(_events.LastOrDefault(e => (string)e["type"] == "user" && (string)e["text"] == text));
                Post(new JObject { ["type"] = "notice", ["text"] = L.T("The agent is still working. Stop it first or wait.") }, false);
                return;
            }
            Post(new JObject { ["type"] = "checkpoint", ["id"] = checkpoint }, false);
        }

        private void HandleAction(string name)
        {
            switch (name)
            {
                case "new":
                    NewChat();
                    Post(new JObject { ["type"] = "cleared" }, false);
                    break;
                case "compact":
                    if (!_session.CompactNow()) Post(new JObject { ["type"] = "notice", ["text"] = L.T("The agent is busy.") });
                    break;
                case "help":
                    var sb = new StringBuilder(L.T("**Commands** (type `/` in the input box):") + "\n\n");
                    foreach (var c in Commands.All(_config))
                        sb.Append($"- `/{c.Name}`: {L.T(c.Description ?? "")}{(c.Source != "built-in" ? $" _({c.Source})_" : "")}\n");
                    sb.Append("\n").Append(L.T("Own commands: Settings → Custom commands, or `.plsqlagent/commands/<name>.md` in the project folder. Mention objects or files with `@NAME` to attach their source."));
                    Post(new JObject { ["type"] = "info", ["markdown"] = sb.ToString() });
                    break;
            }
        }

        private JArray CommandsJson() =>
            new JArray(Commands.All(_config).Select(c => new JObject { ["name"] = c.Name, ["description"] = L.T(c.Description ?? ""), ["kind"] = c.Kind }));

        private void RememberPrompt(string text) => PromptHistory.Remember(text, _config);

        // ------------------------------------------------------------------ editor actions from the chat

        private void InsertCode(string mode, string text)
        {
            try
            {
                var c = _ide.GetContext();
                if (!c.HasEditor) throw new IdeException(L.T("Open an editor window (SQL, Program, Test or Command window) first."));
                var before = _ide.GetEditorText();
                var after = mode == "selection"
                    ? EditorText.ReplaceSelection(before, _ide.GetSelectedText(), c.CursorLine, c.CursorColumn, text)
                    : EditorText.InsertAt(before, c.CursorLine, c.CursorColumn, text);
                _ide.ReplaceEditorText(before, after);
                var id = EditJournal.Add(new EditJournal.Record { Kind = "editor", Label = c.WindowLabel, Before = before, After = after });
                _session.Journal?.Add(new ChangeRecord
                {
                    Kind = ChangeRecord.Editor, Label = c.WindowNameUi, Before = before, After = after, UndoId = id, Checkpoint = _session.CurrentCheckpoint,
                });
                Post(new JObject
                {
                    ["type"] = "applied",
                    ["text"] = mode == "selection" ? L.T("Replaced the selection in the editor.") : L.T("Inserted at line {0} in the editor.", c.CursorLine),
                    ["undo"] = new JObject { ["id"] = id, ["label"] = "editor" },
                });
            }
            catch (Exception ex)
            {
                Post(new JObject { ["type"] = "error", ["text"] = ex.Message });
            }
        }

        private void Revert(string id)
        {
            var r = EditJournal.Get(id);
            string error = null;
            if (r == null) error = L.T("Undo information is no longer available (it is kept until PL/SQL Developer is closed).");
            else
            {
                try
                {
                    if (r.Kind == "editor")
                    {
                        _ide.ReplaceEditorText(r.After, r.Before);
                    }
                    else
                    {
                        string current = File.Exists(r.Path) ? TextFile.Read(r.Path, out _) : null;
                        if (current != r.After) throw new IdeException(L.T("The file was changed after the edit; not reverted."));
                        if (r.Before == null) File.Delete(r.Path);
                        else TextFile.Write(r.Path, r.Before, r.Encoding);
                    }
                }
                catch (IdeException ex) when (r.Kind == "editor")
                {
                    error = L.T("Not reverted: the editor content changed after the edit, or a different window is active. Activate the window or use Undo (Ctrl+Z) in the editor.") +
                            " (" + ex.Message + ")";
                }
                catch (Exception ex)
                {
                    error = L.T("Not reverted: {0}", ex.Message);
                }
            }
            if (error == null)
            {
                EditJournal.Remove(id);
                _session.Journal?.MarkReverted(id);
                _session.AddNote($"The user reverted your edit of {r.Label}; it has its previous content again.");
            }
            Post(new JObject { ["type"] = "undo_done", ["id"] = id, ["ok"] = error == null, ["text"] = error ?? L.T("Reverted.") });
        }

        private void RestoreBackup(string id)
        {
            Task.Run(() =>
            {
                string text;
                bool ok = false;
                try
                {
                    var entry = BackupStore.Get(id);
                    var ctx = _session.UserActionContext();
                    var guard = _session.CurrentGuard();
                    if (entry == null) text = L.T("Backup not found.");
                    else if (guard.ReadOnly) text = L.T("Not restored: the agent is read-only on this connection. Unlock the connection in the chat header first.");
                    else if (!string.Equals(entry.Connection, ctx.ConnectionLabel, StringComparison.OrdinalIgnoreCase))
                        text = L.T("Not restored: the backup belongs to {0}, but PL/SQL Developer is connected to {1}.", entry.Connection, ctx.ConnectionLabel);
                    else
                    {
                        text = RestoreBackupTool.RestoreNow(ctx, entry);
                        ok = text.IndexOf("FAILED", StringComparison.Ordinal) < 0;
                        if (ok) _session.Journal?.MarkReverted(id);
                        _session.AddNote($"The user restored {entry.Label} from backup {entry.Id}: {text.Replace('\n', ' ')}");
                    }
                }
                catch (Exception ex)
                {
                    text = L.T("Restore failed: {0}", ex.Message);
                }
                Post(new JObject { ["type"] = "restore_done", ["id"] = id, ["ok"] = ok, ["text"] = text });
            });
        }

        // ------------------------------------------------------------------ uncommitted changes of the agent's session

        private void OnTransactionChanged() => Post(TransactionJson(), false);

        /// <summary>The guard badge may change once the database's own names are known.</summary>
        private void OnDatabaseIdentityChanged() => MainThread.Post(UpdateContext);

        private JObject TransactionJson()
        {
            var txn = _ide.PendingTransaction;
            return new JObject
            {
                ["type"] = "txn",
                ["active"] = txn != null,
                ["own"] = _ide.UsesOwnSession,
                ["statements"] = new JArray(ChangeJournal.StatementsInTransaction(txn)),
            };
        }

        /// <summary>Commit / Rollback button for the open transaction of the agent's own session.</summary>
        private void EndTransaction(bool commit)
        {
            if (_session.IsBusy) { Post(new JObject { ["type"] = "notice", ["text"] = L.T("Stop the agent before committing or rolling back.") }, false); return; }
            if (!_ide.UsesOwnSession) { Post(new JObject { ["type"] = "notice", ["text"] = L.T("The agent uses the main session of PL/SQL Developer: commit or roll back there.") }, false); return; }
            var txn = _ide.PendingTransaction;
            if (txn == null) { Post(TransactionJson(), false); return; }
            var statements = ChangeJournal.StatementsInTransaction(txn);
            Task.Run(() =>
            {
                try
                {
                    var res = _ide.Execute(commit ? "commit" : "rollback");
                    if (!res.Ok) throw new IdeException(res.ErrorMessage);
                    if (!commit) ChangeJournal.MarkTransactionReverted(txn);
                    var list = statements.Count == 0 ? "" : ": " + string.Join("; ", statements);
                    Log.Info((commit ? "User committed" : "User rolled back") + " the agent's transaction " + txn + list);
                    _session.AddNote(commit
                        ? "The user COMMITTED the pending data changes of your database session" + list + "."
                        : "The user ROLLED BACK the pending data changes of your database session" + list + "; they no longer exist.");
                    Post(new JObject { ["type"] = "applied", ["text"] = commit ? L.T("Committed the changes of the agent.") : L.T("Rolled back the changes of the agent.") });
                }
                catch (Exception ex)
                {
                    Post(new JObject { ["type"] = "error", ["text"] = (commit ? "COMMIT: " : "ROLLBACK: ") + ex.Message });
                }
                Post(TransactionJson(), false);
            });
        }

        // ------------------------------------------------------------------ rewind

        /// <summary>Where undoing starts: the beginning of a user message or the first change of a tool call.</summary>
        private int? RewindStart(ChangeJournal journal, string checkpoint, string callId) =>
            journal == null ? null : checkpoint != null ? journal.StartOfCheckpoint(checkpoint) : journal.StartOfCall(callId);

        /// <summary>Shows what a rewind would undo (and what not) before anything is changed.</summary>
        private void RewindPreview(string checkpoint, string callId)
        {
            if (_session.IsBusy) { Post(new JObject { ["type"] = "notice", ["text"] = L.T("Stop the agent before rewinding.") }, false); return; }
            var journal = _session.Journal;
            Task.Run(() =>
            {
                var json = new JObject { ["type"] = "rewind_plan", ["checkpoint"] = checkpoint, ["callId"] = callId };
                try
                {
                    var start = RewindStart(journal, checkpoint, callId);
                    var items = new JArray();
                    int undoable = 0;
                    if (start != null)
                    {
                        var plan = Rewinder.Plan(journal, start.Value, _session.UserActionContext(), _session.CurrentGuard());
                        foreach (var s in plan.Steps) items.Add(new JObject { ["text"] = s.Text, ["problem"] = s.Problem });
                        foreach (var r in plan.NotUndoable) items.Add(new JObject { ["text"] = r.Label, ["notUndoable"] = true });
                        undoable = plan.Undoable;
                    }
                    json["items"] = items;
                    json["undoable"] = undoable;
                    json["known"] = start != null;
                    json["canEdit"] = checkpoint != null && _session.CanTruncateAt(checkpoint);
                }
                catch (Exception ex)
                {
                    Log.Error("Rewind preview failed", ex);
                    json["error"] = ex.Message;
                }
                Post(json, false);
            });
        }

        /// <summary>
        /// Undoes the changes from the given point on (newest first). With <paramref name="edit"/> the conversation is
        /// also cut off before the user message, whose text goes back into the input box.
        /// </summary>
        private void Rewind(string checkpoint, string callId, bool edit)
        {
            if (_session.IsBusy) { Post(new JObject { ["type"] = "notice", ["text"] = L.T("Stop the agent before rewinding.") }, false); return; }
            var journal = _session.Journal;
            Task.Run(() =>
            {
                var lines = new List<string>();
                var reverted = new JArray();
                var revertedCalls = new JArray();
                var notUndone = new List<string>();
                try
                {
                    var start = RewindStart(journal, checkpoint, callId);
                    if (start != null)
                    {
                        var ctx = _session.UserActionContext();
                        var plan = Rewinder.Plan(journal, start.Value, ctx, _session.CurrentGuard());
                        lines = Rewinder.Execute(plan, ctx, journal);
                        foreach (var s in plan.Steps)
                        {
                            if (s.Record.Reverted) { if (s.Record.UndoId != null) reverted.Add(s.Record.UndoId); if (s.Record.BackupId != null) reverted.Add(s.Record.BackupId); }
                            else notUndone.Add(s.Record.Label);
                        }
                        notUndone.AddRange(plan.NotUndoable.Select(r => r.Label));
                        foreach (var call in plan.Steps.Select(s => s.Record.CallId).Where(c => c != null).Distinct())
                            if (journal.IsFullyReverted(call)) revertedCalls.Add(call);
                        var undone = plan.Steps.Where(s => s.Record.Reverted).Select(s => s.Record.Label).ToList();
                        Log.Info($"Rewind ({checkpoint ?? callId}): {undone.Count} change(s) undone, {notUndone.Count} not");
                        if (!edit && undone.Count > 0)
                            _session.AddNote("The user rewound (undid) your changes from that point on: " + string.Join("; ", undone) +
                                             " are back to their previous state" + (notUndone.Count > 0 ? "; NOT undone: " + string.Join("; ", notUndone) : "") + ".");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Rewind failed", ex);
                    lines.Add("✗ " + ex.Message);
                }

                string prompt = null;
                bool truncated = false;
                if (edit && checkpoint != null && _session.TruncateAt(checkpoint))
                {
                    truncated = true;
                    lock (_eventsLock)
                    {
                        int idx = -1;
                        for (int i = 0; i < _events.Count; i++)
                            if ((string)_events[i]["type"] == "user" && (string)_events[i]["checkpoint"] == checkpoint) idx = i;
                        if (idx >= 0)
                        {
                            prompt = (string)_events[idx]["text"];
                            while (_events.Count > idx) _events.RemoveAt(_events.Count - 1);
                        }
                    }
                    if (notUndone.Count > 0)
                        _session.AddNote("Changes of an earlier attempt that were NOT undone and are still in place: " + string.Join("; ", notUndone) + ".");
                }

                Post(new JObject { ["type"] = "rewind_done", ["checkpoint"] = checkpoint, ["callId"] = callId, ["truncated"] = truncated, ["prompt"] = prompt }, false);
                if (lines.Count > 0)
                    Post(new JObject { ["type"] = "rewound", ["lines"] = new JArray(lines), ["reverted"] = reverted, ["revertedCalls"] = revertedCalls });
                SaveChat();
            });
        }

        // ------------------------------------------------------------------ saved chats

        private static string NewChatId() => DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N").Substring(0, 6);

        private void Record(JObject m)
        {
            var type = (string)m["type"];
            lock (_eventsLock)
            {
                switch (type)
                {
                    case "user": case "tool_start": case "tool_end": case "notice": case "error": case "info":
                    case "attachments": case "compacted": case "applied": case "approval": case "approval_done":
                    case "undo_done": case "restore_done": case "tasks": case "subtask_step": case "rewound":
                        _events.Add(m.DeepClone());
                        break;
                    case "approval_cancel":
                        _events.Add(new JObject { ["type"] = "approval_done", ["id"] = m["id"], ["ok"] = false, ["text"] = L.T("Cancelled") });
                        break;
                    case "assistant_start":
                        var ev = new JObject { ["type"] = "assistant_full", ["id"] = m["id"], ["text"] = "", ["reasoning"] = "" };
                        _assistantEvents[(string)m["id"]] = ev;
                        _events.Add(ev);
                        break;
                    case "delta":
                    case "reasoning":
                        if (_assistantEvents.TryGetValue((string)m["id"], out var a))
                        {
                            var field = type == "delta" ? "text" : "reasoning";
                            a[field] = (string)a[field] + (string)m["text"];
                        }
                        break;
                }
            }
        }

        private void SaveChat()
        {
            ChatRecord record;
            lock (_eventsLock)
            {
                var firstUser = _events.FirstOrDefault(e => (string)e["type"] == "user");
                if (firstUser == null || _session.MessageCount == 0) return;
                var ctx = SafeContext();
                record = new ChatRecord
                {
                    Id = _chatId,
                    Title = ChatStore.MakeTitle((string)firstUser["text"]),
                    Created = _chatCreated,
                    Updated = DateTime.Now,
                    Connection = ctx?.Connected == true ? $"{ctx.User}@{ctx.Database}" : "",
                    Model = _config.Model,
                    Messages = _events.Count(e => (string)e["type"] == "user"),
                    History = _session.ExportHistory(),
                    Events = (JArray)_events.DeepClone(),
                    Checkpoints = _session.ExportCheckpoints(),
                };
            }
            if (!_config.SaveChatHistory) return; // history switched off: nothing is written to disk
            try
            {
                ChatStore.Save(LocalData.ForStorage(record, _config));
                if (_config.ChatRetentionDays > 0) ChatStore.DeleteOlderThan(DateTime.Now.AddDays(-_config.ChatRetentionDays));
            }
            catch (Exception ex) { Log.Warn("Saving chat failed: " + ex.Message); }
        }

        /// <summary>"Delete saved chats now" (settings): all saved chats and the prompt history.</summary>
        private void DeleteHistory()
        {
            Task.Run(() =>
            {
                int count = 0;
                try
                {
                    count = ChatStore.DeleteAll();
                    PromptHistory.Delete();
                    Log.Info($"User deleted the saved chats ({count}) and the prompt history");
                }
                catch (Exception ex) { Log.Warn("Deleting the saved chats failed: " + ex.Message); }
                Post(new JObject { ["type"] = "history_deleted", ["text"] = L.T("{0} saved chat(s) and the prompt history were deleted.", count) }, false);
            });
        }

        private void NewChat()
        {
            SaveChat();
            _session.Reset();
            lock (_eventsLock) { _events = new JArray(); _assistantEvents.Clear(); }
            _chatId = NewChatId();
            _chatCreated = DateTime.Now;
            _session.Journal = ChangeJournal.For(_chatId);
        }

        private void PostChats()
        {
            var items = new JArray(ChatStore.List().Select(h => new JObject
            {
                ["id"] = h.Id, ["title"] = h.Title, ["updated"] = h.Updated.ToString("yyyy-MM-dd HH:mm"),
                ["connection"] = h.Connection, ["messages"] = h.Messages,
            }));
            Post(new JObject { ["type"] = "chats", ["items"] = items, ["current"] = _chatId, ["disabled"] = !_config.SaveChatHistory }, false);
        }

        private void OpenChat(string id)
        {
            if (_session.IsBusy) { Post(new JObject { ["type"] = "notice", ["text"] = L.T("Stop the agent before opening another chat.") }); return; }
            var record = ChatStore.Load(id);
            if (record == null) { Post(new JObject { ["type"] = "error", ["text"] = L.T("Chat not found.") }); return; }
            SaveChat();
            _session.ImportHistory(record.History, record.Checkpoints);
            lock (_eventsLock) { _events = (JArray)record.Events.DeepClone(); _assistantEvents.Clear(); }
            _chatId = record.Id;
            _chatCreated = record.Created;
            _session.Journal = ChangeJournal.For(_chatId);
            Post(new JObject { ["type"] = "replay", ["events"] = record.Events, ["title"] = record.Title }, false);
        }

        // ------------------------------------------------------------------ settings

        private void SaveSettings(JObject ui)
        {
            var cfg = _config.Clone();
            cfg.UpdateFromUi(ui);
            if (!Uri.TryCreate(cfg.BaseUrl?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                Post(new JObject { ["type"] = "settings_error", ["text"] = L.T("Base URL must be an http(s) URL, e.g. http://server:8000/v1") }, false);
                return;
            }
            if (!string.IsNullOrWhiteSpace(cfg.WorkspaceDir) && !Directory.Exists(cfg.WorkspaceDir))
            {
                Post(new JObject { ["type"] = "settings_error", ["text"] = L.T("The project folder does not exist.") }, false);
                return;
            }
            if (!string.IsNullOrWhiteSpace(cfg.ExtraBodyJson))
            {
                try { JObject.Parse(cfg.ExtraBodyJson); }
                catch (Exception ex) { Post(new JObject { ["type"] = "settings_error", ["text"] = L.T("Extra request JSON is invalid: {0}", ex.Message) }, false); return; }
            }
            cfg.MaxSteps = Math.Max(1, cfg.MaxSteps);
            cfg.MaxQueryRows = Math.Max(1, cfg.MaxQueryRows);
            cfg.RequestTimeoutSeconds = Math.Max(30, cfg.RequestTimeoutSeconds);
            cfg.ContextWindowTokens = Math.Max(4096, cfg.ContextWindowTokens);
            cfg.CompactAtPercent = Math.Max(0, Math.Min(95, cfg.CompactAtPercent));
            if (cfg.ChatHistoryContent != "full") cfg.ChatHistoryContent = "noResults";
            cfg.ChatRetentionDays = Math.Max(0, Math.Min(3650, cfg.ChatRetentionDays));
            cfg.BackupRetentionDays = Math.Max(0, Math.Min(3650, cfg.BackupRetentionDays));
            cfg.LogRetentionDays = Math.Max(0, Math.Min(3650, cfg.LogRetentionDays));
            bool historySwitchedOff = _config.SaveChatHistory && !cfg.SaveChatHistory;
            cfg.Save();
            _config = cfg;
            // apply the local data settings right away (history off: saved chats and prompts are deleted)
            Task.Run(() => LocalData.Cleanup(cfg));
            if (historySwitchedOff)
                Post(new JObject { ["type"] = "notice", ["text"] = L.T("Chat history is off: saved chats and the prompt history are deleted and nothing is saved any more.") }, false);
            _session.UpdateConfig(cfg);
            ApplyIdeSettings();
            Plugin.ApplyLanguage(_ide, cfg);
            if (_web.CoreWebView2 != null)
            {
                _web.CoreWebView2.Settings.AreDevToolsEnabled = cfg.DevTools;
                _web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = cfg.DevTools;
            }
            Post(new JObject { ["type"] = "settings_saved", ["context"] = ContextText(), ["commands"] = CommandsJson(), ["model"] = cfg.Model, ["lang"] = L.Lang }, false);
            ApplyTheme(true);
            UpdateContext();
        }

        private void TestConnection(JObject ui)
        {
            var cfg = _config.Clone();
            cfg.UpdateFromUi(ui);
            Task.Run(async () =>
            {
                try
                {
                    List<string> models;
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                        models = await _testClient.ListModelsAsync(cfg, cts.Token);
                    // the page fills in the first model if none is set: detect the context window of that one
                    var model = string.IsNullOrWhiteSpace(cfg.Model) ? models.FirstOrDefault() : cfg.Model;
                    var context = await DetectContextAsync(cfg, model);
                    Post(new JObject { ["type"] = "test_result", ["ok"] = true, ["models"] = new JArray(models), ["context"] = context }, false);
                }
                catch (Exception ex)
                {
                    var message = ex is TaskCanceledException ? L.T("Timed out after 20 s.") : ex.GetBaseException().Message;
                    Post(new JObject { ["type"] = "test_result", ["ok"] = false, ["error"] = message }, false);
                }
            });
        }

        /// <summary>The context window the server reports for <paramref name="model"/>, for the settings page (null if unknown).</summary>
        private async Task<JObject> DetectContextAsync(AgentConfig cfg, string model)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    var info = await ContextWindowDetector.DetectAsync(_testClient, cfg, model, cts.Token);
                    if (!info.Known) return null;
                    var json = info.ToJson();
                    json["model"] = model;
                    json["setting"] = info.SettingFor(cfg.ContextWindowTokens);
                    return json;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Context window detection failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>The model was changed in the settings page: detect its context window.</summary>
        private void DetectContext(JObject ui)
        {
            var cfg = _config.Clone();
            cfg.UpdateFromUi(ui);
            Task.Run(async () =>
            {
                var context = await DetectContextAsync(cfg, cfg.Model);
                Post(new JObject { ["type"] = "context_info", ["model"] = cfg.Model, ["context"] = context }, false);
            });
        }

        private void ApplyIdeSettings()
        {
            if (_ide is PlsqlIde p && p.UseSeparateSession != _config.SeparateSession)
            {
                p.UseSeparateSession = _config.SeparateSession;
                p.ResetSession();
            }
        }

        // ------------------------------------------------------------------ public actions (main thread)

        public void Prefill(string text)
        {
            ShowNear();
            Post(new JObject { ["type"] = "prefill", ["text"] = text }, false);
        }

        public void ShowSettings()
        {
            ShowNear();
            PostSettings();
        }

        private void PostSettings()
        {
            Post(new JObject { ["type"] = "settings", ["config"] = _config.ToUiJson(), ["defaultPrompt"] = SystemPrompt.DefaultBasePrompt }, false);
            PostRulesInfo(_config.CompanyRulesPath);
        }

        /// <summary>Which rule files are loaded (computed in the background: a network share may be slow).</summary>
        private void PostRulesInfo(string configuredPath)
        {
            var cfg = _config.Clone();
            cfg.CompanyRulesPath = configuredPath ?? "";
            Task.Run(() =>
            {
                string text;
                try { text = RulesLoader.Describe(cfg); } catch (Exception ex) { text = ex.Message; }
                Post(new JObject { ["type"] = "rules_info", ["text"] = text, ["pluginDir"] = RulesLoader.PluginRulesDir }, false);
            });
        }

        /// <summary>Test hook: stops the agent like the Stop button.</summary>
        public void StopForTest() => _session.Cancel();

        /// <summary>Test hook: changes the configuration for this session only (not saved).</summary>
        public void ApplyConfigForTest(Action<AgentConfig> change)
        {
            change(_config);
            _config.SessionOnly = true; // e.g. a test model: automatic changes must not be saved either
            _session.UpdateConfig(_config);
        }

        public void OnConnectionChanged()
        {
            _session.OnConnectionChanged();
            UpdateContext();
        }

        public void UpdateContext()
        {
            if (!_ready || !Visible) return;
            Post(new JObject { ["type"] = "context", ["text"] = ContextText() }, false);
            Post(GuardJson(), false);
        }

        private JObject GuardJson()
        {
            var g = _session.CurrentGuard();
            return new JObject
            {
                ["type"] = "guard",
                ["mode"] = g.Mode.ToString(),
                ["connection"] = g.Connection,
                ["pattern"] = g.Pattern,
                ["text"] = g.Describe(),
            };
        }

        private string ContextText()
        {
            try
            {
                return _ide.GetContext().ConnectionLabel;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private IdeContext SafeContext()
        {
            try { return _ide.GetContext(); } catch { return null; }
        }

        private void SafeIde(Action action)
        {
            try { action(); }
            catch (Exception ex) { Post(new JObject { ["type"] = "error", ["text"] = ex.Message }); }
        }

        // ------------------------------------------------------------------ IAgentUi

        public void AssistantStart(string messageId) => Post(new JObject { ["type"] = "assistant_start", ["id"] = messageId });
        public void AssistantDelta(string messageId, string text) => Post(new JObject { ["type"] = "delta", ["id"] = messageId, ["text"] = text });
        public void ReasoningDelta(string messageId, string text) => Post(new JObject { ["type"] = "reasoning", ["id"] = messageId, ["text"] = text });
        public void AssistantEnd(string messageId) => Post(new JObject { ["type"] = "assistant_end", ["id"] = messageId });

        public void ToolStart(string callId, string toolName, string summary, string argumentsJson) =>
            Post(new JObject { ["type"] = "tool_start", ["callId"] = callId, ["name"] = toolName, ["summary"] = summary, ["args"] = Shorten(argumentsJson, 20000) });

        public void ToolEnd(string callId, bool ok, string result, JObject meta)
        {
            Post(new JObject { ["type"] = "tool_end", ["callId"] = callId, ["ok"] = ok, ["result"] = Shorten(result, 20000), ["meta"] = meta });
            // the statement list of an open transaction is complete only after the tool recorded its change
            if (_ide.PendingTransaction != null) Post(TransactionJson(), false);
        }

        public void Attachments(JArray items) => Post(new JObject { ["type"] = "attachments", ["items"] = items });

        public void ContextUsage(int tokens, int window, bool estimated)
        {
            _lastUsage = new JObject { ["type"] = "usage", ["tokens"] = tokens, ["window"] = window, ["estimated"] = estimated };
            Post(_lastUsage, false);
        }

        public void Tasks(JArray tasks) => Post(new JObject { ["type"] = "tasks", ["items"] = tasks });

        public void SubtaskStep(string parentCallId, string text) =>
            Post(new JObject { ["type"] = "subtask_step", ["callId"] = parentCallId, ["text"] = text });

        public void Compacted(string summary, int tokensBefore, int tokensAfter) =>
            Post(new JObject { ["type"] = "compacted", ["summary"] = summary, ["before"] = tokensBefore, ["after"] = tokensAfter });

        public void Error(string message) => Post(new JObject { ["type"] = "error", ["text"] = message });
        public void Notice(string message) => Post(new JObject { ["type"] = "notice", ["text"] = message });

        public void Busy(bool busy)
        {
            Post(new JObject { ["type"] = "busy", ["value"] = busy }, false);
            _ide.SetStatus(busy ? L.T("PL/Pilot is working…") : L.T("PL/Pilot finished."));
            if (!busy)
            {
                SaveChat();
                MainThread.Post(UpdateContext);
            }
        }

        public Task<ApprovalResult> RequestApproval(string callId, ApprovalRequest request, CancellationToken ct)
        {
            var id = "a" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var tcs = new TaskCompletionSource<ApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _approvals[id] = new PendingApproval { Tcs = tcs, Request = request };
            var registration = ct.Register(() =>
            {
                if (_approvals.TryRemove(id, out var p))
                {
                    p.Tcs.TrySetCanceled();
                    Post(new JObject { ["type"] = "approval_cancel", ["id"] = id });
                }
            });
            tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

            Post(new JObject
            {
                ["type"] = "approval",
                ["id"] = id,
                ["callId"] = callId,
                ["category"] = request.Category,
                ["title"] = request.Title,
                ["detail"] = request.Detail,
                ["diff"] = request.Diff,
                ["warning"] = request.Warning,
                ["language"] = request.DetailLanguage,
                ["canDiffInIde"] = _ide.IsReal && _ide.CanShowDifferences && request.DiffLeftText != null,
                ["alwaysAsk"] = request.AlwaysAsk,
            });
            MainThread.Post(() =>
            {
                if (!Visible) ShowNear();
                else if (!ContainsFocus) FlashWindow(Handle, true);
            });
            return tcs.Task;
        }

        /// <summary>Test hook for the harness: runs JavaScript in the chat page.</summary>
        public Task<string> ExecuteScriptAsync(string script) =>
            _web.CoreWebView2 == null ? Task.FromResult<string>(null) : _web.CoreWebView2.ExecuteScriptAsync(script);

        public bool IsChatReady => _ready;

        /// <summary>Test hook for the harness: PNG of the chat page as WebView2 renders it (works for an off-screen window).</summary>
        public async Task CapturePageAsync(string path)
        {
            if (_web.CoreWebView2 == null) return;
            using (var f = File.Create(path))
                await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, f);
        }

        private static string Shorten(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "\n…";

        private sealed class PendingApproval
        {
            public TaskCompletionSource<ApprovalResult> Tcs;
            public ApprovalRequest Request;
        }

        private sealed class Win32Owner : IWin32Window
        {
            public Win32Owner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }

        /// <summary>WebView2 control whose window procedure runs with masked FPU exceptions.</summary>
        private sealed class GuardedWebView : WebView2
        {
            protected override void WndProc(ref Message m)
            {
                using (Fpu.Mask()) base.WndProc(ref m);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hWnd, bool invert);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
