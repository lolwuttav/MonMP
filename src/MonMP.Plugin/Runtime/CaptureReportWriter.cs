#if MONMP_PERFORMANCE_CAPTURE
using System;
using System.Threading;

namespace MonMP.Plugin.Runtime
{
    // At most one active write and one replaceable pending report. No game/native
    // objects cross the worker boundary and diagnostic IO never ends a session.
    internal sealed class CaptureReportWriter
    {
        private readonly Action<string, string> write;
        private readonly object gate = new object();
        private readonly ManualResetEventSlim idle = new ManualResetEventSlim(true);
        private (string Path, string Text)? pending;
        private bool active;
        private long completed, replaced, failures;
        internal long Completed => Interlocked.Read(ref completed);
        internal long Replaced => Interlocked.Read(ref replaced);
        internal long Failures => Interlocked.Read(ref failures);
        internal CaptureReportWriter(Action<string, string> write) { this.write = write; }
        internal void Queue(string path, string text)
        {
            lock (gate)
            {
                if (pending.HasValue) Interlocked.Increment(ref replaced);
                pending = (path, text);
                if (active) return;
                active = true; idle.Reset();
                ThreadPool.QueueUserWorkItem(_ => Drain());
            }
        }
        private void Drain()
        {
            while (true)
            {
                (string Path, string Text) report;
                lock (gate)
                {
                    if (!pending.HasValue) { active = false; idle.Set(); return; }
                    report = pending.Value; pending = null;
                }
                try { write(report.Path, report.Text); Interlocked.Increment(ref completed); }
                catch (Exception) { Interlocked.Increment(ref failures); }
            }
        }
        internal bool WaitForIdle(int milliseconds) => idle.Wait(milliseconds);
    }
}
#endif
