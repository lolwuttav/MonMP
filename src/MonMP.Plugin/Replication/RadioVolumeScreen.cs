using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Local presentation of a routed button: native text and native return coroutine,
    // without changing guest audio volume or replaying a second button click.
    internal static class RadioVolumeScreen
    {
        private static readonly FieldInfo Screen = AccessTools.Field(typeof(Furniture_Radio), "ScreenText");
        private static readonly FieldInfo Timer = AccessTools.Field(typeof(Furniture_Radio), "TimerScreenGoBackNormal");
        private static readonly FieldInfo Running = AccessTools.Field(typeof(Furniture_Radio), "CoroutineScreenRunning");
        private static readonly MethodInfo ReturnScreen = AccessTools.Method(typeof(Furniture_Radio), "BringBackNormalScreen", new[] { typeof(bool) })
            ?? throw new MissingMethodException(typeof(Furniture_Radio).FullName, "BringBackNormalScreen");

        internal static bool IsVisible(Furniture_Radio radio) => (float)Timer.GetValue(radio)! > 0f;

        internal static bool Refresh(Furniture_Radio radio, float volume, bool restart)
        {
            if (!restart && !IsVisible(radio)) return false;
            ((TMPro.TextMeshPro)Screen.GetValue(radio)!).text = "VOLUME " + Mathf.Round(Mathf.Clamp(volume, 0f, 1f) * 100f);
            if (restart) Timer.SetValue(radio, 3f);
            if (!(bool)Running.GetValue(radio)!)
                radio.StartCoroutine((IEnumerator)ReturnScreen.Invoke(radio, new object[] { false })!);
            return true;
        }
    }
}
