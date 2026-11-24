using System;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // Read native fields at the normal cadence, but retain the immutable capture when
    // every field is unchanged. Only a record proved to contain that exact capture
    // permits skipping the codec; direct command/refuel/lifecycle publication invalidates it.
    internal static class VehicleStatePublication
    {
        // restDeadband: the host's capture of a vehicle nobody drives. Its body and wheels
        // may keep the published sample while they stay within RestDeadband of it.
        internal static VehicleState Reuse(VehicleState? previous, ItemLease driver, ItemPose pose,
            VehicleVector velocity = default, VehicleVector angularVelocity = default,
            float fuel = 0, float engineAngularVelocity = 0, float steering = 0, float throttle = 0,
            float brake = 0, float handbrake = 0, int gear = 0, int ignition = 0,
            bool engineIgnition = false, bool starter = false, int parkingBrake = 0, int transferCase = 0,
            ItemPose? accessory = null, VehicleLightingState lighting = default, VehicleWheelSpin wheelSpin = default,
            VehicleWheelAngles wheelAngles = default, VehicleEffects effects = default, float handbrakeInput = 0,
            bool automaticPark = false, bool active = true, bool motionReady = true, double refuelCredit = 0,
            bool liftFrozen = false, bool restDeadband = false)
        {
            if (previous != null && previous.Driver.Owner == driver.Owner && previous.Driver.Generation == driver.Generation &&
                (Pose(previous.Pose, pose) && Vector(previous.Velocity, velocity) && Vector(previous.AngularVelocity, angularVelocity) ||
                    restDeadband && RestDeadband.Holds(previous.Pose, previous.Velocity, previous.AngularVelocity, pose, velocity, angularVelocity)) &&
                Float(previous.Fuel, fuel) && Float(previous.EngineAngularVelocity, engineAngularVelocity) &&
                Float(previous.Steering, steering) && Float(previous.Throttle, throttle) && Float(previous.Brake, brake) &&
                Float(previous.Handbrake, handbrake) && Float(previous.HandbrakeInput, handbrakeInput) &&
                previous.Gear == gear && previous.Ignition == ignition && previous.EngineIgnition == engineIgnition &&
                previous.Starter == starter && previous.ParkingBrake == parkingBrake && previous.TransferCase == transferCase &&
                previous.Accessory.HasValue == accessory.HasValue && (!accessory.HasValue || Pose(previous.Accessory!.Value, accessory.Value)) &&
                previous.Lighting.Lamps == lighting.Lamps && previous.Lighting.Controls == lighting.Controls &&
                previous.Lighting.LeftPhase == lighting.LeftPhase && previous.Lighting.RightPhase == lighting.RightPhase &&
                previous.Lighting.InteriorMode == lighting.InteriorMode &&
                Spin(previous.WheelSpin.Wheel0, wheelSpin.Wheel0, restDeadband) && Spin(previous.WheelSpin.Wheel1, wheelSpin.Wheel1, restDeadband) &&
                Spin(previous.WheelSpin.Wheel2, wheelSpin.Wheel2, restDeadband) && Spin(previous.WheelSpin.Wheel3, wheelSpin.Wheel3, restDeadband) &&
                previous.WheelAngles.Equals(wheelAngles) && previous.Effects.Equals(effects) &&
                previous.AutomaticPark == automaticPark && previous.Active == active && previous.MotionReady == motionReady &&
                BitConverter.DoubleToInt64Bits(previous.RefuelCredit) == BitConverter.DoubleToInt64Bits(refuelCredit) &&
                previous.LiftFrozen == liftFrozen) return previous;
            return new VehicleState(driver, pose, velocity, angularVelocity, fuel, engineAngularVelocity, steering,
                throttle, brake, handbrake, gear, ignition, engineIgnition, starter, parkingBrake, transferCase,
                accessory, lighting, wheelSpin, wheelAngles, effects, handbrakeInput, automaticPark, active,
                motionReady, refuelCredit, liftFrozen);
        }

        internal static bool CanSkip(VehicleState state, VehicleState? published, WorldRecord? publishedRecord, WorldRecord? currentRecord) =>
            ReferenceEquals(state, published) && publishedRecord != null && ReferenceEquals(publishedRecord, currentRecord);

        // Bitwise comparisons retain the codec's signed-zero representation and do
        // not silently normalize inputs before the ordinary Encode validation.
        private static bool Float(float left, float right) => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
        // A resting wheel's angular velocity wanders below the wire's quantization step; values
        // that encode to the same step are the same published value.
        private static bool Spin(float left, float right, bool restDeadband) => Float(left, right) ||
            restDeadband && Math.Round(left / VehicleWheelSpin.AngularVelocityStep, MidpointRounding.AwayFromZero) ==
                Math.Round(right / VehicleWheelSpin.AngularVelocityStep, MidpointRounding.AwayFromZero);
        private static bool Vector(VehicleVector left, VehicleVector right) =>
            Float(left.X, right.X) && Float(left.Y, right.Y) && Float(left.Z, right.Z);
        private static bool Pose(ItemPose left, ItemPose right) =>
            Float(left.X, right.X) && Float(left.Y, right.Y) && Float(left.Z, right.Z) &&
            Float(left.Qx, right.Qx) && Float(left.Qy, right.Qy) && Float(left.Qz, right.Qz) && Float(left.Qw, right.Qw);
    }
}
