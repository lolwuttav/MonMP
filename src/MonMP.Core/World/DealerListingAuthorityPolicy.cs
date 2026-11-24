namespace MonMP.Core.World
{
    public static class DealerListingAuthorityPolicy
    {
        // Outdoor planter purchases are not registered with a shared transaction adapter. Their
        // native callback starts bunker spawning and hides the local stock before the cash debit.
        public static bool ShouldSuppressNativeOutdoorPlanterPurchase(bool multiplayerSessionRunning,
            bool host, bool exactOutdoorPlanterListing) =>
            multiplayerSessionRunning && !host && exactOutdoorPlanterListing;
    }
}
