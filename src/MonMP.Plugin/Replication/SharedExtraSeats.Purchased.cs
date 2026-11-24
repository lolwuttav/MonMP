using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedExtraSeats
    {
        private readonly Dictionary<ulong, (Vehicle_Type Type, GameObject Instance)> purchasedSeatVehicles =
            new Dictionary<ulong, (Vehicle_Type, GameObject)>();
        private readonly Dictionary<ulong, VehiclePassengerSeatState> retainedPurchasedSeatStates =
            new Dictionary<ulong, VehiclePassengerSeatState>();

        internal IEnumerable<ulong> RequiredCatalogEntityIds => seats.Values
            .Where(seat => seat.PurchasedVehicleEntity == 0 && seat.Id > WorldRecord.DynamicIdStart).Select(seat => seat.Id);
        internal bool IsPurchasedRecord(ulong id) => seats.TryGetValue(id, out var seat) && seat.PurchasedVehicleEntity != 0;
        private static bool HasExtraVehicleSeats(Vehicle_Type type) => type == Vehicle_Type.OlTruck || type == Vehicle_Type.SmollATV;
        private static bool IsStaticVehicleSeatCandidate(VehicleController vehicle) => vehicle != null &&
            HasExtraVehicleSeats(vehicle.vehicle_type) && vehicle.gameObject.scene.IsValid() &&
            SharedVehicleSales.Active?.OwnsSaleClone(vehicle.transform) != true;

        private void RegisterInitialVehicleSeats()
        {
            foreach (var vehicle in UnityEngine.Object.FindObjectsOfType<VehicleController>(true)
                .Where(IsStaticVehicleSeatCandidate)
                .OrderBy(vehicle => RuntimeProbe.CanonicalPath(vehicle.transform), StringComparer.Ordinal))
                RegisterSceneVehicleSeats(vehicle);
            DiscoverPurchasedSeats();
        }

        private void RegisterSceneVehicleSeats(VehicleController vehicle)
        {
            if (!IsStaticVehicleSeatCandidate(vehicle)) return;
            RegisterVehicleSeats(vehicle, 0);
        }

        private void DiscoverPurchasedSeats()
        {
            var sales = SharedVehicleSales.Active;
            if (sales == null) return;
            foreach (var purchase in sales.PurchasedVehicles())
                RegisterPurchasedVehicle(purchase.Entity, purchase.Type, purchase.Instance);
        }

        // A sale's stable entity/type/slot identifies these seats, never its clone's scene
        // path or sibling index. The constructor and live sale callback share this route.
        internal bool RegisterPurchasedVehicle(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (disposed || ChairSeatField == null || !HasExtraVehicleSeats(type)) return false;
            if (entity <= WorldRecord.DynamicIdStart) throw new InvalidOperationException("Purchased extra-seat root is not dynamic.");
            if (instance == null || !instance.scene.IsValid()) throw new InvalidOperationException("Purchased extra-seat root is outside the active scene.");
            if (purchasedSeatVehicles.TryGetValue(entity, out var existing))
            {
                if (existing.Type != type || existing.Instance != instance)
                    throw new InvalidOperationException("Conflicting purchased extra-seat root for entity " + entity.ToString("X16") + ".");
                HydratePurchasedSeats(entity);
                return true;
            }
            if (purchasedSeatVehicles.Any(pair => pair.Key != entity && pair.Value.Instance == instance))
                throw new InvalidOperationException("Purchased vehicle already has a different extra-seat sale identity.");
            var controllers = instance.GetComponentsInChildren<VehicleController>(true);
            if (controllers.Length != 1 || controllers[0] == null || controllers[0].vehicle_type != type)
                throw new InvalidOperationException("Purchased extra-seat root has an unsupported native controller layout.");
            var vehicle = controllers[0];
            if (seats.Values.Any(seat => seat.Truck == vehicle))
                throw new InvalidOperationException("Purchased vehicle already has scene-path extra seats.");
            int count = type == Vehicle_Type.OlTruck ? 6 : 2;
            for (int slot = 0; slot < count; slot++)
            {
                string identity = PurchasedSeatIdentity(entity, type, slot);
                ulong id = PurchasedSeatEntityId(identity);
                if (seats.ContainsKey(id) || identities.Resolve(id) != null)
                    throw new InvalidOperationException("Purchased extra-seat identity collides with an existing world entity: " + identity);
            }
            RegisterVehicleSeats(vehicle, entity);
            purchasedSeatVehicles.Add(entity, (type, instance));
            HydratePurchasedSeats(entity);
            if (channel != null) BindSurfaceGlows();
            Write();
            return true;
        }

        private void RegisterVehicleSeats(VehicleController vehicle, ulong purchasedEntity)
        {
            bool truck = vehicle.vehicle_type == Vehicle_Type.OlTruck;
            int count = truck ? 6 : 2;
            string path = purchasedEntity == 0 ? RuntimeProbe.CanonicalPath(vehicle.transform) : "";
            for (int slot = 0; slot < count; slot++)
            {
                var root = new GameObject((truck ? "MonMP Seat " : "MonMP Rider Seat ") + slot);
                root.transform.SetParent(vehicle.transform, false);
                var anchor = new GameObject("Anchor").transform; anchor.SetParent(root.transform, false);
                var location = new GameObject("SeatLocation").transform; location.SetParent(root.transform, false);
                // Preserve the native-state adapter and collider-free moving-seat layout.
                var chair = root.AddComponent<Furniture_Chair>();
                chair.Name = truck ? "Sit" : "Ride";
                ChairSeatField!.SetValue(chair, location);
                var identity = purchasedEntity == 0 ? path + (truck ? "/monmp-extra-seat-v1/" : "/monmp-atv-rider-v1/") + slot :
                    PurchasedSeatIdentity(purchasedEntity, vehicle.vehicle_type, slot);
                Add(new Seat
                {
                    Kind = truck ? slot < 2 ? SeatKind.Cab : SeatKind.Ground : SeatKind.Rider,
                    Parent = vehicle.transform, Anchor = anchor, SeatLocation = location,
                    Chairs = new[] { chair }, Created = root, Truck = vehicle, Slot = slot,
                    PurchasedVehicleEntity = purchasedEntity
                }, identity, identities);
                if (!truck || slot >= 2) AddPad(root, truck ? new Vector3(0.4f, 0.03f, 0.4f) : new Vector3(0.28f, 0.03f, 0.34f));
            }
        }

        private static string PurchasedSeatIdentity(ulong entity, Vehicle_Type type, int slot) =>
            "purchased-extra-seat-v1/" + entity.ToString("X16", CultureInfo.InvariantCulture) + "/" +
            ((int)type).ToString(CultureInfo.InvariantCulture) + "/" + slot.ToString(CultureInfo.InvariantCulture);
        private static ulong PurchasedSeatEntityId(string identity) =>
            WorldRecord.DynamicIdStart | Math.Max(1UL, StaticId.Hash(identity) & 0x7FFFFFFFFFFFFFFFUL);

        private void HydratePurchasedSeats(ulong entity)
        {
            // SharedWorld attaches its prepared channel before NetworkSession.Host establishes
            // the role. Its initial host catalog already contains preowned seats; only a live
            // host may inspect that catalog or register a later purchase.
            if (channel == null || host && session?.IsHost != true) return;
            foreach (var seat in seats.Values.Where(seat => seat.PurchasedVehicleEntity == entity))
            {
                if (host)
                {
                    if (channel.Records.TryGetValue(seat.Id, out var existing))
                    {
                        if (existing.Adapter != VehiclePassengerSeatState.Adapter) throw new InvalidOperationException("Purchased extra-seat record adapter changed.");
                    }
                    else channel.Register(new WorldRecord(seat.Id, 1, VehiclePassengerSeatState.Adapter, seat.State.Encode()));
                }
                else if (retainedPurchasedSeatStates.TryGetValue(seat.Id, out var retained))
                {
                    Apply(seat, retained);
                    retainedPurchasedSeatStates.Remove(seat.Id);
                }
                else if (channel.Records.TryGetValue(seat.Id, out var record))
                {
                    if (record.Adapter != VehiclePassengerSeatState.Adapter) throw new InvalidDataException("Purchased extra-seat record adapter changed.");
                    Apply(seat, VehiclePassengerSeatState.Decode(record.Payload));
                }
            }
        }

        private void ObserveSeatRecord(WorldRecord record)
        {
            var state = VehiclePassengerSeatState.Decode(record.Payload);
            if (seats.TryGetValue(record.Id, out var seat)) Apply(seat, state);
            else if (record.Id > WorldRecord.DynamicIdStart) retainedPurchasedSeatStates[record.Id] = state;
        }

#if MONMP_DEVELOPMENT
        // Pure catalog/binding inspection for paired purchase/rejoin probes; no seat use.
        internal ulong[] TestPurchasedSeatIds(ulong entity, GameObject instance)
        {
            if (channel == null || !purchasedSeatVehicles.TryGetValue(entity, out var purchase) ||
                purchase.Instance != instance || instance == null)
                throw new InvalidOperationException("Purchased extra-seat instance is not registered.");
            var controllers = instance.GetComponentsInChildren<VehicleController>(true);
            if (controllers.Length != 1 || controllers[0].vehicle_type != purchase.Type)
                throw new InvalidOperationException("Purchased extra-seat native layout changed.");
            int count = purchase.Type == Vehicle_Type.OlTruck ? 6 : 2;
            var bound = seats.Values.Where(seat => seat.PurchasedVehicleEntity == entity).OrderBy(seat => seat.Slot).ToArray();
            if (bound.Length != count) throw new InvalidOperationException("Purchased extra-seat count differs from its native vehicle family.");
            for (int slot = 0; slot < count; slot++)
            {
                var seat = bound[slot];
                string identity = PurchasedSeatIdentity(entity, purchase.Type, slot);
                if (seat.Slot != slot || seat.Id != PurchasedSeatEntityId(identity) || seat.Identity != identity ||
                    seat.Truck != controllers[0] || seat.Parent != controllers[0].transform || seat.Created == null ||
                    seat.Chairs.Length != 1 || !byChair.TryGetValue(seat.Chairs[0], out var mapped) || mapped != seat ||
                    !channel.Records.TryGetValue(seat.Id, out var record) || record.Adapter != VehiclePassengerSeatState.Adapter)
                    throw new InvalidOperationException("Purchased extra-seat identity, native binding or channel record differs.");
                var state = VehiclePassengerSeatState.Decode(record.Payload);
                if (state.Owner != seat.State.Owner || state.Generation != seat.State.Generation)
                    throw new InvalidOperationException("Purchased extra-seat occupancy record was not applied to its binding.");
            }
            return bound.Select(seat => seat.Id).OrderBy(id => id).ToArray();
        }
#endif
    }
}
