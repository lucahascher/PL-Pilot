using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlsqlDevAgent.Agent
{
    public class ChatHeader
    {
        public string Id;
        public string Title;
        public DateTime Created;
        public DateTime Updated;
        public string Connection;
        public string Model;
        public int Messages;
    }

    public sealed class ChatRecord : ChatHeader
    {
        /// <summary>LLM conversation (OpenAI message format, without system prompt).</summary>
        public JArray History = new JArray();
        /// <summary>UI events to re-render the chat (deltas merged).</summary>
        public JArray Events = new JArray();
        /// <summary>User message id (checkpoint) -> its content in <see cref="History"/>, for "rewind and edit".</summary>
        public System.Collections.Generic.Dictionary<string, string> Checkpoints = new System.Collections.Generic.Dictionary<string, string>();
    }

    /// <summary>Saved chats in %LOCALAPPDATA%\PlsqlDevAgent\chats (one JSON file per chat + index.json).</summary>
    public static class ChatStore
    {
        private const int MaxChats = 200;
        private static readonly object Sync = new object();

        public static string Directory => Path.Combine(Util.Log.Directory, "chats");
        private static string IndexFile => Path.Combine(Directory, "index.json");

        public static void Save(ChatRecord chat)
        {
            lock (Sync)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(PathOf(chat.Id), JsonConvert.SerializeObject(chat), new UTF8Encoding(false));
                var index = ReadIndex().Where(h => h.Id != chat.Id).ToList();
                index.Add(new ChatHeader
                {
                    Id = chat.Id, Title = chat.Title, Created = chat.Created, Updated = chat.Updated,
                    Connection = chat.Connection, Model = chat.Model, Messages = chat.Messages,
                });
                index = index.OrderByDescending(h => h.Updated).ToList();
                foreach (var old in index.Skip(MaxChats)) TryDelete(PathOf(old.Id));
                WriteIndex(index.Take(MaxChats).ToList());
            }
        }

        public static List<ChatHeader> List()
        {
            lock (Sync) return ReadIndex().OrderByDescending(h => h.Updated).ToList();
        }

        public static ChatRecord Load(string id)
        {
            lock (Sync)
            {
                var path = PathOf(id);
                return File.Exists(path) ? JsonConvert.DeserializeObject<ChatRecord>(File.ReadAllText(path, Encoding.UTF8)) : null;
            }
        }

        public static void Delete(string id)
        {
            lock (Sync)
            {
                TryDelete(PathOf(id));
                WriteIndex(ReadIndex().Where(h => h.Id != id).ToList());
            }
        }

        /// <summary>Deletes every saved chat; returns how many there were.</summary>
        public static int DeleteAll()
        {
            lock (Sync)
            {
                if (!System.IO.Directory.Exists(Directory)) return 0;
                int count = ReadIndex().Count;
                foreach (var f in System.IO.Directory.GetFiles(Directory, "*.json")) TryDelete(f);
                return count;
            }
        }

        /// <summary>Deletes chats last updated before <paramref name="cutoff"/> (also files missing from the index).</summary>
        public static int DeleteOlderThan(DateTime cutoff)
        {
            lock (Sync)
            {
                if (!System.IO.Directory.Exists(Directory)) return 0;
                var index = ReadIndex();
                var old = index.Where(h => h.Updated < cutoff).ToList();
                foreach (var h in old) TryDelete(PathOf(h.Id));
                if (old.Count > 0) WriteIndex(index.Except(old).ToList());
                var known = new HashSet<string>(index.Except(old).Select(h => PathOf(h.Id)), StringComparer.OrdinalIgnoreCase) { IndexFile };
                foreach (var f in System.IO.Directory.GetFiles(Directory, "*.json"))
                    if (!known.Contains(f) && File.GetLastWriteTime(f) < cutoff) TryDelete(f);
                if (old.Count > 0) Util.Log.Info($"Deleted {old.Count} saved chat(s) older than {cutoff:yyyy-MM-dd}");
                return old.Count;
            }
        }

        private static string PathOf(string id) => Path.Combine(Directory, new string((id ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()) + ".json");

        private static List<ChatHeader> ReadIndex()
        {
            try
            {
                return File.Exists(IndexFile)
                    ? JsonConvert.DeserializeObject<List<ChatHeader>>(File.ReadAllText(IndexFile, Encoding.UTF8)) ?? new List<ChatHeader>()
                    : new List<ChatHeader>();
            }
            catch (Exception ex)
            {
                Util.Log.Warn("Chat index unreadable: " + ex.Message);
                return new List<ChatHeader>();
            }
        }

        private static void WriteIndex(List<ChatHeader> index) =>
            File.WriteAllText(IndexFile, JsonConvert.SerializeObject(index, Formatting.Indented), new UTF8Encoding(false));

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static string MakeTitle(string firstUserMessage)
        {
            var t = (firstUserMessage ?? "").Replace("\r", " ").Replace("\n", " ").Replace("```sql", "").Replace("```", "").Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            return t.Length > 70 ? t.Substring(0, 67) + "…" : (t.Length == 0 ? "(empty chat)" : t);
        }
    }

    // ------------------------------------------------------------------------------------------ quick commands

    public sealed class ChatCommand
    {
        public string Name;
        public string Description;
        public string Prompt;
        /// <summary>"prompt" = sent to the LLM, "action" = handled by the plug-in (new, compact, help).</summary>
        public string Kind = "prompt";
        public string Source = "built-in";
    }

    public static class Commands
    {
        private const string Target = "the selected code (or, if nothing is selected, the code in the active editor)";

        private static readonly ChatCommand[] BuiltIn =
        {
            new ChatCommand { Name = "explain", Description = "Explain what the selected code / active editor does",
                Prompt = $"Explain what {Target} does: purpose, inputs and outputs, side effects (DML, commits, exceptions) and anything noteworthy. Look up the objects it uses if that helps. Do not change anything.\n\n$ARGUMENTS" },
            new ChatCommand { Name = "review", Description = "Code review: bugs, performance, security, style",
                Prompt = $"Review {Target} like a senior Oracle developer: bugs and edge cases, error handling, transaction handling, performance (SQL in loops, missing bulk operations, implicit conversions, non-sargable predicates), security (dynamic SQL, SQL injection) and readability. Look up the tables and objects it uses. List the findings ordered by severity with line references and concrete fixes. Do not change anything yet.\n\n$ARGUMENTS" },
            new ChatCommand { Name = "document", Description = "Add header comments / documentation",
                Prompt = $"Add or improve documentation comments in {Target}: a header comment for each program unit (purpose, parameters, return value, raised exceptions) in the existing comment style and language. Do not change any logic.\n\n$ARGUMENTS" },
            new ChatCommand { Name = "test", Description = "Write a test block for the program unit",
                Prompt = $"Write a test for {Target}: an anonymous PL/SQL block (or utPLSQL tests if the project already uses utPLSQL) covering normal cases, edge cases and expected errors. Look up the signatures and the tables first. Open the test in a new Test window. Do not execute anything that changes data without asking.\n\n$ARGUMENTS" },
            new ChatCommand { Name = "optimize", Description = "Analyse and improve performance (uses explain plan)",
                Prompt = $"Analyse the performance of {Target}. Use explain_plan for the SQL statements and check the tables' indexes and optimizer statistics (describe_object with include_statistics). Suggest concrete improvements (rewrites, indexes, bulk processing; hints only if justified) and explain the expected effect. Do not change anything yet.\n\n$ARGUMENTS" },
            new ChatCommand { Name = "fix", Description = "Compile the active editor and fix all errors",
                Prompt = "Compile the code in the active editor and fix all compilation errors (edit the editor, then compile again) until it compiles cleanly. " +
                         "If a runtime error stack (ORA-06512 lines) is given below, resolve it with resolve_error_stack and fix the cause instead. Explain briefly what was wrong.\n\n$ARGUMENTS" },
            new ChatCommand { Name = "new", Description = "Start a new chat", Kind = "action" },
            new ChatCommand { Name = "compact", Description = "Summarize the conversation to free context", Kind = "action" },
            new ChatCommand { Name = "help", Description = "List the available commands", Kind = "action" },
        };

        public static List<ChatCommand> All(AgentConfig cfg)
        {
            var result = new List<ChatCommand>(BuiltIn);
            foreach (var line in (cfg.CustomCommands ?? "").Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*/?([\w-]+)\s*[=:]\s*(.+?)\s*$");
                if (m.Success) Add(result, new ChatCommand { Name = m.Groups[1].Value.ToLowerInvariant(), Prompt = m.Groups[2].Value, Description = Shorten(m.Groups[2].Value), Source = "settings" });
            }
            if (cfg.HasWorkspace)
            {
                var dir = Path.Combine(cfg.WorkspaceDir, ".plsqlagent", "commands");
                try
                {
                    if (System.IO.Directory.Exists(dir))
                    {
                        foreach (var file in System.IO.Directory.GetFiles(dir, "*.md"))
                        {
                            var text = File.ReadAllText(file);
                            var lines = text.Replace("\r\n", "\n").Split('\n');
                            var first = lines.FirstOrDefault(l => l.Trim().Length > 0) ?? "";
                            string description = Shorten(first.TrimStart('#', ' '));
                            if (first.TrimStart().StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                            {
                                description = first.Substring(first.IndexOf(':') + 1).Trim();
                                text = string.Join("\n", lines.SkipWhile(l => l.Trim().Length == 0).Skip(1));
                            }
                            Add(result, new ChatCommand { Name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), Prompt = text.Trim(), Description = description, Source = "project" });
                        }
                    }
                }
                catch (Exception ex) { Util.Log.Warn("Reading project commands failed: " + ex.Message); }
            }
            return result;
        }

        /// <summary>"/review focus on exceptions" → the command and its arguments.</summary>
        public static ChatCommand Find(string text, AgentConfig cfg, out string args)
        {
            args = null;
            var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"^\s*/([\w-]+)(?:\s+([\s\S]*))?$");
            if (!m.Success) return null;
            var cmd = All(cfg).FirstOrDefault(c => c.Name.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            if (cmd != null) args = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
            return cmd;
        }

        public static string Expand(ChatCommand cmd, string args)
        {
            var prompt = cmd.Prompt ?? "";
            if (prompt.Contains("$ARGUMENTS"))
                return prompt.Replace("$ARGUMENTS", string.IsNullOrWhiteSpace(args) ? "" : "Additional instructions: " + args).Trim();
            return string.IsNullOrWhiteSpace(args) ? prompt : prompt + "\n\n" + args;
        }

        private static void Add(List<ChatCommand> list, ChatCommand cmd)
        {
            list.RemoveAll(c => c.Name == cmd.Name); // custom commands override built-ins
            list.Add(cmd);
        }

        private static string Shorten(string s)
        {
            s = (s ?? "").Replace('\n', ' ').Trim();
            return s.Length > 70 ? s.Substring(0, 67) + "…" : s;
        }
    }
}
