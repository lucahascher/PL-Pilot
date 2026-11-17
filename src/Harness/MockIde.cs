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

        /// <summary>The joined source query fails like ORA-01489 (a very long line), so Db.ReadSource falls back.</summary>
        public volatile bool FailJoinedSource;
        /// <summary>Oracle's runtime estimate the simulated EXPLAIN PLAN returns (seconds).</summary>
        public volatile int EstimateSeconds = 1;

        /// <summary>The lines of the simulated EMP_API body (the editor text), each with its line feed.</summary>
        private List<string> SourceLines()
        {
            var text = MainThread.Invoke(() => _form.Editor.Text);
            text = System.Text.RegularExpressions.Regex.Replace(text, "^create or replace ", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => l + "\n").ToList();
        }

        public QueryResult Query(string sql, int maxRows)
        {
            lock (ExecutedSql) ExecutedSql.Add(sql);
            var r = new QueryResult();
            var s = sql.ToUpperInvariant().Replace("DBA_", "ALL_"); // same canned answers with the DBA views setting
            if (s.Contains("PDA_SLOW")) System.Threading.Thread.Sleep(1300); // a slow query of the model
            if (s.Contains("PRODUCT_COMPONENT_VERSION"))
            {
                r.Columns.AddRange(new[] { "PRODUCT", "VERSION_FULL" });
                r.Rows.Add(new[] { "Oracle Database 19c Enterprise Edition", "19.24.0.0.0" });
            }
            else if (s.Contains("NLS_SESSION_PARAMETERS"))
            {
                r.Columns.AddRange(new[] { "PARAMETER", "VALUE" });
                foreach (var p in new[] { "NLS_LANGUAGE|GERMAN", "NLS_TERRITORY|GERMANY", "NLS_DATE_FORMAT|DD.MM.RR", "NLS_NUMERIC_CHARACTERS|,.",
                                          "NLS_LENGTH_SEMANTICS|BYTE", "NLS_CHARACTERSET|AL32UTF8", "EDITION|ORA$BASE" })
                    r.Rows.Add(p.Split('|'));
            }
            // impact analysis and compiler settings: nothing depends on anything in the simulated database
            else if (s.Contains("ALL_DEPENDENCIES") || s.Contains("ALL_PLSQL_OBJECT_SETTINGS") || s.Contains("V$PARAMETER")) r.Columns.Add("X");
            // Db.ReadSource: lines joined into rows (LISTAGG), or one row per line (fallback); the "source" is the editor text
            else if (s.Contains("ALL_SOURCE") && s.Contains("COUNT(*) OVER ()"))
            {
                if (s.Contains("LISTAGG") && FailJoinedSource) throw new OracleSqlException(1489, "ORA-01489: result of string concatenation is too long");
                r.Columns.AddRange(new[] { "FIRST", "LAST", "TOTAL", "TEXT" });
                var lines = s.Contains("'EMP_API'") ? SourceLines() : new List<string>();
                var from = System.Text.RegularExpressions.Regex.Match(s, @"LINE >= (\d+)");
                var to = System.Text.RegularExpressions.Regex.Match(s, @"LINE <= (\d+)");
                int first = from.Success ? int.Parse(from.Groups[1].Value) : 1, last = to.Success ? Math.Min(lines.Count, int.Parse(to.Groups[1].Value)) : lines.Count;
                int per = s.Contains("LISTAGG") ? 3 : 1; // three lines per joined row
                for (int i = first; i <= last; i += per)
                {
                    int end = Math.Min(last, i + per - 1);
                    r.Rows.Add(new[] { i.ToString(), end.ToString(), lines.Count.ToString(), string.Concat(lines.Skip(i - 1).Take(end - i + 1)) });
                }
            }
            else if (s.Contains("CURRENT_SCHEMA")) { r.Columns.Add("SCHEMA"); r.Rows.Add(new[] { CurrentSchema }); }
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
            // the procedures and functions in a range of lines (list of what a cut get_object_source does not show)
            else if (s.Contains("ALL_SOURCE") && s.Contains("REGEXP_LIKE"))
            {
                r.Columns.AddRange(new[] { "LINE", "TEXT" });
                var lines = s.Contains("'EMP_API'") ? SourceLines() : new List<string>();
                var between = System.Text.RegularExpressions.Regex.Match(s, @"LINE BETWEEN (\d+) AND (\d+)");
                for (int i = int.Parse(between.Groups[1].Value); i <= Math.Min(lines.Count, int.Parse(between.Groups[2].Value)); i++)
                    if (System.Text.RegularExpressions.Regex.IsMatch(lines[i - 1], @"^\s*(procedure|function)\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        r.Rows.Add(new[] { i.ToString(), lines[i - 1] });
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
            // simulated session state: ALTER SESSION SET CURRENT_SCHEMA = X (plain or "quoted")
            var schema = System.Text.RegularExpressions.Regex.Match(sql, @"^\s*alter\s+session\s+set\s+current_schema\s*=\s*(?:""(?<q>[^""]+)""|(?<n>[\w$#]+))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (schema.Success) CurrentSchema = schema.Groups["q"].Success ? schema.Groups["q"].Value : schema.Groups["n"].Value.ToUpperInvariant();
            // simulated transaction: DML opens one, COMMIT / ROLLBACK / DDL end it (not ALTER SESSION), ROLLBACK TO SAVEPOINT keeps it
            var s = sql.TrimStart().ToUpperInvariant();
            var txn = _transaction;
            if (s.StartsWith("INSERT") || s.StartsWith("UPDATE") || s.StartsWith("DELETE") || s.StartsWith("MERGE")) txn = txn ?? "1.2.3";
            else if (s.StartsWith("COMMIT") || (s.StartsWith("ROLLBACK") && !s.Contains(" TO ")) || s.StartsWith("CREATE") || s.StartsWith("DROP") ||
                     (s.StartsWith("ALTER") && !s.StartsWith("ALTER SESSION"))) txn = null;
            if (txn != _transaction) { _transaction = txn; TransactionChanged?.Invoke(); }
            // QueryRunner.Estimate: EXPLAIN PLAN in an autonomous block, the estimate comes back as DBMS_OUTPUT
            if (sql.Contains("PDA_ESTIMATE")) return new ExecResult { ErrorCode = 0, DbmsOutput = $"PDA_ESTIMATE {EstimateSeconds}|4711|1000\n" };
            return new ExecResult { ErrorCode = 0, DbmsOutput = "" };
        }

        public bool IsExecutingSql => false;
        private string _transaction;
        public string PendingTransaction => _transaction;
        public event Action TransactionChanged;
        /// <summary>False simulates the agent sharing PL/SQL Developer's main session with the user's windows.</summary>
        public bool UsesOwnSession { get; set; } = true;

        /// <summary>Current schema of the simulated session (set by ALTER SESSION, or by the "user's windows" in tests).</summary>
        public volatile string CurrentSchema = "SCOTT";
        public string DatabaseIdentity => "XE|XE|XEPDB1|XEPDB1|mockhost|XE";
        public event Action DatabaseIdentityChanged { add { } remove { } }

        public void RefreshObject(string objectType, string owner, string name, int action) { }

        public void ShowDifferences(string caption1, string text1, string caption2, string text2) =>
            MainThread.Invoke(() => MessageBox.Show(_form, "IDE diff viewer (simulated)", "Diff"));

        public void SetStatus(string message) => MainThread.Post(() => _form.Text = "Harness – " + message);
        public PopupObject GetPopupObject() => new PopupObject { ObjectType = "TABLE", Owner = "SCOTT", Name = "EMP" };
        public void ResetSession() { }
    }
}
