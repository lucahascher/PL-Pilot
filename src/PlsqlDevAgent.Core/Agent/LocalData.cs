using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Agent
{
    /// <summary>
    /// What the plug-in keeps on this PC and for how long (Settings → Local data): saved chats, prompt history,
    /// source backups and the log. Undo information (rewind, Revert) only lives in memory.
    /// </summary>
    public static class LocalData
    {
        public const string NotStored = "[not stored on this PC: tool results are not saved with the chat (Settings → Local data)]";
        private const string AttachmentsNotStored = "\n\n<attachments>[attached contents are not stored on this PC]</attachments>";

        /// <summary>Deletes what the settings no longer allow: everything of the history when it is off, else what is too old. Never throws.</summary>
        public static void Cleanup(AgentConfig cfg)
        {
            Try("saved chats", () =>
            {
                if (!cfg.SaveChatHistory) ChatStore.DeleteAll();
                else if (cfg.ChatRetentionDays > 0) ChatStore.DeleteOlderThan(DateTime.Now.AddDays(-cfg.ChatRetentionDays));
            });
            Try("prompt history", () => PromptHistory.ApplySettings(cfg));
            Try("backups", () => { if (cfg.BackupRetentionDays > 0) BackupStore.DeleteOlderThan(DateTime.Now.AddDays(-cfg.BackupRetentionDays)); });
            Try("log", () => { if (cfg.LogRetentionDays > 0) Log.DeleteOlderThan(DateTime.Now.AddDays(-cfg.LogRetentionDays)); });
        }

        private static void Try(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { Log.Warn($"Cleaning up {what} failed: {ex.Message}"); }
        }

        /// <summary>The chat as it is written to disk: without tool results and attached contents unless "full" is set.</summary>
        public static ChatRecord ForStorage(ChatRecord chat, AgentConfig cfg)
        {
            if (cfg.ChatHistoryContent == "full") return chat;
            foreach (var m in chat.History.OfType<JObject>())
            {
                var role = (string)m["role"];
                if (role == "tool") m["content"] = NotStored;
                else if (role == "user" && m["content"]?.Type == JTokenType.String) m["content"] = StripAttachments((string)m["content"]);
            }
            foreach (var e in chat.Events.OfType<JObject>().Where(e => (string)e["type"] == "tool_end"))
                e["result"] = L.T("(not stored on this PC)");
            // "edit the message" finds the message by its content: store the same form
            chat.Checkpoints = chat.Checkpoints.ToDictionary(kv => kv.Key, kv => StripAttachments(kv.Value));
            return chat;
        }

        private static string StripAttachments(string content) =>
            Regex.Replace(content ?? "", @"\s*<attachments>[\s\S]*</attachments>\s*$", AttachmentsNotStored);
    }

    /// <summary>Previous prompts for ↑ / ↓ in the input box, with the time they were sent (for the retention period).</summary>
    public static class PromptHistory
    {
        private sealed class Entry
        {
            public string Text;
            public DateTime Time;
        }

        private const int Max = 200;
        private static readonly object Sync = new object();
        private static List<Entry> _entries;

        private static string FilePath => Path.Combine(Log.Directory, "prompts.json");

        public static List<string> Texts(AgentConfig cfg)
        {
            lock (Sync)
            {
                if (!cfg.SaveChatHistory) return new List<string>();
                return Load().Select(e => e.Text).ToList();
            }
        }

        public static void Remember(string text, AgentConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(text) || !cfg.SaveChatHistory) return;
            lock (Sync)
            {
                var entries = Load();
                entries.RemoveAll(e => e.Text == text);
                entries.Add(new Entry { Text = text, Time = DateTime.Now });
                while (entries.Count > Max) entries.RemoveAt(0);
                Save(entries);
            }
        }

        /// <summary>History off: delete the file; otherwise drop entries older than the retention period.</summary>
        public static void ApplySettings(AgentConfig cfg)
        {
            lock (Sync)
            {
                if (!cfg.SaveChatHistory) { Delete(); return; }
                if (cfg.ChatRetentionDays <= 0) return;
                var entries = Load();
                int before = entries.Count;
                entries.RemoveAll(e => e.Time < DateTime.Now.AddDays(-cfg.ChatRetentionDays));
                if (entries.Count != before) Save(entries);
            }
        }

        public static void Delete()
        {
            lock (Sync)
            {
                _entries = null; // read again (empty) on the next access
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
        }

        private static List<Entry> Load()
        {
            if (_entries != null) return _entries;
            _entries = new List<Entry>();
            try
            {
                if (!File.Exists(FilePath)) return _entries;
                var token = JToken.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                var fileTime = File.GetLastWriteTime(FilePath);
                foreach (var t in token as JArray ?? new JArray())
                {
                    if (t.Type == JTokenType.String) _entries.Add(new Entry { Text = (string)t, Time = fileTime }); // old format: texts only
                    else if (t is JObject o && (string)o["Text"] != null) _entries.Add(new Entry { Text = (string)o["Text"], Time = (DateTime?)o["Time"] ?? fileTime });
                }
            }
            catch (Exception ex) { Log.Warn("Prompt history unreadable: " + ex.Message); }
            return _entries;
        }

        private static void Save(List<Entry> entries)
        {
            _entries = entries;
            Directory.CreateDirectory(Log.Directory);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(entries), new UTF8Encoding(false));
        }
    }
}
