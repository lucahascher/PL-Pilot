using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PlsqlDevAgent.Ide;

namespace PlsqlDevAgent.Agent
{
    /// <summary>
    /// Setting "No parallel execution" (a DBA policy for small development databases): the agent's statements run
    /// serially. Its own session gets ALTER SESSION DISABLE PARALLEL QUERY / DML / DDL (<see cref="PlsqlIde"/>); in
    /// addition every query and DML statement gets a statement-level NO_PARALLEL hint and loses PARALLEL hints the
    /// model wrote – a statement-level PARALLEL hint could otherwise still ask for parallel execution.
    /// </summary>
    internal static class SerialSql
    {
        /// <summary>Statements that change the session's parallel mode back (refused while the setting is on).</summary>
        private static readonly Regex AlterParallel = new Regex(
            @"^ALTER\s+SESSION\s+(?:ENABLE|FORCE)\s+PARALLEL\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Hints that ask for parallel execution (NO_PARALLEL… is not matched: no word boundary after "NO_").</summary>
        private static readonly Regex ParallelHint = new Regex(
            @"\b(?:ENABLE_PARALLEL_DML|PARALLEL_INDEX|PARALLEL)\b\s*(?:\([^)]*\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NoParallelHint = new Regex(@"(?<![\w$#])NO_PARALLEL(?![\w$#(])|(?<![\w$#])NO_PARALLEL\s*\(\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> Statements = new HashSet<string> { "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "MERGE" };

        /// <summary>Why the statement is refused while the setting is on, or null.</summary>
        public static string RefusalReason(string sql) =>
            AlterParallel.IsMatch(SqlUtil.StripLeadingComments(sql ?? ""))
                ? "Not executed: parallel execution is switched off for the agent (PL/Pilot setting \"No parallel execution\", " +
                  "a DBA policy for this database). Run the statements serially."
                : null;

        /// <summary>The statement with a statement-level NO_PARALLEL hint and without PARALLEL hints (other statements unchanged).</summary>
        public static string Rewrite(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql) || !Statements.Contains(SqlUtil.FirstKeyword(sql))) return sql;
            var text = RemoveParallelHints(sql);
            var mask = Mask(text);
            int pos = InsertPosition(mask);
            if (pos < 0) return text;

            // directly after the keyword: an existing hint comment gets NO_PARALLEL, otherwise a new one is inserted
            int next = pos;
            while (next < mask.Length && char.IsWhiteSpace(mask[next])) next++;
            if (next + 2 < mask.Length && mask[next] == '#' && text.Substring(next, 3) == "/*+")
            {
                int end = text.IndexOf("*/", next + 3, StringComparison.Ordinal);
                var body = end < 0 ? text.Substring(next + 3) : text.Substring(next + 3, end - next - 3);
                return NoParallelHint.IsMatch(body) ? text : text.Insert(next + 3, " NO_PARALLEL");
            }
            return text.Insert(pos, " /*+ NO_PARALLEL */");
        }

        /// <summary>Removes PARALLEL, PARALLEL_INDEX and ENABLE_PARALLEL_DML from every hint comment (not from strings or other comments).</summary>
        private static string RemoveParallelHints(string sql)
        {
            var mask = Mask(sql);
            var hints = Regex.Matches(sql, @"/\*\+([\s\S]*?)\*/").Cast<Match>().Where(m => mask[m.Index] == '#').ToList();
            if (hints.Count == 0) return sql;
            var sb = new StringBuilder(sql);
            for (int i = hints.Count - 1; i >= 0; i--)
            {
                var body = hints[i].Groups[1];
                var cleaned = ParallelHint.Replace(body.Value, "");
                if (cleaned == body.Value) continue;
                if (cleaned.Trim().Length > 0)
                {
                    sb.Remove(body.Index, body.Length).Insert(body.Index, Regex.Replace(cleaned, @"\s{2,}", " "));
                    continue;
                }
                // nothing left: the whole hint comment goes (with one of the blanks around it)
                int start = hints[i].Index, length = hints[i].Length;
                if (start + length < sb.Length && char.IsWhiteSpace(sb[start + length]) && start > 0 && char.IsWhiteSpace(sb[start - 1])) length++;
                sb.Remove(start, length);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Where the statement-level hint goes: after the first keyword; for WITH after the main query's SELECT (the first
        /// SELECT outside parentheses). -1 for WITH FUNCTION / PROCEDURE (PL/SQL in the query) or if none is found.
        /// </summary>
        private static int InsertPosition(string mask)
        {
            var first = Regex.Match(mask, @"[A-Za-z]+");
            if (!first.Success) return -1;
            var kw = first.Value.ToUpperInvariant();
            if (kw != "WITH") return first.Index + first.Length;
            if (Regex.IsMatch(mask.Substring(first.Index), @"^WITH\s+(FUNCTION|PROCEDURE)\b", RegexOptions.IgnoreCase)) return -1;
            int depth = 0;
            for (int i = first.Index + 4; i < mask.Length; i++)
            {
                char c = mask[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && (c == 'S' || c == 's') && IsWordAt(mask, i, "SELECT")) return i + 6;
            }
            return -1;
        }

        private static bool IsWordAt(string s, int i, string word) =>
            i + word.Length <= s.Length && string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
            (i == 0 || !IsWordChar(s[i - 1])) && (i + word.Length == s.Length || !IsWordChar(s[i + word.Length]));

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';

        /// <summary>The statement with string literals and comments blanked out and hint comments as '#' (same length).</summary>
        internal static string Mask(string s)
        {
            var m = s.ToCharArray();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\'')
                {
                    int j = i + 1;
                    while (j < s.Length && !(s[j] == '\'' && (j + 1 >= s.Length || s[j + 1] != '\''))) j += s[j] == '\'' ? 2 : 1;
                    Fill(m, i, Math.Min(j, s.Length - 1), ' ');
                    i = j + 1;
                }
                else if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
                {
                    int j = s.IndexOf('\n', i);
                    if (j < 0) j = s.Length;
                    Fill(m, i, j - 1, ' ');
                    i = j;
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int j = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    j = j < 0 ? s.Length - 1 : j + 1;
                    Fill(m, i, j, i + 2 < s.Length && s[i + 2] == '+' ? '#' : ' ');
                    i = j + 1;
                }
                else i++;
            }
            return new string(m);
        }

        private static void Fill(char[] m, int from, int to, char c)
        {
            for (int k = from; k <= to && k < m.Length; k++) m[k] = c;
        }

        /// <summary>Rule for the model while the setting is on.</summary>
        public const string PromptRule =
            "- Parallel execution is switched off for your database session (the user's setting \"No parallel execution\", a DBA " +
            "policy for this database): never use PARALLEL hints or ALTER SESSION ENABLE/FORCE PARALLEL. PL/Pilot removes PARALLEL " +
            "hints and runs your statements serially (NO_PARALLEL).";
    }

    /// <summary>IIde decorator for the setting "No parallel execution": the agent's statements run serially (see <see cref="SerialSql"/>).</summary>
    internal sealed class SerialSqlIde : IdeDecorator
    {
        public SerialSqlIde(IIde inner) : base(inner) { }

        public override QueryResult Query(string sql, int maxRows) => Inner.Query(SerialSql.Rewrite(sql), maxRows);

        public override ExecResult Execute(string sql)
        {
            if (SerialSql.RefusalReason(sql) is string reason) throw new IdeException(reason);
            return Inner.Execute(SerialSql.Rewrite(sql));
        }
    }
}
