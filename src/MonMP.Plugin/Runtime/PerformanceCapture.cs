#if MONMP_PERFORMANCE_CAPTURE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MonMP.Plugin.Runtime
{
    // Separate capture build only. Ordinary player builds do not compile this
    // type, its clocks, buffers, frame sampling, report strings or worker.
    internal sealed class PerformanceCapture : IDisposable
    {
        private sealed class Scope { internal long Ticks, Worst, Calls; }
        private sealed class ContextSummary
        {
            internal int Id, Windows;
            internal long Frames, FirstWindow, LastWindow, Gc0, Gc1, Gc2, Dropped, ScheduledRenders, SkippedRenders;
            internal double DurationTicks, DeltaSeconds, UpdateTicks, ScopeTicks, WorstDelta, FirstElapsed, LastElapsed;
            internal string Representative = "";
            internal bool RepresentativeSettled;
            internal CaptureNativeTotals Native = null!;
            internal readonly Dictionary<string, Scope> Scopes = new Dictionary<string, Scope>(StringComparer.Ordinal);
            internal readonly Dictionary<string, Scope> Details = new Dictionary<string, Scope>(StringComparer.Ordinal);
        }
        private readonly Dictionary<string, Scope> scopes = new Dictionary<string, Scope>(StringComparer.Ordinal);
        private readonly Dictionary<string, Scope> details = new Dictionary<string, Scope>(StringComparer.Ordinal);
        private readonly Dictionary<(string Role, int Guests, bool Focused, bool Paused, bool Loaded, int Target, int Vsync, int Width, int Height, string Synchronization, int SynchronizedGuests, string Experiment, int RenderInterval), ContextSummary> contexts =
            new Dictionary<(string, int, bool, bool, bool, int, int, int, int, string, int, string, int), ContextSummary>();
        private readonly Queue<string> history = new Queue<string>();
        // Per-frame attribution of long frames: which scopes ran in the update before a
        // long interval. Strings are built only for qualifying frames; buffers are reused.
        private Dictionary<string, long> frameScopes = new Dictionary<string, long>(StringComparer.Ordinal);
        private Dictionary<string, long> previousFrameScopes = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Queue<string> hitchFrames = new Queue<string>();
        private long frameScopeTicks, previousFrameScopeTicks, hitchFrameCount;
        private int previousFrameGc;
        private double previousFrameElapsed;
        private readonly int[] histogram = new int[4097];
        private readonly string path, build;
        private readonly Func<long> clock;
        private readonly long frequency;
        private readonly CaptureReportWriter writer;
        private readonly CaptureNativeMetrics nativeMetrics;
        private readonly Func<DateTime> utcClock;
        private CapturePlayerLoop? playerLoop;
        private string role = "";
        private string synchronization = "unknown";
        private string experiment = "normal";
        private int synchronizedGuests = -1;
        private int guests, frames, dropped, gc0, gc1, gc2, targetFrameRate, vSyncCount, width, height, unretainedContextWindows;
        private bool focused, paused, worldLoaded, initialized, disposed;
        private long started, frameStart, origin, windowId, generation, scheduledRenders, skippedRenders;
        private int renderFrameInterval = 1;
        private readonly DateTime captureStartedUtc;
        private DateTime firstFrameUtc;
        private double deltaSum, worstDelta, updateTicks;
        internal PerformanceCapture(string path, string build, Action<string, string>? write = null,
            Func<long>? clock = null, long frequency = 0, ICaptureNativeProbe? native = null, Func<DateTime>? utcClock = null)
        {
            this.path = path; this.build = build;
            this.clock = clock ?? Stopwatch.GetTimestamp;
            this.frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
            this.utcClock = utcClock ?? (() => DateTime.UtcNow);
            captureStartedUtc = this.utcClock();
            nativeMetrics = new CaptureNativeMetrics(native);
            writer = new CaptureReportWriter(write ?? File.WriteAllText);
        }
        internal void InstallPlayerLoop(Func<UnityEngine.LowLevel.PlayerLoopSystem>? read = null,
            Action<UnityEngine.LowLevel.PlayerLoopSystem>? write = null, Func<int>? frame = null)
        { if (!disposed && playerLoop == null) playerLoop = CapturePlayerLoop.TryInstall(this, read, write, frame); }
        internal long CaptureWindowGeneration => generation;
        internal bool PlayerLoopSamplingReady => initialized && !disposed;
        internal void SetRenderingSettings(int targetFrameRate, int vSyncCount, int width, int height, int renderFrameInterval = 1)
        {
            renderFrameInterval = Math.Max(1, renderFrameInterval);
            if (disposed || this.targetFrameRate == targetFrameRate && this.vSyncCount == vSyncCount && this.width == width && this.height == height && this.renderFrameInterval == renderFrameInterval) return;
            if (initialized) CloseWindow(clock());
            this.targetFrameRate = targetFrameRate; this.vSyncCount = vSyncCount; this.width = width; this.height = height;
            this.renderFrameInterval = renderFrameInterval;
        }
        internal void ObserveScheduledRender(bool willRender)
        { if (disposed || !initialized) return; if (willRender) scheduledRenders++; else skippedRenders++; }
        internal void SetExperiment(string experiment)
        {
            // Fixed diagnostic labels only; keep reports bounded and single-line.
            if (string.IsNullOrEmpty(experiment) || experiment.Length > 32) throw new ArgumentException("A capture experiment needs a short label.", nameof(experiment));
            foreach (char character in experiment)
                if (!char.IsLetterOrDigit(character) && character != '-' && character != '_') throw new ArgumentException("Capture labels must be letters, digits, hyphens or underscores.", nameof(experiment));
            if (disposed || this.experiment == experiment) return;
            if (initialized) CloseWindow(clock());
            this.experiment = experiment;
        }
        internal void BeginFrame(string role, int guests, bool focused, bool paused, bool worldLoaded = false,
            string synchronization = "unknown", int synchronizedGuests = -1)
        {
            if (disposed) return;
            long now = clock();
            if (initialized && (this.role != role || this.guests != guests || this.focused != focused || this.paused != paused || this.worldLoaded != worldLoaded ||
                this.synchronization != synchronization || this.synchronizedGuests != synchronizedGuests))
                CloseWindow(now);
            if (!initialized)
            {
                initialized = true; origin = started = now;
                firstFrameUtc = utcClock(); generation++;
                gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
                nativeMetrics.Begin();
            }
            this.role = role; this.guests = guests; this.focused = focused; this.paused = paused;
            this.worldLoaded = worldLoaded;
            this.synchronization = synchronization; this.synchronizedGuests = synchronizedGuests;
            nativeMetrics.Sample();
            frameStart = now;
        }
        internal void Record(string name, long ticks)
        {
            Record(scopes, name, ticks);
            if (!disposed && ticks > 0) { frameScopeTicks += ticks; AddFrameTicks(name, ticks); }
        }
        internal void RecordDetail(string name, long ticks)
        {
            Record(details, name, ticks);
            if (!disposed && ticks > 0) AddFrameTicks(name, ticks);
        }
        private void AddFrameTicks(string name, long ticks)
        {
            if (frameScopes.TryGetValue(name, out long total)) frameScopes[name] = total + ticks;
            else if (frameScopes.Count < 256) frameScopes.Add(name, ticks);
        }
        // Unity's delta at frame N is the interval that ended with frame N, so it
        // measures the update recorded at N-1. MonMP scopes run after BeginFrame.
        private void AttributeFrame(long now, float deltaTime)
        {
            double deltaMs = deltaTime * 1000, scopeMs = Milliseconds(previousFrameScopeTicks);
            if (previousFrameElapsed > 0 && (scopeMs >= 3 || deltaMs >= 30))
            {
                hitchFrameCount++;
                var line = new StringBuilder("hitchFrame elapsedSeconds=").Append(Number(previousFrameElapsed))
                    .Append(" intervalMs=").Append(Number(deltaMs)).Append(" scopeSumMs=").Append(Number(scopeMs))
                    .Append(" gc=").Append(GC.CollectionCount(0) - previousFrameGc).Append(" top=");
                foreach (var entry in previousFrameScopes.Where(pair => Milliseconds(pair.Value) >= 0.25).OrderByDescending(pair => pair.Value).Take(10))
                    line.Append(entry.Key).Append(':').Append(Milliseconds(entry.Value).ToString("F2", CultureInfo.InvariantCulture)).Append(',');
                hitchFrames.Enqueue(line.ToString()); while (hitchFrames.Count > 600) hitchFrames.Dequeue();
            }
            var swap = previousFrameScopes; previousFrameScopes = frameScopes; frameScopes = swap; frameScopes.Clear();
            previousFrameScopeTicks = frameScopeTicks; frameScopeTicks = 0;
            previousFrameElapsed = (now - origin) / (double)frequency; previousFrameGc = GC.CollectionCount(0);
        }
        internal long Timestamp() => clock();
        private void Record(Dictionary<string, Scope> table, string name, long ticks)
        {
            if (disposed || ticks < 0) return;
            if (!table.TryGetValue(name, out var scope))
            {
                if (table.Count >= 128 || name.Length > 64) { dropped++; return; }
                table.Add(name, scope = new Scope());
            }
            scope.Ticks += ticks; scope.Calls++;
            if (ticks > scope.Worst) scope.Worst = ticks;
        }
        internal void EndFrame(float deltaTime)
        {
            if (disposed || !initialized) return;
            long now = clock();
            if (deltaTime > 0 && !float.IsNaN(deltaTime) && !float.IsInfinity(deltaTime))
            {
                frames++; deltaSum += deltaTime;
                if (deltaTime > worstDelta) worstDelta = deltaTime;
                int bin = Math.Min(4096, Math.Max(0, (int)Math.Ceiling(Math.Min(1024, deltaTime * 1000) * 4) - 1));
                histogram[bin]++;
                updateTicks += Math.Max(0, now - frameStart);
                AttributeFrame(now, deltaTime);
            }
            if (now - started >= frequency * 10) CloseWindow(now);
        }
        private static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
        private double Milliseconds(double ticks) => ticks * 1000 / frequency;
        private DateTime UtcAt(long ticks) => firstFrameUtc.AddTicks((long)((ticks - origin) * (double)TimeSpan.TicksPerSecond / frequency));
        private double Percentile(double fraction)
        {
            int rank = Math.Max(1, (int)Math.Ceiling(frames * fraction)), seen = 0;
            for (int i = 0; i < histogram.Length; i++)
            { seen += histogram[i]; if (seen >= rank) return i == 4096 ? worstDelta * 1000 : (i + 1) * .25; }
            return 0;
        }
        private void CloseWindow(long now)
        {
            generation++;
            nativeMetrics.Finish((now - started) * 1000.0 / frequency);
            if (frames > 0)
            {
                double scopeTotal = scopes.Values.Sum(scope => Milliseconds(scope.Ticks));
                long id = ++windowId;
                double elapsedStart = (started - origin) / (double)frequency, elapsedEnd = (now - origin) / (double)frequency;
                int windowGc0 = GC.CollectionCount(0) - gc0, windowGc1 = GC.CollectionCount(1) - gc1, windowGc2 = GC.CollectionCount(2) - gc2;
                var text = new StringBuilder();
                text.Append("phase=").Append(role).Append(" guests=").Append(guests).Append(" focused=").Append(focused)
                    .Append(" paused=").Append(paused).Append(" durationSeconds=").Append(Number((now - started) / (double)frequency))
                    .Append(" frames=").Append(frames).Append(" worldLoaded=").Append(worldLoaded)
                    .Append(" windowId=").Append(id).Append(" elapsedStartSeconds=").Append(Number(elapsedStart))
                    .Append(" elapsedEndSeconds=").Append(Number(elapsedEnd))
                    .Append(" synchronization=").Append(synchronization).Append(" synchronizedGuests=").Append(synchronizedGuests)
                    .Append(" experiment=").Append(experiment)
                    .Append(" utcStart=").Append(UtcAt(started).ToString("o", CultureInfo.InvariantCulture))
                    .Append(" utcEnd=").Append(UtcAt(now).ToString("o", CultureInfo.InvariantCulture)).AppendLine();
                text.Append("renderSettings targetFrameRate=").Append(targetFrameRate).Append(" vSyncCount=").Append(vSyncCount)
                    .Append(" width=").Append(width).Append(" height=").Append(height).Append(" renderFrameInterval=").Append(renderFrameInterval).AppendLine();
                text.Append("renderSchedule scheduledRenderUpdates=").Append(scheduledRenders).Append(" skippedRenderUpdates=").Append(skippedRenders).AppendLine();
                text.Append("wholeFrameMeanMs=").Append(Number(deltaSum * 1000 / frames)).Append(" estimatedFPS=").Append(Number(frames / deltaSum))
                    .Append(" p50Ms=").Append(Number(Percentile(.5))).Append(" p95Ms=").Append(Number(Percentile(.95)))
                    .Append(" worstMs=").Append(Number(worstDelta * 1000)).AppendLine();
                text.Append("pluginUpdateSpanMeanMs=").Append(Number(Milliseconds(updateTicks) / frames))
                    .Append(" measuredScopeSumPerFrameMs=").Append(Number(scopeTotal / frames))
                    .Append(" frameMinusScopesEstimateMs=").Append(Number(Math.Max(0, deltaSum * 1000 - scopeTotal) / frames)).AppendLine();
                text.Append("GC collections=").Append(windowGc0).Append('/')
                    .Append(windowGc1).Append('/').Append(windowGc2)
                    .Append(" droppedScopeSamples=").Append(dropped).AppendLine();
                foreach (var entry in scopes.OrderByDescending(pair => pair.Value.Ticks))
                    if (entry.Value.Calls != 0) text.Append(entry.Key).Append(" calls=").Append(entry.Value.Calls)
                        .Append(" totalMs=").Append(Number(Milliseconds(entry.Value.Ticks)))
                        .Append(" perFrameMs=").Append(Number(Milliseconds(entry.Value.Ticks) / frames))
                        .Append(" worstCallMs=").Append(Number(Milliseconds(entry.Value.Worst))).AppendLine();
                foreach (var entry in details.OrderByDescending(pair => pair.Value.Ticks))
                    if (entry.Value.Calls != 0) text.Append("detail ").Append(entry.Key).Append(" calls=").Append(entry.Value.Calls)
                        .Append(" totalMs=").Append(Number(Milliseconds(entry.Value.Ticks)))
                        .Append(" perFrameMs=").Append(Number(Milliseconds(entry.Value.Ticks) / frames))
                        .Append(" worstCallMs=").Append(Number(Milliseconds(entry.Value.Worst))).AppendLine();
                nativeMetrics.Window.Append(text, "native");
                string window = text.ToString();
                history.Enqueue(window); while (history.Count > 12) history.Dequeue();
                var key = (role, guests, focused, paused, worldLoaded, targetFrameRate, vSyncCount, width, height, synchronization, synchronizedGuests, experiment, renderFrameInterval);
                if (!contexts.TryGetValue(key, out var summary) && contexts.Count < 32)
                {
                    summary = new ContextSummary { Id = contexts.Count + 1, FirstWindow = id, FirstElapsed = elapsedStart,
                        Native = new CaptureNativeTotals(nativeMetrics.Window.Definitions) };
                    contexts.Add(key, summary);
                }
                if (summary != null)
                {
                    summary.Windows++; summary.Frames += frames; summary.LastWindow = id; summary.LastElapsed = elapsedEnd;
                    summary.DurationTicks += now - started; summary.DeltaSeconds += deltaSum; summary.UpdateTicks += updateTicks;
                    summary.ScopeTicks += scopes.Values.Sum(scope => scope.Ticks);
                    summary.Gc0 += windowGc0; summary.Gc1 += windowGc1; summary.Gc2 += windowGc2; summary.Dropped += dropped;
                    summary.ScheduledRenders += scheduledRenders; summary.SkippedRenders += skippedRenders;
                    summary.WorstDelta = Math.Max(summary.WorstDelta, worstDelta);
                    Accumulate(summary.Scopes, scopes); Accumulate(summary.Details, details);
                    summary.Native.Accumulate(nativeMetrics.Window);
                    bool settled = now - started >= frequency * 5 && frames >= 120;
                    if (summary.Representative.Length == 0 || !summary.RepresentativeSettled && settled)
                    { summary.Representative = window; summary.RepresentativeSettled = settled; }
                }
                else unretainedContextWindows++;
                var report = new StringBuilder("MONMP PERFORMANCE CAPTURE BUILD; not a player release\nversion=").Append(build).AppendLine();
                report.Append("captureStartedUtc=").Append(captureStartedUtc.ToString("o", CultureInfo.InvariantCulture)).AppendLine();
                report.Append("firstFrameUtc=").Append(firstFrameUtc.ToString("o", CultureInfo.InvariantCulture)).AppendLine();
                report.AppendLine("Window utcStart/utcEnd use monotonic elapsed time calibrated at firstFrameUtc; captureStartedUtc is constructor time, not the elapsed-time origin. renderSchedule counts engine will-render decisions per observed Update, not completed GPU frames.");
                report.AppendLine("Main-thread elapsed scopes, not a CPU/GPU profiler. Nested detail rows are EXCLUDED from measuredScopeSumPerFrameMs and context scope totals.");
                report.AppendLine("Detail scopes may nest/overlap (for example LateUpdate.Total and its children); do not sum detail rows.");
                report.AppendLine("Loop.* details are inclusive main-thread elapsed brackets around retained current PlayerLoop nodes; native waits, scripts and other mods can contribute. Parent/child loop phases overlap. Missing, reentrant, frame-changing or cross-window samples are discarded; installation status and discard counters are lifetime snapshots.");
                report.AppendLine("Frame-minus-scopes is an estimate: native work, rendering/GPU wait, unscoped MonMP, GC and scheduling may contribute.");
                report.AppendLine("Whole-frame Unity delta describes the preceding frame interval: the first delta after a context boundary may include the previous phase's stall. Synchronization labels separate native load/channel/profile/activation; synchronizedGuests counts channel acknowledgements, not guest input activation.");
                report.AppendLine("Native counters are separate, potentially overlapping rolling LastValue observations; do not add them to MonMP scopes. Sparse markers may repeat a previous completed value; completedSamples counts observations, not proven unique frames. Up to 8 discovered counters use 4-sample buffers; unavailable/zero-only metrics are not credited as zero work. Buffered frames and the first observation after each window/context boundary are discarded; boundary Reset is always followed by Start. Recorder status is a boundary snapshot; restart/failure counts are lifetime totals.");
                report.AppendLine("On scheduled skipped-render updates, native LastValue can repeat the most recent rendered-frame counter. Native counter means are not guaranteed per rendered frame or current Update; use loop elapsed time, wall duration, process CPU and renderSchedule for interpretation.");
                report.AppendLine("Process CPU is OS processor time across ALL process threads, including capture overhead; equivalent occupied cores = CPU time / valid window wall time and can exceed 1. It is not main-thread CPU, GPU time, FPS attribution or a measure of descheduling. No FrameTimingManager/GPU timing is collected.");
                report.AppendLine("Each window has one role/guest/focus/pause/world-loaded/render-settings context. At most 12 recent windows, 32 persistent contexts, 128 scopes and 128 details are retained.");
                report.AppendLine("Context summaries include every retained-context sample; representatives are NOT additional samples. Representative prefers the first >=5s/120-frame window over an initial short fragment; it is not proof of a controlled view.");
                report.Append("background reports completed=").Append(writer.Completed).Append(" replaced=").Append(writer.Replaced)
                    .Append(" failed=").Append(writer.Failures).Append(" unretainedContextWindows=").Append(unretainedContextWindows).AppendLine();
                if (playerLoop != null) playerLoop.AppendStatus(report); else report.AppendLine("playerLoop status=notRequested maximumPairs=16");
                AppendContexts(report);
                report.Append("HITCH FRAMES total=").Append(hitchFrameCount).Append(" retained=").Append(hitchFrames.Count)
                    .AppendLine(" (MonMP scopes >=3 ms or next interval >=30 ms; elapsed is the recorded update; top lists scopes and overlapping details >=0.25 ms; LateUpdate/FixedUpdate rows after EndFrame fall into the following update)");
                foreach (string hitch in hitchFrames) report.AppendLine(hitch);
                report.AppendLine("RECENT WINDOWS (representatives and context summaries above may overlap these samples)");
                foreach (string recentWindow in history) report.AppendLine(recentWindow);
                writer.Queue(path, report.ToString());
            }
            foreach (var scope in scopes.Values) scope.Ticks = scope.Worst = scope.Calls = 0;
            foreach (var scope in details.Values) scope.Ticks = scope.Worst = scope.Calls = 0;
            Array.Clear(histogram, 0, histogram.Length); frames = dropped = 0;
            deltaSum = worstDelta = updateTicks = 0; started = now;
            scheduledRenders = skippedRenders = 0;
            nativeMetrics.NextWindow();
            gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
        }
        private static void Accumulate(Dictionary<string, Scope> target, Dictionary<string, Scope> source)
        {
            foreach (var entry in source)
            {
                if (entry.Value.Calls == 0) continue;
                if (!target.TryGetValue(entry.Key, out var total)) target.Add(entry.Key, total = new Scope());
                total.Ticks += entry.Value.Ticks; total.Calls += entry.Value.Calls; total.Worst = Math.Max(total.Worst, entry.Value.Worst);
            }
        }
        private void AppendContexts(StringBuilder report)
        {
            report.AppendLine("PERSISTENT CONTEXT SUMMARIES (cumulative; weight by frames; menu/unloaded and in-world samples stay separate)");
            foreach (var entry in contexts.OrderBy(pair => pair.Value.Id))
            {
                var key = entry.Key; var total = entry.Value;
                report.Append("contextSummary id=").Append(total.Id).Append(" phase=").Append(key.Role).Append(" guests=").Append(key.Guests)
                    .Append(" focused=").Append(key.Focused).Append(" paused=").Append(key.Paused).Append(" worldLoaded=").Append(key.Loaded)
                    .Append(" targetFrameRate=").Append(key.Target).Append(" vSyncCount=").Append(key.Vsync).Append(" width=").Append(key.Width).Append(" height=").Append(key.Height)
                    .Append(" renderFrameInterval=").Append(key.RenderInterval)
                    .Append(" synchronization=").Append(key.Synchronization).Append(" synchronizedGuests=").Append(key.SynchronizedGuests)
                    .Append(" experiment=").Append(key.Experiment)
                    .Append(" windows=").Append(total.Windows).Append(" frames=").Append(total.Frames)
                    .Append(" durationSeconds=").Append(Number(total.DurationTicks / frequency))
                    .Append(" firstWindowId=").Append(total.FirstWindow).Append(" lastWindowId=").Append(total.LastWindow)
                    .Append(" firstElapsedSeconds=").Append(Number(total.FirstElapsed)).Append(" lastElapsedSeconds=").Append(Number(total.LastElapsed)).AppendLine();
                report.Append("contextMetrics id=").Append(total.Id).Append(" wholeFrameMeanMs=").Append(Number(total.DeltaSeconds * 1000 / total.Frames))
                    .Append(" pluginUpdateSpanMeanMs=").Append(Number(Milliseconds(total.UpdateTicks) / total.Frames))
                    .Append(" measuredScopeSumPerFrameMs=").Append(Number(Milliseconds(total.ScopeTicks) / total.Frames))
                    .Append(" frameMinusScopesEstimateMs=").Append(Number(Math.Max(0, total.DeltaSeconds * 1000 - Milliseconds(total.ScopeTicks)) / total.Frames))
                    .Append(" worstMs=").Append(Number(total.WorstDelta * 1000)).Append(" GC=").Append(total.Gc0).Append('/').Append(total.Gc1).Append('/').Append(total.Gc2)
                    .Append(" droppedScopeSamples=").Append(total.Dropped).AppendLine();
                report.Append("contextRenderSchedule id=").Append(total.Id).Append(" scheduledRenderUpdates=").Append(total.ScheduledRenders)
                    .Append(" skippedRenderUpdates=").Append(total.SkippedRenders).AppendLine();
                AppendContextScopes(report, total.Id, total.Frames, total.Scopes, "contextScope");
                AppendContextScopes(report, total.Id, total.Frames, total.Details, "contextDetail");
                total.Native.Append(report, "contextNative", total.Id);
                report.Append("representative contextId=").Append(total.Id).Append(" sufficientDurationAndFrames=").Append(total.RepresentativeSettled).AppendLine();
                foreach (string line in total.Representative.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    report.Append("representative ").AppendLine(line);
                report.AppendLine();
            }
        }
        private void AppendContextScopes(StringBuilder report, int id, long frameCount, Dictionary<string, Scope> table, string prefix)
        {
            foreach (var scope in table.OrderByDescending(pair => pair.Value.Ticks))
                report.Append(prefix).Append(" id=").Append(id).Append(" name=").Append(scope.Key).Append(" calls=").Append(scope.Value.Calls)
                    .Append(" totalMs=").Append(Number(Milliseconds(scope.Value.Ticks)))
                    .Append(" perFrameMs=").Append(Number(Milliseconds(scope.Value.Ticks) / frameCount))
                    .Append(" worstCallMs=").Append(Number(Milliseconds(scope.Value.Worst))).AppendLine();
        }
        internal bool WaitForIdle(int milliseconds) => writer.WaitForIdle(milliseconds);
        public void Dispose()
        {
            if (disposed) return;
            try
            {
                playerLoop?.Dispose();
                if (initialized) CloseWindow(clock());
            }
            finally
            {
                disposed = true;
                nativeMetrics.Dispose();
            }
        }
    }
}
#endif
