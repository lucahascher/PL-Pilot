using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlsqlDevAgent.Harness
{
    /// <summary>
    /// Tiny scripted OpenAI-compatible server (raw TCP, so no URL ACL / admin rights needed).
    /// Script: user message -> get_editor_text -> edit_editor -> run_query -> final markdown answer.
    /// </summary>
    internal sealed class MockLlmServer
    {
        private readonly TcpListener _listener;
        public int Port { get; }
        public int Requests;
        /// <summary>Requests that announced "Expect: 100-continue" (the client then waits before sending the body).</summary>
        public static int ExpectContinueRequests;

        public MockLlmServer(int port)
        {
            Port = port;
            _listener = new TcpListener(IPAddress.Loopback, port);
        }

        public void Start()
        {
            _listener.Start();
            Task.Run(async () =>
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => Handle(client));
                }
            });
        }

        private async Task Handle(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                // Read the header bytes up to CRLFCRLF, then exactly Content-Length body bytes.
                var header = new MemoryStream();
                var one = new byte[1];
                while (true)
                {
                    if (await stream.ReadAsync(one, 0, 1) == 0) return;
                    header.WriteByte(one[0]);
                    var h = header.GetBuffer();
                    long len = header.Length;
                    if (len >= 4 && h[len - 4] == '\r' && h[len - 3] == '\n' && h[len - 2] == '\r' && h[len - 1] == '\n') break;
                }
                var headerLines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                var requestLine = headerLines[0];
                int contentLength = headerLines
                    .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    .Select(l => int.Parse(l.Substring(15).Trim())).FirstOrDefault();
                var buf = new byte[contentLength];
                int read = 0;
                while (read < contentLength) { int n = await stream.ReadAsync(buf, read, contentLength - read); if (n == 0) break; read += n; }
                var body = Encoding.UTF8.GetString(buf, 0, read);
                Interlocked.Increment(ref Requests);

                if (requestLine.StartsWith("GET") && requestLine.Contains("/models"))
                {
                    await WriteAll(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n" +
                        "{\"data\":[{\"id\":\"mock-deepseek\"},{\"id\":\"mock-other\"}]}");
                    return;
                }

                if (headerLines.Any(l => l.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase))) Interlocked.Increment(ref ExpectContinueRequests);
                if (body.Contains("Task: PARALLEL research")) await Task.Delay(1500); // a slow model answer for the helpers

                await WriteAll(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
                foreach (var chunk in Script(JObject.Parse(body)))
                {
                    await WriteAll(stream, "data: " + chunk.ToString(Formatting.None) + "\n\n");
                    await Task.Delay(25);
                }
                await WriteAll(stream, "data: [DONE]\n\n");
            }
        }

        private static System.Collections.Generic.IEnumerable<JObject> Final(string text)
        {
            foreach (var c in Text(text)) yield return c;
            yield return Delta(new JObject(), "stop");
        }

        private static JObject Tasks(params string[] statusAndContent)
        {
            var arr = new JArray();
            foreach (var s in statusAndContent) { var p = s.Split('|'); arr.Add(new JObject { ["content"] = p[1], ["status"] = p[0] }); }
            return new JObject { ["tasks"] = arr };
        }

        /// <summary>Scripted flows for long tasks, sub-tasks and plan mode (null = default script).</summary>
        private static System.Collections.Generic.IEnumerable<JObject> Flow(JArray messages, string system, string request, JObject last)
        {
            string role = (string)last["role"];
            var calls = messages.OfType<JObject>().Where(m => m["tool_calls"] != null).SelectMany(m => m["tool_calls"]).Select(c => (string)c["function"]["name"]).ToList();
            int taskUpdates = calls.Count(c => c == "update_tasks");

            if (system.StartsWith("You summarize")) return null;
            if (system.StartsWith("You are a research helper"))
            {
                var task = (string)messages[1]["content"] ?? "";
                int p = task.IndexOf("PARALLEL research ", StringComparison.Ordinal);
                var label = p >= 0 ? " for " + task.Substring(p + 18, 1) : "";
                return role == "user"
                    ? ToolCall("sub_1", "search_source", new JObject { ["text"] = "EMP" })
                    : Final($"Findings{label}: table EMP is used in EMP_API.list_emps (line 4).");
            }

            if (request.Contains("DMLTEST"))
            {
                bool done = messages.OfType<JObject>().Reverse().TakeWhile(m => (string)m["role"] != "user").Any(m => m["tool_calls"] != null);
                return done ? Final("Raised the salaries of department 10. The change is not committed yet.")
                    : ToolCall("dml_" + messages.Count, "execute_sql", new JObject { ["sql"] = "update emp set sal = sal * 1.1 where deptno = 10", ["purpose"] = "Raise salaries" });
            }

            if (request.Contains("REWINDTEST"))
            {
                var sinceUser = messages.OfType<JObject>().Reverse().TakeWhile(m => (string)m["role"] != "user").ToList();
                bool edited = sinceUser.Any(m => m["tool_calls"] != null);
                if (request.Contains("REWINDTEST one"))
                    return edited ? Final("Step one done.") : ToolCall("rw_one", "edit_editor", new JObject { ["old_string"] = "select *", ["new_string"] = "select e.empno" });
                return edited ? Final("Step two done.")
                    : ToolCall("rw_two", "edit_editor", new JObject { ["old_string"] = "where e.deptno = p_deptno", ["new_string"] = "where e.deptno = p_deptno and e.sal > 0" });
            }

            if (request.Contains("PARALLEL"))
            {
                if (!calls.Contains("run_subtask"))
                    return ToolCalls(new[] { "A", "B", "C" }.Select((x, i) => ("pa_" + i, "run_subtask", new JObject { ["task"] = "PARALLEL research " + x })).ToArray());
                var order = messages.OfType<JObject>().Where(m => (string)m["role"] == "tool")
                    .Select(m => System.Text.RegularExpressions.Regex.Match((string)m["content"] ?? "", @"Findings for (\w)").Groups[1].Value);
                return Final("Parallel reports in order: " + string.Join(",", order) + ". " + string.Join(" ", Enumerable.Repeat("More text to stream.", 40)));
            }

            if (request.Contains("LONGTASK"))
            {
                if (role == "user" && last["content"].ToString().StartsWith("[auto-continue]"))
                    return ToolCall("lt_3", "update_tasks", Tasks("completed|Task A", "completed|Task B"));
                if (taskUpdates == 0) return ToolCall("lt_1", "update_tasks", Tasks("in_progress|Task A", "pending|Task B"));
                if (taskUpdates == 1) return ToolCall("lt_2", "update_tasks", Tasks("completed|Task A", "pending|Task B"));
                if (taskUpdates == 2) return Final("Task A is done. Should I continue with Task B?");
                return Final("All done: Task A and Task B are finished.");
            }

            if (request.Contains("SUBTASK"))
                return calls.Contains("run_subtask")
                    ? Final("Summary: the helper found EMP in EMP_API.list_emps.")
                    : ToolCall("st_1", "run_subtask", new JObject { ["task"] = "Find all usages of table EMP and report object and line." });

            if (request.Contains("PLANTEST"))
                return taskUpdates == 0
                    ? ToolCall("pl_1", "update_tasks", Tasks("pending|Replace SELECT * in list_emps"))
                    : Final("Plan:\n1. Replace `SELECT *` in list_emps with an explicit column list.\n\nApprove to execute.");

            if (request.Contains("Execute the plan"))
            {
                int executes = messages.OfType<JObject>().Count(m => (string)m["role"] == "user" && ((string)m["content"] ?? "").Contains("Execute the plan"));
                var sinceExecute = messages.OfType<JObject>().SkipWhile(m => !((string)m["content"] ?? "").Contains("Execute the plan")).ToList();
                var callsSince = sinceExecute.Where(m => m["tool_calls"] != null).SelectMany(m => m["tool_calls"]).Select(c => (string)c["function"]["name"]).ToList();
                if (!callsSince.Contains("edit_editor"))
                    return ToolCall("ex_1", "edit_editor", new JObject { ["old_string"] = "select *", ["new_string"] = "select e.empno, e.ename" });
                if (!callsSince.Contains("update_tasks"))
                    return ToolCall("ex_2", "update_tasks", Tasks("completed|Replace SELECT * in list_emps"));
                return Final("Plan executed.");
            }
            return null;
        }

        private static Task WriteAll(Stream s, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return s.WriteAsync(bytes, 0, bytes.Length);
        }

        private static JObject Delta(JObject delta, string finish = null) => new JObject
        {
            ["id"] = "chatcmpl-mock",
            ["object"] = "chat.completion.chunk",
            ["choices"] = new JArray { new JObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish } },
        };

        private static System.Collections.Generic.IEnumerable<JObject> Text(string text, string field = "content")
        {
            foreach (var piece in text.Split(' '))
                yield return Delta(new JObject { [field] = piece + " " });
        }

        private static System.Collections.Generic.IEnumerable<JObject> ToolCall(string id, string name, JObject args)
        {
            var json = args.ToString(Formatting.None);
            int half = json.Length / 2;
            yield return Delta(new JObject { ["tool_calls"] = new JArray { new JObject { ["index"] = 0, ["id"] = id, ["type"] = "function", ["function"] = new JObject { ["name"] = name, ["arguments"] = json.Substring(0, half) } } } });
            yield return Delta(new JObject { ["tool_calls"] = new JArray { new JObject { ["index"] = 0, ["function"] = new JObject { ["arguments"] = json.Substring(half) } } } });
            yield return Delta(new JObject(), "tool_calls");
        }

        /// <summary>Several tool calls in one answer (index 0, 1, 2 …).</summary>
        private static System.Collections.Generic.IEnumerable<JObject> ToolCalls(params (string Id, string Name, JObject Args)[] calls)
        {
            for (int i = 0; i < calls.Length; i++)
            {
                var c = calls[i];
                yield return Delta(new JObject { ["tool_calls"] = new JArray { new JObject { ["index"] = i, ["id"] = c.Id, ["type"] = "function",
                    ["function"] = new JObject { ["name"] = c.Name, ["arguments"] = c.Args.ToString(Formatting.None) } } } });
            }
            yield return Delta(new JObject(), "tool_calls");
        }

        /// <summary>Tool names offered in the most recent main-agent request (to verify plan mode).</summary>
        public static System.Collections.Generic.List<string> LastTools = new System.Collections.Generic.List<string>();

        /// <summary>System prompt of the most recent main-agent request (to verify prompt customization).</summary>
        public static string LastSystemPrompt = "";
        public static string LastUserContent = "";
        /// <summary>All messages of the most recent main-agent request.</summary>
        public static string LastMessagesJson = "";

        private static System.Collections.Generic.IEnumerable<JObject> Script(JObject request)
        {
            var messages = (JArray)request["messages"];
            var last = (JObject)messages.Last;
            var system = (string)messages.First["content"] ?? "";
            if (!system.StartsWith("You are a research helper") && !system.StartsWith("You summarize"))
            {
                LastSystemPrompt = system;
                LastMessagesJson = messages.ToString(Formatting.None);
                var lastUserMsg = messages.OfType<JObject>().LastOrDefault(m => (string)m["role"] == "user");
                if (lastUserMsg != null) LastUserContent = (string)lastUserMsg["content"] ?? "";
            }
            var firstUser = messages.OfType<JObject>().LastOrDefault(m => (string)m["role"] == "user" && !((string)m["content"] ?? "").StartsWith("[auto-continue]"));
            var request_ = (string)firstUser?["content"] ?? "";
            var flow = Flow(messages, system, request_, last);
            if (flow != null)
            {
                if (!system.StartsWith("You are a research helper") && !system.StartsWith("You summarize"))
                    LastTools = (request["tools"] as JArray ?? new JArray()).Select(t => (string)t["function"]["name"]).ToList();
                foreach (var c in flow) yield return c;
                yield break;
            }
            string lastTool = null;
            if ((string)last["role"] == "tool")
            {
                var id = (string)last["tool_call_id"];
                lastTool = messages.OfType<JObject>()
                    .Where(m => m["tool_calls"] != null)
                    .SelectMany(m => m["tool_calls"])
                    .Where(c => (string)c["id"] == id)
                    .Select(c => (string)c["function"]["name"])
                    .FirstOrDefault();
            }

            if (((string)messages.First["content"] ?? "").StartsWith("You summarize a conversation"))
            {
                foreach (var c in Text("The user asked to improve the query in EMP_API.list_emps; SELECT * was replaced by an explicit column list.")) yield return c;
                yield return Delta(new JObject(), "stop");
            }
            else if ((string)last["role"] == "user" && ((string)last["content"] ?? "").Contains("Answer briefly"))
            {
                foreach (var c in Text("Short answer: the package lists employees.")) yield return c;
                yield return Delta(new JObject(), "stop");
            }
            else if ((string)last["role"] == "user")
            {
                foreach (var c in Text("The user wants the query improved. I should read the editor first.", "reasoning_content")) yield return c;
                foreach (var c in Text("Let me look at the code in the editor first.")) yield return c;
                foreach (var c in ToolCall("call_1", "get_editor_text", new JObject { ["with_line_numbers"] = false })) yield return c;
            }
            else if (lastTool == "get_editor_text")
            {
                foreach (var c in Text("I'll replace `SELECT *` with an explicit column list.")) yield return c;
                foreach (var c in ToolCall("call_2", "edit_editor", new JObject { ["old_string"] = "select *", ["new_string"] = "select e.empno, e.ename, e.sal" })) yield return c;
            }
            else if (lastTool == "edit_editor")
            {
                foreach (var c in ToolCall("call_3", "run_query", new JObject { ["sql"] = "select count(*) from emp", ["max_rows"] = 5 })) yield return c;
            }
            else
            {
                var answer = "## Done\n\nI replaced `SELECT *` with an **explicit column list** and verified the table has rows.\n\n" +
                             "```sql\nselect e.empno, e.ename, e.sal\n  from emp e\n where e.deptno = p_deptno; -- comment\n```\n\n" +
                             "| Check | Result |\n|---|---|\n| Rows in EMP | 14 |\n| Compiled | yes |\n\n- point one\n- point two\n";
                foreach (var c in Text(answer)) yield return c;
                yield return Delta(new JObject(), "stop");
            }
            // usage chunk as sent with stream_options.include_usage
            yield return new JObject
            {
                ["id"] = "chatcmpl-mock", ["object"] = "chat.completion.chunk", ["choices"] = new JArray(),
                ["usage"] = new JObject { ["prompt_tokens"] = 9000 + messages.ToString().Length / 4, ["completion_tokens"] = 120, ["total_tokens"] = 9120 },
            };
        }
    }
}
