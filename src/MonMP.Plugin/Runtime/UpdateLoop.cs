using System;
using System.Collections.Generic;

namespace MonMP.Plugin.Runtime
{
    // Preserve ordinary adapter exception containment without the development
    // profiler, clocks, frame samples, report files or background report worker.
    internal static class UpdateLoop
    {
        private static readonly HashSet<string> loggedErrors = new HashSet<string>(StringComparer.Ordinal);
        internal static bool Strict => false;
        internal static Action<string>? Log;
#if MONMP_PERFORMANCE_CAPTURE
        internal static PerformanceCapture? Capture;
        internal static void Measure(string name, Action update)
        {
            var capture = Capture;
            if (capture == null) { update(); return; }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { update(); }
            finally
            {
                try { capture.Record(name, System.Diagnostics.Stopwatch.GetTimestamp() - start); }
                catch (Exception) { Capture = null; }
            }
        }
#else
        internal static void Measure(string name, Action update) => update();
#endif
        internal static void Run(string name, Action update)
        {
#if MONMP_PERFORMANCE_CAPTURE
            var capture = Capture;
            long start = capture != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
#endif
            try { update(); }
            catch (Exception ex)
            {
                if (loggedErrors.Add(name + "|" + ex.GetType().Name + ": " + ex.Message))
                    Log?.Invoke("MonMP " + name + " update failed; the session continues without this step: " + ex);
            }
#if MONMP_PERFORMANCE_CAPTURE
            }
            finally
            {
                try { if (capture != null) capture.Record(name, System.Diagnostics.Stopwatch.GetTimestamp() - start); }
                catch (Exception) { Capture = null; }
            }
#endif
        }
    }
}
