using System;
using System.Collections.Generic;
using MonMP.Core.World;
using NWH;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Effects;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Contact effects happen before CrashComponent can start its (possibly queued) sound.
    // Keep the native contact exclusions separate from the successful audio-start relay.
    internal static class VehicleCrashGameplay
    {
        private const byte Heavy = 1, Derby = 2, HitActor = 3, Delivery = 0x80;
        private const float RecipientGraceSeconds = 3f;
        private const int MaximumRememberedOccupancies = 2048;
        private static ContactScope? current;
        private static readonly Dictionary<(ulong Vehicle, uint Peer), float> occupants =
            new Dictionary<(ulong, uint), float>();
        private static readonly List<(ulong Vehicle, uint Peer)> expiredOccupants =
            new List<(ulong, uint)>();

        internal sealed class ContactScope : IDisposable
        {
            private readonly ContactScope? previous;
            internal readonly DamageHandler Handler;
            internal readonly float PreviousContactTime;
            internal bool NativeApplied;
            private bool disposed;
            internal ContactScope(DamageHandler handler)
            {
                previous = current; current = this; Handler = handler;
                PreviousContactTime = handler.lastCollisionTime;
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true; current = previous;
            }
        }

        internal static ContactScope Begin(DamageHandler handler) => new ContactScope(handler);

        internal static bool AllowContact(DamageHandler handler, Collision collision)
        {
            var plugin = MonMPPlugin.Instance;
            // Avoid item classification for contacts native DamageHandler would not
            // accept. Its threshold/timeout remain authoritative for exterior hits.
            if (plugin?.Session?.IsRunning != true || plugin.World?.IsDisposed != false || collision == null ||
                !handler.Active || Time.realtimeSinceStartup < handler.lastCollisionTime + handler.collisionTimeout ||
                !(collision.relativeVelocity.magnitude * 100f > handler.decelerationThreshold)) return true;
            return !LooseItemCrashContact.IsLooseItem(collision);
        }

        internal static bool AllowNative(Gameplay gameplay)
        {
            if (current == null || MonMPPlugin.Instance?.Session?.IsRunning != true) return true;
            var source = current.Handler.VehicleController;
            // Every personal chassis subscribes the same Gameplay callback. Its native method
            // checks only that the player sits somewhere, not that this chassis is theirs.
            return source != null && source.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Local &&
                gameplay.Get_CurrentVehicleController == source &&
                (gameplay.Get_PlayerState == PlayerState.InVehicle_Driver || gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger);
        }

        internal static void NativeApplied(Gameplay gameplay, Collision collision, bool ranOriginal)
        {
            if (ranOriginal && current != null && AllowNative(gameplay) &&
                Classify(current.Handler.VehicleController, collision) != 0) current.NativeApplied = true;
        }

        internal static void Contact(ContactScope scope, Collision collision, bool ranOriginal)
        {
            var plugin = MonMPPlugin.Instance;
            var world = plugin?.World;
            var events = world?.Events;
            var source = scope.Handler.VehicleController;
            if (!ranOriginal || SharedPresentationEvents.Replaying || plugin?.Session?.IsRunning != true ||
                world?.IsDisposed != false || events?.Live != true || source == null || collision == null ||
                !ReferenceEquals(scope.Handler.lastCollision, collision) ||
                scope.Handler.lastCollisionTime == scope.PreviousContactTime || !TryTarget(source, out ulong target)) return;
            byte effect = Classify(source, collision);
            if (effect == 0) return;
            if (effect == Heavy) world.PoliceSpots.ReportCollision(source, collision, scope.NativeApplied);
            Vector3 at = collision.contacts.Length != 0 ? collision.contacts[0].point : source.transform.position;
            foreach (uint peer in RecordedOccupants(source))
            {
                // This local occupant already ran the real native callback, including its
                // random swear and any authoritative world consequence. Other occupants have
                // personal effects delivered while they still occupy this chassis.
                if (scope.NativeApplied && peer == events.LocalPeer) continue;
                events.PostCrashEffect(new PresentationEvent(0, (byte)PresentationKind.VehicleCrash,
                    effect, peer, target, at.x, at.y, at.z, Mathf.Clamp(collision.relativeVelocity.magnitude, 0f, 200f)));
            }
        }

        private static byte Classify(VehicleController? source, Collision? collision)
        {
            if (source == null || collision?.collider == null) return 0;
            float speed = collision.relativeVelocity.magnitude;
            if (float.IsNaN(speed) || float.IsInfinity(speed)) return 0;
            if (source.vehicle_type == Vehicle_Type.LeMissile &&
                source.GetComponent<Event_DerbyDemolition_Vehicle>()?.Get_IsEventRunning == true) return Derby;
            if (collision.collider.GetComponent<CollisionNoAnxietyDamage>() != null || speed <= 8f) return 0;
            return collision.collider.tag == "Actor" ? HitActor : Heavy;
        }

        internal static bool IsReport(PresentationEvent e) => e.Kind == (byte)PresentationKind.VehicleCrash &&
            e.Variant >= Heavy && e.Variant <= HitActor;
        internal static bool IsDelivery(PresentationEvent e) => e.Kind == (byte)PresentationKind.VehicleCrash &&
            e.Variant >= (Delivery | Heavy) && e.Variant <= (Delivery | HitActor);
        internal static bool ValidReport(PresentationEvent e) => IsReport(e) && e.Value >= 0f && e.Value <= 200f &&
            (e.Variant == Derby || e.Value > 8f);

        internal static bool AcceptRecipient(PresentationEvent e)
        {
            if (!ValidReport(e) || Resolve(e.Target) is not VehicleController source) return false;
            var session = MonMPPlugin.Instance?.Session;
            if (session?.IsRunning != true || e.Actor != session.LocalPeerId && !session.Peers.ContainsKey(e.Actor)) return false;
            bool derby = source.vehicle_type == Vehicle_Type.LeMissile &&
                source.GetComponent<Event_DerbyDemolition_Vehicle>()?.Get_IsEventRunning == true;
            if ((e.Variant == Derby) != derby) return false;
            Remember(source, e.Target);
            return occupants.TryGetValue((e.Target, e.Actor), out float seen) &&
                Time.unscaledTime - seen >= 0f && Time.unscaledTime - seen <= RecipientGraceSeconds;
        }

        internal static PresentationEvent Deliver(PresentationEvent e) =>
            new PresentationEvent(0, e.Kind, (byte)(e.Variant | Delivery), e.Actor, e.Target, e.X, e.Y, e.Z, e.Value);

        internal static void Play(PresentationEvent e)
        {
            var plugin = MonMPPlugin.Instance;
            var world = plugin?.World;
            if (!IsDelivery(e) || e.Actor != world?.Events?.LocalPeer || world?.IsDisposed != false ||
                plugin?.Session?.IsRunning != true || !(e.Value >= 0f && e.Value <= 200f)) return;
            var gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
            var needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>();
            if (gameplay == null || needs == null) return;
            var source = Resolve(e.Target);
            // A host seat lease can trail a forced/native exit. Validate actual
            // local attachment instead of trusting the same possibly stale lease
            // again at delivery. Late packets must not affect an exited player.
            bool nativeSeat = (gameplay.Get_PlayerState == PlayerState.InVehicle_Driver ||
                gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger) && gameplay.Get_CurrentVehicleController == source &&
                gameplay.PlayerWalking != null && source != null && gameplay.PlayerWalking.transform.IsChildOf(source.transform);
            bool addedSeat = gameplay.Get_PlayerState == PlayerState.InFurnitureChair &&
                world.ExtraSeats.CurrentLocalPassengerVehicle == source;
            if (source == null || !nativeSeat && !addedSeat) return;
            switch (e.Variant & ~Delivery)
            {
                case Heavy:
                    if (e.Value <= 8f) return;
                    needs.HeavyCrash(e.Value);
                    // The native method chooses a reaction independently for each participant.
                    // The existing PlayerVoiceEmotePatch relays the clip after its native cooldown.
                    if (UnityEngine.Random.value >= 0.5f)
                        UnityEngine.Object.FindObjectOfType<SOUND_Manager>()?.PlaySound_PlayerVoice_Sacre();
                    break;
                case Derby: needs.DerbyCrash(e.Value); break;
                case HitActor:
                    if (e.Value > 8f) gameplay.InitiateDeath(TEXT.GetText("DEATH_HitActor"));
                    break;
            }
        }

        private static bool TryTarget(VehicleController source, out ulong target)
        {
            target = 0;
            var world = MonMPPlugin.Instance?.World;
            return world?.IsDisposed == false &&
                (world.Vehicles.TryGetCrashTarget(source, out target) || world.SchoolBus.TryGetCrashTarget(source, out target) ||
                 world.MartinCar.TryGetCrashTarget(source, out target));
        }
        private static VehicleController? Resolve(ulong target)
        {
            var world = MonMPPlugin.Instance?.World;
            return world?.Vehicles.VehicleFor(target) ?? world?.SchoolBus.CrashVehicleFor(target) ?? world?.MartinCar.CrashVehicleFor(target);
        }
        private static uint[] RecordedOccupants(VehicleController source)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session == null || plugin.World == null) return Array.Empty<uint>();
            var found = new HashSet<uint>();
            foreach (uint peer in plugin.Session.Peers.Keys)
                if (Occupies(peer, source)) found.Add(peer);
            if (Occupies(plugin.Session.LocalPeerId, source)) found.Add(plugin.Session.LocalPeerId);
            var result = new uint[found.Count]; found.CopyTo(result); return result;
        }
        private static bool Occupies(uint peer, VehicleController source)
        {
            var plugin = MonMPPlugin.Instance;
            var world = plugin?.World;
            if (world == null) return false;
            return VehicleForPeer(peer) == source;
        }
        private static VehicleController? VehicleForPeer(uint peer)
        {
            var plugin = MonMPPlugin.Instance;
            var world = plugin?.World;
            if (world == null) return null;
            var source = world.Vehicles.VehicleForPeer(peer) ?? world.SchoolBusPassengers.VehicleForPeer(peer) ??
                world.ExtraSeats.VehicleForPeer(peer) ?? world.MartinCar.VehicleForPeer(peer);
            if (source != null) return source;
            if (peer != plugin?.Session?.LocalPeerId) return null;
            // Tick reaches this path every frame while the host is walking. The
            // plugin already owns the gameplay reference for this live world.
            var gameplay = plugin?.Gameplay;
            return gameplay != null && (gameplay.Get_PlayerState == PlayerState.InVehicle_Driver ||
                gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger) ? gameplay.Get_CurrentVehicleController as VehicleController : null;
        }
        private static void Remember(VehicleController source, ulong target)
        {
            foreach (uint peer in RecordedOccupants(source)) Remember(target, peer);
        }
        private static void Remember(ulong target, uint peer)
        {
            var key = (target, peer);
            if (!occupants.ContainsKey(key) && occupants.Count >= MaximumRememberedOccupancies)
            {
                // Current occupants always qualify directly; only the departure-race
                // grace history needs eviction when there is unusually heavy seat churn.
                (ulong Vehicle, uint Peer) oldest = default;
                float oldestTime = float.MaxValue;
                foreach (var entry in occupants)
                    if (entry.Value < oldestTime) { oldest = entry.Key; oldestTime = entry.Value; }
                occupants.Remove(oldest);
            }
            occupants[key] = Time.unscaledTime;
        }
        internal static void Tick()
        {
            var plugin = MonMPPlugin.Instance;
            var world = plugin?.World;
            if (world?.Events?.IsHost != true) return;
            // Resolve each connected participant once instead of scanning every chassis
            // against every participant in dense purchased-vehicle scenes.
            foreach (uint peer in plugin!.Session!.Peers.Keys)
            {
                var source = VehicleForPeer(peer);
                if (source == null) continue;
                ulong target = world.Vehicles.EntityOf(source);
                if (target == 0) TryTarget(source, out target);
                if (target != 0) Remember(target, peer);
            }
            expiredOccupants.Clear();
            foreach (var entry in occupants)
                if (Time.unscaledTime - entry.Value > RecipientGraceSeconds || Time.unscaledTime < entry.Value) expiredOccupants.Add(entry.Key);
            foreach (var key in expiredOccupants) occupants.Remove(key);
            expiredOccupants.Clear();
        }
        internal static void Reset() { current = null; occupants.Clear(); expiredOccupants.Clear(); }
    }
}
