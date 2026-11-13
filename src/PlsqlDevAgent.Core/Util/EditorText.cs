using System;
using System.Collections.Generic;

namespace PlsqlDevAgent.Util
{
    /// <summary>Text operations for "Insert at cursor" / "Replace selection" (the plug-in API has no insert call).</summary>
    public static class EditorText
    {
        /// <summary>Character offset of a 1-based line / column position (column clamped to the line length).</summary>
        public static int OffsetOf(string text, int line, int column)
        {
            text = text ?? "";
            int offset = 0;
            for (int current = 1; current < line; current++)
            {
                int nl = text.IndexOf('\n', offset);
                if (nl < 0) return text.Length;
                offset = nl + 1;
            }
            int lineEnd = text.IndexOf('\n', offset);
            if (lineEnd < 0) lineEnd = text.Length;
            if (lineEnd > offset && text[lineEnd - 1] == '\r') lineEnd--;
            return Math.Min(offset + Math.Max(0, column - 1), lineEnd);
        }

        public static string InsertAt(string text, int line, int column, string insert)
        {
            text = text ?? "";
            return text.Insert(OffsetOf(text, line, column), MatchLineEndings(text, insert));
        }

        /// <summary>
        /// Replaces the selected text. The API only returns the selection's text, so the occurrence is located via
        /// the cursor position (which is at the start or end of a selection) if the text occurs more than once.
        /// </summary>
        public static string ReplaceSelection(string text, string selection, int line, int column, string replacement)
        {
            if (string.IsNullOrEmpty(selection)) throw new InvalidOperationException(L.T("Select the code to replace in the editor first."));
            text = text ?? "";
            var candidates = new List<string> { selection };
            if (!selection.Contains("\r\n") && selection.Contains("\n")) candidates.Add(selection.Replace("\n", "\r\n"));
            if (selection.Contains("\r\n")) candidates.Add(selection.Replace("\r\n", "\n"));

            foreach (var sel in candidates)
            {
                var positions = new List<int>();
                for (int p = text.IndexOf(sel, StringComparison.Ordinal); p >= 0; p = text.IndexOf(sel, p + 1, StringComparison.Ordinal))
                    positions.Add(p);
                if (positions.Count == 0) continue;

                int cursor = OffsetOf(text, line, column);
                int best = positions[0], bestDistance = int.MaxValue;
                foreach (var p in positions)
                {
                    int distance = Math.Min(Math.Abs(p - cursor), Math.Abs(p + sel.Length - cursor));
                    if (distance < bestDistance) { best = p; bestDistance = distance; }
                }
                return text.Substring(0, best) + MatchLineEndings(text, replacement) + text.Substring(best + sel.Length);
            }
            throw new InvalidOperationException(L.T("The selected text could not be located in the editor (block selections are not supported)."));
        }

        private static string MatchLineEndings(string editor, string insert)
        {
            insert = (insert ?? "").Replace("\r\n", "\n");
            return editor.Contains("\r\n") ? insert.Replace("\n", "\r\n") : insert;
        }
    }
}
