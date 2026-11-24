using System;
using System.Linq;
using HarmonyLib;
using NWH;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Modules.Trailer;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Uses isolated local copies only: stage a detached WoodSplitter at the Konig
    // hitch, exercise host-authoritative native attach/detach, then restore its pose.
    internal sealed class PairedTrailerAuthorityTest
    {
        private sealed class FixtureBaseline
        {
            internal Rigidbody Body = null!;
            internal TrailerHitchModule Hitch = null!;
            internal Vector3 Position, Velocity, AngularVelocity;
            internal Quaternion Rotation;
            internal bool HitchInstalled, IsKinematic;
            internal Rigidbody? HeldTowBody;
            internal RigidbodyConstraints TowConstraints;
            internal bool TowIsKinematic;
            internal Vector3 TowPosition, TowVelocity, TowAngularVelocity;
            internal Quaternion TowRotation;
        }

        private static FixtureBaseline? baseline;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedTrailers trailers;
        private readonly bool host;
        private readonly bool nativeCouplerInput;
        private readonly ulong towId, childId;
        private int phase;
        private uint guestPeer, baselineGeneration, attachedGeneration;
        private float next, deadline;

        internal static void PrepareFixture(Vehicle_Type towType = Vehicle_Type.MainVehicle)
        {
            if (baseline != null) return;
            var tow = UnityEngine.Object.FindObjectsOfType<VehicleController>()
                .SingleOrDefault(v => v.vehicle_type == towType && v.gameObject.scene.IsValid());
            var child = UnityEngine.Object.FindObjectsOfType<TrailerModuleWrapper>()
                .Select(w => w.module)
                .SingleOrDefault(m => m != null && m.VehicleController != null &&
                    m.VehicleController.name.IndexOf("WoodSplitter", StringComparison.OrdinalIgnoreCase) >= 0);
            var hitch = tow?.GetComponent<TrailerHitchModuleWrapper>()?.module;
            if (tow == null || hitch?.attachmentPoint == null || child?.attachmentPoint == null || child.VehicleController?.vehicleRigidbody == null)
                throw new InvalidOperationException("Trailer fixture could not find the Konig hitch and WoodSplitter trailer.");
            if (hitch.attached || hitch.Trailer != null || child.attached || child.TrailerHitch != null)
                throw new InvalidOperationException("Trailer fixture requires a detached WoodSplitter and empty Konig hitch in the isolated save.");

            var body = child.VehicleController.vehicleRigidbody;
            var installedField = AccessTools.Field(typeof(TrailerHitchModule), "isHitchInstalled") ??
                throw new InvalidOperationException("Unsupported native hitch installation field.");
            baseline ??= new FixtureBaseline
            {
                Body = body,
                Hitch = hitch,
                Position = body.position,
                Rotation = body.rotation,
                Velocity = body.velocity,
                AngularVelocity = body.angularVelocity,
                HitchInstalled = (bool)installedField.GetValue(hitch),
                IsKinematic = body.isKinematic
            };
            // The fixture copy may have the optional tow hitch removed. Enable its
            // native installation flag temporarily so NWH's own nearest/attach path runs.
            hitch.Set_isHitchInstalled = true;
            Vector3 offset = hitch.attachmentPoint.position - child.attachmentPoint.position;
            body.position += offset;
            child.VehicleController.transform.position = body.position;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();

            hitch.SyncTrailers();
            var findNearest = AccessTools.Method(typeof(TrailerHitchModule), "FindNearestTrailerModule");
            var nearest = findNearest?.Invoke(hitch, null) as TrailerModule;
            if (nearest != child)
            {
                string details = "nearest=" + (nearest?.VehicleController?.name ?? "none") +
                    "; installed=" + installedField.GetValue(hitch) +
                    "; pointGap=" + Vector3.Distance(hitch.attachmentPoint.position, child.attachmentPoint.position).ToString("F3") +
                    "; thresholdSquared=" + hitch.attachDistanceThreshold.ToString("F3");
                RestoreFixture();
                throw new InvalidOperationException("Staged WoodSplitter is not the Konig hitch's native nearest trailer candidate (" + details + ").");
            }
        }

        internal static void RestoreFixture()
        {
            if (baseline == null) return;
            var saved = baseline;
            baseline = null;
            if (saved.Body == null) return;
            saved.Body.position = saved.Position;
            saved.Body.rotation = saved.Rotation;
            saved.Body.transform.SetPositionAndRotation(saved.Position, saved.Rotation);
            saved.Body.velocity = saved.Velocity;
            saved.Body.angularVelocity = saved.AngularVelocity;
            saved.Body.isKinematic = saved.IsKinematic;
            saved.Hitch.Set_isHitchInstalled = saved.HitchInstalled;
            if (saved.HeldTowBody != null)
            {
                var tow = saved.HeldTowBody;
                tow.position = saved.TowPosition;
                tow.rotation = saved.TowRotation;
                tow.transform.SetPositionAndRotation(saved.TowPosition, saved.TowRotation);
                tow.constraints = saved.TowConstraints;
                tow.isKinematic = saved.TowIsKinematic;
                tow.velocity = saved.TowVelocity;
                tow.angularVelocity = saved.TowAngularVelocity;
            }
            Physics.SyncTransforms();
        }

        internal PairedTrailerAuthorityTest(MonMPPlugin plugin, Gameplay gameplay, bool nativeCouplerInput = false)
        {
            this.plugin = plugin;
            this.gameplay = gameplay;
            this.nativeCouplerInput = nativeCouplerInput;
            host = plugin.TestOptions.IsHost;
            trailers = plugin.World!.Trailers;
            (towId, childId) = trailers.TestFixtureIds();
        }

        internal string Status => "trailer phase=" + phase + "; link=" + trailers.TestState(childId).Link.ParentId.ToString("X16") +
            "; generation=" + trailers.TestState(childId).Link.Generation + "; native=" +
            (trailers.TestHitch(towId).Trailer == trailers.TestTrailer(childId)) +
            "; commands=" + trailers.TestInteractionCommandCount + "; last=" + trailers.TestLastInteractionAction +
            "/" + trailers.TestLastInteractionPeer + "/" + trailers.TestLastInteractionAccepted +
            "; towBody=" + trailers.TestVehicle(towId).vehicleRigidbody.position.ToString("F3") +
            "; towVelocity=" + trailers.TestVehicle(towId).vehicleRigidbody.velocity.ToString("F3") +
            "; towConstraints=" + trailers.TestVehicle(towId).vehicleRigidbody.constraints +
            "; towKinematic=" + trailers.TestVehicle(towId).vehicleRigidbody.isKinematic +
            "; " + trailers.TestNativeAttachDiagnostics(towId, childId);

        internal bool Tick()
        {
            if (!trailers.Ready || plugin.World?.Channel.FullySynchronized != true || Time.unscaledTime < next) return false;
            try
            {
                if (deadline == 0) deadline = Time.unscaledTime + 100;
                if (Time.unscaledTime > deadline) throw new InvalidOperationException("Paired trailer authority test stalled: " + Status);
                return host ? Host() : Guest();
            }
            catch
            {
                RestoreFixture();
                throw;
            }
        }

        private bool Host()
        {
            var child = trailers.TestTrailer(childId);
            var hitch = trailers.TestHitch(towId);
            if (phase == 0 && plugin.Session!.Peers.Count >= 2)
            {
                guestPeer = plugin.Session.Peers.Keys.Single(peer => peer != 0);
                HoldTowForCoupler();
                trailers.TestStageAtHitch(towId, childId);
                if (!trailers.TestCanNativeAttach(towId, childId))
                    throw new InvalidOperationException("Paired trailer authority test: native hitch did not select the staged WoodSplitter (" +
                        trailers.TestNativeAttachDiagnostics(towId, childId) + ").");
                baselineGeneration = trailers.TestState(childId).Link.Generation;
                phase = 1;
            }
            else if (phase == 1 && trailers.TestInteractionCommandCount >= 1)
            {
                RequireLastInteraction(TrailerAuthorityAction.Attach, accepted: false, count: 1);
                if (!trailers.TestPeerWalkingOutOfRange(guestPeer, childId))
                    throw new InvalidOperationException("Paired trailer authority test: first guest request was not observed from a fresh distant walking pose.");
                if (trailers.TestState(childId).Link.ParentId != 0 ||
                    trailers.TestState(childId).Link.Generation != baselineGeneration || hitch.Trailer != null || hitch.attached)
                    throw new InvalidOperationException("Paired trailer authority test: a distant guest attach changed hitch state.");
                phase = 2;
            }
            else if (phase == 2 && trailers.TestInteractionCommandCount >= 2)
            {
                // The command starts the native-style easing pull; acceptance is
                // committed only once the host creates and verifies its joint.
                if (trailers.TestInteractionCommandCount == 2 && trailers.TestInteractionPullPending(childId, guestPeer)) return false;
                RequireLastInteraction(TrailerAuthorityAction.Attach, accepted: true, count: 2);
                if (!trailers.TestNativeLinkIntact(towId, childId))
                    throw new InvalidOperationException("Paired trailer authority test: nearby guest attach did not create the expected host native hitch joint.");
                attachedGeneration = trailers.TestState(childId).Link.Generation;
                phase = 3;
            }
            else if (phase == 3 && trailers.TestInteractionCommandCount >= 3)
            {
                RequireLastInteraction(TrailerAuthorityAction.Detach, accepted: false, count: 3);
                if (!trailers.TestPeerWalkingOutOfRange(guestPeer, childId))
                    throw new InvalidOperationException("Paired trailer authority test: second guest request was not observed from a fresh distant walking pose.");
                if (trailers.TestState(childId).Link.ParentId != towId ||
                    trailers.TestState(childId).Link.Generation != attachedGeneration || hitch.Trailer != child || !child.attached)
                    throw new InvalidOperationException("Paired trailer authority test: a distant guest detach changed hitch state.");
                phase = 4;
            }
            else if (phase == 4 && trailers.TestInteractionCommandCount >= 4)
            {
                RequireLastInteraction(TrailerAuthorityAction.Detach, accepted: true, count: 4);
                if (trailers.TestState(childId).Link.ParentId != 0 || trailers.TestState(childId).Link.Generation <= attachedGeneration)
                    throw new InvalidOperationException("Paired trailer authority test: nearby guest detach did not advance the link generation.");
                if (hitch.Trailer != null || hitch.attached || child.attached || child.TrailerHitch != null)
                    throw new InvalidOperationException("Paired trailer authority test: nearby guest detach did not clear the host native hitch joint.");
                RestoreFixture();
                phase = 5;
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            var child = trailers.TestTrailer(childId);
            var hitch = trailers.TestHitch(towId);
            if (phase == 0)
            {
                // The host stages after both peers join. Wait for its trailer and
                // tow poses to arrive before timing the actual native clicks.
                if (!trailers.TestCanNativeAttach(towId, childId)) return false;
                MovePlayer(child.VehicleController.transform.position + Vector3.right * 30 + Vector3.up * 0.5f);
                phase = 1;
                next = Time.unscaledTime + 1.25f;
            }
            else if (phase == 1 && Time.unscaledTime >= next)
            {
                GuestAttach();
                phase = 2;
                next = Time.unscaledTime + 1.25f;
            }
            else if (phase == 2 && Time.unscaledTime >= next)
            {
                MovePlayer(child.VehicleController.transform.position + Vector3.up * 0.5f);
                phase = 3;
                next = Time.unscaledTime + 0.9f;
            }
            else if (phase == 3 && Time.unscaledTime >= next)
            {
                GuestAttach();
                phase = 4;
            }
            else if (phase == 4 && trailers.TestState(childId).Link.ParentId == towId)
            {
                if (!trailers.TestNativeLinkIntact(towId, childId))
                    throw new InvalidOperationException("Paired trailer authority test: nearby guest request did not apply the host's native attach.");
                attachedGeneration = trailers.TestState(childId).Link.Generation;
                MovePlayer(child.VehicleController.transform.position + Vector3.right * 30 + Vector3.up * 0.5f);
                phase = 5;
                next = Time.unscaledTime + 1.25f;
            }
            else if (phase == 5 && Time.unscaledTime >= next)
            {
                GuestDetach();
                phase = 6;
                next = Time.unscaledTime + 1.25f;
            }
            else if (phase == 6 && Time.unscaledTime >= next)
            {
                if (trailers.TestState(childId).Link.ParentId != towId ||
                    trailers.TestState(childId).Link.Generation != attachedGeneration || hitch.Trailer != child || !child.attached)
                    throw new InvalidOperationException("Paired trailer authority test: a distant guest detach was not rejected.");
                MovePlayer(child.VehicleController.transform.position + Vector3.up * 0.5f);
                phase = 7;
                next = Time.unscaledTime + 0.9f;
            }
            else if (phase == 7 && Time.unscaledTime >= next)
            {
                GuestDetach();
                phase = 8;
            }
            else if (phase == 8 && trailers.TestState(childId).Link.ParentId == 0 && trailers.TestState(childId).Link.Generation > attachedGeneration)
            {
                if (hitch.Trailer != null || hitch.attached || child.attached || child.TrailerHitch != null)
                    throw new InvalidOperationException("Paired trailer authority test: nearby guest request did not apply the host's native detach.");
                RestoreFixture();
                phase = 9;
                return true;
            }
            return false;
        }

        private void RequireLastInteraction(TrailerAuthorityAction action, bool accepted, int count)
        {
            if (trailers.TestInteractionCommandCount != count || trailers.TestLastInteractionPeer != guestPeer ||
                trailers.TestLastInteractionAction != action || trailers.TestLastInteractionAccepted != accepted)
                throw new InvalidOperationException("Paired trailer authority test: unexpected guest command result (" + Status + ").");
        }

        private void MovePlayer(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = position;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private void GuestAttach()
        {
            if (nativeCouplerInput)
            {
                // A native click outside the local hitch radius never sends a
                // command, so it cannot test the host's player-range rejection.
                if (!trailers.TestCanNativeAttach(towId, childId))
                    throw new InvalidOperationException("Native coupler fixture lost its staged geometry before the click: " + Status);
                trailers.TestNativeCouplerClick(childId);
            }
            else trailers.TestRequestAttach(towId, childId, plugin.Session!.LocalPeerId);
        }

        private void HoldTowForCoupler()
        {
            if (baseline == null) throw new InvalidOperationException("Trailer fixture baseline was not prepared.");
            var body = trailers.TestVehicle(towId).vehicleRigidbody;
            // This suite tests clicks and authority, with the trailer already
            // held at the hitch. Keep the unattended tow from rolling away while
            // the guest loads. Native ApplyLowSpeedFixes overwrites constraints,
            // so hold this body after catalog binding instead. Native hitch
            // joints still use this exact body; TrailerTowing separately checks
            // dynamic physical towing on both simulation owners.
            if (baseline.HeldTowBody == null)
            {
                baseline.HeldTowBody = body;
                baseline.TowConstraints = body.constraints;
                baseline.TowIsKinematic = body.isKinematic;
                baseline.TowPosition = body.position;
                baseline.TowRotation = body.rotation;
                baseline.TowVelocity = body.velocity;
                baseline.TowAngularVelocity = body.angularVelocity;
            }
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
        }

        private void GuestDetach()
        {
            if (nativeCouplerInput) trailers.TestNativeCouplerClick(childId);
            else trailers.TestRequestDetach(childId, plugin.Session!.LocalPeerId);
        }
    }
}
