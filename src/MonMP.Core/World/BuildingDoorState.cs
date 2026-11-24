using System;
using System.IO;

namespace MonMP.Core.World
{
    public readonly struct BuildingDoorState : IEquatable<BuildingDoorState>
    {
        public const ushort Adapter = 15;
        public readonly bool Open, Locked;
        public BuildingDoorState(bool open, bool locked) { Open = open; Locked = locked; }
        public byte[] Encode() => new[] { (byte)((Open ? 1 : 0) | (Locked ? 2 : 0)) };
        public static BuildingDoorState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 1 || payload[0] > 3) throw new InvalidDataException("Invalid building door payload.");
            return new BuildingDoorState((payload[0] & 1) != 0, (payload[0] & 2) != 0);
        }
        public bool Equals(BuildingDoorState other) => Open == other.Open && Locked == other.Locked;
        public override bool Equals(object? obj) => obj is BuildingDoorState other && Equals(other);
        public override int GetHashCode() => (Open ? 1 : 0) | (Locked ? 2 : 0);
    }

    // Host reply when a guest's toggle is refused by a native lock or progression rule.
    public static class BuildingDoorReply
    {
        public const byte Locked = 1;
        public static byte[] Encode() => new[] { Locked };
        public static void Validate(byte[] payload)
        {
            if (payload == null || payload.Length != 1 || payload[0] != Locked) throw new InvalidDataException("Invalid building door reply.");
        }
    }

    // Guests send the open state they observed so a toggle based on stale presentation is rejected.
    public readonly struct BuildingDoorCommand
    {
        public readonly bool ObservedOpen;
        public readonly byte Handle;
        public BuildingDoorCommand(bool observedOpen, byte handle) { ObservedOpen = observedOpen; Handle = handle; }
        public byte[] Encode() => new[] { (byte)(ObservedOpen ? 1 : 0), Handle };
        public static BuildingDoorCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 2 || payload[0] > 1) throw new InvalidDataException("Invalid building door command.");
            return new BuildingDoorCommand(payload[0] == 1, payload[1]);
        }
    }
}
