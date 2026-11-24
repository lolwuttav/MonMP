#if MONMP_PERFORMANCE_CAPTURE
using System;

namespace MonMP.Plugin.Runtime
{
    // A value-type detail scope: no delegate or boxing allocation per sample.
    // Its elapsed time is reported separately from top-level adapter totals.
    internal static class CaptureDetail
    {
        internal static Scope Measure(string name)
        {
            var capture = UpdateLoop.Capture;
            if (capture == null) return default;
            try { return new Scope(capture, name, capture.Timestamp()); }
            catch (Exception) { UpdateLoop.Capture = null; return default; }
        }
        internal readonly struct Scope : IDisposable
        {
            private readonly PerformanceCapture? capture;
            private readonly string? name;
            private readonly long started;
            internal Scope(PerformanceCapture capture, string name, long started)
            { this.capture = capture; this.name = name; this.started = started; }
            public void Dispose()
            {
                if (capture == null) return;
                try { capture.RecordDetail(name!, capture.Timestamp() - started); }
                catch (Exception) { UpdateLoop.Capture = null; }
            }
        }
    }
}
#endif
