using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // Authoritative session state for the two native AdvancedShipController boats. Boat controls,
    // fuel and anchoring retain their existing adapters; this record owns the physics lease,
    // transform and the input/engine presentation needed by boat replicas.
    public sealed class BoatMotionState
    {
        public const ushort Adapter = 142;
        public const int MaxEngines = 8;
        public const float MaximumSpeed = 200f;
        public const float MaximumAngularSpeed = 50f;
        private const float AxisScale = 16384f;

        public readonly ItemLease Driver;
        public readonly ItemPose Pose;
        public readonly VehicleVector Velocity, AngularVelocity, AnchorPosition;
        public readonly float Steering, Throttle, Throttle2, Throttle3, Throttle4, BowThruster, SternThruster;
        public readonly int EngineCount;
        public readonly byte EngineOnMask;

        public BoatMotionState(ItemLease driver, ItemPose pose, VehicleVector velocity = default,
            VehicleVector angularVelocity = default, float steering = 0, float throttle = 0,
            float throttle2 = 0, float throttle3 = 0, float throttle4 = 0,
            float bowThruster = 0, float sternThruster = 0, VehicleVector anchorPosition = default,
            int engineCount = 0, byte engineOnMask = 0)
        {
            Driver = driver;
            Pose = pose;
            Velocity = velocity;
            AngularVelocity = angularVelocity;
            Steering = steering;
            Throttle = throttle;
            Throttle2 = throttle2;
            Throttle3 = throttle3;
            Throttle4 = throttle4;
            BowThruster = bowThruster;
            SternThruster = sternThruster;
            AnchorPosition = anchorPosition;
            EngineCount = engineCount;
            EngineOnMask = engineOnMask;
            Validate();
        }

        public bool EngineOn(int index) => index >= 0 && index < EngineCount && (EngineOnMask & (1 << index)) != 0;

        public BoatMotionState WithDriver(ItemLease driver) => new BoatMotionState(driver, Pose, Velocity,
            AngularVelocity, Steering, Throttle, Throttle2, Throttle3, Throttle4, BowThruster,
            SternThruster, AnchorPosition, EngineCount, EngineOnMask);

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(88);
            using var writer = new BinaryWriter(stream);
            writer.Write(Driver.Owner);
            writer.Write(Driver.Generation);
            Pose.Write(writer);
            Velocity.Write(writer);
            AngularVelocity.Write(writer);
            AnchorPosition.Write(writer);
            writer.Write(Quantize(Steering));
            writer.Write(Quantize(Throttle));
            writer.Write(Quantize(Throttle2));
            writer.Write(Quantize(Throttle3));
            writer.Write(Quantize(Throttle4));
            writer.Write(Quantize(BowThruster));
            writer.Write(Quantize(SternThruster));
            writer.Write((byte)EngineCount);
            writer.Write(EngineOnMask);
            return stream.ToArray();
        }

        public static BoatMotionState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 88) throw new InvalidDataException("Invalid boat-motion payload size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            var driver = new ItemLease(reader.ReadUInt32(), reader.ReadUInt32());
            var pose = ItemPose.Read(reader);
            var velocity = VehicleVector.Read(reader);
            var angular = VehicleVector.Read(reader);
            var anchor = VehicleVector.Read(reader);
            float steering = Dequantize(reader.ReadInt16());
            float throttle = Dequantize(reader.ReadInt16());
            float throttle2 = Dequantize(reader.ReadInt16());
            float throttle3 = Dequantize(reader.ReadInt16());
            float throttle4 = Dequantize(reader.ReadInt16());
            float bow = Dequantize(reader.ReadInt16());
            float stern = Dequantize(reader.ReadInt16());
            int engines = reader.ReadByte();
            byte engineMask = reader.ReadByte();
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing boat-motion payload data.");
            return new BoatMotionState(driver, pose, velocity, angular, steering, throttle,
                throttle2, throttle3, throttle4, bow, stern, anchor, engines, engineMask);
        }

        private void Validate()
        {
            if (Driver.Generation == 0) throw new InvalidDataException("Invalid boat-motion lease generation.");
            Pose.Validate();
            Velocity.Validate(MaximumSpeed);
            AngularVelocity.Validate(MaximumAngularSpeed);
            AnchorPosition.Validate(100000f);
            if (!Axis(Steering) || !Axis(Throttle) || !Axis(Throttle2) || !Axis(Throttle3) ||
                !Axis(Throttle4) || !Axis(BowThruster) || !Axis(SternThruster) ||
                EngineCount < 0 || EngineCount > MaxEngines || (EngineOnMask & ~MaskFor(EngineCount)) != 0)
                throw new InvalidDataException("Invalid boat-motion state.");
        }

        private static byte MaskFor(int count) => count == 0 ? (byte)0 : (byte)((1 << count) - 1);
        private static bool Axis(float value) => ItemPose.Finite(value, 1f) && value >= -1f;
        private static short Quantize(float value) => checked((short)Math.Round(value * AxisScale, MidpointRounding.AwayFromZero));
        private static float Dequantize(short value)
        {
            float decoded = value / AxisScale;
            if (decoded < -1f || decoded > 1f) throw new InvalidDataException("Invalid boat-motion axis.");
            return decoded;
        }
    }

    public enum BoatMotionAction : byte
    {
        AcquireDriver = 1,
        ReleaseDriver = 2,
        ControlFrame = 3
    }

    // The remote driver sends only the native axes and the one-frame starter pulse. The host owns
    // all transforms and applies the controls to its own native boat physics.
    public readonly struct BoatMotionCommand
    {
        public const int AcquireSize = 5;
        public const int ReleaseSize = 5;
        public const int ControlFrameSize = 20;
        private const float AxisScale = 16384f;

        public readonly BoatMotionAction Action;
        public readonly uint ExpectedGeneration;
        public readonly float Steering, Throttle, Throttle2, Throttle3, Throttle4, BowThruster, SternThruster;
        public readonly bool EngineStartStop;

        public BoatMotionCommand(BoatMotionAction action, uint expectedGeneration, float steering = 0,
            float throttle = 0, float throttle2 = 0, float throttle3 = 0, float throttle4 = 0,
            float bowThruster = 0, float sternThruster = 0, bool engineStartStop = false)
        {
            if (!Enum.IsDefined(typeof(BoatMotionAction), action) || expectedGeneration == 0)
                throw new InvalidDataException("Invalid boat-motion command.");
            Action = action;
            ExpectedGeneration = expectedGeneration;
            Steering = steering;
            Throttle = throttle;
            Throttle2 = throttle2;
            Throttle3 = throttle3;
            Throttle4 = throttle4;
            BowThruster = bowThruster;
            SternThruster = sternThruster;
            EngineStartStop = engineStartStop;
            if (action != BoatMotionAction.ControlFrame)
            {
                if (steering != 0 || throttle != 0 || throttle2 != 0 || throttle3 != 0 || throttle4 != 0 ||
                    bowThruster != 0 || sternThruster != 0 || engineStartStop)
                    throw new InvalidDataException("Unexpected boat-motion command data.");
            }
            else if (!Axis(steering) || !Axis(throttle) || !Axis(throttle2) || !Axis(throttle3) ||
                !Axis(throttle4) || !Axis(bowThruster) || !Axis(sternThruster))
                throw new InvalidDataException("Invalid boat-motion control frame.");
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Action == BoatMotionAction.ControlFrame ? ControlFrameSize : AcquireSize);
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)Action);
            writer.Write(ExpectedGeneration);
            if (Action == BoatMotionAction.ControlFrame)
            {
                writer.Write(Quantize(Steering));
                writer.Write(Quantize(Throttle));
                writer.Write(Quantize(Throttle2));
                writer.Write(Quantize(Throttle3));
                writer.Write(Quantize(Throttle4));
                writer.Write(Quantize(BowThruster));
                writer.Write(Quantize(SternThruster));
                writer.Write(EngineStartStop);
            }
            return stream.ToArray();
        }

        public static BoatMotionCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 1 || !Enum.IsDefined(typeof(BoatMotionAction), payload[0]))
                throw new InvalidDataException("Invalid boat-motion command size or action.");
            var action = (BoatMotionAction)payload[0];
            int expectedSize = action == BoatMotionAction.ControlFrame ? ControlFrameSize : AcquireSize;
            if (payload.Length != expectedSize) throw new InvalidDataException("Invalid boat-motion command size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            reader.ReadByte();
            uint generation = reader.ReadUInt32();
            if (action != BoatMotionAction.ControlFrame) return new BoatMotionCommand(action, generation);
            float steering = Dequantize(reader.ReadInt16());
            float throttle = Dequantize(reader.ReadInt16());
            float throttle2 = Dequantize(reader.ReadInt16());
            float throttle3 = Dequantize(reader.ReadInt16());
            float throttle4 = Dequantize(reader.ReadInt16());
            float bow = Dequantize(reader.ReadInt16());
            float stern = Dequantize(reader.ReadInt16());
            byte pulse = reader.ReadByte();
            if (pulse > 1) throw new InvalidDataException("Invalid boat-motion pulse flag.");
            return new BoatMotionCommand(action, generation, steering, throttle, throttle2, throttle3,
                throttle4, bow, stern, pulse != 0);
        }

        private static bool Axis(float value) => ItemPose.Finite(value, 1f) && value >= -1f;
        private static short Quantize(float value) => checked((short)Math.Round(value * AxisScale, MidpointRounding.AwayFromZero));
        private static float Dequantize(short value)
        {
            float decoded = value / AxisScale;
            if (decoded < -1f || decoded > 1f) throw new InvalidDataException("Invalid boat-motion control axis.");
            return decoded;
        }
    }

    public static class BoatMotionAuthorityPolicy
    {
        public static bool CanAcquire(bool ready, bool transfersBlocked, bool purchased, bool freshWalkingPose,
            bool nearDriverSeat, uint peer, uint expectedGeneration, ItemLease current) =>
            ready && !transfersBlocked && purchased && freshWalkingPose && nearDriverSeat && peer != 0 &&
            peer != ItemLease.Unowned && expectedGeneration != 0 && current.Owner == ItemLease.Unowned &&
            current.Generation == expectedGeneration;

        public static bool CanControl(bool ready, bool transfersBlocked, bool purchased, bool freshDriverPose,
            bool nearDriverSeat, uint peer, uint generation, ItemLease current) =>
            ready && !transfersBlocked && purchased && freshDriverPose && nearDriverSeat && peer != 0 &&
            peer != ItemLease.Unowned && generation != 0 && current.Owner == peer && current.Generation == generation;

        public static bool CanRelease(uint peer, uint generation, ItemLease current) =>
            peer != ItemLease.Unowned && generation != 0 && current.Owner == peer && current.Generation == generation;

        // Release is a cleanup command and remains admissible while the save gate blocks new
        // ownership/input. It still requires a live host route and the exact current lease.
        public static bool CanAcceptRelease(bool host, bool sessionRunning, bool channelReady, bool usableRecord,
            uint peer, uint generation, ItemLease current) =>
            host && sessionRunning && channelReady && usableRecord && CanRelease(peer, generation, current);
    }

    public sealed class BoatEngineFuelGuard<TTank> where TTank : class
    {
        private readonly Dictionary<TTank, int> activeUpdates = new Dictionary<TTank, int>();

        // Returns the tank token carried through Harmony __state. Only an active guest replica of
        // a mapped boat is scoped; host physics and untracked engines keep the native setter.
        public TTank? BeginNativeEngineUpdate(bool sessionRunning, bool host, bool mappedBoat, TTank? tank)
        {
            if (!sessionRunning || host || !mappedBoat || tank == null) return null;
            activeUpdates[tank] = activeUpdates.TryGetValue(tank, out int depth) ? depth + 1 : 1;
            return tank;
        }

        public void EndNativeEngineUpdate(TTank tank)
        {
            if (!activeUpdates.TryGetValue(tank, out int depth)) return;
            if (depth <= 1) activeUpdates.Remove(tank);
            else activeUpdates[tank] = depth - 1;
        }

        public bool SuppressNativeFuelWrite(TTank tank) => activeUpdates.TryGetValue(tank, out int depth) && depth > 0;

        // Harmony prefix contract for Part_FuelTank.set_Get_FuelLevel. Unscoped writes, including
        // host state application and SharedFuel application, continue through the native setter.
        public bool AllowNativeFuelWrite(TTank tank) => !SuppressNativeFuelWrite(tank);
    }
}
