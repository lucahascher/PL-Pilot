using System;
using System.Collections.Generic;
using System.Text;

namespace PlsqlDevAgent.Util
{
    /// <summary>Small line based unified diff (common prefix/suffix trimming + LCS on the middle).</summary>
    public static class LineDiff
    {
        private const long MaxLcsCells = 4_000_000;

        public static string Unified(string before, string after, int context = 3)
        {
            var a = SplitLines(before);
            var b = SplitLines(after);
            var ops = Diff(a, b);

            var sb = new StringBuilder();
            int n = ops.Count, i = 0;
            int added = 0, removed = 0;
            foreach (var op in ops) { if (op.Kind == '+') added++; else if (op.Kind == '-') removed++; }
            if (added == 0 && removed == 0) return "(no changes)";

            while (i < n)
            {
                // find next change
                int start = i;
                while (start < n && ops[start].Kind == ' ') start++;
                if (start >= n) break;
                int hunkStart = Math.Max(i, start - context);
                // extend hunk while changes are within 2*context lines
                int end = start;
                int lastChange = start;
                while (end < n)
                {
                    if (ops[end].Kind != ' ') lastChange = end;
                    else if (end - lastChange > 2 * context) break;
                    end++;
                }
                int hunkEnd = Math.Min(n, lastChange + context + 1);
                var first = ops[hunkStart];
                sb.Append("@@ -").Append(first.LineA + 1).Append(" +").Append(first.LineB + 1).Append(" @@\n");
                for (int k = hunkStart; k < hunkEnd; k++) sb.Append(ops[k].Kind).Append(ops[k].Text).Append('\n');
                i = hunkEnd;
            }
            return $"{removed} line(s) removed, {added} line(s) added\n" + sb;
        }

        private struct Op
        {
            public char Kind;   // ' ', '-', '+'
            public string Text;
            public int LineA, LineB;
        }

        private static List<Op> Diff(string[] a, string[] b)
        {
            int pre = 0;
            while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
            int suf = 0;
            while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;

            var ops = new List<Op>();
            for (int k = 0; k < pre; k++) ops.Add(new Op { Kind = ' ', Text = a[k], LineA = k, LineB = k });

            int na = a.Length - pre - suf, nb = b.Length - pre - suf;
            if ((long)(na + 1) * (nb + 1) <= MaxLcsCells)
            {
                var lcs = new int[na + 1, nb + 1];
                for (int x = na - 1; x >= 0; x--)
                    for (int y = nb - 1; y >= 0; y--)
                        lcs[x, y] = a[pre + x] == b[pre + y] ? lcs[x + 1, y + 1] + 1 : Math.Max(lcs[x + 1, y], lcs[x, y + 1]);
                int i = 0, j = 0;
                while (i < na || j < nb)
                {
                    if (i < na && j < nb && a[pre + i] == b[pre + j])
                    {
                        ops.Add(new Op { Kind = ' ', Text = a[pre + i], LineA = pre + i, LineB = pre + j }); i++; j++;
                    }
                    else if (i < na && (j >= nb || lcs[i + 1, j] >= lcs[i, j + 1]))
                    {
                        ops.Add(new Op { Kind = '-', Text = a[pre + i], LineA = pre + i, LineB = pre + j }); i++;
                    }
                    else
                    {
                        ops.Add(new Op { Kind = '+', Text = b[pre + j], LineA = pre + i, LineB = pre + j }); j++;
                    }
                }
            }
            else
            {
                for (int k = 0; k < na; k++) ops.Add(new Op { Kind = '-', Text = a[pre + k], LineA = pre + k, LineB = pre });
                for (int k = 0; k < nb; k++) ops.Add(new Op { Kind = '+', Text = b[pre + k], LineA = pre + na, LineB = pre + k });
            }

            for (int k = 0; k < suf; k++)
                ops.Add(new Op { Kind = ' ', Text = a[a.Length - suf + k], LineA = a.Length - suf + k, LineB = b.Length - suf + k });
            return ops;
        }

        private static string[] SplitLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            return s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
    }
}
