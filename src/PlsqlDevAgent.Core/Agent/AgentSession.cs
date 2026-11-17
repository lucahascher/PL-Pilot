using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Agent.Tools;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Agent
{
    /// <summary>Callbacks from the agent to the chat UI. Implementations must be thread safe.</summary>
    public interface IAgentUi
    {
        void AssistantStart(string messageId);
        void AssistantDelta(string messageId, string text);
        void ReasoningDelta(string messageId, string text);
        void AssistantEnd(string messageId);
        void ToolStart(string callId, string toolName, string summary, string argumentsJson);
        void ToolEnd(string callId, bool ok, string result, JObject meta);
        Task<ApprovalResult> RequestApproval(string callId, ApprovalRequest request, CancellationToken ct);
        void Attachments(JArray items);
        void ContextUsage(int tokens, int window, bool estimated);
        void Compacted(string summary, int tokensBefore, int tokensAfter);
        void Tasks(JArray tasks);
        void SubtaskStep(string parentCallId, string text);
        void Error(string message);
        void Notice(string message);
        void Busy(bool busy);
    }

    /// <summary>One conversation with the LLM, including the tool calling loop.</summary>
    public sealed class AgentSession : IDisposable
    {
        private const string SummaryPrefix = "[Summary of the earlier conversation – older messages were compacted to save context]";
        private const string AutoContinuePrefix = "[auto-continue]";

        private readonly IIde _ide;
        private readonly IAgentUi _ui;
        private readonly OpenAiClient _client = new OpenAiClient();
        private readonly JArray _history = new JArray();
        private readonly HashSet<string> _alwaysAllow = new HashSet<string>();
        private readonly ConcurrentQueue<string> _notes = new ConcurrentQueue<string>();
        private readonly ToolContext _ctx;
        private CancellationTokenSource _cts;
        private string _systemPrompt;
        private int _busy;
        private double _tokensPerChar = 1 / 3.5;   // calibrated from the server's usage numbers
        private JArray _tasks = new JArray();
        private bool _lastPlanMode;

        public AgentConfig Config { get; private set; }
        public ConnectionGuard Guard { get; } = new ConnectionGuard();
        public bool IsBusy => _busy != 0;
        public int MessageCount => _history.Count;

        public AgentSession(IIde ide, IAgentUi ui, AgentConfig config)
        {
            _ide = ide;
            _ui = ui;
            Config = config;
            // tool calls get their own copy with the call id (ExecuteToolAsync); these are the defaults outside a call
            _ctx = new ToolContext { Ide = ide, Config = config, RequestApproval = r => ApproveAsync(r, null), Guard = CurrentGuard };
            _ctx.SetTasks = SetTasks;
            _ctx.RunSubtask = task => RunSubtaskAsync(task, null, _ctx.Cancellation);
            _ctx.RecordChange = change =>
            {
                change.Checkpoint = _checkpoint;
                Journal?.Add(change);
            };
        }

        /// <summary>Changes of the current chat, for rewinding (set by the chat window when the chat changes).</summary>
        public ChangeJournal Journal { get; set; }

        /// <summary>The user message the agent is working on (its changes are recorded under it).</summary>
        public string CurrentCheckpoint => _checkpoint;
        private string _checkpoint;
        /// <summary>Content of the history message of each checkpoint (to cut the conversation off there; saved with the chat).</summary>
        private Dictionary<string, string> _checkpointMessages = new Dictionary<string, string>();

        public Dictionary<string, string> ExportCheckpoints() => new Dictionary<string, string>(_checkpointMessages);

        private int CheckpointIndex(string checkpoint)
        {
            if (checkpoint == null || !_checkpointMessages.TryGetValue(checkpoint, out var content)) return -1;
            for (int i = _history.Count - 1; i >= 0; i--)
                if ((string)_history[i]["role"] == "user" && (string)_history[i]["content"] == content) return i;
            return -1; // compacted away
        }

        /// <summary>The conversation still contains this user message (not compacted away).</summary>
        public bool CanTruncateAt(string checkpoint) => CheckpointIndex(checkpoint) >= 0;

        /// <summary>Removes the user message of <paramref name="checkpoint"/> and everything after it from the conversation.</summary>
        public bool TruncateAt(string checkpoint)
        {
            if (IsBusy) return false;
            int idx = CheckpointIndex(checkpoint);
            if (idx < 0) return false;
            while (_history.Count > idx) _history.RemoveAt(_history.Count - 1);
            RestoreTasksFromHistory();
            _ui.ContextUsage(EstimateTokens(), Config.ContextWindowTokens, true);
            return true;
        }

        public JArray CurrentTasks => (JArray)_tasks.DeepClone();

        private void SetTasks(JArray tasks)
        {
            _tasks = (JArray)tasks.DeepClone();
            _ui.Tasks(CurrentTasks);
        }

        private List<string> OpenTasks() =>
            _tasks.Where(t => (string)t["status"] != "completed").Select(t => (string)t["content"]).ToList();

        public void UpdateConfig(AgentConfig config)
        {
            Config = config;
            _ctx.Config = config;
            _systemPrompt = null; // rebuilt with the next message
        }

        public void OnConnectionChanged()
        {
            _ctx.ResetSchemaCache();
            lock (_databaseLock)
            {
                _databaseGeneration++;
                _database = null;
                _databaseLoaded = false;
            }
            _systemPrompt = null; // the next model call gets the facts of the new database
        }

        private readonly object _databaseLock = new object();
        private DatabaseInfo _database;
        private bool _databaseLoaded;
        private int _databaseGeneration;

        /// <summary>
        /// Version, NLS settings and character set of the connected database for the system prompt; read once per
        /// connection (null if not connected or not readable). <paramref name="load"/> = false never queries the
        /// database (callers on the UI thread).
        /// </summary>
        public DatabaseInfo Database(bool load = true)
        {
            int generation;
            lock (_databaseLock)
            {
                if (_databaseLoaded || !load) return _database;
                generation = _databaseGeneration;
            }
            DatabaseInfo info = null;
            try
            {
                if (_ide.GetContext().Connected) info = DatabaseInfo.Load(_ide);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn("Database facts for the system prompt could not be read: " + ex.Message); }
            lock (_databaseLock)
            {
                if (generation != _databaseGeneration) return info; // the connection changed meanwhile: do not cache
                _database = info;
                _databaseLoaded = true;
                return info;
            }
        }

        public GuardState CurrentGuard()
        {
            try { return Guard.Evaluate(Config, _ide.GetContext(), _ide.DatabaseIdentity); }
            catch (Exception ex)
            {
                // fail closed: if the connection cannot be checked, it could be a protected one
                Log.Warn("Production guard: the connection could not be checked (" + ex.Message + "); treated as read-only");
                if (Config.ReadOnlyMode) return new GuardState { Mode = GuardMode.ReadOnlySetting };
                if (string.IsNullOrWhiteSpace(Config.ProtectedConnections)) return new GuardState { Mode = GuardMode.Normal };
                return new GuardState { Mode = GuardMode.Protected, Connection = "(unknown connection)", Pattern = "(the connection could not be checked)" };
            }
        }

        /// <summary>Information for the model, sent with the next user message (e.g. "the user reverted your edit").</summary>
        public void AddNote(string note) => _notes.Enqueue(note);

        /// <summary>A tool context for actions the user triggers in the UI (restore backup …), approvals are implicit.</summary>
        public ToolContext UserActionContext() => new ToolContext
        {
            Ide = _ide, Config = Config, Guard = CurrentGuard,
            RequestApproval = r => Task.FromResult(ApprovalResult.Yes()),
        };

        public void Reset()
        {
            Cancel();
            _history.Clear();
            _alwaysAllow.Clear();
            while (_notes.TryDequeue(out _)) { }
            _systemPrompt = null;
            _ctx.ResetSchemaCache();
            _tasks = new JArray();
            _ui.Tasks(new JArray());
            _ui.ContextUsage(0, Config.ContextWindowTokens, true);
            _checkpoint = null;
            _checkpointMessages = new Dictionary<string, string>();
        }

        public JArray ExportHistory() => (JArray)_history.DeepClone();

        public void ImportHistory(JArray history, Dictionary<string, string> checkpoints = null)
        {
            Reset();
            foreach (var m in history ?? new JArray()) _history.Add(m.DeepClone());
            if (checkpoints != null) _checkpointMessages = new Dictionary<string, string>(checkpoints);
            RestoreTasksFromHistory();
            _ui.ContextUsage(EstimateTokens(), Config.ContextWindowTokens, true);
        }

        /// <summary>The task list as of the last update_tasks call in the history (empty if there is none).</summary>
        private void RestoreTasksFromHistory()
        {
            var lastTasks = _history.Where(m => m["tool_calls"] is JArray).SelectMany(m => (JArray)m["tool_calls"])
                .LastOrDefault(c => (string)c["function"]?["name"] == "update_tasks");
            JArray tasks = null;
            if (lastTasks != null)
            {
                try { tasks = UpdateTasksTool.Normalize(JObject.Parse((string)lastTasks["function"]["arguments"]), out _); }
                catch { }
            }
            SetTasks(tasks ?? new JArray());
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Starts processing a user message in the background. Returns false if the agent is busy.
        /// <paramref name="checkpoint"/>: id of the user message for rewinding (null = keep the current one, e.g. retry).
        /// </summary>
        public bool Send(string userText, bool planMode = false, string checkpoint = null)
        {
            if (IsBusy) return false;
            if (checkpoint != null)
            {
                _checkpoint = checkpoint;
                Journal?.StartCheckpoint(checkpoint);
            }
            if (!Start(ct => RunAsync(userText, planMode, ct))) return false;
            _lastPlanMode = planMode;
            return true;
        }

        /// <summary>Removes the last answer (everything after the last user message) and asks again.</summary>
        public bool Retry()
        {
            if (IsBusy) return false;
            int idx = LastUserIndex();
            if (idx < 0) return false;
            var text = StripInjectedBlocks((string)_history[idx]["content"]);
            while (_history.Count > idx) _history.RemoveAt(_history.Count - 1);
            return Send(text, _lastPlanMode);
        }

        /// <summary>Summarizes the whole conversation now (/compact).</summary>
        public bool CompactNow() => Start(async ct =>
        {
            if (_history.Count < 2) { _ui.Notice(L.T("Nothing to compact yet.")); return; }
            await CompactAsync(_history.Count, ct).ConfigureAwait(false);
        });

        private bool Start(Func<CancellationToken, Task> work)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ctx.Cancellation = ct;
            _ui.Busy(true);
            Task.Run(async () =>
            {
                // database calls of this run stop waiting as soon as the user stops the agent
                IdeCancellation.Current = ct;
                try
                {
                    await work(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Log.Info("Agent run cancelled by the user");
                    CloseDanglingToolCalls("Cancelled by the user.");
                    bool sqlStillRunning;
                    try { sqlStillRunning = _ide.IsExecutingSql; } catch { sqlStillRunning = false; }
                    _ui.Notice(sqlStillRunning
                        ? L.T("Stopped. The last database statement is still running in the agent's session (it cannot be interrupted from a plug-in); the agent's next database access waits until it has finished.")
                        : L.T("Stopped."));
                }
                catch (LlmException ex)
                {
                    CloseDanglingToolCalls("Not executed.");
                    _ui.Error(ex.Message);
                }
                catch (Exception ex)
                {
                    Log.Error("Agent run failed", ex);
                    CloseDanglingToolCalls("Not executed.");
                    _ui.Error(ex.GetBaseException().Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                    _ui.Busy(false);
                }
            });
            return true;
        }

        /// <summary>The schema the user works in: the session's schema when the current request started.</summary>
        private string _userSchema;

        /// <summary>
        /// One user request. The agent starts in the schema the user works in; if something it ran may have switched
        /// the session to another schema, the session is switched back at the end, also after an error or Stop.
        /// </summary>
        private async Task RunAsync(string userText, bool planMode, CancellationToken ct)
        {
            // without a session of its own the agent shares the session of the user's windows: they may have switched its schema
            if (!_ide.UsesOwnSession) _ctx.ResetSchemaCache();
            _userSchema = null; // read for the context block of the request
            int changes = _ctx.SessionChanges, generation = DatabaseGeneration;
            try
            {
                await RunTurnsAsync(userText, planMode, ct).ConfigureAwait(false);
            }
            finally
            {
                ReturnToUserSchema(_userSchema, changes, generation, ct.IsCancellationRequested);
            }
        }

        private int DatabaseGeneration { get { lock (_databaseLock) return _databaseGeneration; } }

        /// <summary>
        /// Switches the session back to <paramref name="schema"/> if a statement of this run may have changed it
        /// (execute_sql with ALTER SESSION or PL/SQL, unit tests). After Stop that statement may still be running: the
        /// switch then waits for it in the background, as one statement, so a new request cannot read the schema
        /// in between (the session runs statements strictly one after another).
        /// </summary>
        private void ReturnToUserSchema(string schema, int changesAtStart, int generation, bool stopped)
        {
            if (string.IsNullOrEmpty(schema) || _ctx.SessionChanges == changesAtStart) return;
            if (stopped)
            {
                Task.Run(() =>
                {
                    IdeCancellation.Current = CancellationToken.None; // the request was stopped, switching back is not
                    SwitchBackToSchema(schema, generation, checkFirst: false);
                });
                return;
            }
            var previous = IdeCancellation.Current;
            IdeCancellation.Current = CancellationToken.None; // a Stop click now must not leave the session in the other schema
            try { SwitchBackToSchema(schema, generation, checkFirst: true); }
            finally { IdeCancellation.Current = previous; }
        }

        private void SwitchBackToSchema(string schema, int generation, bool checkFirst)
        {
            try
            {
                if (generation != DatabaseGeneration) return; // another connection or session: it starts in the user's schema
                var current = checkFirst ? _ctx.QueryCurrentSchema() : null;
                if (current == schema) return;
                var res = _ide.Execute("alter session set current_schema = \"" + schema.Replace("\"", "") + "\"");
                _ctx.ResetSchemaCache();
                if (!res.Ok)
                {
                    Log.Warn($"Could not switch the session back to schema {schema}: {res.ErrorMessage}");
                    _ui.Error(L.T("Could not switch the session back to schema {0}, the schema you work in: {1}", schema, (res.ErrorMessage ?? "").Trim()));
                    return;
                }
                Log.Info($"Session switched back to the user's schema {schema}" + (current != null ? $" (was {current})" : " (after Stop)"));
                if (current == null) return;
                _ui.Notice(L.T("Switched the session back from schema {0} to {1}, the schema you work in.", current, schema));
                _notes.Enqueue($"At the end of your last turn the plug-in switched the session back from schema {current} to the user's schema {schema}.");
            }
            catch (Exception ex) { Log.Warn($"Switching the session back to schema {schema} failed: {ex.Message}"); }
        }

        private async Task RunTurnsAsync(string userText, bool planMode, CancellationToken ct)
        {
            var guard = CurrentGuard();
            var tools = AvailableTools(guard, planMode);
            var toolSchemas = new JArray(tools.Select(t => t.ToOpenAiTool()));
            _ctx.PlanMode = planMode; // read-only tools that could change data (measure_sql with DML) check it
            var notes = new List<string>();
            while (_notes.TryDequeue(out var n)) notes.Add(n);
            string contextBlock;
            try
            {
                var ideCtx = _ide.GetContext();
                _userSchema = ideCtx.Connected ? _ctx.CurrentSchema : null;
                contextBlock = SystemPrompt.ContextBlock(ideCtx, _userSchema, guard, notes, planMode);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read IDE context: " + ex.Message);
                contextBlock = "";
            }

            var (attachText, attachItems) = await Mentions.ResolveAsync(userText, _ctx).ConfigureAwait(false);
            if (attachItems.Count > 0) _ui.Attachments(attachItems);
            var userMessage = new JObject { ["role"] = "user", ["content"] = contextBlock + userText + attachText };
            _history.Add(userMessage);
            if (_checkpoint != null) _checkpointMessages[_checkpoint] = (string)userMessage["content"];

            int continues = 0, stepsInRound = 0;
            bool progressSinceNudge = true;
            for (int step = 0; ; step++, stepsInRound++)
            {
                if (stepsInRound >= Config.MaxSteps)
                {
                    if (Config.AutoContinue && !planMode && continues < Config.MaxAutoContinues)
                    {
                        continues++;
                        stepsInRound = 0;
                        _ui.Notice(L.T("Reached {0} steps, continuing automatically ({1}/{2}).", Config.MaxSteps, continues, Config.MaxAutoContinues));
                    }
                    else
                    {
                        _ui.Notice(L.T("Stopped after {0} steps. Send \"continue\" to let the agent go on.", step));
                        break;
                    }
                }
                ct.ThrowIfCancellationRequested();
                await MaybeCompactAsync(ct).ConfigureAwait(false);

                var messageId = "m" + Guid.NewGuid().ToString("N").Substring(0, 10);
                // rebuilt after a connection change (other database version / NLS settings)
                if (_systemPrompt == null) _systemPrompt = SystemPrompt.Build(Config, Config.HasWorkspace, Database());
                var messages = BuildMessages();
                int sentChars = CharCount(messages);
                _ui.AssistantStart(messageId);
                AssistantTurn turn;
                try
                {
                    turn = await _client.StreamChatAsync(Config, messages, toolSchemas, new Sink(_ui, messageId), ct).ConfigureAwait(false);
                }
                finally
                {
                    _ui.AssistantEnd(messageId);
                }
                Log.Info($"LLM turn {step + 1}: {turn.Content.Length} chars, {turn.Reasoning.Length} reasoning chars, " +
                         $"tools [{string.Join(", ", turn.ToolCalls.Select(c => c.Name))}], finish={turn.FinishReason ?? "-"}" +
                         (turn.Usage != null ? $", usage {turn.Usage.ToString(Formatting.None)}" : ""));
                ReportUsage(turn, sentChars);

                if (turn.FinishReason == "repetition")
                {
                    // the model is stuck repeating itself: keep one copy and stop, continuing would only repeat the loop
                    _history.Add(new JObject { ["role"] = "assistant", ["content"] = turn.Content ?? "" });
                    _ui.Notice(L.T("The model kept repeating the same text and was stopped. Rephrase the request, split it into smaller steps or use a larger model."));
                    break;
                }

                var assistant = new JObject { ["role"] = "assistant" };
                if (turn.ToolCalls.Count > 0)
                {
                    // ids must be unique in the conversation (UI, "Undo from here"); some servers repeat ids like "call_0"
                    var usedIds = new HashSet<string>(_history.Where(m => m["tool_calls"] is JArray)
                        .SelectMany(m => (JArray)m["tool_calls"]).Select(c => (string)c["id"]));
                    int n = 0;
                    foreach (var call in turn.ToolCalls)
                        if (string.IsNullOrEmpty(call.Id) || !usedIds.Add(call.Id)) usedIds.Add(call.Id = $"call_{messageId}_{n++}");
                    assistant["content"] = string.IsNullOrEmpty(turn.Content) ? null : turn.Content;
                    assistant["tool_calls"] = new JArray(turn.ToolCalls.Select(c => new JObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JObject { ["name"] = c.Name, ["arguments"] = string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments },
                    }));
                }
                else
                {
                    assistant["content"] = turn.Content ?? "";
                }
                _history.Add(assistant);

                if (turn.ToolCalls.Count == 0)
                {
                    // Self-prompting: keep the agent going while its task list has open items (or it was cut off),
                    // but only if it made progress since the last nudge, so it cannot loop without doing anything.
                    var open = OpenTasks();
                    bool cutOff = turn.FinishReason == "length";
                    if (Config.AutoContinue && !planMode && continues < Config.MaxAutoContinues && progressSinceNudge && (open.Count > 0 || cutOff))
                    {
                        continues++;
                        progressSinceNudge = false;
                        var nudge = cutOff
                            ? $"{AutoContinuePrefix} Your answer was cut off by the length limit. Continue exactly where you stopped."
                            : $"{AutoContinuePrefix} You stopped, but these tasks are still open:\n- {string.Join("\n- ", open)}\n" +
                              "Continue working on them now and update the task list as you go. If you are blocked or need a decision " +
                              "from the user, say exactly what you need, and if a task is no longer needed, mark it completed.";
                        _history.Add(new JObject { ["role"] = "user", ["content"] = nudge });
                        _ui.Notice(cutOff
                            ? L.T("The answer was cut off, continuing automatically ({0}/{1}).", continues, Config.MaxAutoContinues)
                            : L.T("{0} open task(s), continuing automatically ({1}/{2}).", open.Count, continues, Config.MaxAutoContinues));
                        continue;
                    }
                    if (cutOff)
                        _ui.Notice(L.T("The answer was cut off because it reached the max tokens limit (Settings, Max tokens)."));
                    else if (string.IsNullOrWhiteSpace(turn.Content))
                        _ui.Notice(L.T("The model returned an empty answer."));
                    else if (open.Count > 0 && !planMode && Config.AutoContinue)
                        _ui.Notice(L.T("The agent stopped with {0} open task(s). Send \"continue\" to let it go on.", open.Count));
                    break;
                }

                progressSinceNudge = true;
                var calls = turn.ToolCalls;
                for (int i = 0; i < calls.Count;)
                {
                    ct.ThrowIfCancellationRequested();
                    // consecutive run_subtask calls are independent research jobs: run them in parallel
                    int batchEnd = i;
                    while (batchEnd < calls.Count && calls[batchEnd].Name == RunSubtaskTool.ToolName) batchEnd++;
                    var batch = calls.Skip(i).Take(Math.Max(1, batchEnd - i)).ToList();
                    var results = batch.Count == 1
                        ? new[] { await ExecuteToolAsync(batch[0], tools).ConfigureAwait(false) }
                        : await Task.WhenAll(batch.Select(c => ExecuteToolAsync(c, tools))).ConfigureAwait(false);
                    for (int k = 0; k < batch.Count; k++)
                        _history.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = batch[k].Id, ["content"] = results[k] });
                    i += batch.Count;
                }
            }
        }

        private List<AgentTool> AvailableTools(GuardState guard, bool planMode) => ToolRegistry.All()
            .Where(t => !((guard.ReadOnly || planMode) && t.Mutating))
            .Where(t => !t.NeedsWorkspace || Config.HasWorkspace)
            .ToList();

        /// <summary>
        /// A helper agent for run_subtask: own fresh conversation, read-only tools, no approvals, returns its final report.
        /// Its tool calls are shown as indented lines under the parent tool call.
        /// </summary>
        private async Task<string> RunSubtaskAsync(string task, string parentCallId, CancellationToken ct)
        {
            var tools = ToolRegistry.All()
                .Where(t => !t.Mutating && t.Name != "run_subtask" && t.Name != "update_tasks")
                .Where(t => !t.NeedsWorkspace || Config.HasWorkspace)
                .ToList();
            var schemas = new JArray(tools.Select(t => t.ToOpenAiTool()));
            // shares the schema cache with the agent and the other helpers that run in parallel
            var subCtx = _ctx.ForHelper(ct, r => Task.FromResult(new ApprovalResult { Approved = false, Feedback = "Helper agents are read-only." }));
            string context = "";
            try
            {
                var c = _ide.GetContext();
                context = SystemPrompt.ContextBlock(c, c.Connected ? _ctx.CurrentSchema : null, null, null);
            }
            catch { }
            var messages = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = SystemPrompt.BuildSubtask(Config, Database()) },
                new JObject { ["role"] = "user", ["content"] = context + "Task: " + task },
            };
            // optionally a smaller / faster model for the research
            var cfg = Config;
            if (!string.IsNullOrWhiteSpace(Config.SubtaskModel))
            {
                cfg = Config.Clone();
                cfg.Model = Config.SubtaskModel.Trim();
            }
            Log.Info($"Sub-task started ({cfg.Model}): " + (task.Length > 200 ? task.Substring(0, 200) : task));
            var watch = System.Diagnostics.Stopwatch.StartNew();

            for (int step = 0; step < Math.Max(1, Config.SubtaskMaxSteps); step++)
            {
                ct.ThrowIfCancellationRequested();
                var turn = await _client.StreamChatAsync(cfg, messages, schemas, null, ct).ConfigureAwait(false);
                var assistant = new JObject { ["role"] = "assistant", ["content"] = string.IsNullOrEmpty(turn.Content) && turn.ToolCalls.Count > 0 ? null : turn.Content ?? "" };
                if (turn.ToolCalls.Count == 0)
                {
                    Log.Info($"Sub-task finished after {step + 1} turn(s) in {watch.Elapsed.TotalSeconds:0.0} s");
                    return string.IsNullOrWhiteSpace(turn.Content) ? "(the helper returned an empty report)" : turn.Content;
                }
                int n = 0;
                foreach (var call in turn.ToolCalls) if (string.IsNullOrEmpty(call.Id)) call.Id = $"sub_{step}_{n++}";
                assistant["tool_calls"] = new JArray(turn.ToolCalls.Select(c => new JObject
                {
                    ["id"] = c.Id, ["type"] = "function",
                    ["function"] = new JObject { ["name"] = c.Name, ["arguments"] = string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments },
                }));
                messages.Add(assistant);

                foreach (var call in turn.ToolCalls)
                {
                    string result;
                    var tool = tools.FirstOrDefault(t => t.Name == call.Name);
                    try
                    {
                        var args = string.IsNullOrWhiteSpace(call.Arguments) ? new JObject() : JObject.Parse(call.Arguments);
                        string summary = "";
                        try { summary = tool?.Summarize(args) ?? ""; } catch { }
                        _ui.SubtaskStep(parentCallId, $"{call.Name} {summary}".Trim());
                        if (!_ide.UsesOwnSession) subCtx.ResetSchemaCache(); // the user's windows may have switched the shared session's schema
                        result = tool == null
                            ? $"Error: unknown tool '{call.Name}'."
                            : await Task.Run(() => tool.ExecuteAsync(args, subCtx)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result = "Error: " + ex.GetBaseException().Message; }
                    messages.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = Db.Truncate(result ?? "", Config.MaxToolOutputChars) });
                }
            }

            // step limit reached: ask for the report without tools
            messages.Add(new JObject { ["role"] = "user", ["content"] = "Stop investigating now and write your report with what you found so far." });
            var final = await _client.StreamChatAsync(cfg, messages, null, null, ct).ConfigureAwait(false);
            Log.Info($"Sub-task reached the step limit, report after {watch.Elapsed.TotalSeconds:0.0} s");
            return string.IsNullOrWhiteSpace(final.Content) ? "(the helper ran out of steps without a report)" : final.Content;
        }

        private async Task<string> ExecuteToolAsync(ToolCall call, List<AgentTool> tools)
        {
            JObject args;
            try
            {
                args = string.IsNullOrWhiteSpace(call.Arguments) ? new JObject() : JObject.Parse(call.Arguments);
            }
            catch (JsonException ex)
            {
                _ui.ToolStart(call.Id, call.Name, "", call.Arguments);
                var msg = "Error: the tool arguments are not valid JSON (" + ex.Message + "). Send valid JSON.";
                _ui.ToolEnd(call.Id, false, msg, null);
                return msg;
            }

            var tool = tools.FirstOrDefault(t => t.Name == call.Name);
            string summary = "";
            try { summary = tool?.Summarize(args) ?? ""; } catch { }
            _ui.ToolStart(call.Id, call.Name, summary, args.ToString(Formatting.Indented));

            if (tool == null)
            {
                var known = ToolRegistry.All().Any(t => t.Name == call.Name && t.Mutating);
                var msg = known
                    ? $"Error: '{call.Name}' is not available now (plan mode or read-only connection). Describe the change instead."
                    : $"Error: unknown tool '{call.Name}'. Available tools: {string.Join(", ", tools.Select(t => t.Name))}.";
                _ui.ToolEnd(call.Id, false, msg, null);
                return msg;
            }

            if (tool.Mutating)
            {
                // the guard may have changed during the run (connection switched, user locked it)
                var guard = CurrentGuard();
                if (guard.ReadOnly)
                {
                    var msg = $"Error: not executed, the agent is {guard.Describe()}. Explain the change to the user instead.";
                    _ui.ToolEnd(call.Id, false, msg, null);
                    return msg;
                }
            }

            string result;
            bool ok = true;
            // own context per call: calls may run in parallel (helper agents), each with its call id and UI metadata
            var callCtx = _ctx.ForCall(call.Id);
            callCtx.RequestApproval = r => ApproveAsync(r, call.Id);
            callCtx.RunSubtask = task => RunSubtaskAsync(task, call.Id, callCtx.Cancellation);
            if (!_ide.UsesOwnSession) callCtx.ResetSchemaCache(); // the user's windows share the session and may have switched the schema
            try
            {
                result = await Task.Run(() => tool.ExecuteAsync(args, callCtx)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ui.ToolEnd(call.Id, false, "Cancelled.", callCtx.UiMeta);
                throw;
            }
            catch (Exception ex) when (ex is ToolArgumentException || ex is OracleSqlException || ex is IdeException || ex is TimeoutException)
            {
                ok = false;
                result = "Error: " + ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error($"Tool {call.Name} failed", ex);
                ok = false;
                result = "Error: " + ex.GetBaseException().Message;
            }

            result = Db.Truncate(result ?? "", Config.MaxToolOutputChars);
            _ui.ToolEnd(call.Id, ok, result, callCtx.UiMeta);
            return result;
        }

        private async Task<ApprovalResult> ApproveAsync(ApprovalRequest request, string callId)
        {
            bool alwaysAsk = request.AlwaysAsk || request.Category.EndsWith("-destructive");
            if (!alwaysAsk && _alwaysAllow.Contains(request.Category)) return ApprovalResult.Yes();
            var result = await _ui.RequestApproval(callId, request, _ctx.Cancellation).ConfigureAwait(false);
            if (result.Approved && result.AlwaysForSession && !alwaysAsk)
                _alwaysAllow.Add(request.Category);
            return result;
        }

        // ------------------------------------------------------------------ context size

        private static int CharCount(JArray messages) =>
            messages.Sum(m => ((string)m["content"] ?? "").Length + (m["tool_calls"]?.ToString(Formatting.None).Length ?? 0));

        private int EstimateTokens() => (int)(CharCount(BuildMessages()) * _tokensPerChar) + 2000; // + tool schemas

        private readonly HashSet<string> _contextChecked = new HashSet<string>();

        /// <summary>
        /// After the model's first answer (now the server has loaded it, e.g. Ollama with its real context size):
        /// asks the server once per server and model and corrects the setting "Context Window" (saved, with a notice).
        /// </summary>
        private void CheckContextWindowOnce()
        {
            var cfg = Config;
            if (!cfg.DetectContextWindow) return;
            var key = cfg.BaseUrl + "|" + cfg.Model;
            lock (_contextChecked) if (!_contextChecked.Add(key)) return;
            Task.Run(async () =>
            {
                try
                {
                    ContextWindowInfo info;
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                        info = await ContextWindowDetector.DetectAsync(_client, cfg, cfg.Model, cts.Token).ConfigureAwait(false);
                    var value = info.SettingFor(cfg.ContextWindowTokens);
                    if (value == null || !ReferenceEquals(cfg, Config)) return; // unchanged, or the settings were changed meanwhile
                    int before = cfg.ContextWindowTokens;
                    cfg.ContextWindowTokens = value.Value;
                    if (!cfg.SessionOnly) cfg.Save();
                    Log.Info($"Context window of {cfg.Model}: {before} -> {value} tokens ({info.Source})");
                    _ui.Notice(L.T("Context window set to {0:N0} tokens (was {1:N0}): {2} reports this size for {3}.", value.Value, before, L.T(info.Source), cfg.Model));
                    _ui.ContextUsage(EstimateTokens(), cfg.ContextWindowTokens, true);
                }
                catch (Exception ex) { Log.Warn("Context window check failed: " + ex.Message); }
            });
        }

        private void ReportUsage(AssistantTurn turn, int sentChars)
        {
            CheckContextWindowOnce();
            int prompt = (int?)turn.Usage?["prompt_tokens"] ?? 0;
            int completion = (int?)turn.Usage?["completion_tokens"] ?? 0;
            if (prompt > 0 && sentChars > 0)
            {
                _tokensPerChar = Math.Max(0.1, Math.Min(1.0, (double)prompt / sentChars));
                _ui.ContextUsage(prompt + completion, Config.ContextWindowTokens, false);
            }
            else
            {
                _ui.ContextUsage((int)((sentChars + turn.Content.Length) * _tokensPerChar) + 2000, Config.ContextWindowTokens, true);
            }
        }

        private async Task MaybeCompactAsync(CancellationToken ct)
        {
            if (Config.CompactAtPercent <= 0 || Config.ContextWindowTokens <= 0) return;
            int estimate = EstimateTokens();
            if (estimate < Config.ContextWindowTokens * Config.CompactAtPercent / 100) return;
            int keepFrom = LastUserIndex();
            if (keepFrom < 2) return; // only the current request is large: old tool outputs get shortened instead
            await CompactAsync(keepFrom, ct).ConfigureAwait(false);
        }

        /// <summary>Replaces messages [0, keepFrom) with an LLM written summary.</summary>
        private async Task CompactAsync(int keepFrom, CancellationToken ct)
        {
            int before = EstimateTokens();
            _ui.Notice(L.T("Compacting the conversation…"));
            var transcript = RenderTranscript(_history.Take(keepFrom).Cast<JObject>().ToList());
            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "system",
                    ["content"] = "You summarize a conversation between a user and an AI coding agent that works inside PL/SQL Developer " +
                                  "(Oracle). The summary replaces the conversation, so the agent must be able to continue seamlessly. Include: " +
                                  "the user's goals and requests, decisions and preferences, the database objects and files involved (exact names), " +
                                  "changes already made (editor edits, compiled objects, executed statements, backups) and their current state, " +
                                  "problems found, and open tasks / next steps. Keep exact identifiers; include code only when essential. " +
                                  "Write in the language of the user; at most about 600 words.",
                },
                new JObject { ["role"] = "user", ["content"] = "Conversation to summarize:\n\n" + transcript },
            };
            var cfg = Config.Clone();
            cfg.MaxTokens = Math.Min(Math.Max(cfg.MaxTokens, 1500), 4000);
            var turn = await _client.StreamChatAsync(cfg, messages, null, null, ct).ConfigureAwait(false);
            var summary = (turn.Content ?? "").Trim();
            if (summary.Length == 0) throw new LlmException("Compaction failed: the model returned an empty summary.");

            var kept = _history.Skip(keepFrom).Select(m => m.DeepClone()).ToList();
            _history.Clear();
            _history.Add(new JObject { ["role"] = "user", ["content"] = SummaryPrefix + "\n\n" + summary });
            _history.Add(new JObject { ["role"] = "assistant", ["content"] = "Understood. I have the context of our earlier conversation and will continue from there." });
            foreach (var m in kept) _history.Add(m);
            int after = EstimateTokens();
            Log.Info($"Compacted conversation: ~{before} -> ~{after} tokens");
            _ui.Compacted(summary, before, after);
            _ui.ContextUsage(after, Config.ContextWindowTokens, true);
        }

        private static string RenderTranscript(List<JObject> messages)
        {
            int toolLimit = messages.Sum(m => ((string)m["content"] ?? "").Length) > 150000 ? 400 : 1500;
            var sb = new StringBuilder();
            foreach (var m in messages)
            {
                var role = (string)m["role"];
                var content = (string)m["content"] ?? "";
                if (role == "tool")
                {
                    sb.Append("TOOL RESULT: ").Append(content.Length > toolLimit ? content.Substring(0, toolLimit) + " …" : content).Append("\n\n");
                    continue;
                }
                sb.Append(role == "user" ? "USER: " : "ASSISTANT: ").Append(content).Append('\n');
                if (m["tool_calls"] is JArray calls)
                {
                    foreach (var c in calls)
                    {
                        var args = (string)c["function"]?["arguments"] ?? "";
                        sb.Append("  [called ").Append((string)c["function"]?["name"]).Append(' ')
                          .Append(args.Length > 600 ? args.Substring(0, 600) + " …" : args).Append("]\n");
                    }
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>System prompt + history; shortens old tool outputs when the conversation gets too large.</summary>
        private JArray BuildMessages()
        {
            long total = _history.Sum(m => ((string)m["content"] ?? "").Length + (m["tool_calls"]?.ToString().Length ?? 0));
            if (total > Config.ContextBudgetChars)
            {
                int protectedTail = 8;
                for (int i = 0; i < _history.Count - protectedTail && total > Config.ContextBudgetChars; i++)
                {
                    var m = (JObject)_history[i];
                    if ((string)m["role"] != "tool") continue;
                    var content = (string)m["content"] ?? "";
                    if (content.Length <= 600) continue;
                    var shortened = content.Substring(0, 400) + "\n... [older tool output removed to save context; call the tool again if needed]";
                    total -= content.Length - shortened.Length;
                    m["content"] = shortened;
                }
            }

            // may run on the UI thread (token estimate): never reads the database here
            var messages = new JArray { new JObject { ["role"] = "system", ["content"] = _systemPrompt ?? SystemPrompt.Build(Config, Config.HasWorkspace, Database(load: false)) } };
            foreach (var m in _history) messages.Add(m.DeepClone());
            return messages;
        }

        private int LastUserIndex()
        {
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                if ((string)_history[i]["role"] != "user") continue;
                var c = (string)_history[i]["content"] ?? "";
                if (c.StartsWith(AutoContinuePrefix)) continue; // self-prompts are not user requests
                return c.StartsWith(SummaryPrefix) ? -1 : i;
            }
            return -1;
        }

        /// <summary>The user's own text of a stored user message (without context block and attachments).</summary>
        private static string StripInjectedBlocks(string content)
        {
            var s = Regex.Replace(content ?? "", @"^<ide_context>[\s\S]*?</ide_context>\s*", "");
            s = Regex.Replace(s, @"\s*<attachments>[\s\S]*</attachments>\s*$", "");
            return s;
        }

        /// <summary>After a cancel/error every tool_call must still get a tool response, or the next request is invalid.</summary>
        private void CloseDanglingToolCalls(string text)
        {
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                var m = (JObject)_history[i];
                if ((string)m["role"] != "assistant") continue;
                if (!(m["tool_calls"] is JArray calls)) return;
                var answered = new HashSet<string>(_history.Skip(i + 1).Where(x => (string)x["role"] == "tool").Select(x => (string)x["tool_call_id"]));
                foreach (var c in calls)
                {
                    var id = (string)c["id"];
                    if (!answered.Contains(id)) _history.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = text });
                }
                return;
            }
        }

        public void Dispose()
        {
            Cancel();
            _client.Dispose();
        }

        private sealed class Sink : IStreamSink
        {
            private readonly IAgentUi _ui;
            private readonly string _id;
            public Sink(IAgentUi ui, string id) { _ui = ui; _id = id; }
            public void OnContent(string delta) => _ui.AssistantDelta(_id, delta);
            public void OnReasoning(string delta) => _ui.ReasoningDelta(_id, delta);
            public void OnToolCallStarted(int index, string name) { }
        }
    }
}
