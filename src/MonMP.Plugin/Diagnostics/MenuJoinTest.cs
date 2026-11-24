using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HarmonyLib;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using NWH.VehiclePhysics2;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class MenuJoinTest
    {
        private readonly MonMPPlugin plugin;
        private readonly GameplayProbe initialWorld;
        private readonly string resultPath;
        private readonly string saveName = "MonMPJoinGuard-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        private string saveDirectory = "", savedHash = "";
        private int phase, timestamp;
        private ulong light;
        private float next;
        private bool finished;
        private bool rejectionTested, preEntryCancellationTested, preEntryGuardChecked, cancellationTested;
        private GuestWorldLoader? loaded;
        // Start-LocalTest -SoakSeconds: after joining, the guest stays in the host's world this long
        // (walking a loop) and both copies write update-profile.txt for performance work.
        private readonly float soakSeconds;
        private float soakUntil;
        private Vector3 soakCenter;
        // --monmp-soak-still (cheat scripts): hold the guest in place instead of walking the loop.
        private readonly bool soakStill;
        public MenuJoinTest(MonMPPlugin plugin, RuntimeProbe probe)
        {
            this.plugin = plugin;
            // Start-LocalTest -HostSave: the host loads a played save to reproduce catalog mismatches.
            var args = Environment.GetCommandLineArgs();
            string? hostSave = args.FirstOrDefault(a => a.StartsWith("--monmp-host-save=", StringComparison.Ordinal))?.Substring("--monmp-host-save=".Length);
            string? hostStamp = args.FirstOrDefault(a => a.StartsWith("--monmp-host-save-timestamp=", StringComparison.Ordinal))?.Substring("--monmp-host-save-timestamp=".Length);
            initialWorld = plugin.TestOptions.IsHost && !string.IsNullOrEmpty(hostSave) && int.TryParse(hostStamp, out int stamp)
                ? new GameplayProbe(probe, false, hostSave!, stamp) : new GameplayProbe(probe, false);
            string? soak = args.FirstOrDefault(a => a.StartsWith("--monmp-soak=", StringComparison.Ordinal))?.Substring("--monmp-soak=".Length);
            if (float.TryParse(soak, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds)) soakSeconds = seconds;
            soakStill = args.Contains("--monmp-soak-still");
            resultPath = Path.Combine(probe.DirectoryPath, "menu-join-test.txt");
            File.WriteAllText(resultPath, "RUNNING: preparing isolated worlds");
        }
        public void Tick(Gameplay? gameplay)
        {
            if (finished) return;
            if (Time.unscaledTime > 300 + soakSeconds) { Fail("Timed out in phase " + phase); return; }
            if (phase == 0) initialWorld.Tick(gameplay);
            if (Time.unscaledTime < next) return;
            if (plugin.TestOptions.IsHost) { Host(gameplay); return; }
            if (phase == 0 && initialWorld.Ready && gameplay?.Player != null)
            {
                LocalTestIsolation.SetString("SaveName", saveName);
                AccessTools.Field(typeof(Cash_Manager), "cash").SetValue(UnityEngine.Object.FindObjectOfType<Cash_Manager>(), 5432);
                Atv().vehicle_PartsManager.Fuel_Tank.Get_FuelLevel = 3.5f;
                SaveSystem.Save(House_Location.SpecialMenu); timestamp = SaveSystem.Get_LastSaveTimestamp;
                saveDirectory = Path.Combine(LocalTestIsolation.SaveRoot, saveName, timestamp.ToString());
                Require(Directory.GetFiles(saveDirectory).Length == 4, "Native foreign save incomplete."); savedHash = SaveHash();
                UnityEngine.Object.FindObjectOfType<EscapeMenuV2>().Button_QuitGameConfirmation(); phase = 1; next = Time.unscaledTime + 5;
                Note("foreign native save prepared; returning to menu before authenticating");
            }
            else if (phase == 1 && gameplay == null && UnityEngine.Object.FindObjectOfType<MainMenu_Manager>() != null)
            {
                if (!plugin.DiscoverPairedHost()) return;
                LocalTestIsolation.SetString("SaveName", saveName); LocalTestIsolation.SetInt("SaveTimestamp", timestamp);
                LocalTestIsolation.SetInt("HardMode", 1); LocalTestIsolation.SetInt("Permadeath", 1);
                if (!rejectionTested)
                {
                    plugin.BeginRejectedMenuTestSession(); phase = 20; Note("checking rejected authentication stays in the menu"); return;
                }
                plugin.BeginPairedTestSession(); Require(plugin.GuestWorld != null, "Join did not begin from the menu.");
                loaded = plugin.GuestWorld; phase = 2; Note("authenticated menu join is loading host scene with foreign save still selected");
            }
            else if (phase == 2)
            {
                if (!preEntryCancellationTested && loaded?.SceneLoadRequested == true && SceneManager.GetActiveScene().buildIndex == 1)
                {
                    var earlyCancelled = loaded;
                    plugin.EndPairedTestSession();
                    Require(ReferenceEquals(plugin.GuestWorld, earlyCancelled) && earlyCancelled.SuppressDisk && earlyCancelled.HoldPresentation &&
                        !earlyCancelled.EnteredWorld && !earlyCancelled.Activated && !earlyCancelled.CanDiscard,
                        "Disconnect before scene entry released the pending guest-world guards or discarded its loader.");
                    phase = 22; next = 0; Note("disconnecting immediately after StartGame was requested, before scene entry"); return;
                }
                if (loaded?.NativeReady == true && !loaded.Activated)
                {
                    Require(!UnityEngine.Object.FindObjectOfType<GameAssets>().inputs.Player.enabled && AudioListener.pause, "Input or audio released before snapshot catch-up.");
                    if (!cancellationTested)
                    {
                        plugin.EndPairedTestSession(); Require(loaded.HoldPresentation && loaded.SuppressDisk, "Cancelled load released its guards.");
                        phase = 21; next = Time.unscaledTime + 5; Note("cancelling after native scene initialization before snapshot activation"); return;
                    }
                }
                if (loaded?.Activated != true || gameplay?.Player == null || plugin.Lights?.Ready != true) return;
                // Activation releases presentation before saved/default spawn recovery
                // finishes. Wait before placing the interaction fixture in the world.
                if (plugin.World?.PersonalProfileReady != true || plugin.World.ReconnectPositions?.Settled != true) return;
                // Lights the host's progression just activated wake over the next frames; the guest
                // reconciles them to the host's records.
                if (settleUntil == 0) { settleUntil = Time.unscaledTime + 3f; return; }
                if (Time.unscaledTime < settleUntil && !plugin.Lights.TestSynchronized) return;
                Require(loaded.BlockedSaveReads > 0 && loaded.PreservedSceneObjects > 0 && loaded.HeldInputFrames > 0, "Native disk/deletion/input guards were not exercised.");
                Require(plugin.World!.Channel.FullySynchronized, "Snapshot catch-up missing.");
                var configuration = loaded.Configuration ?? throw new InvalidOperationException("Host configuration missing.");
                Require(gameplay.Get_isHardMode == configuration.HardMode && gameplay.Get_isPermadeath == configuration.Permadeath,
                    "Host difficulty missing: actual=" + gameplay.Get_isHardMode + "/" + gameplay.Get_isPermadeath +
                    "; expected=" + configuration.HardMode + "/" + configuration.Permadeath + ".");
                Require(UnityEngine.Object.FindObjectOfType<Cash_Manager>().GetCash != 5432, "Foreign local save cash leaked into remote world.");
                var remoteAtv = AtvOrNull();
                uint atvBit = 1u << (int)Vehicle_Type.SmollATV;
                Require(remoteAtv != null || ((configuration.HiddenVehicles & atvBit) != 0 && (configuration.StoredVehicles & atvBit) == 0),
                    "Host's expected starter ATV is missing.");
                Require(remoteAtv == null || Mathf.Abs(remoteAtv.vehicle_PartsManager.Fuel_Tank.Get_FuelLevel - 3.5f) > 0.01f,
                    "Foreign local vehicle fuel leaked into remote world.");
                Require(MainKonigCap().isCapFullyUnScrewed, "Guest did not receive the host's open main Konig fuel cap.");
                VerifyPreferences(); Require(SaveHash() == savedHash, "Joining changed the foreign save.");
                light = plugin.Lights.TestHostChangedEntity;
                Require(plugin.Lights.TestLive(light), "The host's test light is not live in the guest world.");
                Require(plugin.Lights.TestRecord(light).Revision >= 3 && plugin.Lights.TestSynchronized, "Join missed host change committed during scene loading: revision=" + plugin.Lights.TestRecord(light).Revision + "; " + plugin.Lights.TestMismatches);
                int lastSave = SaveSystem.Get_LastSaveTimestamp; SaveSystem.Save(House_Location.SpecialMenu);
                SaveSystem.HardMode_Permadeath(); Require(!SaveSystem.DeleteSaveName(saveName), "Guest save deletion was not blocked.");
                Require(lastSave == SaveSystem.Get_LastSaveTimestamp && SaveHash() == savedHash, "Guest save was not blocked.");
                var location = plugin.Lights.TestPosition(light) + new Vector3(0, -0.5f, 1.5f);
                gameplay.PlayerWalking.transform.position = location; gameplay.Get_Player_Rigidbody.position = location; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                phase = 3; next = Time.unscaledTime + 0.8f; Note("host snapshot and guards verified; testing native guest interaction");
                if (soakSeconds > 0)
                {
                    UpdateProfiler.Reset(); soakCenter = location; soakUntil = Time.unscaledTime + soakSeconds; phase = 30; next = 0;
                    Note("soaking in the host world for " + soakSeconds + " s");
                }
            }
            else if (phase == 30)
            {
                // Walk a 12 m circle so distance-based presentation and streaming change.
                float angle = (Time.unscaledTime % 40f) / 40f * Mathf.PI * 2f;
                var target = soakCenter + (soakStill ? Vector3.zero : new Vector3(Mathf.Cos(angle) * 12f, 0, Mathf.Sin(angle) * 12f));
                if (!soakStill && Physics.Raycast(target + Vector3.up * 30f, Vector3.down, out var hit, 60f, ~0, QueryTriggerInteraction.Ignore)) target = hit.point + Vector3.up * 0.1f;
                gameplay!.PlayerWalking.transform.position = target; gameplay.Get_Player_Rigidbody.position = target; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                if (Time.unscaledTime < soakUntil) return;
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(resultPath)!, "soak-profile.txt"), UpdateProfiler.Report());
                gameplay.PlayerWalking.transform.position = soakCenter; gameplay.Get_Player_Rigidbody.position = soakCenter;
                phase = 3; next = Time.unscaledTime + 0.8f; Note("soak finished; testing native guest interaction");
            }
            else if (phase == 3)
            {
                plugin.Lights!.TestClick(light); phase = 4;
            }
            else if (phase == 4 && plugin.Lights!.TestRecord(light).Revision >= 4)
            {
                plugin.EndPairedTestSession(); Require(loaded!.SuppressDisk && loaded.HoldPresentation, "Disconnect released the remote-world guard before scene unload.");
                int lastSave = SaveSystem.Get_LastSaveTimestamp; SaveSystem.Save(House_Location.SpecialMenu);
                SaveSystem.HardMode_Permadeath(); Require(!SaveSystem.DeleteSaveName(saveName), "Disconnected remote world could delete the selected save.");
                Require(lastSave == SaveSystem.Get_LastSaveTimestamp && SaveHash() == savedHash, "Disconnected remote world could overwrite selected save.");
                phase = 5; next = Time.unscaledTime + 5; Note("disconnecting to menu with save guards retained");
            }
            else if (phase == 5 && gameplay == null && UnityEngine.Object.FindObjectOfType<MainMenu_Manager>() != null)
            {
                Require(plugin.GuestWorld == null && plugin.Session?.IsRunning != true, "Guest load state survived menu return.");
                VerifyPreferences(); Require(SaveHash() == savedHash, "Disconnect changed the foreign save.");
                UnityEngine.Object.FindObjectOfType<MainMenu_Manager>().StartGame(false); phase = 6; next = Time.unscaledTime + 10;
                Note("reloading original native save offline");
            }
            else if (phase == 6 && gameplay?.Player != null && !gameplay.Get_isGamePaused)
            { phase = 7; next = Time.unscaledTime + 5; }
            else if (phase == 7 && gameplay?.Player != null)
            {
                Require(UnityEngine.Object.FindObjectOfType<Cash_Manager>().GetCash == 5432 && Mathf.Abs(Atv().vehicle_PartsManager.Fuel_Tank.Get_FuelLevel - 3.5f) < 0.01f, "Original save did not reload after leaving guest world.");
                Require(SaveSystem.GetPlayerFromSave() != null && SaveHash() == savedHash && gameplay.Get_Player_Rigidbody != null, "Offline disk reads or walking did not recover.");
                gameplay.PauseGame(); Require(Time.timeScale == 0, "Offline pause was not restored."); gameplay.ResumeGame();
                string deletionProbe = "MonMPEmptyDeleteProbe-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(Path.Combine(LocalTestIsolation.SaveRoot, deletionProbe));
                Require(SaveSystem.DeleteSaveName(deletionProbe) && !Directory.Exists(Path.Combine(LocalTestIsolation.SaveRoot, deletionProbe)), "Offline native deletion remained blocked.");
                Pass("Guest: native foreign save remained selected and byte-identical; rejected authentication, pre-entry and post-NativeReady cancellation with retry, menu authentication, save-read/object-deletion guards, blocked saving/permadeath/save deletion, host difficulty, loading input/audio, catch-up, live interaction, guarded disconnect/menu return and offline cash/fuel/walking reload passed. Offline deletion of a new empty test directory remained available.");
            }
            else if (phase == 20 && plugin.Session?.IsRunning != true)
            {
                // StopSession clears the session object but retains its terminal reason for
                // the native menu. Require the explicit rejection, not merely a stopped join.
                Require(gameplay == null && plugin.GuestWorld == null && plugin.MenuStatus.Contains("Incorrect session password"), "Rejected authentication loaded a world, retained guards or lost its password rejection reason.");
                VerifyPreferences(); Require(SaveHash() == savedHash, "Rejected join changed the foreign save.");
                rejectionTested = true; phase = 1; next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 21 && gameplay == null && UnityEngine.Object.FindObjectOfType<MainMenu_Manager>() != null)
            {
                Require(plugin.GuestWorld == null, "Cancelled loading retained guest state after menu return.");
                VerifyPreferences(); Require(SaveHash() == savedHash, "Cancelled join changed the foreign save.");
                cancellationTested = true; phase = 1; next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 22)
            {
                if (loaded?.EnteredWorld == true && SceneManager.GetActiveScene().buildIndex == 2 && !preEntryGuardChecked)
                {
                    Require(loaded.SuppressDisk && loaded.HoldPresentation && !loaded.CanDiscard && ReferenceEquals(plugin.GuestWorld, loaded),
                        "Pre-entry cancellation released the guest-world guards before returning to the menu.");
                    int lastSave = SaveSystem.Get_LastSaveTimestamp;
                    SaveSystem.Save(House_Location.SpecialMenu);
                    Require(!SaveSystem.DeleteSaveName(saveName), "Pre-entry-cancelled guest world could delete the selected save.");
                    Require(lastSave == SaveSystem.Get_LastSaveTimestamp && SaveHash() == savedHash,
                        "Pre-entry-cancelled guest world changed the selected save.");
                    preEntryGuardChecked = true;
                    Note("cancelled transition entered the host scene with save guards retained; waiting for safe menu return");
                }
                if (loaded?.EnteredWorld == true && loaded.CanDiscard && SceneManager.GetActiveScene().buildIndex == 1 &&
                    gameplay == null && UnityEngine.Object.FindObjectOfType<MainMenu_Manager>() != null)
                {
                    Require(plugin.GuestWorld == null && plugin.Session?.IsRunning != true,
                        "Pre-entry-cancelled loader was not discarded after confirmed menu return.");
                    Require(preEntryGuardChecked, "Pre-entry cancellation did not observe the guarded host-scene transition.");
                    VerifyPreferences(); Require(SaveHash() == savedHash, "Pre-entry cancellation changed the foreign save.");
                    preEntryCancellationTested = true; phase = 1; next = Time.unscaledTime + 0.2f;
                    Note("pre-entry cancellation safely returned to menu; retrying menu authentication");
                }
            }
        }
        private void Host(Gameplay? gameplay)
        {
            if (phase == 0 && initialWorld.Ready && gameplay?.Player != null)
            {
                // A cap left fully unscrewed before hosting sits under its open location. Its record
                // must still match a fresh guest's catalog, or every join is refused.
                var cap = MainKonigCap();
                Require(AccessTools.Field(typeof(Part_FuelTankCap), "OriginalParent").GetValue(cap) != null, "Main Konig fuel cap did not start before hosting.");
                AccessTools.Field(typeof(Part_FuelTankCap), "Screwed").SetValue(cap, 0f);
                AccessTools.Method(typeof(Part_FuelTankCap), "UpdateVisual").Invoke(cap, null);
                Require(cap.isCapFullyUnScrewed && cap.transform.parent == (Transform)AccessTools.Field(typeof(Part_FuelTankCap), "CapOpenLocation").GetValue(cap),
                    "Main Konig fuel cap did not move to its open location.");
                plugin.BeginPairedTestSession(); Require(plugin.Session?.IsHost == true, "Host did not start.");
                light = plugin.Lights!.TestEntity; plugin.Lights.TestClick(light); phase = 1; Note("host listening while guest prepares a different local save");
            }
            else if (phase == 1 && plugin.Session!.Peers.Count == 2)
            { plugin.Lights!.TestClick(light); phase = 2; UpdateProfiler.Reset(); Note("host changed light while guest scene was loading"); }
            else if (phase == 2 && plugin.Lights!.TestRecord(light).Revision >= 4) { phase = 3; Note("guest native interaction accepted; waiting for clean departure"); }
            else if (phase == 3 && plugin.Session!.Peers.Count == 1)
            {
                if (soakSeconds > 0) File.WriteAllText(Path.Combine(Path.GetDirectoryName(resultPath)!, "soak-profile.txt"), UpdateProfiler.Report());
                Pass("Host: authenticated menu guest, retained simulation during scene loading, delivered intervening light change, accepted guest native interaction and cleaned up departure.");
            }
        }
        private void VerifyPreferences() => Require(LocalTestIsolation.GetString("SaveName") == saveName && LocalTestIsolation.GetInt("SaveTimestamp") == timestamp &&
            LocalTestIsolation.GetInt("HardMode") == 1 && LocalTestIsolation.GetInt("Permadeath") == 1, "Join altered the user's selected save/difficulty preferences.");
        private string SaveHash()
        {
            using var sha = SHA256.Create();
            return string.Join(";", Directory.GetFiles(saveDirectory).OrderBy(p => p).Select(p => BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(p)))));
        }
        // The scene's starter ATV, even when the host's world keeps it stored (inactive) in a garage.
        private float settleUntil;
        private static Part_FuelTankCap MainKonigCap() => UnityEngine.Object.FindObjectsOfType<Part_FuelTankCap>(true)
            .Single(cap => cap.gameObject.scene.IsValid() && cap.transform.root.name == "Konig" && cap.GetComponentsInParent<InteractableItems>(true).Length == 0);
        private static VehicleController Atv() => AtvOrNull() ?? throw new InvalidOperationException("Required offline starter ATV is missing.");
        private static VehicleController? AtvOrNull() => UnityEngine.Object.FindObjectsOfType<VehicleController>(true)
            .SingleOrDefault(v => v.vehicle_type == Vehicle_Type.SmollATV && v.gameObject.scene.IsValid() && !v.name.EndsWith("(Clone)"));
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Menu join test: " + message); }
        private void Note(string message) => File.WriteAllText(resultPath, "RUNNING: " + message);
        private void Pass(string message) { finished = true; File.WriteAllText(resultPath, "MONMP_MENU_JOIN_PASS\n" + message); Application.Quit(); }
        public void Fail(string message) { if (finished) return; finished = true; File.WriteAllText(resultPath, "MONMP_MENU_JOIN_FAIL\n" + message); Application.Quit(1); }
    }
}
