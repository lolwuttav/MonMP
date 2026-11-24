namespace MonMP.Core.World
{
    /// <summary>
    /// The electric percolator is represented by the generic shared-item record (native IntValue1
    /// is its frozen state and IntValue2 is the child percolator's coffee count).
    /// Its child Interactable bypasses that item interaction path, so guest-side native mutations
    /// must be suppressed while the shared world is running.
    /// </summary>
    public static class ElectricCoffeePercolatorPolicy
    {
        public static bool BlocksGuestInteraction(bool sessionRunning, bool isHost, bool hasElectricParent) =>
            sessionRunning && !isHost && hasElectricParent;
    }
}
