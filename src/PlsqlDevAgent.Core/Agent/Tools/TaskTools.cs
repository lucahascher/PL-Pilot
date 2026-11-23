using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PlsqlDevAgent.Agent.Tools
{
    /// <summary>The agent's own task list (like Claude Code's todo list); shown live in the chat.</summary>
    internal sealed class UpdateTasksTool : AgentTool
    {
        public override string Name => "update_tasks";
        public override string Description =>
            "Creates or updates your task list for the current request (the user sees it as a checklist). Use it for every " +
            "task with 3 or more steps: write the plan first, keep exactly one task in_progress, mark tasks completed as " +
            "soon as they are done and add tasks you discover. Always send the COMPLETE list; it replaces the previous one.";

        public override JObject Parameters => JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""tasks"": {
      ""type"": ""array"",
      ""description"": ""The complete task list, in order."",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""content"": { ""type"": ""string"", ""description"": ""Short description of the task, e.g. 'Compile package ORDERS_API and fix errors'."" },
          ""status"": { ""type"": ""string"", ""enum"": [""pending"", ""in_progress"", ""completed""] }
        },
        ""required"": [""content"", ""status""]
      }
    }
  },
  ""required"": [""tasks""]
}");

        public override string Summarize(JObject args)
        {
            var tasks = Normalize(args, out _);
            return tasks == null ? "" : Util.L.T("{0}/{1} done", tasks.Count(t => (string)t["status"] == "completed"), tasks.Count);
        }

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var tasks = Normalize(args, out var error);
            if (tasks == null) return Task.FromResult("Error: " + error);
            ctx.SetTasks?.Invoke(tasks);
            int done = tasks.Count(t => (string)t["status"] == "completed");
            var current = tasks.FirstOrDefault(t => (string)t["status"] == "in_progress");
            var open = tasks.Where(t => (string)t["status"] != "completed").Select(t => (string)t["content"]).ToList();
            return Task.FromResult($"Task list updated: {done}/{tasks.Count} completed." +
                                   (current != null ? $" In progress: {current["content"]}." : "") +
                                   (open.Count > 0 ? " Continue with the open tasks." : " All tasks are completed: give the user a short summary."));
        }

        /// <summary>Accepts the array or a JSON string (some local models stringify nested arrays).</summary>
        internal static JArray Normalize(JObject args, out string error)
        {
            error = null;
            var token = args?["tasks"];
            if (token != null && token.Type == JTokenType.String)
            {
                try { token = JToken.Parse((string)token); }
                catch { error = "'tasks' must be an array of {content, status}."; return null; }
            }
            if (!(token is JArray arr)) { error = "'tasks' must be an array of {content, status}."; return null; }
            var result = new JArray();
            foreach (var t in arr)
            {
                string content = t.Type == JTokenType.String ? (string)t : (string)t["content"] ?? (string)t["task"] ?? (string)t["title"];
                if (string.IsNullOrWhiteSpace(content)) continue;
                var status = (t.Type == JTokenType.Object ? (string)t["status"] : null)?.Trim().ToLowerInvariant() ?? "pending";
                if (status == "done" || status == "complete") status = "completed";
                if (status == "in progress" || status == "active" || status == "doing") status = "in_progress";
                if (status != "pending" && status != "in_progress" && status != "completed") status = "pending";
                result.Add(new JObject { ["content"] = content.Trim(), ["status"] = status });
            }
            if (result.Count == 0) { error = "The task list is empty."; return null; }
            return result;
        }
    }

    /// <summary>Delegates a research question to a helper agent with a fresh context (read-only tools).</summary>
    internal sealed class RunSubtaskTool : AgentTool
    {
        public const string ToolName = "run_subtask";
        public override string Name => ToolName;
        public override string Description =>
            "Delegates a self-contained research task to a helper agent that has its own fresh context and the read-only " +
            "tools (queries, object source, search, files). Use it for broad investigations, e.g. 'find all code that " +
            "writes to table ORDERS and summarize how', so that this conversation stays small. The helper cannot change " +
            "anything and returns a report of its findings. Describe the task completely; the helper does not see this conversation. " +
            "Several run_subtask calls in one response run in parallel.";
        public override JObject Parameters => Schema(
            Req("task", "string", "Complete description of what to find out and what the report should contain."));
        public override string Summarize(JObject args)
        {
            var t = Str(args, "task", "");
            return t.Length > 100 ? t.Substring(0, 97) + "..." : t;
        }

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            if (ctx.RunSubtask == null) return "Error: sub-tasks are not available here.";
            var report = await ctx.RunSubtask(ReqStr(args, "task"));
            return "Report of the helper agent:\n" + report;
        }
    }

    /// <summary>A question to the user with answer buttons; the answer comes back as the result of this call.</summary>
    internal sealed class AskUserTool : AgentTool
    {
        public const string ToolName = "ask_user";
        public override string Name => ToolName;
        public override string Description =>
            "Asks the user a question that only the user can decide, with 2 to 5 answer options shown as buttons (the user " +
            "can also type another answer). Waits for the answer and returns it, so you can continue in the same turn. " +
            "Use it for real decisions – e.g. whether a change goes to the database, the local files or both – not to " +
            "confirm single actions (changes are shown for approval anyway) and not for things you can find out yourself.";
        public override JObject Parameters => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["question"] = new JObject { ["type"] = "string", ["description"] = "The question, short and in the user's language." },
                ["options"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["description"] = "2 to 5 short answers (a few words each), in the user's language.",
                },
            },
            ["required"] = new JArray("question", "options"),
        };
        public override string Summarize(JObject args) => Str(args, "question", "");

        public override async Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            if (ctx.AskUser == null) return "Not available: nobody can answer here (helper agents cannot ask the user). Decide yourself or report the open question.";
            var question = ReqStr(args, "question").Trim();
            var options = (args?["options"] is JArray a ? a.Select(x => x.Type == JTokenType.String ? (string)x : x.ToString())
                          : (Str(args, "options") ?? "").Split(new[] { '|', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).Distinct().Take(5).ToArray();
            var answer = await ctx.AskUser(question, options).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(answer)) return "The user gave no answer. Ask again in your reply or decide carefully yourself.";
            return options.Contains(answer)
                ? $"The user chose: {answer}"
                : $"The user answered: {answer}";
        }
    }
}
