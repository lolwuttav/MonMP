using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Persistence;
using MonMP.Core.Session;
using MonMP.Plugin.Patches;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Two isolated game processes exercise the staged-save boundary: one is hard-killed
    // after all native files and the sidecar are durable, then a new process loads recovery.
    internal sealed class CrashRecoveryTest
    {
        private const string CreateFlag = "--monmp-crash-recovery-create";
        private const string VerifyFlag = "--monmp-crash-recovery-verify";
        private static CrashRecoveryTest? active;
        private readonly MonMPPlugin plugin;
        private readonly RuntimeProbe probe;
        private readonly bool create;
        private readonly string saveName;
        private readonly string resultPath;
        private readonly string boundaryPath;
        private readonly int expectedTimestamp;
        private float next;
        private float loadedAt = -1;
        private bool requestedWorld, sessionStarted, saveRequested, finished;

        internal CrashRecoveryTest(MonMPPlugin plugin, RuntimeProbe probe, string[] args)
        {
            this.plugin = plugin; this.probe = probe;
            bool creating = args.Contains(CreateFlag), verifying = args.Contains(VerifyFlag);
            if (creating == verifying) throw new ArgumentException("Select exactly one staged-save recovery phase.");
            create = creating;
            string? requestedName = args.SingleOrDefault(a => a.StartsWith("--monmp-crash-recovery-save=", StringComparison.Ordinal));
            saveName = create
                ? requestedName?.Substring("--monmp-crash-recovery-save=".Length) ?? throw new ArgumentException("Crash recovery save name is required.")
                : LocalTestIsolation.GetString("SaveName");
            if (saveName.Length == 0 || saveName.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0)
                throw new ArgumentException("Crash recovery save name is invalid.");
            expectedTimestamp = create ? -1 : LocalTestIsolation.GetInt("SaveTimestamp", -1);
            if (verifying && expectedTimestamp <= 0)
                throw new InvalidDataException("Startup did not select the recovered native save.");
            resultPath = Path.Combine(probe.DirectoryPath, "crash-recovery-test.txt");
            boundaryPath = Path.Combine(probe.DirectoryPath, "crash-recovery-boundary.txt");
            if (create)
            {
                active = this;
                if (File.Exists(boundaryPath)) File.Delete(boundaryPath);
                File.WriteAllText(resultPath, "RUNNING: loading isolated new game before staged-save crash");
            }
            else File.WriteAllText(resultPath, "RUNNING: loading the save selected by startup recovery");
        }

        internal static void KillAfterStagedCommit(SaveSlot slot)
        {
            var test = active;
            if (test == null || !test.create) return;
            if (MonMPPlugin.Instance?.TestOptions.Enabled != true || !LocalTestIsolation.Installed)
                throw new InvalidOperationException("Refusing a staged-save crash outside the isolated test copy.");
            var snapshot = SidecarStore.Load(slot.StagingDirectory, SessionOptions.SupportedGameHash)
                ?? throw new InvalidDataException("Staged sidecar could not be read at the crash boundary.");
            string evidence = "MONMP_STAGED_SAVE_CRASH_BOUNDARY\n" +
                "save=" + test.saveName + "\n" +
                "stage=" + slot.StagingDirectory + "\n" +
                "destination=" + slot.DestinationDirectory + "\n" +
                "timestamp=" + slot.Timestamp.ToString(CultureInfo.InvariantCulture) + "\n" +
                "sourceTimestamp=" + test.expectedTimestamp.ToString(CultureInfo.InvariantCulture) + "\n" +
                "world=" + snapshot.Document.WorldId.ToString("D") + "\n" +
                "generation=" + snapshot.Document.Generation.ToString("D") + "\n";
            using (var file = new FileStream(test.boundaryPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(file)) { writer.Write(evidence); writer.Flush(); file.Flush(true); }
            // Kill the actual game process before SaveSlot.Publish and before its writer lock
            // can be disposed. Windows releases the lock as part of process teardown.
            Process.GetCurrentProcess().Kill();
        }

        internal void Tick(Gameplay? gameplay)
        {
            if (finished) return;
            try
            {
                if (Time.unscaledTime > 240) throw new TimeoutException("Crash recovery fixture timed out.");
                if (Time.unscaledTime < next) return;
                if (!requestedWorld)
                {
                    var menu = UnityEngine.Object.FindObjectOfType<MainMenu_Manager>();
                    if (menu == null || Time.unscaledTime < 8) return;
                    if (create)
                    {
                        LocalTestIsolation.SetString("SaveName", saveName);
                        LocalTestIsolation.SetInt("SaveTimestamp", -1);
                        LocalTestIsolation.SetInt("HardMode", 0);
                        LocalTestIsolation.SetInt("Permadeath", 0);
                        LocalTestIsolation.Save();
                    }
                    else if (LocalTestIsolation.GetString("SaveName") != saveName || LocalTestIsolation.GetInt("SaveTimestamp") != expectedTimestamp)
                        throw new InvalidDataException("Recovered save selection changed before the native menu load.");
                    menu.StartGame(false);
                    requestedWorld = true;
                    next = Time.unscaledTime + 1;
                    return;
                }
                if (gameplay?.Player == null || gameplay.Get_isGamePaused || !NativeLoadGate.Ready) return;
                if (loadedAt < 0) loadedAt = Time.unscaledTime;
                if (!create)
                {
                    if (Time.unscaledTime < loadedAt + 5) return;
                    VerifyLoadedRecovery();
                    return;
                }
                if (!sessionStarted)
                {
                    SetKnownNativeState();
                    plugin.BeginPairedTestSession();
                    sessionStarted = true;
                    next = Time.unscaledTime + 1;
                    return;
                }
                if (plugin.World?.PersonalProfileReady != true) return;
                if (saveRequested) return;
                saveRequested = true;
                SaveSystem.Save(House_Location.SpecialMenu);
                next = Time.unscaledTime + 1;
                File.WriteAllText(resultPath, "RUNNING: host save is collecting before the hard-kill boundary");
                // HostSaveCoordinator writes the completed stage, records its identity, and
                // terminates this process before publishing its numeric destination.
            }
            catch (Exception ex)
            {
                finished = true; active = null;
                File.WriteAllText(resultPath, "MONMP_CRASH_RECOVERY_FAIL\n" + ex);
                Application.Quit(1);
            }
        }

        private void VerifyLoadedRecovery()
        {
            int selected = LocalTestIsolation.GetInt("SaveTimestamp", -1);
            Require(selected == expectedTimestamp,
                "Startup recovery did not keep the recovered numeric save selected before load (selected " +
                selected.ToString(CultureInfo.InvariantCulture) + ", expected " + expectedTimestamp.ToString(CultureInfo.InvariantCulture) + ").");
            string directory = Path.Combine(LocalTestIsolation.SaveRoot, saveName, expectedTimestamp.ToString(CultureInfo.InvariantCulture));
            var snapshot = SidecarStore.Load(directory, SessionOptions.SupportedGameHash);
            Require(snapshot != null && snapshot.Document.Profiles.Count == 1, "Recovered native save is missing its valid host sidecar.");
            Require(UnityEngine.Object.FindObjectOfType<Cash_Manager>().GetCash == 4321,
                "Recovered native cash value was not loaded by the new game process.");
            var atv = UnityEngine.Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>()
                .Single(v => v.vehicle_type == Vehicle_Type.SmollATV);
            Require(Math.Abs(atv.vehicle_PartsManager.Fuel_Tank.Get_FuelLevel - 7.25f) < 0.01f,
                "Recovered native vehicle fuel was not loaded by the new game process.");
            finished = true;
            File.WriteAllText(resultPath, "MONMP_CRASH_RECOVERY_PASS\n" +
                "A new Mon Bazou process recovered the complete staged native save before vanilla load.\n" +
                "Timestamp: " + expectedTimestamp.ToString(CultureInfo.InvariantCulture) + "\n" +
                "World: " + snapshot!.Document.WorldId.ToString("D") + "\n" +
                "Generation: " + snapshot.Document.Generation.ToString("D") + "\n" +
                "Native cash and ATV fuel read back after recovery.\n");
            Application.Quit();
        }

        private static void SetKnownNativeState()
        {
            AccessTools.Field(typeof(Cash_Manager), "cash").SetValue(UnityEngine.Object.FindObjectOfType<Cash_Manager>(), 4321);
            var atv = UnityEngine.Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>()
                .Single(v => v.vehicle_type == Vehicle_Type.SmollATV);
            atv.vehicle_PartsManager.Fuel_Tank.Get_FuelLevel = 7.25f;
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidDataException(message); }
    }
}
