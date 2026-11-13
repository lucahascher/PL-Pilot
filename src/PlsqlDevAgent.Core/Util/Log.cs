using System;
using System.IO;
using System.Linq;

namespace PlsqlDevAgent.Util
{
    /// <summary>Very small file logger: %LOCALAPPDATA%\PlsqlDevAgent\agent.log (rotated at 2 MB).</summary>
    public static class Log
    {
        private static readonly object Sync = new object();

        /// <summary>Data folder (log, chats, backups, WebView2 profile); PLSQLDEVAGENT_DATA overrides it for tests.</summary>
        public static string Directory { get; } =
            Environment.GetEnvironmentVariable("PLSQLDEVAGENT_DATA") is string custom && custom.Length > 0
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlsqlDevAgent");

        public static string FilePath => Path.Combine(Directory, "agent.log");

        public static void Info(string message) => Write("INFO ", message);

        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string message, Exception ex = null) =>
            Write("ERROR", ex == null ? message : message + ": " + ex);

        /// <summary>Removes log entries written before <paramref name="cutoff"/> (retention setting).</summary>
        public static void DeleteOlderThan(DateTime cutoff)
        {
            lock (Sync)
            {
                var old = FilePath + ".1";
                if (File.Exists(old) && File.GetLastWriteTime(old) < cutoff) File.Delete(old);
                if (!File.Exists(FilePath)) return;
                var lines = File.ReadAllLines(FilePath);
                int first = 0; // entries are chronological; lines without a time stamp belong to the entry above
                while (first < lines.Length && !(lines[first].Length >= 19 &&
                       DateTime.TryParseExact(lines[first].Substring(0, 19), "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                           System.Globalization.DateTimeStyles.None, out var time) && time >= cutoff))
                    first++;
                if (first > 0) File.WriteAllLines(FilePath, lines.Skip(first));
            }
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    var file = new FileInfo(FilePath);
                    if (file.Exists && file.Length > 2 * 1024 * 1024)
                    {
                        var old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}\r\n");
                }
            }
            catch
            {
                // logging must never throw
            }
        }
    }
}
