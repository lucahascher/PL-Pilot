using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// explore_objects: a large dossier of several objects and their neighbourhood in one call, so a model with a large
    /// context window does not have to explore step by step (every step is a slow round trip to the model).
    /// </summary>
    internal sealed class ExploreObjectsTool : AgentTool
    {
        public const string ToolName = "explore_objects";
        public override string Name => ToolName;
        public override string Description =>
            "Gets to know database objects in ONE call. Use it FIRST when a request involves objects you have not seen yet, " +
            "and pass ALL of them (and the ones you expect to need) together. For each object it returns a dossier: PL/SQL " +
            "units with their complete source (package specification and body), tables with columns and comments, keys, " +
            "indexes, triggers (with source) and their foreign-key neighbour tables, views with columns and query; plus what " +
            "the object uses (tables and views described compactly, specifications of the packages it calls) and which " +
            "objects use it, with the source lines that call or write it. The result is large and stays in the conversation: " +
            "work from it and do not read the same objects again with get_object_source or describe_object. The calling lines " +
            "are those that name the object; for the usages of one column use find_dependencies with column.";
        public override JObject Parameters => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["names"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["description"] = "Objects to explore, NAME or OWNER.NAME: tables, views, packages, procedures, functions, " +
                                      "triggers, types, sequences, synonyms. Several in one call.",
                },
                ["depth"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Foreign-key hops of the data model around tables: 1 (default) = direct neighbour tables, " +
                                      "2 or 3 = also their neighbours.",
                },
            },
            ["required"] = new JArray("names"),
        };

        public override int OutputLimit(AgentConfig cfg) => cfg.ExploreOutputLimit;

        public override string Summarize(JObject args)
        {
            var names = Names(args);
            int depth = Int(args, "depth", 1);
            return string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" … (+{names.Count - 5})" : "") + (depth > 1 ? $" (depth {depth})" : "");
        }

        /// <summary>The names from "names" (array, or a string with commas); some models send "name".</summary>
        internal static List<string> Names(JObject args)
        {
            var t = args?["names"] ?? args?["name"];
            IEnumerable<string> raw;
            if (t is JArray a) raw = a.Select(x => x.Type == JTokenType.String ? (string)x : x.ToString());
            else if (t == null || t.Type == JTokenType.Null) raw = Enumerable.Empty<string>();
            else raw = new[] { t.Type == JTokenType.String ? (string)t : t.ToString() };
            return raw.SelectMany(s => (s ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                      .Select(s => s.Trim()).Where(s => s.Length > 0)
                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public override Task<string> ExecuteAsync(JObject args, ToolContext ctx)
        {
            var names = Names(args);
            if (names.Count == 0) throw new ToolArgumentException("Missing required argument 'names' (array of object names).");
            var dossier = new Dossier(ctx, ctx.DossierLimit, Math.Max(1, Math.Min(Int(args, "depth", 1), 3)));
            foreach (var name in names.Take(Dossier.MaxRoots)) dossier.Add(name);
            if (names.Count > Dossier.MaxRoots) dossier.Note($"Only the first {Dossier.MaxRoots} names were explored.");
            return Task.FromResult(dossier.Build());
        }
    }

    /// <summary>
    /// Builds an object dossier: the objects themselves (full source or structure) and their neighbourhood (what they
    /// use, what uses them with the lines, foreign-key neighbours), within a character budget. The dictionary queries are
    /// batched over all objects, so their number hardly grows with the number of neighbours. Parts that do not fit are
    /// named at the end with the tool that reads them.
    /// </summary>
    internal sealed class Dossier
    {
        public const int MaxRoots = 30;
        /// <summary>Room for the header and the footer.</summary>
        private const int Reserve = 3000;
        /// <summary>Share of the budget the objects themselves may use; the rest is for their neighbourhood.</summary>
        private const double CoreShare = 0.6;
        private const int MaxUsedBy = 300, MaxRelatedTables = 150, MaxCallLinesPerUnit = 30, MaxCallRows = 4000, SpecChars = 15000;
        private const int Chunk = 100;

        private static readonly HashSet<string> SourceTypes = new HashSet<string>
        {
            "PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "TYPE BODY", "JAVA SOURCE",
        };
        private static readonly HashSet<string> TableTypes = new HashSet<string> { "TABLE", "VIEW", "MATERIALIZED VIEW" };

        /// <summary>Oracle's own packages every unit references: never listed.</summary>
        private const string IgnoredReferences =
            "not (d.referenced_owner = 'SYS' and d.referenced_name in ('STANDARD', 'DBMS_STANDARD', 'PLITBLM', 'SYS_STUB_FOR_PURITY_ANALYSIS'))";

        private const string ColumnType =
            "c.data_type || case " +
            "when c.data_type in ('VARCHAR2','NVARCHAR2','CHAR','NCHAR') then '(' || c.char_length || decode(c.char_used, 'C', ' CHAR', '') || ')' " +
            "when c.data_type = 'NUMBER' and c.data_precision is not null then '(' || c.data_precision || nullif(',' || c.data_scale, ',0') || ')' " +
            "when c.data_type = 'RAW' then '(' || c.data_length || ')' end";

        private sealed class Dep
        {
            public string Owner, Name, Type, Status, Link, Via;
            public string Key => $"{Owner}.{Name}|{Type}";
            public override string ToString() => $"{Type} {Owner}.{Name}";
        }

        private sealed class Root
        {
            public Db.ObjRef Obj;
            public string Requested, StatusLine;
            public readonly StringBuilder Core = new StringBuilder();
            public readonly List<Dep> Uses = new List<Dep>();
            public readonly List<Dep> UsedBy = new List<Dep>();
            public bool UsedByMore;
            /// <summary>The name and the synonyms that point to the object (what callers write).</summary>
            public readonly List<string> Names = new List<string>();
            public readonly StringBuilder Callers = new StringBuilder();
            /// <summary>Its local source files in the project folder (with the comparison to the database), or null.</summary>
            public string LocalFiles;
            public string Key => $"{Obj.Owner}.{Obj.Name}";
            public bool IsTable => TableTypes.Contains(Obj.Type) && Obj.Type != "VIEW";
        }

        private sealed class Related
        {
            public string Owner, Name, Type, Origin;
            public bool Described;
            public string Key => $"{Owner}.{Name}";
            public override string ToString() => $"{Type} {Owner}.{Name}";
        }

        private sealed class Constraint
        {
            public string Type, Name, Columns, ROwner, RTable;
        }

        private sealed class Incoming
        {
            public string Owner, Table, Name, Columns;
        }

        private sealed class TableInfo
        {
            public List<Constraint> Constraints;
            public List<Incoming> Incoming;
            public string Stats;
            public int Hop;
        }

        private readonly ToolContext _ctx;
        private readonly int _budget, _depth;
        private int _used, _queries;
        private readonly List<Root> _roots = new List<Root>();
        private readonly List<string> _notes = new List<string>();
        private readonly List<Related> _related = new List<Related>();
        private readonly Dictionary<string, Related> _relatedByKey = new Dictionary<string, Related>();
        private readonly Dictionary<string, TableInfo> _tables = new Dictionary<string, TableInfo>();
        private readonly List<string> _omitted = new List<string>();
        private readonly List<(string Owner, string Name, string Type, string Status)> _objectInfo = new List<(string, string, string, string)>();

        public Dossier(ToolContext ctx, int budget, int depth)
        {
            _ctx = ctx;
            _budget = Math.Max(4000, budget);
            _depth = Math.Max(1, Math.Min(depth, 3));
        }

        /// <summary>Number of objects found so far.</summary>
        public int Count => _roots.Count;

        public void Note(string text) => _notes.Add(text);

        // ------------------------------------------------------------------ roots

        private static readonly Regex TypePrefix = new Regex(
            @"^(PACKAGE\s+BODY|TYPE\s+BODY|PACKAGE|TYPE|TABLE|VIEW|MATERIALIZED\s+VIEW|PROCEDURE|FUNCTION|TRIGGER|SEQUENCE|SYNONYM)\s+(?<n>\S+)$",
            RegexOptions.IgnoreCase);

        /// <summary>Adds an object by name ("NAME", "OWNER.NAME", "PACKAGE.SUBPROGRAM", "TABLE NAME"); null if not found.</summary>
        public Db.ObjRef Add(string requested)
        {
            var name = requested.Trim().TrimEnd(';');
            string type = null;
            var m = TypePrefix.Match(name);
            if (m.Success)
            {
                type = Regex.Replace(m.Groups[1].Value.ToUpperInvariant(), @"\s+", " ").Replace(" BODY", "");
                name = m.Groups["n"].Value;
            }
            Db.ObjRef obj;
            try
            {
                obj = Pick(Db.Resolve(_ctx, name, type));
                if (obj == null && name.Contains('.'))
                {
                    // PACKAGE.SUBPROGRAM (or a type's method): the package
                    obj = Pick(Db.Resolve(_ctx, name.Split('.')[0], null));
                    if (obj != null && !SourceTypes.Contains(obj.Type)) obj = null;
                    if (obj != null) Note($"{requested}: the dossier shows {obj}.");
                }
                if (obj != null && obj.Type == "SYNONYM") obj = FollowSynonym(obj) ?? obj;
            }
            catch (OracleSqlException ex)
            {
                Note($"{requested}: could not be looked up ({FirstLine(ex.Message)}).");
                return null;
            }
            if (obj == null)
            {
                Note($"{requested}: no such object is visible to the current user (list_objects searches by pattern).");
                return null;
            }
            Add(obj, requested);
            return obj;
        }

        private void Add(Db.ObjRef obj, string requested)
        {
            if (obj.Type == "PACKAGE BODY" || obj.Type == "TYPE BODY")
                obj = new Db.ObjRef { Owner = obj.Owner, Name = obj.Name, Type = obj.Type.Replace(" BODY", ""), Status = obj.Status };
            if (_roots.Any(r => r.Obj.Owner == obj.Owner && r.Obj.Name == obj.Name && r.Obj.Type == obj.Type)) return;
            var root = new Root { Obj = obj, Requested = requested };
            root.Names.Add(obj.Name);
            _roots.Add(root);
        }

        private static int Rank(string type) => type == "SYNONYM" ? 1 : type == "INDEX" || type == "LOB" ? 2 : 0;

        /// <summary>The best match: the first owner of the list (current schema first), there tables/units before synonyms and indexes.</summary>
        private static Db.ObjRef Pick(List<Db.ObjRef> list)
        {
            if (list == null || list.Count == 0) return null;
            var owner = list[0].Owner;
            return list.Where(o => o.Owner == owner).OrderBy(o => Rank(o.Type)).First();
        }

        private Db.ObjRef FollowSynonym(Db.ObjRef syn)
        {
            var r = Q($"select table_owner, table_name, db_link from all_synonyms where owner = {Lit(syn.Owner)} and synonym_name = {Lit(syn.Name)}", 1);
            if (r.Rows.Count == 0 || string.IsNullOrEmpty(r.Rows[0][0])) return null;
            if (!string.IsNullOrEmpty(r.Rows[0][2]))
            {
                Note($"SYNONYM {syn.Owner}.{syn.Name} points to {r.Rows[0][0]}.{r.Rows[0][1]}@{r.Rows[0][2]} in another database: not explored.");
                return null;
            }
            var target = Pick(Db.Resolve(_ctx, r.Rows[0][0] + "." + r.Rows[0][1], null));
            if (target != null) Note($"SYNONYM {syn.Owner}.{syn.Name} points to {target}.");
            return target;
        }

        // ------------------------------------------------------------------ build

        public string Build()
        {
            var watch = Stopwatch.StartNew();
            if (_roots.Count == 0)
                return "No object found.\n" + string.Join("\n", _notes);

            // 1. what is known about the objects and their neighbourhood (small queries, nothing rendered yet)
            Step("object status", LoadObjectInfo);
            Step("synonyms", LoadSynonyms);
            Step("references", LoadUses);
            Step("users", LoadUsedBy);
            Step("foreign keys", LoadDataModel);
            Step("table statistics", () => LoadStats(_roots.Where(r => TableTypes.Contains(r.Obj.Type)).Select(r => (r.Obj.Owner, r.Obj.Name))
                .Concat(_related.Where(x => TableTypes.Contains(x.Type)).Take(MaxRelatedTables).Select(x => (x.Owner, x.Name)))));

            // the lists of references and incoming foreign keys are always shown
            _used += _roots.Sum(r => r.Uses.Count * 45 + (_tables.TryGetValue(r.Key, out var t) && t.Incoming != null ? t.Incoming.Count * 80 : 0));

            // 2. the objects themselves (at most CoreShare of the budget, in equal shares)
            int coreCap = (int)((_budget - Reserve) * CoreShare);
            int coreUsed = 0;
            for (int i = 0; i < _roots.Count; i++)
            {
                _ctx.Cancellation.ThrowIfCancellationRequested();
                int share = Math.Max(1500, (coreCap - coreUsed) / (_roots.Count - i));
                var root = _roots[i];
                try { RenderCore(root, share); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is OracleSqlException || ex is IdeException || ex is FormatException || ex is IndexOutOfRangeException)
                {
                    Log.Warn($"Dossier: {root.Obj} failed: {ex.Message}");
                    root.Core.Append($"(not available: {FirstLine(ex.Message)})\n");
                }
                coreUsed += root.Core.Length;
                _used += root.Core.Length;
            }

            // the local source files of the objects (project folder), compared with the database
            if (_ctx.Config.HasWorkspace) Step("local files", RenderLocalFiles);

            // 3. neighbourhood: related tables, then the calling lines, then the specifications of called packages
            var related = new StringBuilder();
            Step("related tables", () => RenderRelatedTables(related, Remaining / 2));
            Step("calling lines", () => RenderCallers(Remaining / 2));
            Step("related programs", () => RenderRelatedPrograms(related, Remaining));

            var text = Assemble(related);
            if (text.Length > _budget) text = Db.Truncate(text, _budget);
            Log.Info($"Dossier of {string.Join(", ", _roots.Select(r => r.Obj.ToString()))}: {text.Length} chars (limit {_budget}), " +
                     $"about {_queries} queries plus source reads, {watch.ElapsedMilliseconds} ms");
            return text;
        }

        private int Remaining => Math.Max(0, _budget - Reserve - _used);

        private QueryResult Q(string sql, int maxRows)
        {
            _queries++;
            return _ctx.Ide.Query(sql, maxRows);
        }

        /// <summary>Runs one part; a failing dictionary query only leaves a note (e.g. a view missing on an old version).</summary>
        private void Step(string what, Action action)
        {
            _ctx.Cancellation.ThrowIfCancellationRequested();
            try { action(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is OracleSqlException || ex is IdeException || ex is FormatException || ex is IndexOutOfRangeException ||
                                       ex is ToolArgumentException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                // a partial dossier is more useful than none
                Log.Warn($"Dossier: {what} failed: {ex.Message}");
                Note($"The {what} could not be read: {FirstLine(ex.Message)}");
            }
        }

        private static string FirstLine(string s) => (s ?? "").Split('\n')[0].Trim();

        private static IEnumerable<List<T>> Chunks<T>(IEnumerable<T> items)
        {
            var list = items.ToList();
            for (int i = 0; i < list.Count; i += Chunk) yield return list.Skip(i).Take(Chunk).ToList();
        }

        private static string Pairs(IEnumerable<(string Owner, string Name)> items) =>
            string.Join(", ", items.Select(x => $"({Lit(x.Owner)}, {Lit(x.Name)})"));

        // ------------------------------------------------------------------ loading

        private void LoadObjectInfo()
        {
            foreach (var chunk in Chunks(_roots.Select(r => (r.Obj.Owner, r.Obj.Name)).Distinct()))
            {
                var r = Q("select owner, object_name, object_type, status, to_char(last_ddl_time, 'YYYY-MM-DD HH24:MI') from all_objects " +
                          $"where (owner, object_name) in ({Pairs(chunk)}) order by owner, object_name, object_type", 500);
                foreach (var row in r.Rows) _objectInfo.Add((row[0], row[1], row[2], row[3]));
                foreach (var root in _roots)
                {
                    var own = r.Rows.Where(x => x[0] == root.Obj.Owner && x[1] == root.Obj.Name && (x[2] == root.Obj.Type || x[2] == root.Obj.Type + " BODY")).ToList();
                    if (own.Count == 0) continue;
                    var status = string.Join(", ", own.Select(x => own.Count > 1 ? $"{x[2]} {x[3]}" : x[3]));
                    var ddl = own.Select(x => x[4]).Where(x => !string.IsNullOrEmpty(x)).DefaultIfEmpty("").Max();
                    root.StatusLine = $"Status: {status}" + (ddl.Length > 0 ? $"; last DDL {ddl}" : "");
                }
            }
        }

        private bool HasBody(Root root) => _objectInfo.Count > 0
            ? _objectInfo.Any(x => x.Owner == root.Obj.Owner && x.Name == root.Obj.Name && x.Type == root.Obj.Type + " BODY")
            : Db.Resolve(_ctx, $"{root.Obj.Owner}.{root.Obj.Name}", root.Obj.Type + " BODY").Count > 0; // the status query failed

        private string StatusOf(Root root, string type) =>
            _objectInfo.Where(x => x.Owner == root.Obj.Owner && x.Name == root.Obj.Name && x.Type == type).Select(x => x.Status).FirstOrDefault();

        /// <summary>Synonyms of the roots: callers may write their names instead (public synonyms in legacy applications).</summary>
        private void LoadSynonyms()
        {
            foreach (var chunk in Chunks(_roots.Select(r => (r.Obj.Owner, r.Obj.Name)).Distinct()))
            {
                var r = Q($"select table_owner, table_name, owner, synonym_name from all_synonyms where (table_owner, table_name) in ({Pairs(chunk)}) " +
                          "and db_link is null order by table_owner, table_name, decode(owner, 'PUBLIC', 0, 1), owner, synonym_name", 500);
                foreach (var row in r.Rows)
                    foreach (var root in _roots.Where(x => x.Obj.Owner == row[0] && x.Obj.Name == row[1]))
                        if (!root.Names.Contains(row[3])) root.Names.Add(row[3]);
            }
        }

        /// <summary>What the PL/SQL units and views among the roots reference (specification and body).</summary>
        private void LoadUses()
        {
            var units = _roots.Where(r => !TableTypes.Contains(r.Obj.Type) || r.Obj.Type == "VIEW" || r.Obj.Type == "MATERIALIZED VIEW").ToList();
            foreach (var chunk in Chunks(units))
            {
                var tuples = string.Join(", ", chunk.SelectMany(r => new[] { r.Obj.Type, r.Obj.Type + " BODY" }
                    .Select(t => $"({Lit(r.Obj.Owner)}, {Lit(r.Obj.Name)}, {Lit(t)})")));
                var r = Q("select d.owner, d.name, d.referenced_type, d.referenced_owner, d.referenced_name, d.referenced_link_name " +
                          $"from all_dependencies d where (d.owner, d.name, d.type) in ({tuples}) and {IgnoredReferences} " +
                          "and not (d.referenced_owner = d.owner and d.referenced_name = d.name) " +
                          "group by d.owner, d.name, d.referenced_type, d.referenced_owner, d.referenced_name, d.referenced_link_name " +
                          "order by d.owner, d.name, d.referenced_type, d.referenced_owner, d.referenced_name", 5000);
                foreach (var row in r.Rows)
                {
                    foreach (var root in chunk.Where(x => x.Obj.Owner == row[0] && x.Obj.Name == row[1]))
                    {
                        var dep = new Dep { Type = row[2], Owner = row[3], Name = row[4], Link = row[5] };
                        if (root.Uses.Any(u => u.Key == dep.Key && u.Link == dep.Link)) continue;
                        root.Uses.Add(dep);
                        if (!string.IsNullOrEmpty(dep.Link)) continue;
                        // a type body is never referenced; a package body neither: their specifications are
                        AddRelated(dep.Owner, dep.Name, dep.Type, $"used by {root.Obj.Name}");
                    }
                }
            }
        }

        private void AddRelated(string owner, string name, string type, string origin)
        {
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(name)) return;
            if (type == "NON-EXISTENT" || type == "PACKAGE BODY" || type == "TYPE BODY") return;
            if (_roots.Any(r => r.Obj.Owner == owner && r.Obj.Name == name)) return;
            var key = $"{owner}.{name}";
            if (_relatedByKey.ContainsKey(key)) return;
            var rel = new Related { Owner = owner, Name = name, Type = type, Origin = origin };
            _relatedByKey[key] = rel;
            _related.Add(rel);
        }

        /// <summary>Objects that depend on each root (directly), also through synonyms of the root.</summary>
        private void LoadUsedBy()
        {
            foreach (var root in _roots)
            {
                _ctx.Cancellation.ThrowIfCancellationRequested();
                var types = root.Obj.Type == "MATERIALIZED VIEW" ? $"{Lit("MATERIALIZED VIEW")}, {Lit("TABLE")}" : Lit(root.Obj.Type);
                var r = Q("select distinct d.owner, d.name, d.type, o.status from all_dependencies d " +
                          "left join all_objects o on o.owner = d.owner and o.object_name = d.name and o.object_type = d.type " +
                          $"where d.referenced_owner = {Lit(root.Obj.Owner)} and d.referenced_name = {Lit(root.Obj.Name)} and d.referenced_type in ({types}) " +
                          $"and not (d.owner = {Lit(root.Obj.Owner)} and d.name = {Lit(root.Obj.Name)}) order by 1, 3, 2", MaxUsedBy + 1);
                foreach (var row in r.Rows.Take(MaxUsedBy))
                    root.UsedBy.Add(new Dep { Owner = row[0], Name = row[1], Type = row[2], Status = row[3] });
                root.UsedByMore = r.Rows.Count > MaxUsedBy || r.Truncated;

                // code that references a synonym of the root depends on the synonym
                var synonyms = root.UsedBy.Where(d => d.Type == "SYNONYM").ToList();
                foreach (var chunk in Chunks(synonyms))
                {
                    if (root.UsedBy.Count >= MaxUsedBy) { root.UsedByMore = true; break; }
                    var s = Q("select distinct d.referenced_owner, d.referenced_name, d.owner, d.name, d.type, o.status from all_dependencies d " +
                              "left join all_objects o on o.owner = d.owner and o.object_name = d.name and o.object_type = d.type " +
                              $"where (d.referenced_owner, d.referenced_name) in ({Pairs(chunk.Select(x => (x.Owner, x.Name)))}) and d.referenced_type = 'SYNONYM' " +
                              "order by 3, 5, 4", MaxUsedBy + 1);
                    foreach (var row in s.Rows)
                    {
                        var dep = new Dep { Owner = row[2], Name = row[3], Type = row[4], Status = row[5], Via = $"{row[0]}.{row[1]}" };
                        if (root.UsedBy.Any(d => d.Key == dep.Key)) continue;
                        if (root.UsedBy.Count >= MaxUsedBy) { root.UsedByMore = true; break; }
                        root.UsedBy.Add(dep);
                    }
                }
            }
        }

        private TableInfo Table(string owner, string name)
        {
            var key = $"{owner}.{name}";
            if (!_tables.TryGetValue(key, out var info)) _tables[key] = info = new TableInfo();
            return info;
        }

        /// <summary>Primary, unique and foreign keys of tables not loaded yet (one query per 100 tables).</summary>
        private void LoadConstraints(IEnumerable<(string Owner, string Name)> tables)
        {
            var todo = tables.Distinct().Where(t => Table(t.Owner, t.Name).Constraints == null).ToList();
            foreach (var chunk in Chunks(todo))
            {
                foreach (var t in chunk) Table(t.Owner, t.Name).Constraints = new List<Constraint>();
                var r = Q("select c.owner, c.table_name, c.constraint_type, c.constraint_name, " +
                          "(select listagg(cc.column_name, ', ') within group (order by cc.position) from all_cons_columns cc " +
                          "  where cc.owner = c.owner and cc.constraint_name = c.constraint_name and cc.table_name = c.table_name) cols, " +
                          "r.owner r_owner, r.table_name r_table " +
                          "from all_constraints c left join all_constraints r on r.owner = c.r_owner and r.constraint_name = c.r_constraint_name " +
                          $"where (c.owner, c.table_name) in ({Pairs(chunk)}) and c.constraint_type in ('P','U','R') " +
                          "order by c.owner, c.table_name, decode(c.constraint_type, 'P', 0, 'U', 1, 2), c.constraint_name", 20000);
                foreach (var row in r.Rows)
                    Table(row[0], row[1]).Constraints.Add(new Constraint { Type = row[2], Name = row[3], Columns = row[4], ROwner = row[5], RTable = row[6] });
            }
        }

        /// <summary>Foreign keys of other tables that reference these tables.</summary>
        private void LoadIncoming(IEnumerable<(string Owner, string Name)> tables)
        {
            var todo = tables.Distinct().Where(t => Table(t.Owner, t.Name).Incoming == null).ToList();
            foreach (var chunk in Chunks(todo))
            {
                foreach (var t in chunk) Table(t.Owner, t.Name).Incoming = new List<Incoming>();
                var r = Q("select p.owner, p.table_name, c.owner, c.table_name, c.constraint_name, " +
                          "(select listagg(cc.column_name, ', ') within group (order by cc.position) from all_cons_columns cc " +
                          "  where cc.owner = c.owner and cc.constraint_name = c.constraint_name and cc.table_name = c.table_name) cols " +
                          "from all_constraints p join all_constraints c on c.r_owner = p.owner and c.r_constraint_name = p.constraint_name and c.constraint_type = 'R' " +
                          $"where (p.owner, p.table_name) in ({Pairs(chunk)}) and p.constraint_type in ('P','U') " +
                          "order by p.owner, p.table_name, c.owner, c.table_name, c.constraint_name", 5000);
                foreach (var row in r.Rows)
                    Table(row[0], row[1]).Incoming.Add(new Incoming { Owner = row[2], Table = row[3], Name = row[4], Columns = row[5] });
            }
        }

        /// <summary>The data model around the table roots: foreign keys in both directions, <see cref="_depth"/> hops.</summary>
        private void LoadDataModel()
        {
            var frontier = _roots.Where(r => r.IsTable).Select(r => (r.Obj.Owner, r.Obj.Name)).Distinct().ToList();
            var seen = new HashSet<string>(frontier.Select(t => $"{t.Owner}.{t.Name}"));
            for (int hop = 1; hop <= _depth && frontier.Count > 0; hop++)
            {
                LoadConstraints(frontier);
                LoadIncoming(frontier);
                var next = new List<(string Owner, string Name)>();
                foreach (var t in frontier)
                {
                    var info = Table(t.Owner, t.Name);
                    var neighbours = info.Constraints.Where(c => c.Type == "R" && !string.IsNullOrEmpty(c.RTable)).Select(c => (c.ROwner, c.RTable))
                        .Concat(info.Incoming.Select(i => (i.Owner, i.Table)));
                    foreach (var n in neighbours)
                    {
                        if (!seen.Add($"{n.Item1}.{n.Item2}")) continue;
                        if (_related.Count(x => TableTypes.Contains(x.Type)) >= MaxRelatedTables)
                        {
                            _omitted.Add($"TABLE {n.Item1}.{n.Item2} (foreign-key neighbour, hop {hop})");
                            continue;
                        }
                        AddRelated(n.Item1, n.Item2, "TABLE", $"foreign-key neighbour of {t.Name}" + (hop > 1 ? $" (hop {hop})" : ""));
                        Table(n.Item1, n.Item2).Hop = hop;
                        next.Add(n);
                    }
                }
                frontier = next;
            }
        }

        private void LoadStats(IEnumerable<(string Owner, string Name)> tables)
        {
            foreach (var chunk in Chunks(tables.Distinct()))
            {
                var r = Q("select owner, table_name, num_rows, to_char(last_analyzed, 'YYYY-MM-DD'), partitioned, temporary from all_tables " +
                          $"where (owner, table_name) in ({Pairs(chunk)})", 1000);
                foreach (var row in r.Rows)
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(row[2]) && long.TryParse(row[2], out var rows))
                        parts.Add($"~{rows.ToString("N0", CultureInfo.InvariantCulture)} rows" + (string.IsNullOrEmpty(row[3]) ? "" : $" (statistics of {row[3]})"));
                    else parts.Add("no statistics");
                    if (row[4] == "YES") parts.Add("partitioned");
                    if (row[5] == "Y") parts.Add("global temporary");
                    Table(row[0], row[1]).Stats = string.Join(", ", parts);
                }
            }
        }

        // ------------------------------------------------------------------ rendering

        /// <summary>The object itself: complete source, or the structure of a table / view.</summary>
        private void RenderCore(Root root, int share)
        {
            var obj = root.Obj;
            var sb = root.Core;
            if (SourceTypes.Contains(obj.Type))
            {
                int budget = share;
                GetObjectSourceTool.AppendSource(_ctx, obj, obj.Type, false, 1, 0, ref budget, sb);
                if ((obj.Type == "PACKAGE" || obj.Type == "TYPE") && HasBody(root))
                {
                    sb.Append("/\n\n");
                    GetObjectSourceTool.AppendSource(_ctx, obj, obj.Type + " BODY", false, 1, 0, ref budget, sb);
                }
                foreach (var type in new[] { obj.Type, obj.Type + " BODY" })
                {
                    if (StatusOf(root, type) != "INVALID") continue;
                    var diag = Db.Diagnostics(_ctx, obj.Owner, obj.Name, type);
                    sb.Append(diag.Errors != null ? $"\nCompile errors of the {type}:\n{diag.Errors}" : $"\nThe {type} is INVALID (no errors recorded: an object it uses changed).\n");
                }
                return;
            }

            var part = new StringBuilder();
            switch (obj.Type)
            {
                case "TABLE":
                case "MATERIALIZED VIEW":
                case "VIEW":
                    DescribeObjectTool.DescribeTable(_ctx, obj, part, false);
                    if (obj.Type != "TABLE") part.Append($"\nQuery of the {obj.Type.ToLowerInvariant()}:\n").Append(ViewSource(obj)).Append('\n');
                    break;
                case "SEQUENCE":
                    part.Append(FormatResult(Q("select min_value, max_value, increment_by, cache_size, cycle_flag, order_flag, last_number from all_sequences " +
                                               $"where sequence_owner = {Lit(obj.Owner)} and sequence_name = {Lit(obj.Name)}", 1), 4000)).Append('\n');
                    break;
                default:
                    var args = new JObject { ["name"] = $"{obj.Owner}.{obj.Name}", ["object_type"] = obj.Type };
                    part.Append(new GetObjectSourceTool().ExecuteAsync(args, _ctx).GetAwaiter().GetResult()).Append('\n');
                    break;
            }
            sb.Append(Cut(part.ToString(), share, $"describe_object '{obj.Owner}.{obj.Name}'"));
            if (root.IsTable) RenderTriggers(root, share - sb.Length);
        }

        private string ViewSource(Db.ObjRef obj)
        {
            string ddl = obj.Type == "VIEW" ? _ctx.Ide.GetObjectSource(obj.Type, obj.Owner, obj.Name) : "";
            if (!string.IsNullOrWhiteSpace(ddl)) return ddl.Trim();
            try { return Db.ReadClob(_ctx, $"dbms_metadata.get_ddl({Lit(obj.Type.Replace(' ', '_'))}, {Lit(obj.Name)}, {Lit(obj.Owner)})").Trim(); }
            catch (OracleSqlException ex) { return $"(DBMS_METADATA failed: {FirstLine(ex.Message)}; get_object_source shows it)"; }
        }

        /// <summary>The triggers of a table root, with their source (they are part of what happens on DML).</summary>
        private void RenderTriggers(Root root, int budget)
        {
            var r = Q("select owner, trigger_name, trigger_type, triggering_event, status from all_triggers " +
                      $"where table_owner = {Lit(root.Obj.Owner)} and table_name = {Lit(root.Obj.Name)} order by trigger_name", 200);
            if (r.Rows.Count == 0) return;
            var sb = root.Core;
            sb.Append($"\nTriggers on {root.Obj.Name}:\n");
            foreach (var row in r.Rows)
            {
                sb.Append($"-- TRIGGER {row[0]}.{row[1]} ({row[2]}, {row[3].Trim()}, {row[4]})\n");
                if (budget < 800)
                {
                    _omitted.Add($"TRIGGER {row[0]}.{row[1]} (source)");
                    continue;
                }
                var src = Db.ReadSource(_ctx, row[0], row[1], "TRIGGER", 1, 0, budget);
                var text = Cut(src.Text.Replace("\r\n", "\n").TrimEnd() + "\n", budget, $"get_object_source '{row[0]}.{row[1]}'");
                sb.Append(text);
                budget -= text.Length;
            }
        }

        private void RenderLocalFiles()
        {
            foreach (var root in _roots)
            {
                _ctx.Cancellation.ThrowIfCancellationRequested();
                var text = LocalSources.Describe(_ctx, root.Obj.Owner, root.Obj.Name, root.Obj.Type, compare: true);
                root.LocalFiles = text.Length > 0 ? text : "  none in the project folder\n";
                _used += root.LocalFiles.Length;
            }
        }

        /// <summary>Text cut at a line end to <paramref name="max"/> characters, with a note how to read the rest.</summary>
        private static string Cut(string text, int max, string readWith)
        {
            if (text.Length <= max) return text;
            int end = text.LastIndexOf('\n', Math.Max(0, max - 200));
            if (end < 0) end = Math.Max(0, max - 200);
            return text.Substring(0, end) + $"\n-- [cut here to fit the output limit: {text.Length - end} more characters; read the rest with {readWith}]\n";
        }

        /// <summary>Compact descriptions of the related tables and views: columns with comments, keys, size.</summary>
        private void RenderRelatedTables(StringBuilder sb, int budget)
        {
            var tables = _related.Where(r => TableTypes.Contains(r.Type) && !Db.SystemOwners.Contains(r.Owner)).Take(MaxRelatedTables).ToList();
            if (tables.Count == 0) return;
            LoadConstraints(tables.Where(t => t.Type != "VIEW").Select(t => (t.Owner, t.Name)));

            var columns = new Dictionary<string, List<string[]>>();
            var comments = new Dictionary<string, string>();
            foreach (var chunk in Chunks(tables.Select(t => (t.Owner, t.Name))))
            {
                var r = Q($"select c.owner, c.table_name, c.column_name, {ColumnType} data_type, c.nullable, substr(cc.comments, 1, 120) " +
                          "from all_tab_columns c left join all_col_comments cc on cc.owner = c.owner and cc.table_name = c.table_name and cc.column_name = c.column_name " +
                          $"where (c.owner, c.table_name) in ({Pairs(chunk)}) order by c.owner, c.table_name, c.column_id", 50000);
                foreach (var row in r.Rows)
                {
                    var key = $"{row[0]}.{row[1]}";
                    if (!columns.TryGetValue(key, out var list)) columns[key] = list = new List<string[]>();
                    list.Add(row);
                }
                var tc = Q($"select owner, table_name, substr(comments, 1, 300) from all_tab_comments where (owner, table_name) in ({Pairs(chunk)}) and comments is not null", 1000);
                foreach (var row in tc.Rows) comments[$"{row[0]}.{row[1]}"] = row[2];
            }

            int start = _used;
            foreach (var t in tables)
            {
                var block = new StringBuilder($"\n### {t} – {t.Origin}");
                var info = Table(t.Owner, t.Name);
                if (!string.IsNullOrEmpty(info.Stats)) block.Append($"; {info.Stats}");
                block.Append('\n');
                if (comments.TryGetValue(t.Key, out var comment)) block.Append($"Comment: {OneLine(comment)}\n");
                if (columns.TryGetValue(t.Key, out var cols))
                {
                    foreach (var c in cols)
                    {
                        block.Append($"  {c[2]} {c[3]}{(c[4] == "N" ? " NOT NULL" : "")}");
                        if (!string.IsNullOrWhiteSpace(c[5])) block.Append($"  -- {OneLine(c[5])}");
                        block.Append('\n');
                    }
                }
                else block.Append("  (columns not visible)\n");
                if (info.Constraints != null && info.Constraints.Count > 0)
                    block.Append("  ").Append(string.Join("; ", info.Constraints.Select(Describe))).Append('\n');

                if (_used - start + block.Length > budget)
                {
                    _omitted.Add($"{t} (describe_object)");
                    continue;
                }
                sb.Append(block);
                _used += block.Length;
                t.Described = true;
            }
        }

        private static string Describe(Constraint c) =>
            c.Type == "P" ? $"PK ({c.Columns})"
            : c.Type == "U" ? $"UK {c.Name} ({c.Columns})"
            : $"FK ({c.Columns}) → {c.ROwner}.{c.RTable}";

        private static string OneLine(string s)
        {
            s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            return s.Length > 120 ? s.Substring(0, 117) + "..." : s;
        }

        /// <summary>The source lines of the users of each root that name the root (or one of its synonyms).</summary>
        private void RenderCallers(int budget)
        {
            var units = _roots.SelectMany(r => r.UsedBy).Where(d => SourceTypes.Contains(d.Type))
                .GroupBy(d => d.Key).Select(g => g.First()).ToList();
            var lines = new Dictionary<string, List<(int Line, string Text)>>();
            var names = _roots.SelectMany(r => r.Names).Distinct().ToList();
            bool more = false;
            if (units.Count > 0 && names.Count > 0)
            {
                // INSTR finds the candidates on the server (no regular expression length limit), the word boundaries are checked here
                var filter = string.Join(" or ", names.Select(n => $"instr(upper(s.text), {Lit(n.ToUpperInvariant())}) > 0"));
                foreach (var chunk in Chunks(units))
                {
                    var tuples = string.Join(", ", chunk.Select(d => $"({Lit(d.Owner)}, {Lit(d.Name)}, {Lit(d.Type)})"));
                    var r = Q($"select s.owner, s.name, s.type, s.line, s.text from all_source s where (s.owner, s.name, s.type) in ({tuples}) " +
                              $"and ({filter}) order by s.owner, s.name, s.type, s.line", MaxCallRows);
                    more |= r.Truncated;
                    foreach (var row in r.Rows)
                    {
                        var key = $"{row[0]}.{row[1]}|{row[2]}";
                        if (!lines.TryGetValue(key, out var list)) lines[key] = list = new List<(int, string)>();
                        list.Add((int.Parse(row[3]), (row[4] ?? "").TrimEnd('\r', '\n')));
                    }
                }
            }

            int used = 0;
            foreach (var root in _roots)
            {
                if (root.UsedBy.Count == 0) continue;
                var word = new Regex(@"(?<![\w$#])(" + string.Join("|", root.Names.Select(Regex.Escape)) + @")(?![\w$#])", RegexOptions.IgnoreCase);
                var writes = TableTypes.Contains(root.Obj.Type)
                    ? new Regex(@"\b(INSERT\s+INTO|UPDATE|DELETE\s+FROM|DELETE|MERGE\s+INTO)\s+((""[^""]+""|[\w$#]+)\s*\.\s*)?""?(" +
                                string.Join("|", root.Names.Select(Regex.Escape)) + @")""?(?![\w$#])", RegexOptions.IgnoreCase)
                    : null;
                var without = new List<string>();
                var notShown = new List<string>();
                var sb = root.Callers;
                foreach (var dep in root.UsedBy)
                {
                    var label = dep + (dep.Via != null ? $" (via synonym {dep.Via})" : "") + (dep.Status == "INVALID" ? " INVALID" : "");
                    if (!SourceTypes.Contains(dep.Type))
                    {
                        if (dep.Type != "SYNONYM") without.Add(label);
                        continue;
                    }
                    var matches = lines.TryGetValue(dep.Key, out var all) ? all.Where(l => word.IsMatch(l.Text)).ToList() : new List<(int Line, string Text)>();
                    if (matches.Count == 0)
                    {
                        without.Add(label);
                        continue;
                    }
                    var block = new StringBuilder(label).Append('\n');
                    foreach (var (line, text) in matches.Take(MaxCallLinesPerUnit))
                    {
                        var t = text.Trim();
                        if (t.Length > 300) t = t.Substring(0, 300) + "…";
                        block.Append($"  line {line}{(writes != null && writes.IsMatch(text) ? " (writes)" : "")}: {t}\n");
                    }
                    if (matches.Count > MaxCallLinesPerUnit) block.Append($"  … {matches.Count - MaxCallLinesPerUnit} more lines (search_source)\n");
                    if (used + block.Length > budget)
                    {
                        notShown.Add(label);
                        continue;
                    }
                    sb.Append(block);
                    used += block.Length;
                }
                int listStart = sb.Length;
                if (without.Count > 0)
                    sb.Append("Without a line that names it (views, SELECT *, %ROWTYPE, other synonyms): ").Append(string.Join(", ", without)).Append('\n');
                if (notShown.Count > 0)
                {
                    sb.Append($"Lines not shown (output limit) for: {string.Join(", ", notShown.Take(100))}{(notShown.Count > 100 ? $" and {notShown.Count - 100} more" : "")}" +
                              $" – search_source '{root.Obj.Name}' finds them.\n");
                }
                if (root.UsedByMore) sb.Append($"More than {MaxUsedBy} objects use it: find_dependencies '{root.Obj.Owner}.{root.Obj.Name}' lists them.\n");
                if (TableTypes.Contains(root.Obj.Type))
                    sb.Append($"These are the lines that name {root.Obj.Name}. Which of them use a certain column – also in views and on other lines of " +
                              "the same statement – shows find_dependencies with column.\n");
                if (more) sb.Append("Not all matching lines were read (too many): search_source finds the others.\n");
                used += sb.Length - listStart;
            }
            _used += used;
        }

        /// <summary>Specifications of the packages and types the roots use; signatures of procedures and functions.</summary>
        private void RenderRelatedPrograms(StringBuilder sb, int budget)
        {
            var programs = _related.Where(r => r.Type == "PACKAGE" || r.Type == "TYPE" || r.Type == "PROCEDURE" || r.Type == "FUNCTION").ToList();
            int used = 0;
            foreach (var p in programs)
            {
                _ctx.Cancellation.ThrowIfCancellationRequested();
                if (Db.SystemOwners.Contains(p.Owner)) continue; // DBMS_… and friends: the model knows them
                int left = budget - used;
                var block = new StringBuilder($"\n### {p} – {p.Origin}\n");
                if ((p.Type == "PACKAGE" || p.Type == "TYPE") && left >= 3000)
                {
                    var src = Db.ReadSource(_ctx, p.Owner, p.Name, p.Type, 1, 0, Math.Min(SpecChars, left - 300));
                    if (src.FirstLine == 0) block.Append("(no source visible)\n");
                    else block.Append(Cut(src.Text.Replace("\r\n", "\n").TrimEnd() + "\n", Math.Min(SpecChars, left - 300),
                        $"get_object_source '{p.Owner}.{p.Name}' object_type '{p.Type}'"));
                    if (!src.Complete && src.Text.Length < Math.Min(SpecChars, left - 300))
                        block.Append($"-- [specification cut: get_object_source '{p.Owner}.{p.Name}' object_type '{p.Type}' shows all of it]\n");
                }
                else if (left >= 600)
                {
                    var sig = new StringBuilder();
                    DescribeObjectTool.DescribeProgram(_ctx, new Db.ObjRef { Owner = p.Owner, Name = p.Name, Type = p.Type }, sig);
                    block.Append(Cut(sig.ToString().TrimEnd() + "\n", Math.Max(300, left - 200), $"describe_object '{p.Owner}.{p.Name}'"));
                }
                if (used + block.Length > budget || left < 600)
                {
                    _omitted.Add($"{p} (get_object_source)");
                    continue;
                }
                sb.Append(block);
                used += block.Length;
                p.Described = true;
            }
            _used += used;
        }

        private string Marker(string owner, string name)
        {
            if (_roots.Any(r => r.Obj.Owner == owner && r.Obj.Name == name)) return " (in this dossier)";
            return _relatedByKey.TryGetValue($"{owner}.{name}", out var rel) && rel.Described ? " (described below)" : "";
        }

        private string Assemble(StringBuilder related)
        {
            var sb = new StringBuilder();
            sb.Append($"# Dossier: {string.Join(", ", _roots.Select(r => r.Obj.ToString()))}\n");
            sb.Append("From the data dictionary in one call. Per object: its source or structure, what it uses, what uses it (with the source " +
                      "lines that name it) and, for tables, the foreign-key neighbours. Then compact descriptions of related objects. " +
                      "Line numbers in \"Used by\" are those of the calling unit.\n");
            foreach (var n in _notes) sb.Append($"Note: {n}\n");

            foreach (var root in _roots)
            {
                var o = root.Obj;
                sb.Append($"\n## {o.Type} {o.Owner}.{o.Name}\n");
                if (root.StatusLine != null) sb.Append(root.StatusLine).Append('\n');
                if (TableTypes.Contains(o.Type) && _tables.TryGetValue(root.Key, out var info) && !string.IsNullOrEmpty(info.Stats))
                    sb.Append($"Size: {info.Stats}\n");
                if (root.Names.Count > 1) sb.Append($"Synonyms: {string.Join(", ", root.Names.Skip(1))}\n");
                if (root.LocalFiles != null) sb.Append("Local files (project folder):\n").Append(root.LocalFiles);
                sb.Append(SourceTypes.Contains(o.Type) ? "\n### Source\n" : "\n### Structure\n");
                sb.Append(root.Core.ToString().TrimEnd()).Append('\n');

                if (root.Uses.Count > 0)
                {
                    sb.Append($"\n### {o.Name} uses\n");
                    foreach (var group in root.Uses.GroupBy(u => u.Type).OrderBy(g => g.Key))
                        sb.Append($"{group.Key}: ").Append(string.Join(", ", group.Select(u =>
                            $"{u.Owner}.{u.Name}{(string.IsNullOrEmpty(u.Link) ? Marker(u.Owner, u.Name) : "@" + u.Link)}"))).Append('\n');
                    sb.Append("Dynamic SQL (EXECUTE IMMEDIATE, DBMS_SQL) is not covered by these references.\n");
                }

                if (TableTypes.Contains(o.Type) && _tables.TryGetValue(root.Key, out var t) && t.Incoming != null && t.Incoming.Count > 0)
                {
                    sb.Append($"\n### Referenced by (foreign keys of other tables to {o.Name})\n");
                    foreach (var fk in t.Incoming)
                        sb.Append($"{fk.Owner}.{fk.Table} ({fk.Columns}) → {o.Name}  [{fk.Name}]{Marker(fk.Owner, fk.Table)}\n");
                }

                sb.Append($"\n### Used by");
                if (root.UsedBy.Count == 0) sb.Append($"\nNo objects visible to the current user depend on {o.Name}.\n");
                else sb.Append($" ({root.UsedBy.Count}{(root.UsedByMore ? "+" : "")} object{(root.UsedBy.Count == 1 && !root.UsedByMore ? "" : "s")})\n").Append(root.Callers);
            }

            if (_depth > 1)
            {
                var hops = _related.Where(r => r.Type == "TABLE" && _tables.TryGetValue(r.Key, out var i) && i.Hop > 1).ToList();
                if (hops.Count > 0)
                    sb.Append($"\n## Data model, further hops\n{string.Join(", ", hops.Select(h => $"{h.Owner}.{h.Name} (hop {Table(h.Owner, h.Name).Hop})"))}\n");
            }

            if (related.Length > 0) sb.Append("\n## Related objects (compact)\n").Append(related.ToString().TrimStart('\n')).Append('\n');

            var names = _related.Where(r => !r.Described && !Db.SystemOwners.Contains(r.Owner) &&
                                            (TableTypes.Contains(r.Type) || r.Type == "PACKAGE" || r.Type == "TYPE" || r.Type == "PROCEDURE" || r.Type == "FUNCTION"))
                .Select(r => r.ToString())
                .Concat(_omitted).Distinct().ToList();
            if (names.Count > 0)
            {
                sb.Append($"\n## Not described here (output limit or only referenced)\n");
                sb.Append(string.Join(", ", names.Take(200))).Append(names.Count > 200 ? $" and {names.Count - 200} more" : "").Append('\n');
                sb.Append("Read them with describe_object / get_object_source, or explore_objects with fewer names.\n");
            }
            return sb.ToString();
        }
    }
}
