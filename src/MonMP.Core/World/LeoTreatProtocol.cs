using System;
using System.Collections.Generic;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    // A stable WorldChannel command endpoint for one host-authoritative Leo treat.
    // The native Leo record changes frequently, so requests must not depend on its revision.
    public readonly struct LeoTreatCommand
    {
        public const ushort Adapter = 64;
        public const byte Marker = 0xEC;
        public const int Size = 50;
        public readonly bool IndependentAim;
        public const float Reach = 2.4f;
        public static readonly ulong Entity = StaticId.Hash("monmp/leo/dog-treat-command/v1");

        public readonly Guid Operation;
        public readonly ulong Tool;
        public readonly uint Generation;
        public readonly float CameraX, CameraY, CameraZ, CameraYaw, CameraPitch;

        public LeoTreatCommand(Guid operation, ulong tool, uint generation,
            float cameraX, float cameraY, float cameraZ, float cameraYaw, float cameraPitch, bool independentAim = false)
        {
            Operation = operation; Tool = tool; Generation = generation;
            CameraX = cameraX; CameraY = cameraY; CameraZ = cameraZ;
            CameraYaw = cameraYaw; CameraPitch = cameraPitch;
            IndependentAim = independentAim;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write(Tool); writer.Write(Generation);
            writer.Write(CameraX); writer.Write(CameraY); writer.Write(CameraZ);
            writer.Write(CameraYaw); writer.Write(CameraPitch);
            writer.Write(IndependentAim);
            return stream.ToArray();
        }

        public static LeoTreatCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid Leo treat command length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Marker) throw new InvalidDataException("Invalid Leo treat command marker.");
            var command = new LeoTreatCommand(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), reader.ReadUInt32(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Leo treat command data.");
            return command;
        }

        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;
        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        private void Validate()
        {
            if (Operation == Guid.Empty || Tool <= WorldRecord.DynamicIdStart || Generation == 0)
                throw new InvalidDataException("Invalid Leo treat operation or tool lease.");
            if (!Finite(CameraX) || Math.Abs(CameraX) > 100000 || !Finite(CameraY) || Math.Abs(CameraY) > 100000 ||
                !Finite(CameraZ) || Math.Abs(CameraZ) > 100000 || !Finite(CameraYaw) || CameraYaw < 0 || CameraYaw > 360 ||
                !Finite(CameraPitch) || CameraPitch < -90 || CameraPitch > 90)
                throw new InvalidDataException("Invalid Leo treat camera ray.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public enum LeoTreatResult : byte
    {
        Applied = 1,
        Duplicate = 2,
        Capacity = 3,
        InvalidToolLease = 4,
        StalePlayerPose = 5,
        PlayerNotWalking = 6,
        InvalidCameraRay = 7,
        TargetNotLeo = 8,
        LeoUnavailable = 9,
        AlreadyGivenToday = 10,
        InsufficientFood = 11,
        NativeEffectFailed = 12
    }

    public readonly struct LeoTreatReply
    {
        public const int Size = 17;
        public readonly Guid Operation;
        public readonly LeoTreatResult Result;

        public LeoTreatReply(Guid operation, LeoTreatResult result)
        { Operation = operation; Result = result; Validate(); }

        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Operation.ToByteArray()); writer.Write((byte)Result); return stream.ToArray();
        }

        public static LeoTreatReply Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid Leo treat reply length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            var reply = new LeoTreatReply(new Guid(reader.ReadBytes(16)), (LeoTreatResult)reader.ReadByte());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Leo treat reply data.");
            return reply;
        }

        private void Validate()
        {
            if (Operation == Guid.Empty || !Enum.IsDefined(typeof(LeoTreatResult), Result))
                throw new InvalidDataException("Invalid Leo treat reply.");
        }
    }

    public static class LeoTreatPolicy
    {
        public const int LeoActorIndex = 24;
        public const float FoodCost = 0.2f;

        public static LeoTreatResult ValidateHostRequest(bool exactToolLease, bool poseFresh, byte playerState,
            bool cameraOriginFits, bool cameraAimFits, bool rayHitsLeo, bool leoActive, bool isTamed,
            byte leoState, bool alreadyGivenToday, float food)
        {
            if (!exactToolLease) return LeoTreatResult.InvalidToolLease;
            if (!poseFresh) return LeoTreatResult.StalePlayerPose;
            if (playerState != 0) return LeoTreatResult.PlayerNotWalking;
            if (!cameraOriginFits || !cameraAimFits) return LeoTreatResult.InvalidCameraRay;
            if (!rayHitsLeo) return LeoTreatResult.TargetNotLeo;
            if (!leoActive || !isTamed || !IsSupportedTamedState(leoState)) return LeoTreatResult.LeoUnavailable;
            if (alreadyGivenToday) return LeoTreatResult.AlreadyGivenToday;
            if (!Finite(food) || food <= 0 || food > 1) return LeoTreatResult.InsufficientFood;
            return LeoTreatResult.Applied;
        }

        // Leo's native TreatGiven override is only active in TamedSM. These are
        // the native enum values reached while that state machine is active.
        public static bool IsSupportedTamedState(byte state) => state <= 10 || state == 14;

        // Preserve Tool_DogTreats.MouseDown's order and rounding: remove 0.2,
        // round to a percent, then clamp to [0,1].
        public static float FoodAfterUse(float food)
        {
            if (!Finite(food) || food <= 0 || food > 1) throw new InvalidDataException("Invalid Leo treat food amount.");
            float remaining = food - FoodCost;
            return Math.Max(0, Math.Min(1, (float)(Math.Round(remaining * 100f, MidpointRounding.AwayFromZero) / 100f)));
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public enum LeoTreatClaimResult : byte { Claimed, Duplicate, Capacity }

    public sealed class LeoTreatClaims
    {
        public const int Capacity = 8192;
        private readonly HashSet<(uint Peer, Guid Operation)> claims = new HashSet<(uint, Guid)>();
        public int Count => claims.Count;

        public LeoTreatClaimResult Claim(uint peer, Guid operation)
        {
            if (peer == 0 || operation == Guid.Empty) throw new InvalidDataException("Invalid Leo treat claim identity.");
            var key = (peer, operation);
            if (claims.Contains(key)) return LeoTreatClaimResult.Duplicate;
            if (claims.Count >= Capacity) return LeoTreatClaimResult.Capacity;
            claims.Add(key);
            return LeoTreatClaimResult.Claimed;
        }

        public void Clear() => claims.Clear();
    }
}
