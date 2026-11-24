using System;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedWindowsTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private SharedVehicleWindows windows;
        private int phase, repairs;
        private float next, presentationDeadline;
        private bool sawOpen;
        private bool sawFirstLeave, sawRejoin;
        private VehicleWindowState original;
        private VehicleWindowState suspended;
        private readonly bool preownedTruck;
        private bool preownedInitialChecked, preownedRejoinChecked;
        private float preownedDeadline;
        private readonly ulong stockWindowEntity;
        internal string Evidence => preownedTruck ? " Preowned native truck existed before host SharedWorld/catalog construction; the guest had no native sale clone before construction. Initial join and reconnect verified the exact sale entity, window bindings, both door aggregate entries, tailgate aggregate entry, six seats, hoist tool, purchased paint and dashboard IDs with retained payloads applied. Canonical high-bit windows remained required. Existing stock-window lifecycle assertions were retained; no additional physical interaction with the purchased components or disk save/load is claimed." : "";
        internal string Status => "windows phase=" + phase + "; count=" + windows.TestCount +
            (windows.TestDisposed ? "; adapter=disposed; selectedEntity=" + stockWindowEntity.ToString("X16") :
                "; selected=" + windows.TestBindingVehicleType + "; byVehicle=" + windows.TestVehicleTypeCoverage +
                "; down=" + windows.TestState.TargetDown + "; closed=" + windows.TestState.Closed) +
            "; preowned=" + preownedTruck + "/" + preownedInitialChecked + "/" + preownedRejoinChecked;
        internal PairedWindowsTest(MonMPPlugin plugin, Gameplay gameplay, bool preownedTruck = false)
        {
            this.plugin = plugin; this.gameplay = gameplay; this.preownedTruck = preownedTruck; windows = plugin.Vehicles!.Windows;
            Require(windows.TestCount > 0, "Expected at least one tracked vehicle window; discovered " + windows.TestCount + ".");
            if (preownedTruck) windows.TestSelectCanonicalWindow(PairedPurchasedTruckDoorsTest.PreownedCanonicalTruckForWindows);
            stockWindowEntity = windows.TestEntity;
            if (preownedTruck) Debug.Log("MONMP_WINDOWS_CANONICAL_SELECTION: peer=" + (plugin.TestOptions.IsHost ? "host" : "guest") +
                "; entity=" + stockWindowEntity.ToString("X16") + "; vehicle=" + windows.TestBindingVehicleType + "; phase=initial");
            if (preownedTruck)
                Require(plugin.World!.Entities.Resolve(stockWindowEntity)?.SpawnKey.StartsWith("purchased-vehicle-window-v1/", StringComparison.Ordinal) == false,
                    "Preowned mode must retain a persistent stock window for all original offline lifecycle assertions.");
            original = windows.TestState;
            Require(original.Closed, "Fixture requires originally closed windows.");
            if (plugin.TestOptions.IsHost) windows.TestPower();
        }
        internal bool Tick()
        {
            if (preownedTruck && !preownedInitialChecked)
            {
                if (!CheckPreowned("initial-join")) return false;
                preownedInitialChecked = true;
            }
            if (plugin.TestOptions.IsHost)
            {
                sawOpen |= windows.TestState.TargetDown;
                int peers = plugin.Session!.Peers.Count;
                if (sawFirstLeave && !sawRejoin && peers > 1)
                {
                    if (preownedTruck && !CheckPreowned("rejoin")) return false;
                    preownedRejoinChecked = preownedTruck; sawRejoin = true;
                }
                if (sawRejoin && peers == 1)
                {
                    Require(!preownedTruck || preownedInitialChecked && preownedRejoinChecked, "Preowned catalog assertions did not finish on the host.");
                    plugin.EndPairedTestSession(); return true;
                }
                if (!sawFirstLeave && sawOpen && peers == 1) sawFirstLeave = true;
                return false;
            }
            if (phase == 8)
            {
                if (Time.unscaledTime < next) return false;
                suspended = windows.TestNativeState;
                Require(suspended.MotorPlaying && Different(suspended, original), "Offline native window did not begin before rejoin.");
                if (preownedTruck) PairedPurchasedTruckDoorsTest.AssertPreownedGuestBeforeJoin(plugin);
                plugin.BeginPairedTestSession(); phase = 9; return false;
            }
            if (phase == 11)
            {
                if (Time.unscaledTime < next) return false;
                Require(windows.TestNativeState.TargetDown && Different(windows.TestNativeState, suspended) && !windows.TestMotorPlaying,
                    "Pre-join window coroutine did not resume and finish offline.");
                Require(!preownedTruck || preownedInitialChecked && preownedRejoinChecked, "Preowned catalog assertions did not finish on the guest.");
                return true;
            }
            if (Time.unscaledTime < next || plugin.World?.PersonalProfileReady != true) return false;
            if (phase == 9)
            {
                if (preownedTruck && !preownedRejoinChecked)
                {
                    if (!CheckPreowned("rejoin")) return false;
                    preownedRejoinChecked = true;
                }
                if (preownedTruck) windows.TestClearSelection();
                windows = plugin.Vehicles!.Windows;
                if (preownedTruck) windows.TestSelectCanonicalWindow(PairedPurchasedTruckDoorsTest.PreownedCanonicalTruckForWindows, stockWindowEntity);
                if (preownedTruck) Debug.Log("MONMP_WINDOWS_CANONICAL_SELECTION: peer=guest; entity=" + windows.TestEntity.ToString("X16") +
                    "; vehicle=" + windows.TestBindingVehicleType + "; phase=rejoin");
                Require(!preownedTruck || windows.TestEntity == stockWindowEntity, "Rejoin changed the stock window selected for the original lifecycle assertions.");
                Require(windows.TestState.TargetDown && !windows.TestState.Closed && Different(windows.TestState, original), "Rejoin lost the open native window.");
                Require(windows.TestSynchronized && !windows.TestMotorPlaying, "Late join restarted a stopped window motor.");
                Debug.Log("MONMP_WINDOWS_REJOIN_BASELINE: entity=" + windows.TestEntity.ToString("X16") + "; suspendedMotor=" + suspended.MotorPlaying +
                    "; capturedMotor=" + windows.TestOriginalState.MotorPlaying + "; suspendedTarget=" + suspended.TargetDown + "; capturedTarget=" + windows.TestOriginalState.TargetDown);
                Move(windows.TestPosition + Vector3.right); phase = 10; next = Time.unscaledTime + 1; return false;
            }
            if (phase == 0) { Move(windows.TestPosition + Vector3.right * 30); phase = 1; next = Time.unscaledTime + 1; }
            else if (phase == 1) { windows.TestClick(); phase = 2; next = Time.unscaledTime + 1; }
            else if (phase == 2)
            {
                Require(windows.TestState.Closed && !windows.TestState.TargetDown, "Distant window request was accepted.");
                Move(windows.TestPosition + Vector3.right); phase = 3; next = Time.unscaledTime + 1;
            }
            else if (phase == 3)
            {
                windows.TestResetPresentationObservation(); windows.TestClick(); phase = 4;
                presentationDeadline = Time.unscaledTime + 8;
            }
            else if (phase == 4 && Time.unscaledTime >= presentationDeadline)
                Require(false, "Guest window pose did not settle after the host animation.");
            else if (phase == 4 && windows.TestState.TargetDown && Different(windows.TestState, original) &&
                windows.TestState.MotorPlaying && windows.TestMotorPlaying && windows.TestSawPresentationInterpolation && windows.TestPresentationPending)
            {
                Require(!windows.TestState.Closed, "Intermediate window state did not apply.");
                phase = 5; next = Time.unscaledTime + 4;
            }
            else if (phase == 5)
            {
                Require(!windows.TestState.MotorPlaying && !windows.TestMotorPlaying, "Guest motor did not stop at the open endpoint.");
                Require(windows.TestSynchronized, "Guest window did not settle at the open endpoint.");
                plugin.EndPairedTestSession(); Require(windows.TestRestored && !windows.TestMotorPlaying, "Open-window disconnect did not restore the local baseline.");
                windows.TestPower(); windows.TestClick();
                phase = 8; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 10)
            {
                windows.TestResetPresentationObservation(); windows.TestClick(); phase = 6;
                presentationDeadline = Time.unscaledTime + 8;
            }
            else if (phase == 6 && Time.unscaledTime >= presentationDeadline)
                Require(false, "Guest window pose did not settle while closing.");
            else if (phase == 6 && windows.TestState.Closed && !windows.TestState.MotorPlaying && windows.TestSynchronized)
            {
                Require(windows.TestSawPresentationInterpolation, "Guest window close did not interpolate between host transform samples.");
                repairs = plugin.World.Channel.RepairsApplied; windows.TestDrift();
                Require(!windows.TestSynchronized, "Fixture did not introduce drift.");
                Require(plugin.World.Channel.RequestResync(SnapshotScope.ForEntity(VehicleWindowState.Adapter, windows.TestEntity)), "Window repair rejected."); phase = 7;
            }
            else if (phase == 7 && plugin.World.Channel.RepairsApplied > repairs)
            {
                Require(windows.TestSynchronized, "Window repair did not restore native pose.");
                plugin.EndPairedTestSession();
                bool restored = windows.TestRestored, motor = windows.TestMotorPlaying;
                string detail = windows.TestRestorationDiagnostic;
                Debug.Log("MONMP_WINDOWS_SECOND_DISCONNECT: restored=" + restored + "; selectedMotor=" + motor + "; " + detail);
                Require(restored && motor, "Guest moving-window baseline was not restored. restored=" + restored + "; selectedMotor=" + motor + "; " + detail);
                phase = 11; next = Time.unscaledTime + 4;
            }
            return false;
        }
        internal void Cleanup() => windows.TestClearSelection();
        private bool CheckPreowned(string stage)
        {
            if (PairedPurchasedTruckDoorsTest.TryAssertPreownedWindows(plugin, stage)) { preownedDeadline = 0; return true; }
            if (preownedDeadline == 0) preownedDeadline = Time.unscaledTime + 20f;
            Require(Time.unscaledTime <= preownedDeadline, "Preowned truck catalog/native bindings did not become ready within 20 seconds: " + stage + "; " + PairedPurchasedTruckDoorsTest.PreownedBindingWait);
            return false;
        }
        private static bool Different(VehicleWindowState a, VehicleWindowState b) =>
            Math.Abs(a.Window.X - b.Window.X) + Math.Abs(a.Window.Y - b.Window.Y) + Math.Abs(a.Window.Z - b.Window.Z) > 0.001f;
        private void Move(Vector3 p)
        { gameplay.PlayerWalking.transform.position = p; gameplay.Get_Player_Rigidbody.position = p; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
