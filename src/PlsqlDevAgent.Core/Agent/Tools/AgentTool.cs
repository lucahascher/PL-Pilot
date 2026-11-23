using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Ide;

namespace PlsqlDevAgent.Agent.Tools
{
    public abstract class AgentTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract JObject Parameters { get; }

        /// <summary>Changes something (editor, database, files). Hidden in read-only mode.</summary>
        public virtual bool Mutating => false;

        /// <summary>Only offered when a workspace folder is configured.</summary>
        public virtual bool NeedsWorkspace => false;

        /// <summary>One line shown in the chat for this call.</summary>
        public virtual string Summarize(JObject args) => "";

        /// <summary>Most characters of this tool's result in the conversation (longer results are cut).</summary>
        public virtual int OutputLimit(AgentConfig cfg) => cfg.ToolOutputLimit;

        public abstract Task<string> ExecuteAsync(JObject args, ToolContext ctx);

        public JObject ToOpenAiTool() => new JObject
        {
            ["type"] = "function",
            ["function"] = new JObject
            {
                ["name"] = Name,
                ["description"] = Description,
                ["parameters"] = Parameters,
            },
        };

        // ---- schema helpers ----

        protected static JObject Schema(params Param[] ps)
        {
            var props = new JObject();
            var required = new JArray();
            foreach (var p in ps)
            {
                var prop = new JObject { ["type"] = p.Type, ["description"] = p.Description };
                if (p.Enum != null) prop["enum"] = new JArray(p.Enum);
                props[p.Name] = prop;
                if (p.Required) required.Add(p.Name);
            }
            var schema = new JObject { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) schema["required"] = required;
            return schema;
        }

        protected sealed class Param
        {
            public string Name, Type, Description;
            public bool Required;
            public string[] Enum;
        }

        protected static Param Req(string name, string type, string description, params string[] enumValues) =>
            new Param { Name = name, Type = type, Description = description, Required = true, Enum = enumValues.Length > 0 ? enumValues : null };

        protected static Param Opt(string name, string type, string description, params string[] enumValues) =>
            new Param { Name = name, Type = type, Description = description, Required = false, Enum = enumValues.Length > 0 ? enumValues : null };

        // ---- argument helpers ----

        protected static string Str(JObject args, string name, string def = null)
        {
            var t = args?[name];
            if (t == null || t.Type == JTokenType.Null) return def;
            return t.Type == JTokenType.String ? (string)t : t.ToString();
        }

        protected static string ReqStr(JObject args, string name)
        {
            var v = Str(args, name);
            if (v == null) throw new ToolArgumentException($"Missing required argument '{name}'.");
            return v;
        }

        protected static int Int(JObject args, string name, int def)
        {
            var t = args?[name];
            if (t == null || t.Type == JTokenType.Null) return def;
            return int.TryParse(t.ToString(), out var v) ? v : def;
        }

        protected static bool Bool(JObject args, string name, bool def)
        {
            var t = args?[name];
            if (t == null || t.Type == JTokenType.Null) return def;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            return bool.TryParse(t.ToString(), out var v) ? v : def;
        }
    }

    public sealed class ToolContext
    {
        private IIde _ide;
        private DbaViewsIde _dbaIde;

        /// <summary>
        /// The IDE for tools. With the setting "Use DBA views" its queries read the DBA_ dictionary views instead of
        /// the ALL_ views (this covers the plug-in's own lookups and the model's queries alike).
        /// </summary>
        public IIde Ide
        {
            get
            {
                if (_ide == null || Config == null || !Config.UseDbaViews) return _ide;
                if (_dbaIde == null || !_dbaIde.Wraps(_ide)) _dbaIde = new DbaViewsIde(_ide);
                return _dbaIde;
            }
            set => _ide = value;
        }

        public AgentConfig Config;
        public CancellationToken Cancellation;
        public Func<ApprovalRequest, Task<ApprovalResult>> RequestApproval;

        /// <summary>Extra information for the chat UI about the current tool call (undo id, backups); set by tools.</summary>
        public JObject UiMeta;

        /// <summary>Current production guard state (null = no guard, e.g. in tests).</summary>
        public Func<GuardState> Guard;

        /// <summary>The agent runs in plan mode: nothing may be changed, not even temporarily.</summary>
        public bool PlanMode;

        /// <summary>
        /// Why a statement that changes data must not run now (plan mode, read-only or protected connection);
        /// null if it may run (with the user's approval).
        /// </summary>
        public string ChangeBlockedReason()
        {
            if (PlanMode) return "the agent is in PLAN MODE";
            var guard = Guard?.Invoke();
            return guard != null && guard.ReadOnly ? "the agent is " + guard.Describe() : null;
        }

        /// <summary>Stores the agent's task list (update_tasks).</summary>
        public Action<JArray> SetTasks;

        /// <summary>Runs a helper agent with a fresh context and returns its report (run_subtask).</summary>
        public Func<string, Task<string>> RunSubtask;

        /// <summary>Shows a progress line under the tool call in the chat (e.g. messages of a slow MCP server); may be null.</summary>
        public Action<string> Progress;

        /// <summary>The MCP servers of the session (resources for @server:… mentions); may be null.</summary>
        public PlsqlDevAgent.Agent.Mcp.McpManager Mcp;

        /// <summary>Tool call this context belongs to (see <see cref="ForCall"/>).</summary>
        public string CallId;

        /// <summary>Records a change for rewinding (null: not recorded, e.g. actions of the user or helper agents).</summary>
        public Action<ChangeRecord> RecordChange;

        /// <summary>Remembers a change of this tool call so the user can rewind it.</summary>
        public void Changed(ChangeRecord change)
        {
            if (RecordChange == null) return;
            change.CallId = CallId;
            RecordChange(change);
            if (change.Kind != ChangeRecord.NotUndoable) AddUiMeta("rewind", true);
        }

        /// <summary>"USER@DB" of the agent's connection (key for backups).</summary>
        public string ConnectionLabel
        {
            get
            {
                var c = Ide.GetContext();
                return c.Connected ? $"{c.User}@{c.Database}".ToUpperInvariant() : "";
            }
        }

        public void AddUiMeta(string key, JToken value)
        {
            if (UiMeta == null) UiMeta = new JObject();
            if (UiMeta[key] is JArray arr && value is JObject) arr.Add(value);
            else UiMeta[key] = value is JObject ? new JArray(value) : value;
        }

        /// <summary>
        /// Shared by the copies made with <see cref="ForCall"/> and <see cref="ForHelper"/>, which may run in parallel
        /// (helper agents): lock-free, a reset only means that the next reader asks the database again.
        /// </summary>
        private sealed class SchemaCache
        {
            public volatile string Value;
            public int SessionChanges;
        }
        private SchemaCache _schema = new SchemaCache();

        /// <summary>Current schema of the agent's database session (remembered until reset).</summary>
        public string CurrentSchema
        {
            get
            {
                var cached = _schema.Value;
                if (cached != null) return cached;
                cached = QueryCurrentSchema() ?? (Ide.GetContext().User ?? "").ToUpperInvariant();
                _schema.Value = cached;
                return cached;
            }
        }

        /// <summary>The current schema read from the session now (not remembered); null if it cannot be read.</summary>
        public string QueryCurrentSchema()
        {
            try
            {
                var r = Ide.Query("select sys_context('USERENV','CURRENT_SCHEMA') from dual", 1);
                return r.Rows.Count > 0 && !string.IsNullOrEmpty(r.Rows[0][0]) ? r.Rows[0][0] : null;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>Forgets the remembered schema: the next <see cref="CurrentSchema"/> reads it from the session.</summary>
        public void ResetSchemaCache() => _schema.Value = null;

        /// <summary>
        /// A statement may have changed the state of the session (ALTER SESSION, SET ROLE, a PL/SQL block, CALL, a
        /// query with PL/SQL): the schema is read again, and the change is counted, so that the agent session switches
        /// back to the user's schema at the end of the run (<see cref="SessionChanges"/>).
        /// </summary>
        public void SessionMayHaveChanged()
        {
            Interlocked.Increment(ref _schema.SessionChanges);
            ResetSchemaCache();
        }

        /// <summary>How often <see cref="SessionMayHaveChanged"/> was called (in this context and its copies).</summary>
        public int SessionChanges => Volatile.Read(ref _schema.SessionChanges);

        /// <summary>Queries that are running right now, shared by this context and its copies (see <see cref="SharedQuery"/>).</summary>
        private System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<QueryResult>> _running =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<QueryResult>>();

        /// <summary>
        /// Runs a read-only query. If exactly the same query is already running or waiting in the agent's session for a
        /// parallel tool call (helper agents), it is not run a second time: both get that result, which is as current as
        /// the second run would be. Nothing is kept after the query has finished.
        /// </summary>
        public QueryResult SharedQuery(string sql, int maxRows, out bool shared)
        {
            var key = maxRows + "|" + sql;
            var mine = new Lazy<QueryResult>(() => Ide.Query(sql, maxRows), LazyThreadSafetyMode.ExecutionAndPublication);
            var entry = _running.GetOrAdd(key, mine);
            shared = !ReferenceEquals(entry, mine);
            try { return entry.Value; }
            finally
            {
                // only the caller that ran the query removes it (a later identical query runs again)
                if (!shared) ((ICollection<KeyValuePair<string, Lazy<QueryResult>>>)_running).Remove(new KeyValuePair<string, Lazy<QueryResult>>(key, mine));
            }
        }

        /// <summary>Exactly this query is running or waiting right now (for a parallel tool call).</summary>
        public bool IsQueryRunning(string sql, int maxRows) => _running.ContainsKey(maxRows + "|" + sql);

        /// <summary>The context of a helper agent (run_subtask): it cannot ask the user.</summary>
        public bool IsHelper;

        /// <summary>
        /// Where the user wants changes of objects that also have local source files: "database", "local", "both" or null
        /// (not asked yet in this chat). Shared by the copies of the context.
        /// </summary>
        public sealed class ChangeTargetHolder { public volatile string Value; }
        public ChangeTargetHolder ChangeTarget;

        /// <summary>Asks the user a question with answer buttons (ask_user); null where nobody can answer (helper agents).</summary>
        public Func<string, string[], Task<string>> AskUser;

        /// <summary>The user excluded this window from the chat (crossed out above the input); null: nothing is excluded.</summary>
        public Func<IdeContext, bool> WindowExcluded;

        /// <summary>
        /// Why the active editor window must not be read or changed now (the user excluded it from the chat), or null.
        /// Checked by the tools that read or change the active window.
        /// </summary>
        public string ExcludedWindowMessage(IdeContext c)
        {
            if (WindowExcluded == null || c == null || !WindowExcluded(c)) return null;
            return $"Not done: the user has excluded the active window ({c.WindowLabel}) from the chat, so it is not part of this " +
                   "request – do not read or change it. Work with the objects of the request instead (get_object_source, explore_objects). " +
                   "If you really need this window, ask the user to include it again (click its name above the chat input).";
        }

        /// <summary>Most characters of an object dossier in this context (null: <see cref="AgentConfig.ExploreOutputLimit"/>).</summary>
        public int? DossierCharsOverride;

        /// <summary>Most characters of an object dossier (explore_objects, @mentions).</summary>
        public int DossierLimit => DossierCharsOverride ?? Config.ExploreOutputLimit;

        /// <summary>
        /// A copy for one tool call with its own <see cref="UiMeta"/> (tool calls may run in parallel, e.g. helper
        /// agents); the schema cache is shared.
        /// </summary>
        public ToolContext ForCall(string callId)
        {
            var copy = (ToolContext)MemberwiseClone();
            copy.UiMeta = null;
            copy.CallId = callId;
            return copy;
        }

        /// <summary>
        /// A context for a helper agent (run_subtask): its own approvals, nothing recorded for rewinding, no task list
        /// and no further helpers. It shares the schema cache, so helpers that run in parallel do not each ask the
        /// database (their statements queue up in the one agent session), and it keeps plan mode and the guard.
        /// </summary>
        public ToolContext ForHelper(CancellationToken ct, Func<ApprovalRequest, Task<ApprovalResult>> requestApproval)
        {
            var copy = ForCall(CallId);
            copy.Cancellation = ct;
            copy.RequestApproval = requestApproval;
            copy.RecordChange = null;
            copy.SetTasks = null;
            copy.RunSubtask = null;
            copy.AskUser = null;
            copy.IsHelper = true;
            return copy;
        }

        public Task<ApprovalResult> ApproveAsync(ApprovalRequest request) => RequestApproval(request);
    }

    public sealed class ApprovalRequest
    {
        /// <summary>"editor", "sql" or "file" — used for "always allow" and auto-approve settings.</summary>
        public string Category;
        public string Title;
        /// <summary>Main content shown to the user (SQL statement, file content…).</summary>
        public string Detail;
        public string DetailLanguage = "sql";
        /// <summary>Unified diff, if this is a modification.</summary>
        public string Diff;
        /// <summary>Optional texts for PL/SQL Developer's own difference viewer.</summary>
        public string DiffLeftCaption, DiffLeftText, DiffRightCaption, DiffRightText;
        /// <summary>Extra warning (e.g. destructive statement).</summary>
        public string Warning;
        /// <summary>Never approved automatically: not by "Always allow" for the chat and not by the auto-approve settings.</summary>
        public bool AlwaysAsk;
    }

    public sealed class ApprovalResult
    {
        public bool Approved;
        public bool AlwaysForSession;
        public string Feedback;

        public static ApprovalResult Yes() => new ApprovalResult { Approved = true };

        public string RejectionMessage =>
            "The user REJECTED this action, it was not executed." +
            (string.IsNullOrWhiteSpace(Feedback) ? " Ask the user how to proceed if unclear." : " User feedback: " + Feedback);
    }

    public sealed class ToolArgumentException : Exception
    {
        public ToolArgumentException(string message) : base(message) { }
    }

    public static class ToolRegistry
    {
        public static List<AgentTool> All() => new List<AgentTool>
        {
            // planning
            new UpdateTasksTool(),
            new RunSubtaskTool(),
            new AskUserTool(),
            // IDE
            new GetIdeContextTool(),
            new GetEditorTextTool(),
            new GetSelectedTextTool(),
            new EditEditorTool(),
            new SetEditorTextTool(),
            new OpenNewWindowTool(),
            new OpenObjectTool(),
            // Database (read)
            new RunQueryTool(),
            new ListObjectsTool(),
            new ExploreObjectsTool(),
            new DescribeObjectTool(),
            new GetObjectSourceTool(),
            new SearchSourceTool(),
            new GetCompileErrorsTool(),
            new ExplainPlanTool(),
            new MeasureSqlTool(),
            new FindDependenciesTool(),
            new ResolveErrorStackTool(),
            new GetResultGridTool(),
            new ListBackupsTool(),
            // Database (write)
            new CompilePlsqlTool(),
            new ExecuteSqlTool(),
            new RecompileInvalidTool(),
            new RunUnitTestsTool(),
            new RestoreBackupTool(),
            // Files
            new ListFilesTool(),
            new FindObjectFilesTool(),
            new ReadFileTool(),
            new SearchFilesTool(),
            new WriteFileTool(),
            new EditFileTool(),
            new OpenFileInIdeTool(),
        };
    }
}
