namespace MonMP.Core.World
{
    /// <summary>Validates one native Konig seat-slider step against the accepted per-seat snapshot.</summary>
    public static class VehicleSeatAdjustmentPolicy
    {
        public static bool AllowsActor(bool walking, bool driving, bool passenger, bool ownsDriver, bool ownsPassenger) =>
            walking || (driving && ownsDriver) || (passenger && ownsPassenger);

        public static bool TryStep(VehicleSeatAdjustmentState accepted, VehicleSeatAdjustmentCommand command,
            short nativeForwardCentimeters, short nativeHeightCentimeters,
            short minimumForwardCentimeters, short maximumForwardCentimeters,
            short minimumHeightCentimeters, short maximumHeightCentimeters,
            out VehicleSeatAdjustmentState next)
        {
            next = default;
            if (!accepted.IsCurrent(command) || nativeForwardCentimeters != accepted.ForwardCentimeters ||
                nativeHeightCentimeters != accepted.HeightCentimeters ||
                minimumForwardCentimeters > maximumForwardCentimeters || minimumHeightCentimeters > maximumHeightCentimeters ||
                accepted.ForwardCentimeters < minimumForwardCentimeters || accepted.ForwardCentimeters > maximumForwardCentimeters ||
                accepted.HeightCentimeters < minimumHeightCentimeters || accepted.HeightCentimeters > maximumHeightCentimeters)
                return false;

            int forward = accepted.ForwardCentimeters;
            int height = accepted.HeightCentimeters;
            if (command.Axis == VehicleSeatAdjustmentAxis.Forward) forward += command.Direction;
            else if (command.Axis == VehicleSeatAdjustmentAxis.Height) height += command.Direction;
            else return false;

            if (forward < minimumForwardCentimeters || forward > maximumForwardCentimeters ||
                height < minimumHeightCentimeters || height > maximumHeightCentimeters)
                return false;

            next = new VehicleSeatAdjustmentState((short)forward, (short)height,
                VehicleSeatAdjustmentState.NextGeneration(accepted.Generation));
            return true;
        }
    }
}
