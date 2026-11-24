using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.Protocol;

namespace MonMP.Plugin.Replication
{
    // Bounded native appearance sources. Numeric wire keys live in PlayerModelPolicy.
    internal static class NpcAvatarModels
    {
        internal sealed class Model
        {
            internal readonly string Key;
            internal readonly string[] Roots;
            internal Model(string key, params string[] roots) { Key = key; Roots = roots; }
        }
        internal static readonly Model[] Catalog = {
            new Model("default"), new Model("francis", "Actors_Francis"),
            new Model("anthony", "Actors_Anthony"), new Model("michael", "Actor_Michael_Standing", "Actor_Michael"),
            new Model("raymond", "Actor_Raymond"), new Model("felix", "Actors_Felix"),
            new Model("roger", "Actors_Roger"), new Model("jacques", "Actors_Jacques"),
            new Model("antoine", "Actors_Antoine"), new Model("gilles", "Actors_Gilles"),
            new Model("eric", "Actors_Eric"), new Model("normand", "Actors_Normand"),
            new Model("mrbonjour", "Actor_MrBonjour"), new Model("melanie", "ActorsF_Melanie"),
            new Model("richard", "Actor_BusDriver"), new Model("martin", "Actor_Martin"),
            new Model("tom", "Actor_Legend_Tom_Standing"), new Model("luc", "Actor_Drift_Luc_Standing"),
            new Model("bobby", "Actor_Bobby"), new Model("dahves", "Actor_Dahves"),
            new Model("liit", "Actor_Liit"), new Model("seb", "Actor_Seb"),
            new Model("chef", "Actors_Chef"), new Model("police", "Actor_Police"),
            new Model("carole", "Actor_Carole")
        };
        private static readonly string[] labels = { "Default", "Francis", "Anthony", "Michael", "Raymond", "Félix", "Roger", "Jacques",
            "Antoine", "Gilles", "Éric", "Normand", "Mr. Bonjour", "Mélanie", "Richard", "Martin", "Tom", "Luc", "Bobby", "Dahves", "Liit",
            "Seb", "Chef", "Police", "Carole" };
        internal static string Label(byte id) => labels[PlayerModelPolicy.Valid(id) ? id : 0];
        internal static string Normalize(string? key) => Catalog.FirstOrDefault(m =>
            string.Equals(m.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase))?.Key ?? "default";
        internal static Model Get(string? key) => Catalog.First(m => m.Key == Normalize(key));
        internal static bool Clothing(string name) => name == "Actor" || name == "Actor.001" ||
            name.StartsWith("Hair", StringComparison.Ordinal) || name.StartsWith("Pants_", StringComparison.Ordinal) ||
            name.StartsWith("Shoes", StringComparison.Ordinal) || name.StartsWith("Shirt_", StringComparison.Ordinal) || Headwear(name);
        internal static bool Headwear(string name) => name.StartsWith("Hat_", StringComparison.Ordinal) ||
            name.StartsWith("Casque", StringComparison.Ordinal) || name == "Cuisinier" || name == "Police_Cap" || name == "Actors_Police_Cap" ||
            name.StartsWith("Glasses", StringComparison.Ordinal);
    }
}
