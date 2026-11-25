using System;
using System.Collections.Generic;

namespace PlsqlDevAgent.Ide
{
    /// <summary>
    /// Passes every call to another <see cref="IIde"/>; derived classes change single calls (e.g. rewrite the agent's SQL
    /// before it runs). Used for the tools' view of the IDE (<c>ToolContext.Ide</c>).
    /// </summary>
    internal abstract class IdeDecorator : IIde
    {
        protected readonly IIde Inner;

        protected IdeDecorator(IIde inner) { Inner = inner; }

        public bool Wraps(IIde ide) => ReferenceEquals(Inner, ide);

        public virtual IdeContext GetContext() => Inner.GetContext();
        public virtual IdeContext GetWindowContext(EditorTarget window) => Inner.GetWindowContext(window);
        public virtual string GetEditorText(EditorTarget window = null) => Inner.GetEditorText(window);
        public virtual string GetSelectedText(EditorTarget window = null) => Inner.GetSelectedText(window);
        public virtual void ReplaceEditorText(string expectedCurrent, string newText, EditorTarget window = null) => Inner.ReplaceEditorText(expectedCurrent, newText, window);
        public virtual void NewWindow(IdeWindowType type, string text) => Inner.NewWindow(type, text);
        public virtual List<string> GetAllEditorTexts() => Inner.GetAllEditorTexts();
        public virtual bool ActivateEditorWithText(string text) => Inner.ActivateEditorWithText(text);
        public virtual bool ObjectAction(string action, string objectType, string owner, string name) => Inner.ObjectAction(action, objectType, owner, name);
        public virtual bool OpenFile(string path) => Inner.OpenFile(path);
        public virtual string GetObjectSource(string objectType, string owner, string name) => Inner.GetObjectSource(objectType, owner, name);
        public virtual QueryResult Query(string sql, int maxRows) => Inner.Query(sql, maxRows);
        public virtual QueryResult GetResultGrid(int maxRows, EditorTarget window = null) => Inner.GetResultGrid(maxRows, window);
        public virtual ExecResult Execute(string sql) => Inner.Execute(sql);
        public bool IsExecutingSql => Inner.IsExecutingSql;
        public string PendingTransaction => Inner.PendingTransaction;
        public event Action TransactionChanged { add => Inner.TransactionChanged += value; remove => Inner.TransactionChanged -= value; }
        public bool UsesOwnSession => Inner.UsesOwnSession;
        public string DatabaseIdentity => Inner.DatabaseIdentity;
        public event Action DatabaseIdentityChanged { add => Inner.DatabaseIdentityChanged += value; remove => Inner.DatabaseIdentityChanged -= value; }
        public virtual void RefreshObject(string objectType, string owner, string name, int action) => Inner.RefreshObject(objectType, owner, name, action);
        public virtual void ShowDifferences(string caption1, string text1, string caption2, string text2) => Inner.ShowDifferences(caption1, text1, caption2, text2);
        public virtual void SetStatus(string message) => Inner.SetStatus(message);
        public virtual PopupObject GetPopupObject() => Inner.GetPopupObject();
        public virtual void ResetSession() => Inner.ResetSession();
        public IntPtr MainWindowHandle => Inner.MainWindowHandle;
        public bool DarkMode => Inner.DarkMode;
        public bool CanShowDifferences => Inner.CanShowDifferences;
        public string IdeLanguage => Inner.IdeLanguage;
        public bool IsReal => Inner.IsReal;
    }
}
