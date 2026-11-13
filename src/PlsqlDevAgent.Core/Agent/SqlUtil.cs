using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PlsqlDevAgent.Ide;

namespace PlsqlDevAgent.Agent
{
    internal static class SqlUtil
    {
        private static readonly Regex PlsqlUnitRegex = new Regex(
            @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:NON)?EDITIONABLE\s+)?(?:EDITIONING\s+)?" +
            @"(?<type>PACKAGE\s+BODY|PACKAGE|PROCEDURE|FUNCTION|TRIGGER|TYPE\s+BODY|TYPE|JAVA\s+SOURCE)\s+" +
            @"(?:(?<owner>""[^""]+""|[A-Za-z][\w$#]*)\s*\.\s*)?(?<name>""[^""]+""|[A-Za-z][\w$#]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AnyDdlRegex = new Regex(
            @"^\s*(?:CREATE|ALTER|DROP|TRUNCATE|RENAME|GRANT|REVOKE|COMMENT)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Removes leading comments and whitespace.</summary>
        public static string StripLeadingComments(string sql)
        {
            var s = sql ?? "";
            while (true)
            {
                s = s.TrimStart();
                if (s.StartsWith("--"))
                {
                    int nl = s.IndexOf('\n');
                    s = nl < 0 ? "" : s.Substring(nl + 1);
                }
                else if (s.StartsWith("/*"))
                {
                    int end = s.IndexOf("*/", 2, StringComparison.Ordinal);
                    s = end < 0 ? "" : s.Substring(end + 2);
                }
                else return s;
            }
        }

        public static string FirstKeyword(string sql)
        {
            var m = Regex.Match(StripLeadingComments(sql), @"^[A-Za-z]+");
            return m.Success ? m.Value.ToUpperInvariant() : "";
        }

        public static bool IsReadOnlyQuery(string sql, out string reason)
        {
            reason = null;
            var kw = FirstKeyword(sql);
            if (kw != "SELECT" && kw != "WITH")
            {
                reason = "run_query only accepts SELECT / WITH queries. Use execute_sql for anything else.";
                return false;
            }
            var stripped = StripStringsAndComments(sql);
            if (Regex.IsMatch(stripped, @"\bFOR\s+UPDATE\b", RegexOptions.IgnoreCase))
            {
                reason = "SELECT ... FOR UPDATE locks rows; it is not allowed in run_query.";
                return false;
            }
            if (WithPlsql.IsMatch(stripped))
            {
                // WITH FUNCTION ... PRAGMA AUTONOMOUS_TRANSACTION can insert, update and commit inside a "query"
                reason = "PL/SQL in the WITH clause (WITH FUNCTION / WITH PROCEDURE) can change data, so it is not allowed in run_query. " +
                         "Use execute_sql instead; the user has to approve it.";
                return false;
            }
            if (Regex.IsMatch(stripped, @";\s*\S"))
            {
                reason = "Only a single statement is allowed.";
                return false;
            }
            return true;
        }

        public static bool IsPlsqlUnit(string sql, out string type, out string owner, out string name)
        {
            var m = PlsqlUnitRegex.Match(StripLeadingComments(sql));
            type = owner = name = null;
            if (!m.Success) return false;
            type = Regex.Replace(m.Groups["type"].Value.ToUpperInvariant(), @"\s+", " ");
            owner = m.Groups["owner"].Success ? Ident(m.Groups["owner"].Value) : null;
            name = Ident(m.Groups["name"].Value);
            return true;
        }

        public static bool IsDdl(string sql) => AnyDdlRegex.IsMatch(StripLeadingComments(sql));

        public static bool IsPlsqlBlock(string sql)
        {
            var kw = FirstKeyword(sql);
            return kw == "BEGIN" || kw == "DECLARE" || IsPlsqlUnit(sql, out _, out _, out _);
        }

        /// <summary>
        /// Prepares a statement for OCI: removes a trailing "/" line and, for plain SQL,
        /// a trailing semicolon (PL/SQL keeps its final "END;").
        /// </summary>
        public static string PrepareStatement(string sql)
        {
            var s = (sql ?? "").Replace("\r\n", "\n").Trim();
            s = Regex.Replace(s, @"\n\s*/\s*$", "").TrimEnd();
            if (s == "/") s = "";
            if (!IsPlsqlBlock(s))
            {
                while (s.EndsWith(";")) s = s.Substring(0, s.Length - 1).TrimEnd();
            }
            return s.Replace("\n", "\r\n");
        }

        /// <summary>Splits a SQL*Plus style script on lines that contain only "/".</summary>
        public static List<string> SplitOnSlash(string script)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            foreach (var line in (script ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Trim() == "/")
                {
                    if (current.ToString().Trim().Length > 0) parts.Add(current.ToString());
                    current.Clear();
                }
                else current.Append(line).Append('\n');
            }
            if (current.ToString().Trim().Length > 0) parts.Add(current.ToString());
            return parts;
        }

        /// <summary>
        /// Decodes the output of Oracle's ASCIISTR() (\XXXX = UTF-16 code unit, \005C = backslash).
        /// Used for server generated texts (ALL_ERRORS) whose non-ASCII characters PL/SQL Developer's
        /// plug-in SQL interface does not convert correctly.
        /// </summary>
        public static string UnAsciiStr(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            return Regex.Replace(s, @"\\([0-9A-Fa-f]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        }

        /// <summary>Oracle string literal.</summary>
        public static string Lit(string s) => "'" + (s ?? "").Replace("'", "''") + "'";

        /// <summary>Normalizes an identifier: "Quoted" stays case sensitive, others are upper-cased.</summary>
        public static string Ident(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\"")) return s.Substring(1, s.Length - 2);
            return s.ToUpperInvariant();
        }

        /// <summary>Splits "OWNER.NAME" (owner optional).</summary>
        public static void SplitName(string full, out string owner, out string name)
        {
            full = (full ?? "").Trim();
            var m = Regex.Match(full, @"^(?<o>""[^""]+""|[^.\s]+)\s*\.\s*(?<n>""[^""]+""|[^.\s]+)$");
            if (m.Success)
            {
                owner = Ident(m.Groups["o"].Value);
                name = Ident(m.Groups["n"].Value);
            }
            else
            {
                owner = null;
                name = Ident(full);
            }
        }

        public static string FormatResult(QueryResult r, int maxChars, int maxCell = 2000)
        {
            if (r.Columns.Count == 0) return "(statement returned no columns)";
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(" | ", r.Columns));
            sb.AppendLine(string.Join(" | ", r.Columns.Select(c => new string('-', Math.Max(3, Math.Min(c.Length, 20))))));
            int shown = 0;
            foreach (var row in r.Rows)
            {
                var line = string.Join(" | ", row.Select(v => Cell(v, maxCell)));
                if (sb.Length + line.Length > maxChars)
                {
                    sb.AppendLine($"... output truncated after {shown} rows (size limit).");
                    return sb.ToString();
                }
                sb.AppendLine(line);
                shown++;
            }
            sb.Append($"({r.Rows.Count} row{(r.Rows.Count == 1 ? "" : "s")}");
            if (r.Truncated) sb.Append(", more rows exist – refine the query or raise max_rows");
            sb.Append(")");
            return sb.ToString();
        }

        private static string Cell(string v, int max)
        {
            if (v == null) return "";
            // Keep '|' as is: code such as a || b must reach the model unchanged.
            v = v.Replace("\r\n", "\\n").Replace("\n", "\\n");
            return v.Length > max ? v.Substring(0, max) + "…" : v;
        }

        private static string StripStringsAndComments(string sql)
        {
            var s = Regex.Replace(sql ?? "", @"'([^']|'')*'", "''");
            s = Regex.Replace(s, @"--[^\n]*", "");
            s = Regex.Replace(s, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return s.Trim().TrimEnd(';');
        }

        // ---- statements that are always shown for approval (also with "Auto approve SQL" / "Always allow") ----

        private static readonly Regex WithPlsql = new Regex(@"\bWITH\s+(FUNCTION|PROCEDURE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Patterns are checked on the statement without comments; in PL/SQL also inside string literals, so dynamic
        /// SQL ('drop table ...') is found as well. When in doubt it asks: a false alarm only costs a click.
        /// </summary>
        private static readonly (Regex Pattern, string Reason)[] SensitivePatterns =
        {
            // most specific first: the approval card lists every reason that applies
            (WithPlsql, "This query contains PL/SQL (WITH FUNCTION) that can change data."),
            (new Regex(@"\bEXECUTE\s+IMMEDIATE\b|\bDBMS_SQL\b|\bEXEC_DDL_STATEMENT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "This statement runs dynamic SQL whose effect cannot be checked beforehand."),
            (new Regex(@"^\s*(GRANT|REVOKE)\b|\b(GRANT|REVOKE)\s+[\w ,]+\s+(TO|FROM)\b|\b(CREATE|ALTER|DROP)\s+(USER|ROLE|PROFILE)\b|\bALTER\s+(SYSTEM|DATABASE)\b|\b(NO)?AUDIT\s+\w", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "This statement changes privileges, users, roles or system settings."),
            (new Regex(@"\bDBMS_SCHEDULER\b|\bDBMS_JOB\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "This statement creates or changes jobs that run later."),
            (new Regex(@"\b(DROP|TRUNCATE|PURGE|FLASHBACK|DELETE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "This statement drops, truncates or deletes data/objects."),
        };

        private static readonly Regex TriggerOnEvents = new Regex(
            @"\bON\s+(DATABASE|SCHEMA|PLUGGABLE\s+DATABASE)\b|\b(LOGON|LOGOFF|STARTUP|SHUTDOWN|SERVERERROR|SUSPEND|DB_ROLE_CHANGE)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Why the statement must always be confirmed by the user, translated, every reason that applies (null = normal
        /// rules apply): destructive or dynamic SQL – also inside PL/SQL blocks –, privilege and system changes, jobs,
        /// PL/SQL in a query, UPDATE without WHERE.
        /// </summary>
        public static string SensitiveReason(string sql)
        {
            // string contents matter only where they can be executed: dynamic SQL inside PL/SQL
            var kw = FirstKeyword(sql);
            var code = kw == "BEGIN" || kw == "DECLARE" || kw == "CALL" || kw == "WITH" ? StripComments(sql) : StripStringsAndComments(sql);
            var reasons = SensitivePatterns.Where(p => p.Pattern.IsMatch(code)).Select(p => p.Reason).ToList();
            if (kw == "UPDATE" && !Regex.IsMatch(StripStringsAndComments(sql), @"\bWHERE\b", RegexOptions.IgnoreCase))
                reasons.Add("This UPDATE has no WHERE clause and changes every row.");
            return reasons.Count == 0 ? null : string.Join(" ", reasons.Select(r => Util.L.T(r)));
        }

        /// <summary>Triggers that fire on database or schema events (e.g. at every logon) always need the user's approval.</summary>
        public static string SensitiveUnitReason(string source)
        {
            if (!IsPlsqlUnit(source, out var type, out _, out _) || type != "TRIGGER") return null;
            var header = StripStringsAndComments(source);
            int begin = Regex.Match(header, @"\b(BEGIN|DECLARE|COMPOUND\s+TRIGGER|CALL)\b", RegexOptions.IgnoreCase).Index;
            if (begin > 0) header = header.Substring(0, begin);
            return TriggerOnEvents.IsMatch(header) ? "This trigger runs automatically on database or schema events (e.g. at every logon)." : null;
        }

        /// <summary>Removes comments but keeps string literals (so dynamic SQL in literals stays visible).</summary>
        private static string StripComments(string sql)
        {
            var s = sql ?? "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'')
                {
                    int end = i + 1;
                    while (end < s.Length && !(s[end] == '\'' && (end + 1 >= s.Length || s[end + 1] != '\''))) end += s[end] == '\'' ? 2 : 1;
                    end = Math.Min(end + 1, s.Length);
                    sb.Append(s, i, end - i);
                    i = end - 1;
                }
                else if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    sb.Append('\n');
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 1;
                    sb.Append(' ');
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
