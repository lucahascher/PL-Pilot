using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PlsqlDevAgent
{
    /// <summary>
    /// Entry point called by the native shim (PlsqlDevAgent.dll) through
    /// ICLRRuntimeHost::ExecuteInDefaultAppDomain. Runs on the PL/SQL Developer main thread.
    /// </summary>
    public static class Bootstrap
    {
        // Must match the enums in src/Shim/PlsqlDevAgentShim.cpp
        public const int EvMenuClick = 1;
        public const int EvActivate = 2;
        public const int EvDeactivate = 3;
        public const int EvDestroy = 4;
        public const int EvConnectionChange = 5;
        public const int EvWindowChange = 6;
        public const int EvCanClose = 7;
        public const int EvSelfTest = 8;

        public const int MiChat = 2;
        public const int MiAskSelection = 3;
        public const int MiSettings = 4;
        public const int MiPopupEditor = 20;
        public const int MiPopupObject = 21;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DispatchDelegate(int evt, IntPtr a, IntPtr b);

        private static DispatchDelegate _dispatch; // kept alive for the lifetime of the process

        public static string PluginDir { get; private set; }
        public static string ManagedDir { get; private set; }
        public static int PluginId { get; private set; }

        /// <summary>arg = "callbackTable|dispatchSlot|pluginId|pluginDir" (pointers in hex).</summary>
        public static int Init(string arg)
        {
            try
            {
                Agent.OpenAiClient.UseModernTls(); // first thing: before anything reads the .NET network settings
                var parts = arg.Split(new[] { '|' }, 4);
                var callbackTable = new IntPtr(Convert.ToInt64(parts[0], 16));
                var dispatchSlot = new IntPtr(Convert.ToInt64(parts[1], 16));
                PluginId = int.Parse(parts[2]);
                PluginDir = parts[3];
                ManagedDir = Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location);

                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPluginFolder;

                InitCore(callbackTable);

                _dispatch = Dispatch;
                Marshal.WriteIntPtr(dispatchSlot, Marshal.GetFunctionPointerForDelegate(_dispatch));
                return 0;
            }
            catch (Exception ex)
            {
                TryLogFatal(ex);
                return 1;
            }
        }

        // Separate method so that types from dependent assemblies (Newtonsoft, WebView2)
        // are only resolved after the AssemblyResolve handler is installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InitCore(IntPtr callbackTable)
        {
            Util.Log.Info($"Starting PL/Pilot {typeof(Bootstrap).Assembly.GetName().Version} from {ManagedDir}");
            Native.Callbacks.Initialize(callbackTable);
            Util.Log.Info(Native.Callbacks.Describe());
            Plugin.Create(new Ide.PlsqlIde(PluginId));
        }

        private static IntPtr Dispatch(int evt, IntPtr a, IntPtr b)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null) return evt == EvCanClose ? new IntPtr(1) : IntPtr.Zero;
                switch (evt)
                {
                    case EvMenuClick: plugin.OnMenuClick(a.ToInt32()); break;
                    case EvActivate: break;
                    case EvDeactivate: plugin.OnDeactivate(); break;
                    case EvDestroy: plugin.OnDestroy(); break;
                    case EvConnectionChange: plugin.OnConnectionChange(); break;
                    case EvWindowChange: plugin.OnWindowChange(); break;
                    case EvCanClose: return new IntPtr(plugin.CanClose() ? 1 : 0);
                    case EvSelfTest: Testing.SelfTest.Start(plugin); break;
                }
            }
            catch (Exception ex)
            {
                Util.Log.Error($"Dispatch({evt}) failed", ex);
            }
            return evt == EvCanClose ? new IntPtr(1) : IntPtr.Zero;
        }

        private static Assembly ResolveFromPluginFolder(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new AssemblyName(args.Name).Name;
                var candidate = Path.Combine(ManagedDir ?? "", name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void TryLogFatal(Exception ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlsqlDevAgent");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "agent.log"), $"{DateTime.Now:s} FATAL Init: {ex}\r\n");
            }
            catch { }
        }
    }
}
