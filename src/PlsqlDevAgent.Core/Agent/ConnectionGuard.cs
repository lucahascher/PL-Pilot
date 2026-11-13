using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using PlsqlDevAgent.Ide;

namespace PlsqlDevAgent.Agent
{
    public enum GuardMode
    {
        /// <summary>Changes allowed (with the normal approvals).</summary>
        Normal,
        /// <summary>Read-only because "Read-only mode" is enabled in the settings.</summary>
        ReadOnlySetting,
        /// <summary>Read-only because the connection matches a protected pattern.</summary>
        Protected,
        /// <summary>Connection matches a protected pattern, but the user allowed changes for this session.</summary>
        ProtectedOverridden,
        /// <summary>The user locked this connection manually.</summary>
        Locked,
    }

    public sealed class GuardState
    {
        public GuardMode Mode;
        public string Connection;
        public string Pattern;
        public bool ReadOnly => Mode == GuardMode.ReadOnlySetting || Mode == GuardMode.Protected || Mode == GuardMode.Locked;

        public string Describe()
        {
            switch (Mode)
            {
                case GuardMode.ReadOnlySetting: return "READ-ONLY (read-only mode is enabled in the settings)";
                case GuardMode.Protected: return $"READ-ONLY (connection {Connection} is protected by the pattern '{Pattern}')";
                case GuardMode.ProtectedOverridden: return $"changes allowed, but CAUTION: {Connection} is a protected connection (pattern '{Pattern}') – the user unlocked it for this session";
                case GuardMode.Locked: return $"READ-ONLY (the user locked connection {Connection})";
                default: return "changes allowed (each change needs the user's approval)";
            }
        }
    }

    /// <summary>
    /// Production guard: connections matching configurable wildcard patterns (e.g. *PROD*) are read-only for
    /// the agent. The user can unlock a protected connection or lock any other connection for the current
    /// IDE session (not persisted, so a restart is always safe).
    /// </summary>
    public sealed class ConnectionGuard
    {
        private readonly ConcurrentDictionary<string, bool> _overrides = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public GuardState Evaluate(AgentConfig cfg, IdeContext ctx)
        {
            var label = ctx != null && ctx.Connected ? $"{ctx.User}@{ctx.Database}" : "";
            var state = new GuardState { Connection = label, Pattern = MatchingPattern(cfg.ProtectedConnections, ctx) };
            if (cfg.ReadOnlyMode) { state.Mode = GuardMode.ReadOnlySetting; return state; }
            _overrides.TryGetValue(label, out var allow);
            bool hasOverride = _overrides.ContainsKey(label);
            if (state.Pattern != null)
                state.Mode = hasOverride && allow ? GuardMode.ProtectedOverridden : GuardMode.Protected;
            else
                state.Mode = hasOverride && !allow ? GuardMode.Locked : GuardMode.Normal;
            return state;
        }

        /// <summary>allow = true: unlock (protected connection); allow = false: lock; null: back to the configured default.</summary>
        public void SetOverride(string connection, bool? allow)
        {
            if (string.IsNullOrEmpty(connection)) return;
            if (allow.HasValue) _overrides[connection] = allow.Value;
            else _overrides.TryRemove(connection, out _);
        }

        public static string MatchingPattern(string patterns, IdeContext ctx)
        {
            if (ctx == null || !ctx.Connected || string.IsNullOrWhiteSpace(patterns)) return null;
            var candidates = new[] { $"{ctx.User}@{ctx.Database}", ctx.Database ?? "" };
            foreach (var raw in patterns.Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                var regex = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                if (candidates.Any(c => Regex.IsMatch(c, regex, RegexOptions.IgnoreCase))) return p;
            }
            return null;
        }
    }
}
