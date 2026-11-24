using System;
using System.Linq;
using MonMP.Core.Persistence;
using MonMP.Core.World;
using UnityEngine;
using System.IO;
using System.Collections.Generic;
using MonMP.Core.Session;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedSaveRoundTest
    {
        private const byte SignalMarker = 0x9a;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private int phase, ack;
        private byte pendingSignal;
        private float next, nextSignal, writingDeadline;
        private Guid writingRound;
        private readonly bool writeTest = Environment.GetCommandLineArgs().Contains("--monmp-native-save-test");
        private readonly string testSave = "MonMPNative-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Status => "native save collection phase " + phase + "; ack=" + ack + "; round=" + plugin.World?.SaveRounds?.Phase;
        internal PairedSaveRoundTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>().GetMappleSyrup = plugin.TestOptions.IsHost ? 0.25f : 0.5f;
            if (!plugin.TestOptions.IsHost) return;
            plugin.World!.Channel.CommandRequested += Acknowledge;
            foreach (float scale in new[] { 0.64f, 0.65f })
            {
                var item = plugin.Items!.TestSpawn(gameplay.PlayerWalking.transform.position + Vector3.forward + Vector3.up);
                item.transform.localScale = Vector3.one * scale;
            }
        }
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ProfileRestoreChannel.Entity && payload.Length == 2 && payload[0] == SignalMarker && payload[1] >= 1 && payload[1] <= 4) ack = Math.Max(ack, payload[1]); }
        private void Signal(byte stage)
        {
            pendingSignal = stage; nextSignal = Time.unscaledTime + .5f;
            // This immutable record avoids racing the clock's live revisions. Repeating
            // an idempotent stage also covers a request queued before synchronization.
            plugin.World!.Channel.Request(ProfileRestoreChannel.Entity, new[] { SignalMarker, stage });
        }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Native save round: " + message); }
        private string FreezeState()
        {
            var world = plugin.World!;
            return "gateActive=" + world.SaveGate.Active + "; frozen=" + world.SaveGate.Frozen + "; timeScale=" + Time.timeScale +
                "; localFlush=" + world.LocalFlushComplete + "; transactions=" + world.TransactionsSettled +
                "; personalEffect=" + world.Profile.HasPendingSaveEffect + "; owners=" + string.Join(",", world.Owners) +
                "; accepted=" + world.SaveRounds!.AcceptedCount + "; localFailure=" + world.SaveRounds.LocalFailure;
        }
        private bool FreezeForWrite()
        {
            var world = plugin.World!;
            if (writingRound != world.SaveRounds!.Round)
            {
                writingRound = world.SaveRounds.Round; writingDeadline = Time.unscaledTime + 5f;
                Debug.Log("MONMP_NATIVE_SAVE_WRITING: " + FreezeState());
            }
            // SaveRoundChannel collects only. This fixture is the writer, so it owns
            // the same strict final freeze as HostSaveCoordinator before native I/O.
            world.Profile.SettleForSave();
            if (!world.LocalFlushComplete || !world.TransactionsSettled || world.Profile.HasPendingSaveEffect)
            {
                Require(Time.unscaledTime < writingDeadline, "Native settlement did not finish before the writing freeze: " + FreezeState());
                return false;
            }
            if (!world.SaveGate.Frozen) world.SaveGate.Freeze();
            CheckFrozen(); return true;
        }
        private void CheckFrozen()
        {
            var world = plugin.World!;
            Require(world.SaveGate.Frozen && Time.timeScale == 0 && world.LocalFlushComplete && world.TransactionsSettled && !world.Profile.HasPendingSaveEffect,
                "Writing was reached before native ownership/effects were flushed and frozen. " + FreezeState());
            Require(!UnityEngine.Object.FindObjectOfType<GameAssets>().inputs.Player.enabled, "Player input remained enabled.");
        }
        private void CheckReleased()
        {
            var world = plugin.World!;
            Require(!world.SaveGate.Active && Time.timeScale > 0 && !world.Items.TransfersBlocked && !world.Stores.NewPurchasesBlocked,
                "Terminal save state did not release native gates.");
        }
        internal bool Tick()
        {
            var world = plugin.World; if (Time.unscaledTime < next || world?.Channel.FullySynchronized != true) return false;
            var saves = world.SaveRounds!;
            if (!plugin.TestOptions.IsHost && pendingSignal != 0 && Time.unscaledTime >= nextSignal) Signal(pendingSignal);
            if (plugin.TestOptions.IsHost)
            {
                if (phase == 0)
                {
                    ulong held = world.Items.TestTaggedItem(1, 0.64f); if (held == 0) return false;
                    world.Items.TestGrab(held); Require(world.Items.PeerHolding(0), "Host fixture pickup failed."); phase = 1;
                }
                else if (phase == 1 && ack >= 1)
                { Require(world.Owners.Length == 2, "Both owners were not present before collection."); saves.Begin(15); phase = 2; }
                else if (phase == 2 && saves.Phase == SavePhase.Writing)
                {
                    if (!FreezeForWrite() || ack < 2) return false;
                    CheckFrozen(); Require(world.Owners.Length == 0 && saves.AcceptedCount == 2, "Native collection did not flush both peers.");
                    var profiles = saves.MergeProfiles(Array.Empty<SidecarProfile>(), 1).ToDictionary(p => p.Id);
                    foreach (var peer in plugin.Session!.Peers.Values)
                        Require(PlayerProfileState.FromSection(profiles[peer.ProfileId].State[PlayerProfileState.SectionKey]).Needs[0] == (peer.Id == 0 ? 0.25f : 0.5f), "Profile was associated with the wrong persistent GUID.");
                    if (writeTest) CheckNativeWrite(true);
                    saves.Fail("Injected write failure."); CheckReleased(); phase = 3;
                }
                else if (phase == 3 && ack >= 3) { saves.Begin(15); phase = 4; }
                else if (phase == 4 && saves.Phase == SavePhase.Writing)
                { if (!FreezeForWrite() || ack < 4) return false; CheckFrozen(); if (writeTest) CheckNativeWrite(false); saves.Complete(); CheckReleased(); world.Channel.CommandRequested -= Acknowledge; return true; }
                return false;
            }
            if (phase == 0)
            {
                ulong held = world.Items.TestTaggedItem(1, 0.65f); if (held == 0) return false;
                var position = world.Items.TestItem(held)!.transform.position + Vector3.right;
                gameplay.PlayerWalking.transform.position = position; gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                phase = 1; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 1) { world.Items.TestGrab(world.Items.TestTaggedItem(1, 0.65f)); phase = 2; }
            else if (phase == 2 && world.Items.PeerHolding(plugin.Session!.LocalPeerId)) { Signal(1); phase = 3; }
            else if (phase == 3 && saves.Phase == SavePhase.Writing) { if (!FreezeForWrite()) return false; Signal(2); phase = 4; }
            else if (phase == 4 && saves.Phase == SavePhase.Failed) { CheckReleased(); Signal(3); phase = 5; }
            else if (phase == 5 && saves.Phase == SavePhase.Writing) { if (!FreezeForWrite()) return false; Signal(4); phase = 6; }
            else if (phase == 6 && saves.Phase == SavePhase.Complete) { CheckReleased(); pendingSignal = 0; return true; }
            return false;
        }
        private void CheckNativeWrite(bool fail)
        {
            Require(LocalTestIsolation.Installed, "Native staging test requires isolated storage.");
            var world = plugin.World!; string selected = LocalTestIsolation.GetString("SaveName"); int previous = SaveSystem.Get_LastSaveTimestamp;
            Directory.CreateDirectory(Path.Combine(LocalTestIsolation.SaveRoot, testSave));
            LocalTestIsolation.SetString("SaveName", testSave);
            try
            {
                using var slot = SaveSlot.Reserve(LocalTestIsolation.SaveRoot, testSave, TimeUtils.GetUnixTime());
                if (fail)
                {
                    bool rejected = false;
                    try { NativeSaveWriter.Write(slot, world, House_Location.SpecialMenu, _ => throw new IOException("Injected native write interruption")); }
                    catch (IOException) { rejected = true; }
                    Require(rejected && File.Exists(Path.Combine(slot.StagingDirectory, "marguerite.txt")) && !Directory.Exists(slot.DestinationDirectory), "Interrupted writer published an incomplete save.");
                    using var probe = new FileStream(Path.Combine(slot.StagingDirectory, "marguerite.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                else
                {
                    var fingerprint = NativeSaveWriter.Write(slot, world, House_Location.SpecialMenu);
                    var doc = new SidecarDocument(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), SessionOptions.SupportedGameHash,
                        SessionOptions.CurrentModVersion, new Dictionary<string, SidecarSection>(), world.SaveRounds!.MergeProfiles(Array.Empty<SidecarProfile>(), DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    // This standalone fixture generation starts a new world identity and
                    // performs no store purchases. Publish its real empty initial receipt
                    // ledger before the sidecar, as the production coordinator requires.
                    TransactionReceiptJournal.Commit(SaveSlot.GetSaveParentPath(LocalTestIsolation.SaveRoot, testSave),
                        new TransactionReceiptBinding(doc.WorldId, doc.Generation, slot.Timestamp, doc.GameBuild), Array.Empty<TransactionReceipt>());
                    SidecarStore.Commit(slot.StagingDirectory, new SidecarSnapshot(doc, fingerprint)); slot.Publish(doc.GameBuild, doc.WorldId);
                    var loaded = SidecarStore.Load(slot.DestinationDirectory, doc.GameBuild);
                    Require(loaded?.Document.Profiles.Count == 2 && SaveSystem.IsSaveExist(testSave, slot.Timestamp), "Published native/sidecar save not found.");
                    var player = SaveSystem.GetPlayerFromSave(testSave, slot.Timestamp);
                    Require(player != null && player.MappleSyrup == 0.25f && player.gameSaveData.TimeStamp == slot.Timestamp, "Native reader lost staged player values/timestamp.");
                }
                Require(LocalTestIsolation.GetString("SaveName") == testSave && SaveSystem.Get_LastSaveTimestamp == previous, "Writer changed selected save or published timestamp prematurely.");
            }
            finally { LocalTestIsolation.SetString("SaveName", selected); }
        }
    }
}
