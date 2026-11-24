namespace MonMP.Plugin.Replication
{
    // Numeric values follow the native Vehicle_Type enum. The stock towing menu filters
    // these out even though TowThisVehicle itself accepts arbitrary non-Useless values.
    internal static class TowingTargetPolicy
    {
        internal const int UselessVehicleType = 8;
        internal const int AiVehicleType = 9;
        internal const int PitBikeType = 10;
        internal const int MaximumVehicleType = 16;

        internal static bool IsNativeMenuType(int type) =>
            type >= 0 && type <= MaximumVehicleType && type != UselessVehicleType &&
            type != AiVehicleType && type != PitBikeType;

        internal static bool IsCanonicalNativeTarget(int requestedType, int actualType, bool sceneIsLoaded,
            bool hasRigidbody, int registeredTypeCount, int exactRegistrationCount) =>
            IsNativeMenuType(requestedType) && requestedType == actualType && sceneIsLoaded && hasRigidbody &&
            registeredTypeCount == 1 && exactRegistrationCount == 1;

        internal static bool HasSingleMovementRoute(bool sharedVehicle, bool sharedTrailer, bool localNativeMirror) =>
            (sharedVehicle ? 1 : 0) + (sharedTrailer ? 1 : 0) + (localNativeMirror ? 1 : 0) == 1;
    }
}
