using System;
using System.Collections.Generic;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Purchased vehicles do not have a stable scene path. Their sale entity is the root of
    // every socket and bolt identity, and the remaining path is relative to that vehicle.
    // Keep this separate from the historic static fleet policy so its IDs never move.
    internal static class PurchasedVehiclePartIdentityPolicy
    {
        internal const string PartIdentityPrefix = "purchased-vehicle-part-v1/";
        internal const string BoltIdentityPrefix = "purchased-vehicle-bolt-v1/";

        internal static bool Supports(Vehicle_Type type) =>
            type == Vehicle_Type.OlTruck || type == Vehicle_Type.SmollATV;

        // The current ATV source confirms its DogBed socket. Do not infer a complete ATV shell
        // catalog from the broader Vehicle_PartsManager type.
        internal static bool IncludesSocket(Vehicle_Type type, Vehicle_PartSocket? socket) =>
            socket != null && socket.gameobject != null &&
            (type == Vehicle_Type.OlTruck || type == Vehicle_Type.SmollATV && socket.car_PartsType == Car_PartsType.DogBed);

        internal static bool TryRelativePath(Transform vehicleRoot, Transform target, out string path)
        {
            path = "";
            if (vehicleRoot == null || target == null) return false;
            var segments = new Stack<string>();
            Transform? current = target;
            while (current != null && current != vehicleRoot)
            {
                string name = current.name.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
                int ordinal = 0;
                Transform? parent = current.parent;
                if (parent == null) return false;
                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform sibling = parent.GetChild(i);
                    if (sibling == current) break;
                    if (sibling.name == current.name) ordinal++;
                }
                segments.Push(StaticId.Segment(name, ordinal));
                current = parent;
            }
            if (current != vehicleRoot || segments.Count == 0) return false;
            path = string.Join("/", segments);
            return path.Length != 0;
        }

        internal static string PartSocketIdentity(ulong vehicleEntity, Vehicle_Type type, string relativeSocketPath)
        {
            Validate(vehicleEntity, type, relativeSocketPath);
            return PartIdentityPrefix + vehicleEntity.ToString("X16") + "/" + (int)type + "/socket/" + relativeSocketPath;
        }

        internal static ulong PartSocketEntityId(ulong vehicleEntity, Vehicle_Type type, string relativeSocketPath) =>
            DynamicId(PartSocketIdentity(vehicleEntity, type, relativeSocketPath));

        internal static string BoltHoleIdentity(ulong vehicleEntity, Vehicle_Type type, string relativeSocketPath, int holeIndex)
        {
            Validate(vehicleEntity, type, relativeSocketPath);
            if (holeIndex < 0) throw new ArgumentOutOfRangeException(nameof(holeIndex));
            return BoltIdentityPrefix + vehicleEntity.ToString("X16") + "/" + (int)type + "/socket/" +
                relativeSocketPath + "/hole/" + holeIndex;
        }

        internal static ulong BoltHoleEntityId(ulong vehicleEntity, Vehicle_Type type, string relativeSocketPath, int holeIndex) =>
            DynamicId(BoltHoleIdentity(vehicleEntity, type, relativeSocketPath, holeIndex));

        internal static bool IsPurchasedIdentity(string? identity) => identity != null &&
            (identity.StartsWith(PartIdentityPrefix, StringComparison.Ordinal) ||
             identity.StartsWith(BoltIdentityPrefix, StringComparison.Ordinal));

        internal static bool IsPurchasedEntityId(ulong id, string? identity) =>
            id > WorldRecord.DynamicIdStart && IsPurchasedIdentity(identity);

        private static ulong DynamicId(string identity)
        {
            // Keep the high-bit namespace used by WorldChannel.Register while avoiding a zero
            // low portion. The sale entity and relative path make each vehicle copy independent.
            ulong low = StaticId.Hash(identity) & 0x7FFFFFFFFFFFFFFFUL;
            return WorldRecord.DynamicIdStart | Math.Max(1UL, low);
        }

        private static void Validate(ulong vehicleEntity, Vehicle_Type type, string relativeSocketPath)
        {
            if (vehicleEntity <= WorldRecord.DynamicIdStart) throw new ArgumentOutOfRangeException(nameof(vehicleEntity));
            if (!Supports(type)) throw new ArgumentOutOfRangeException(nameof(type), "Only the source-confirmed purchased truck and ATV are catalogued.");
            if (string.IsNullOrWhiteSpace(relativeSocketPath) || relativeSocketPath.Length > 400)
                throw new ArgumentException("A bounded relative socket path is required.", nameof(relativeSocketPath));
        }
    }
}
