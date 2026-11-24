using System;

namespace MonMP.Plugin.Replication
{
    // Reverse-facing copies only around the native thin cloth shoulder/upper-arm folds.
    // These are face indices into the hash-guarded UV topology, not native geometry.
    internal static class StockAvatarShirtBacking
    {
        internal static readonly int[] ShoulderFaces = {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 36, 38, 41, 42,
            46, 47, 49, 53, 57, 58, 59, 60, 63, 64, 65, 66, 67, 71, 72, 73,
            75, 76, 77, 78, 79, 80, 83, 84, 89, 90, 95, 96, 176, 177, 178, 183,
            184, 185, 186, 187, 188, 191, 192, 193, 194, 195, 197, 199, 200, 201, 202, 210,
            211, 215, 216, 219, 220, 228, 230, 231, 232, 233, 234, 235, 236, 238, 240, 242,
            248, 249, 250, 251, 252, 253, 254, 255, 256, 257, 258, 259, 260, 261, 262, 263,
            264, 265, 266, 267, 268, 269, 270, 271, 272, 273, 274, 275, 276, 277, 278, 279,
            280, 282, 283, 284, 285, 355, 356, 357, 358, 359, 360, 361, 362, 363, 364, 365,
            366, 367, 368, 369, 370, 371, 372, 373, 374, 376, 377, 380, 381, 382, 383, 442,
            443, 444, 445, 450, 451, 452, 453, 456, 457, 458, 459, 460, 484, 485, 486, 487,
            488, 489, 490, 491,
        };

        internal static int[] Triangles()
        {
            var original = StockAvatarShirtUv.Triangles;
            var result = new int[original.Length + ShoulderFaces.Length * 3];
            Array.Copy(original, result, original.Length);
            int cursor = original.Length;
            foreach (int face in ShoulderFaces)
            {
                result[cursor++] = original[face * 3 + 2];
                result[cursor++] = original[face * 3 + 1];
                result[cursor++] = original[face * 3];
            }
            return result;
        }
    }
}
