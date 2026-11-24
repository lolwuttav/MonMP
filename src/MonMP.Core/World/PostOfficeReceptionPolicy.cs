namespace MonMP.Core.World
{
    // The receiver itself has no progression behavior. These are the only envelope
    // kinds whose native post flow has a host-side bill/progression effect.
    public enum PostOfficeEnvelopeKind : byte
    {
        Unknown = 0,
        HydroConstruction = 1,
        HydroBill = 2,
        PoliceTicket = 3
    }

    public static class PostOfficeReceptionPolicy
    {
        public static bool HasHostPostRoute(PostOfficeEnvelopeKind kind) =>
            kind == PostOfficeEnvelopeKind.HydroConstruction || kind == PostOfficeEnvelopeKind.HydroBill ||
            kind == PostOfficeEnvelopeKind.PoliceTicket;

        // Preserve native-local rejection feedback for nonpostable envelopes. A postable
        // kind without an explicit shared route is blocked on guests before native costs
        // or progression code can run.
        public static bool ShouldBlockGuestPost(bool canBePosted, PostOfficeEnvelopeKind kind) =>
            canBePosted && !HasHostPostRoute(kind);

        // The native receiver trigger is not the only possible entry point: mods and
        // other game callbacks can call Store_PostOffice.TryingToSendThisLetter directly.
        // A known kind is safe only when its host route is actually attached; otherwise
        // the native callback would charge the guest's local copy of cash/progression.
        public static bool ShouldSuppressLocalPost(bool sessionRunning, bool isHost, bool canBePosted,
            PostOfficeEnvelopeKind kind, bool hostRouteAvailable) =>
            sessionRunning && !isHost && canBePosted && (!HasHostPostRoute(kind) || !hostRouteAvailable);
    }
}
