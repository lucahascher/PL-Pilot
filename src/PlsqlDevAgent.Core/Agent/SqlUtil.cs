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
    }
}
