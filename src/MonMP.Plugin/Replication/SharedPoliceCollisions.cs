using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedPoliceSpots
    {
        private readonly HashSet<(uint Peer, Guid Operation)> collisionReceipts = new HashSet<(uint, Guid)>();
        private readonly Queue<(uint Peer, Guid Operation)> collisionOrder = new Queue<(uint, Guid)>();

        // Called only for a contact accepted by the local chassis DamageHandler. Native
        // Gameplay checks this exact collider/component, not arbitrary police ancestry.
        internal void ReportCollision(VehicleController source, Collision collision, bool nativeApplied)
        {
            var plugin = MonMPPlugin.Instance;
            if (session?.IsRunning != true || channel?.Ready != true || source == null || collision?.collider == null ||
                host && nativeApplied || collision.collider.tag != "Vehicle" ||
                collision.collider.GetComponent<Store_PoliceInterceptor_Car>() is not { } car ||
                !bySpot.TryGetValue(car.Get_Police_SpotDetails, out var binding) ||
                !plugin!.World!.Vehicles.TryGetDriverState(source, out var state) || state.Driver.Owner != session.LocalPeerId) return;
            Vector3 at = collision.contacts.Length > 0 ? collision.contacts[0].point : collision.collider.ClosestPoint(source.transform.position);
            var request = new PoliceCollisionRequest(Guid.NewGuid(), plugin.World.Vehicles.EntityOf(source), state.Driver.Generation,
                at.x, at.y, at.z, Mathf.Clamp(collision.relativeVelocity.magnitude, 0, 200));
            if (host) return; // A host driver uses vanilla's already-authoritative ticket callback.
            channel.Request(binding.Id, request.Encode(), true);
        }

        private void CollisionCommand(uint peer, ulong id, byte[] payload)
        {
            if (!host || session?.IsRunning != true || channel?.Ready != true || peer == 0 ||
                !bindings.TryGetValue(id, out var binding) || !binding.Root.activeInHierarchy ||
                binding.Car == null || !binding.Car.isActiveAndEnabled || !binding.Car.gameObject.activeInHierarchy ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;
            PoliceCollisionRequest request;
            try { request = PoliceCollisionRequest.Decode(payload); } catch (InvalidDataException) { return; }
            var receipt = (peer, request.Operation);
            if (collisionReceipts.Contains(receipt)) return;
            var vehicles = MonMPPlugin.Instance?.World?.Vehicles;
            if (vehicles == null || !vehicles.TryAuthorizePoliceRadar(peer, request.Vehicle, request.Generation,
                out var vehicle, out var driver, out _, out float speed, out float age) || vehicle == null) return;
            var at = new Vector3(request.X, request.Y, request.Z);
            bool policeContact = binding.Car.GetComponents<Collider>().Any(c => c != null && c.enabled && !c.isTrigger &&
                c.gameObject.activeInHierarchy && c.tag == "Vehicle" && (c.ClosestPoint(at) - at).sqrMagnitude <= 1f);
            // Accepted owner samples and the rendered remote body can be one send/interpolation
            // interval behind the actual contact. Bound that travel around real chassis geometry.
            float travel = Mathf.Min(12f, 2f + speed / 3.6f * Mathf.Min(age + 0.2f, 0.5f));
            bool chassisContact = vehicle.GetComponentsInChildren<Collider>(true).Any(c => c != null && c.enabled &&
                !c.isTrigger && c.gameObject.activeInHierarchy && c.GetComponentInParent<VehicleController>() == vehicle &&
                (c.ClosestPoint(at) - at).sqrMagnitude <= travel * travel);
            if (!policeContact || !chassisContact || (driver - at).sqrMagnitude > (20f + travel) * (20f + travel)) return;
            // Retire before native mutation: neither a duplicate nor a UI failure can create a second fine.
            collisionReceipts.Add(receipt); collisionOrder.Enqueue(receipt);
            while (collisionOrder.Count > 1024) collisionReceipts.Remove(collisionOrder.Dequeue());
            CreateTicket(peer, binding, new PoliceTicketData {
                TicketType = PoliceTicketData.Ticket_Type.Crash, DaySinceTicket = 0,
                PlayerSpeed = 0, SpeedLimit = 0, Vehicle_Type = Vehicle_Type.Useless });
        }
    }
}
