using HarmonyLib;
using MonMP.Core.World;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Garden_DirtPatch), "OnDestroy")]
    internal static class GardenNativeDestroyPatch
    {
        private static void Prefix(Garden_DirtPatch __instance) =>
            Replication.SharedGarden.NativePatchDestroyed(__instance);
    }

    [HarmonyPatch(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.Start))]
    internal static class GardenNativeStartPatch
    {
        private static void Postfix(Garden_DirtPatch __instance) =>
            MonMPPlugin.Instance?.World?.Garden.NativePatchStarted(__instance);
    }

    [HarmonyPatch(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.MouseHover))]
    internal static class GardenOrientationHoverPatch
    {
        private static void Prefix(Garden_DirtPatch __instance) =>
            MonMPPlugin.Instance?.World?.Garden.RefreshGuestOrientation(__instance);
    }

    // Guests never simulate ground patches; the host's minute tick and day roll are authoritative.
    [HarmonyPatch(typeof(Garden_DirtPatch), "Tick")]
    internal static class GardenTickPatch
    {
        private static bool Prefix(Garden_DirtPatch __instance) => MonMPPlugin.Instance?.World?.Garden.GuestBlocks(__instance) != true;
    }
    [HarmonyPatch(typeof(Garden_DirtPatch), "DayChanged")]
    internal static class GardenDayPatch
    {
        private static bool Prefix(Garden_DirtPatch __instance) => MonMPPlugin.Instance?.World?.Garden.GuestBlocks(__instance) != true;
    }
    // A guest's native garden actions run locally as a prediction and are mirrored to the host.
    [HarmonyPatch(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.Plant))]
    internal static class GardenPlantPatch
    {
        private static void Prefix(Garden_DirtPatch __instance, SeedsType _seedsType, out bool __state)
        {
            __state = _seedsType != SeedsType.Useless && __instance.Get_SeedsType == SeedsType.Useless;
            if (__state) MonMPPlugin.Instance?.World?.Garden.Mirror(__instance, GardenOp.Plant, _seedsType);
        }
        // Planting sounds only for the planter natively; everyone else hears it at the mound (#9).
        private static void Postfix(Garden_DirtPatch __instance, bool __state)
        {
            if (__state && __instance.Get_SeedsType != SeedsType.Useless && MonMPPlugin.Instance?.World?.Garden.Applying == false)
                Replication.PresentationReplays.Post(Replication.PresentationKind.GardenPlant, 0, 0, __instance.transform.position);
        }
    }
    [HarmonyPatch(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.Water))]
    internal static class GardenWaterPatch
    {
        private static void Prefix(Garden_DirtPatch __instance) => MonMPPlugin.Instance?.World?.Garden.Mirror(__instance, GardenOp.Water);
    }
    [HarmonyPatch(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.Fertilize))]
    internal static class GardenFertilizePatch
    {
        private static void Prefix(Garden_DirtPatch __instance)
        {
            if (__instance.Get_SeedsType != SeedsType.Useless && !__instance.Get_isFertilized && __instance.Get_GrowPercent < 1f) MonMPPlugin.Instance?.World?.Garden.Mirror(__instance, GardenOp.Fertilize);
        }
        // The fertilizer pack's puff and sound follow an accepted fertilize; others see it too (#9).
        private static void Postfix(Garden_DirtPatch __instance, bool __result)
        {
            if (__result && MonMPPlugin.Instance?.World?.Garden.Applying == false)
                Replication.PresentationReplays.Post(Replication.PresentationKind.GardenFertilize, 0, 0, __instance.transform.position);
        }
    }
    [HarmonyPatch(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.Desinfect))]
    internal static class GardenDesinfectPatch
    {
        private static void Prefix(Garden_DirtPatch __instance)
        {
            if (__instance.Get_isInfested) MonMPPlugin.Instance?.World?.Garden.Mirror(__instance, GardenOp.Desinfect);
        }
    }
    // A guest's spray used its native dose on its own copy of the bottle; the host's copy pays it too.
    [HarmonyPatch(typeof(Tool_PesticideSolution), nameof(Tool_PesticideSolution.MouseDown))]
    internal static class GardenPesticideSprayPatch
    {
        private static void Prefix(Tool_PesticideSolution __instance, out float __state) => __state = __instance.Value1;
        private static void Postfix(Tool_PesticideSolution __instance, float __state)
        {
            if (__instance.Value1 < __state) MonMPPlugin.Instance?.World?.Garden.MirrorSpray();
        }
    }
    [HarmonyPatch(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.Harvest))]
    internal static class GardenHarvestPatch
    {
        private static void Prefix(Garden_DirtPatch __instance)
        {
            if (__instance.Get_GrowPercent >= 1f || __instance.Get_HARDMODE_IsPlantDead) MonMPPlugin.Instance?.World?.Garden.Mirror(__instance, GardenOp.Harvest);
        }
    }
    [HarmonyPatch(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.RemovePlantFromShovel))]
    internal static class GardenClearPatch
    {
        private static void Prefix(Garden_DirtPatch __instance)
        {
            if (__instance.Get_SeedsType != SeedsType.Useless) MonMPPlugin.Instance?.World?.Garden.Mirror(__instance, GardenOp.Clear);
        }
    }
    [HarmonyPatch(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.RemoveWithAShovel))]
    internal static class GardenRemovePatch
    {
        private static void Prefix(Garden_DirtPatch __instance) => MonMPPlugin.Instance?.World?.Garden.Mirror(__instance, GardenOp.Remove);
        private static void Postfix(Garden_DirtPatch __instance) => MonMPPlugin.Instance?.World?.Garden.AfterNative(__instance, GardenOp.Remove);
    }
    [HarmonyPatch(typeof(Tool_DirtShovel), nameof(Tool_DirtShovel.MouseDown))]
    internal static class GardenDigPatch
    {
        private static bool Prefix(Tool_DirtShovel __instance)
        {
            ShovelCamera.Ensure(__instance);
            return MonMPPlugin.Instance?.Session?.IsRunning != true || !SharedDig(__instance);
        }
        private static void Postfix() => MonMPPlugin.Instance?.World?.Garden.AfterShovel();

        // Both peers need the same clearance check: native overlap counts triggers and player/tool
        // colliders as obstacles. Retry only when that is the sole obstruction; solid world objects,
        // the native terrain/range and home-land checks remain. Offline digging stays native.
        private static float lastLog;
        private static bool SharedDig(Tool_DirtShovel shovel)
        {
            var camera = UnityEngine.Camera.main;
            if (camera == null || !UnityEngine.Physics.Raycast(MonMP.Plugin.Integration.LocalInteraction.SelectionRay(camera), out var hit, 3f)) return false;
            if (hit.collider.gameObject.layer != UnityEngine.LayerMask.NameToLayer("Ground") || !(hit.collider is UnityEngine.TerrainCollider)) return false;
            var native = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(UnityEngine.Physics.OverlapSphere(hit.point, 0.5f), c =>
                c != shovel.itemCollider && !(c is UnityEngine.TerrainCollider) && c.tag != "Player" && c.tag != "Water" && c.tag != "Leo"));
            if (native.Length == 0) return false;
            var player = Singleton<Gameplay>.i?.Player;
            var solid = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(native, c => !c.isTrigger &&
                !c.transform.root.name.StartsWith("MonMP", System.StringComparison.Ordinal) && c.transform.root != shovel.transform.root &&
                (player == null || c.transform.root != player.transform.root)));
            if (UnityEngine.Time.unscaledTime > lastLog + 2f)
            {
                lastLog = UnityEngine.Time.unscaledTime;
                UnityEngine.Debug.Log("MonMP shovel near: " + string.Join(", ", System.Linq.Enumerable.Select(native, c =>
                    MonMP.Plugin.Replication.SharedBuildingDoors.StablePath(c.transform) + (c.isTrigger ? " (trigger)" : "") + " layer " + c.gameObject.layer)));
            }
            if (solid.Length > 0) return false;
            if (!Singleton<Gameplay>.i!.IsOnHomeLand(hit.point)) return false;
            var patch = UnityEngine.Object.Instantiate(Singleton<GameAssets>.i.Garden_DirtPatch);
            patch.transform.position = hit.point;
            var forward = camera.transform.position - hit.point; forward.y = 0f;
            patch.transform.rotation = UnityEngine.Quaternion.LookRotation(forward);
            shovel.GetComponent<SOUND_Tool_DirtShovel>()?.Shovel();
            return true;
        }
    }
    // Each dig, clear and removal plays the shovel sound for the digger alone natively (#9).
    [HarmonyPatch(typeof(SOUND_Tool_DirtShovel), nameof(SOUND_Tool_DirtShovel.Shovel))]
    internal static class ShovelSoundRelayPatch
    {
        private static void Postfix(SOUND_Tool_DirtShovel __instance)
        {
            if (__instance != null) Replication.PresentationReplays.Post(Replication.PresentationKind.GardenShovel, 0, 0, __instance.transform.position);
        }
    }
    // The shovel caches Camera.main only in Equiped. A replicated shovel the guest right-clicks
    // before ever equipping it would raycast from a null camera.
    [HarmonyPatch(typeof(Tool_DirtShovel), nameof(Tool_DirtShovel.MouseRightDown))]
    internal static class ShovelRightClickCameraPatch
    {
        private static void Prefix(Tool_DirtShovel __instance) => ShovelCamera.Ensure(__instance);
    }
    internal static class ShovelCamera
    {
        private static readonly System.Reflection.FieldInfo Field = HarmonyLib.AccessTools.Field(typeof(Tool_DirtShovel), "mainCamera");
        internal static void Ensure(Tool_DirtShovel shovel)
        {
            if (shovel != null && Field != null && Field.GetValue(shovel) as UnityEngine.Camera == null) Field.SetValue(shovel, UnityEngine.Camera.main);
        }
    }
}
