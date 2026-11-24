using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace MonMP.Plugin.Diagnostics
{
    // Per-subsystem frame cost, so a stutter on a large save names the system that caused it.
    // Also contains a subsystem's exceptions in normal play: one faulty adapter logs once and the
    // session continues. Paired tests keep them fatal so regressions still fail the run.
    internal static class UpdateProfiler
    {
        private sealed class Entry
        {
            public string Name = "";
            public long Ticks, Worst, Calls, Errors;
            public long FrameNumber, FrameTicks;
            public long WindowTicks, WindowWorst, WindowCalls, RecentCalls;
            public double RecentAverage, RecentWorst;
            public string LastError = "";
        }

        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly HashSet<string> loggedErrors = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static readonly LatestReportWriter reportWriter = new LatestReportWriter(File.WriteAllText);
        private static long frameStart, worstFrame;
        private static long frameNumber;
        private static string worstFrameOwner = "";
        private static int frames, hitches, stalls;
        private static int windowFrames;
        private static int windowFocused, windowPaused, windowStalls, recentFrames, recentFocused, recentPaused, recentStalls;
        private static readonly int[] collectionCounts = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        private static readonly int[] recentCollections = new int[3];
        private static float windowWorstDelta, recentWorstDelta;
        private static double windowDelta, recentFrameMs, recentFps;
        private static readonly FramePacingWindow pacing = new FramePacingWindow();
        private static int recentPacingFrames, recentOver16Ms, recentOver33Ms;
        private static double recentP50, recentP95, recentP99;
        private static float worstDelta;
        private static float nextWrite;
        internal static bool Strict;
        internal static string? Directory;
        internal static Action<string>? Log;
        // Extra sections (received-record cost per adapter) and their reset.
        internal static Func<string>? Extra;
        internal static Action? ResetExtra;

        internal static void Run(string name, Action update)
        {
            long start = clock.ElapsedTicks;
            try { update(); }
            catch (Exception ex) when (!Strict)
            {
                var entry = Get(name);
                entry.Errors++;
                entry.LastError = ex.GetType().Name + ": " + ex.Message;
                if (loggedErrors.Add(name + "|" + entry.LastError))
                    Log?.Invoke("MonMP " + name + " update failed; the session continues without this step: " + ex);
            }
            finally
            {
                long elapsed = clock.ElapsedTicks - start;
                var entry = Get(name);
                entry.Ticks += elapsed; entry.Calls++;
                FrameCost(entry, elapsed);
                if (elapsed > entry.Worst) entry.Worst = elapsed;
            }
        }

        // Timed without containment, for steps whose failures must still end the session.
        internal static void Measure(string name, Action update)
        {
            long start = clock.ElapsedTicks;
            try { update(); }
            finally
            {
                long elapsed = clock.ElapsedTicks - start;
                var entry = Get(name);
                entry.Ticks += elapsed; entry.Calls++;
                FrameCost(entry, elapsed);
                if (elapsed > entry.Worst) entry.Worst = elapsed;
            }
        }

        internal static void BeginFrame() { frameNumber++; frameStart = clock.ElapsedTicks; }

        internal static void EndFrame(float now, float deltaTime, bool focused = true, bool paused = false)
        {
            long elapsed = clock.ElapsedTicks - frameStart;
            frames++;
            windowFrames++; windowDelta += deltaTime;
            if (focused) windowFocused++;
            if (paused) windowPaused++;
            if (focused && !paused) pacing.Add(deltaTime);
            if (deltaTime > 0.05f) windowStalls++;
            if (deltaTime > windowWorstDelta) windowWorstDelta = deltaTime;
            // Whole-frame stalls, whoever caused them (MonMP, native scripts, GC, loading).
            if (deltaTime > 0.05f) stalls++;
            if (deltaTime > worstDelta) worstDelta = deltaTime;
            if (elapsed > worstFrame) { worstFrame = elapsed; worstFrameOwner = Heaviest(); }
            if (Ms(elapsed) > 8) hitches++;
            if (now < nextWrite) return;
            nextWrite = now + 10;
            recentFrameMs = windowFrames == 0 ? 0 : windowDelta * 1000 / windowFrames;
            recentFps = windowDelta <= 0 ? 0 : windowFrames / windowDelta;
            recentFrames = windowFrames; recentFocused = windowFocused; recentPaused = windowPaused;
            recentStalls = windowStalls; recentWorstDelta = windowWorstDelta;
            recentPacingFrames = pacing.Count; recentOver16Ms = pacing.Over16Ms; recentOver33Ms = pacing.Over33Ms;
            recentP50 = pacing.Percentile(.5); recentP95 = pacing.Percentile(.95); recentP99 = pacing.Percentile(.99);
            pacing.Reset();
            windowFrames = windowFocused = windowPaused = windowStalls = 0;
            windowDelta = windowWorstDelta = 0;
            foreach (var entry in entries.Values)
            {
                entry.RecentAverage = entry.WindowCalls == 0 ? 0 : Ms(entry.WindowTicks) / entry.WindowCalls;
                entry.RecentWorst = Ms(entry.WindowWorst); entry.RecentCalls = entry.WindowCalls;
                entry.WindowTicks = entry.WindowWorst = entry.WindowCalls = 0;
            }
            for (int generation = 0; generation < collectionCounts.Length; generation++)
            {
                int count = GC.CollectionCount(generation);
                recentCollections[generation] = count - collectionCounts[generation];
                collectionCounts[generation] = count;
            }
            if (Directory == null) return;
            // Format only on the Unity thread, then let bounded background IO publish the text.
            // No Unity/native objects or mutable profiler entries cross into the writer.
            long reportStart = clock.ElapsedTicks;
            string report = Report();
            var formatEntry = Get("ProfileReport");
            long reportTicks = clock.ElapsedTicks - reportStart;
            formatEntry.Ticks += reportTicks; formatEntry.Calls++;
            FrameCost(formatEntry, reportTicks);
            if (reportTicks > formatEntry.Worst) formatEntry.Worst = reportTicks;
            reportWriter.Queue(Path.Combine(Directory, "update-profile.txt"), report);
        }

        internal static string Report()
        {
            var text = new StringBuilder();
            text.AppendLine("frames=" + frames + " stalls(>50ms frame)=" + stalls + " worstFrameTime=" + (worstDelta * 1000).ToString("0") + "ms");
            text.AppendLine("last profile interval: mean frame=" + recentFrameMs.ToString("0.00") + "ms estimated FPS=" + recentFps.ToString("0.0") + "; includes native/GC/background stalls");
            text.AppendLine("active focused frame pacing: frames=" + recentPacingFrames + " p50=" + recentP50.ToString("0.00") +
                "ms p95=" + recentP95.ToString("0.00") + "ms p99=" + recentP99.ToString("0.00") +
                "ms over16.67ms=" + recentOver16Ms + " over33.33ms=" + recentOver33Ms + " (0.25ms histogram upper bounds)");
            text.AppendLine("MonMP: hitches(>8ms)=" + hitches + " worst=" + Ms(worstFrame).ToString("0.00") + "ms (" + worstFrameOwner + ")");
            text.AppendLine("recent interval: frames=" + recentFrames + " focused=" + recentFocused + " paused=" + recentPaused +
                " stalls=" + recentStalls + " worstFrameTime=" + (recentWorstDelta * 1000).ToString("0.00") +
                "ms GC collections=" + recentCollections[0] + "/" + recentCollections[1] + "/" + recentCollections[2]);
            text.AppendLine("profile IO (process lifetime): completed=" + reportWriter.Completed + " replaced=" + reportWriter.Replaced +
                " failed=" + reportWriter.Failures + " lastWrite=" + reportWriter.LastWriteMs.ToString("0.00") + "ms (background)");
            foreach (var e in entries.Values.OrderByDescending(e => e.Ticks).Take(40))
                text.AppendLine(e.Name + " avg=" + (e.Calls == 0 ? 0 : Ms(e.Ticks) / e.Calls).ToString("0.000") + "ms worst=" + Ms(e.Worst).ToString("0.00") +
                    "ms total=" + Ms(e.Ticks).ToString("0") + "ms recentAvg=" + e.RecentAverage.ToString("0.000") +
                    "ms recentWorst=" + e.RecentWorst.ToString("0.00") + "ms recentCalls=" + e.RecentCalls +
                    (e.Errors > 0 ? " errors=" + e.Errors + " last=" + e.LastError : ""));
            try { if (Extra != null) text.Append(Extra()); }
            catch (Exception ex) { text.AppendLine("extra unavailable: " + ex.Message); }
            return text.ToString();
        }

        internal static int ErrorCount(string name) => entries.TryGetValue(name, out var entry) ? (int)entry.Errors : 0;

        internal static void Reset()
        {
            ResetExtra?.Invoke();
            entries.Clear(); loggedErrors.Clear(); frames = hitches = stalls = 0; worstDelta = 0; worstFrame = 0; frameNumber = 0; worstFrameOwner = ""; nextWrite = 0;
            windowFrames = 0; windowDelta = recentFrameMs = recentFps = 0;
            pacing.Reset(); recentPacingFrames = recentOver16Ms = recentOver33Ms = 0;
            recentP50 = recentP95 = recentP99 = 0;
            windowFocused = windowPaused = windowStalls = recentFrames = recentFocused = recentPaused = recentStalls = 0;
            windowWorstDelta = recentWorstDelta = 0;
            for (int generation = 0; generation < collectionCounts.Length; generation++)
            { collectionCounts[generation] = GC.CollectionCount(generation); recentCollections[generation] = 0; }
        }

        private static void FrameCost(Entry entry, long elapsed)
        {
            if (entry.FrameNumber != frameNumber) { entry.FrameNumber = frameNumber; entry.FrameTicks = 0; }
            entry.FrameTicks += elapsed;
            entry.WindowTicks += elapsed; entry.WindowCalls++;
            if (elapsed > entry.WindowWorst) entry.WindowWorst = elapsed;
        }

        private static string Heaviest()
        {
            // Attribute this exact frame using each subsystem's total cost in it.
            // Historical single-call maxima may belong to an earlier frame.
            Entry? worst = null;
            foreach (var e in entries.Values)
                if (e.FrameNumber == frameNumber && (worst == null || e.FrameTicks > worst.FrameTicks)) worst = e;
            return worst == null ? "" : worst.Name + " " + Ms(worst.FrameTicks).ToString("0.00") + "ms";
        }

        private static Entry Get(string name)
        {
            if (!entries.TryGetValue(name, out var entry)) entries[name] = entry = new Entry { Name = name };
            return entry;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
