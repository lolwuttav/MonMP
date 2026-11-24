using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public enum BoltToolAction : byte { Begin = 1, KeepAlive = 2, End = 3 }
    public enum BoltToolKind : byte { AdjustableSpanner = 1, AirImpact = 2 }
    public enum BoltToolDirection : byte { Tighten = 1, Loosen = 2 }

    // A leased tool reports only input intent, never a client-selected bolt state or progress.
    // The host resolves Bolt against its own native ray and advances that hole over time.
    public readonly struct BoltToolCommand
    {
        public const byte Marker = 0xEB;
        public const int Size = 65;
        public readonly bool IndependentAim;
        public readonly BoltToolAction Action;
        public readonly Guid Operation;
        public readonly uint Sequence;
        public readonly ulong Tool;
        public readonly uint Generation;
        public readonly ulong Bolt;
        public readonly BoltToolKind ToolKind;
        public readonly BoltToolDirection Direction;
        public readonly float CameraX, CameraY, CameraZ, CameraYaw, CameraPitch;

        public BoltToolCommand(BoltToolAction action, Guid operation, uint sequence, ulong tool, uint generation,
            ulong bolt, BoltToolKind toolKind, BoltToolDirection direction,
            float cameraX, float cameraY, float cameraZ, float cameraYaw, float cameraPitch, bool independentAim = false)
        {
            Action = action; Operation = operation; Sequence = sequence; Tool = tool; Generation = generation;
            Bolt = bolt; ToolKind = toolKind; Direction = direction;
            CameraX = cameraX; CameraY = cameraY; CameraZ = cameraZ; CameraYaw = cameraYaw; CameraPitch = cameraPitch;
            IndependentAim = independentAim;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write((byte)Action); writer.Write(Operation.ToByteArray()); writer.Write(Sequence);
            writer.Write(Tool); writer.Write(Generation); writer.Write(Bolt); writer.Write((byte)ToolKind); writer.Write((byte)Direction);
            writer.Write(CameraX); writer.Write(CameraY); writer.Write(CameraZ); writer.Write(CameraYaw); writer.Write(CameraPitch);
            writer.Write(IndependentAim);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static BoltToolCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid bolt tool command size or marker.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            _ = reader.ReadByte();
            var command = new BoltToolCommand((BoltToolAction)reader.ReadByte(), new Guid(reader.ReadBytes(16)),
                reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt64(),
                (BoltToolKind)reader.ReadByte(), (BoltToolDirection)reader.ReadByte(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing bolt tool command data.");
            return command;
        }

        public bool CameraFits(PlayerPose player, AimSegmentTest? clear = null) =>
            ItemInteractionPosePolicy.Allows(player) &&
                ToolAimPolicy.Fits(player, IndependentAim, CameraX, CameraY, CameraZ, CameraYaw, CameraPitch, clear, allowCreeper: true, allowChair: true);

        private void Validate()
        {
            // Scene-mounted impact guns use stable static entity IDs. Only that tool kind may
            // name a static ID; the host still resolves it against its exact impactTools map.
            // Spanners remain ordinary leased dynamic items.
            bool toolIdentityValid = ToolKind == BoltToolKind.AirImpact
                ? Tool != 0 && Tool != WorldRecord.DynamicIdStart
                : Tool > WorldRecord.DynamicIdStart;
            if (!Enum.IsDefined(typeof(BoltToolAction), Action) || Operation == Guid.Empty || Sequence == 0 ||
                !toolIdentityValid || Generation == 0 || Bolt == 0 ||
                !Enum.IsDefined(typeof(BoltToolKind), ToolKind) || !Enum.IsDefined(typeof(BoltToolDirection), Direction))
                throw new InvalidDataException("Invalid bolt tool identity or action.");
            if ((Action == BoltToolAction.Begin) != (Sequence == 1))
                throw new InvalidDataException("Invalid bolt tool sequence.");
            if (!Finite(CameraX) || !Finite(CameraY) || !Finite(CameraZ) ||
                Math.Abs(CameraX) > 100000 || Math.Abs(CameraY) > 100000 || Math.Abs(CameraZ) > 100000 ||
                !Finite(CameraYaw) || CameraYaw < 0 || CameraYaw > 360 ||
                !Finite(CameraPitch) || CameraPitch < -90 || CameraPitch > 90)
                throw new InvalidDataException("Invalid bolt tool camera pose.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public static class BoltToolPolicy
    {
        public static bool CanOperate(byte playerState) => ItemInteractionPosePolicy.AllowsState(playerState);

        public static bool CanBegin(bool worldSynchronized, bool actionsBlocked, bool requestBudget,
            bool exactToolLease, bool toolInRange, bool freshWorkingPose, bool cameraFitsPose,
            bool targetRegisteredAndActive, bool targetInRange, bool hostRayHitsRequestedBolt,
            bool boltInstalled, bool atRequestedLimit, bool compressorReady) =>
            worldSynchronized && !actionsBlocked && requestBudget && exactToolLease && toolInRange &&
            freshWorkingPose && cameraFitsPose && targetRegisteredAndActive && targetInRange &&
            hostRayHitsRequestedBolt && boltInstalled && !atRequestedLimit && compressorReady;

        public static float Advance(float percent, BoltToolKind tool, BoltToolDirection direction,
            float elapsedSeconds, bool compressorReady)
        {
            if (!Finite(percent) || percent < 0 || percent > 1 || !Finite(elapsedSeconds) || elapsedSeconds < 0 ||
                !Enum.IsDefined(typeof(BoltToolKind), tool) || !Enum.IsDefined(typeof(BoltToolDirection), direction))
                throw new ArgumentOutOfRangeException(nameof(percent), "Invalid bolt progress input.");
            if (tool == BoltToolKind.AirImpact && !compressorReady) return percent;
            float secondsToFull = tool == BoltToolKind.AirImpact ? 0.4f : 3f;
            float delta = elapsedSeconds / secondsToFull;
            return direction == BoltToolDirection.Tighten
                ? Math.Min(1, percent + delta)
                : Math.Max(0, percent - delta);
        }

        public static bool AtLimit(float percent, BoltToolDirection direction) =>
            direction == BoltToolDirection.Tighten ? percent >= 1 : percent <= 0;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
