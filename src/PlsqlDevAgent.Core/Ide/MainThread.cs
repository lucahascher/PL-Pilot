using System;
using System.Collections.Concurrent;
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
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        private static Receiver _receiver;
        private static int _threadId;
        private static readonly ConcurrentQueue<WorkItem> Queue = new ConcurrentQueue<WorkItem>();

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

        public static T Invoke<T>(Func<T> func, int timeoutMs = 10 * 60 * 1000)
        {
            if (IsMainThread || _receiver == null) return func();

            var item = new WorkItem(() => func());
            Queue.Enqueue(item);
            if (!PostMessage(_receiver.Handle, WM_RUN, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("Could not post to the IDE main thread.");
            if (!item.Done.Wait(timeoutMs))
                throw new TimeoutException("The IDE did not respond (it may be busy with a modal dialog or a running query).");
            if (item.Error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(item.Error).Throw();
            return (T)item.Result;
        }

        public static void Invoke(Action action) => Invoke<object>(() => { action(); return null; });

        /// <summary>Fire and forget on the main thread.</summary>
        public static void Post(Action action)
        {
            if (_receiver == null) { action(); return; }
            Queue.Enqueue(new WorkItem(() => { action(); return null; }));
            PostMessage(_receiver.Handle, WM_RUN, IntPtr.Zero, IntPtr.Zero);
        }

        private static void Drain()
        {
            while (Queue.TryDequeue(out var item))
            {
                try { item.Result = item.Func(); }
                catch (Exception ex) { item.Error = ex; Util.Log.Error("Main thread work item failed", ex); }
                finally { item.Done.Set(); }
            }
        }

        private sealed class WorkItem
        {
            public readonly Func<object> Func;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public object Result;
            public Exception Error;
            public WorkItem(Func<object> func) { Func = func; }
        }

        private sealed class Receiver : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_RUN)
                {
                    using (Fpu.Mask()) Drain();
                    return;
                }
                base.WndProc(ref m);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
