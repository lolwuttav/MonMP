using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public enum WoodenKegUseStatus : byte
    {
        Accepted = 1,
        Unavailable = 2,
        HandsBusy = 3,
        NotMature = 4,
        TooFar = 5,
        InvalidTarget = 6,
        Capacity = 7,
        DeliveryFailed = 8,
        Uncertain = 9,
        NativeRejected = 10
    }

    // A stable command endpoint keeps keg extraction separate from SharedItems' own command
    // payloads. Per-keg syrup and fermentation values remain in the existing ItemState records.
    public static class WoodenKegUseState
    {
        public const ushort Adapter = 75;
        public static readonly ulong Entity = StaticId.Hash("monmp/wooden-keg-use/v1");
        public static byte[] InitialPayload() => new byte[] { 1 };
        public static bool ValidPayload(byte[] payload) => payload != null && payload.Length == 1 && payload[0] == 1;
    }

    // The request identifies the exact shared keg and carries a pose-bounded camera ray. It never
    // asks the host to consume syrup or set fermentation progress supplied by a client.
    public readonly struct WoodenKegUseRequest
    {
        public const byte Marker = 0xA8;
        public const byte Version = 1;
        public const int Size = 47;
        public readonly bool IndependentAim;
        public Guid Operation { get; }
        public ulong Keg { get; }
        public float CameraX { get; }
        public float CameraY { get; }
        public float CameraZ { get; }
        public float CameraYaw { get; }
        public float CameraPitch { get; }

        public WoodenKegUseRequest(Guid operation, ulong keg, float cameraX, float cameraY, float cameraZ,
            float cameraYaw, float cameraPitch, bool independentAim = false)
        {
            Operation = operation; Keg = keg; CameraX = cameraX; CameraY = cameraY; CameraZ = cameraZ;
            CameraYaw = cameraYaw; CameraPitch = cameraPitch;
            IndependentAim = independentAim;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Version); writer.Write(Operation.ToByteArray()); writer.Write(Keg);
            writer.Write(CameraX); writer.Write(CameraY); writer.Write(CameraZ); writer.Write(CameraYaw); writer.Write(CameraPitch);
            writer.Write(IndependentAim);
            return stream.ToArray();
        }

        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static WoodenKegUseRequest Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid wooden-keg request length.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            if (reader.ReadByte() != Marker || reader.ReadByte() != Version)
                throw new InvalidDataException("Invalid wooden-keg request marker or version.");
            var request = new WoodenKegUseRequest(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing wooden-keg request data.");
            return request;
        }

        public bool CameraFits(PlayerPose pose, AimSegmentTest? clear = null) =>
            ToolAimPolicy.Fits(pose, IndependentAim, CameraX, CameraY, CameraZ, CameraYaw, CameraPitch, clear);

        private void Validate()
        {
            if (Operation == Guid.Empty || Keg == 0 || !Bounded(CameraX) || !Bounded(CameraY) || !Bounded(CameraZ) ||
                !Finite(CameraYaw) || CameraYaw < 0f || CameraYaw > 360f ||
                !Finite(CameraPitch) || CameraPitch < -90f || CameraPitch > 90f)
                throw new InvalidDataException("Invalid wooden-keg request.");
        }

        private static bool Bounded(float value) => Finite(value) && Math.Abs(value) <= 100000f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public readonly struct WoodenKegUseReply
    {
        public const byte Marker = 0xA9;
        public const byte Version = 1;
        public const int Size = 27;
        public Guid Operation { get; }
        public WoodenKegUseStatus Status { get; }
        public ulong WinePack { get; }

        public WoodenKegUseReply(Guid operation, WoodenKegUseStatus status, ulong winePack = 0)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(WoodenKegUseStatus), status) ||
                (status == WoodenKegUseStatus.Accepted ? winePack <= WorldRecord.DynamicIdStart : winePack != 0))
                throw new InvalidDataException("Invalid wooden-keg reply.");
            Operation = operation; Status = status; WinePack = winePack;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Version); writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status); writer.Write(WinePack);
            return stream.ToArray();
        }

        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static WoodenKegUseReply Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid wooden-keg reply length.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            if (reader.ReadByte() != Marker || reader.ReadByte() != Version)
                throw new InvalidDataException("Invalid wooden-keg reply marker or version.");
            Guid operation = new Guid(reader.ReadBytes(16));
            byte status = reader.ReadByte();
            if (!Enum.IsDefined(typeof(WoodenKegUseStatus), status)) throw new InvalidDataException("Unknown wooden-keg reply status.");
            var reply = new WoodenKegUseReply(operation, (WoodenKegUseStatus)status, reader.ReadUInt64());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing wooden-keg reply data.");
            return reply;
        }
    }

    public static class WoodenKegUsePolicy
    {
        public const float MaxUseRange = 2.4f;
        public const float FreshPoseSeconds = 2f;

        public static WoodenKegUseStatus ValidateHostRequest(bool synchronized, bool actionsBlocked, bool budgetAvailable,
            bool peerKnown, bool exactTarget, bool exactKegType, bool itemUnowned, bool kegActive,
            bool readyForWine, bool freshWalkingPose, bool cameraFitsPose, bool hostRayHitsKeg,
            bool guestHandsFree)
        {
            if (!synchronized || actionsBlocked || !peerKnown) return WoodenKegUseStatus.Unavailable;
            if (!budgetAvailable) return WoodenKegUseStatus.Capacity;
            if (!exactTarget || !exactKegType || !itemUnowned || !kegActive) return WoodenKegUseStatus.InvalidTarget;
            if (!freshWalkingPose || !cameraFitsPose || !hostRayHitsKeg) return WoodenKegUseStatus.TooFar;
            if (!guestHandsFree) return WoodenKegUseStatus.HandsBusy;
            if (!readyForWine) return WoodenKegUseStatus.NotMature;
            return WoodenKegUseStatus.Accepted;
        }
    }
}
