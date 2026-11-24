namespace MonMP.Plugin.Replication
{
    internal static class AvatarSkinPartition
    {
        // Bind-space height refines seams only after the skeleton identifies the limb.
        // Low hands must remain torso skin, including when trousers/shoes use another body.
        internal static int Part(float bindY, float headWeight, float lowerWeight, float footWeight) =>
            headWeight > .5f ? 0 : lowerWeight > .5f && bindY < 1.02f ? (footWeight > .5f && bindY < .19f ? 3 : 2) : 1;
        internal static int Face(int a, int b, int c) => a == b || a == c ? a : b;
        internal static bool Exposed(float hands, float head, float headAndNeck, bool hooded) =>
            hands > .5f || (hooded ? headAndNeck > .05f : head > .5f);
    }
}
