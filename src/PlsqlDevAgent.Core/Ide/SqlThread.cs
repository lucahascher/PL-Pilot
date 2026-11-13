using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using PlsqlDevAgent.Native;

namespace PlsqlDevAgent.Ide
{
    /// <summary>
    /// Cancellation of the agent request that is currently running, available to IDE calls without passing it
    /// through every tool (flows with async calls). When the user stops the agent, database calls stop waiting.
    /// </summary>
    public static class IdeCancellation
    {
        private static readonly AsyncLocal<CancellationToken> Ambient = new AsyncLocal<CancellationToken>();

        public static CancellationToken Current
        {
            get => Ambient.Value;
            set => Ambient.Value = value;
        }
    }

    /// <summary>
    /// One background thread that executes the agent's SQL on the plug-in's own Oracle session, so a long running
    /// statement does not block PL/SQL Developer's main thread (the IDE and the chat window stay usable).
    /// The SQL_* callbacks share one cursor per plug-in, so all statements run strictly one after the other here.
    /// A statement cannot be interrupted once it runs in the database: when the caller stops waiting (Stop button,
    /// timeout), it finishes in the background and the next statement waits for it.
    /// </summary>
    internal static class SqlThread
    {
        private static readonly BlockingCollection<WorkItem> Queue = new BlockingCollection<WorkItem>();
        private static readonly object StartLock = new object();
        private static Thread _thread;
        private static volatile WorkItem _running;

        public static bool IsSqlThread => _thread != null && Thread.CurrentThread == _thread;

        /// <summary>A statement is executing (possibly one nobody waits for any more).</summary>
        public static bool IsBusy => _running != null;

        /// <summary>Runs <paramref name="func"/> on the SQL thread and waits for it (cancellable).</summary>
        public static T Run<T>(string label, Func<T> func, CancellationToken ct, TimeSpan timeout)
        {
            if (IsSqlThread) return func();
            ct.ThrowIfCancellationRequested();
            EnsureStarted();

            var item = new WorkItem(label, () => func());
            Queue.Add(item);
            bool done;
            try
            {
                done = item.Done.Wait(timeout, ct);
            }
            catch (OperationCanceledException)
            {
                item.Abandon("cancelled");
                throw;
            }
            if (!done)
            {
                item.Abandon("timed out");
                throw new TimeoutException($"The database did not answer within {timeout.TotalMinutes:0} minutes; the statement is still running in the agent's session.");
            }
            if (item.Error != null) ExceptionDispatchInfo.Capture(item.Error).Throw();
            return (T)item.Result;
        }

        /// <summary>Waits until statements queued before this call have finished.</summary>
        public static void WaitIdle(CancellationToken ct, TimeSpan timeout) => Run<object>("(wait)", () => null, ct, timeout);

        private static void EnsureStarted()
        {
            if (_thread != null) return;
            lock (StartLock)
            {
                if (_thread != null) return;
                var t = new Thread(Loop) { IsBackground = true, Name = "PL/Pilot SQL" };
                t.Start();
                _thread = t;
            }
        }

        private static void Loop()
        {
            foreach (var item in Queue.GetConsumingEnumerable())
            {
                if (!item.TryStart()) continue; // the caller gave up before it started: skip
                _running = item;
                var sw = Stopwatch.StartNew();
                try
                {
                    // Delphi code may change the floating point control word; keep .NET's (masked) state
                    using (Fpu.Mask()) item.Result = item.Func();
                }
                catch (Exception ex)
                {
                    item.Error = ex;
                }
                finally
                {
                    _running = null;
                    if (item.AbandonReason != null)
                        Util.Log.Info($"SQL the agent stopped waiting for ({item.AbandonReason}) finished after {sw.Elapsed.TotalSeconds:0.0} s: {item.Label}");
                    item.Done.Set();
                }
            }
        }

        private sealed class WorkItem
        {
            private const int Queued = 0, Started = 1, Skipped = 2;
            private int _state;

            public readonly string Label;
            public readonly Func<object> Func;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public object Result;
            public Exception Error;
            public volatile string AbandonReason;

            public WorkItem(string label, Func<object> func) { Label = label; Func = func; }

            public bool TryStart() => Interlocked.CompareExchange(ref _state, Started, Queued) == Queued;

            public void Abandon(string reason)
            {
                AbandonReason = reason;
                if (Interlocked.CompareExchange(ref _state, Skipped, Queued) == Started)
                    Util.Log.Info($"Stopped waiting for SQL ({reason}); it keeps running in the agent's session: {Label}");
            }
        }
    }
}
