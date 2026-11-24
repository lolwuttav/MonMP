#if MONMP_PERFORMANCE_CAPTURE
using System;
using System.Globalization;
using System.Text;

namespace MonMP.Plugin.Runtime
{
    internal readonly struct CaptureNativeCounter
    {
        internal readonly string Name, Unit, Unavailable;
        internal CaptureNativeCounter(string name, string unit, string unavailable = "none")
        { Name = name; Unit = unit; Unavailable = unavailable; }
    }
    internal readonly struct CaptureNativeReading
    {
        internal readonly long Total, Maximum;
        internal readonly int Samples;
        internal CaptureNativeReading(long total, long maximum, int samples)
        { Total = total; Maximum = maximum; Samples = samples; }
    }
    // The pure aggregation layer is exercised headlessly; the Unity/OS probe is
    // supplied only by the separate capture build's guarded startup.
    internal interface ICaptureNativeProbe : IDisposable
    {
        int CounterCount { get; }
        CaptureNativeCounter Counter(int index);
        double ReadProcessCpuMilliseconds();
        string CpuUnavailable { get; }
        CaptureNativeReading ReadCounter(int index);
        void ResetCounters();
    }
    internal readonly struct CaptureNativeStatus
    {
        internal readonly string Category, State;
        internal readonly bool Created, Running, Observed;
        internal readonly int Count, Capacity;
        internal readonly long LastValue, Restarts, Failures;
        internal CaptureNativeStatus(string category, string state, bool created, bool running, int count, int capacity,
            long lastValue, long restarts, long failures)
        {
            Category = category; State = state; Created = created; Running = running; Count = count; Capacity = capacity;
            LastValue = lastValue; Restarts = restarts; Failures = failures; Observed = true;
        }
    }
    internal interface ICaptureNativeDiagnostics
    {
        CaptureNativeStatus Status(int index);
    }
    internal sealed class CaptureNativeTotals
    {
        internal sealed class CounterTotal
        {
            internal long Total, Maximum, Samples, Missing, Rejected;
            internal bool Positive;
            internal CaptureNativeStatus Status;
        }
        internal readonly CaptureNativeCounter[] Definitions;
        internal readonly CounterTotal[] Counters;
        internal double CpuMilliseconds, CpuDurationMilliseconds;
        internal long CpuWindows, CpuUnavailableWindows;
        internal string CpuUnavailable = "notConfigured";
        internal CaptureNativeTotals(CaptureNativeCounter[] definitions)
        {
            Definitions = definitions; Counters = new CounterTotal[definitions.Length];
            for (int i = 0; i < Counters.Length; i++) Counters[i] = new CounterTotal();
        }
        internal void Accumulate(CaptureNativeTotals other)
        {
            CpuMilliseconds += other.CpuMilliseconds; CpuDurationMilliseconds += other.CpuDurationMilliseconds;
            CpuWindows += other.CpuWindows; CpuUnavailableWindows += other.CpuUnavailableWindows;
            if (other.CpuUnavailableWindows > 0) CpuUnavailable = other.CpuUnavailable;
            for (int i = 0; i < Counters.Length; i++)
            {
                var target = Counters[i]; var source = other.Counters[i];
                target.Total += source.Total; target.Samples += source.Samples; target.Missing += source.Missing;
                target.Rejected += source.Rejected; target.Maximum = Math.Max(target.Maximum, source.Maximum); target.Positive |= source.Positive;
                target.Status = source.Status;
            }
        }
        internal void Clear()
        {
            CpuMilliseconds = CpuDurationMilliseconds = 0; CpuWindows = CpuUnavailableWindows = 0;
            foreach (var counter in Counters)
            { counter.Total = counter.Maximum = counter.Samples = counter.Missing = counter.Rejected = 0; counter.Positive = false; counter.Status = default; }
        }
        private static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
        internal void Append(StringBuilder report, string prefix, int? contextId = null)
        {
            string identity = contextId.HasValue ? " id=" + contextId.Value : "";
            report.Append(prefix).Append("Process").Append(identity).Append(" available=").Append(CpuWindows > 0)
                .Append(" cpuTotalMs=").Append(CpuWindows > 0 ? Number(CpuMilliseconds) : "unavailable")
                .Append(" cpuSampleDurationSeconds=").Append(Number(CpuDurationMilliseconds / 1000))
                .Append(" equivalentOccupiedCores=").Append(CpuWindows > 0 && CpuDurationMilliseconds > 0 ? Number(CpuMilliseconds / CpuDurationMilliseconds) : "unavailable")
                .Append(" validWindows=").Append(CpuWindows).Append(" unavailableWindows=").Append(CpuUnavailableWindows)
                .Append(" unavailableReason=").Append(CpuUnavailableWindows > 0 ? CpuUnavailable : "none").AppendLine();
            for (int i = 0; i < Counters.Length; i++)
            {
                var definition = Definitions[i]; var value = Counters[i];
                bool available = definition.Unavailable == "none" && value.Positive;
                double scale = definition.Unit == "TimeNanoseconds" ? .000001 : 1;
                report.Append(prefix).Append("Counter").Append(identity).Append(" name=").Append(definition.Name)
                    .Append(" unit=").Append(definition.Unit == "TimeNanoseconds" ? "milliseconds" : definition.Unit)
                    .Append(" available=").Append(available)
                    .Append(" mean=").Append(available && value.Samples > 0 ? Number(value.Total * scale / value.Samples) : "unavailable")
                    .Append(" maximum=").Append(available ? Number(value.Maximum * scale) : "unavailable")
                    .Append(" completedSamples=").Append(value.Samples).Append(" missingObservations=").Append(value.Missing)
                    .Append(" rejectedObservations=").Append(value.Rejected)
                    .Append(" unavailableReason=").Append(definition.Unavailable != "none" ? definition.Unavailable : value.Positive ? "none" : "noPositiveSamples").AppendLine();
                var status = value.Status;
                if (status.Observed)
                    report.Append(prefix).Append("Recorder").Append(identity).Append(" name=").Append(definition.Name)
                        .Append(" category=").Append(status.Category).Append(" status=").Append(status.State)
                        .Append(" created=").Append(status.Created).Append(" isRunning=").Append(status.Running)
                        .Append(" count=").Append(status.Count).Append(" capacity=").Append(status.Capacity)
                        .Append(" lastValueRaw=").Append(status.LastValue).Append(" rawUnit=").Append(definition.Unit)
                        .Append(" lifetimeBoundaryRestarts=").Append(status.Restarts).Append(" lifetimeFailures=").Append(status.Failures).AppendLine();
            }
        }
    }
    internal sealed class CaptureNativeMetrics : IDisposable
    {
        private readonly ICaptureNativeProbe? probe;
        internal readonly CaptureNativeTotals Window;
        private double cpuStart = -1;
        private bool skipCompletedFrame, disposed;
        internal CaptureNativeMetrics(ICaptureNativeProbe? probe)
        {
            this.probe = probe;
            int count = Math.Min(8, Math.Max(0, probe?.CounterCount ?? 0));
            var definitions = new CaptureNativeCounter[count];
            for (int i = 0; i < count; i++) definitions[i] = probe!.Counter(i);
            Window = new CaptureNativeTotals(definitions);
        }
        private double Cpu()
        {
            if (probe == null) { Window.CpuUnavailable = "notConfigured"; return -1; }
            try
            {
                double value = probe.ReadProcessCpuMilliseconds();
                Window.CpuUnavailable = value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value) ? "none" : probe.CpuUnavailable;
                return value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : -1;
            }
            catch (Exception) { Window.CpuUnavailable = "queryFailed"; return -1; }
        }
        internal void Begin()
        { Window.Clear(); cpuStart = Cpu(); ResetCompletedFrames(); }
        internal void Finish(double durationMilliseconds)
        {
            double end = Cpu();
            if (cpuStart >= 0 && end >= cpuStart && durationMilliseconds > 0)
            { Window.CpuMilliseconds = end - cpuStart; Window.CpuDurationMilliseconds = durationMilliseconds; Window.CpuWindows = 1; }
            else
            { Window.CpuUnavailableWindows = 1; if (Window.CpuUnavailable == "none") Window.CpuUnavailable = "invalidBoundarySample"; }
            cpuStart = end;
            if (probe is ICaptureNativeDiagnostics diagnostics)
                for (int i = 0; i < Window.Counters.Length; i++)
                    try { Window.Counters[i].Status = diagnostics.Status(i); }
                    catch (Exception) { Window.Counters[i].Status = new CaptureNativeStatus("unknown", "diagnosticFailed", false, false, 0, 0, 0, 0, 1); }
        }
        internal void NextWindow()
        { Window.Clear(); ResetCompletedFrames(); }
        private void ResetCompletedFrames()
        {
            skipCompletedFrame = true;
            try { probe?.ResetCounters(); }
            catch (Exception) { }
        }
        internal void Sample()
        {
            if (probe == null || disposed) return;
            // Recorders publish completed frames. Drop the first observation
            // after a phase boundary as well as the already-buffered samples.
            if (skipCompletedFrame) { skipCompletedFrame = false; try { probe.ResetCounters(); } catch (Exception) { } return; }
            for (int i = 0; i < Window.Counters.Length; i++)
            {
                var total = Window.Counters[i]; var definition = Window.Definitions[i];
                if (definition.Unavailable != "none") { total.Missing++; continue; }
                try
                {
                    var reading = probe.ReadCounter(i);
                    if (reading.Samples == 0) { total.Missing++; continue; }
                    if (reading.Samples < 0 || reading.Total < 0 || reading.Maximum < 0 || reading.Maximum > reading.Total ||
                        definition.Unit != "TimeNanoseconds" && definition.Unit != "Count" && definition.Unit != "Bytes")
                    { total.Rejected++; continue; }
                    total.Total += reading.Total; total.Samples += reading.Samples; total.Maximum = Math.Max(total.Maximum, reading.Maximum);
                    total.Positive |= reading.Total > 0;
                }
                catch (Exception) { total.Rejected++; }
            }
        }
        public void Dispose()
        { if (disposed) return; disposed = true; try { probe?.Dispose(); } catch (Exception) { } }
    }
}
#endif
