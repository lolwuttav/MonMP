using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MonMP.Core.Persistence;
using MonMP.Core.Session;
using MonMP.Core.World;

internal static class Program
{
    private static int assertions;
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static PlayerProfileState State(int marker) => new PlayerProfileState(new float[7],
        new[] { marker, 0, 0, 0, 0, 0, 0 }, new float[21], marker % 9);
    private static int Marker(SidecarProfile profile) => PlayerProfileState.FromSection(profile.State[PlayerProfileState.SectionKey]).Counters[0];
    private static SidecarProfile Saved(Guid id, int marker, long seen = 100) => new SidecarProfile(id, "previous", 0x123456, seen,
        new Dictionary<string, SidecarSection> { [PlayerProfileState.SectionKey] = State(marker).ToSection(), ["extension"] = new SidecarSection(1, new byte[] { 42 }) });

    private sealed class Fixture : IDisposable
    {
        internal readonly NetworkSession[] Sessions;
        internal readonly WorldChannel[] Worlds;
        internal readonly SaveRoundChannel[] Rounds;
        internal readonly bool[] Blocked, Ready;
        internal readonly int[] Captures, Flushes, CaptureFailures, FlushFailures;
        internal readonly HashSet<uint> Owners = new HashSet<uint>();
        internal readonly Dictionary<Guid, PlayerProfileState> Cached = new Dictionary<Guid, PlayerProfileState>();
        internal double Now;
        internal bool Release = true, WorldReady = true;
        internal int WritingChecks, Recoveries, RevokeCalls;
        internal bool ThrowRevoke, ThrowReadiness;
        internal Fixture(int guests = 1, bool recovery = true)
        {
            int count = guests + 1;
            Sessions = new NetworkSession[count]; Worlds = new WorldChannel[count]; Rounds = new SaveRoundChannel[count];
            Blocked = new bool[count]; Ready = Enumerable.Repeat(true, count).ToArray();
            Captures = new int[count]; Flushes = new int[count]; CaptureFailures = new int[count]; FlushFailures = new int[count];
            var records = SaveRoundChannel.InitialRecords().ToArray();
            for (int index = 0; index < count; index++)
            {
                int local = index;
                Sessions[index] = new NetworkSession(new SessionOptions { Scene = "Gameplay", DisplayName = "Recovery " + index,
                    ProfileId = Guid.NewGuid(), EnableIPv6 = false, WorldCatalog = WorldRecord.Digest(records, true) });
                var session = Sessions[index];
                Worlds[index] = new WorldChannel(session, records, clock: () => Now);
                Rounds[index] = new SaveRoundChannel(session, Worlds[index], () => Now, () => Owners.ToArray(), peer =>
                    { RevokeCalls++; if (ThrowRevoke) throw new InvalidOperationException("stuck ownership adapter"); Owners.Remove(peer); },
                    value => Blocked[local] = value,
                    () => { Flushes[local]++; if (FlushFailures[local]-- > 0) throw new InvalidOperationException("temporary release"); if (Release) Owners.Remove(session.LocalPeerId); },
                    () => Ready[local],
                    () => { Check(Blocked[local], "Capture escaped the mutation gate."); Captures[local]++;
                        if (CaptureFailures[local]-- > 0) throw new InvalidOperationException("temporary capture"); return State(10 + local); },
                    (peer, profile) => { Check(session.IsHost && session.Peers.TryGetValue(peer, out var info) && info.ProfileId == profile,
                        "Checkpoint lookup was not bound to the participant profile."); return Cached.TryGetValue(profile, out var state) ? state : null; },
                    () => { WritingChecks++; Check(Owners.Count == 0, "Ordinary writing readiness ran while owners remained.");
                        if (ThrowReadiness) throw new InvalidOperationException("stuck readiness adapter"); return WorldReady; },
                    recovery ? () => { Recoveries++; Check(Blocked[0], "Recovery ran outside the mutation gate."); } : null,
                    settlementGraceSeconds: 1);
            }
            Sessions[0].Host(0);
            for (int index = 1; index < count; index++) Sessions[index].Join("127.0.0.1", Sessions[0].Port);
            Until(() => Worlds.All(w => w.FullySynchronized), "initial synchronization");
        }
        internal void Poll()
        {
            foreach (var session in Sessions) session.Tick();
            foreach (var round in Rounds) round.Update();
            foreach (var world in Worlds) world.Pump();
            Check(Sessions.All(s => s.IsRunning), "A transient save failure stopped a valid session.");
            Thread.Sleep(1);
        }
        internal void Until(Func<bool> done, string phase)
        {
            for (int poll = 0; !done() && poll < 5000; poll++) Poll();
            Check(done(), phase + " did not complete; phases=" + string.Join(",", Rounds.Select(r => r.Phase)));
        }
        internal void Begin() { Rounds[0].Begin(3); Until(() => Blocked.All(value => value), "collection gates"); }
        internal void Finish()
        {
            Rounds[0].Complete(); Until(() => Rounds.All(r => r.Phase == SavePhase.Complete) && Blocked.All(value => !value), "completion releases gates");
        }
        public void Dispose()
        { foreach (var round in Rounds) round.Dispose(); foreach (var world in Worlds) world.Dispose(); foreach (var session in Sessions) session.Dispose(); }
    }

    private static void TransientCaptureAndRelease()
    {
        using var f = new Fixture(); f.CaptureFailures[0] = 2; f.CaptureFailures[1] = 2; f.FlushFailures[0] = 1;
        f.Begin(); Check(f.Rounds[0].Phase == SavePhase.Collecting, "A transient callback failed the save.");
        for (int step = 1; step <= 5 && f.Rounds[0].Phase == SavePhase.Collecting; step++)
        { f.Now = step * 0.11; for (int poll = 0; poll < 12; poll++) f.Poll(); }
        f.Until(() => f.Rounds.All(r => r.Phase == SavePhase.Writing), "transient recovery");
        Check(f.Captures.All(count => count == 3) && f.Rounds[0].AcceptedCount == 2 && !f.Rounds[0].UsedRecovery,
            "Transient capture did not retry to a fresh profile.");
        Check(f.Rounds.All(r => r.LocalFailure.Length == 0) && f.Recoveries == 0, "Healed transient error was retained as a degraded save.");
        f.Finish();
    }

    private static void CaptureDoesNotStopSettlement()
    {
        using var f = new Fixture(); f.Release = false; f.WorldReady = false;
        f.Owners.UnionWith(f.Sessions.Select(s => s.LocalPeerId)); f.Begin();
        f.Until(() => f.Captures.All(count => count == 1), "early profiles");
        int before = f.Flushes.Sum(); f.Now = 0.26; for (int poll = 0; poll < 10; poll++) f.Poll();
        Check(f.Flushes.Sum() > before && f.WritingChecks == 0 && f.Rounds[0].Phase == SavePhase.Collecting,
            "Profile submission stopped owner settlement or froze too early.");
        f.Release = true; f.Now = 0.52; f.Until(() => f.Owners.Count == 0 && f.WritingChecks > 0, "actual release");
        Check(f.Rounds[0].Phase == SavePhase.Collecting, "Profiles alone bypassed pending world transactions.");
        f.WorldReady = true; f.Until(() => f.Rounds.All(r => r.Phase == SavePhase.Writing), "settled writing");
        Check(f.Captures.All(count => count == 1) && f.Recoveries == 0, "Settling recaptured a changed multipart profile."); f.Finish();
    }

    private static void CheckpointsAreSeparateFromFreshProfiles()
    {
        using var f = new Fixture(2); f.Ready[0] = f.Ready[2] = false;
        f.Cached.Add(f.Sessions[0].ProfileId, State(90)); f.Cached.Add(f.Sessions[2].ProfileId, State(92)); f.Begin();
        f.Until(() => f.Rounds[0].AcceptedCount == 1, "fresh guest");
        Check(f.Rounds[0].Phase == SavePhase.Collecting && f.Rounds[0].RetainedProfileIds.Count == 0, "Checkpoint replaced a possible fresh profile early.");
        f.Now = 3; f.Until(() => f.Rounds.All(r => r.Phase == SavePhase.Writing), "checkpoint writing");
        var host = f.Rounds[0];
        Check(host.AcceptedCount == 1 && host.AcceptedProfileIds.SequenceEqual(new[] { f.Sessions[1].ProfileId }) &&
            host.RetainedProfileIds.Count == 2 && host.UsedRecovery && f.Recoveries == 0,
            "Retained checkpoints were falsely counted as fresh captures.");
        var prior = f.Sessions.Select((s, i) => Saved(s.ProfileId, 50 + i)).ToArray();
        var merged = host.MergeProfiles(prior, 200).ToDictionary(p => p.Id);
        Check(Marker(merged[f.Sessions[0].ProfileId]) == 90 && Marker(merged[f.Sessions[1].ProfileId]) == 11 && Marker(merged[f.Sessions[2].ProfileId]) == 92,
            "Fresh/checkpoint merge lost the correct participant state.");
        Check(merged.Values.All(p => p.State["extension"].Payload.SequenceEqual(new byte[] { 42 })), "Checkpoint merge discarded sidecar extensions.");
        Check(merged[f.Sessions[0].ProfileId].LastSeenUtc == 100 && merged[f.Sessions[2].ProfileId].LastSeenUtc == 100 &&
            merged[f.Sessions[1].ProfileId].LastSeenUtc == 200, "Retained checkpoint was falsely stamped as a fresh observation.");
        host.RefreshHostProfile(State(99)); merged = host.MergeProfiles(prior, 201).ToDictionary(p => p.Id);
        Check(Marker(merged[f.Sessions[0].ProfileId]) == 99 && merged[f.Sessions[0].ProfileId].LastSeenUtc == 201 &&
            host.AcceptedCount == 2 && !host.RetainedProfileIds.Contains(f.Sessions[0].ProfileId) && !host.MissingPeers.Contains(0u),
            "Final frozen host refresh did not replace the retained checkpoint as fresh state.");
        f.Finish();
    }

    private static void MissingHostCanUseWorldOnlyRecovery()
    {
        using var f = new Fixture(); f.Ready[0] = false; f.Begin(); f.Now = 3; for (int poll = 0; poll < 8; poll++) f.Poll();
        Check(f.Rounds[0].Phase == SavePhase.Collecting && f.Recoveries == 0, "Recovery skipped the short settlement grace.");
        f.Now = 4; f.Until(() => f.Rounds.All(r => r.Phase == SavePhase.Writing), "world-only recovery");
        var host = f.Rounds[0]; Check(host.UsedRecovery && host.RecoveryReason.Contains("Host profile") && f.Recoveries == 1,
            "Missing host did not enter explicit authoritative recovery.");
        var merged = host.MergeProfiles(Array.Empty<SidecarProfile>(), 200);
        Check(merged.Count == 1 && merged[0].Id == f.Sessions[1].ProfileId && !host.RetainedProfileIds.Contains(f.Sessions[0].ProfileId),
            "World-only recovery invented default host needs.");
        for (int poll = 0; poll < 8; poll++) f.Poll(); Check(f.Recoveries == 1, "Recovery callback repeated while Writing."); f.Finish();
        f.Ready[0] = true; f.Begin(); f.Until(() => f.Rounds.All(r => r.Phase == SavePhase.Writing), "fresh next save");
        Check(!host.UsedRecovery && host.RecoveryReason.Length == 0 && host.RetainedProfileIds.Count == 0 && host.AcceptedCount == 2,
            "Recovery diagnostics/checkpoints leaked into the next round."); f.Finish();
    }

    private static void StuckWorldAdaptersAreBounded()
    {
        using var f = new Fixture(2); f.Release = false; f.ThrowRevoke = true;
        f.Owners.UnionWith(f.Sessions.Select(s => s.LocalPeerId)); f.Begin(); f.Until(() => f.Captures.All(count => count == 1), "owned captures");
        f.Now = 3; f.Poll(); Check(f.RevokeCalls >= 3 && f.Rounds[0].Phase == SavePhase.Collecting,
            "One throwing owner prevented best-effort revocation of other owners.");
        f.Now = 4; f.Until(() => f.Rounds.All(r => r.Phase == SavePhase.Writing), "stuck owner recovery");
        Check(f.Recoveries == 1 && f.Rounds[0].AcceptedCount == 3 && f.Rounds[0].UsedRecovery,
            "Stuck internal bookkeeping blocked the authoritative snapshot or lost valid captures."); f.Finish();

        f.Release = true; f.ThrowRevoke = false; f.Owners.Clear(); f.ThrowReadiness = true; f.Begin();
        Check(f.Rounds[0].Phase == SavePhase.Collecting, "A readiness callback error immediately aborted saving.");
        f.Now += 4; f.Until(() => f.Rounds.All(r => r.Phase == SavePhase.Writing), "readiness recovery");
        Check(f.Recoveries == 2 && f.Rounds[0].RecoveryReason.Contains("readiness"), "Readiness exception was not diagnosed/recovered."); f.Finish();
    }

    private static void BarrierRejectsInvalidCheckpointsAndKeepsStrictDefault()
    {
        double now = 0; var profile = Guid.NewGuid(); var peers = new[] { new KeyValuePair<uint, Guid>(0, profile) };
        var strict = new SaveBarrierRound(() => now, () => Array.Empty<uint>(), (_, bytes) => bytes.SequenceEqual(new byte[] { 1 }));
        strict.Begin(peers, 3); now = 3;
        try { strict.TryEnterWriting(_ => { }, retainedProfile: (_, _) => new byte[] { 0 }); Check(false, "Invalid checkpoint entered Writing."); }
        catch (InvalidOperationException) { Check(strict.Phase == SavePhase.Failed, "Legacy strict default did not fail closed."); }

        var recover = new SaveBarrierRound(() => now, () => Array.Empty<uint>(), (_, bytes) => bytes.SequenceEqual(new byte[] { 1 }), 1);
        recover.Begin(peers, 3); now = 7; int calls = 0;
        Check(recover.TryEnterWriting(_ => { }, retainedProfile: (_, _) => new byte[] { 0 }, recoverWriting: () => calls++) &&
            recover.RetainedProfiles.Count == 0 && recover.AcceptedCount == 0 && calls == 1 && recover.UsedRecovery,
            "Invalid checkpoint was retained or blocked opt-in world-only recovery.");
        recover.Complete();
    }

    private static void DamagedOwnershipInventoryRecoversAtBound()
    {
        double now = 0; var profile = Guid.NewGuid(); bool damaged = true; int recoveries = 0;
        var barrier = new SaveBarrierRound(() => now, () => damaged ? throw new InvalidOperationException("damaged owner inventory") : Array.Empty<uint>(),
            (_, bytes) => bytes.SequenceEqual(new byte[] { 1 }), 1);
        barrier.Begin(new[] { new KeyValuePair<uint, Guid>(0, profile) }, 3);
        Check(!barrier.TryEnterWriting(_ => { }, recoverWriting: () => recoveries++), "Damaged bookkeeping recovered before collection deadline.");
        now = 3; Check(!barrier.TryEnterWriting(_ => { }, recoverWriting: () => recoveries++), "Damaged bookkeeping bypassed grace.");
        now = 4; Check(barrier.TryEnterWriting(_ => { }, recoverWriting: () => recoveries++) && recoveries == 1 && barrier.UsedRecovery,
            "Damaged owner inventory permanently blocked saving.");
        barrier.RefreshHostProfile(new byte[] { 1 });
        Check(barrier.AcceptedCount == 1 && barrier.MissingPeers.Count == 0, "Frozen final host capture could not repair missing recovery state.");
        try { barrier.RefreshHostProfile(new byte[] { 2 }); Check(false, "Invalid final capture replaced host state."); }
        catch (InvalidDataException) { Check(barrier.AcceptedProfiles[0].Payload.SequenceEqual(new byte[] { 1 }), "Rejected refresh changed the accepted state."); }
        barrier.Complete(); damaged = false;
        try { barrier.RefreshHostProfile(new byte[] { 1 }); Check(false, "Refresh worked outside Writing."); }
        catch (InvalidOperationException) { Check(true, "Refresh requires Writing."); }
    }

    private static void LateFreshProfileSupersedesRetainedCandidate()
    {
        double now = 0; var profile = Guid.NewGuid(); bool ready = false;
        var barrier = new SaveBarrierRound(() => now, () => Array.Empty<uint>(), (_, bytes) => bytes.Length == 1, 1);
        barrier.Begin(new[] { new KeyValuePair<uint, Guid>(0, profile) }, 3); now = 3;
        Check(!barrier.TryEnterWriting(_ => { }, () => ready, (_, _) => new byte[] { 1 }, () => { }), "Pending world work was skipped by a checkpoint.");
        foreach (var part in SaveFlushPart.Split(barrier.Round, new byte[] { 2 })) barrier.Receive(0, part);
        ready = true;
        Check(barrier.TryEnterWriting(_ => { }, () => ready, (_, _) => new byte[] { 1 }, () => { }) && barrier.RetainedProfiles.Count == 0 &&
            barrier.AcceptedProfiles[0].Payload.SequenceEqual(new byte[] { 2 }) && !barrier.UsedRecovery,
            "Late fresh profile did not supersede the checkpoint candidate.");
        barrier.Complete();
        barrier.Begin(new[] { new KeyValuePair<uint, Guid>(0, profile) }, 3); now = 6;
        Check(barrier.TryEnterWriting(_ => { }, () => true, (_, _) => new byte[] { 1 }, () => { }) && barrier.UsedRecovery,
            "Checkpoint-only writing did not report retained state.");
        barrier.RefreshHostProfile(new byte[] { 2 });
        Check(!barrier.UsedRecovery && barrier.RecoveryReason.Length == 0 && barrier.RetainedProfiles.Count == 0,
            "A final fresh host snapshot was falsely reported as checkpoint retention."); barrier.Complete();
    }

    private static int Main()
    {
        try
        {
            TransientCaptureAndRelease(); CaptureDoesNotStopSettlement(); CheckpointsAreSeparateFromFreshProfiles();
            MissingHostCanUseWorldOnlyRecovery(); StuckWorldAdaptersAreBounded(); BarrierRejectsInvalidCheckpointsAndKeepsStrictDefault();
            DamagedOwnershipInventoryRecoversAtBound(); LateFreshProfileSupersedesRetainedCandidate();
            Console.WriteLine("PASS save collection recovery: 8 scenarios, " + assertions + " assertions; real Core and localhost UDP; no native game/disk save acceptance."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }
}
