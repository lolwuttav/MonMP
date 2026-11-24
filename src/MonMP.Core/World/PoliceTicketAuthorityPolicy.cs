namespace MonMP.Core.World
{
    public static class PoliceTicketAuthorityPolicy
    {
        public static bool ShouldSuppressNativeCreation(bool sessionRunning, bool isHost) =>
            sessionRunning && !isHost;
    }
}
