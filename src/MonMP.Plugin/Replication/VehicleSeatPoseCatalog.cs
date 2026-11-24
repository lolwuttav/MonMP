using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NWH;
using NWH.DWP2.ShipController;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Model/seat identities are presentation identities, independent of network entity
    // IDs. A bought copy of the same model can reuse a saved pose; movable native seats
    // retain their anchor so adjustments and chassis motion continue to work normally.
    internal static class VehicleSeatPoseCatalog
    {
        internal sealed class Entry
        {
            internal Vehicle Vehicle = null!;
            internal Transform Anchor = null!;
            internal string VehicleKey = "", SeatKey = "", Shape = "", Label = "";
            internal StockAvatarBody.Mode Mode;
            internal bool AlignHips;

            // These values must remain live while the editor is open and while drivers
            // steer. Do not freeze the wheel angle in a discovered catalog snapshot.
            internal Transform? Wheel => Mode != StockAvatarBody.Mode.Drive || Vehicle == null ? null :
                Vehicle is VehicleController car ? car.steering?.steeringWheel :
                Vehicle is AdvancedShipController boat ? boat.vehicle_Visual_SteeringWheel : null;
            internal float WheelAngle => Vehicle == null ? 0f :
                Vehicle is VehicleController car && car.steering != null ? car.steering.Angle * car.steering.steeringWheelTurnRatio :
                Vehicle is AdvancedShipController boat && boat.rudders != null && boat.rudders.Count > 0 && boat.rudders[0] != null
                    ? boat.rudders[0].Angle * boat.SteeringWheel_TurnRatio : 0f;
            internal Vector3 VehicleRight => Vehicle != null ? Vehicle.transform.right : Vector3.right;
        }

        private const int MaximumCachedSeats = 2048;

        internal static IReadOnlyList<Entry> Discover(SharedWorld? world)
        {
            var entries = new List<Entry>();
            var anchors = new HashSet<Transform>();
            var byAnchor = world?.SeatPoseEntries;
            byAnchor?.Clear();

            void Add(Entry? entry)
            {
                if (entry == null || !anchors.Add(entry.Anchor)) return;
                entries.Add(entry);
                if (byAnchor != null && byAnchor.Count < MaximumCachedSeats) byAnchor[entry.Anchor.GetInstanceID()] = entry;
            }

            // Prefer Konig's movable seat when two interaction components happen to
            // reference the same anchor. Component enumeration never assigns a role.
            foreach (var seat in UnityEngine.Object.FindObjectsOfType<Konig_Seat>(true))
                if (seat != null) Add(Native(seat.vehicleController, seat.seatPosition, seat, seat.Get_isDriverSeat));
            foreach (var seat in UnityEngine.Object.FindObjectsOfType<Vehicle_DriverSeat>(true))
                if (seat != null) Add(Native(seat.vehicleController, seat.seatPosition, seat, true));
            foreach (var seat in UnityEngine.Object.FindObjectsOfType<Vehicle_PassengerSeat>(true))
                if (seat != null) Add(Native(seat.vehicleController, seat.seatPosition, seat, false));
            if (world != null)
                foreach (var seat in world.ExtraSeats.VehicleSeatAnchors())
                    Add(Extra(seat.Vehicle, seat.Anchor, seat.Kind, seat.Slot));

            return entries.OrderBy(entry => entry.VehicleKey, StringComparer.Ordinal)
                .ThenBy(entry => entry.SeatKey, StringComparer.Ordinal).ToArray();
        }

        internal static Entry? Resolve(SharedWorld? world, uint peer, Transform anchor, bool driver,
            SharedExtraSeats.SeatKind extraKind)
        {
            if (anchor == null) return null;
            // Mono's weak-table generic cast path crashed native remote presentation. The
            // world now owns a bounded ordinary cache and clears it on session teardown.
            var byAnchor = world?.SeatPoseEntries;
            int id = anchor.GetInstanceID();
            if (byAnchor != null && byAnchor.TryGetValue(id, out var entry))
            {
                if (ReferenceEquals(entry.Anchor, anchor) && entry.Vehicle != null && anchor.IsChildOf(entry.Vehicle.transform))
                    return (entry.Mode == StockAvatarBody.Mode.Drive) == driver ? entry : null;
                // A surviving seat object can be reparented or its old vehicle can
                // unload. Do not let the prior positive lookup hide its new model.
                byAnchor.Remove(id);
            }

            // Use the provider that selected this anchor. There is no scene search in
            // the avatar update path, and a model's native topology is scanned once.
            Vehicle? vehicle = world?.Vehicles.VehicleForPeer(peer) ?? world?.SchoolBusPassengers.VehicleForPeer(peer) ??
                world?.MartinCar?.VehicleForPeer(peer) ?? (Vehicle?)world?.BoatMotion.VehicleForPeer(peer);
            vehicle ??= anchor.GetComponentInParent<Vehicle>();
            Entry? resolved = null;
            if (!driver && world != null)
                foreach (var extra in world.ExtraSeats.VehicleSeatAnchors())
                    if (extra.Anchor == anchor && extra.Kind == extraKind)
                    { resolved = Extra(extra.Vehicle, anchor, extra.Kind, extra.Slot); break; }
            if (resolved == null && vehicle != null)
            {
                foreach (var seat in vehicle.GetComponentsInChildren<Konig_Seat>(true))
                    if (seat != null && seat.seatPosition == anchor && seat.Get_isDriverSeat == driver)
                    { resolved = Native(vehicle, anchor, seat, driver); break; }
                if (resolved == null && driver)
                    foreach (var seat in vehicle.GetComponentsInChildren<Vehicle_DriverSeat>(true))
                        if (seat != null && seat.seatPosition == anchor)
                        { resolved = Native(vehicle, anchor, seat, true); break; }
                if (resolved == null && !driver)
                    foreach (var seat in vehicle.GetComponentsInChildren<Vehicle_PassengerSeat>(true))
                        if (seat != null && seat.seatPosition == anchor)
                        { resolved = Native(vehicle, anchor, seat, false); break; }
            }
            // Do not retain misses: a native seat/part can be installed later while
            // its anchor survives. Successful render lookups remain cached.
            if (resolved != null && byAnchor != null)
            {
                if (byAnchor.Count >= MaximumCachedSeats) byAnchor.Clear();
                byAnchor[id] = resolved;
            }
            return resolved;
        }

#if MONMP_DEVELOPMENT
        internal static void TestRuntimeCache(SharedWorld world)
        {
            var entries = Discover(world);
            if (entries.Count == 0) throw new InvalidOperationException("Native seat catalog is empty.");
            int lookups = 0;
            foreach (var entry in entries)
            {
                bool driver = entry.Mode == StockAvatarBody.Mode.Drive;
                var kind = SharedExtraSeats.SeatKind.Stool;
                if (entry.SeatKey.StartsWith("extra/", StringComparison.Ordinal)) Enum.TryParse(entry.SeatKey.Split('/')[1], out kind);
                for (int i = 0; i < 1000; i++)
                {
                    if (!ReferenceEquals(Resolve(world, 9996, entry.Anchor, driver, kind), entry) ||
                        Resolve(world, 9996, entry.Anchor, !driver, kind) != null)
                        throw new InvalidOperationException("Native seat cache lost anchor identity or driver/passenger separation.");
                    lookups += 2;
                }
            }
            var first = entries.First(e => !e.SeatKey.StartsWith("extra/", StringComparison.Ordinal));
            world.SeatPoseEntries.Clear();
            var cold = Resolve(world, 9996, first.Anchor, first.Mode == StockAvatarBody.Mode.Drive, SharedExtraSeats.SeatKind.Stool);
            if (cold == null || cold.Shape != first.Shape || cold.VehicleKey != first.VehicleKey || cold.SeatKey != first.SeatKey)
                throw new InvalidOperationException("Clearing the session seat cache lost native pose compatibility.");
            Debug.Log("MonMP native seat catalog cache passed: " + lookups + " lookups, exact anchors and roles, cold rebuild, session-owned bounded cache.");
        }
#endif

        private static Entry? Native(Vehicle? vehicle, Transform? anchor, Component component, bool driver)
        {
            if (!Valid(vehicle, anchor) || component == null || !component.transform.IsChildOf(vehicle!.transform)) return null;
            string anchorPath = RelativePath(vehicle!.transform, anchor!);
            string role = driver ? "driver" : "passenger";
            string componentType = component.GetType().FullName ?? component.GetType().Name;
            string key = ModelKey(vehicle);
            return new Entry
            {
                Vehicle = vehicle, Anchor = anchor!, VehicleKey = key,
                SeatKey = role + "/" + anchorPath,
                Shape = Shape(key, vehicle.GetType().FullName ?? vehicle.GetType().Name, componentType,
                    RelativePath(vehicle.transform, component.transform), anchorPath),
                Label = ModelLabel(vehicle) + " — " + role + " (" + anchorPath + ")",
                Mode = driver ? StockAvatarBody.Mode.Drive : StockAvatarBody.Mode.Sit
            };
        }

        private static Entry? Extra(VehicleController vehicle, Transform anchor, SharedExtraSeats.SeatKind kind, int slot)
        {
            if (!Valid(vehicle, anchor) || kind == SharedExtraSeats.SeatKind.Stool) return null;
            string key = ModelKey(vehicle);
            string role = "extra/" + kind + "/" + slot;
            return new Entry
            {
                Vehicle = vehicle, Anchor = anchor, VehicleKey = key, SeatKey = role,
                Shape = Shape(key, vehicle.GetType().FullName ?? vehicle.GetType().Name, "MonMP.ExtraSeat/" + kind,
                    role, RelativePath(vehicle.transform, anchor)),
                Label = ModelLabel(vehicle) + " — " + kind + " " + (slot + 1),
                Mode = kind == SharedExtraSeats.SeatKind.Ground ? StockAvatarBody.Mode.Ground : StockAvatarBody.Mode.Chair,
                AlignHips = kind == SharedExtraSeats.SeatKind.Ground
            };
        }

        private static bool Valid(Vehicle? vehicle, Transform? anchor) => vehicle != null && anchor != null &&
            vehicle.gameObject.scene.IsValid() && vehicle.gameObject.scene.isLoaded && anchor.IsChildOf(vehicle.transform);

        // NWH.Vehicle has a native model enum but no prefab-name field. The root
        // name distinguishes models sharing AI_Vehicle (school bus and Martin).
        private static string ModelKey(Vehicle vehicle) => vehicle.vehicle_type + "/" + ModelName(vehicle);
        private static string ModelLabel(Vehicle vehicle) => ModelName(vehicle) + " [" + vehicle.vehicle_type + "]";
        private static string ModelName(Vehicle vehicle) => vehicle.gameObject.name.Replace("(Clone)", "").Trim();

        private static string Shape(string model, string controller, string component, string componentPath, string anchorPath)
        {
            using var digest = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes("vehicle-seat-pose-v1\n" + model + "\n" + controller + "\n" +
                component + "\n" + componentPath + "\n" + anchorPath);
            return BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "");
        }

        private static string RelativePath(Transform root, Transform value)
        {
            var segments = new Stack<string>();
            for (var current = value; current != null && current != root; current = current.parent)
            {
                // Unrelated parts and paint can add siblings. Only a preceding sibling
                // with this same authored name disambiguates the anchor's segment.
                int ordinal = 0;
                var parent = current.parent;
                if (parent != null)
                    for (int i = 0; i < current.GetSiblingIndex(); i++)
                        if (string.Equals(parent.GetChild(i).name, current.name, StringComparison.Ordinal)) ordinal++;
                string name = current.name.Replace("%", "%25").Replace("/", "%2F").Replace("#", "%23");
                segments.Push(ordinal == 0 ? name : name + "#" + ordinal);
            }
            return segments.Count == 0 ? "." : string.Join("/", segments);
        }
    }
}
