using System;

namespace MonMP.Core.World
{
    public readonly struct MountedPartTuning : IEquatable<MountedPartTuning>
    {
        public readonly float SpringForce, Rebound;
        public readonly int Height, Damper;

        public MountedPartTuning(float springForce, float rebound, int height, int damper)
        { SpringForce = springForce; Rebound = rebound; Height = height; Damper = damper; }

        public bool IsValid(MountedVehiclePartKind kind) => kind == MountedVehiclePartKind.Coilover
            ? InRange(SpringForce) && InRange(Rebound) && Height >= 0 && Height <= 50 && Damper >= 0 && Damper <= 50
            : kind == MountedVehiclePartKind.TunedControlArm && InRange(SpringForce) && Rebound == 0 && Height == 0 && Damper == 0;
        public bool Equals(MountedPartTuning other) => SpringForce.Equals(other.SpringForce) && Rebound.Equals(other.Rebound) &&
            Height == other.Height && Damper == other.Damper;
        public override bool Equals(object? obj) => obj is MountedPartTuning other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(SpringForce, Rebound, Height, Damper);

        private static bool InRange(float value) => value >= 0 && value <= 50;
    }

    public enum MountedVehiclePartKind
    {
        None,
        Coilover,
        TunedControlArm,
        RearSeat,
        RpmGaugeShiftThreshold,
        SpeedGaugeUnits,
        TurboWastegateSound,
        StakePickupHistory,
        SpoilerPickupHistory
    }

    // Subtype interpretation of the existing VehiclePartState native-value fields.
    // Coilovers and tuned arms preserve native fractional save values. The gauge, wastegate,
    // seat, and pickup-history fields are bounded to their concrete native subtype contracts.
    public static class MountedVehiclePartValues
    {
        public static bool IsOneNativeStep(MountedPartTuning current, MountedPartTuning next, MountedVehiclePartKind kind)
        {
            if (!current.IsValid(kind) || !next.IsValid(kind)) return false;
            if (kind == MountedVehiclePartKind.TunedControlArm)
                return current.SpringForce != next.SpringForce && IsFloatStep(current.SpringForce, next.SpringForce);
            if (kind != MountedVehiclePartKind.Coilover) return false;
            int changes = 0;
            if (current.SpringForce != next.SpringForce)
            { if (!IsFloatStep(current.SpringForce, next.SpringForce)) return false; changes++; }
            if (current.Rebound != next.Rebound)
            { if (!IsFloatStep(current.Rebound, next.Rebound)) return false; changes++; }
            if (current.Height != next.Height)
            { if (Math.Abs(current.Height - next.Height) != 1) return false; changes++; }
            if (current.Damper != next.Damper)
            { if (Math.Abs(current.Damper - next.Damper) != 1) return false; changes++; }
            return changes == 1;
        }

        public static bool IsValid(VehiclePartState state, MountedVehiclePartKind kind, int optionCount = 0)
        {
            if ((uint)kind > (uint)MountedVehiclePartKind.SpoilerPickupHistory) return false;
            if (!state.Installed) return state.Equals(VehiclePartState.Empty);
            switch (kind)
            {
                case MountedVehiclePartKind.None:
                    return !state.HasNativeValues;
                case MountedVehiclePartKind.Coilover:
                    return InRange(state.Value1) && InRange(state.Value2) &&
                        state.IntValue1 >= 0 && state.IntValue1 <= 50 &&
                        state.IntValue2 >= 0 && state.IntValue2 <= 50;
                case MountedVehiclePartKind.TunedControlArm:
                    return InRange(state.Value1) && state.Value2 == 0 && state.IntValue1 == 0 && state.IntValue2 == 0;
                case MountedVehiclePartKind.RearSeat:
                    return (state.Value1 == 0 || state.Value1 == 1) &&
                        state.Value2 == 0 && state.IntValue1 == 0 && state.IntValue2 == 0;
                case MountedVehiclePartKind.RpmGaugeShiftThreshold:
                    return state.Value1 == 0 && state.Value2 == 0 && state.IntValue2 == 0 &&
                        // The native save setter clamps but does not round to a 100 RPM grid.
                        state.IntValue1 >= 2000 && state.IntValue1 <= 10000;
                case MountedVehiclePartKind.SpeedGaugeUnits:
                    return state.Value1 == 0 && state.Value2 == 0 && state.IntValue2 == 0 &&
                        (state.IntValue1 == 0 || state.IntValue1 == 1);
                case MountedVehiclePartKind.TurboWastegateSound:
                    return state.Value1 == 0 && state.Value2 == 0 && state.IntValue2 == 0 &&
                        optionCount >= 1 && optionCount <= 32 && state.IntValue1 >= 0 && state.IntValue1 < optionCount;
                case MountedVehiclePartKind.StakePickupHistory:
                case MountedVehiclePartKind.SpoilerPickupHistory:
                    return state.Value1 == 0 && state.Value2 == 0 && state.IntValue1 == 0 &&
                        (state.IntValue2 == 0 || state.IntValue2 == 1);
                default:
                    return false;
            }
        }

        public static bool IsValidTransition(VehiclePartState current, VehiclePartState next,
            MountedVehiclePartKind kind, bool boltless = false, int optionCount = 0)
        {
            if (!IsValid(current, kind, optionCount) || !IsValid(next, kind, optionCount)) return false;
            if (!next.Installed) return current.Installed && next.Equals(VehiclePartState.Empty);
            if (!current.Installed) return next.FullyTightened == boltless;
            if (current.Prefab != next.Prefab) return false;
            if (current.FullyTightened != next.FullyTightened)
                return !boltless && !current.FullyTightened && next.FullyTightened && SameValues(current, next);
            if (boltless && !next.FullyTightened) return false;
            return IsOneNativeStep(current, next, kind, optionCount);
        }

        public static bool IsOneNativeStep(VehiclePartState current, VehiclePartState next,
            MountedVehiclePartKind kind, int optionCount = 0)
        {
            if (!current.Installed || !next.Installed || !IsValid(current, kind, optionCount) || !IsValid(next, kind, optionCount) ||
                current.Prefab != next.Prefab || current.FullyTightened != next.FullyTightened) return false;
            if (kind == MountedVehiclePartKind.RearSeat) return current.Value1 != next.Value1;
            if (kind == MountedVehiclePartKind.RpmGaugeShiftThreshold)
                return current.IntValue1 != next.IntValue1 &&
                    (next.IntValue1 == Math.Min(10000, current.IntValue1 + 100) ||
                     next.IntValue1 == Math.Max(2000, current.IntValue1 - 100));
            if (kind == MountedVehiclePartKind.SpeedGaugeUnits)
                return current.IntValue1 != next.IntValue1;
            if (kind == MountedVehiclePartKind.TurboWastegateSound)
                return current.IntValue1 != next.IntValue1 &&
                    (Math.Abs(current.IntValue1 - next.IntValue1) == 1 ||
                     optionCount > 1 && (current.IntValue1 == 0 && next.IntValue1 == optionCount - 1 ||
                         next.IntValue1 == 0 && current.IntValue1 == optionCount - 1));
            if (kind == MountedVehiclePartKind.StakePickupHistory || kind == MountedVehiclePartKind.SpoilerPickupHistory)
                return current.IntValue2 == 0 && next.IntValue2 == 1;
            if (kind != MountedVehiclePartKind.Coilover && kind != MountedVehiclePartKind.TunedControlArm) return false;

            int changes = 0;
            if (current.Value1 != next.Value1)
            {
                if (!IsFloatStep(current.Value1, next.Value1)) return false;
                changes++;
            }
            if (current.Value2 != next.Value2)
            {
                if (!IsFloatStep(current.Value2, next.Value2)) return false;
                changes++;
            }
            if (current.IntValue1 != next.IntValue1)
            {
                if (Math.Abs(current.IntValue1 - next.IntValue1) != 1) return false;
                changes++;
            }
            if (current.IntValue2 != next.IntValue2)
            {
                if (Math.Abs(current.IntValue2 - next.IntValue2) != 1) return false;
                changes++;
            }
            return changes == 1;
        }

        private static bool InRange(float value) => value >= 0 && value <= 50;
        private static bool IsFloatStep(float current, float next) =>
            next == Math.Min(50f, current + 1f) || next == Math.Max(0f, current - 1f);
        private static bool SameValues(VehiclePartState a, VehiclePartState b) =>
            a.Value1 == b.Value1 && a.Value2 == b.Value2 && a.IntValue1 == b.IntValue1 && a.IntValue2 == b.IntValue2;
    }
}
