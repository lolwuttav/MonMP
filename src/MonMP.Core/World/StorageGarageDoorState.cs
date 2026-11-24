using System;
using System.IO;

namespace MonMP.Core.World
{
    // One storage garage (EntreposageGarage) bay door. The host runs the native door, its safety
    // checks and the load/unload of the parked vehicle; guests mirror the door animation and hide or
    // show their copy of the vehicle. StoredVehicle names the shared vehicle entity parked behind the
    // closed door (0 when the vehicle is not a shared one: the guest then uses its own bay detection).
    public readonly struct StorageGarageDoorState : IEquatable<StorageGarageDoorState>
    {
        public const ushort Adapter = 92;
        public const byte Version = 1;
        public const int Size = 11;
        public readonly bool Open;     // open, or moving towards open
        public readonly bool Stored;   // a vehicle is unloaded (inactive) in this bay
        public readonly ulong StoredVehicle;

        public StorageGarageDoorState(bool open, bool stored, ulong storedVehicle)
        {
            Open = open; Stored = stored; StoredVehicle = storedVehicle;
            Validate();
        }
        private void Validate()
        {
            if (!Stored && StoredVehicle != 0) throw new InvalidDataException("Invalid storage garage door state.");
        }
        public static ulong EntityFor(string identity) => StaticId.Hash("storage-garage/door/v1/" + identity) | WorldRecord.DynamicIdStart;
        public byte[] Encode()
        {
            Validate();
            var payload = new byte[Size];
            payload[0] = Version; payload[1] = (byte)(Open ? 1 : 0); payload[2] = (byte)(Stored ? 1 : 0);
            BitConverter.GetBytes(StoredVehicle).CopyTo(payload, 3);
            return payload;
        }
        public static StorageGarageDoorState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || payload[0] != Version || payload[1] > 1 || payload[2] > 1)
                throw new InvalidDataException("Invalid storage garage door state.");
            return new StorageGarageDoorState(payload[1] == 1, payload[2] == 1, BitConverter.ToUInt64(payload, 3));
        }
        public bool Equals(StorageGarageDoorState other) => Open == other.Open && Stored == other.Stored && StoredVehicle == other.StoredVehicle;
        public override bool Equals(object? obj) => obj is StorageGarageDoorState other && Equals(other);
        public override int GetHashCode() => (Open ? 1 : 0) | (Stored ? 2 : 0) ^ StoredVehicle.GetHashCode() << 2;
    }

    // A guest pressing a storage garage door button, addressed to the door's entity.
    public static class StorageGarageDoorCommand
    {
        public const byte Marker = 0xC9;
        public static byte[] Encode() => new[] { Marker, (byte)1 };
        public static bool Is(byte[]? payload) => payload != null && payload.Length == 2 && payload[0] == Marker && payload[1] == 1;
    }
}
