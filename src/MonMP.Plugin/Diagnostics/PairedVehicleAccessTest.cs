using System;
using System.Linq;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Konig driver door, hood release/open/close and trunk, all guest-initiated; the host mirrors
    // native state transitions the same way PairedVehiclePartsTest does for socket installs.
    internal sealed class PairedVehicleAccessTest
    {
        private static readonly Car_PartsType[] RequiredParts = { Car_PartsType.Door_Left, Car_PartsType.Door_Right, Car_PartsType.Door_Hood, Car_PartsType.Door_Trunk };
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedVehicleDoors doors;
        private readonly SharedVehicleHood hood;
        private readonly SharedVehicleTrunk trunk;
        private int phase;
        private float next, phaseDeadline;
        internal string Status => "access phase=" + phase + "; driver=" + doors.TestKonigNative().Open +
            "; driverRecord=" + doors.TestKonigState().Open + "; hood=" + hood.TestNative().Open + "; trunk=" + trunk.TestNativeOpen +
            "; position=" + gameplay.PlayerWalking.transform.position.ToString("F2") +
            "; handleDistance=" + Vector3.Distance(gameplay.PlayerWalking.transform.position, doors.TestKonigPosition()).ToString("F2") +
            "; positionSettled=" + (plugin.World?.ReconnectPositions?.Settled == true);
        internal PairedVehicleAccessTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            var vehicles = plugin.Vehicles!;
            doors = vehicles.Doors; hood = vehicles.Hood; trunk = vehicles.Trunk;
            if (plugin.TestOptions.IsHost)
                foreach (var type in RequiredParts) plugin.World!.VehicleParts.TestEnsureInstalled(type);
        }
        internal bool Tick()
        {
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }
        private bool Host()
        {
            if (phase == 0 && doors.TestKonigNative().Open) phase = 1;
            else if (phase == 1 && hood.TestNative().Released) phase = 2;
            else if (phase == 2 && hood.TestNative().Open) phase = 3;
            else if (phase == 3 && trunk.TestNativeOpen) phase = 4;
            else if (phase == 4 && !doors.TestKonigNative().Open && !trunk.TestNativeOpen) phase = 5;
            else if (phase == 5 && plugin.Session!.Peers.Count == 1) { plugin.EndPairedTestSession(); return true; }
            return false;
        }
        private bool Guest()
        {
            var parts = plugin.World!.VehicleParts;
            // Each action needs its own move-then-wait step first: the host only learns the guest's
            // new position from the next periodic pose broadcast, not instantly on local teleport.
            if (phase == 0)
            {
                if (plugin.World?.PersonalProfileReady != true || plugin.World.ReconnectPositions?.Settled != true) return false;
                if (!RequiredParts.All(t => parts.TestRecord(parts.TestSocketOfType(t)).Installed)) return false;
                Require(!doors.TestKonigNative().Open, "Fixture requires the Konig driver door closed.");
                doors.TestResetKonigPresentationObservation();
                Move(doors.TestKonigPosition()); phase = 1; next = Time.unscaledTime + 1;
                return false;
            }
            if (phase == 1)
            {
                Require(Vector3.Distance(gameplay.PlayerWalking.transform.position, doors.TestKonigPosition()) < 3,
                    "Fixture moved away from the driver handle before opening; " + Status);
                doors.TestResetKonigPresentationObservation(); doors.TestKonigClick(); phase = 2;
                next = Time.unscaledTime + 1.5f; phaseDeadline = Time.unscaledTime + 8; return false;
            }
            if (phase == 2)
            {
                if (Time.unscaledTime >= phaseDeadline) Require(false, "Guest Konig door did not settle while opening.");
                if (!doors.TestKonigState().Open || !doors.TestKonigNative().Open || !doors.TestSynchronized) return false;
                Require(doors.TestKonigSawPresentationInterpolation, "Guest door did not interpolate between host hinge updates.");
                Move(hood.TestReleasePosition); phase = 3; next = Time.unscaledTime + 1;
                return false;
            }
            if (phase == 3) { hood.TestRelease(); phase = 4; next = Time.unscaledTime + 0.5f; return false; }
            if (phase == 4)
            {
                Require(hood.TestState.Released && !hood.TestState.Open && hood.TestSynchronized &&
                    Mathf.Abs(Mathf.DeltaAngle(-2f, hood.TestNativeLocalEuler)) < 0.1f,
                    "Hood release did not present the native released-only -2 degree state.");
                Move(hood.TestOpenPosition); phase = 5; next = Time.unscaledTime + 1;
                return false;
            }
            if (phase == 5)
            {
                hood.TestResetPresentationObservation(); hood.TestOpen(); phase = 6;
                next = Time.unscaledTime + 1.5f; phaseDeadline = Time.unscaledTime + 8; return false;
            }
            if (phase == 6)
            {
                if (Time.unscaledTime >= phaseDeadline) Require(false, "Guest hood did not settle while opening.");
                if (!hood.TestState.Open || !hood.TestNative().Open || !hood.TestSynchronized) return false;
                Require(hood.TestSawPresentationInterpolation, "Guest hood did not interpolate between host hinge updates.");
                Move(trunk.TestPosition); phase = 7; next = Time.unscaledTime + 1;
                return false;
            }
            if (phase == 7)
            {
                trunk.TestResetPresentationObservation(); trunk.TestClick(); phase = 8;
                next = Time.unscaledTime + 1.5f; phaseDeadline = Time.unscaledTime + 8; return false;
            }
            if (phase == 8)
            {
                if (Time.unscaledTime >= phaseDeadline) Require(false, "Guest trunk did not settle while opening.");
                if (!trunk.TestState.Open || !trunk.TestNativeOpen || !trunk.TestSynchronized) return false;
                Require(trunk.TestSawPresentationInterpolation, "Guest trunk did not interpolate between host hinge updates.");
                Move(hood.TestClosePosition); phase = 9; next = Time.unscaledTime + 1;
                return false;
            }
            if (phase == 9)
            {
                hood.TestResetPresentationObservation(); hood.TestClose(); phase = 10;
                next = Time.unscaledTime + 1; phaseDeadline = Time.unscaledTime + 8; return false;
            }
            if (phase == 10)
            {
                if (Time.unscaledTime >= phaseDeadline) Require(false, "Guest hood did not settle while closing.");
                if (hood.TestState.Open || !hood.TestSynchronized) return false;
                Require(!hood.TestState.Open && hood.TestSawPresentationInterpolation, "Guest hood close did not interpolate.");
                doors.TestResetKonigPresentationObservation(); Move(doors.TestKonigPosition()); phase = 11; next = Time.unscaledTime + 1; return false;
            }
            if (phase == 11)
            {
                doors.TestResetKonigPresentationObservation(); doors.TestKonigClick(); phase = 12;
                next = Time.unscaledTime + 1; phaseDeadline = Time.unscaledTime + 8; return false;
            }
            if (phase == 12)
            {
                if (Time.unscaledTime >= phaseDeadline) Require(false, "Guest door did not settle while closing.");
                if (doors.TestKonigState().Open || !doors.TestSynchronized) return false;
                Require(!doors.TestKonigState().Open && doors.TestKonigSawPresentationInterpolation, "Guest door close did not interpolate.");
                trunk.TestResetPresentationObservation(); Move(trunk.TestPosition); phase = 13; next = Time.unscaledTime + 1; return false;
            }
            if (phase == 13)
            {
                trunk.TestResetPresentationObservation(); trunk.TestClick(); phase = 14;
                next = Time.unscaledTime + 2; phaseDeadline = Time.unscaledTime + 8; return false;
            }
            if (phase == 14)
            {
                if (Time.unscaledTime >= phaseDeadline) Require(false, "Guest trunk did not settle while closing.");
                if (trunk.TestState.Open || !trunk.TestSynchronized) return false;
                Require(!doors.TestKonigState().Open && !trunk.TestState.Open, "Closing commands were not applied.");
                Require(trunk.TestSawPresentationInterpolation, "Guest trunk close did not interpolate.");
                plugin.EndPairedTestSession();
                var hoodNative = hood.TestNative();
                Require(!doors.TestKonigNative().Open && !trunk.TestNativeOpen && !hoodNative.Released && !hoodNative.Open &&
                    hood.TestSynchronized && Mathf.Abs(Mathf.DeltaAngle(hood.TestBaselineEuler, hood.TestNativeLocalEuler)) < 0.1f,
                    "Guest vehicle panels did not restore their native baselines on disconnect.");
                return true;
            }
            return false;
        }
        private void Move(Vector3 p)
        { gameplay.PlayerWalking.transform.position = p; gameplay.Get_Player_Rigidbody.position = p; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Vehicle access test: " + message); }
    }
}
