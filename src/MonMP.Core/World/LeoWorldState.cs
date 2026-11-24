using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // Adapter 38 is the single native Leo_Manager. This consumer mirrors compatible
    // on-foot roaming and following-player travel; item, bed, vehicle and friendship
    // actions remain host-only until their effects can be routed safely.
    public sealed class LeoWorldState
    {
        public const ushort Adapter = 38;
        public const int Size = 58;
        public const byte MaxNativeState = 14;
        public const byte MaxHome = 5;
        public const int MaxVehicleType = 16;
        public const float MaxWorldCoordinate = 1_000_000f;
        public const float MaxEulerAngle = 36_000f;
        // The native bowl subtracts twice the amount eaten without clamping, so a fed Leo's needs go below 0.
        public const float MaxNeed = 4f;
        public static readonly ulong Entity = StaticId.Hash("leo-manager/v1");

        public readonly bool IsTamed;
        public readonly byte NativeState;
        public readonly bool StayAtHome;
        public readonly byte AttachedHome;
        public readonly int InVehicleId;
        public readonly float X, Y, Z;
        public readonly float EulerX, EulerY, EulerZ;
        public readonly float Thirst, Hunger;
        public readonly uint TicksSinceThirsty, TicksSinceHungry;
        public readonly float AnimationForward, AnimationTurn;
        // The native animator's State/customValue pair (-1 when unset) for puppet presentation.
        public readonly sbyte AnimationState, AnimationCustom;

        public LeoWorldState(bool isTamed, byte nativeState, bool stayAtHome, byte attachedHome, int inVehicleId,
            float x, float y, float z, float eulerX, float eulerY, float eulerZ,
            float thirst, float hunger, uint ticksSinceThirsty, uint ticksSinceHungry,
            float animationForward = 0, float animationTurn = 0, sbyte animationState = -1, sbyte animationCustom = -1)
        {
            IsTamed = isTamed; NativeState = nativeState; StayAtHome = stayAtHome; AttachedHome = attachedHome; InVehicleId = inVehicleId;
            X = x; Y = y; Z = z; EulerX = eulerX; EulerY = eulerY; EulerZ = eulerZ;
            Thirst = thirst; Hunger = hunger; TicksSinceThirsty = ticksSinceThirsty; TicksSinceHungry = ticksSinceHungry;
            AnimationForward = animationForward; AnimationTurn = animationTurn;
            AnimationState = animationState; AnimationCustom = animationCustom;
            Validate();
        }

        private void Validate()
        {
            if (NativeState > MaxNativeState) throw Invalid(nameof(NativeState), NativeState);
            if (AttachedHome > MaxHome) throw Invalid(nameof(AttachedHome), AttachedHome);
            if (InVehicleId != -1 && (InVehicleId < 0 || InVehicleId > MaxVehicleType)) throw Invalid(nameof(InVehicleId), InVehicleId);
            if (!Finite(Thirst) || Math.Abs(Thirst) > MaxNeed) throw Invalid(nameof(Thirst), Thirst);
            if (!Finite(Hunger) || Math.Abs(Hunger) > MaxNeed) throw Invalid(nameof(Hunger), Hunger);
            if (TicksSinceThirsty > int.MaxValue) throw Invalid(nameof(TicksSinceThirsty), TicksSinceThirsty);
            if (TicksSinceHungry > int.MaxValue) throw Invalid(nameof(TicksSinceHungry), TicksSinceHungry);
            if (!Finite(AnimationForward) || AnimationForward < 0 || AnimationForward > 100) throw Invalid(nameof(AnimationForward), AnimationForward);
            if (!Finite(AnimationTurn) || AnimationTurn < -1 || AnimationTurn > 1) throw Invalid(nameof(AnimationTurn), AnimationTurn);
            if (!Coordinate(X)) throw Invalid(nameof(X), X);
            if (!Coordinate(Y)) throw Invalid(nameof(Y), Y);
            if (!Coordinate(Z)) throw Invalid(nameof(Z), Z);
            if (!Angle(EulerX)) throw Invalid(nameof(EulerX), EulerX);
            if (!Angle(EulerY)) throw Invalid(nameof(EulerY), EulerY);
            if (!Angle(EulerZ)) throw Invalid(nameof(EulerZ), EulerZ);
        }

        private static InvalidDataException Invalid(string field, object value) => new InvalidDataException(
            "Invalid shared Leo state: " + field + "=" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) + ".");

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Coordinate(float value) => Finite(value) && Math.Abs(value) <= MaxWorldCoordinate;
        private static bool Angle(float value) => Finite(value) && Math.Abs(value) <= MaxEulerAngle;

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write((byte)(IsTamed ? 1 : 0));
            writer.Write(NativeState);
            writer.Write((byte)(StayAtHome ? 1 : 0));
            writer.Write(AttachedHome);
            writer.Write(InVehicleId);
            writer.Write(X); writer.Write(Y); writer.Write(Z);
            writer.Write(EulerX); writer.Write(EulerY); writer.Write(EulerZ);
            writer.Write(Thirst); writer.Write(Hunger);
            writer.Write(TicksSinceThirsty); writer.Write(TicksSinceHungry);
            writer.Write(AnimationForward); writer.Write(AnimationTurn);
            writer.Write(AnimationState); writer.Write(AnimationCustom);
            return stream.ToArray();
        }

        public static LeoWorldState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid shared Leo payload length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            byte tamed = reader.ReadByte(); byte state = reader.ReadByte(); byte homeFlag = reader.ReadByte(); byte home = reader.ReadByte();
            if (tamed > 1 || homeFlag > 1) throw new InvalidDataException("Invalid shared Leo flags.");
            var result = new LeoWorldState(tamed == 1, state, homeFlag == 1, home, reader.ReadInt32(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadUInt32(), reader.ReadUInt32(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSByte(), reader.ReadSByte());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing shared Leo data.");
            return result;
        }

        public bool SameAs(LeoWorldState other) => other != null &&
            System.Linq.Enumerable.SequenceEqual(Encode(), other.Encode());
    }

    public enum LeoCommandAction : byte
    {
        GiveTreat = 1,
        Pet = 2,
        ToggleStayAtHome = 3,
        FollowPlayer = 4,
        UseCarBed = 5,
        UseBed = 6
    }

    // Commands are requests only. The host still must validate the actor's pose,
    // target identity, item lease/value, daily friendship flags and current Leo state.
    public readonly struct LeoWorldCommand
    {
        public const int Size = 25;
        public readonly Guid Operation;
        public readonly LeoCommandAction Action;
        public readonly ulong Target;

        public LeoWorldCommand(Guid operation, LeoCommandAction action, ulong target = 0)
        {
            Operation = operation; Action = action; Target = target;
            Validate();
        }

        private void Validate()
        {
            if (Operation == Guid.Empty || (byte)Action < (byte)LeoCommandAction.GiveTreat || (byte)Action > (byte)LeoCommandAction.UseBed)
                throw new InvalidDataException("Invalid shared Leo command.");
            bool needsTarget = Action == LeoCommandAction.GiveTreat || Action == LeoCommandAction.UseCarBed || Action == LeoCommandAction.UseBed;
            if (needsTarget ? Target == 0 : Target != 0) throw new InvalidDataException("Invalid shared Leo command target.");
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Operation.ToByteArray()); writer.Write((byte)Action); writer.Write(Target);
            return stream.ToArray();
        }

        public static LeoWorldCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid shared Leo command length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            var result = new LeoWorldCommand(new Guid(reader.ReadBytes(16)), (LeoCommandAction)reader.ReadByte(), reader.ReadUInt64());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing shared Leo command data.");
            return result;
        }
    }

    public enum LeoWorldActionStatus : byte
    { Accepted = 1, TooFar, NotTamed, InvalidTarget, DoorClosed, TooFast, InvalidState, NotReady, Capacity }

    public readonly struct LeoWorldReply
    {
        public const int Size = 18;
        public readonly Guid Operation;
        public readonly LeoCommandAction Action;
        public readonly LeoWorldActionStatus Status;
        public LeoWorldReply(Guid operation, LeoCommandAction action, LeoWorldActionStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(LeoCommandAction), action) || !Enum.IsDefined(typeof(LeoWorldActionStatus), status))
                throw new InvalidDataException("Invalid Leo world reply.");
            Operation = operation; Action = action; Status = status;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Operation.ToByteArray()); writer.Write((byte)Action); writer.Write((byte)Status); return stream.ToArray();
        }
        public static LeoWorldReply Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid Leo world reply length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            var reply = new LeoWorldReply(new Guid(reader.ReadBytes(16)), (LeoCommandAction)reader.ReadByte(), (LeoWorldActionStatus)reader.ReadByte());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Leo world reply data.");
            return reply;
        }
    }

    public enum LeoCommandClaim : byte { Claimed, Duplicate, Capacity }

    // Session-local, fail-closed replay protection. When full, the host must reject
    // new remote Leo actions until the session is recreated; silently evicting an
    // operation could repeat friendship or consume the same treat twice.
    public sealed class LeoCommandClaims
    {
        public const int Capacity = 512;
        private readonly HashSet<(uint Peer, Guid Operation)> claims = new HashSet<(uint, Guid)>();
        public int Count => claims.Count;

        public LeoCommandClaim Claim(uint peer, Guid operation)
        {
            if (peer == 0 || operation == Guid.Empty) throw new InvalidDataException("Invalid Leo operation identity.");
            var key = (peer, operation);
            if (claims.Contains(key)) return LeoCommandClaim.Duplicate;
            if (claims.Count >= Capacity) return LeoCommandClaim.Capacity;
            claims.Add(key);
            return LeoCommandClaim.Claimed;
        }

        public void Clear() => claims.Clear();
    }
}
