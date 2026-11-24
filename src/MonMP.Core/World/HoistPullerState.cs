using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public enum HoistPullerPhase : byte
    {
        StoredOrHeld = 0,
        GroundPlaced = 1,
        AttachedToVehicle = 2
    }

    // Host-owned state for the native hoist. Anchor inputs and cable ranges let the
    // vehicle's current physics owner apply the pull without accepting a requested force or pose.
    public readonly struct HoistPullerState
    {
        public const ushort Adapter = 66;
        public const byte Marker = 0x48;
        public const byte Version = 2;
        public const int Size = 63;

        public HoistPullerPhase Phase { get; }
        public float GroundX { get; }
        public float GroundY { get; }
        public float GroundZ { get; }
        public ulong Vehicle { get; }
        public float AnchorX { get; }
        public float AnchorY { get; }
        public float AnchorZ { get; }
        public float HandX { get; }
        public float HandY { get; }
        public float HandZ { get; }
        public float CrankTimer { get; }
        public uint CrankSequence { get; }
        public float MinRange { get; }
        public float MaxRange { get; }

        public HoistPullerState(HoistPullerPhase phase, float groundX, float groundY, float groundZ,
            ulong vehicle, float anchorX, float anchorY, float anchorZ, float handX, float handY, float handZ,
            float crankTimer, uint crankSequence, float minRange = 0f, float maxRange = 0f)
        {
            Phase = phase;
            GroundX = groundX; GroundY = groundY; GroundZ = groundZ; Vehicle = vehicle;
            AnchorX = anchorX; AnchorY = anchorY; AnchorZ = anchorZ;
            HandX = handX; HandY = handY; HandZ = handZ;
            CrankTimer = crankTimer; CrankSequence = crankSequence;
            MinRange = minRange; MaxRange = maxRange;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Version); writer.Write((byte)Phase);
            writer.Write(GroundX); writer.Write(GroundY); writer.Write(GroundZ); writer.Write(Vehicle);
            writer.Write(AnchorX); writer.Write(AnchorY); writer.Write(AnchorZ);
            writer.Write(HandX); writer.Write(HandY); writer.Write(HandZ);
            writer.Write(CrankTimer); writer.Write(CrankSequence);
            writer.Write(MinRange); writer.Write(MaxRange);
            return stream.ToArray();
        }

        public static HoistPullerState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || payload[0] != Marker || payload[1] != Version)
                throw new InvalidDataException("Invalid hoist puller payload size or version.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            reader.ReadByte(); reader.ReadByte();
            var state = new HoistPullerState((HoistPullerPhase)reader.ReadByte(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadUInt64(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadUInt32(), reader.ReadSingle(), reader.ReadSingle());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing hoist puller payload data.");
            return state;
        }

        private void Validate()
        {
            if (!Enum.IsDefined(typeof(HoistPullerPhase), Phase) ||
                !Position(GroundX, GroundY, GroundZ) || !Position(AnchorX, AnchorY, AnchorZ) ||
                !Position(HandX, HandY, HandZ) || !Finite(CrankTimer) || CrankTimer < 0f || CrankTimer > 1.1f ||
                !Finite(MinRange) || !Finite(MaxRange) || MinRange < 0f || MaxRange < MinRange || MaxRange > 100000f)
                throw new InvalidDataException("Invalid hoist puller state.");

            bool groundIsZero = GroundX == 0f && GroundY == 0f && GroundZ == 0f;
            bool anchorIsZero = AnchorX == 0f && AnchorY == 0f && AnchorZ == 0f;
            bool handIsZero = HandX == 0f && HandY == 0f && HandZ == 0f;
            if (Phase == HoistPullerPhase.StoredOrHeld && (Vehicle != 0 || !groundIsZero || !anchorIsZero || !handIsZero) ||
                Phase == HoistPullerPhase.GroundPlaced && (Vehicle != 0 || !anchorIsZero) ||
                Phase == HoistPullerPhase.AttachedToVehicle && (Vehicle == 0 || !handIsZero) ||
                Phase != HoistPullerPhase.AttachedToVehicle && (MinRange != 0f || MaxRange != 0f))
                throw new InvalidDataException("Inconsistent hoist puller phase data.");
        }

        private static bool Position(float x, float y, float z) => Bounded(x) && Bounded(y) && Bounded(z);
        private static bool Bounded(float value) => Finite(value) && Math.Abs(value) <= 100000f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public enum HoistPullerCommandKind : byte { Crank = 1 }

    public readonly struct HoistPullerCommand
    {
        public const byte Marker = 0x49;
        public const int Size = 23;
        public readonly bool IndependentAim;
        public HoistPullerCommandKind Kind { get; }
        public float CameraX { get; }
        public float CameraY { get; }
        public float CameraZ { get; }
        public float CameraYaw { get; }
        public float CameraPitch { get; }

        public HoistPullerCommand(HoistPullerCommandKind kind, float cameraX, float cameraY, float cameraZ,
            float cameraYaw, float cameraPitch, bool independentAim = false)
        {
            if (kind != HoistPullerCommandKind.Crank) throw new InvalidDataException("Unsupported hoist command.");
            Kind = kind; CameraX = cameraX; CameraY = cameraY; CameraZ = cameraZ;
            CameraYaw = cameraYaw; CameraPitch = cameraPitch;
            IndependentAim = independentAim;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write((byte)Kind);
            writer.Write(CameraX); writer.Write(CameraY); writer.Write(CameraZ); writer.Write(CameraYaw); writer.Write(CameraPitch);
            writer.Write(IndependentAim);
            return stream.ToArray();
        }

        public static HoistPullerCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || payload[0] != Marker)
                throw new InvalidDataException("Invalid hoist puller command.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            reader.ReadByte();
            var command = new HoistPullerCommand((HoistPullerCommandKind)reader.ReadByte(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing hoist puller command data.");
            return command;
        }

        public bool CameraFits(PlayerPose player, AimSegmentTest? clear = null) =>
            ToolAimPolicy.Fits(player, IndependentAim, CameraX, CameraY, CameraZ, CameraYaw, CameraPitch, clear);

        private void Validate()
        {
            if (Kind != HoistPullerCommandKind.Crank || !Bounded(CameraX) || !Bounded(CameraY) || !Bounded(CameraZ) ||
                !Finite(CameraYaw) || CameraYaw < 0 || CameraYaw > 360 ||
                !Finite(CameraPitch) || CameraPitch < -90 || CameraPitch > 90)
                throw new InvalidDataException("Invalid hoist puller command.");
        }
        private static bool Bounded(float value) => Finite(value) && Math.Abs(value) <= 100000f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public static class HoistPullerAuthorityPolicy
    {
        public const float InteractionRange = 2.4f;
        public const float FreshPoseSeconds = 2f;

        public static bool CanAcceptCrank(uint peer, bool host, bool worldSynchronized, bool actionsBlocked,
            bool freshPose, bool walking, bool cameraFitsPose, bool handleActive, bool attachedToVehicle,
            bool hostRayHitsHandle, bool withinRateLimit) =>
            peer != 0 && host && worldSynchronized && !actionsBlocked && freshPose && walking &&
            cameraFitsPose && handleActive && attachedToVehicle && hostRayHitsHandle && withinRateLimit;
    }

    public enum HoistPullerPlacementKind : byte { Claim = 1, Release = 2, Attach = 3, Recover = 4 }
    public enum HoistPullerPlacementStatus : byte { Granted = 1, Busy = 2, Rejected = 3 }

    // A guest's use of the hoist (#116): it claims the tool from its case, aims the ground and
    // vehicle anchors with the native tool, then asks the host to attach there. The host
    // owns attachment and shortening while the vehicle's physics owner applies the pull.
    // Recover detaches an attached hoist and grants the requester a pickup claim.
    public readonly struct HoistPullerPlacement
    {
        public const byte Marker = 0x4A;
        public const byte ReplyMarker = 0x4B;
        public const int Size = 34;
        public const int SequencedSize = Size + 4;
        public HoistPullerPlacementKind Kind { get; }
        public uint Operation { get; }
        public float GroundX { get; }
        public float GroundY { get; }
        public float GroundZ { get; }
        public ulong Vehicle { get; }
        public float AnchorX { get; }
        public float AnchorY { get; }
        public float AnchorZ { get; }

        public HoistPullerPlacement(HoistPullerPlacementKind kind, float groundX = 0, float groundY = 0, float groundZ = 0,
            ulong vehicle = 0, float anchorX = 0, float anchorY = 0, float anchorZ = 0, uint operation = 0)
        {
            Kind = kind; GroundX = groundX; GroundY = groundY; GroundZ = groundZ;
            Vehicle = vehicle; AnchorX = anchorX; AnchorY = anchorY; AnchorZ = anchorZ;
            Operation = operation;
            Validate();
        }

        public HoistPullerPlacement WithOperation(uint operation) =>
            new HoistPullerPlacement(Kind, GroundX, GroundY, GroundZ, Vehicle, AnchorX, AnchorY, AnchorZ, operation);

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write((byte)Kind);
            writer.Write(GroundX); writer.Write(GroundY); writer.Write(GroundZ);
            writer.Write(Vehicle);
            writer.Write(AnchorX); writer.Write(AnchorY); writer.Write(AnchorZ);
            if (Operation != 0) writer.Write(Operation);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null &&
            (payload.Length == Size || payload.Length == SequencedSize) && payload[0] == Marker;

        public static HoistPullerPlacement Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid hoist placement.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            reader.ReadByte();
            var kind = (HoistPullerPlacementKind)reader.ReadByte();
            float gx = reader.ReadSingle(), gy = reader.ReadSingle(), gz = reader.ReadSingle();
            ulong vehicle = reader.ReadUInt64();
            float ax = reader.ReadSingle(), ay = reader.ReadSingle(), az = reader.ReadSingle();
            uint operation = payload.Length == SequencedSize ? reader.ReadUInt32() : 0;
            if (payload.Length == SequencedSize && operation == 0) throw new InvalidDataException("Invalid hoist operation.");
            return new HoistPullerPlacement(kind, gx, gy, gz, vehicle, ax, ay, az, operation);
        }

        public static byte[] EncodeReply(HoistPullerPlacementKind kind, HoistPullerPlacementStatus status, uint operation = 0)
        {
            var payload = new byte[operation == 0 ? 3 : 7];
            payload[0] = ReplyMarker; payload[1] = (byte)kind; payload[2] = (byte)status;
            if (operation != 0) Buffer.BlockCopy(BitConverter.GetBytes(operation), 0, payload, 3, 4);
            return payload;
        }

        public static bool TryDecodeReply(byte[]? payload, out HoistPullerPlacementKind kind, out HoistPullerPlacementStatus status)
            => TryDecodeReply(payload, out kind, out status, out _);

        public static bool TryDecodeReply(byte[]? payload, out HoistPullerPlacementKind kind, out HoistPullerPlacementStatus status, out uint operation)
        {
            kind = default; status = default; operation = 0;
            if (payload == null || (payload.Length != 3 && payload.Length != 7) || payload[0] != ReplyMarker) return false;
            kind = (HoistPullerPlacementKind)payload[1]; status = (HoistPullerPlacementStatus)payload[2];
            if (payload.Length == 7) { operation = BitConverter.ToUInt32(payload, 3); if (operation == 0) return false; }
            return Enum.IsDefined(typeof(HoistPullerPlacementKind), kind) && Enum.IsDefined(typeof(HoistPullerPlacementStatus), status);
        }

        private void Validate()
        {
            if (!Enum.IsDefined(typeof(HoistPullerPlacementKind), Kind) || !Bounded(GroundX) || !Bounded(GroundY) || !Bounded(GroundZ) ||
                !Bounded(AnchorX) || !Bounded(AnchorY) || !Bounded(AnchorZ) || (Kind == HoistPullerPlacementKind.Attach) != (Vehicle != 0))
                throw new InvalidDataException("Invalid hoist placement.");
        }
        private static bool Bounded(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= 100000f;
    }

    /// <summary>A timed, correlated claim prevents an old reply from granting a later retry.</summary>
    public sealed class HoistClaimPending
    {
        public const double TimeoutSeconds = 5;
        private ulong entity;
        private uint operation;
        private double sentAt;
        public bool HasPending => operation != 0;
        public bool Begin(ulong entity, uint operation, double now)
        {
            if (entity == 0 || operation == 0 || double.IsNaN(now) || double.IsInfinity(now))
                throw new ArgumentOutOfRangeException(nameof(operation));
            if (HasPending) return false;
            this.entity = entity; this.operation = operation; sentAt = now;
            return true;
        }
        public bool Complete(ulong entity, uint operation)
        {
            if (!HasPending || this.entity != entity || this.operation != operation) return false;
            Clear(); return true;
        }
        public bool Expire(double now)
        {
            if (!HasPending || now >= sentAt && now - sentAt < TimeoutSeconds) return false;
            Clear(); return true;
        }
        public void Clear() { entity = 0; operation = 0; sentAt = 0; }
    }
}
