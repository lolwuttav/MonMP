#if MONMP_PERFORMANCE_CAPTURE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

namespace MonMP.Plugin.Runtime
{
    // No profiler connection, enable switch, native setting or GPU query. The
    // player decides which counters exist; absent markers stay unavailable.
    internal sealed class CaptureNativeSource : ICaptureNativeProbe, ICaptureNativeDiagnostics
    {
        private sealed class CounterState
        {
            internal CaptureNativeCounter Definition;
            internal ProfilerRecorder Recorder;
            internal bool Created;
            internal string Category = "unknown";
            internal long Restarts, Failures;
            internal CounterState(string name, string unit) { Definition = new CaptureNativeCounter(name, unit, "metricNotFound"); }
        }
        private readonly CounterState[] counters = {
            new CounterState("Main Thread", "TimeNanoseconds"), new CounterState("Render Thread", "TimeNanoseconds"),
            new CounterState("Physics.Simulate", "TimeNanoseconds"), new CounterState("WaitForTargetFPS", "TimeNanoseconds"),
            new CounterState("Gfx.WaitForPresentOnGfxThread", "TimeNanoseconds"), new CounterState("Draw Calls Count", "Count"),
            new CounterState("Triangles Count", "Count"), new CounterState("GC Allocated In Frame", "Bytes") };
        private Process? process;
        private bool disposed;
        public string CpuUnavailable { get; private set; } = "none";
        public int CounterCount => counters.Length;
        public CaptureNativeCounter Counter(int index) => counters[index].Definition;
        internal CaptureNativeSource()
        {
            try { process = Process.GetCurrentProcess(); }
            catch (Exception) { CpuUnavailable = "processUnavailable"; }
            try
            {
                var available = new List<ProfilerRecorderHandle>();
                ProfilerRecorderHandle.GetAvailable(available);
                foreach (var handle in available)
                {
                    var description = ProfilerRecorderHandle.GetDescription(handle);
                    foreach (var counter in counters)
                    {
                        if (counter.Created || counter.Definition.Name != description.Name) continue;
                        counter.Category = description.Category.Name;
                        if (description.UnitType.ToString() != counter.Definition.Unit)
                        { counter.Definition = new CaptureNativeCounter(counter.Definition.Name, counter.Definition.Unit, "unitMismatch"); continue; }
                        try
                        {
                            counter.Recorder = ProfilerRecorder.StartNew(description.Category, counter.Definition.Name, 4, ProfilerRecorderOptions.StartImmediately |
                                ProfilerRecorderOptions.WrapAroundWhenCapacityReached | ProfilerRecorderOptions.SumAllSamplesInFrame);
                            counter.Created = counter.Recorder.Valid;
                            counter.Definition = new CaptureNativeCounter(counter.Definition.Name, counter.Definition.Unit, counter.Created ? "none" : "invalidRecorder");
                            if (!counter.Created) counter.Recorder.Dispose();
                        }
                        catch (Exception)
                        {
                            counter.Failures++;
                            try { counter.Recorder.Dispose(); } catch (Exception) { }
                            counter.Definition = new CaptureNativeCounter(counter.Definition.Name, counter.Definition.Unit, "recorderFailed");
                        }
                    }
                }
            }
            catch (Exception)
            {
                foreach (var counter in counters)
                    if (!counter.Created) counter.Definition = new CaptureNativeCounter(counter.Definition.Name, counter.Definition.Unit, "discoveryFailed");
            }
        }
        public double ReadProcessCpuMilliseconds()
        {
            if (disposed || process == null) return -1;
            try { process.Refresh(); return process.TotalProcessorTime.TotalMilliseconds; }
            catch (Exception) { CpuUnavailable = "processQueryFailed"; return -1; }
        }
        public CaptureNativeReading ReadCounter(int index)
        {
            var counter = counters[index];
            if (disposed || !counter.Created) return default;
            try
            {
                if (!counter.Recorder.Valid || !counter.Recorder.IsRunning || counter.Recorder.Count == 0) return default;
                // Continuous recording is required for frame-spanning markers.
                // Resetting/draining with reset=true during Update can stop the
                // recorder or cut a Main Thread marker before the frame ends.
                long value = counter.Recorder.LastValue;
                return value < 0 ? new CaptureNativeReading(-1, -1, 1) : new CaptureNativeReading(value, value, 1);
            }
            catch (Exception) { counter.Failures++; Retire(counter); return new CaptureNativeReading(-1, -1, -1); }
        }
        public void ResetCounters()
        {
            foreach (var counter in counters)
                if (counter.Created)
                {
                    // Reset can stop collection. Explicitly restart only at a
                    // context/window boundary; never reset per-frame readings.
                    try { counter.Recorder.Reset(); counter.Recorder.Start(); counter.Restarts++; }
                    catch (Exception) { counter.Failures++; Retire(counter); }
                }
        }
        public CaptureNativeStatus Status(int index)
        {
            var counter = counters[index];
            if (!counter.Created)
                return new CaptureNativeStatus(counter.Category, counter.Failures > 0 ? "retired-after-failure" : counter.Definition.Unavailable,
                    false, false, 0, 0, 0, counter.Restarts, counter.Failures);
            try
            {
                bool running = counter.Recorder.IsRunning; int count = counter.Recorder.Count;
                return new CaptureNativeStatus(counter.Category, running ? count > 0 ? "running-buffered" : "running-empty" : "stopped",
                    true, running, count, counter.Recorder.Capacity, counter.Recorder.LastValue, counter.Restarts, counter.Failures);
            }
            catch (Exception)
            {
                counter.Failures++; Retire(counter);
                return new CaptureNativeStatus(counter.Category, "diagnosticFailed", false, false, 0, 0, 0, counter.Restarts, counter.Failures);
            }
        }
        private static void Retire(CounterState counter)
        {
            counter.Created = false;
            try { counter.Recorder.Dispose(); } catch (Exception) { }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (var counter in counters)
                if (counter.Created) Retire(counter);
            try { process?.Dispose(); } catch (Exception) { }
            process = null;
        }
    }
}
#endif
