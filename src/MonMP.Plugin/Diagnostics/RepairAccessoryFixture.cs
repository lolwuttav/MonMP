using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Explicit alternate fixtures use a host-owned dynamic item's first independent
    // save accessory. They fail clearly if the current save has no eligible target;
    // they never fall back to the generic static-body smoke fixture.
    internal static class RepairAccessoryFixture
    {
        private static Vehicle_PaintablePanel? preparedRustRoot;
        private static PaintPannelData? preparedRustBaseline;

        // Run once after native save loading and before SharedWorld construction, so the
        // host catalog and joining guest both see the staged native accessory baseline.
        internal static void PrepareRust()
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin == null || !plugin.TestOptions.Enabled || !plugin.TestOptions.IsHost || !LocalTestIsolation.Installed ||
                plugin.World != null || plugin.Session?.IsRunning == true)
                throw new InvalidOperationException("Accessory rust preparation requires an isolated host before world creation.");
            if (preparedRustRoot != null || preparedRustBaseline != null)
                throw new InvalidOperationException("Accessory rust preparation already owns a baseline.");
            var parentField = AccessTools.Field(typeof(Vehicle_PaintablePanel), "ParentPanel");
            int mask = LayerMask.GetMask("MainVehicleTrigger", "InteractableItem", "PartSocket", "VehiclePlayerCantGoThrough");
            var candidates = UnityEngine.Object.FindObjectsOfType<Part_FrontGrille>(true)
                .Where(part => part != null && part.gameObject.scene.IsValid() && part.gameObject.activeInHierarchy &&
                    part.PrefabName == "Part/Body_Front_Grill_OEM" &&
                    part.GetComponentInParent<Vehicle_PartsManager>(true)?.Get_Vehicle?.vehicle_type == Vehicle_Type.MainVehicle)
                .Select(part => part.GetComponent<Vehicle_PaintablePanel>())
                .Where(root => root != null && root.PannelAssociatedToSave != null && root.PannelAssociatedToSave != root &&
                    parentField.GetValue(root) == null && parentField.GetValue(root.PannelAssociatedToSave) == null &&
                    root.PannelAssociatedToSave.gameObject.activeInHierarchy && !root.PannelAssociatedToSave.Get_CantBeDent &&
                    root.PannelAssociatedToSave.Get_Material != null)
                .Where(root => root.PannelAssociatedToSave.GetComponent<Collider>() is Collider collider &&
                    collider.enabled && (mask & (1 << collider.gameObject.layer)) != 0)
                .ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException("Rust accessory preparation requires exactly one active native Konig OEM grille with an independent eligible contour collider; found " + candidates.Length + ".");
            preparedRustRoot = candidates[0];
            preparedRustBaseline = new PaintPannelData(preparedRustRoot);
            try
            {
                var accessory = preparedRustRoot.PannelAssociatedToSave;
                if (!accessory.ForceRust() || !accessory.Get_IsRusted || !accessory.Get_ForceRust)
                    throw new InvalidOperationException("Native ForceRust refused the isolated Konig grille contour preparation.");
                var after = new PaintPannelData(preparedRustRoot);
                if (!SameNativeScalars(preparedRustBaseline, after) ||
                    !SameNativeData(preparedRustBaseline.PannelAssociatedToSave.PannelAssociatedToSave,
                        after.PannelAssociatedToSave.PannelAssociatedToSave))
                    throw new InvalidOperationException("Native accessory rust preparation changed its canonical parent or another accessory.");
            }
            catch
            {
                RestoreRust();
                throw;
            }
        }

        internal static void RestoreRust()
        {
            var root = preparedRustRoot;
            var baseline = preparedRustBaseline;
            if (root == null || baseline == null)
            { preparedRustRoot = null; preparedRustBaseline = null; return; }
            root.LoadSaveSystem(baseline);
            if (!SameNativeData(baseline, new PaintPannelData(root)))
                throw new InvalidOperationException("Accessory preparation did not restore its exact native paint baseline.");
            preparedRustRoot = null; preparedRustBaseline = null;
        }
        private static bool SameNativeScalars(PaintPannelData before, PaintPannelData after) =>
            before.PAINT_IsRusted == after.PAINT_IsRusted && before.PAINT_ForceRust == after.PAINT_ForceRust &&
            before.PaintColor.SequenceEqual(after.PaintColor) && before.PaintDetails.SequenceEqual(after.PaintDetails);
        private static bool SameNativeData(PaintPannelData? before, PaintPannelData? after) =>
            before == null ? after == null : after != null && SameNativeScalars(before, after) &&
                SameNativeData(before.PannelAssociatedToSave, after.PannelAssociatedToSave);

        internal static (ulong Id, Vehicle_PaintablePanel Panel, Collider Collider) Find(
            SharedPaintPanels shared, Func<Vehicle_PaintablePanel, bool> eligible)
        {
            int mask = LayerMask.GetMask("MainVehicleTrigger", "InteractableItem", "PartSocket", "VehiclePlayerCantGoThrough");
            foreach (ulong id in shared.TestDynamicPanels.OrderBy(value => value))
            {
                var root = shared.TestNative(id);
                var panel = root.PannelAssociatedToSave;
                if (panel == null || panel == root || !root.gameObject.activeInHierarchy ||
                    !panel.gameObject.activeInHierarchy || !eligible(panel)) continue;
                var collider = panel.GetComponent<Collider>();
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy ||
                    (mask & (1 << collider.gameObject.layer)) == 0) continue;
                return (id, panel, collider);
            }
            throw new InvalidOperationException("Associated repair fixture requires a registered dynamic native item with an eligible first save accessory and a direct collider on the native repair ray mask.");
        }

        internal static void AssertOnlyAccessoryChanged(PaintPanelState baseline, PaintPanelState current)
        {
            if (baseline.Associated == null || current.Associated == null ||
                !ScalarState(baseline).Encode().SequenceEqual(ScalarState(current).Encode()) ||
                !SameTail(baseline.Associated.Associated, current.Associated.Associated))
                throw new InvalidOperationException("Accessory action mutated the canonical parent or another saved accessory.");
        }

        private static bool SameTail(PaintPanelState? before, PaintPanelState? after) =>
            before == null ? after == null : after != null && before.Encode().SequenceEqual(after.Encode());
        private static PaintPanelState ScalarState(PaintPanelState state) => new PaintPanelState(
            state.IsRusted, state.ForceRust, state.Red, state.Green, state.Blue,
            state.DentScale, state.Smoothness, state.Metalness);
    }
}
