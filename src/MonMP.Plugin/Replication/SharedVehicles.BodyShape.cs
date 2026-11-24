using System.Collections.Generic;
using System.Linq;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicles
    {
        // Vehicle_PartSocket.InstallPart parents player-installed parts (and their bolts) under the
        // vehicle with a kinematic Rigidbody. Those follow the chassis rigidly and are replicated by
        // the part systems, so they are not independent vehicle bodies.
        internal static bool IsInstalledAttachmentBody(Rigidbody body, VehicleController vehicle) =>
            body != null && body != vehicle.vehicleRigidbody && body.isKinematic &&
            body.GetComponent<Joint>() == null && body.GetComponent<InteractableItems>() != null;

        internal static Rigidbody[] VehicleBodies(VehicleController vehicle, bool includeInactive = true) =>
            vehicle.GetComponentsInChildren<Rigidbody>(includeInactive).Where(body => !IsInstalledAttachmentBody(body, vehicle)).ToArray();

        // Native Release/LoadFromSaveData adds one Rigidbody/HingeJoint to an open tailgate.
        // It is already synchronized by SharedVehicleTrunk, rather than by chassis motion. Use
        // the parts manager's exact reference, never a body name or any arbitrary hinged child.
        // All three truck admission paths share this check (starter, sale prefab and live purchase).
        internal static bool HasSupportedTruckBodyConfiguration(VehicleController vehicle, Rigidbody? chassis,
            Rigidbody? accessory, Rigidbody[] bodies, bool includeInactive, out string reason)
        {
            reason = "";
            if (chassis == null || accessory == null || accessory == chassis)
            { reason = "missing chassis or accessory"; return false; }

            var allowed = new HashSet<Rigidbody>(vehicle.GetComponentsInChildren<Vehicle_Door>(includeInactive)
                .Select(door => door.GetComponent<Rigidbody>()).Where(body => body != null).Cast<Rigidbody>()) { chassis, accessory };
            var tailgate = vehicle.GetComponent<Vehicle_PartsManager>()?.Get_Vehicle_Trunk;
            if (!TryAddTrunkBody(vehicle, tailgate, chassis, allowed))
            { reason = "unrecognized native tailgate body/hinge"; return false; }
            if (bodies.Length == allowed.Count && bodies.All(allowed.Contains)) return true;
            reason = "rigidbodies=" + bodies.Length + "; allowed=" + allowed.Count + "; unexpected=" +
                string.Join(",", bodies.Where(body => !allowed.Contains(body)).Take(6)
                    .Select(body => body.name + (body.isKinematic ? "(kinematic)" : "(dynamic)")));
            return false;
        }

        private static bool TryAddTrunkBody(VehicleController vehicle, Vehicle_Trunk? trunk, Rigidbody chassis, HashSet<Rigidbody> allowed) =>
            trunk == null || TryAddHingeBody(vehicle, trunk, chassis, trunk.vehicleController == chassis, trunk.Get_IsOpen, allowed);

        private static bool TryAddHingeBody(VehicleController vehicle, Component part, Rigidbody chassis,
            bool nativeOwnerMatches, bool open, HashSet<Rigidbody> allowed)
        {
            var body = part.GetComponent<Rigidbody>();
            if (body == null) return true;
            var hinge = part.GetComponent<HingeJoint>();
            return part.transform.IsChildOf(vehicle.transform) && nativeOwnerMatches && open && body != chassis &&
                part.GetComponents<Rigidbody>().Length == 1 && part.GetComponents<Joint>().Length == 1 &&
                hinge != null && hinge.connectedBody == chassis && allowed.Add(body);
        }

        // The racecar's doors use existing hinge adapters too.
        // They must not make a built vehicle look like an unsupported multi-body chassis.
        internal static bool HasSupportedBuiltVehicleBodyConfiguration(VehicleController vehicle, Rigidbody[] bodies)
        {
            var chassis = vehicle.vehicleRigidbody;
            if (chassis == null) return false;
            var allowed = new HashSet<Rigidbody> { chassis };
            foreach (var door in vehicle.GetComponentsInChildren<Vehicle_Door>(true))
                if (!TryAddHingeBody(vehicle, door, chassis, door.vehicleController == vehicle, door.Get_IsOpen, allowed)) return false;
            if (!TryAddTrunkBody(vehicle, PrimaryTrunk(vehicle), chassis, allowed)) return false;
            return bodies.Length == allowed.Count && bodies.All(allowed.Contains);
        }

        // These three native Konig-family shells have one fixed glovebox path.
        // SharedVehicleTrunk already handles it through the tow-carrier late record.
        // Do not accept any other extra trunk merely because it has a hinge component.
        internal static bool TryAddSupportedGloveboxBody(VehicleController vehicle, HashSet<Rigidbody> allowed)
        {
            string? path = vehicle.vehicle_type == Vehicle_Type.Prinz ? "Hatch/GloveBox" :
                vehicle.vehicle_type == Vehicle_Type.MainVehicle || vehicle.vehicle_type == Vehicle_Type.ScrapyardKonig
                    ? "Konig/Interior_Dash/GloveBox" : null;
            if (path == null) return false;
            var node = vehicle.transform.Find(path);
            var glovebox = node == null ? null : node.GetComponent<Vehicle_Trunk>();
            return TryAddTrunkBody(vehicle, glovebox, vehicle.vehicleRigidbody, allowed);
        }
    }
}
