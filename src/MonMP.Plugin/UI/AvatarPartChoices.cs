using System;
using System.Collections.Generic;
using System.Globalization;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.UI
{
    // Representative native shapes/materials. Legacy whole-NPC IDs still render;
    // the menu avoids cycling through dozens of identical shirts and trousers.
    internal static class AvatarPartChoices
    {
        private static readonly byte[][] ids = {
            new byte[] { 0, 7, 17, 13, 24, 18, 19, 20, 21, 22, 23 }, new byte[] { 0, 4, 13, 18, 20, 22, 23, 24 },
            new byte[] { 0, 12, 3, 8, 13, 18, 19, 21, 22, 23, 24 },
            new byte[] { 0, 1, 3, 13, 18, 19, 22, 23 }
        };
        private static readonly string[][] labels = {
            new[] { "Short dark hair", "Short grey hair", "Short blond hair", "Long blond hair", "Long dark hair", "Mullet", "Side-parted brown hair", "Swept dark hair", "Swept blond hair", "Cap", "Police cap" }, new[] { "Long sleeves", "Short sleeves", "Fitted V-neck", "Relaxed short sleeves", "Loose shirt", "Wide shirt", "Uniform", "Fitted collared shirt" },
            new[] { "Jeans", "Black trousers", "Black shorts", "Denim shorts", "Fitted trousers", "Relaxed jeans", "Relaxed black trousers", "Brown trousers", "Wide jeans", "Uniform trousers", "Skirt" },
            new[] { "Default", "Black shoes", "Brown shoes", "Fitted shoes", "Chunky black shoes", "Chunky brown shoes", "Wide shoes", "Uniform shoes" }
        };
        internal static IReadOnlyList<byte> HeadStyles { get; } = Array.AsReadOnly(ids[0]);
        internal static IReadOnlyList<byte> TorsoStyles { get; } = Array.AsReadOnly(ids[1]);
        internal static string UpperPortrait(byte torso, byte style, bool back, string part) => "upper-" +
            Representative(1, torso).ToString("D2", CultureInfo.InvariantCulture) + "-" +
            Representative(0, style).ToString("D2", CultureInfo.InvariantCulture) + (back ? "-back-" : "-front-") + part + ".png";
        internal static byte Representative(int part, byte id)
        {
            if (part == 0)
            {
                if (id == 14) return 7;
                return Array.IndexOf(ids[0], id) >= 0 ? id : (byte)0;
            }
            if (part == 1)
            {
                if (id == 3 || id == 5 || id == 7 || id == 10 || id == 14 || id == 15 || id == 16 || id == 17) return 4;
                if (id == 19 || id == 21) return 18;
                if (Array.IndexOf(ids[part], id) < 0) return 0;
            }
            if (part == 2)
            {
                if (id == 15) return 3;
                if (Array.IndexOf(ids[part], id) < 0) return 0;
            }
            if (part == 3)
            {
                if (id == 24) return 13;
                if (id == 21) return 19;
                if (id == 4 || id == 5 || id == 6 || id == 8 || id == 14 || id == 15 || id == 16 || id == 17) return 3;
                if (Array.IndexOf(ids[part], id) < 0) return 1;
            }
            return id;
        }
        internal static byte Cycle(int part, byte current, int direction)
        {
            int index = Array.IndexOf(ids[part], Representative(part, current));
            return ids[part][(index + direction + ids[part].Length) % ids[part].Length];
        }
        internal static string Label(int part, byte current) => labels[part][Array.IndexOf(ids[part], Representative(part, current))];
    }
}
