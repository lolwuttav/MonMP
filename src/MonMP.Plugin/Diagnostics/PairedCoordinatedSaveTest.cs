using System;
using System.IO;
using System.Linq;
using MonMP.Core.Persistence;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Testing;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedCoordinatedSaveTest
    {
        private const byte SignalMarker = 0x9c;
        private static string? preparedSaveName, preparedOriginalName;
        private static int preparedOriginalTimestamp;
        private readonly MonMPPlugin plugin;
        private readonly string originalName;
        private readonly int originalTimestamp;
        private SidecarSnapshot? first;
        private string firstDirectory = "";
        private int phase, ack;
        private float next, nextSignal;
        private byte pendingSignal;
        private WorldChannel? signalChannel;
        public string Status => "coordinated save phase " + phase + "; ack=" + ack + "; save=" + plugin.World?.SaveRounds?.Phase;
        internal PairedCoordinatedSaveTest(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            originalName = plugin.TestOptions.IsHost ? preparedOriginalName ?? throw new InvalidOperationException("Coordinated save fixture was not selected before hosting.") : LocalTestIsolation.GetString("SaveName");
            originalTimestamp = plugin.TestOptions.IsHost ? preparedOriginalTimestamp : LocalTestIsolation.GetInt("SaveTimestamp");
            if (!plugin.TestOptions.IsHost)
            { UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>().GetMappleSyrup = 0.37f; GameManager.Get_PlayerStats[17] = 42; return; }
            Require(LocalTestIsolation.GetString("SaveName") == preparedSaveName && LocalTestIsolation.GetInt("SaveTimestamp") == -1,
                "Fixture save selection changed after the coordinator bound its history.");
            plugin.World!.Channel.CommandRequested += Acknowledge;
        }
        internal static void PrepareHostSave()
        {
            if (preparedSaveName != null) return;
            Require(LocalTestIsolation.Installed, "Fixture save selection requires isolated storage.");
            preparedOriginalName = LocalTestIsolation.GetString("SaveName"); preparedOriginalTimestamp = LocalTestIsolation.GetInt("SaveTimestamp");
            preparedSaveName = "MonMPCoord-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            LocalTestIsolation.SetString("SaveName", preparedSaveName); LocalTestIsolation.SetInt("SaveTimestamp", -1);
        }
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ProfileRestoreChannel.Entity && payload.Length == 2 && payload[0] == SignalMarker && payload[1] >= 1 && payload[1] <= 3) ack = Math.Max(ack, payload[1]); }
        private void Signal(byte stage)
        {
            signalChannel = plugin.World!.Channel; pendingSignal = stage; nextSignal = Time.unscaledTime + .5f;
            signalChannel.Request(ProfileRestoreChannel.Entity, new[] { SignalMarker, stage });
        }
        private static SidecarSnapshot LoadSidecar(string directory, string checkpoint) =>
            SidecarStore.Load(directory, SessionOptions.SupportedGameHash) ?? throw new InvalidOperationException("Coordinated save: " + checkpoint +
                " has no complete multiplayer sidecar: " + (SaveSlot.LoadNativeOnly(directory, SessionOptions.SupportedGameHash)?.RecoveryReason ?? "No recovery marker was published."));
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Coordinated save: " + message); }
        internal bool Tick()
        {
            if (pendingSignal != 0 && !ReferenceEquals(signalChannel, plugin.World?.Channel)) { pendingSignal = 0; signalChannel = null; }
            if (Time.unscaledTime < next) return false;
            if (!plugin.TestOptions.IsHost && phase == 3) { plugin.BeginPairedTestSession(); phase = 4; return false; }
            var world = plugin.World; if (world?.Channel.FullySynchronized != true || !world.PersonalProfileReady) return false;
            if (!plugin.TestOptions.IsHost)
            {
                if (pendingSignal != 0 && Time.unscaledTime >= nextSignal) Signal(pendingSignal);
                if (phase == 0) { Signal(1); phase = 1; }
                else if (phase == 1 && world.SaveRounds!.Phase == SavePhase.Complete)
                { Require(!world.SaveGate.Active && Time.timeScale > 0, "First save left guest frozen."); Signal(2); phase = 2; }
                else if (phase == 2 && world.Economy.TestState.CashCents == 1091)
                {
                    Require(world.SaveRounds!.Phase == SavePhase.Complete && !world.SaveGate.Active && Time.timeScale > 0, "Second save left guest frozen.");
                    Require(LocalTestIsolation.GetString("SaveName") == originalName && LocalTestIsolation.GetInt("SaveTimestamp") == originalTimestamp, "Host save changed guest selection.");
                    UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>().GetMappleSyrup = 0.63f; GameManager.Get_PlayerStats[17] = 84;
                    phase = 6;
                }
                else if (phase == 6 && world.Economy.TestState.CashCents == 1093)
                {
                    plugin.EndPairedTestSession();
                    UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>().GetMappleSyrup = 0.91f; GameManager.Get_PlayerStats[17] = 123;
                    phase = 3; next = Time.unscaledTime + 1;
                }
                else if (phase == 4)
                {
                    Require(Math.Abs(UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>().GetMappleSyrup - 0.63f) < 0.01f && GameManager.Get_PlayerStats[17] == 84,
                        "Reconnect did not restore the newer unsaved GUID-bound native profile."); Signal(3); phase = 5;
                }
                else if (phase == 5 && world.Economy.TestState.CashCents == 1092)
                {
                    plugin.EndPairedTestSession();
                    Require(Math.Abs(UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>().GetMappleSyrup - 0.91f) < 0.001f && GameManager.Get_PlayerStats[17] == 123,
                        "Disconnect did not restore the guest's distinct local personal baseline."); return true;
                }
                return false;
            }
            var saves = world.Saves!;
            if (phase == 0 && ack >= 1)
            {
                int prior = SaveSystem.Get_LastSaveTimestamp; SaveSystem.Save(House_Location.SpecialMenu);
                Require(saves.Pending && SaveSystem.Get_LastSaveTimestamp == prior, "Native save bypassed asynchronous collection.");
                SaveSystem.Save(House_Location.SpecialMenu); // Duplicate native trigger must not start another round.
                phase = 1;
            }
            else if (phase == 1 && !saves.Pending)
            {
                Require(world.SaveRounds!.Phase == SavePhase.Complete && saves.LastFailure == "", "First native-triggered save failed: " + saves.LastFailure);
                firstDirectory = saves.LastSavedDirectory; first = LoadSidecar(firstDirectory, "First native save");
                Require(first.Document.Profiles.Count == 2 && SaveSystem.Get_LastSaveTimestamp == LocalTestIsolation.GetInt("SaveTimestamp"), "Native timestamp/profile publication failed."); phase = 2;
            }
            else if (phase == 2 && ack >= 2) { SaveSystem.Save(House_Location.SpecialMenu); Require(saves.Pending, "Second native save was not queued."); phase = 3; }
            else if (phase == 3 && !saves.Pending)
            {
                Require(world.SaveRounds!.Phase == SavePhase.Complete && saves.LastFailure == "" && saves.LastSavedDirectory != firstDirectory, "Second save failed or reused a destination.");
                var second = LoadSidecar(saves.LastSavedDirectory, "Second native save");
                Require(second.Document.WorldId == first!.Document.WorldId && second.Document.Generation != first.Document.Generation &&
                    second.Document.Profiles.Keys.OrderBy(k => k).SequenceEqual(first.Document.Profiles.Keys.OrderBy(k => k)), "Save lineage/profile identity changed.");
                Require(LoadSidecar(firstDirectory, "Previous native save").Document.Generation == first.Document.Generation, "Previous save was changed.");
                var player = SaveSystem.GetPlayerFromSave(LocalTestIsolation.GetString("SaveName"), SaveSystem.Get_LastSaveTimestamp);
                Require(player != null && player.gameSaveData.TimeStamp == SaveSystem.Get_LastSaveTimestamp && !world.SaveGate.Active && Time.timeScale > 0, "Native readback or gate cleanup failed.");
                UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(1091, new float[5]);
                phase = 4;
            }
            else if (phase == 4)
            {
                var guest = plugin.Session!.Peers.Values.Single(p => p.Id != 0);
                if (world.LiveProfiles!.Find(guest.ProfileId)?.Statistics[17] == 84)
                {
                    var onDisk = LoadSidecar(saves.LastSavedDirectory, "Completed disk save");
                    Require(PlayerProfileState.FromSection(onDisk.Document.Profiles[guest.ProfileId].State[PlayerProfileState.SectionKey]).Statistics[17] == 42,
                        "Live sampling changed the completed disk save.");
                    UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(1093, new float[5]); phase = 5;
                }
            }
            else if (phase == 5 && ack >= 3)
            {
                LocalTestIsolation.SetString("SaveName", originalName); LocalTestIsolation.SetInt("SaveTimestamp", originalTimestamp);
                UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(1092, new float[5]);
                world.Channel.CommandRequested -= Acknowledge; return true;
            }
            return false;
        }
    }
}
