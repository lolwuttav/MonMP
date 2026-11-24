using System.Collections.Generic;

namespace MonMP.Plugin.Replication
{
    // Native female Actor mesh1824: same face order as the default body, different
    // proportions. Only face IDs are distributed. Keep exposed head/neck/hands/legs;
    // the common plaid replaces the skin underneath the shirt, including the chest.
    internal static class NpcAvatarCoveredSkin
    {
        internal const ulong PositionHash = 0x6A8C212B9A188430UL;
        // The other imported Actor variants differ from the baseline only by
        // sub-micrometre float rounding and retain its complete face order.
        internal const ulong AlternateMaleHash = 0xF09A814820E2B982UL;
        internal static bool Accepts(ulong hash) => hash == PositionHash || hash == AlternateMaleHash;
        private static readonly HashSet<int> Covered = new HashSet<int> {
            190, 196, 197, 204, 205, 206, 213, 214, 215, 216, 217, 222, 223, 224, 225, 226,
            227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239, 240, 241, 242,
            243, 244, 245, 246, 247, 248, 249, 250, 251, 252, 253, 254, 255, 256, 257, 265,
            266, 267, 268, 269, 270, 271, 272, 273, 274, 282, 283, 284, 285, 286, 287, 288,
            289, 290, 291, 292, 293, 294, 295, 296, 297, 298, 299, 300, 301, 302, 303, 304,
            305, 306, 309, 310, 312, 313, 314, 316, 317, 318, 319, 324, 325, 326, 329, 330,
            331, 333, 335, 336, 337, 338, 343, 344, 345, 357, 359, 360, 708, 709, 710, 711,
            712, 713, 714, 715, 716, 717, 718, 719, 720, 721, 722, 723, 724, 725, 726, 727,
            728, 729, 730, 731, 732, 733, 734, 735, 736, 737, 738, 739, 740, 741, 742, 743,
            744, 745, 748, 749, 754, 953, 954, 955, 956, 957, 958, 959, 960, 961, 962, 963,
            964, 965, 966, 967, 968, 969, 970, 971, 972, 973, 974, 975, 976, 977, 980, 981,
            983, 984, 985, 987, 988, 989, 990, 995, 996, 997, 1000, 1001, 1003, 1004, 1005, 1006,
            1007, 1008, 1009, 1011, 1012, 1013, 1015, 1016, 1018, 1019, 1020, 1021, 1022, 1027, 1028, 1029,
            1167, 1171, 1172, 1173, 1174, 1175, 1177, 1181,
        };
        internal static int[] ExcludeCoveredFaces(int[] indices)
        {
            var removed = new HashSet<int>(Covered);
            removed.UnionWith(StockAvatarSleeveCoverage.CoveredFaces);
            var result = new List<int>(indices.Length);
            for (int face = 0; face < indices.Length / 3; face++)
                if (!removed.Contains(face))
                    for (int corner = 0; corner < 3; corner++) result.Add(indices[face * 3 + corner]);
            return result.ToArray();
        }
    }
}
