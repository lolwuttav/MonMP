namespace MonMP.Plugin.Replication
{
    // SpecialMenu.Start initializes these exact stock presets only when its page first opens.
    // A remote request must also work before the host has ever opened that page.
    internal static class NativeCheatGravity
    {
        private static readonly float[] Values = { 0, -9.81f, 0, -1.62f, -3.721f, -24.79f, -11.15f, -10.44f, -8.87f, -3.7f, -0.62f };
        private static readonly string[] Labels = { "", "Earth (Default) | 1g", "Zero Gravity | 0g", "Moon | 0.166g", "Mars | 0.376g", "Jupiter | 2.36g", "Neptune | 1.12g", "Saturn | 0.916g", "Venus | 0.907g", "Mercury | 0.378g", "Pluto | 0.071g" };
        internal static bool TryGet(int selection, out float gravity, out string label)
        {
            gravity = 0; label = "";
            if (selection < 1 || selection >= Values.Length) return false;
            gravity = Values[selection]; label = Labels[selection]; return true;
        }
    }
}
