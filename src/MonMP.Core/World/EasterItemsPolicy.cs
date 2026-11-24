namespace MonMP.Core.World
{
    // Easter egg collection increments the local basket and destroys the egg from Grab().
    // Gilles' delayed-return request also mutates Store_Gilles state outside SharedItems.
    // Until those actions are host-routed, guests must not run them on local replicas.
    public static class EasterItemsPolicy
    {
        public static bool BlockGuestEasterAction(bool sessionRunning, bool host) => sessionRunning && !host;
    }
}
