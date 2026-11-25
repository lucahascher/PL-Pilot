using System;
using System.Collections.Generic;
using System.Text;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Agent
{
    /// <summary>
    /// Setting "Use DBA views": queries read the DBA_ data dictionary views instead of the ALL_ views
    /// (ALL_TABLES -> DBA_TABLES …), as some DBA teams require.
    /// </summary>
    public static class DbaViews
    {
        /// <summary>
        /// ALL_ views whose DBA_ counterpart has the same columns. ALL_TAB_PRIVS and ALL_COL_PRIVS are missing on purpose:
        /// their DBA_ views have different columns.
        /// </summary>
        private static readonly HashSet<string> Views = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ALL_ALL_TABLES", "ALL_ARGUMENTS", "ALL_CATALOG", "ALL_COLL_TYPES", "ALL_COL_COMMENTS", "ALL_CONSTRAINTS",
            "ALL_CONS_COLUMNS", "ALL_DB_LINKS", "ALL_DEPENDENCIES", "ALL_DIRECTORIES", "ALL_ERRORS", "ALL_EXTERNAL_TABLES",
            "ALL_IDENTIFIERS", "ALL_INDEXES", "ALL_IND_COLUMNS", "ALL_IND_EXPRESSIONS", "ALL_IND_PARTITIONS", "ALL_IND_STATISTICS",
            "ALL_JOBS", "ALL_LOBS", "ALL_MVIEWS", "ALL_MVIEW_LOGS", "ALL_NESTED_TABLES", "ALL_OBJECTS", "ALL_OBJECT_TABLES",
            "ALL_PART_INDEXES", "ALL_PART_KEY_COLUMNS", "ALL_PART_TABLES", "ALL_PLSQL_OBJECT_SETTINGS", "ALL_POLICIES",
            "ALL_PROCEDURES", "ALL_QUEUES", "ALL_SCHEDULER_JOBS", "ALL_SCHEDULER_JOB_RUN_DETAILS", "ALL_SCHEDULER_PROGRAMS",
            "ALL_SCHEDULER_SCHEDULES", "ALL_SEQUENCES", "ALL_SOURCE", "ALL_STATEMENTS", "ALL_SUBPART_KEY_COLUMNS", "ALL_SYNONYMS",
            "ALL_TABLES", "ALL_TAB_COLS", "ALL_TAB_COLUMNS", "ALL_TAB_COL_STATISTICS", "ALL_TAB_COMMENTS", "ALL_TAB_MODIFICATIONS",
            "ALL_TAB_PARTITIONS", "ALL_TAB_STATISTICS", "ALL_TAB_SUBPARTITIONS", "ALL_TRIGGERS", "ALL_TRIGGER_COLS", "ALL_TYPES",
            "ALL_TYPE_ATTRS", "ALL_TYPE_METHODS", "ALL_UPDATABLE_COLUMNS", "ALL_USERS", "ALL_VIEWS",
        };

        /// <summary>Instruction for the model (system prompt) while the setting is on.</summary>
        public const string PromptRule =
            "- Company policy: query the DBA_ data dictionary views (DBA_OBJECTS, DBA_TABLES, DBA_TAB_COLUMNS, " +
            "DBA_DEPENDENCIES …) instead of the ALL_ views in every query you run. Filter them by OWNER, because unlike the ALL_ " +
            "views they list the objects of all schemas. Queries that use ALL_ views are rewritten to DBA_ automatically. " +
            "The tools (get_object_source, search_source …) follow this policy by themselves.";

        /// <summary>
        /// Replaces references to ALL_ dictionary views (also SYS.ALL_…) by the DBA_ views. String literals, quoted
        /// identifiers and comments (hints included) are not touched; the case of the text is kept.
        /// </summary>
        public static string Rewrite(string sql) => Rewrite(sql, null, null);

        /// <summary>
        /// <paramref name="skip"/>: DBA_ views not to use (not readable on this connection);
        /// <paramref name="used"/>: receives the DBA_ views the result refers to.
        /// </summary>
        public static string Rewrite(string sql, ICollection<string> skip, ICollection<string> used)
        {
            if (string.IsNullOrEmpty(sql) || sql.IndexOf("all_", StringComparison.OrdinalIgnoreCase) < 0) return sql;
            var sb = new StringBuilder(sql.Length);
            int i = 0, n = sql.Length;
            bool changed = false;
            while (i < n)
            {
                char c = sql[i];
                int end;
                if (c == '-' && i + 1 < n && sql[i + 1] == '-')
                {
                    end = sql.IndexOf('\n', i);
                    end = end < 0 ? n : end;
                }
                else if (c == '/' && i + 1 < n && sql[i + 1] == '*')
                {
                    end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 2;
                }
                else if (c == '\'')
                {
                    end = StringEnd(sql, i);
                }
                else if (c == '"')
                {
                    end = sql.IndexOf('"', i + 1);
                    end = end < 0 ? n : end + 1;
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    end = i + 1;
                    while (end < n && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_' || sql[end] == '$' || sql[end] == '#')) end++;
                    var word = sql.Substring(i, end - i);
                    if ((word.Equals("q", StringComparison.OrdinalIgnoreCase) || word.Equals("nq", StringComparison.OrdinalIgnoreCase)) && end < n && sql[end] == '\'')
                    {
                        end = QuoteEnd(sql, end); // q'[ ... ]'
                    }
                    else if (Views.Contains(word) && OwnerAllowsRewrite(sql, i))
                    {
                        var dba = ToDba(word);
                        if (skip == null || !skip.Contains(dba.ToUpperInvariant()))
                        {
                            used?.Add(dba.ToUpperInvariant());
                            sb.Append(dba);
                            changed = true;
                            i = end;
                            continue;
                        }
                    }
                }
                else
                {
                    end = i + 1;
                }
                sb.Append(sql, i, end - i);
                i = end;
            }
            return changed ? sb.ToString() : sql;
        }

        /// <summary>ALL_X -> DBA_X with the same upper/lower case.</summary>
        private static string ToDba(string word)
        {
            var chars = word.ToCharArray();
            chars[0] = char.IsLower(chars[0]) ? 'd' : 'D';
            chars[1] = char.IsLower(chars[1]) ? 'b' : 'B';
            chars[2] = char.IsLower(chars[2]) ? 'a' : 'A';
            return new string(chars);
        }

        /// <summary>Unqualified or SYS.ALL_…: rewrite; SCOTT.ALL_… is someone's own object.</summary>
        private static bool OwnerAllowsRewrite(string sql, int start)
        {
            int j = start - 1;
            while (j >= 0 && char.IsWhiteSpace(sql[j])) j--;
            if (j < 0 || sql[j] != '.') return true;
            j--;
            while (j >= 0 && char.IsWhiteSpace(sql[j])) j--;
            int ownerEnd = j + 1;
            while (j >= 0 && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_' || sql[j] == '"')) j--;
            var owner = sql.Substring(j + 1, ownerEnd - j - 1).Trim('"');
            return owner.Equals("SYS", StringComparison.OrdinalIgnoreCase);
        }

        private static int StringEnd(string sql, int quote)
        {
            int j = quote + 1;
            while (j < sql.Length)
            {
                if (sql[j] == '\'')
                {
                    if (j + 1 < sql.Length && sql[j + 1] == '\'') { j += 2; continue; }
                    return j + 1;
                }
                j++;
            }
            return sql.Length;
        }

        /// <summary>End of a q-quoted literal; <paramref name="quote"/> is the position of the first '.</summary>
        private static int QuoteEnd(string sql, int quote)
        {
            if (quote + 1 >= sql.Length) return sql.Length;
            char open = sql[quote + 1];
            char close = open == '[' ? ']' : open == '(' ? ')' : open == '{' ? '}' : open == '<' ? '>' : open;
            int end = sql.IndexOf(close + "'", quote + 2, StringComparison.Ordinal);
            return end < 0 ? sql.Length : end + 2;
        }
    }

    /// <summary>
    /// IIde decorator for the setting "Use DBA views": queries are rewritten to the DBA_ views. If the user cannot read
    /// a DBA_ view (no SELECT_CATALOG_ROLE, or the view does not exist in this Oracle version), the original query runs.
    /// Statements (Execute) are never changed: they may create views or code that must keep their text.
    /// </summary>
    internal sealed class DbaViewsIde : IdeDecorator
    {

        /// <summary>DBA_ views that failed per connection ("USER@DB" -> view names); not tried again.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, bool>> Unreadable =
            new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);

        public DbaViewsIde(IIde inner) : base(inner) { }

        /// <summary>ORA errors meaning "this DBA_ view cannot be used here" (missing, no privilege, other columns, EXPLAIN on it).</summary>
        public static bool IsFallbackError(int code) => code == 942 || code == 904 || code == 1031 || code == 1039;

        private System.Collections.Concurrent.ConcurrentDictionary<string, bool> UnreadableHere()
        {
            var c = Inner.GetContext();
            return Unreadable.GetOrAdd($"{c.User}@{c.Database}", _ => new System.Collections.Concurrent.ConcurrentDictionary<string, bool>());
        }

        /// <summary>The DBA_ version of a query (or the query itself), and the DBA_ views it uses.</summary>
        public string Rewrite(string sql, out List<string> used)
        {
            used = new List<string>();
            return DbaViews.Rewrite(sql, (ICollection<string>)UnreadableHere().Keys, used);
        }

        /// <summary>Remembers that these views did not work, so the next queries use the ALL_ views directly.</summary>
        public void MarkUnreadable(IEnumerable<string> views, int code, string sql)
        {
            var set = UnreadableHere();
            foreach (var v in views) set[v] = true;
            Log.Warn($"DBA views setting: ORA-{code:00000}, using the ALL_ views instead of {string.Join(", ", views)} on this connection " +
                     "(no SELECT_CATALOG_ROLE, or the view does not exist in this Oracle version). Statement: " + sql);
        }

        public override QueryResult Query(string sql, int maxRows)
        {
            var dba = Rewrite(sql, out var used);
            if (ReferenceEquals(dba, sql)) return Inner.Query(sql, maxRows);
            try
            {
                return Inner.Query(dba, maxRows);
            }
            catch (OracleSqlException ex) when (IsFallbackError(ex.Code))
            {
                MarkUnreadable(used, ex.Code, dba);
                return Inner.Query(sql, maxRows);
            }
        }
    }
}
