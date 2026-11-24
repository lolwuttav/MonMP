using System;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // The guest observes a host-held item between real 10 Hz pose samples.
    internal sealed class PairedHeldItemInterpolationTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ulong entity;
        private int phase;
        private float deadline, movementStarted, sampleAt, initialDistance, initialRotationAngle;
        private Vector3 movementOrigin, priorTarget, sampleStart, sampleTarget;
        private Quaternion originalItemRotation, priorTargetRotation, sampleStartRotation, sampleTargetRotation;
        internal string Status => "held-item interpolation phase=" + phase + "; item=" + entity.ToString("X") +
            "; distance=" + initialDistance.ToString("0.000") + "; angle=" + initialRotationAngle.ToString("0.0");

        internal PairedHeldItemInterpolationTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
        }

        internal bool Tick()
        {
            if (deadline == 0) deadline = Time.unscaledTime + 70;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Held-item interpolation stalled: " + Status);
            if (plugin.World?.Channel.FullySynchronized != true) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            var items = plugin.Items!;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                entity = items.TestFirstId;
                var item = items.TestItem(entity);
                Require(item != null, "host has no native shared item for pickup.");
                MoveNear(item!.transform.position);
                items.TestGrab(entity);
                Require(items.TestState(entity).Lease.Owner == 0 && gameplay.Player.Get_HoldingItem == item,
                    "host did not hold the shared item natively.");
                originalItemRotation = item.transform.rotation;
                movementOrigin = gameplay.PlayerWalking.transform.position;
                movementStarted = Time.unscaledTime;
                phase = 1;
            }
            else if (phase == 1)
            {
                if (plugin.Session!.Peers.Count == 1)
                {
                    var item = items.TestItem(entity);
                    if (item != null) item.transform.rotation = originalItemRotation;
                    items.TestDrop(false);
                    return true;
                }
                float elapsed = Time.unscaledTime - movementStarted;
                var location = movementOrigin + Vector3.right * (1.5f * Mathf.Sin(elapsed * 2f));
                gameplay.PlayerWalking.transform.position = location;
                gameplay.Get_Player_Rigidbody.position = location;
                gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                var held = items.TestItem(entity);
                if (held != null)
                    held.transform.rotation = Quaternion.AngleAxis(50f * Mathf.Sin(elapsed * 2f), Vector3.up) * originalItemRotation;
            }
            return false;
        }

        private bool Guest()
        {
            var items = plugin.Items!;
            if (phase == 0)
            {
                entity = items.TestFirstId;
                if (items.TestItem(entity) == null || items.TestState(entity).Lease.Owner != 0) return false;
                MoveNear(items.TestItem(entity)!.transform.position);
                priorTarget = Target(items.TestState(entity));
                priorTargetRotation = TargetRotation(items.TestState(entity));
                phase = 1;
            }
            else if (phase == 1)
            {
                var state = items.TestState(entity);
                if (state.Lease.Owner != 0) return false;
                var target = Target(state);
                var targetRotation = TargetRotation(state);
                if ((target - priorTarget).sqrMagnitude < 0.04f || Quaternion.Angle(priorTargetRotation, targetRotation) < 4f) return false;
                var item = items.TestItem(entity)!;
                sampleStart = item.transform.position;
                sampleStartRotation = item.transform.rotation;
                sampleTarget = target;
                sampleTargetRotation = targetRotation;
                initialDistance = Vector3.Distance(sampleStart, target);
                initialRotationAngle = Quaternion.Angle(sampleStartRotation, sampleTargetRotation);
                Require(initialDistance > 0.03f, "remote held item snapped to a newly received pose.");
                Require(initialRotationAngle > 2f, "remote held item received too little rotation change for a meaningful intermediate check.");
                // A scoped repair replays a snapshot with forceValues=true. It must
                // not teleport an already-present held replica to this new target.
                items.TestReplayItemSnapshot(entity);
                Require(Vector3.Distance(item.transform.position, sampleStart) < 0.001f &&
                    Quaternion.Angle(item.transform.rotation, sampleStartRotation) < 0.1f,
                    "remote held item snapped when its current snapshot was reapplied.");
                sampleAt = Time.unscaledTime;
                phase = 2;
            }
            else if (phase == 2)
            {
                float elapsed = Time.unscaledTime - sampleAt;
                var item = items.TestItem(entity)!;
                float moved = Vector3.Distance(item.transform.position, sampleStart);
                float remaining = Vector3.Distance(item.transform.position, sampleTarget);
                float rotated = Quaternion.Angle(sampleStartRotation, item.transform.rotation);
                float remainingRotation = Quaternion.Angle(item.transform.rotation, sampleTargetRotation);
                if (moved > 0.02f && remaining > 0.01f && remaining < initialDistance - 0.02f &&
                    rotated > 1f && remainingRotation > 1f && remainingRotation < initialRotationAngle - 1f && elapsed < 0.13f)
                {
                    plugin.EndPairedTestSession();
                    return true;
                }
                if (elapsed > 0.13f)
                {
                    priorTarget = Target(items.TestState(entity));
                    priorTargetRotation = TargetRotation(items.TestState(entity));
                    phase = 1;
                }
            }
            return false;
        }

        private static Vector3 Target(ItemState state) => new Vector3(state.Pose.X, state.Pose.Y, state.Pose.Z);
        private static Quaternion TargetRotation(ItemState state) => new Quaternion(state.Pose.Qx, state.Pose.Qy, state.Pose.Qz, state.Pose.Qw);
        private void MoveNear(Vector3 item)
        {
            var location = item + new Vector3(0, -0.8f, 1);
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Held-item interpolation: " + reason); }
    }
}
