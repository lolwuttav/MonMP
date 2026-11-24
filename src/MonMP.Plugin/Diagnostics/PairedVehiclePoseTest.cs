using System;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // The host moves one native, unoccupied ATV; the guest checks a rendered midpoint
    // between real vehicle state samples rather than only checking the final state.
    internal sealed class PairedVehiclePoseTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private int phase;
        private float deadline, movementStarted, sampleAt, initialDistance;
        private bool originalKinematic;
        private Vector3 origin, priorTarget, sampleStart, sampleTarget;
        private Quaternion originalRotation;
        internal string Status => "vehicle pose phase=" + phase + "; distance=" + initialDistance.ToString("0.000");

        internal PairedVehiclePoseTest(MonMPPlugin plugin, Gameplay gameplay)
        { this.plugin = plugin; this.gameplay = gameplay; }

        internal bool Tick()
        {
            if (deadline == 0) deadline = Time.unscaledTime + 70;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Vehicle pose interpolation stalled: " + Status);
            if (plugin.World?.Channel.FullySynchronized != true || plugin.Vehicles?.Ready != true) return false;
            plugin.Vehicles.TestType = Vehicle_Type.SmollATV;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            var vehicle = plugin.Vehicles!.TestVehicle;
            var body = vehicle.vehicleRigidbody;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                origin = body.position; originalRotation = body.rotation; originalKinematic = body.isKinematic;
                body.isKinematic = true;
                movementStarted = Time.unscaledTime;
                phase = 1;
            }
            else if (phase == 1)
            {
                if (plugin.Session!.Peers.Count == 1)
                {
                    body.position = origin; body.rotation = originalRotation;
                    vehicle.transform.SetPositionAndRotation(origin, originalRotation);
                    body.isKinematic = originalKinematic;
                    return true;
                }
                var position = origin + Vector3.right * (2.5f * Mathf.Sin((Time.unscaledTime - movementStarted) * 1.7f));
                body.position = position;
                vehicle.transform.SetPositionAndRotation(position, originalRotation);
            }
            return false;
        }

        private bool Guest()
        {
            var vehicle = plugin.Vehicles!.TestVehicle;
            if (phase == 0)
            {
                Require(vehicle.vehicleRigidbody.isKinematic, "guest vehicle is not a remote kinematic body.");
                MoveNear(vehicle.transform.position);
                priorTarget = Target(plugin.Vehicles.TestState);
                phase = 1;
            }
            else if (phase == 1)
            {
                var target = Target(plugin.Vehicles.TestState);
                if ((target - priorTarget).sqrMagnitude < 0.01f) return false;
                sampleStart = vehicle.transform.position;
                sampleTarget = target;
                initialDistance = Vector3.Distance(sampleStart, target);
                Require(initialDistance > 0.025f, "remote vehicle snapped to a newly received pose.");
                sampleAt = Time.unscaledTime;
                phase = 2;
            }
            else if (phase == 2)
            {
                float elapsed = Time.unscaledTime - sampleAt;
                float moved = Vector3.Distance(vehicle.transform.position, sampleStart);
                float remaining = Vector3.Distance(vehicle.transform.position, sampleTarget);
                if (moved > 0.02f && remaining > 0.01f && remaining < initialDistance - 0.02f && elapsed < 0.11f)
                {
                    plugin.EndPairedTestSession();
                    return true;
                }
                if (elapsed > 0.11f)
                {
                    priorTarget = Target(plugin.Vehicles.TestState);
                    phase = 1;
                }
            }
            return false;
        }

        private void MoveNear(Vector3 position)
        {
            var location = position + new Vector3(0, 0, 3);
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static Vector3 Target(VehicleState state) => new Vector3(state.Pose.X, state.Pose.Y, state.Pose.Z);
        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Vehicle pose interpolation: " + reason); }
    }
}
