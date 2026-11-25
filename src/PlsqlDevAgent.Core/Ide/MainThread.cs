using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using PlsqlDevAgent.Native;

namespace PlsqlDevAgent.Ide
{
    /// <summary>
    /// Runs work on the PL/SQL Developer main (UI) thread. The IDE callbacks are not thread
    /// safe, so every call into the IDE goes through here. Implemented with a hidden
    /// message-only window: posted messages are dispatched by the IDE's own message loop.
    /// </summary>
    public static class MainThread
    {
        private const int WM_RUN = 0x0400 + 0x2A1; // WM_USER + n
        private const int WM_TIMER = 0x0113;
        private static readonly IntPtr ResumeTimer = new IntPtr(0x2A1);
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        /// <summary>
        /// Work on the main thread longer than this in one go lets PL/SQL Developer handle input and painting before the
        /// next queued item runs. Windows hands out posted messages (ours) before keyboard, mouse and paint messages, so
        /// without this pause a steady stream of work (e.g. helper agents whose SQL has to run on the main thread)
        /// would keep the IDE from reacting to the user at all.
        /// </summary>
        private static readonly int YieldAfterMs =
            int.TryParse(Environment.GetEnvironmentVariable("PLSQLDEVAGENT_MAINTHREAD_YIELD_MS"), out var ms) && ms > 0 ? ms : 40; // override for measurements

        /// <summary>Work items slower than this are written to the log (what kept the IDE busy).</summary>
        private const int LogSlowItemMs = 1000;

        private static Receiver _receiver;
        private static int _threadId;
        private static readonly ConcurrentQueue<WorkItem> Queue = new ConcurrentQueue<WorkItem>();
        /// <summary>The queue continues on the resume timer: WM_RUN messages are ignored until it fires.</summary>
        private static bool _deferred;
        /// <summary>Label of the work item running on the main thread right now (null: none of ours).</summary>
        private static volatile string _current;

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _threadId;

        /// <summary>Must be called once, on the main thread.</summary>
        public static void Initialize()
        {
            if (_receiver != null) return;
            _threadId = Thread.CurrentThread.ManagedThreadId;
            _receiver = new Receiver();
            _receiver.CreateHandle(new CreateParams { Caption = "PlsqlDevAgent.MainThread", Parent = HWND_MESSAGE });
        }

        public static void Shutdown()
        {
            try { _receiver?.DestroyHandle(); } catch { }
            _receiver = null;
        }

        public static T Invoke<T>(Func<T> func, int timeoutMs = 10 * 60 * 1000, string label = null)
        {
            if (IsMainThread || _receiver == null) return func();

            var item = new WorkItem(() => func(), label ?? Describe(func));
            Queue.Enqueue(item);
            if (!PostMessage(_receiver.Handle, WM_RUN, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("Could not post to the IDE main thread.");
            if (!item.Done.Wait(timeoutMs))
                throw new TimeoutException("The IDE did not respond (it may be busy with a modal dialog or a running query).");
            if (item.Error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(item.Error).Throw();
            return (T)item.Result;
        }

        public static void Invoke(Action action, string label = null) => Invoke<object>(() => { action(); return null; }, label: label ?? Describe(action));

        /// <summary>Fire and forget on the main thread.</summary>
        public static void Post(Action action)
        {
            if (_receiver == null) { action(); return; }
            Queue.Enqueue(new WorkItem(() => { action(); return null; }, Describe(action)));
            PostMessage(_receiver.Handle, WM_RUN, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>"PlsqlIde.GetContext" for a lambda inside PlsqlIde.GetContext (compiler names look like "&lt;GetContext&gt;b__12_0").</summary>
        private static string Describe(Delegate d)
        {
            try
            {
                var m = d.Method;
                var name = m.Name;
                int lt = name.IndexOf('<'), gt = name.IndexOf('>');
                if (lt >= 0 && gt > lt + 1) name = name.Substring(lt + 1, gt - lt - 1);
                var type = m.DeclaringType;
                while (type != null && type.Name.StartsWith("<")) type = type.DeclaringType; // closure classes
                return (type?.Name ?? "?") + "." + name;
            }
            catch { return "?"; }
        }

        private static void Drain()
        {
            var batch = Stopwatch.StartNew();
            while (Queue.TryDequeue(out var item))
            {
                var sw = Stopwatch.StartNew();
                _current = item.Label;
                try { item.Result = item.Func(); }
                catch (Exception ex) { item.Error = ex; Util.Log.Error("Main thread work item failed", ex); }
                finally
                {
                    _current = null;
                    item.Done.Set();
                }
                if (sw.ElapsedMilliseconds >= LogSlowItemMs)
                    Util.Log.Info($"PL/SQL Developer's main thread was busy for {sw.ElapsedMilliseconds} ms with plug-in work: {item.Label}");
                if (batch.ElapsedMilliseconds >= YieldAfterMs && !Queue.IsEmpty)
                {
                    // let the IDE handle input and painting first (WM_TIMER comes after them)
                    _deferred = true;
                    SetTimer(_receiver.Handle, ResumeTimer, 15, IntPtr.Zero);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------ watchdog

        private static int _watchers;
        private static Thread _watchdog;

        /// <summary>
        /// While the agent works, a background thread checks every second that the main thread answers and logs stalls of
        /// more than two seconds with what the main thread was doing (plug-in work or PL/SQL Developer itself). Dispose
        /// the result when the work is done.
        /// </summary>
        public static IDisposable Watch()
        {
            if (_receiver == null) return new Watcher();
            Interlocked.Increment(ref _watchers);
            lock (Queue)
            {
                if (_watchdog == null)
                {
                    _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "PL/Pilot main thread watchdog" };
                    _watchdog.Start();
                }
            }
            return new Watcher(true);
        }

        private sealed class Watcher : IDisposable
        {
            private int _active;
            public Watcher(bool active = false) { _active = active ? 1 : 0; }
            public void Dispose() { if (Interlocked.Exchange(ref _active, 0) == 1) Interlocked.Decrement(ref _watchers); }
        }

        private static void WatchdogLoop()
        {
            while (_receiver != null)
            {
                Thread.Sleep(1000);
                if (Volatile.Read(ref _watchers) <= 0) continue;
                var item = new WorkItem(() => null, "(watchdog)");
                var sw = Stopwatch.StartNew();
                Queue.Enqueue(item);
                if (_receiver == null || !PostMessage(_receiver.Handle, WM_RUN, IntPtr.Zero, IntPtr.Zero)) continue;
                if (item.Done.Wait(2000)) continue;
                // stalled: note what is running now, then wait for the end of the stall
                var during = _current;
                item.Done.Wait(10 * 60 * 1000);
                Util.Log.Warn($"PL/SQL Developer's main thread did not respond for {sw.Elapsed.TotalSeconds:0.0} s; " +
                              (during != null ? "plug-in work on it: " + during : "no plug-in work was running on it (PL/SQL Developer itself was busy)"));
            }
        }

        private sealed class WorkItem
        {
            public readonly Func<object> Func;
            public readonly string Label;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public object Result;
            public Exception Error;
            public WorkItem(Func<object> func, string label) { Func = func; Label = label; }
        }

        private sealed class Receiver : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_RUN)
                {
                    if (!_deferred)
                        using (Fpu.Mask()) Drain();
                    return;
                }
                if (m.Msg == WM_TIMER && m.WParam == ResumeTimer)
                {
                    KillTimer(Handle, ResumeTimer);
                    _deferred = false;
                    using (Fpu.Mask()) Drain();
                    return;
                }
                base.WndProc(ref m);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint elapseMs, IntPtr timerProc);

        [DllImport("user32.dll")]
        private static extern bool KillTimer(IntPtr hWnd, IntPtr id);
    }
}
