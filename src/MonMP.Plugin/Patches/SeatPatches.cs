using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Ordinary native chairs use a serialized seat claim in multiplayer. Offline native interactions
    // keep their existing path, and MonMP-created vehicle seats stay under SharedExtraSeats.
    internal static class SeatExclusivity
    {
        [HarmonyPatch(typeof(Interactable), nameof(Interactable.CanUse))]
        internal static class ExtraChairUsePatch
        {
            private static void Postfix(Interactable __instance, ref bool __result)
            { if (__instance is Furniture_Chair chair && MonMPPlugin.Instance?.World?.ExtraSeats.CanInteract(chair) == false) __result = false; }
        }

        [HarmonyPatch(typeof(Gameplay), nameof(Gameplay.EnterExitChair))]
        internal static class ExtraChairExitPatch
        {
            private static bool Prefix() => MonMPPlugin.Instance?.World?.ExtraSeats.AllowChairTransition() ?? true;
        }
        private static readonly System.Reflection.FieldInfo ChairSeat = AccessTools.Field(typeof(Furniture_Chair), "SeatLocation");
        private static readonly System.Reflection.FieldInfo DecorationSeat = AccessTools.Field(typeof(Furniture_Decoration_Seat), "SeatLocation");

        internal static bool Allow(Transform? seat)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin == null || seat == null || Singleton<Gameplay>.i.Get_PlayerState != PlayerState.Walking) return true;
            // NPCs fill some seats on their schedules; don't sit in their laps.
            if (!plugin.RemoteSeatTaken(seat.position) && !NpcSeated(seat.position)) return true;
            plugin.ReportItemUseUnavailable("Someone is already sitting there.");
            return false;
        }

        private static bool NpcSeated(Vector3 seat)
        {
            foreach (var actor in Replication.SharedExtraSeats.NpcActors())
                if (actor != null && actor.isActiveAndEnabled && actor.gameObject.activeInHierarchy &&
                    Vector3.ProjectOnPlane(actor.transform.position - seat, Vector3.up).sqrMagnitude < 0.45f * 0.45f &&
                    Mathf.Abs(actor.transform.position.y - seat.y) < 1.6f) return true;
            return false;
        }

        [HarmonyPatch(typeof(Furniture_Chair), nameof(Furniture_Chair.MouseDown))]
        internal static class ChairPatch
        {
            private static bool Prefix(Furniture_Chair __instance)
            {
                // MonMP's own seats (truck, extra bar stools) are host-arbitrated.
                var world = MonMPPlugin.Instance?.World;
                var extra = world?.ExtraSeats;
                if (extra != null && extra.Handles(__instance)) return extra.Click(__instance);
                var furnitureSeats = world?.FurnitureSeats;
                if (furnitureSeats?.Active == true) return furnitureSeats.Click(__instance);
                return Allow(ChairSeat.GetValue(__instance) as Transform);
            }
        }

        // Truck seats go by aim (SharedExtraSeats.PickSeat): the seat picked there replaces the native
        // ray's pick, with the native hover bookkeeping, so the highlight stays steady.
        [HarmonyPatch(typeof(PlayerInteractable), "CheckForInteractable")]
        internal static class CrosshairPatch
        {
            private static readonly System.Reflection.FieldInfo Current = AccessTools.Field(typeof(PlayerInteractable), "currentInteractable");
            private static readonly System.Reflection.FieldInfo CurrentItem = AccessTools.Field(typeof(PlayerInteractable), "currentInteractableItem");
            private static readonly System.Reflection.FieldInfo LeftDown = AccessTools.Field(typeof(PlayerInteractable), "isMouseLeftClickDown");
            private static readonly System.Reflection.FieldInfo RightDown = AccessTools.Field(typeof(PlayerInteractable), "isMouseRightClickDown");
            private static readonly System.Reflection.FieldInfo Crosshair = AccessTools.Field(typeof(PlayerInteractable), "ui_Crosshair");
            private static bool Prefix(PlayerInteractable __instance)
            {
                var extra = MonMPPlugin.Instance?.World?.ExtraSeats;
                var camera = __instance.mainCamera;
                if (camera == null) return true;
                // Paint wins over seat/control triggers, but a visible door handle,
                // hood release, trunk button or ordinary tool-compatible interaction
                // still uses native click/hover behavior. Include painted body solids
                // omitted by the native interaction mask so controls behind them lose.
                bool painting = MonMPPlugin.Instance?.Session?.IsRunning == true && __instance.Get_ActiveTool is Tool_SprayPaintCan;
                // Without a VR provider both are the camera-centre ray. With one, selection follows
                // the controller and the paint hover follows the ray the spray payload will carry.
                var ray = painting ? Integration.LocalInteraction.SampleTool(camera).Ray : Integration.LocalInteraction.SelectionRay(camera);
                var pick = MonMPPlugin.Instance?.Vehicles?.PickLocalExitSeat(ray) ?? extra?.PickSeat(ray);
                bool pouring = (__instance.Get_ActiveTool is Tool_FuelCanister || __instance.Get_ActiveTool is Kaligaz_FuelGun) && Replication.ContainerHoleAim.Target(ray);
                if (pouring || painting) pick = null;
                if (painting)
                {
                    int mask = ~LayerMask.GetMask("Player", "Ignore Raycast", "ContainerHole", "SwimmingPlayerSurface", "IgnoreByPlayerInteractable");
                    pick = Replication.SprayPaintRaycast.PickInteraction(ray.origin, ray.direction, mask, __instance.Get_ActiveTool.transform);
                }
                if (pick == null && !pouring && !painting) return true;
                CurrentItem.SetValue(__instance, null);
                var current = Current.GetValue(__instance) as Interactable;
                if (current == pick) return false;
                var crosshair = Crosshair.GetValue(__instance) as UI_Crosshair;
                if (current != null)
                {
                    current.MouseOut();
                    crosshair?.Change(InteractiveCustomCrosshair.Default);
                    if (LeftDown.GetValue(__instance) is true) current.MouseUp();
                    if (RightDown.GetValue(__instance) is true) current.MouseRightUp();
                }
                Current.SetValue(__instance, pick);
                if (pick != null)
                {
                    pick.MouseHover();
                    crosshair?.Change(pick.customCrosshair());
                }
                return false;
            }
        }

        [HarmonyPatch]
        internal static class OccupiedVehicleSeatClickPatch
        {
            private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var type in new[] { typeof(Vehicle_DriverSeat), typeof(Konig_Seat_Button) })
                    yield return AccessTools.Method(type, nameof(Interactable.MouseDown));
            }
            private static bool Prefix(Interactable __instance) =>
                MonMPPlugin.Instance?.Vehicles?.ClickLocalExitSeat(__instance) != true;
        }

        [HarmonyPatch(typeof(Furniture_Decoration_Seat), nameof(Furniture_Decoration_Seat.MouseDown))]
        internal static class DecorationSeatPatch
        {
            private static bool Prefix(Furniture_Decoration_Seat __instance)
            {
                var furnitureSeats = MonMPPlugin.Instance?.World?.FurnitureSeats;
                if (furnitureSeats?.Active == true) return furnitureSeats.Click(__instance);
                return Allow(DecorationSeat.GetValue(__instance) as Transform);
            }
        }
    }
}
