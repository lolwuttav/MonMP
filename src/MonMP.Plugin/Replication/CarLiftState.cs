using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The car lift is a freezable decoration whose native save values are its platform height
    // (Value1) and arm positions (IntValue2). The host runs its hydraulics; replicas follow values.
    internal static class CarLiftState
    {
        private static readonly FieldInfo Platform = AccessTools.Field(typeof(Furniture_CarLift), "Collider_LiftVehicle");
        private static readonly FieldInfo Lower = AccessTools.Field(typeof(Furniture_CarLift), "LiftVehicle_LowerPosition");
        private static readonly FieldInfo Upper = AccessTools.Field(typeof(Furniture_CarLift), "LiftVehicle_HighPosition");
        private static readonly FieldInfo Buttons = AccessTools.Field(typeof(Furniture_CarLift), "Colliders_Button");
        private static readonly FieldInfo Arms = AccessTools.Field(typeof(Furniture_CarLift), "Collider_Arms");
        private static readonly FieldInfo Moving = AccessTools.Field(typeof(Furniture_CarLift), "MovingLift_Direction");
        private static readonly MethodInfo FreezeMode = AccessTools.Method(typeof(Furniture_CarLift), "ChangeFreezeMode");
        internal static bool AtGround(Furniture_CarLift lift) =>
            Vector3.Distance(((Transform)Platform.GetValue(lift)).localPosition, ((Transform)Lower.GetValue(lift)).localPosition) <= 0.05f;
        internal static float LowerHeight(Furniture_CarLift lift) => ((Transform)Lower.GetValue(lift)).localPosition.y;
        internal static float UpperHeight(Furniture_CarLift lift) => ((Transform)Upper.GetValue(lift)).localPosition.y;
        internal static bool IsMoving(Furniture_CarLift lift) => (float)Moving.GetValue(lift) != 0;
        internal static float Direction(Furniture_CarLift lift)
        {
            float moving = (float)Moving.GetValue(lift);
            // Native press can set direction at an endstop without starting the audio.
            if (moving > 0 && lift.Value1 < UpperHeight(lift) - 0.001f) return 1;
            if (moving < 0 && lift.Value1 > LowerHeight(lift) + 0.001f) return -1;
            return 0;
        }
        internal static void Stop(Furniture_CarLift lift)
        {
            try { lift.LiftButtonReleased(); }
            finally { Moving.SetValue(lift, 0f); }
        }
        // Replicas receive the frozen flag as a plain value; give them the native frozen colliders
        // (buttons and arms clickable) exactly once per change.
        internal static void Sync(Furniture_CarLift lift, bool held, ref bool wasHeld)
        {
            bool frozen = lift.IntValue1 == 1;
            var buttons = (Collider[])Buttons.GetValue(lift);
            if (buttons != null && buttons.Length != 0 && buttons[0] != null && buttons[0].enabled != frozen)
            {
                float height = lift.Value1; int arms = lift.IntValue2;
                FreezeMode.Invoke(lift, new object[] { frozen });
                if (frozen) { lift.Value1 = height; lift.IntValue2 = arms; }
            }
            // Native Grab disables all four arm colliders, in addition to the root
            // trigger. A guest pickup does not call Grab on the host/other observers.
            // Restore as native UnGrab does only after an actual hold, preserving
            // the initial/unfreeze collider state when no one has picked it up.
            if (!held && !wasHeld) return;
            foreach (var arm in (BoxCollider[])Arms.GetValue(lift))
                if (arm != null && arm.enabled == held) arm.enabled = !held;
            wasHeld = held;
        }
    }
}
