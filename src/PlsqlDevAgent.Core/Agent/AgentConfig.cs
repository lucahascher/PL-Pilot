using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Agent.Mcp;

namespace PlsqlDevAgent.Agent
{
    /// <summary>
    /// User settings, stored in %APPDATA%\PlsqlDevAgent\config.json.
    /// The API key is encrypted with Windows DPAPI (current user).
    /// </summary>
    public sealed class AgentConfig
    {
        // ---- LLM endpoint ----
        /// <summary>OpenAI compatible base URL, e.g. http://llm-server:8000/v1 (".../chat/completions" is accepted too).</summary>
        public string BaseUrl { get; set; } = "http://localhost:8000/v1";
        [JsonIgnore] public string ApiKey { get; set; } = "";
        public string Model { get; set; } = ""; // chosen after "Test" in the settings
        public double Temperature { get; set; } = 0.2;
        public int MaxTokens { get; set; } = 8192;
        public int RequestTimeoutSeconds { get; set; } = 600;
        public bool UseSystemProxy { get; set; } = true;
        /// <summary>
        /// HTTPS certificates to trust although Windows does not (e.g. the company CA is not installed on this PC):
        /// SHA-1 or SHA-256 thumbprints of the server certificate or of a CA certificate in its chain, separated by ;.
        /// </summary>
        public string TrustedCertificates { get; set; } = "";
        /// <summary>Extra JSON merged into every request body (e.g. {"top_p":0.95} or server specific options).</summary>
        public string ExtraBodyJson { get; set; } = "";

        // ---- agent behaviour ----
        public int MaxSteps { get; set; } = 40;
        /// <summary>Prompt the agent to go on when it stops with open tasks, hits MaxSteps or is cut off.</summary>
        public bool AutoContinue { get; set; } = true;
        public int MaxAutoContinues { get; set; } = 5;
        /// <summary>Model turns a helper agent (run_subtask) may use.</summary>
        public int SubtaskMaxSteps { get; set; } = 15;
        /// <summary>Model for helper agents, e.g. a smaller, faster one; empty = the main model.</summary>
        public string SubtaskModel { get; set; } = "";
        public int MaxQueryRows { get; set; } = 200;
        /// <summary>Compile with PLSQL_WARNINGS enabled and report the warnings to the model (the unit keeps that setting).</summary>
        public bool CompilerWarnings { get; set; } = true;
        /// <summary>
        /// Typing @ in the chat input looks up matching database objects (two small queries per pause in typing). Off: no
        /// database queries while typing; @NAME still attaches the object when the message is sent.
        /// </summary>
        public bool MentionSuggestions { get; set; } = true;
        /// <summary>
        /// Before a query of the model runs, Oracle estimates its runtime (EXPLAIN PLAN, not executed); at or above this
        /// many seconds the user is asked first, because a running statement cannot be stopped. 0 = never ask.
        /// </summary>
        public int ConfirmSlowQuerySeconds { get; set; } = 60;
        public int MaxToolOutputChars { get; set; } = 40000;
        /// <summary>Rough size budget of the conversation; older tool outputs are shortened above it.</summary>
        public int ContextBudgetChars { get; set; } = 300000;
        public string ExtraInstructions { get; set; } = "";
        /// <summary>Replaces the built-in base system prompt (role, way of working, answer style); empty = default.</summary>
        public string SystemPromptOverride { get; set; } = "";
        /// <summary>Markdown file or folder with company rules / conventions (may be a network share, %VARS% allowed).</summary>
        public string CompanyRulesPath { get; set; } = "";
        public string WorkspaceDir { get; set; } = "";
        /// <summary>Own quick commands, one per line: "/name = prompt" ($ARGUMENTS = text after the command).</summary>
        public string CustomCommands { get; set; } = "";
        /// <summary>
        /// External tools: the user's own MCP servers reached over HTTP, and the user's settings for servers of the team
        /// configuration (entries with Team = true).
        /// </summary>
        public List<McpServerConfig> McpServers { get; set; } = new List<McpServerConfig>();
        /// <summary>Team MCP configuration: an mcp.json file or a folder containing one (e.g. a network share, %VARIABLES% allowed).</summary>
        public string McpConfigPath { get; set; } = "";
        /// <summary>Context window of the model in tokens (for the meter and automatic compaction).</summary>
        public int ContextWindowTokens { get; set; } = 131072;
        /// <summary>Ask the server for the model's context window (Test, model change, first answer) and set ContextWindowTokens.</summary>
        public bool DetectContextWindow { get; set; } = true;
        /// <summary>Changed for this session only (test hook): never written to config.json.</summary>
        [JsonIgnore] public bool SessionOnly { get; set; }
        /// <summary>Summarize older messages when the conversation exceeds this share of the context window (0 = never).</summary>
        public int CompactAtPercent { get; set; } = 80;
        /// <summary>Ask the server for token usage in streaming responses (stream_options.include_usage).</summary>
        public bool RequestUsage { get; set; } = true;

        // ---- safety ----
        public bool ReadOnlyMode { get; set; } = false;
        /// <summary>Connections matching one of these wildcard patterns (user@db or db; separated by ; or new lines) are read-only for the agent.</summary>
        public string ProtectedConnections { get; set; } = "*PROD*;*PRD*";
        /// <summary>Save the current database source before the agent replaces or drops a PL/SQL unit or view.</summary>
        public bool BackupBeforeChanges { get; set; } = true;
        public bool SeparateSession { get; set; } = true;
        /// <summary>Queries use the DBA_ data dictionary views instead of ALL_ (company policy of some DBA teams).</summary>
        public bool UseDbaViews { get; set; } = false;
        public bool AutoApproveEditorEdits { get; set; } = false;
        public bool AutoApproveSql { get; set; } = false;
        public bool AutoApproveFileWrites { get; set; } = false;

        // ---- local data (privacy / GDPR) ----
        /// <summary>Save chats and the prompt history on this PC. Off: nothing is written; saved chats and prompts are deleted.</summary>
        public bool SaveChatHistory { get; set; } = true;
        /// <summary>"noResults": saved chats leave out tool results and attached contents; "full": everything.</summary>
        public string ChatHistoryContent { get; set; } = "noResults";
        /// <summary>Saved chats and prompt history entries older than this are deleted (0 = keep).</summary>
        public int ChatRetentionDays { get; set; } = 30;
        /// <summary>Source backups older than this are deleted (0 = keep).</summary>
        public int BackupRetentionDays { get; set; } = 90;
        /// <summary>Log entries older than this are deleted (0 = keep; the log is limited to 2 × 2 MB anyway).</summary>
        public int LogRetentionDays { get; set; } = 30;

        // ---- UI ----
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowWidth { get; set; } = 0;
        public int WindowHeight { get; set; } = 0;
        public bool StayOnTopOfIde { get; set; } = true;
        /// <summary>"auto" (follow PL/SQL Developer), "light" or "dark".</summary>
        public string Theme { get; set; } = "auto";
        /// <summary>UI language: "auto" (PL/SQL Developer's language, then Windows), "en" or "de".</summary>
        public string Language { get; set; } = "auto";
        public bool DevTools { get; set; } = false;

        [JsonProperty("ApiKeyProtected")]
        private string ApiKeyProtected
        {
            get => Protect(ApiKey);
            set => ApiKey = Unprotect(value);
        }

        /// <summary>%APPDATA%\PlsqlDevAgent\config.json, overridable with PLSQLDEVAGENT_CONFIG (used by the test harness).</summary>
        public static string FilePath =>
            Environment.GetEnvironmentVariable("PLSQLDEVAGENT_CONFIG") is string custom && custom.Length > 0
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlsqlDevAgent", "config.json");

        public string ChatCompletionsUrl
        {
            get
            {
                var url = (BaseUrl ?? "").Trim().TrimEnd('/');
                return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? url : url + "/chat/completions";
            }
        }

        public string ModelsUrl
        {
            get
            {
                var url = (BaseUrl ?? "").Trim().TrimEnd('/');
                if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                    url = url.Substring(0, url.Length - "/chat/completions".Length);
                return url + "/models";
            }
        }

        public bool HasWorkspace => !string.IsNullOrWhiteSpace(WorkspaceDir) && Directory.Exists(WorkspaceDir);

        public static AgentConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonConvert.DeserializeObject<AgentConfig>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new AgentConfig();
            }
            catch (Exception ex)
            {
                Util.Log.Error("Could not read config, using defaults", ex);
            }
            return new AgentConfig();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented), Encoding.UTF8);
        }

        public AgentConfig Clone() => JsonConvert.DeserializeObject<AgentConfig>(JsonConvert.SerializeObject(this));

        /// <summary>Settings shown in the settings dialog (includes the plain API key for editing).</summary>
        public JObject ToUiJson()
        {
            var o = JObject.FromObject(this);
            o.Remove("ApiKeyProtected");
            o["ApiKey"] = ApiKey ?? "";
            o["McpServers"] = new JArray((McpServers ?? new List<McpServerConfig>()).Select(s => s.ToUiJson()));
            return o;
        }

        public void UpdateFromUi(JObject o)
        {
            var apiKey = (string)o["ApiKey"];
            o.Remove("ApiKey");
            // lists would be appended to by PopulateObject: replaced as a whole (with the plain header values)
            var servers = o["McpServers"] as JArray;
            o.Remove("McpServers");
            JsonConvert.PopulateObject(o.ToString(), this);
            ApiKey = apiKey ?? "";
            if (servers != null) McpServers = servers.OfType<JObject>().Select(McpServerConfig.FromUiJson).ToList();
        }

        internal static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return "dpapi:" + Convert.ToBase64String(data);
            }
            catch
            {
                return "plain:" + plain;
            }
        }

        internal static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                if (stored.StartsWith("dpapi:"))
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(6)), null, DataProtectionScope.CurrentUser));
                if (stored.StartsWith("plain:")) return stored.Substring(6);
                return stored;
            }
            catch
            {
                return "";
            }
        }
    }
}
