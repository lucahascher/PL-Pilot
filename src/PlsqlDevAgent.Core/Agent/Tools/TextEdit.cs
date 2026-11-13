using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlsqlDevAgent.Agent.Tools
{
    /// <summary>Exact string replacement with a whitespace tolerant fallback (shared by editor and file edits).</summary>
    internal static class TextEdit
    {
        public static string Apply(string original, string oldStr, string newStr, bool replaceAll)
        {
            original = original ?? "";
            bool crlf = original.Contains("\r\n");
            var text = original.Replace("\r\n", "\n");
            var o = (oldStr ?? "").Replace("\r\n", "\n");
            var n = (newStr ?? "").Replace("\r\n", "\n");
            if (o.Length == 0)
                throw new ToolArgumentException("old_string must not be empty. To replace everything use the full-rewrite tool.");
            if (o == n)
                throw new ToolArgumentException("old_string and new_string are identical; nothing to change.");

            string result;
            int count = Count(text, o);
            if (count == 1 || (count > 1 && replaceAll))
            {
                result = replaceAll ? text.Replace(o, n) : ReplaceFirst(text, o, n);
            }
            else if (count > 1)
            {
                throw new ToolArgumentException(
                    $"old_string occurs {count} times. Include more surrounding lines to make it unique, or set replace_all=true.");
            }
            else
            {
                result = LineFuzzyReplace(text, o, n)
                    ?? throw new ToolArgumentException(
                        "old_string was not found in the current text. Read the text again and copy the exact lines " +
                        "(including indentation and comments) into old_string.");
            }
            return crlf ? result.Replace("\n", "\r\n") : result;
        }

        private static int Count(string text, string sub)
        {
            int count = 0, pos = 0;
            while ((pos = text.IndexOf(sub, pos, StringComparison.Ordinal)) >= 0) { count++; pos += sub.Length; }
            return count;
        }

        private static string ReplaceFirst(string text, string o, string n)
        {
            int pos = text.IndexOf(o, StringComparison.Ordinal);
            return text.Substring(0, pos) + n + text.Substring(pos + o.Length);
        }

        /// <summary>Matches whole lines ignoring trailing whitespace and indentation differences.</summary>
        private static string LineFuzzyReplace(string text, string o, string n)
        {
            var lines = text.Split('\n');
            var oldLines = o.Trim('\n').Split('\n').Select(Key).ToArray();
            if (oldLines.Length == 0 || oldLines.All(l => l.Length == 0)) return null;

            int found = -1;
            for (int i = 0; i + oldLines.Length <= lines.Length; i++)
            {
                bool match = true;
                for (int k = 0; k < oldLines.Length && match; k++)
                    match = Key(lines[i + k]) == oldLines[k];
                if (!match) continue;
                if (found >= 0) return null; // ambiguous
                found = i;
            }
            if (found < 0) return null;

            var sb = new StringBuilder();
            for (int i = 0; i < found; i++) sb.Append(lines[i]).Append('\n');
            sb.Append(n.Trim('\n'));
            int after = found + oldLines.Length;
            if (after < lines.Length) sb.Append('\n');
            for (int i = after; i < lines.Length; i++)
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string Key(string line) => line.Trim();

        public static string WithLineNumbers(string text, int startLine, int endLine)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            int from = Math.Max(1, startLine), to = endLine <= 0 ? lines.Length : Math.Min(lines.Length, endLine);
            var sb = new StringBuilder();
            int width = to.ToString().Length;
            for (int i = from; i <= to; i++) sb.Append(i.ToString().PadLeft(width)).Append(": ").Append(lines[i - 1]).Append('\n');
            return sb.ToString();
        }

        public static string Lines(string text, int startLine, int endLine)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            int from = Math.Max(1, startLine), to = endLine <= 0 ? lines.Length : Math.Min(lines.Length, endLine);
            if (from > to) return "";
            return string.Join("\n", lines.Skip(from - 1).Take(to - from + 1));
        }
    }
}
