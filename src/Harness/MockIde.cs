using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using PlsqlDevAgent.Ide;

namespace PlsqlDevAgent.Harness
{
    /// <summary>Simulated PL/SQL Developer: the "editor" is a TextBox, queries return canned data.</summary>
    internal sealed class MockIde : IIde
    {
        private readonly HarnessForm _form;
        public readonly List<string> ExecutedSql = new List<string>();

        public MockIde(HarnessForm form) { _form = form; }

        public bool IsReal => false;
        public IntPtr MainWindowHandle => MainThread.Invoke(() => _form.Handle);
        public bool DarkMode => false;
        public string IdeLanguage => "";
        public bool CanShowDifferences => false;

        public IdeContext GetContext() => MainThread.Invoke(() => new IdeContext
        {
            Connected = true,
            User = "SCOTT",
            Database = "XEPDB1",
            IdeVersion = 1700,
            WindowType = IdeWindowType.Program,
            ObjectType = "PACKAGE BODY",
            ObjectOwner = "SCOTT",
            ObjectName = "EMP_API",
            HasEditor = true,
            CursorLine = 1,
            CursorColumn = 1,
            Modified = _form.Editor.Modified,
        });

        public string GetEditorText() => MainThread.Invoke(() => _form.Editor.Text);
        public string GetSelectedText() => MainThread.Invoke(() => _form.Editor.SelectedText);

        public void ReplaceEditorText(string expectedCurrent, string newText) => MainThread.Invoke(() =>
        {
            if (expectedCurrent != null && _form.Editor.Text.Replace("\r\n", "\n").TrimEnd() != expectedCurrent.Replace("\r\n", "\n").TrimEnd())
                throw new IdeException("The editor content changed since it was read.");
            _form.Editor.Text = newText;
        });

        public void NewWindow(IdeWindowType type, string text) =>
            MainThread.Invoke(() => MessageBox.Show(_form, text, "New " + type + " window (simulated)"));

        public List<string> GetAllEditorTexts() => new List<string> { GetEditorText() };

        public bool ActivateEditorWithText(string text) =>
            GetEditorText().Replace("\r\n", "\n").TrimEnd() == (text ?? "").Replace("\r\n", "\n").TrimEnd();

        public bool ObjectAction(string action, string objectType, string owner, string name) => true;
        public bool OpenFile(string path) => true;
        public string GetObjectSource(string objectType, string owner, string name) => "";

        public QueryResult Query(string sql, int maxRows)
        {
            lock (ExecutedSql) ExecutedSql.Add(sql);
            var r = new QueryResult();
            var s = sql.ToUpperInvariant().Replace("DBA_", "ALL_"); // same canned answers with the DBA views setting
            if (s.Contains("CURRENT_SCHEMA")) { r.Columns.Add("SCHEMA"); r.Rows.Add(new[] { "SCOTT" }); }
            else if (s.Contains("ALL_ERRORS")) { r.Columns.AddRange(new[] { "TYPE", "LINE", "POSITION", "ATTRIBUTE", "TEXT" }); }
            else if (s.Contains("COUNT(*)")) { r.Columns.Add("COUNT(*)"); r.Rows.Add(new[] { "14" }); }
            else if (s.Contains("FROM ALL_OBJECTS") && s.Contains(" LIKE "))
            {
                r.Columns.AddRange(new[] { "OWNER", "OBJECT_NAME", "OBJECT_TYPE" });
                r.Rows.Add(new[] { "SCOTT", "EMP", "TABLE" });
                r.Rows.Add(new[] { "SCOTT", "EMP_API", "PACKAGE" });
            }
            else if (s.Contains("FROM ALL_OBJECTS"))
            {
                r.Columns.AddRange(new[] { "OWNER", "OBJECT_NAME", "OBJECT_TYPE", "STATUS" });
                if (s.Contains("'EMP'")) r.Rows.Add(new[] { "SCOTT", "EMP", "TABLE", "VALID" });
                if (s.Contains("'EMP_API'")) r.Rows.Add(new[] { "SCOTT", "EMP_API", "PACKAGE", "VALID" });
            }
            else if (s.Contains("FROM ALL_SOURCE") && s.Contains("'EMP_API'") && !s.Contains("UPPER(TEXT)"))
            {
                r.Columns.Add("TEXT");
                var text = MainThread.Invoke(() => _form.Editor.Text);
                text = System.Text.RegularExpressions.Regex.Replace(text, "^create or replace ", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var line in text.Replace("\r\n", "\n").Split('\n')) r.Rows.Add(new[] { line + "\n" });
            }
            else if (s.Contains("ALL_TAB_COMMENTS") || s.Contains("ALL_CONSTRAINTS") || s.Contains("ALL_INDEXES") || s.Contains("ALL_SYNONYMS") || s.Contains("ALL_ARGUMENTS") || s.Contains("ALL_SOURCE"))
            {
                r.Columns.Add("X");
            }
            else if (s.Contains("ALL_TAB_COLUMNS"))
            {
                r.Columns.AddRange(new[] { "COLUMN_NAME", "DATA_TYPE", "NULLABLE", "COMMENTS" });
                foreach (var c in new[] { "EMPNO|NUMBER(4)|NOT NULL", "ENAME|VARCHAR2(10)|", "JOB|VARCHAR2(9)|", "MGR|NUMBER(4)|",
                                          "HIREDATE|DATE|", "SAL|NUMBER(7,2)|", "COMM|NUMBER(7,2)|", "DEPTNO|NUMBER(2)|" })
                {
                    var p = c.Split('|');
                    r.Rows.Add(new[] { p[0], p[1], p[2], "" });
                }
            }
            else
            {
                r.Columns.AddRange(new[] { "EMPNO", "ENAME", "SAL" });
                r.Rows.Add(new[] { "7369", "SMITH", "800" });
                r.Rows.Add(new[] { "7499", "ALLEN", "1600" });
            }
            r.Rows = r.Rows.Take(maxRows).ToList();
            return r;
        }

        public QueryResult GetResultGrid(int maxRows)
        {
            var r = new QueryResult();
            r.Columns.AddRange(new[] { "DEPTNO", "DNAME" });
            r.Rows.Add(new[] { "10", "ACCOUNTING" });
            r.Rows.Add(new[] { "20", "RESEARCH" });
            return r;
        }

        public ExecResult Execute(string sql)
        {
            lock (ExecutedSql) ExecutedSql.Add(sql);
            return new ExecResult { ErrorCode = 0, DbmsOutput = "" };
        }

        public bool IsExecutingSql => false;

        public void RefreshObject(string objectType, string owner, string name, int action) { }

        public void ShowDifferences(string caption1, string text1, string caption2, string text2) =>
            MainThread.Invoke(() => MessageBox.Show(_form, "IDE diff viewer (simulated)", "Diff"));

        public void SetStatus(string message) => MainThread.Post(() => _form.Text = "Harness – " + message);
        public PopupObject GetPopupObject() => new PopupObject { ObjectType = "TABLE", Owner = "SCOTT", Name = "EMP" };
        public void ResetSession() { }
    }
}
