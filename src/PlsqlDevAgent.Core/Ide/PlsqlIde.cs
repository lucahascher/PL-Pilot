using System;
using System.Linq;
using PlsqlDevAgent.Native;
using static PlsqlDevAgent.Native.Callbacks;

namespace PlsqlDevAgent.Ide
{
    /// <summary>IIde implemented on top of the PL/SQL Developer plug-in callbacks.</summary>
    public sealed class PlsqlIde : IIde
    {
        private readonly int _pluginId;

        /// <summary>Use a separate Oracle session for the agent (does not touch the user's transaction).</summary>
        public bool UseSeparateSession { get; set; } = true;

        public PlsqlIde(int pluginId)
        {
            _pluginId = pluginId;
        }

        public bool IsReal => true;

        public IntPtr MainWindowHandle => MainThread.Invoke(() => new IntPtr(IDE_GetWindowHandle?.Invoke() ?? 0));

        public bool CanShowDifferences => IDE_ShowDifferences != null;

        public string IdeLanguage => MainThread.Invoke(() => IDE_TranslationLanguage != null ? Str(IDE_TranslationLanguage()) : "");

        private string _darkLogged;

        /// <summary>
        /// PL/SQL Developer 17 has VCL styles (IDE_GetCurrentStyle) and newer color themes such as
        /// "Windows 11 Dark" that follow the Windows setting and are not reported by that callback.
        /// Checked in order: style callback, theme preferences, Windows app theme.
        /// </summary>
        public bool DarkMode => MainThread.Invoke(() =>
        {
            bool result;
            string source;
            int flag = 0;
            string style = "";
            if (IDE_GetCurrentStyle != null) { IDE_GetCurrentStyle(out flag, out var namePtr); style = Str(namePtr); }

            var themePref = ReadPref("ColorTheme") ?? ReadPref("Theme") ?? ReadPref("Style");
            if (flag != 0 || Contains(style, "dark")) { result = true; source = $"IDE style '{style}'"; }
            else if (!string.IsNullOrEmpty(style) && !Contains(style, "windows")) { result = false; source = $"IDE style '{style}'"; }
            else if (themePref != null && (Contains(themePref, "dark") || Contains(themePref, "light")))
            { result = Contains(themePref, "dark"); source = $"IDE theme preference '{themePref}'"; }
            else { result = WindowsAppsUseDarkTheme(); source = "Windows app theme"; }

            var log = $"{source} -> dark={result}";
            if (_darkLogged != log) { Util.Log.Info("Theme detection: " + log); _darkLogged = log; }
            return result;
        });

        private static string ReadPref(string name)
        {
            foreach (var fn in new[] { IDE_GetUserPref, IDE_GetGeneralPref })
            {
                if (fn == null) continue;
                try
                {
                    using (var n = new NativeString(name))
                    {
                        var v = Str(fn(n.Ptr));
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                }
                catch { }
            }
            return null;
        }

        private static bool Contains(string s, string what) => s != null && s.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool WindowsAppsUseDarkTheme()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        public IdeContext GetContext() => MainThread.Invoke(() =>
        {
            var ctx = new IdeContext
            {
                IdeVersion = SYS_Version?.Invoke() ?? 0,
                Connected = IDE_Connected?.Invoke() != 0,
            };
            if (ctx.Connected && IDE_GetConnectionInfo != null)
            {
                IDE_GetConnectionInfo(out var u, out _, out var d); // password is never read
                ctx.User = Str(u);
                ctx.Database = Str(d);
                ctx.ConnectAs = IDE_GetConnectAs != null ? Str(IDE_GetConnectAs()) : "";
            }

            ctx.WindowType = (IdeWindowType)(IDE_GetWindowType?.Invoke() ?? 0);
            if (ctx.WindowType != IdeWindowType.None)
            {
                ctx.FileName = IDE_Filename != null ? Str(IDE_Filename()) : "";
                ctx.HasEditor = IDE_WindowHasEditor?.Invoke(1) != 0;
                ctx.Modified = IDE_WindowIsModified?.Invoke() != 0;
                ctx.ReadOnly = IDE_GetReadOnly?.Invoke() != 0;
                if (ctx.HasEditor)
                {
                    ctx.CursorLine = IDE_GetCursorY?.Invoke() ?? 0;
                    ctx.CursorColumn = IDE_GetCursorX?.Invoke() ?? 0;
                }
                if (IDE_GetWindowObject != null && IDE_GetWindowObject(out var t, out var o, out var n, out _) != 0)
                {
                    ctx.ObjectType = Str(t);
                    ctx.ObjectOwner = Str(o);
                    ctx.ObjectName = Str(n);
                }
            }
            return ctx;
        });

        public string GetEditorText() => MainThread.Invoke(() =>
        {
            RequireEditor();
            return Str(IDE_GetText());
        });

        public string GetSelectedText() => MainThread.Invoke(() =>
        {
            if ((IDE_GetWindowType?.Invoke() ?? 0) == 0) return "";
            return Str(IDE_GetSelectedText());
        });

        public void ReplaceEditorText(string expectedCurrent, string newText) => MainThread.Invoke(() =>
        {
            RequireEditor();
            var current = Str(IDE_GetText());
            if (expectedCurrent != null && Normalize(current) != Normalize(expectedCurrent))
                throw new IdeException("The editor content changed since it was read (did the user edit it or switch windows?). Read it again and retry.");
            if (IDE_GetReadOnly?.Invoke() != 0)
                throw new IdeException("The active editor is read-only.");

            // The plug-in interface only transfers the Windows ANSI code page. If the editor already contains
            // other characters, they were read as '?' and writing the text back would destroy them.
            var existing = FirstUnrepresentable(ReadEditorUnicode());
            if (existing != null)
                throw new IdeException($"The editor contains the character '{existing}', which the PL/SQL Developer plug-in interface " +
                    $"cannot transfer (only Windows code page {Enc.CodePage}); it was read as '?'. Changing the text would destroy such " +
                    "characters, so nothing was changed. Tell the user and suggest making this edit manually.");
            RequireRepresentable(newText, "new editor text");

            int x = IDE_GetCursorX?.Invoke() ?? 0, y = IDE_GetCursorY?.Invoke() ?? 0;
            using (var s = new NativeString(newText))
            {
                if (IDE_SetText(s.Ptr) == 0) throw new IdeException("PL/SQL Developer refused to set the editor text.");
            }
            if (x > 0 && y > 0) IDE_SetCursor?.Invoke(x, y);
        });

        public void NewWindow(IdeWindowType type, string text) => MainThread.Invoke(() =>
        {
            RequireRepresentable(text, "window text");
            using (var s = new NativeString(text)) IDE_CreateWindow((int)type, s.Ptr, 0);
        });

        public System.Collections.Generic.List<string> GetAllEditorTexts() => MainThread.Invoke(() =>
        {
            var list = new System.Collections.Generic.List<string>();
            ForEachEditorWindow((i, text) => { list.Add(text); return false; });
            return list;
        });

        public bool ActivateEditorWithText(string text) => MainThread.Invoke(() =>
        {
            var wanted = Normalize(text);
            // the active window first (the usual case)
            if ((IDE_GetWindowType?.Invoke() ?? 0) != 0 && IDE_WindowHasEditor?.Invoke(1) != 0 && Normalize(Str(IDE_GetText())) == wanted) return true;
            int found = -1;
            ForEachEditorWindow((i, t) => { if (Normalize(t) != wanted) return false; found = i; return true; });
            if (found < 0) return false;
            IDE_ActivateWindow(found);
            IDE_SelectWindow?.Invoke(0); // the activated window is now the top one
            return Normalize(Str(IDE_GetText())) == wanted;
        });

        /// <summary>
        /// Calls <paramref name="visit"/>(index, text) for every child window with a code editor until it returns true.
        /// IDE_SelectWindow points the plug-in functions to a window without activating it; selection is reset to the
        /// top (active) window afterwards.
        /// </summary>
        private static void ForEachEditorWindow(Func<int, string, bool> visit)
        {
            if (IDE_GetWindowCount == null || IDE_SelectWindow == null) return;
            int count = IDE_GetWindowCount();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (IDE_SelectWindow(i) == 0) continue;
                    if ((IDE_GetWindowType?.Invoke() ?? 0) == 0 || IDE_WindowHasEditor?.Invoke(1) == 0) continue;
                    if (visit(i, Str(IDE_GetText()))) return;
                }
            }
            finally
            {
                IDE_SelectWindow(0);
            }
        }

        /// <summary>
        /// Text of the active editor read directly from the editor control (UTF-16, lossless), or null.
        /// Only used to detect characters the plug-in interface cannot transfer.
        /// </summary>
        public string ReadEditorUnicode() => MainThread.Invoke(() =>
        {
            try
            {
                var h = new IntPtr(IDE_GetEditorHandle?.Invoke() ?? 0);
                if (h == IntPtr.Zero) return null;
                int len = (int)SendMessage(h, 0x000E /* WM_GETTEXTLENGTH */, IntPtr.Zero, IntPtr.Zero);
                if (len <= 0) return null;
                var sb = new System.Text.StringBuilder(len + 1);
                SendMessage(h, 0x000D /* WM_GETTEXT */, new IntPtr(len + 1), sb);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                Util.Log.Error("Reading the editor text via WM_GETTEXT failed", ex);
                return null;
            }
        });

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, System.Text.StringBuilder lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>Refuses text with characters the plug-in interface would turn into '?' (after transliteration).</summary>
        private static void RequireRepresentable(string text, string what)
        {
            var bad = FirstUnrepresentable(Transliterate(text));
            if (bad != null)
                throw new IdeException($"The {what} contains the character '{bad}', which the PL/SQL Developer plug-in interface cannot " +
                    $"transfer (only Windows code page {Enc.CodePage}; it would become '?'). Nothing was done. Use other characters " +
                    "(for example plain ASCII) and try again.");
        }

        public bool ObjectAction(string action, string objectType, string owner, string name) => MainThread.Invoke(() =>
        {
            if (IDE_ObjectAction == null) return false;
            using (var a = new NativeString(action))
            using (var t = new NativeString(objectType))
            using (var o = new NativeString(owner))
            using (var n = new NativeString(name))
                return IDE_ObjectAction(a.Ptr, t.Ptr, o.Ptr, n.Ptr) != 0;
        });

        public bool OpenFile(string path) => MainThread.Invoke(() =>
        {
            using (var p = new NativeString(path)) return IDE_OpenFile(0, p.Ptr) != 0;
        });

        public string GetObjectSource(string objectType, string owner, string name) => MainThread.Invoke(() =>
        {
            RequireConnection();
            if (IDE_GetObjectSource == null) return "";
            using (var t = new NativeString(objectType))
            using (var o = new NativeString(owner))
            using (var n = new NativeString(name))
                return Str(IDE_GetObjectSource(t.Ptr, o.Ptr, n.Ptr));
        });

        public QueryResult Query(string sql, int maxRows)
        {
            RequireRepresentable(sql, "query");
            return RunSql(sql, () =>
            {
                int rc;
                using (var s = new NativeString(sql)) rc = SQL_Execute(s.Ptr);
                if (rc != 0) throw new OracleSqlException(rc, Str(SQL_ErrorMessage()));

                var result = new QueryResult();
                int fields = SQL_FieldCount();
                for (int i = 0; i < fields; i++) result.Columns.Add(Str(SQL_FieldName(i)));
                while (SQL_Eof() == 0)
                {
                    if (result.Rows.Count >= maxRows) { result.Truncated = true; break; }
                    var row = new string[fields];
                    for (int i = 0; i < fields; i++) row[i] = Str(SQL_Field(i));
                    result.Rows.Add(row);
                    rc = SQL_Next();
                    if (rc != 0) throw new OracleSqlException(rc, Str(SQL_ErrorMessage()));
                }
                return result;
            });
        }

        /// <summary>
        /// Grid layout as observed in PL/SQL Developer 17 (see SelfTest): row 0 holds the column titles, data rows
        /// follow; column 0 is an indicator column, column 1 the row number and there are filler columns without a
        /// title at the end. Only columns with a title are returned; the last data row may be at index RowCount.
        /// </summary>
        public QueryResult GetResultGrid(int maxRows) => MainThread.Invoke(() =>
        {
            var result = new QueryResult();
            if (IDE_ResultGridRowCount == null || IDE_ResultGridCell == null) throw new IdeException("This PL/SQL Developer version does not expose the result grid.");
            int rows = IDE_ResultGridRowCount();
            int cols = IDE_ResultGridColCount();
            if (rows <= 0 || cols <= 0) return result;
            var dataCols = new System.Collections.Generic.List<int>();
            for (int c = 0; c < cols; c++)
            {
                var title = Str(IDE_ResultGridCell(c, 0));
                if (string.IsNullOrWhiteSpace(title)) continue;
                dataCols.Add(c);
                result.Columns.Add(title.Trim());
            }
            for (int r = 1; r <= rows; r++)
            {
                var row = dataCols.Select(c => Str(IDE_ResultGridCell(c, r))).ToArray();
                if (row.All(string.IsNullOrEmpty)) continue;
                if (result.Rows.Count >= maxRows) { result.Truncated = true; break; }
                result.Rows.Add(row);
            }
            return result;
        });

        public ExecResult Execute(string sql)
        {
            RequireRepresentable(sql, "statement");
            string transaction = null;
            var result = RunSql(sql, () =>
            {
                int rc;
                using (var s = new NativeString(sql)) rc = SQL_Execute(s.Ptr);
                var r = new ExecResult { ErrorCode = rc };
                if (rc != 0) r.ErrorMessage = Str(SQL_ErrorMessage());
                if (SQL_GetDBMSGetOutput != null) r.DbmsOutput = Str(SQL_GetDBMSGetOutput());
                transaction = ReadTransaction();
                return r;
            });
            SetTransaction(transaction);
            return result;
        }

        public bool IsExecutingSql => SqlThread.IsBusy;

        private volatile string _transaction;
        public string PendingTransaction => _transaction;
        public event Action TransactionChanged;
        public bool UsesOwnSession => !_sessionReady ? UseSeparateSession : _ownSession;

        /// <summary>Id of the open transaction of the plug-in's session (dbms_transaction needs no extra privileges); null if none.</summary>
        private static string ReadTransaction()
        {
            using (var s = new NativeString("select dbms_transaction.local_transaction_id from dual"))
                if (SQL_Execute(s.Ptr) != 0) return null;
            if (SQL_Eof() != 0) return null;
            var id = Str(SQL_Field(0));
            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }

        private void SetTransaction(string id)
        {
            if (_transaction == id) return;
            _transaction = id;
            Util.Log.Info(id == null ? "Agent session: no open transaction" : "Agent session: open transaction " + id);
            try { TransactionChanged?.Invoke(); } catch (Exception ex) { Util.Log.Error("TransactionChanged handler failed", ex); }
        }

        public void RefreshObject(string objectType, string owner, string name, int action) => MainThread.Invoke(() =>
        {
            if (IDE_RefreshObject == null) return;
            using (var t = new NativeString(objectType))
            using (var o = new NativeString(owner))
            using (var n = new NativeString(name))
                IDE_RefreshObject(t.Ptr, o.Ptr, n.Ptr, action);
        });

        public void ShowDifferences(string caption1, string text1, string caption2, string text2) => MainThread.Invoke(() =>
        {
            if (IDE_ShowDifferences == null) throw new IdeException("This PL/SQL Developer version has no difference viewer callback.");
            using (var c1 = new NativeString(caption1))
            using (var t1 = new NativeString(text1))
            using (var c2 = new NativeString(caption2))
            using (var t2 = new NativeString(text2))
                IDE_ShowDifferences(_pluginId, c1.Ptr, t1.Ptr, c2.Ptr, t2.Ptr);
        });

        public void SetStatus(string message) => MainThread.Post(() =>
        {
            if ((IDE_GetWindowType?.Invoke() ?? 0) == 0) return;
            using (var s = new NativeString(message)) IDE_SetStatusMessage?.Invoke(s.Ptr);
        });

        /// <summary>Only valid while handling a popup menu click (main thread).</summary>
        public PopupObject GetPopupObject() => MainThread.Invoke(() =>
        {
            if (IDE_GetPopupObject == null) return null;
            IDE_GetPopupObject(out var t, out var o, out var n, out var s);
            return new PopupObject { ObjectType = Str(t), Owner = Str(o), Name = Str(n), SubObject = Str(s) };
        });

        /// <summary>Connection changed or the session setting was switched: the next statement sets up the session again.</summary>
        public void ResetSession()
        {
            _sessionReady = false;
            if (_transaction != null)
                Util.Log.Warn("The agent's session was reset (connection change / setting) while transaction " + _transaction + " was open");
            SetTransaction(null);
        }

        // ---- SQL execution ----

        /// <summary>How long the agent waits for one statement.</summary>
        private static readonly TimeSpan SqlTimeout = TimeSpan.FromMinutes(10);

        private volatile bool _sessionReady;
        /// <summary>The plug-in has its own Oracle session: its SQL runs on <see cref="SqlThread"/>, not on the IDE main thread.</summary>
        private volatile bool _ownSession;
        private volatile bool _outputEnabled;

        /// <summary>
        /// Runs SQL_* callbacks. With the plug-in's own session they run on the SQL thread, so PL/SQL Developer stays
        /// responsive during long statements; with the IDE's main session (setting off, or single session mode) they
        /// have to run on the main thread, because the IDE uses that session itself.
        /// </summary>
        private T RunSql<T>(string sql, Func<T> body)
        {
            var ct = IdeCancellation.Current;
            ct.ThrowIfCancellationRequested();
            MainThread.Invoke(RequireConnection);
            if (!_sessionReady)
            {
                // never switch the session under a statement that is still running (e.g. one the user stopped waiting for)
                if (SqlThread.IsBusy && !MainThread.IsMainThread) SqlThread.WaitIdle(ct, SqlTimeout);
                MainThread.Invoke(EnsureSession);
            }

            T Run()
            {
                if (!_outputEnabled)
                {
                    using (var s = new NativeString("begin dbms_output.enable(null); end;")) SQL_Execute(s.Ptr);
                    _outputEnabled = true;
                }
                return body();
            }

            return _ownSession ? SqlThread.Run(OneLine(sql), Run, ct, SqlTimeout) : MainThread.Invoke(Run);
        }

        private static string OneLine(string sql)
        {
            var s = System.Text.RegularExpressions.Regex.Replace(sql ?? "", @"\s+", " ").Trim();
            return s.Length > 150 ? s.Substring(0, 147) + "..." : s;
        }

        // ---- helpers (main thread only) ----

        private void EnsureSession()
        {
            if (_sessionReady) return;
            bool own = false;
            if (UseSeparateSession && SQL_UsePlugInSession != null)
            {
                own = SQL_UsePlugInSession(_pluginId) != 0;
                Util.Log.Info("SQL_UsePlugInSession -> " + own + (own ? " (agent SQL runs in the background)" : " (agent SQL runs on the IDE main thread)"));
            }
            else
            {
                SQL_UseDefaultSession?.Invoke(_pluginId);
            }
            _ownSession = own;
            _outputEnabled = false;
            _sessionReady = true;
        }

        private static void RequireConnection()
        {
            if (IDE_Connected?.Invoke() == 0) throw new IdeException("PL/SQL Developer is not connected to a database.");
        }

        private static void RequireEditor()
        {
            if ((IDE_GetWindowType?.Invoke() ?? 0) == 0 || IDE_WindowHasEditor?.Invoke(1) == 0)
                throw new IdeException("There is no active editor window (SQL, Program, Test or Command window).");
        }

        private static string Normalize(string s) => (s ?? "").Replace("\r\n", "\n").TrimEnd();
    }
}
