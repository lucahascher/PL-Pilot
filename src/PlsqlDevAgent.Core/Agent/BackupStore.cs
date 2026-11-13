using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace PlsqlDevAgent.Agent
{
    public sealed class BackupEntry
    {
        public string Id;
        public DateTime Time;
        public string Connection;
        public string ObjectType;
        public string Owner;
        public string Name;
        public string Reason;
        public string File;
        public int Lines;

        public string Label => $"{ObjectType} {Owner}.{Name}";
        public override string ToString() => $"{Id}  {Time:yyyy-MM-dd HH:mm:ss}  {Label}  ({Lines} lines, {Connection}, before {Reason})";
    }

    /// <summary>
    /// Local history of database source code: before the agent replaces or drops a PL/SQL unit or view, the
    /// current version is saved to %LOCALAPPDATA%\PlsqlDevAgent\backups\&lt;connection&gt;\ (one .sql file each,
    /// plus index.jsonl), so it can always be restored.
    /// </summary>
    public static class BackupStore
    {
        private static readonly object Sync = new object();
        private static string _lastId;
        private static int _sequence;

        public static string Directory => Path.Combine(Util.Log.Directory, "backups");
        private static string IndexFile => Path.Combine(Directory, "index.jsonl");

        public static BackupEntry Save(string connection, string objectType, string owner, string name, string source, string reason)
        {
            lock (Sync)
            {
                var now = DateTime.Now;
                var id = now.ToString("yyyyMMdd-HHmmss-fff");
                if (id == _lastId) id += "-" + (++_sequence);
                else { _lastId = id; _sequence = 0; }
                var dir = Path.Combine(Directory, Sanitize(connection));
                System.IO.Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"{Sanitize(owner)}.{Sanitize(name)}.{Sanitize(objectType.Replace(' ', '_'))}.{id}.sql");
                var text = source.Replace("\r\n", "\n").Replace("\n", "\r\n");
                if (!text.TrimEnd().EndsWith("/")) text = text.TrimEnd() + "\r\n/\r\n";
                File.WriteAllText(file, text, new UTF8Encoding(false));
                var entry = new BackupEntry
                {
                    Id = id, Time = now, Connection = connection, ObjectType = objectType, Owner = owner, Name = name,
                    Reason = reason, File = file, Lines = source.Split('\n').Length,
                };
                File.AppendAllText(IndexFile, JsonConvert.SerializeObject(entry) + "\n", Encoding.UTF8);
                return entry;
            }
        }

        public static List<BackupEntry> List(string connection = null, string name = null, int max = 50)
        {
            lock (Sync)
            {
                if (!File.Exists(IndexFile)) return new List<BackupEntry>();
                return File.ReadAllLines(IndexFile, Encoding.UTF8)
                    .Where(l => l.Trim().Length > 0)
                    .Select(l => { try { return JsonConvert.DeserializeObject<BackupEntry>(l); } catch { return null; } })
                    .Where(e => e != null && File.Exists(e.File))
                    .Where(e => connection == null || string.Equals(e.Connection, connection, StringComparison.OrdinalIgnoreCase))
                    .Where(e => string.IsNullOrWhiteSpace(name) || e.Name.IndexOf(name.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(e => e.Time)
                    .Take(max)
                    .ToList();
            }
        }

        public static BackupEntry Get(string id) => List(max: int.MaxValue).FirstOrDefault(e => e.Id == id);

        public static string ReadSource(BackupEntry entry) => File.ReadAllText(entry.File, Encoding.UTF8);

        private static string Sanitize(string s) => Regex.Replace(s ?? "", @"[^\w@.\-$#]", "_");
    }

    /// <summary>Undo information for edits the agent applied (editor text or files), kept for the IDE session.</summary>
    public static class EditJournal
    {
        public sealed class Record
        {
            public string Id;
            public string Kind;      // "editor" or "file"
            public string Label;
            public string Before;    // null = file did not exist
            public string After;
            public string Path;
            public Encoding Encoding;
        }

        private static readonly Dictionary<string, Record> Records = new Dictionary<string, Record>();

        public static string Add(Record r)
        {
            lock (Records)
            {
                r.Id = "e" + Guid.NewGuid().ToString("N").Substring(0, 10);
                Records[r.Id] = r;
                return r.Id;
            }
        }

        public static Record Get(string id)
        {
            lock (Records) return id != null && Records.TryGetValue(id, out var r) ? r : null;
        }

        public static void Remove(string id)
        {
            lock (Records) Records.Remove(id ?? "");
        }
    }
}
