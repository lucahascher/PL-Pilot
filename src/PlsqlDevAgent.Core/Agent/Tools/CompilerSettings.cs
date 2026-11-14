using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PlsqlDevAgent.Ide;
using static PlsqlDevAgent.Agent.SqlUtil;

namespace PlsqlDevAgent.Agent.Tools
{
    /// <summary>
    /// CREATE OR REPLACE takes the PL/SQL compiler settings (PLSQL_CCFLAGS, PLSQL_OPTIMIZE_LEVEL, PLSQL_CODE_TYPE,
    /// PLSQL_DEBUG, NLS_LENGTH_SEMANTICS, PLSCOPE_SETTINGS, PLSQL_WARNINGS) from the session that compiles, and the
    /// agent compiles in its own session: without this, a unit compiled with e.g. PLSQL_CCFLAGS='debug:true', as NATIVE
    /// or with debug information would silently lose that when the agent replaces it.
    /// <para>
    /// The scope sets the agent's session to the settings stored with the unit before it is replaced (a new unit gets
    /// the session's own settings), turns the compiler warnings on (setting CompilerWarnings; the unit keeps the
    /// warning qualifiers it had, e.g. ERROR:… or DISABLE:…) and sets the session back when it is disposed.
    /// </para>
    /// </summary>
    internal sealed class CompilerSettingsScope : IDisposable
    {
        private const string OptimizeLevel = "PLSQL_OPTIMIZE_LEVEL", CodeType = "PLSQL_CODE_TYPE", Debug = "PLSQL_DEBUG",
            LengthSemantics = "NLS_LENGTH_SEMANTICS", CcFlags = "PLSQL_CCFLAGS", Plscope = "PLSCOPE_SETTINGS", Warnings = "PLSQL_WARNINGS";

        /// <summary>Warnings that are noise for the agent or would tempt it into unwanted changes.</summary>
        private static readonly string[] NoisyWarnings =
        {
            "5018", // unit omitted optional AUTHID clause (appears for almost every unit)
            "7203", // parameter may benefit from NOCOPY (changes semantics, not a fix)
            "6015", // parameter PLSQL_DEBUG is deprecated (when debug information is kept)
            "6005", // inlining of call was done (informational)
        };

        private readonly ToolContext _ctx;
        private readonly bool _keepStored;
        /// <summary>Settings of the session before the scope; null = unknown, then nothing is changed.</summary>
        private Dictionary<string, string> _baseline;
        private bool _baselineRead;
        /// <summary>The session's settings now (as far as this scope knows).</summary>
        private Dictionary<string, string> _current;

        /// <param name="keepStored">false: compile with the session's settings (only the warnings are turned on).</param>
        public CompilerSettingsScope(ToolContext ctx, bool keepStored = true)
        {
            _ctx = ctx;
            _keepStored = keepStored;
        }

        /// <summary>Before CREATE OR REPLACE of a PL/SQL unit (not for views: they have no compiler settings).</summary>
        public void Prepare(string type, string owner, string name)
        {
            try
            {
                if (!ReadBaseline()) return;
                var stored = _keepStored ? Stored(type, owner, name) : null;
                var target = new Dictionary<string, string>(_baseline, StringComparer.OrdinalIgnoreCase);
                if (stored != null)
                    foreach (var kv in stored)
                        if (!string.IsNullOrEmpty(kv.Value)) target[kv.Key] = kv.Value;
                if (_ctx.Config.CompilerWarnings) target[Warnings] = EnableWarnings(target[Warnings]);
                Apply(target);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Util.Log.Warn($"Compiler settings for {type} {owner}.{name} could not be prepared: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_baseline == null || _current == null) return;
            try { Apply(_baseline); }
            catch (Exception ex) { Util.Log.Warn("The compiler settings of the session could not be restored: " + ex.Message); }
        }

        /// <summary>
        /// Reads the session's settings with inquiry directives ($$PLSQL_CCFLAGS …) in an anonymous block, which
        /// needs no privileges (V$PARAMETER would).
        /// </summary>
        private bool ReadBaseline()
        {
            if (_baselineRead) return _baseline != null;
            _baselineRead = true;
            var res = _ctx.Ide.Execute(
                "begin\n" +
                "  dbms_output.put_line('PDA_CS " + OptimizeLevel + "=' || $$PLSQL_OPTIMIZE_LEVEL);\n" +
                "  dbms_output.put_line('PDA_CS " + CodeType + "=' || $$PLSQL_CODE_TYPE);\n" +
                "  dbms_output.put_line('PDA_CS " + LengthSemantics + "=' || $$NLS_LENGTH_SEMANTICS);\n" +
                "  dbms_output.put_line('PDA_CS " + CcFlags + "=' || $$PLSQL_CCFLAGS);\n" +
                "  dbms_output.put_line('PDA_CS " + Plscope + "=' || $$PLSCOPE_SETTINGS);\n" +
                "  dbms_output.put_line('PDA_CS " + Warnings + "=' || $$PLSQL_WARNINGS);\n" +
                "end;");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (res.Ok)
                foreach (var line in (res.DbmsOutput ?? "").Replace("\r\n", "\n").Split('\n'))
                {
                    var m = Regex.Match(line, @"^PDA_CS (\w+)=(.*)$");
                    if (m.Success) values[m.Groups[1].Value] = m.Groups[2].Value.Trim();
                }
            if (!values.ContainsKey(OptimizeLevel) || !values.ContainsKey(Warnings))
            {
                Util.Log.Info("Compiler settings of the session unknown (" + (res.Ok ? "no DBMS_OUTPUT" : res.ErrorMessage) + "): units are compiled with the session's settings");
                return false;
            }
            // PLSQL_DEBUG has no inquiry directive; V$PARAMETER needs privileges, otherwise assume the default
            values[Debug] = "FALSE";
            try
            {
                var r = _ctx.Ide.Query("select upper(value) from v$parameter where name = 'plsql_debug'", 1);
                if (r.Rows.Count > 0 && !string.IsNullOrEmpty(r.Rows[0][0])) values[Debug] = r.Rows[0][0];
            }
            catch (OracleSqlException) { }
            _baseline = values;
            _current = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            return true;
        }

        /// <summary>Settings stored with the unit, null if it does not exist yet (or they cannot be read).</summary>
        private Dictionary<string, string> Stored(string type, string owner, string name)
        {
            try
            {
                var r = _ctx.Ide.Query(
                    "select plsql_optimize_level, plsql_code_type, plsql_debug, nls_length_semantics, plsql_ccflags, plscope_settings, plsql_warnings " +
                    $"from all_plsql_object_settings where owner = {Lit(owner)} and name = {Lit(name)} and type = {Lit(type.ToUpperInvariant())}", 1);
                if (r.Rows.Count == 0) return null;
                var row = r.Rows[0];
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [OptimizeLevel] = row[0], [CodeType] = row[1], [Debug] = row[2], [LengthSemantics] = row[3],
                    [CcFlags] = row[4] ?? "", [Plscope] = row[5], [Warnings] = row[6],
                };
            }
            catch (OracleSqlException ex)
            {
                Util.Log.Warn($"Stored compiler settings of {type} {owner}.{name} not readable: {ex.Message}");
                return null;
            }
        }

        /// <summary>Sets the session parameters that differ from <paramref name="target"/>.</summary>
        private void Apply(Dictionary<string, string> target)
        {
            var assignments = new List<string>();
            var changed = new List<string>();
            foreach (var p in new[] { OptimizeLevel, CodeType, Debug, LengthSemantics, CcFlags, Plscope })
            {
                if (!target.TryGetValue(p, out var value) || value == null) continue;
                if (_current.TryGetValue(p, out var now) && Same(now, value)) continue;
                var literal = Literal(p, value);
                if (literal == null) continue;
                assignments.Add($"{p.ToLowerInvariant()} = {literal}");
                changed.Add(p);
            }
            if (assignments.Count > 0)
            {
                var res = _ctx.Ide.Execute("alter session set " + string.Join(" ", assignments));
                if (res.Ok) foreach (var p in changed) _current[p] = target[p];
                else Util.Log.Warn("ALTER SESSION for the compiler settings failed: " + res.ErrorMessage);
            }

            if (target.TryGetValue(Warnings, out var warnings) && warnings != null &&
                !(_current.TryGetValue(Warnings, out var currentWarnings) && Canonical(currentWarnings) == Canonical(warnings)))
            {
                var qualifiers = ParseWarnings(warnings);
                if (qualifiers.Count > 0)
                {
                    var res = _ctx.Ide.Execute("alter session set plsql_warnings = " + string.Join(", ", qualifiers.Select(Lit)));
                    if (res.Ok) _current[Warnings] = warnings;
                    else Util.Log.Warn("ALTER SESSION SET PLSQL_WARNINGS failed: " + res.ErrorMessage);
                }
            }
        }

        private static bool Same(string a, string b) =>
            string.Equals(Regex.Replace(a ?? "", @"\s+", ""), Regex.Replace(b ?? "", @"\s+", ""), StringComparison.OrdinalIgnoreCase);

        /// <summary>The value as it is written in ALTER SESSION, null if it does not look valid.</summary>
        private static string Literal(string parameter, string value)
        {
            value = value.Trim();
            switch (parameter)
            {
                case OptimizeLevel: return Regex.IsMatch(value, @"^[0-3]$") ? value : null;
                case CodeType: return Regex.IsMatch(value, @"^(INTERPRETED|NATIVE)$", RegexOptions.IgnoreCase) ? value.ToUpperInvariant() : null;
                case Debug: return Regex.IsMatch(value, @"^(TRUE|FALSE)$", RegexOptions.IgnoreCase) ? value.ToUpperInvariant() : null;
                case LengthSemantics: return Regex.IsMatch(value, @"^(BYTE|CHAR)$", RegexOptions.IgnoreCase) ? value.ToUpperInvariant() : null;
                case CcFlags:
                case Plscope: return Lit(value);
                default: return null;
            }
        }

        /// <summary>
        /// "ENABLE:ALL,DISABLE:  5018,ERROR:	6002" (the format of $$PLSQL_WARNINGS and ALL_PLSQL_OBJECT_SETTINGS) ->
        /// ["ENABLE:ALL", "DISABLE:5018", "ERROR:6002"].
        /// </summary>
        internal static List<string> ParseWarnings(string setting)
        {
            var list = new List<string>();
            foreach (var part in (setting ?? "").Split(','))
            {
                var m = Regex.Match(part, @"^\s*(ENABLE|DISABLE|ERROR)\s*:\s*(\w+)\s*$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var target = m.Groups[2].Value.ToUpperInvariant();
                if (Regex.IsMatch(target, @"^\d+$")) target = int.Parse(target).ToString();
                list.Add(m.Groups[1].Value.ToUpperInvariant() + ":" + target);
            }
            return list;
        }

        private static string Canonical(string setting) => string.Join(",", ParseWarnings(setting));

        /// <summary>
        /// All warnings on, except the noisy ones; the unit's own qualifiers for single warnings (ENABLE/DISABLE/ERROR:n)
        /// and its ERROR qualifiers for categories stay in force, because they come later.
        /// </summary>
        internal static string EnableWarnings(string stored)
        {
            var kept = ParseWarnings(stored)
                .Where(q => Regex.IsMatch(q, @":\d+$") || q.StartsWith("ERROR:"))
                .ToList();
            var result = new List<string> { "ENABLE:ALL" };
            result.AddRange(NoisyWarnings.Select(n => "DISABLE:" + n));
            result.AddRange(kept);
            return string.Join(",", result);
        }
    }
}
