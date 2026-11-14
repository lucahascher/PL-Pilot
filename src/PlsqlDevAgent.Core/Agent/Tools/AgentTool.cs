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

        /// <summary>Shared by the copies made with <see cref="ForCall"/>.</summary>
        private sealed class SchemaCache { public volatile string Value; }
        private SchemaCache _schema = new SchemaCache();

        /// <summary>Current schema of the agent's database session (cached until reset).</summary>
        public string CurrentSchema
        {
            get
            {
                var cached = _schema.Value;
                if (cached != null) return cached;
                try
                {
                    var r = Ide.Query("select sys_context('USERENV','CURRENT_SCHEMA') from dual", 1);
                    cached = r.Rows.Count > 0 ? r.Rows[0][0] : "";
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    cached = (Ide.GetContext().User ?? "").ToUpperInvariant();
                }
                _schema.Value = cached;
                return cached;
            }
        }

        public void ResetSchemaCache() => _schema.Value = null;

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
            new ReadFileTool(),
            new SearchFilesTool(),
            new WriteFileTool(),
            new EditFileTool(),
            new OpenFileInIdeTool(),
        };
    }
}
