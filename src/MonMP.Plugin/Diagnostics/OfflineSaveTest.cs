using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Plugin.Testing;
using MonMP.Core.Persistence;
using MonMP.Core.Session;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class OfflineSaveTest
    {
        private readonly MonMPPlugin plugin;
        private readonly GameplayProbe loader;
        private readonly string resultPath;
        private readonly string saveName = "MonMPOffline-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        private int phase, timestamp;
        private float nextAction;
        private bool finished;
        private readonly bool coordinated;
        private SidecarSnapshot? first;
        private string firstDirectory = "";
        private FileStream? failedExitWriterLock;
        public OfflineSaveTest(MonMPPlugin plugin, RuntimeProbe probe, bool coordinated = false)
        {
            this.coordinated = coordinated;
            this.plugin = plugin; loader = new GameplayProbe(probe, false);
            resultPath = Path.Combine(probe.DirectoryPath, coordinated ? "coordinated-reload-test.txt" : "offline-save-test.txt");
            File.WriteAllText(resultPath, "RUNNING: loading isolated offline gameplay");
        }
        public void Tick(Gameplay? gameplay)
        {
            if (finished) return;
            if (Time.unscaledTime > 220) { Fail("Timed out in phase " + phase); return; }
            if (!coordinated && plugin.Session?.IsRunning == true) throw new InvalidOperationException("Offline test unexpectedly started networking.");
            if (phase == 0) loader.Tick(gameplay);
            if (Time.unscaledTime < nextAction) return;
            if (phase == 0 && loader.Ready && gameplay?.Player != null)
            {
                gameplay.PauseGame(); Require(Time.timeScale == 0, "Offline pause no longer stops simulation.");
                gameplay.ResumeGame(); Require(Time.timeScale > 0, "Offline resume failed.");
                LocalTestIsolation.SetString("SaveName", saveName);
                AccessTools.Field(typeof(Cash_Manager), "cash").SetValue(UnityEngine.Object.FindObjectOfType<Cash_Manager>(), 1234);
                Atv().vehicle_PartsManager.Fuel_Tank.Get_FuelLevel = 7.25f;
                if (coordinated)
                {
                    AccessTools.Field(typeof(BasicNeeds_Manager), "LastTime_Ate").SetValue(UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>(), 1234);
                    LocalTestIsolation.SetInt("SaveTimestamp", -1); plugin.BeginPairedTestSession();
                }
                phase = 10;
            }
            else if (phase == 10 && (!coordinated || plugin.World?.PersonalProfileReady == true))
            {
                SaveSystem.Save(House_Location.SpecialMenu); phase = 11;
            }
            else if (phase == 11 && SaveFinished())
            {
                timestamp = SaveSystem.Get_LastSaveTimestamp;
                firstDirectory = Path.Combine(LocalTestIsolation.SaveRoot, saveName, timestamp.ToString());
                foreach (string file in new[] { "marguerite.txt", "braaaap.txt", "blopblop.txt", "lyne.txt" })
                    Require(new FileInfo(Path.Combine(firstDirectory, file)).Length > 0, "Native save file missing: " + file);
                Require(SaveSystem.IsSaveExist(saveName, timestamp), "Native save lookup missed the isolated save.");
                if (coordinated)
                {
                    first = SidecarStore.Load(firstDirectory, SessionOptions.SupportedGameHash);
                    Require(first != null && first.Document.Profiles.Count == 1, "Host profile was not published.");
                }
                File.WriteAllText(resultPath, "RUNNING: native save verified at " + firstDirectory + "; returning to menu");
                phase = 1; UnityEngine.Object.FindObjectOfType<EscapeMenuV2>().Button_QuitGameConfirmation();
                if (coordinated)
                {
                    Require(plugin.HostExitPending && plugin.Session?.IsRunning == true, "Host exit bypassed coordinated saving.");
                    UnityEngine.Object.FindObjectOfType<EscapeMenuV2>().Button_QuitGameConfirmation();
                    // A failed player barrier is recoverable through a native-only save.
                    // Hold the isolated save's writer lock so neither publication path can
                    // reserve a native generation until the negative exit check completes.
                    Require(LocalTestIsolation.Installed, "Exit-save failure injection requires isolated saves.");
                    failedExitWriterLock = new FileStream(Path.Combine(SaveSlot.GetSaveParentPath(LocalTestIsolation.SaveRoot, saveName), ".monmp-save.lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
                    plugin.World!.SaveRounds!.Fail("Injected orderly-exit save failure"); phase = 14;
                }
                nextAction = Time.unscaledTime + 1;
            }
            else if (phase == 14 && !plugin.HostExitPending)
            {
                Require(gameplay?.Player != null && plugin.Session?.IsRunning == true, "Failed exit save left the world.");
                var failedSave = plugin.World?.Saves;
                Require(failedSave != null && !failedSave.LastSaveSucceeded && !string.IsNullOrEmpty(failedSave.LastFailure), "Injected writer-lock failure was reported as a saved exit.");
                Require(SaveSystem.Get_LastSaveTimestamp == timestamp, "Failed exit save changed the published timestamp.");
                Require(SidecarStore.Load(firstDirectory, SessionOptions.SupportedGameHash)?.Document.Generation == first!.Document.Generation,
                    "Failed exit save changed the previous native checkpoint or sidecar.");
                failedExitWriterLock?.Dispose(); failedExitWriterLock = null;
                UnityEngine.Object.FindObjectOfType<EscapeMenuV2>().Button_QuitGameConfirmation();
                Require(plugin.HostExitPending, "Exit retry was not queued."); phase = 1;
            }
            else if (phase == 1 && gameplay == null)
            {
                var menu = UnityEngine.Object.FindObjectOfType<MainMenu_Manager>(); if (menu == null) return;
                if (coordinated)
                {
                    Require(plugin.Session?.IsRunning != true && !plugin.HostExitPending, "Successful exit did not stop hosting.");
                    timestamp = SaveSystem.Get_LastSaveTimestamp;
                    var exitSave = SidecarStore.Load(Path.Combine(LocalTestIsolation.SaveRoot, saveName, timestamp.ToString()), SessionOptions.SupportedGameHash)!;
                    Require(exitSave.Document.WorldId == first!.Document.WorldId && exitSave.Document.Generation != first.Document.Generation,
                        "Orderly exit did not publish a new save before leaving.");
                }
                LocalTestIsolation.SetString("SaveName", saveName); LocalTestIsolation.SetInt("SaveTimestamp", timestamp);
                menu.StartGame(false); phase = 2; nextAction = Time.unscaledTime + 10;
                File.WriteAllText(resultPath, "RUNNING: reloading native saved world");
            }
            else if (phase == 2 && gameplay?.Player != null && !gameplay.Get_isGamePaused)
            { phase = 3; nextAction = Time.unscaledTime + 5; }
            else if (phase == 3 && gameplay?.Player != null)
            {
                Require(UnityEngine.Object.FindObjectOfType<Cash_Manager>().GetCash == 1234, "Cash did not survive native reload.");
                Require(Mathf.Abs(Atv().vehicle_PartsManager.Fuel_Tank.Get_FuelLevel - 7.25f) < 0.01f, "Vehicle fuel did not survive native reload.");
                Require(gameplay.PlayerWalking.mover.Get_RigidBody != null && gameplay.Get_PlayerState == PlayerState.Walking, "Walking was not restored after reload.");
                if (coordinated)
                {
                    int counter = (int)AccessTools.Field(typeof(BasicNeeds_Manager), "LastTime_Ate").GetValue(UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>());
                    Require(counter >= 1234 && counter < 1264, "Sidecar-only eating counter was not restored at native load.");
                    AccessTools.Field(typeof(BasicNeeds_Manager), "LastTime_Ate").SetValue(UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>(), 7777);
                    plugin.BeginPairedTestSession();
                }
                phase = 12;
            }
            else if (phase == 12 && (!coordinated || plugin.World?.PersonalProfileReady == true))
            {
                if (coordinated) Require((int)AccessTools.Field(typeof(BasicNeeds_Manager), "LastTime_Ate").GetValue(UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>()) >= 7777,
                    "Starting host overwrote personal progress made after load.");
                SaveSystem.Save(House_Location.SpecialMenu); phase = 13;
            }
            else if (phase == 13 && SaveFinished())
            {
                Require(SaveSystem.IsSaveExist(saveName, SaveSystem.Get_LastSaveTimestamp), "Saving the reloaded world failed.");
                if (coordinated)
                {
                    var second = SidecarStore.Load(plugin.World!.Saves!.LastSavedDirectory, SessionOptions.SupportedGameHash)!;
                    Require(second.Document.WorldId == first!.Document.WorldId && second.Document.Generation != first.Document.Generation &&
                        second.Document.Profiles.Keys.OrderBy(k => k).SequenceEqual(first.Document.Profiles.Keys.OrderBy(k => k)), "Reload lost save lineage/profile identity.");
                    Require(SidecarStore.Load(firstDirectory, SessionOptions.SupportedGameHash)!.Document.Generation == first.Document.Generation, "Reload/save altered the previous generation.");
                    plugin.EndPairedTestSession();
                }
                finished = true;
                File.WriteAllText(resultPath, (coordinated ? "MONMP_COORDINATED_RELOAD_PASS" : "MONMP_OFFLINE_SAVE_PASS") +
                    "\nNative pause/resume, four-file save, return to menu, native world reload, cash/fuel restoration, walking physics and a second save passed." +
                    (coordinated ? " Coordinated sidecars retained world/profile identity with a new generation after session reconstruction; native load restored the sidecar-only eating counter and hosting preserved subsequent personal progress. Orderly host exit waited for publication, stayed in-world with its previous native checkpoint intact on an injected writer-lock I/O failure that also blocked native-only recovery, and succeeded on retry." : "") + "\nSave: " + saveName);
                Application.Quit();
            }
        }
        private bool SaveFinished()
        {
            if (!coordinated) return true;
            var saves = plugin.World?.Saves;
            Require(saves != null, "Host save coordinator disappeared.");
            Require(saves!.LastFailure == "", "Coordinated save failed: " + saves.LastFailure);
            if (saves.Pending) return false;
            Require(plugin.World!.SaveRounds!.Phase == SavePhase.Complete, "Coordinated save did not complete.");
            Require(!plugin.World.SaveGate.Active && Time.timeScale > 0, "Save left simulation frozen.");
            return true;
        }
        private static VehicleController Atv() => UnityEngine.Object.FindObjectsOfType<VehicleController>().Single(v => v.vehicle_type == Vehicle_Type.SmollATV);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        public void Fail(string message)
        {
            if (finished) return; finished = true;
            failedExitWriterLock?.Dispose(); failedExitWriterLock = null;
            File.WriteAllText(resultPath, (coordinated ? "MONMP_COORDINATED_RELOAD_FAIL\n" : "MONMP_OFFLINE_SAVE_FAIL\n") + message); Application.Quit(1);
        }
    }
}
