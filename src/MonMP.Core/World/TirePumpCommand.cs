using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public enum TirePumpKind : byte { Manual = 1, Compressor = 2 }
    public enum TirePumpAction : byte { Inflate = 1, Deflate = 2 }

    // A click pulse carries the exact tool lease, wheel socket identity and camera pose.
    // It deliberately contains no requested pressure or tire-pop result.
    public readonly struct TirePumpCommand
    {
        public const byte Marker = 0xEC;
        public const int Size = 60;
        public readonly bool IndependentAim;
        public Guid Operation { get; }
        public ulong Tool { get; }
        public uint Generation { get; }
        public TirePumpKind Kind { get; }
        public TirePumpAction Action { get; }
        public ulong Wheel { get; }
        public float CameraX { get; }
        public float CameraY { get; }
        public float CameraZ { get; }
        public float CameraYaw { get; }
        public float CameraPitch { get; }

        public TirePumpCommand(Guid operation, ulong tool, uint generation, TirePumpKind kind,
            TirePumpAction action, ulong wheel, float cameraX, float cameraY, float cameraZ,
            float cameraYaw, float cameraPitch, bool independentAim = false)
        {
            Operation = operation; Tool = tool; Generation = generation; Kind = kind; Action = action; Wheel = wheel;
            CameraX = cameraX; CameraY = cameraY; CameraZ = cameraZ; CameraYaw = cameraYaw; CameraPitch = cameraPitch;
            IndependentAim = independentAim;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write(Tool); writer.Write(Generation);
            writer.Write((byte)Kind); writer.Write((byte)Action); writer.Write(Wheel);
            writer.Write(CameraX); writer.Write(CameraY); writer.Write(CameraZ); writer.Write(CameraYaw); writer.Write(CameraPitch);
            writer.Write(IndependentAim);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static TirePumpCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid tire pump command size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            reader.ReadByte();
            var command = new TirePumpCommand(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), reader.ReadUInt32(),
                (TirePumpKind)reader.ReadByte(), (TirePumpAction)reader.ReadByte(), reader.ReadUInt64(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing tire pump command data.");
            return command;
        }

        public bool CameraFits(PlayerPose player, AimSegmentTest? clear = null) =>
            ItemInteractionPosePolicy.Allows(player) &&
                ToolAimPolicy.Fits(player, IndependentAim, CameraX, CameraY, CameraZ, CameraYaw, CameraPitch, clear, allowCreeper: true, allowChair: true);

        private void Validate()
        {
            if (Operation == Guid.Empty || Tool == 0 || Generation == 0 || Wheel == 0 ||
                (Kind != TirePumpKind.Manual && Kind != TirePumpKind.Compressor) ||
                (Action != TirePumpAction.Inflate && Action != TirePumpAction.Deflate))
                throw new InvalidDataException("Invalid tire pump identity or action.");
            if (!Finite(CameraX) || !Finite(CameraY) || !Finite(CameraZ) ||
                Math.Abs(CameraX) > 100000 || Math.Abs(CameraY) > 100000 || Math.Abs(CameraZ) > 100000 ||
                !Finite(CameraYaw) || CameraYaw < 0 || CameraYaw > 360 ||
                !Finite(CameraPitch) || CameraPitch < -90 || CameraPitch > 90)
                throw new InvalidDataException("Invalid tire pump camera pose.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public static class TirePumpPolicy
    {
        public const float MaxTargetRange = 2.4f;
        public const float ManualInflateInterval = 0.65f;
        public const float ManualDeflateInterval = 0.13f;
        public const float CompressorInflateInterval = 0.25f;
        public const float CompressorDeflateInterval = 0.07f;
        public const float FreshPoseSeconds = 2f;

        public static float PulseInterval(TirePumpKind kind, TirePumpAction action) =>
            kind == TirePumpKind.Manual
                ? action == TirePumpAction.Inflate ? ManualInflateInterval : ManualDeflateInterval
                : action == TirePumpAction.Inflate ? CompressorInflateInterval : CompressorDeflateInterval;

        public static bool CanExecuteHostRequest(bool worldSynchronized, bool actionsBlocked, bool requestBudget,
            bool exactToolLease, bool freshWalkingPose, bool cameraFitsPose, bool targetRegisteredAndActive,
            bool targetIsMountedOnSharedFleet, bool hostRayHitsRequestedWheel, TirePumpKind kind,
            TirePumpAction action, bool wheelHasTire, float wheelPressure, float compressorPressure) =>
            worldSynchronized && !actionsBlocked && requestBudget && exactToolLease && freshWalkingPose && cameraFitsPose &&
            targetRegisteredAndActive && targetIsMountedOnSharedFleet && hostRayHitsRequestedWheel && wheelHasTire &&
            (action == TirePumpAction.Inflate
                ? Finite(wheelPressure) && wheelPressure < 100f &&
                    (kind == TirePumpKind.Manual || (Finite(compressorPressure) && compressorPressure > wheelPressure))
                : Finite(wheelPressure) && wheelPressure > -100000f);

        public static bool CanInflate(float pressure, bool hasTire, TirePumpKind kind, float compressorPressure) =>
            Finite(pressure) && hasTire && pressure < 100f &&
            (kind == TirePumpKind.Manual || (Finite(compressorPressure) && compressorPressure > pressure));

        public static bool CanDeflate(float pressure, bool hasTire) => Finite(pressure) && hasTire && pressure > -100000f;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    // The host owns the pulse clock; requests cannot accelerate the native pump cadence.
    public sealed class TirePumpPulseClock
    {
        private const int Capacity = 4096;
        private readonly System.Collections.Generic.Dictionary<(uint Peer, ulong Tool), (uint Generation, TirePumpKind Kind,
            TirePumpAction Action, float Time)> latest =
            new System.Collections.Generic.Dictionary<(uint, ulong), (uint, TirePumpKind, TirePumpAction, float)>();

        public bool TryRecord(uint peer, ulong tool, uint generation, TirePumpKind kind,
            TirePumpAction action, float now)
        {
            if (peer == 0 || tool == 0 || generation == 0 || !Enum.IsDefined(typeof(TirePumpKind), kind) ||
                !Enum.IsDefined(typeof(TirePumpAction), action) || !Finite(now) || now < 0) return false;
            var key = (peer, tool);
            if (latest.TryGetValue(key, out var previous) && previous.Generation == generation &&
                now < previous.Time + TirePumpPolicy.PulseInterval(previous.Kind, previous.Action))
                return false;
            if (!latest.ContainsKey(key) && latest.Count >= Capacity) return false;
            latest[key] = (generation, kind, action, now);
            return true;
        }

        public void RemovePeer(uint peer)
        {
            foreach (var key in new System.Collections.Generic.List<(uint Peer, ulong Tool)>(latest.Keys))
                if (key.Peer == peer) latest.Remove(key);
        }

        public int Count => latest.Count;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
