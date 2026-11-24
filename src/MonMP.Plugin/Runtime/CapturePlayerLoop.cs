#if MONMP_PERFORMANCE_CAPTURE
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.LowLevel;

namespace MonMP.Plugin.Runtime
{
    // Supported managed entry points bracket the original native nodes. No
    // native function pointer is called, replaced or patched by this helper.
    internal sealed class CapturePlayerLoop : IDisposable
    {
        private sealed class Marker { }
        private sealed class Probe
        {
            internal readonly CapturePlayerLoop Owner;
            internal readonly string Name;
            internal readonly Type First, Last;
            internal readonly PlayerLoopSystem.UpdateFunction Before, After;
            internal bool Installed, Valid;
            internal int Depth;
            internal long Started, Generation, Entered, Completed, CrossWindow, Reentrant, Unmatched, FrameChanged, Failures;
            internal Probe(CapturePlayerLoop owner, string name, Type first, Type? last = null)
            { Owner = owner; Name = name; First = first; Last = last ?? first; Before = Begin; After = End; }
            private void Begin()
            {
                if (Owner.disposed || !Owner.enabled) return;
                try
                {
                    Owner.CheckFrame();
                    Entered++;
                    if (Depth++ != 0) { Valid = false; Reentrant++; return; }
                    Valid = Owner.capture.PlayerLoopSamplingReady;
                    if (!Valid) return;
                    Generation = Owner.capture.CaptureWindowGeneration;
                    Started = Owner.capture.Timestamp();
                }
                catch (Exception) { Valid = false; Failures++; }
            }
            private void End()
            {
                if (Owner.disposed || !Owner.enabled) return;
                try
                {
                    Owner.CheckFrame();
                    if (Depth == 0) { Unmatched++; return; }
                    if (--Depth != 0 || !Valid) return;
                    Valid = false;
                    if (!Owner.capture.PlayerLoopSamplingReady || Generation != Owner.capture.CaptureWindowGeneration) { CrossWindow++; return; }
                    long elapsed = Owner.capture.Timestamp() - Started;
                    if (elapsed < 0) { Failures++; return; }
                    Owner.capture.RecordDetail(Name, elapsed); Completed++;
                }
                catch (Exception) { Valid = false; Failures++; }
            }
        }
        private readonly PerformanceCapture capture;
        private readonly Func<PlayerLoopSystem> read;
        private readonly Action<PlayerLoopSystem> write;
        private readonly Func<int> frame;
        private readonly Probe[] probes;
        private const int MaximumOriginalNodes = 4096;
        private const int MaximumCleanupNodes = MaximumOriginalNodes + 32 + 512;
        private bool disposed, enabled;
        private int lastFrame = int.MinValue, installed;
        private string status = "notInstalled", cleanup = "notRequested";
        private CapturePlayerLoop(PerformanceCapture capture, Func<PlayerLoopSystem> read, Action<PlayerLoopSystem> write, Func<int> frame)
        {
            this.capture = capture; this.read = read; this.write = write; this.frame = frame;
            probes = new[] {
                new Probe(this, "Loop.TimeUpdate", typeof(UnityEngine.PlayerLoop.TimeUpdate)),
                new Probe(this, "Loop.BehaviourFixedUpdate", typeof(UnityEngine.PlayerLoop.FixedUpdate.ScriptRunBehaviourFixedUpdate)),
                new Probe(this, "Loop.EarlyUpdate", typeof(UnityEngine.PlayerLoop.EarlyUpdate)),
                new Probe(this, "Loop.FixedUpdate", typeof(UnityEngine.PlayerLoop.FixedUpdate)),
                new Probe(this, "Loop.PreUpdate", typeof(UnityEngine.PlayerLoop.PreUpdate)),
                new Probe(this, "Loop.Update", typeof(UnityEngine.PlayerLoop.Update)),
                new Probe(this, "Loop.PreLateUpdate", typeof(UnityEngine.PlayerLoop.PreLateUpdate)),
                new Probe(this, "Loop.PostLateUpdate", typeof(UnityEngine.PlayerLoop.PostLateUpdate)),
                new Probe(this, "Loop.Physics", typeof(UnityEngine.PlayerLoop.FixedUpdate.PhysicsFixedUpdate)),
                new Probe(this, "Loop.BehaviourUpdate", typeof(UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate)),
                new Probe(this, "Loop.Animation", typeof(UnityEngine.PlayerLoop.PreLateUpdate.DirectorUpdateAnimationBegin), typeof(UnityEngine.PlayerLoop.PreLateUpdate.DirectorUpdateAnimationEnd)),
                new Probe(this, "Loop.BehaviourLateUpdate", typeof(UnityEngine.PlayerLoop.PreLateUpdate.ScriptRunBehaviourLateUpdate)),
                new Probe(this, "Loop.UpdateAllRenderers", typeof(UnityEngine.PlayerLoop.PostLateUpdate.UpdateAllRenderers)),
                new Probe(this, "Loop.UpdateAllSkinnedMeshes", typeof(UnityEngine.PlayerLoop.PostLateUpdate.UpdateAllSkinnedMeshes)),
                new Probe(this, "Loop.FinishFrameRendering", typeof(UnityEngine.PlayerLoop.PostLateUpdate.FinishFrameRendering)),
                new Probe(this, "Loop.PresentAfterDraw", typeof(UnityEngine.PlayerLoop.PostLateUpdate.PresentAfterDraw)) };
        }
        internal static CapturePlayerLoop TryInstall(PerformanceCapture capture, Func<PlayerLoopSystem>? read = null,
            Action<PlayerLoopSystem>? write = null, Func<int>? frame = null)
        {
            var result = new CapturePlayerLoop(capture, read ?? PlayerLoop.GetCurrentPlayerLoop, write ?? PlayerLoop.SetPlayerLoop, frame ?? (() => UnityEngine.Time.frameCount));
            try
            {
                var current = result.read(); int budget = MaximumOriginalNodes;
                Validate(current, 0, ref budget);
                if (ContainsProbe(current)) { result.status = "captureProbeAlreadyPresent"; return result; }
                var changed = result.Insert(current);
                if (result.installed == 0) { result.status = "targetsMissing"; return result; }
                result.write(changed); result.status = "installed"; result.enabled = true;
            }
            catch (Exception)
            {
                result.status = "installFailed";
                // A failed setter may have accepted part of the supplied loop.
                // Remove only this instance's managed additions if accessible.
                result.RemoveCurrent();
            }
            return result;
        }
        private static void Validate(PlayerLoopSystem node, int depth, ref int budget)
        {
            if (--budget < 0 || depth > 64) throw new InvalidOperationException("Capture player loop exceeds its structural limit.");
            if (node.subSystemList != null) foreach (var child in node.subSystemList) Validate(child, depth + 1, ref budget);
        }
        private static bool ContainsProbe(PlayerLoopSystem node)
        {
            if (node.updateDelegate != null)
                foreach (var callback in node.updateDelegate.GetInvocationList()) if (callback.Target is Probe) return true;
            if (node.subSystemList != null) foreach (var child in node.subSystemList) if (ContainsProbe(child)) return true;
            return false;
        }
        private PlayerLoopSystem Insert(PlayerLoopSystem parent)
        {
            var children = parent.subSystemList;
            if (children == null || children.Length == 0) return parent;
            var first = new int[probes.Length]; var last = new int[probes.Length];
            for (int p = 0; p < probes.Length; p++)
            {
                first[p] = last[p] = -1;
                if (probes[p].Installed) continue;
                for (int i = 0; i < children.Length; i++)
                {
                    if (first[p] < 0 && children[i].type == probes[p].First) first[p] = i;
                    if (children[i].type == probes[p].Last) last[p] = i;
                }
                if (first[p] >= 0 && last[p] >= first[p]) { probes[p].Installed = true; installed++; }
                else first[p] = last[p] = -1;
            }
            var output = new List<PlayerLoopSystem>(children.Length + probes.Length * 2);
            for (int i = 0; i < children.Length; i++)
            {
                for (int p = 0; p < probes.Length; p++) if (first[p] == i) output.Add(new PlayerLoopSystem { type = typeof(Marker), updateDelegate = probes[p].Before });
                output.Add(Insert(children[i]));
                for (int p = probes.Length - 1; p >= 0; p--) if (last[p] == i) output.Add(new PlayerLoopSystem { type = typeof(Marker), updateDelegate = probes[p].After });
            }
            parent.subSystemList = output.ToArray(); return parent;
        }
        private void CheckFrame()
        {
            int current = frame();
            if (current == lastFrame) return;
            lastFrame = current;
            foreach (var probe in probes)
                if (probe.Depth != 0) { probe.FrameChanged++; probe.Depth = 0; probe.Valid = false; }
        }
        private bool Owns(PlayerLoopSystem.UpdateFunction? callbacks)
        {
            if (callbacks != null)
                foreach (var callback in callbacks.GetInvocationList())
                    if (callback.Target is Probe probe && ReferenceEquals(probe.Owner, this)) return true;
            return false;
        }
        private PlayerLoopSystem.UpdateFunction? StripOwned(PlayerLoopSystem.UpdateFunction? callbacks, ref bool changed)
        {
            if (!Owns(callbacks)) return callbacks;
            PlayerLoopSystem.UpdateFunction? retained = null;
            foreach (var callback in callbacks!.GetInvocationList())
                if (callback.Target is Probe probe && ReferenceEquals(probe.Owner, this)) changed = true;
                else retained += (PlayerLoopSystem.UpdateFunction)callback;
            return retained;
        }
        private PlayerLoopSystem Remove(PlayerLoopSystem parent, ref bool changed)
        {
            parent.updateDelegate = StripOwned(parent.updateDelegate, ref changed);
            if (parent.subSystemList == null) return parent;
            var output = new List<PlayerLoopSystem>(parent.subSystemList.Length);
            foreach (var original in parent.subSystemList)
            {
                bool owned = Owns(original.updateDelegate);
                var child = Remove(original, ref changed);
                if (!owned) { output.Add(child); continue; }
                // Preserve foreign children or native work attached later to an
                // owned marker. Pure managed markers can be removed outright.
                if (child.updateDelegate != null || child.updateFunction != IntPtr.Zero || child.loopConditionFunction != IntPtr.Zero) output.Add(child);
                else if (child.subSystemList != null) output.AddRange(child.subSystemList);
            }
            parent.subSystemList = output.ToArray(); return parent;
        }
        private void RemoveCurrent()
        {
            try
            {
                // Installation accepts 4096 original nodes and adds at most 32;
                // allow bounded later additions by other mods during cleanup.
                var current = read(); int budget = MaximumCleanupNodes; Validate(current, 0, ref budget);
                bool changed = false; var cleaned = Remove(current, ref changed);
                if (changed) write(cleaned);
                cleanup = "ownedProbesRemoved";
            }
            catch (Exception) { cleanup = "cleanupFailedCallbacksDisabled"; }
        }
        internal void AppendStatus(StringBuilder report)
        {
            report.Append("playerLoop status=").Append(status).Append(" installedPairsAtSetup=").Append(installed)
                .Append(" maximumPairs=16 callbacksEnabled=").Append(enabled && !disposed).Append(" cleanup=").Append(cleanup).AppendLine();
            foreach (var probe in probes)
                report.Append("playerLoopProbe name=").Append(probe.Name).Append(" installedAtSetup=").Append(probe.Installed)
                    .Append(" entered=").Append(probe.Entered).Append(" completed=").Append(probe.Completed)
                    .Append(" crossWindowDiscarded=").Append(probe.CrossWindow).Append(" reentrantDiscarded=").Append(probe.Reentrant)
                    .Append(" unmatchedDiscarded=").Append(probe.Unmatched).Append(" frameChangedDiscarded=").Append(probe.FrameChanged)
                    .Append(" failures=").Append(probe.Failures).AppendLine();
        }
        public void Dispose()
        { if (disposed) return; disposed = true; enabled = false; RemoveCurrent(); }
    }
}
#endif
