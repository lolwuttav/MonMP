using System;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // Match the existing encoded-state comparison without building two streams/payloads.
    internal static class HoistPullerStateComparison
    {
        internal static bool Same(HoistPullerState a, HoistPullerState b) =>
            a.Phase == b.Phase && a.Vehicle == b.Vehicle && a.CrankSequence == b.CrankSequence &&
            Bits(a.GroundX) == Bits(b.GroundX) && Bits(a.GroundY) == Bits(b.GroundY) && Bits(a.GroundZ) == Bits(b.GroundZ) &&
            Bits(a.AnchorX) == Bits(b.AnchorX) && Bits(a.AnchorY) == Bits(b.AnchorY) && Bits(a.AnchorZ) == Bits(b.AnchorZ) &&
            Bits(a.HandX) == Bits(b.HandX) && Bits(a.HandY) == Bits(b.HandY) && Bits(a.HandZ) == Bits(b.HandZ) &&
            Bits(a.CrankTimer) == Bits(b.CrankTimer) && Bits(a.MinRange) == Bits(b.MinRange) && Bits(a.MaxRange) == Bits(b.MaxRange);

        private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    }
}
