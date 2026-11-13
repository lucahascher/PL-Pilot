using System;
using System.IO;

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
