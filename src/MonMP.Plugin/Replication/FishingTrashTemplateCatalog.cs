using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The fishing result list is serialized on GameAssets rather than expressed as resource names.
    // Carry only its small array index over the network, then resolve it against this machine's list.
    internal static class FishingTrashTemplateCatalog
    {
        private static readonly FieldInfo? TemplatesField = AccessTools.Field(typeof(GameAssets), "Fishing_Trash");

        private static GameObject[] Templates
        {
            get
            {
                try { return TemplatesField?.GetValue(Singleton<GameAssets>.i) as GameObject[] ?? Array.Empty<GameObject>(); }
                catch { return Array.Empty<GameObject>(); }
            }
        }

        internal static bool TryIndex(GameObject result, out ushort index)
        {
            index = ushort.MaxValue;
            if (result == null) return false;
            var templates = Templates;
            if (templates.Length == 0 || templates.Length >= ushort.MaxValue) return false;
            var resultItem = result.GetComponent<InteractableItems>();
            string resultName = BaseName(result.name);
            for (int i = 0; i < templates.Length; i++)
            {
                var template = templates[i];
                if (template == null || !string.Equals(template.name, resultName, StringComparison.Ordinal)) continue;
                var templateItem = template.GetComponent<InteractableItems>();
                if (resultItem != null && (templateItem == null || resultItem.GetType() != templateItem.GetType() ||
                    !string.Equals(resultItem.PrefabName, templateItem.PrefabName, StringComparison.Ordinal))) continue;
                if (resultItem == null && templateItem != null) continue;
                index = (ushort)i;
                return true;
            }
            return false;
        }

        internal static bool IsValidIndex(ushort index) => index != ushort.MaxValue && index < Templates.Length;

        internal static GameObject? TemplateAt(ushort index)
        {
            var templates = Templates;
            return index != ushort.MaxValue && index < templates.Length ? templates[index] : null;
        }

        internal static bool Matches(ushort index, Type type, string prefabPath)
        {
            var templates = Templates;
            if (index == ushort.MaxValue || index >= templates.Length) return false;
            var item = templates[index] == null ? null : templates[index].GetComponent<InteractableItems>();
            return item != null && item.GetType() == type && string.Equals(item.PrefabName, prefabPath, StringComparison.Ordinal);
        }

        private static string BaseName(string name)
        {
            const string clone = "(Clone)";
            name = name ?? string.Empty;
            return name.EndsWith(clone, StringComparison.Ordinal) ? name.Substring(0, name.Length - clone.Length).TrimEnd() : name;
        }
    }
}
