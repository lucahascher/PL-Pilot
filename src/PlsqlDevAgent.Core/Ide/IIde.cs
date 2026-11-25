using System;
using System.Collections.Generic;
using System.Text;

namespace PlsqlDevAgent.Ide
{
    /// <summary>
    /// Everything the agent needs from the IDE. Implementations must be callable from any
    /// thread (the real one marshals to the PL/SQL Developer main thread).
    /// </summary>
    public interface IIde
    {
        IdeContext GetContext();

        /// <summary>
        /// State of the editor window <paramref name="window"/> (null: the active window), also when the user has activated
        /// another window meanwhile. Throws <see cref="IdeException"/> if that window was closed.
        /// </summary>
        IdeContext GetWindowContext(EditorTarget window);

        /// <summary>Text of the editor window <paramref name="window"/> (null: the active window).</summary>
        string GetEditorText(EditorTarget window = null);
        string GetSelectedText(EditorTarget window = null);

        /// <summary>
        /// Replaces the text of the editor window <paramref name="window"/> (null: the active window) if it still equals
        /// <paramref name="expectedCurrent"/>. The window is not brought to the front.
        /// </summary>
        void ReplaceEditorText(string expectedCurrent, string newText, EditorTarget window = null);

        void NewWindow(IdeWindowType type, string text);

        /// <summary>Texts of all open editor windows (for rewinding edits in windows that are not active).</summary>
        List<string> GetAllEditorTexts();

        /// <summary>Activates the editor window whose text equals <paramref name="text"/> (line ends ignored); false if there is none.</summary>
        bool ActivateEditorWithText(string text);
        bool ObjectAction(string action, string objectType, string owner, string name);
        bool OpenFile(string path);
        string GetObjectSource(string objectType, string owner, string name);

        QueryResult Query(string sql, int maxRows);

        /// <summary>Rows shown in the result grid of the SQL/Test window <paramref name="window"/> (null: the active window).</summary>
        QueryResult GetResultGrid(int maxRows, EditorTarget window = null);
        ExecResult Execute(string sql);

        /// <summary>
        /// A statement of the agent is still executing in the database, e.g. one the user stopped waiting for
        /// (it cannot be interrupted); the agent's next database call waits for it.
        /// </summary>
        bool IsExecutingSql { get; }

        /// <summary>
        /// Id of the open transaction in the agent's database session (uncommitted changes), null if there is none.
        /// Checked after every statement the agent executes.
        /// </summary>
        string PendingTransaction { get; }

        /// <summary>Raised (on any thread) when <see cref="PendingTransaction"/> changes.</summary>
        event Action TransactionChanged;

        /// <summary>
        /// The agent works in its own database session. False: it shares PL/SQL Developer's main session, so a
        /// commit or rollback there would also affect the user's own changes.
        /// </summary>
        bool UsesOwnSession { get; }

        /// <summary>
        /// Names of the database the session is really connected to (database, unique and service name, container,
        /// server host, instance), separated by '|'; null while unknown. The production guard matches them as well,
        /// so a protected database is recognized however it was reached (alias, EZConnect, IP address).
        /// On the main thread it never waits (it loads in the background and raises <see cref="DatabaseIdentityChanged"/>).
        /// </summary>
        string DatabaseIdentity { get; }

        event Action DatabaseIdentityChanged;
        void RefreshObject(string objectType, string owner, string name, int action);

        void ShowDifferences(string caption1, string text1, string caption2, string text2);
        void SetStatus(string message);
        PopupObject GetPopupObject();
        void ResetSession();

        IntPtr MainWindowHandle { get; }
        bool DarkMode { get; }
        /// <summary>The IDE's difference viewer is available (PL/SQL Developer 14+).</summary>
        bool CanShowDifferences { get; }
        /// <summary>Language of PL/SQL Developer's translation file ("" = no translation, i.e. English).</summary>
        string IdeLanguage { get; }
        bool IsReal { get; }
    }

    public enum IdeWindowType
    {
        None = 0, Sql = 1, Test = 2, Program = 3, Command = 4, Plan = 5, Report = 6, Html = 7,
    }

    public sealed class PopupObject
    {
        public string ObjectType, Owner, Name, SubObject;
        public override string ToString() =>
            $"{ObjectType} {(string.IsNullOrEmpty(Owner) ? "" : Owner + ".")}{Name}{(string.IsNullOrEmpty(SubObject) ? "" : "." + SubObject)}";
    }

    public sealed class IdeContext
    {
        public bool Connected;
        public string User, Database, ConnectAs;
        public int IdeVersion;
        public IdeWindowType WindowType;
        public string FileName;
        public string ObjectType, ObjectOwner, ObjectName;
        public int CursorLine, CursorColumn;
        public bool HasEditor, Modified, ReadOnly;
        /// <summary>Window handle of the editor control (identifies the window while it is open); 0 if unknown.</summary>
        public long EditorHandle;

        public string ConnectionLabel => Connected
            ? $"{User}@{Database}{(string.IsNullOrEmpty(ConnectAs) || ConnectAs == "Normal" ? "" : " as " + ConnectAs)}"
            : "not connected";

        private string WindowTypeUi => !Util.L.German ? WindowType.ToString()
            : WindowType == IdeWindowType.Sql ? "SQL" : WindowType == IdeWindowType.Program ? "Programm"
            : WindowType == IdeWindowType.Command ? "Befehls" : WindowType.ToString();

        /// <summary>WindowLabel in the UI language (WindowLabel itself stays English: it is also sent to the model).</summary>
        public string WindowLabelUi
        {
            get
            {
                if (!Util.L.German) return WindowLabel;
                if (WindowType == IdeWindowType.None) return "kein Editorfenster aktiv";
                var sb = new StringBuilder(WindowTypeUi + "-Fenster");
                if (!string.IsNullOrEmpty(ObjectName)) sb.Append($" für {ObjectType} {ObjectOwner}.{ObjectName}");
                if (!string.IsNullOrEmpty(FileName)) sb.Append($", Datei {FileName}");
                if (HasEditor) sb.Append($", Cursor in Zeile {CursorLine}, Spalte {CursorColumn}");
                if (Modified) sb.Append(", ungespeicherte Änderungen");
                if (ReadOnly) sb.Append(", schreibgeschützt");
                return sb.ToString();
            }
        }

        /// <summary>Short name of the window in the UI language, e.g. "Program window (PACKAGE BODY SCOTT.EMP_API)".</summary>
        public string WindowNameUi
        {
            get
            {
                var name = Util.L.German ? WindowTypeUi + "-Fenster" : WindowType + " window";
                if (!string.IsNullOrEmpty(ObjectName)) return $"{name} ({ObjectType} {ObjectOwner}.{ObjectName})";
                if (!string.IsNullOrEmpty(FileName)) return $"{name} ({System.IO.Path.GetFileName(FileName)})";
                return name;
            }
        }

        /// <summary>Short English name of the window for the model, e.g. "Program window for PACKAGE BODY SCOTT.EMP_API".</summary>
        public string WindowName
        {
            get
            {
                if (WindowType == IdeWindowType.None) return "no editor window";
                if (!string.IsNullOrEmpty(ObjectName)) return $"{WindowType} window for {ObjectType} {ObjectOwner}.{ObjectName}";
                if (!string.IsNullOrEmpty(FileName)) return $"{WindowType} window for file {FileName}";
                return $"{WindowType} window (unsaved, no file)";
            }
        }

        /// <summary>" in the Program window for PACKAGE BODY X.Y" – used when quoting a selection.</summary>
        public string LabelForPrompt()
        {
            if (WindowType == IdeWindowType.None) return "";
            var s = Util.L.T(" from the {0} window", WindowTypeUi);
            if (!string.IsNullOrEmpty(ObjectName)) s += $" ({ObjectType} {ObjectOwner}.{ObjectName})";
            else if (!string.IsNullOrEmpty(FileName)) s += $" ({System.IO.Path.GetFileName(FileName)})";
            return s;
        }

        /// <summary>
        /// Identifies the window by what it shows (type, object, file), e.g. to remember that the user excluded it from the
        /// chat. Unsaved windows of the same type without an object share one key. Null if no editor window is active.
        /// </summary>
        public string WindowKey => WindowType == IdeWindowType.None ? null
            : $"{WindowType}|{ObjectType}|{ObjectOwner}|{ObjectName}|{FileName}".ToUpperInvariant();

        public string WindowLabel
        {
            get
            {
                if (WindowType == IdeWindowType.None) return "no editor window active";
                var sb = new StringBuilder(WindowType + " window");
                if (!string.IsNullOrEmpty(ObjectName)) sb.Append($" for {ObjectType} {ObjectOwner}.{ObjectName}");
                if (!string.IsNullOrEmpty(FileName)) sb.Append($", file {FileName}");
                if (HasEditor) sb.Append($", cursor at line {CursorLine} col {CursorColumn}");
                if (Modified) sb.Append(", unsaved changes");
                if (ReadOnly) sb.Append(", read-only");
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// The editor window a request works on: the window that was active when the user sent it (or the one the agent
    /// opened). The editor tools keep working on it when the user activates another window meanwhile. It is found again
    /// by its editor control and, if that is gone (window closed and opened again), by the object or file it shows.
    /// </summary>
    public sealed class EditorTarget
    {
        public long EditorHandle;
        /// <summary><see cref="IdeContext.WindowKey"/> when the window was chosen.</summary>
        public string Key;
        /// <summary>For the model, e.g. "Program window for PACKAGE BODY SCOTT.EMP_API" (English).</summary>
        public string Name;
        /// <summary>For the user, in the UI language.</summary>
        public string NameUi;

        /// <summary>The window shows a database object or a file: another window showing the same is just as good.</summary>
        public bool HasIdentity => Key != null && !Key.EndsWith("||||", StringComparison.Ordinal);

        public static EditorTarget From(IdeContext c) => c == null || !c.HasEditor || c.WindowKey == null ? null : new EditorTarget
        {
            EditorHandle = c.EditorHandle,
            Key = c.WindowKey,
            Name = c.WindowName,
            NameUi = c.WindowNameUi,
        };

        /// <summary>
        /// The context describes this window: a window with an object or file must still show it (the same editor control
        /// is preferred, see <see cref="IsSameControl"/>); an unsaved window without one is recognized by its editor control.
        /// </summary>
        public bool Matches(IdeContext c) =>
            c != null && c.HasEditor && (HasIdentity ? c.WindowKey == Key : IsSameControl(c));

        public bool IsSameControl(IdeContext c) => c != null && EditorHandle != 0 && c.EditorHandle == EditorHandle;

        public override string ToString() => Name;
    }

    public sealed class QueryResult
    {
        public List<string> Columns = new List<string>();
        public List<string[]> Rows = new List<string[]>();
        public bool Truncated;
    }

    public sealed class ExecResult
    {
        public int ErrorCode;
        public string ErrorMessage;
        public string DbmsOutput;
        public bool Ok => ErrorCode == 0;
        /// <summary>ORA-24344: success with compilation error.</summary>
        public bool CompiledWithErrors => ErrorCode == 24344;
    }

    public sealed class OracleSqlException : Exception
    {
        public int Code { get; }
        public OracleSqlException(int code, string message)
            : base(string.IsNullOrWhiteSpace(message) ? $"ORA-{code:00000}" : message.Trim()) { Code = code; }
    }

    public sealed class IdeException : Exception
    {
        public IdeException(string message) : base(message) { }
    }
}
