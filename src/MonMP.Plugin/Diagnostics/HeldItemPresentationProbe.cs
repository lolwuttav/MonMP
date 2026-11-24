using System;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Read the scene's actual serialized equip transforms. These are first-person item poses,
    // not palm/grip definitions: HandLocation also spawns purchases/newspapers in native code.
    // Keeping both duplicate spanner entries exposes the native variant selection for #176.
    internal static class HeldItemPresentationProbe
    {
        internal static void WriteNativeAnchors(string directory)
        {
            var player = Singleton<Gameplay>.i?.Player;
            if (player == null) throw new InvalidOperationException("Held presentation probe needs the native PlayerInteractable.");
            var locations = AccessTools.Field(typeof(PlayerInteractable), "toolLocations").GetValue(player) as InteractableToolLocation[];
            if (locations == null) throw new InvalidOperationException("Native PlayerInteractable has no serialized toolLocations.");
            var text = new StringBuilder("#176 native serialized first-person equip anchors; read-only snapshot.\n");
            text.AppendLine("Native EquipTool copies Location.localPosition/localRotation directly, using the second AdjustableSpanner match when IntValue1_Max == 1. HoistPuller uses its separate location.");
            text.AppendLine("HandLocation/HandMiddleLocation are native general holding/spawn anchors, not verified hand grips. No per-item palm offsets are inferred here.");
            Append(text, "PlayerInteractable", player.transform);
            Append(text, "HandLocation", player.HandLocation);
            Append(text, "HandMiddleLocation", player.HandMiddleLocation);
            Append(text, "HoistPuller", player.Tool_HoistPuller_Location);
            for (int i = 0; i < locations.Length; i++)
                Append(text, "toolLocations[" + i + "] type=" + locations[i].toolsType, locations[i].Location);
            var active = player.Get_ActiveTool;
            if (active != null)
            {
                text.AppendLine("active=" + active.GetType().Name + "; toolsType=" + active.toolsType + "; equipped=" + active.isEquiped + "; IntValue1_Max=" + active.IntValue1_Max);
                text.AppendLine(DescribeItemScale(active));
                Append(text, "active actual pose (native use may animate it)", active.transform);
            }
            var held = player.Get_HoldingItem;
            if (held != null) { Append(text, "held actual click-relative pose", held.transform); text.AppendLine(DescribeItemScale(held)); }
            var visual = AccessTools.Field(typeof(PlayerInteractable), "visualHands").GetValue(player) as PlayerVisualHands;
            if (visual != null)
            {
                var animator = AccessTools.Field(typeof(PlayerVisualHands), "animator").GetValue(visual) as Animator;
                text.AppendLine("native visualHands=" + PathOf(visual.transform) + "; humanoid=" + (animator != null && animator.isHuman));
                if (animator != null && animator.isHuman)
                {
                    Append(text, "native visual left hand", animator.GetBoneTransform(HumanBodyBones.LeftHand));
                    Append(text, "native visual right hand", animator.GetBoneTransform(HumanBodyBones.RightHand));
                }
            }
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "held-native-anchors.txt"), text.ToString());
        }

        private static void Append(StringBuilder text, string label, Transform? transform)
        {
            if (transform == null) { text.AppendLine(label + ": absent"); return; }
            text.AppendLine(label + ": path=" + PathOf(transform) + "; localPosition=" + transform.localPosition.ToString("F5") +
                "; localRotation=" + transform.localRotation.ToString("F5") + "; localScale=" + transform.localScale.ToString("F5") +
                "; worldScale=" + transform.lossyScale.ToString("F5") + "; parentWorldScale=" + (transform.parent == null ? "none" : transform.parent.lossyScale.ToString("F5")));
        }

        // Diagnostic only: native equip can shrink the root for the first-person view while the
        // world model remains full size. Record both provenance and rendered size, not just scale.
        internal static string DescribeItemScale(InteractableItems item)
        {
            if (item == null) return "held scale: absent";
            var root = item.transform;
            Bounds bounds = default;
            int meshes = 0;
            foreach (var renderer in item.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                if (meshes++ == 0) bounds = renderer.bounds; else bounds.Encapsulate(renderer.bounds);
            }
            return "held scale: type=" + item.GetType().Name + "; prefab=" + item.PrefabName +
                "; equipped=" + (item is InteractableTools tool && tool.isEquiped) + "; path=" + PathOf(root) +
                "; local=" + root.localScale.ToString("F5") + "; world=" + root.lossyScale.ToString("F5") +
                "; parentWorld=" + (root.parent == null ? "none" : root.parent.lossyScale.ToString("F5")) +
                "; meshRenderers=" + meshes + "; worldBoundsSize=" + bounds.size.ToString("F5");
        }

        private static string PathOf(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null) { transform = transform.parent; path = transform.name + "/" + path; }
            return path;
        }
    }
}
