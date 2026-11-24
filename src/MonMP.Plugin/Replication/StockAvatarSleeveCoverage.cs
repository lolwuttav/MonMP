using System;

namespace MonMP.Plugin.Replication
{
    // Face IDs only, measured on the stock Actor mesh (sharedassets2, mesh1822).
    // Both shoulder patches are underneath the long sleeves; neck/head/hand skin is excluded.
    internal static class StockAvatarSleeveCoverage
    {
        internal const int VertexCount = 971, TriangleCount = 1388;
        internal const ulong PositionHash = 0xADF366ED28D28EC4UL, IndexHash = 0xA481B976E96E1906UL;
        internal static readonly int[] CoveredFaces = {
            242, 247, 248, 249, 251, 254, 271, 272, 273, 274, 289, 290, 295, 296, 301, 302,
            307, 308, 309, 310, 311, 316, 317, 326, 335, 336, 337, 338, 343, 344, 345, 357,
            358, 359, 360, 361, 362, 363, 364, 365, 366, 367, 368, 369, 370, 371, 372, 373,
            374, 375, 376, 377, 378, 379, 380, 381, 382, 383, 384, 385, 387, 391, 392, 402,
            403, 406, 409, 410, 411, 412, 413, 424, 425, 426, 427, 428, 463, 464, 719, 720,
            721, 728, 729, 730, 731, 736, 737, 947, 948, 950, 960, 961, 966, 967, 972, 973,
            978, 979, 980, 981, 982, 987, 988, 997, 1004, 1005, 1006, 1008, 1009, 1010, 1011, 1012,
            1013, 1014, 1015, 1016, 1017, 1018, 1023, 1024, 1025, 1026, 1030, 1031, 1032, 1033, 1034, 1035,
            1036, 1037, 1038, 1039, 1040, 1041, 1042, 1043, 1044, 1045, 1046, 1047, 1048, 1050, 1054, 1055,
            1065, 1066, 1069, 1072, 1073, 1074, 1075, 1076, 1087, 1088, 1089, 1090, 1091, 1126, 1127, 1382,
            1383, 1385,
        };

        internal static int[] ExcludeCoveredFaces(int[] original)
        {
            if (original.Length != TriangleCount * 3) throw new ArgumentException("Unsupported stock body topology.");
            var result = new int[original.Length - CoveredFaces.Length * 3];
            int cursor = 0, next = 0;
            for (int face = 0; face < TriangleCount; face++)
            {
                if (next < CoveredFaces.Length && CoveredFaces[next] == face) { next++; continue; }
                Array.Copy(original, face * 3, result, cursor, 3);
                cursor += 3;
            }
            return result;
        }
    }
}
