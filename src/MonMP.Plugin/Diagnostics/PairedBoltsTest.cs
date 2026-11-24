using System;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Bolt holes are host-authoritative: guests only turn bolts through leased-tool commands. The host
    // installs and tightens a bolt in steps and the guest must see the intermediate progress live, not
    // only the final result. Meanwhile the guest writes its own native hole directly (what an old or
    // forged client would do) and the host must never adopt it. The two holes are the first two by
    // binding id, a fixed property of the hole set, so both sides agree on the pair.
    internal sealed class PairedBoltsTest
    {
        // A distinct in-progress marker identifies the host's action (save-state bolts sit at 0 or 100).
        private const int HostMark = 30, GuestForgery = 40;
        private readonly MonMPPlugin plugin;
        private ulong mine, theirs;
        private int phase;
        private float next;
        private float deadline;
        private bool sawHostMark;
        private BoltHoleState beforeForgery;
        private int hostStep;
        private Quaternion? firstTurn;
        private bool sawTurning;
        private ulong stuckProbe;
        private float stuckDeadline;
        private BoltSoundLifecycleProbe? soundProbe;
        internal string Status => plugin.World == null ? "bolts: no shared world" : "bolts phase=" + phase + "; ready=" + plugin.World.Bolts.Ready + "; sync=" + plugin.World.SynchronizationStatus +
            "; mine=" + (mine == 0 ? "?" : plugin.World.Bolts.TestNative(mine).Percent.ToString()) +
            "; theirs=" + (theirs == 0 ? "?" : plugin.World.Bolts.TestNative(theirs).Percent.ToString()) + "; count=" + plugin.World.Bolts.Count + "; " + soundProbe?.Status;
        internal PairedBoltsTest(MonMPPlugin plugin) { this.plugin = plugin; }
        internal bool Tick()
        {
            if (plugin.World == null) throw new InvalidOperationException("Bolts test: the shared world ended (session stopped) at " + Status);
            var bolts = plugin.World.Bolts;
            if (!bolts.Ready) return false;
            if (soundProbe == null)
            {
                var gameplay = plugin.Gameplay;
                if (gameplay?.Player == null || plugin.Session?.Peers.Count < 2 || plugin.World!.Events?.Live != true) return false;
                soundProbe = new BoltSoundLifecycleProbe(plugin, gameplay);
                PresentationDepartureProbe.Verify(plugin, gameplay);
            }
            // The observer records a baseline before the host starts its lifecycle phase.
            if (!plugin.TestOptions.IsHost) soundProbe.Tick(false);
            if (bolts.Count == 0) throw new InvalidOperationException("No Konig bolt holes found; is the master scene loaded?");
            if (mine == 0)
            {
                var holes = bolts.TestFirstHoles(2);
                mine = plugin.TestOptions.IsHost ? holes[0] : holes[1];
                theirs = plugin.TestOptions.IsHost ? holes[1] : holes[0];
            }
            // Fail fast instead of idling until the suite-wide timeout.
            if (deadline == 0) deadline = Time.unscaledTime + 90f;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Bolts test stalled: " + Status);
            // The host's hole is "theirs" on the guest.
            if (!plugin.TestOptions.IsHost)
            {
                var h = bolts.TestNative(theirs); sawHostMark |= h.Installed && h.Percent == HostMark;
                // The host's wrench steps must turn the bolt here while it eases in (#133).
                var turn = bolts.TestBoltTurn(theirs);
                if (sawHostMark && firstTurn == null) firstTurn = turn;
                if (firstTurn != null && turn != null && bolts.TestAnimatingBolts > 0 && Quaternion.Angle(firstTurn.Value, turn.Value) > 1f) sawTurning = true;
            }
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host(bolts) : Guest(bolts);
        }
        private bool Host(SharedVehicleBolts bolts)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                if (bolts.TestNative(mine).Installed) bolts.TestUnscrew(mine);
                bolts.TestInstall(mine); bolts.TestTighten(mine, HostMark);
                // Give the guest time to see the mark and try its forged write.
                phase = 1; next = Time.unscaledTime + 5f;
            }
            else if (phase == 1)
            {
                Require(bolts.TestNative(theirs).Equals(bolts.TestOriginal(theirs)), "Host adopted a guest's direct native bolt write: " + Status);
                hostStep = HostMark; phase = 10;
            }
            else if (phase == 10)
            {
                // A wrench's continuous progress, about 25% a second.
                hostStep = Math.Min(100, hostStep + 1);
                bolts.TestTighten(mine, hostStep);
                next = Time.unscaledTime + 0.04f;
                if (hostStep == 100) phase = 2;
            }
            else if (phase == 2)
            {
                if (soundProbe!.Tick(true)) phase = 3;
            }
            else if (phase == 3 && plugin.Session!.Peers.Count == 1) { plugin.EndPairedTestSession(); return true; }
            return false;
        }
        private bool Guest(SharedVehicleBolts bolts)
        {
            if (phase == 0)
            {
                if (!sawHostMark) return false;
                beforeForgery = bolts.TestRecord(mine);
                if (bolts.TestNative(mine).Installed) bolts.TestUnscrew(mine);
                bolts.TestInstall(mine); bolts.TestTighten(mine, GuestForgery);
                phase = 1;
                return false;
            }
            if (phase == 1 && bolts.TestRecord(theirs).Percent == 100 && bolts.TestNative(theirs).Percent == 100)
            {
                Require(bolts.TestRecord(mine).Equals(beforeForgery), "The host record took the guest's forged bolt write.");
                Require(sawTurning, "The host's bolt jumped to each step on the guest without turning.");
                if (stuckProbe == 0)
                {
                    stuckProbe = bolts.TestAnyEmptyHole();
                    bolts.TestAttachUnsharedBolt(stuckProbe);
                    next = Time.unscaledTime + 1f; stuckDeadline = Time.unscaledTime + 8f;
                    return false;
                }
                if (!bolts.TestInstallSettled(stuckProbe))
                { Require(Time.unscaledTime < stuckDeadline, "A guest's unshared bolt install never settled (#134): " + Status); return false; }
                Require(bolts.TestLocallyInstalled(stuckProbe) == bolts.TestRecord(stuckProbe).Installed,
                    "A guest's bolt install stayed on the guest alone (#134): local " + bolts.TestLocallyInstalled(stuckProbe) + ", host " + bolts.TestRecord(stuckProbe).Installed);
                Debug.Log("MonMP bolts: unshared guest bolt settled; installed on both=" + bolts.TestLocallyInstalled(stuckProbe) + ", rolled back=" + bolts.TestRolledBackInstalls);
                if (!soundProbe!.Tick(false)) return false;
                var original = bolts.TestOriginal(mine);
                plugin.EndPairedTestSession();
                Require(bolts.TestNative(mine).Equals(original), "Guest disconnect did not restore the original baseline.");
                return true;
            }
            return false;
        }
        internal void Restore() => soundProbe?.Dispose();
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Bolts test: " + message); }
    }
}
