using System;
using System.IO;

namespace MonMP.Core.World
{
    public readonly struct VehicleVector
    {
        public readonly float X, Y, Z;
        public VehicleVector(float x, float y, float z) { X = x; Y = y; Z = z; }
        internal void Validate(float limit)
        {
            if (!ItemPose.Finite(X, limit) || !ItemPose.Finite(Y, limit) || !ItemPose.Finite(Z, limit))
                throw new InvalidDataException("Invalid vehicle velocity.");
        }
        internal void Write(BinaryWriter w) { w.Write(X); w.Write(Y); w.Write(Z); }
        internal static VehicleVector Read(BinaryReader r) => new VehicleVector(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    // Vetted stock vehicles with one driver and an optional jointed accessory body. Steering travels as
    // a 1/16384 step and the pedals as bytes: that made room for the effect inputs within the 128-byte
    // command limit (a truck command with its accessory and automatic Park flag is 128 bytes).
    public sealed class VehicleState
    {
        public const ushort Adapter = 4;
        public readonly ItemLease Driver;
        public readonly ItemPose Pose;
        public readonly VehicleVector Velocity, AngularVelocity;
        public readonly float Fuel, EngineAngularVelocity, Steering, Throttle, Brake, Handbrake, HandbrakeInput;
        public readonly int Gear, Ignition, ParkingBrake, TransferCase;
        public readonly bool EngineIgnition, Starter, AutomaticPark, Active, MotionReady, LiftFrozen;
        public readonly ItemPose? Accessory;
        public readonly VehicleLightingState Lighting;
        public readonly VehicleWheelSpin WheelSpin;
        public readonly VehicleWheelAngles WheelAngles;
        public readonly VehicleEffects Effects;
        // Host-added fuel is independent of the driver's simulation. Only world records carry
        // this cumulative credit; driver commands acknowledge it separately to retain 128 bytes.
        public readonly double RefuelCredit;
        public VehicleState(ItemLease driver, ItemPose pose, VehicleVector velocity = default, VehicleVector angularVelocity = default,
            float fuel = 0, float engineAngularVelocity = 0, float steering = 0, float throttle = 0, float brake = 0, float handbrake = 0,
            int gear = 0, int ignition = 0, bool engineIgnition = false, bool starter = false, int parkingBrake = 0, int transferCase = 0, ItemPose? accessory = null, VehicleLightingState lighting = default, VehicleWheelSpin wheelSpin = default, VehicleWheelAngles wheelAngles = default, VehicleEffects effects = default, float handbrakeInput = 0, bool automaticPark = false, bool active = true, bool motionReady = true, double refuelCredit = 0, bool liftFrozen = false)
        {
            Driver = driver; Pose = pose; Velocity = velocity; AngularVelocity = angularVelocity; Fuel = fuel;
            EngineAngularVelocity = engineAngularVelocity; Steering = steering; Throttle = throttle; Brake = brake;
            Handbrake = handbrake; HandbrakeInput = handbrakeInput; Gear = gear; Ignition = ignition; EngineIgnition = engineIgnition; Starter = starter;
            ParkingBrake = parkingBrake; TransferCase = transferCase; AutomaticPark = automaticPark; Active = active; MotionReady = motionReady;
            Accessory = accessory;
            Lighting = lighting;
            WheelSpin = wheelSpin;
            WheelAngles = wheelAngles;
            Effects = effects;
            RefuelCredit = refuelCredit;
            LiftFrozen = liftFrozen;
        }
        public VehicleState WithDriver(ItemLease driver) => new VehicleState(driver, Pose, Velocity, AngularVelocity, Fuel,
            EngineAngularVelocity, Steering, Throttle, Brake, Handbrake, Gear, Ignition, EngineIgnition, Starter, ParkingBrake, TransferCase, Accessory, Lighting, WheelSpin, WheelAngles, Effects, HandbrakeInput, AutomaticPark, Active, MotionReady, RefuelCredit, LiftFrozen);
        public VehicleState WithFuel(float fuel, double refuelCredit) => new VehicleState(Driver, Pose, Velocity, AngularVelocity, fuel,
            EngineAngularVelocity, Steering, Throttle, Brake, Handbrake, Gear, Ignition, EngineIgnition, Starter, ParkingBrake, TransferCase, Accessory, Lighting, WheelSpin, WheelAngles, Effects, HandbrakeInput, AutomaticPark, Active, MotionReady, refuelCredit, LiftFrozen);
        public VehicleState WithLiftFrozen(bool frozen) => new VehicleState(Driver, Pose, Velocity, AngularVelocity, Fuel,
            EngineAngularVelocity, Steering, Throttle, Brake, Handbrake, Gear, Ignition, EngineIgnition, Starter, ParkingBrake, TransferCase, Accessory, Lighting, WheelSpin, WheelAngles, Effects, HandbrakeInput, AutomaticPark, Active, MotionReady, RefuelCredit, frozen);
        private void Validate()
        {
            Pose.Validate(); Velocity.Validate(200); AngularVelocity.Validate(50); WheelSpin.Validate();
            if (Accessory.HasValue)
            {
                var a = Accessory.Value; a.Validate();
                if (!ItemPose.Finite(a.X, 10) || !ItemPose.Finite(a.Y, 10) || !ItemPose.Finite(a.Z, 10)) throw new InvalidDataException("Invalid accessory offset.");
            }
            if (Driver.Generation == 0 || !ItemPose.Finite(Fuel, 1000) || Fuel < 0 || !VehicleRefuelAcknowledgement.ValidCredit(RefuelCredit) || !ItemPose.Finite(EngineAngularVelocity, 5000) ||
                !ItemPose.Finite(Steering, 1) || !Unit(Throttle) || !Unit(Brake) || !Unit(Handbrake) || !Unit(HandbrakeInput) || Gear < -16 || Gear > 16 || Ignition < 0 || Ignition > 2 ||
                ParkingBrake < 0 || ParkingBrake > 2 || TransferCase < 0 || TransferCase > 1)
                throw new InvalidDataException("Invalid vehicle state.");
        }
        private static bool Unit(float f) => ItemPose.Finite(f, 1) && f >= 0;
        private const float SteeringScale = 16384f;
        private static byte Pedal(float f) => (byte)Math.Round(f * 255, MidpointRounding.AwayFromZero);
        public byte[] Encode(bool includeRefuelCredit = true)
        {
            Validate(); using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Driver.Owner); w.Write(Driver.Generation); Pose.Write(w); Velocity.Write(w); AngularVelocity.Write(w);
            w.Write(Fuel); w.Write(EngineAngularVelocity); w.Write((short)Math.Round(Steering * SteeringScale, MidpointRounding.AwayFromZero));
            w.Write(Pedal(Throttle)); w.Write(Pedal(Brake)); w.Write(Pedal(Handbrake));
            w.Write((sbyte)Gear);
            w.Write((byte)(Ignition | (EngineIgnition ? 4 : 0) | (Starter ? 8 : 0) | (ParkingBrake << 4) | (TransferCase << 6) | (Accessory.HasValue ? 0x80 : 0)));
            if (Accessory.HasValue) Accessory.Value.Write(w);
            Effects.Write(w);
            Lighting.Write(w, Effects.RadiatorHeatStep);
            WheelSpin.Write(w);
            WheelAngles.Write(w);
            w.Write(Pedal(HandbrakeInput));
            // Negative lifecycle bits preserve older saved records' default live chassis.
            w.Write((byte)((AutomaticPark ? 1 : 0) | (Active ? 0 : 2) | (MotionReady ? 0 : 4) | (LiftFrozen ? 8 : 0)));
            if (includeRefuelCredit) w.Write(RefuelCredit);
            return stream.ToArray();
        }
        public static VehicleState Decode(byte[] payload)
        {
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            var driver = new ItemLease(r.ReadUInt32(), r.ReadUInt32()); var pose = ItemPose.Read(r);
            var velocity = VehicleVector.Read(r); var angular = VehicleVector.Read(r);
            float fuel = r.ReadSingle(), rpm = r.ReadSingle(), steer = r.ReadInt16() / SteeringScale;
            float throttle = r.ReadByte() / 255f, brake = r.ReadByte() / 255f, handbrake = r.ReadByte() / 255f;
            int gear = r.ReadSByte(), packed = r.ReadByte();
            int ignition = packed & 3, parking = (packed >> 4) & 3, transfer = (packed >> 6) & 1;
            bool engineIgnition = (packed & 4) != 0, starter = (packed & 8) != 0, hasAccessory = (packed & 0x80) != 0;
            ItemPose? accessory = hasAccessory ? ItemPose.Read(r) : (ItemPose?)null;
            var effects = VehicleEffects.Read(r);
            var lighting = VehicleLightingState.Read(r, out byte radiatorHeat);
            effects = effects.WithRadiatorHeat(radiatorHeat);
            var wheelSpin = VehicleWheelSpin.Read(r);
            var wheelAngles = VehicleWheelAngles.Read(r);
            // Older records had only the effective brake value. New records always carry raw normalized input.
            float handbrakeInput = stream.Position < stream.Length ? r.ReadByte() / 255f : handbrake;
            // Gear zero alone cannot distinguish the automatic transmission's P/N.
            // The original packed flags are full, so use the last available command byte.
            byte flags = stream.Position < stream.Length ? r.ReadByte() : (byte)0;
            if (flags > 15) throw new InvalidDataException("Invalid vehicle lifecycle flags.");
            double refuelCredit = stream.Position < stream.Length ? r.ReadDouble() : 0;
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing vehicle state data.");
            var result = new VehicleState(driver, pose, velocity, angular, fuel, rpm, steer, throttle, brake, handbrake, gear, ignition, engineIgnition, starter, parking, transfer, accessory, lighting, wheelSpin, wheelAngles, effects, handbrakeInput, (flags & 1) != 0, (flags & 2) == 0, (flags & 4) == 0, refuelCredit, (flags & 8) != 0);
            result.Validate(); return result;
        }
    }

    public enum VehicleAction : byte { Enter = 1, Move, Exit }
    public readonly struct VehicleCommand
    {
        public readonly VehicleAction Action;
        public readonly VehicleState State;
        public VehicleCommand(VehicleAction action, VehicleState state) { Action = action; State = state; }
        public byte[] Encode()
        {
            var state = State.Encode(includeRefuelCredit: false); var result = new byte[state.Length + 1]; result[0] = (byte)Action;
            Array.Copy(state, 0, result, 1, state.Length); return result;
        }
        public static VehicleCommand Decode(byte[] payload)
        {
            if (payload.Length < 2 || !Enum.IsDefined(typeof(VehicleAction), payload[0])) throw new InvalidDataException("Invalid vehicle action.");
            var state = new byte[payload.Length - 1]; Array.Copy(payload, 1, state, 0, state.Length);
            return new VehicleCommand((VehicleAction)payload[0], VehicleState.Decode(state));
        }
    }
}
