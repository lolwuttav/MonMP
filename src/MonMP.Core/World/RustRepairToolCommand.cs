using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public readonly struct RustRepairToolCommand
    {
        public const byte Marker = 0xE8;
        public const int Size = 50;
        public readonly bool IndependentAim;

        public Guid Operation { get; }
        public ulong Tool { get; }
        public uint Generation { get; }
        public float CameraX { get; }
        public float CameraY { get; }
        public float CameraZ { get; }
        public float CameraYaw { get; }
        public float CameraPitch { get; }

        public RustRepairToolCommand(Guid operation, ulong tool, uint generation,
            float cameraX, float cameraY, float cameraZ, float cameraYaw, float cameraPitch, bool independentAim = false)
        {
            Operation = operation;
            Tool = tool;
            Generation = generation;
            CameraX = cameraX;
            CameraY = cameraY;
            CameraZ = cameraZ;
            CameraYaw = cameraYaw;
            CameraPitch = cameraPitch;
            IndependentAim = independentAim;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write(Tool);
            writer.Write(Generation);
            writer.Write(CameraX);
            writer.Write(CameraY);
            writer.Write(CameraZ);
            writer.Write(CameraYaw);
            writer.Write(CameraPitch);
            writer.Write(IndependentAim);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static RustRepairToolCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid rust repair command size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var command = new RustRepairToolCommand(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), reader.ReadUInt32(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing rust repair command data.");
            return command;
        }

        public bool CameraFits(PlayerPose player, AimSegmentTest? clear = null) =>
            ToolAimPolicy.Fits(player, IndependentAim, CameraX, CameraY, CameraZ, CameraYaw, CameraPitch, clear);

        private void Validate()
        {
            if (Operation == Guid.Empty || Tool <= WorldRecord.DynamicIdStart || Generation == 0)
                throw new InvalidDataException("Invalid rust repair tool identity.");
            if (!Finite(CameraX) || !Finite(CameraY) || !Finite(CameraZ) ||
                Math.Abs(CameraX) > 100000 || Math.Abs(CameraY) > 100000 || Math.Abs(CameraZ) > 100000 ||
                !Finite(CameraYaw) || CameraYaw < 0 || CameraYaw > 360 ||
                !Finite(CameraPitch) || CameraPitch < -90 || CameraPitch > 90)
                throw new InvalidDataException("Invalid rust repair camera pose.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public enum RustRepairResult : byte
    {
        Applied = 0,
        Rejected = 1,
        Duplicate = 2,
        Capacity = 3,
        NoChange = 4
    }

    public static class RustRepairPolicy
    {
        public static bool CanExecuteHostRequest(bool worldSynchronized, bool actionsBlocked, bool requestBudget,
            bool exactKitLease, bool kitInRange, bool freshWalkingPose, bool cameraFitsPose,
            bool targetRegisteredAndActive, bool panelInRange, bool rayHitsRequestedPanel, ulong requestedPanel) =>
            worldSynchronized && !actionsBlocked && requestBudget && exactKitLease && kitInRange && freshWalkingPose && cameraFitsPose &&
            targetRegisteredAndActive && panelInRange && rayHitsRequestedPanel && requestedPanel != 0;

        public static RustRepairResult Complete(bool authorized, bool mutationRan, bool nativeRepairChanged) =>
            !authorized || !mutationRan ? RustRepairResult.Rejected :
            nativeRepairChanged ? RustRepairResult.Applied : RustRepairResult.NoChange;

        public static RustRepairResult Finalize(bool authorized, bool mutationRan, bool nativeRepairChanged, bool kitConsumed)
        {
            var result = Complete(authorized, mutationRan, nativeRepairChanged);
            return result == RustRepairResult.Applied && !kitConsumed ? RustRepairResult.Rejected : result;
        }

        public static bool ConsumeKit(RustRepairResult result) => result == RustRepairResult.Applied;
    }
}
