using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.Util;
using static PlsqlDevAgent.Agent.SqlUtil;

namespace PlsqlDevAgent.Agent.Tools
{
    /// <summary>
    /// Index of the database objects in the local source files of the project folder (all sub folders, e.g. one per
    /// schema): which file contains which unit, in which lines. Files are parsed again only when they changed.
    /// The schema of a unit is the one in its CREATE statement, else the nearest folder named like a schema.
    /// </summary>
    internal static class LocalSources
    {
        public sealed class Unit
        {
            /// <summary>Path relative to the project folder.</summary>
            public string Path;
            public string Type, Owner, Name;
            /// <summary>"statement", "folder" or null (schema unknown).</summary>
            public string OwnerFrom;
            public int StartLine, EndLine;
            public override string ToString() => $"{Path} lines {StartLine}-{EndLine} ({Type})";
        }

        private sealed class FileEntry
        {
            public DateTime Modified;
            public long Size;
            public List<Unit> Units;
        }

        /// <summary>Extensions of SQL / PL/SQL source files (PL/SQL Developer, SQL*Plus and common conventions).</summary>
        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".sql", ".pks", ".pkb", ".pck", ".pkg", ".pkh", ".spc", ".bdy", ".prc", ".fnc", ".trg", ".typ", ".tps", ".tpb",
            ".tyb", ".vw", ".pls", ".plb", ".seq", ".tab", ".ddl", ".mv", ".syn",
        };

        private static readonly Regex Create = new Regex(
            @"^[ \t]*CREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:NON)?EDITIONABLE\s+)?(?:NO\s*FORCE\s+|FORCE\s+)?(?:(?:GLOBAL|PRIVATE)\s+TEMPORARY\s+)?" +
            @"(?<type>PACKAGE\s+BODY|TYPE\s+BODY|PACKAGE|TYPE|PROCEDURE|FUNCTION|TRIGGER|MATERIALIZED\s+VIEW|VIEW|TABLE|SEQUENCE|(?:PUBLIC\s+)?SYNONYM)\s+" +
            @"(?:IF\s+NOT\s+EXISTS\s+)?(?:(?<o>""[^""\r\n]+""|[A-Za-z][\w$#]*)\s*\.\s*)?(?<n>""[^""\r\n]+""|[A-Za-z][\w$#]*)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly object Lock = new object();
        private static string _root;
        private static Dictionary<string, FileEntry> _files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _scanned;
        private static List<Unit> _units = new List<Unit>();
        private static int _fileCount;

        /// <summary>Schema names of the connected database (to recognise schema folders), per connection.</summary>
        private static readonly Dictionary<string, HashSet<string>> Schemas = new Dictionary<string, HashSet<string>>();

        private const long MaxFileBytes = 5_000_000;

        /// <summary>All indexed units (rescans changed files at most every few seconds).</summary>
        public static List<Unit> All(ToolContext ctx, out int fileCount)
        {
            var root = Workspace.Root(ctx);
            var schemas = SchemaNames(ctx);
            lock (Lock)
            {
                if (!string.Equals(root, _root, StringComparison.OrdinalIgnoreCase))
                {
                    _root = root;
                    _files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
                    _scanned = DateTime.MinValue;
                }
                if ((DateTime.UtcNow - _scanned).TotalSeconds > 5) Scan(ctx, root);
                fileCount = _fileCount;
                // the schema from the folder depends on the connection's schema list: applied on every call
                return _units.Select(u => WithFolderOwner(u, schemas)).ToList();
            }
        }

        private static void Scan(ToolContext ctx, string root)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int parsed = 0;
            foreach (var path in Workspace.EnumerateFiles(root, "*", true))
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                if (!Extensions.Contains(System.IO.Path.GetExtension(path))) continue;
                seen.Add(path);
                FileInfo info;
                try { info = new FileInfo(path); } catch { continue; }
                if (_files.TryGetValue(path, out var entry) && entry.Modified == info.LastWriteTimeUtc && entry.Size == info.Length) continue;
                var units = new List<Unit>();
                try
                {
                    if (info.Length <= MaxFileBytes && !TextFile.LooksBinary(path))
                        units = Parse(TextFile.Read(path, out _), path.Substring(root.Length).TrimStart('\\'));
                }
                catch (Exception ex) { Log.Warn($"Indexing {path} failed: {ex.Message}"); }
                _files[path] = new FileEntry { Modified = info.LastWriteTimeUtc, Size = info.Length, Units = units };
                parsed++;
            }
            foreach (var gone in _files.Keys.Where(k => !seen.Contains(k)).ToList()) _files.Remove(gone);
            _units = _files.Values.SelectMany(f => f.Units).ToList();
            _fileCount = _files.Count;
            _scanned = DateTime.UtcNow;
            if (parsed > 0) Log.Info($"Project folder index: {_units.Count} objects in {_fileCount} files ({parsed} parsed) in {watch.ElapsedMilliseconds} ms");
        }

        /// <summary>The CREATE statements of one file, each up to its "/" line, the next CREATE or the end of the file.</summary>
        internal static List<Unit> Parse(string text, string relativePath)
        {
            var units = new List<Unit>();
            text = text.Replace("\r\n", "\n");
            var lines = text.Split('\n');
            // line number of every character offset: count line feeds as we go
            var matches = Create.Matches(text).Cast<Match>().ToList();
            var starts = new List<int>();
            int line = 1, pos = 0;
            foreach (var m in matches)
            {
                for (; pos < m.Index; pos++) if (text[pos] == '\n') line++;
                starts.Add(line);
            }
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                int start = starts[i];
                int limit = i + 1 < matches.Count ? starts[i + 1] - 1 : lines.Length;
                int end = limit;
                for (int l = start + 1; l <= limit; l++)
                    if (lines[l - 1].Trim() == "/") { end = l - 1; break; }
                while (end > start && lines[end - 1].Trim().Length == 0) end--;
                var type = Regex.Replace(m.Groups["type"].Value.ToUpperInvariant(), @"\s+", " ");
                if (type == "PUBLIC SYNONYM") type = "SYNONYM";
                units.Add(new Unit
                {
                    Path = relativePath,
                    Type = type,
                    Owner = m.Groups["o"].Success ? Ident(m.Groups["o"].Value) : (m.Groups["type"].Value.ToUpperInvariant().StartsWith("PUBLIC") ? "PUBLIC" : null),
                    OwnerFrom = m.Groups["o"].Success ? "statement" : null,
                    Name = Ident(m.Groups["n"].Value),
                    StartLine = start,
                    EndLine = end,
                });
            }
            return units;
        }

        private static Unit WithFolderOwner(Unit u, HashSet<string> schemas)
        {
            if (u.Owner != null || schemas == null) return u;
            var folders = u.Path.Split('\\', '/');
            for (int i = folders.Length - 2; i >= 0; i--)
            {
                if (!schemas.Contains(folders[i].ToUpperInvariant())) continue;
                return new Unit { Path = u.Path, Type = u.Type, Owner = folders[i].ToUpperInvariant(), OwnerFrom = "folder", Name = u.Name, StartLine = u.StartLine, EndLine = u.EndLine };
            }
            return u;
        }

        private static HashSet<string> SchemaNames(ToolContext ctx)
        {
            try
            {
                var c = ctx.Ide.GetContext();
                if (!c.Connected) return null;
                var key = ctx.ConnectionLabel;
                lock (Schemas) if (Schemas.TryGetValue(key, out var known)) return known;
                var set = new HashSet<string>(ctx.Ide.Query("select username from all_users", 100000).Rows.Select(r => r[0]), StringComparer.OrdinalIgnoreCase);
                lock (Schemas) Schemas[key] = set;
                return set;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("Schema names for the project folder index: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The local units of a database object (a package or type: specification and body). Units without a known schema
        /// match any schema.
        /// </summary>
        public static List<Unit> Find(ToolContext ctx, string owner, string name, string type)
        {
            var baseType = (type ?? "").Replace(" BODY", "");
            return All(ctx, out _).Where(u => u.Name == name && (owner == null || u.Owner == null || u.Owner == owner) &&
                                             (string.IsNullOrEmpty(baseType) || u.Type.Replace(" BODY", "") == baseType))
                .OrderBy(u => u.Owner == null ? 1 : 0).ThenBy(u => u.Type).ThenBy(u => u.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The text of a unit as it is in the file now.</summary>
        public static string Text(ToolContext ctx, Unit u)
        {
            var lines = TextFile.Read(Workspace.Resolve(ctx, u.Path), out _).Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.Skip(u.StartLine - 1).Take(Math.Max(0, u.EndLine - u.StartLine + 1)));
        }

        private static readonly HashSet<string> PlsqlTypes = new HashSet<string>
        {
            "PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "TYPE BODY",
        };

        /// <summary>Source for comparing: without CREATE OR REPLACE, the schema prefix, blanks at line ends and case.</summary>
        private static string Comparable(string source)
        {
            var s = Db.NormalizeSource(source);
            return Regex.Replace(s, @"^((?:PACKAGE|TYPE)\s+BODY|PACKAGE|TYPE|PROCEDURE|FUNCTION|TRIGGER)\s+(?:""?[\w$#]+""?\s*\.\s*)?""?([\w$#]+)""?", "$1 $2");
        }

        /// <summary>"identical to the database", "differs from the database (12 lines)", "not in the database" or null (not compared).</summary>
        public static string Compare(ToolContext ctx, Unit u, string owner)
        {
            if (!PlsqlTypes.Contains(u.Type) || owner == null) return null;
            try
            {
                var db = Db.CurrentSource(ctx, u.Type, owner, u.Name);
                if (db == null) return "not in the database";
                var local = Comparable(Text(ctx, u));
                var remote = Comparable(db);
                if (local == remote) return "identical to the database";
                var a = remote.Split('\n');
                var b = local.Split('\n');
                if (a.Length > 4000 || b.Length > 4000) return "differs from the database";
                int changed = LineDiff.Unified(remote, local, 0).Split('\n').Count(l => l.StartsWith("+") || l.StartsWith("-"));
                return $"differs from the database ({changed} changed lines)";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is OracleSqlException || ex is IOException || ex is ToolArgumentException)
            {
                return "not compared: " + ex.Message.Split('\n')[0];
            }
        }

        /// <summary>Lines "  path lines a-b (TYPE): identical to the database" for the local files of an object; "" if none.</summary>
        public static string Describe(ToolContext ctx, string owner, string name, string type, bool compare, string indent = "  ")
        {
            var sb = new StringBuilder();
            foreach (var u in Find(ctx, owner, name, type))
            {
                sb.Append(indent).Append(u.Path.Replace('\\', '/')).Append($" lines {u.StartLine}-{u.EndLine} ({u.Type}");
                if (u.Owner == null) sb.Append(", schema not given in the file or its folders");
                else if (owner != null && u.Owner != owner) sb.Append($", schema {u.Owner}");
                sb.Append(')');
                var cmp = compare ? Compare(ctx, u, u.Owner ?? owner) : null;
                if (cmp != null) sb.Append(": ").Append(cmp);
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Database changes of objects that also have local source files: the first time in a chat the plug-in asks the user
    /// whether such changes go to the database, the local files or both (independent of the model following its rules),
    /// and every later change follows that answer.
    /// </summary>
    internal static class LocalChanges
    {
        public const string Database = "database", Local = "local", Both = "both";

        /// <summary>The decision in words for the model, or null (not decided).</summary>
        public static string Describe(string decision) =>
            decision == Database ? "the database only (local files stay unchanged)"
            : decision == Local ? "the local files only (not the database)"
            : decision == Both ? "the database and the local files (keep them identical)"
            : null;

        /// <summary>
        /// Before the database (or an editor window showing a database object) is changed for these units: Proceed false =
        /// do not change it, Message says why and which local files to change instead; Proceed true with an optional
        /// Message to add to the result (the local files to change as well).
        /// </summary>
        public static async Task<(bool Proceed, string Message)> CheckAsync(ToolContext ctx, IEnumerable<(string Type, string Owner, string Name)> units)
        {
            if (!ctx.Config.HasWorkspace || ctx.ChangeTarget == null) return (true, null);
            List<LocalSources.Unit> files;
            try
            {
                files = new List<LocalSources.Unit>();
                foreach (var u in units)
                {
                    var found = LocalSources.Find(ctx, u.Owner, u.Name, u.Type);
                    var exact = found.Where(f => f.Type == u.Type).ToList();
                    files.AddRange(exact.Count > 0 ? exact : found);
                }
                files = files.GroupBy(f => f.Path + "|" + f.StartLine).Select(g => g.First()).ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("Local files of the changed units: " + ex.Message);
                return (true, null);
            }
            if (files.Count == 0) return (true, null);

            var list = string.Join("\n", files.Select(f => "  " + f.Path.Replace('\\', '/') + $" lines {f.StartLine}-{f.EndLine} ({f.Type})"));
            var decision = ctx.ChangeTarget.Value;
            if (decision == null)
            {
                if (ctx.AskUser == null) return (true, null); // helper agents do not change anything anyway
                var names = string.Join(", ", units.Select(u => u.Name).Distinct());
                string db = L.T("Database"), local = L.T("Local files"), both = L.T("Both");
                var answer = await ctx.AskUser(
                    L.T("{0} also exists in local files of the project folder ({1}). Where should the agent make changes in this chat?",
                        names, string.Join(", ", files.Select(f => System.IO.Path.GetFileName(f.Path)).Distinct())),
                    new[] { db, local, both }).ConfigureAwait(false);
                decision = answer == db ? Database : answer == local ? Local : answer == both ? Both : null;
                if (decision == null)
                    return (false, $"Nothing was changed. Asked whether to change the database or the local files, the user answered: \"{answer}\". " +
                                   $"Follow that answer. The local files of these objects:\n{list}");
                ctx.ChangeTarget.Value = decision;
                Log.Info($"Changes of objects with local files go to: {decision}");
            }
            switch (decision)
            {
                case Local:
                    return (false, "Not done: the user wants changes of objects that have local source files made in the local files only, " +
                                   "not in the database (their choice in this chat). Make this change with edit_file in every one of these files " +
                                   $"(read them first with read_file):\n{list}\n" +
                                   "If the user now explicitly wants the database changed, tell them to click the chip \"Changes: local files\" above " +
                                   "the input to choose again.");
                case Both:
                    return (true, $"The user wants the same change in the local files too (their choice in this chat): also apply it with edit_file to:\n{list}");
                default:
                    return (true, null);
            }
        }

        /// <summary>
        /// Before the agent edits a local file that contains PL/SQL units: if the user has not decided yet in this chat
        /// where changes go, the same question is asked (local edits themselves are never refused: the user approves them).
        /// </summary>
        public static async Task<(bool Proceed, string Message)> CheckFileAsync(ToolContext ctx, string fullPath)
        {
            if (!ctx.Config.HasWorkspace || ctx.ChangeTarget == null || ctx.ChangeTarget.Value != null || ctx.AskUser == null) return (true, null);
            List<LocalSources.Unit> units;
            try
            {
                var rel = Workspace.Relative(ctx, fullPath);
                units = LocalSources.All(ctx, out _).Where(u => string.Equals(u.Path, rel, StringComparison.OrdinalIgnoreCase) &&
                                                                 u.Type != "TABLE" && u.Type != "SEQUENCE" && u.Type != "SYNONYM").ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("Units of the edited file: " + ex.Message);
                return (true, null);
            }
            if (units.Count == 0) return (true, null);
            var (proceed, message) = await CheckAsync(ctx, units.Select(u => (u.Type, u.Owner ?? ctx.CurrentSchema, u.Name)).ToList()).ConfigureAwait(false);
            // the choice only limits database changes; the file edit itself is still shown for approval. Only a free answer stops it.
            if (ctx.ChangeTarget.Value != null)
                return (true, ctx.ChangeTarget.Value == Database
                    ? "The user chose the database for changes of objects that also have local files; edit local files only when the user wants that."
                    : null);
            return (proceed, message);
        }
    }

    internal sealed class FindObjectFilesTool : AgentTool
    {
        public override string Name => "find_object_files";
        public override string Description =>
            "Finds the local source files of database objects in the project folder, including all sub folders (e.g. one " +
            "folder per schema): which files contain an object (for a package: its specification and body files), in which " +
            "lines, and whether each file is identical to the version in the database. Without names: an overview of the " +
            "indexed files. Use it before changing an object, to know every local file that has to change.";
        public override JObject Parameters => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["names"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["description"] = "Objects, NAME or OWNER.NAME. Omit for an overview.",
                },
                ["name_like"] = new JObject { ["type"] = "string", ["description"] = "Name pattern with % and _, e.g. 'PKG_ORDER%' (no comparison with the database)." },
            },
        };
        public override bool NeedsWorkspace => true;
        public override string Summarize(JObject args) => Str(args, "name_like") ?? string.Join(", ", ExploreObjectsTool.Names(args).Take(5));

        private const int MaxCompared = 30;

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var all = LocalSources.All(ctx, out int files);
            var root = Workspace.Root(ctx);
            var names = ExploreObjectsTool.Names(args);
            var like = Str(args, "name_like");
            var sb = new StringBuilder();

            if (names.Count == 0 && string.IsNullOrWhiteSpace(like))
            {
                sb.Append($"Project folder {root}: {all.Count} database objects in {files} source files (all sub folders).\n");
                foreach (var g in all.GroupBy(u => u.Path.Contains('\\') ? u.Path.Substring(0, u.Path.IndexOf('\\')) : ".").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(100))
                    sb.Append($"  {g.Key}/: {g.Count()} objects ({string.Join(", ", g.GroupBy(u => u.Type).OrderBy(t => t.Key).Select(t => $"{t.Count()} {t.Key}"))})\n");
                sb.Append("Schemas come from the CREATE statements, else from folder names that are schema names. Call find_object_files with names for the files of certain objects.");
                return Task.FromResult(sb.ToString());
            }

            if (!string.IsNullOrWhiteSpace(like))
            {
                var re = new Regex("^" + Regex.Escape(like.Trim().ToUpperInvariant()).Replace("%", ".*").Replace("_", ".") + "$");
                var hits = all.Where(u => re.IsMatch(u.Name)).OrderBy(u => u.Name).ThenBy(u => u.Type).ThenBy(u => u.Path).ToList();
                if (hits.Count == 0) return Task.FromResult($"No local file contains an object named like {like} ({all.Count} objects in {files} files indexed).");
                foreach (var u in hits.Take(500))
                    sb.Append($"{u.Type} {(u.Owner ?? "?")}.{u.Name}: {u.Path.Replace('\\', '/')} lines {u.StartLine}-{u.EndLine}\n");
                if (hits.Count > 500) sb.Append($"... {hits.Count - 500} more: narrow the pattern.\n");
                return Task.FromResult(Db.Truncate(sb.ToString(), ctx.Config.ToolOutputLimit));
            }

            int compared = 0;
            bool connected = ctx.Ide.GetContext().Connected;
            foreach (var name in names.Take(50))
            {
                SplitName(name, out var owner, out var n);
                Db.ObjRef obj = null;
                if (connected)
                {
                    try { obj = Db.Resolve(ctx, name, null).FirstOrDefault(); }
                    catch (OracleSqlException) { }
                }
                var o = obj?.Owner ?? owner;
                var objName = obj?.Name ?? n;
                var type = obj?.Type;
                bool compare = connected && compared < MaxCompared;
                var lines = LocalSources.Describe(ctx, o, objName, type, compare);
                sb.Append(obj != null ? $"{obj.Type} {obj.Owner}.{obj.Name}" : $"{name} (not found in the database)").Append('\n');
                sb.Append(lines.Length > 0 ? lines : "  no local file\n");
                if (compare) compared += lines.Split('\n').Length - 1;
            }
            if (names.Count > 50) sb.Append($"Only the first 50 names were looked up.\n");
            if (compared >= MaxCompared) sb.Append($"Only the first {MaxCompared} files were compared with the database.\n");
            return Task.FromResult(Db.Truncate(sb.ToString(), ctx.Config.ToolOutputLimit));
        }
    }
}
