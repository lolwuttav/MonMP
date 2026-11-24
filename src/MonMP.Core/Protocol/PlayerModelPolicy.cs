using System;

namespace MonMP.Core.Protocol
{
    // Wire identities are append-only. These are local catalog keys, never asset paths.
    public static class PlayerModelPolicy
    {
        private static readonly string[] keys = { "default", "francis", "anthony", "michael", "raymond", "felix", "roger", "jacques",
            "antoine", "gilles", "eric", "normand", "mrbonjour", "melanie", "richard", "martin", "tom", "luc", "bobby", "dahves",
            "liit", "seb", "chef", "police", "carole" };
        public const byte Maximum = 24;
        public static bool Valid(byte model) => model <= Maximum;
        public static string Key(byte model) => keys[Valid(model) ? model : 0];
        public static byte Id(string? key)
        {
            if (key == null || key.Length > 64) return 0;
            key = key.Trim();
            for (byte i = 0; i < keys.Length; i++) if (string.Equals(keys[i], key, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }
    }
}
