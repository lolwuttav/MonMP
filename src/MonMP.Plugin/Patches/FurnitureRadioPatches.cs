using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Furniture_Radio), nameof(Furniture_Radio.UpdateScreen))]
    internal static class FurnitureRadioCdScreenPatch
    {
        private static void Postfix(Furniture_Radio __instance) => MonMPPlugin.Instance?.World?.FurnitureRadios.RefreshCdScreen(__instance);
    }

    // Guests route radio buttons to the host; vehicle and generator forced power changes are host-driven.
    [HarmonyPatch(typeof(Furniture_Radio), nameof(Furniture_Radio.Button_Power))]
    internal static class FurnitureRadioPowerPatch
    {
        private static bool Prefix(Furniture_Radio __instance, bool forceOn, bool forceOff) =>
            MonMPPlugin.Instance?.World?.FurnitureRadios.AllowNative(__instance, FurnitureRadioButton.Power, 0, forceOn || forceOff) ?? true;
    }
    [HarmonyPatch(typeof(Furniture_Radio), nameof(Furniture_Radio.Button_Volume))]
    internal static class FurnitureRadioVolumePatch
    {
        private static bool Prefix(Furniture_Radio __instance, float value) =>
            MonMPPlugin.Instance?.World?.FurnitureRadios.AllowNative(__instance, FurnitureRadioButton.Volume, UnityEngine.Mathf.Clamp(value, -100f, 100f), false) ?? true;
    }
    [HarmonyPatch(typeof(Furniture_Radio), nameof(Furniture_Radio.Button_Presets))]
    internal static class FurnitureRadioPresetPatch
    {
        private static bool Prefix(Furniture_Radio __instance, int _index) =>
            _index < 1 || _index > 6 || (MonMPPlugin.Instance?.World?.FurnitureRadios.AllowNative(__instance, FurnitureRadioButton.Preset, _index, false) ?? true);
    }
    [HarmonyPatch(typeof(Furniture_Radio), nameof(Furniture_Radio.Button_FMCD))]
    internal static class FurnitureRadioBandPatch
    {
        private static bool Prefix(Furniture_Radio __instance) =>
            MonMPPlugin.Instance?.World?.FurnitureRadios.AllowNative(__instance, FurnitureRadioButton.Band, 0, false) ?? true;
    }
    [HarmonyPatch(typeof(Furniture_Radio), nameof(Furniture_Radio.Button_SeekNext))]
    internal static class FurnitureRadioSeekNextPatch
    {
        private static bool Prefix(Furniture_Radio __instance) =>
            MonMPPlugin.Instance?.World?.FurnitureRadios.AllowNative(__instance, FurnitureRadioButton.SeekNext, 0, false) ?? true;
    }
    [HarmonyPatch(typeof(Furniture_Radio), nameof(Furniture_Radio.Button_SeekPrev))]
    internal static class FurnitureRadioSeekPreviousPatch
    {
        private static bool Prefix(Furniture_Radio __instance) =>
            MonMPPlugin.Instance?.World?.FurnitureRadios.AllowNative(__instance, FurnitureRadioButton.SeekPrevious, 0, false) ?? true;
    }
    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.Tick))]
    internal static class RadioStationClockPatch
    {
        private static void Prefix(SOUND_Manager __instance) => MonMPPlugin.Instance?.World?.FurnitureRadios.HoldGuestStations(__instance);
    }

    [HarmonyPatch]
    internal static class RadioCdPlaybackPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(SOUND_Manager), "PlayStationOnThisRadio", new[] { typeof(SOUND_Radio) })!;
        private static bool Prefix(SOUND_Manager __instance, SOUND_Radio __0) =>
            MonMPPlugin.Instance?.World?.FurnitureRadios.AllowNativeCdPlayback(__instance, __0) ?? true;
    }

    [HarmonyPatch(typeof(Store_Bar), nameof(Store_Bar.UpdateRadio))]
    internal static class BarRadioPlaybackBoundaryPatch
    {
        private static bool Prefix(Store_Bar __instance) =>
            MonMPPlugin.Instance?.World?.FurnitureRadios.AllowNativeBarPlayback(__instance) ?? true;
    }

    [HarmonyPatch(typeof(SOUND_Radio), nameof(SOUND_Radio.ButtonPressed))]
    internal static class FurnitureRadioButtonSoundRelayPatch
    {
        private static void Postfix(SOUND_Radio __instance) => MonMPPlugin.Instance?.World?.FurnitureRadios.NativeButtonPressed(__instance);
    }

    // BuildPlayerMusicList starts one native async LoadMusic coroutine per exact CDPlayer file.
    // The game names clips SongN by completion order, so track the list-count increment while that
    // exact enumerator is advanced instead of guessing from a later directory re-enumeration.
    [HarmonyPatch]
    internal static class RadioCdLoadMusicPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(SOUND_Manager), "LoadMusic", new[] { typeof(string), typeof(AudioType) })!;

        private static void Postfix(SOUND_Manager __instance, string __0, ref IEnumerator __result)
        {
            if (__instance != null && !string.IsNullOrWhiteSpace(__0) && __result != null)
                __result = new TrackLoadEnumerator(__instance, __0, __result);
        }

        private sealed class TrackLoadEnumerator : IEnumerator, IDisposable
        {
            private readonly SOUND_Manager manager;
            private readonly string sourcePath;
            private readonly IEnumerator inner;
            private readonly Action<AudioClip>? manuallyLoaded = CdManualLoadScope<AudioClip>.CurrentTrackLoaded;
            private readonly Action? finished = MonMPPlugin.Instance?.World?.FurnitureRadios.BeginNativeCdTrackLoad();
            private readonly long sourceLength, sourceWriteTicks;
            public TrackLoadEnumerator(SOUND_Manager manager, string sourcePath, IEnumerator inner)
            {
                this.manager = manager; this.sourcePath = sourcePath; this.inner = inner;
                try
                {
                    var info = new FileInfo(sourcePath); info.Refresh();
                    if (info.Exists) { sourceLength = info.Length; sourceWriteTicks = info.LastWriteTimeUtc.Ticks; }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException) { }
            }
            public object? Current => inner.Current;
            public bool MoveNext()
            {
                int before = RadioCdNative.CdPlayer(manager)?.Count ?? 0;
                bool more;
                try { more = inner.MoveNext(); }
                catch { finished?.Invoke(); throw; }
                var tracks = RadioCdNative.CdPlayer(manager);
                if (tracks != null && tracks.Count == before + 1)
                {
                    var clip = tracks[before];
                    if (clip != null)
                    {
                        RadioCdNative.TrackSources.Associate(clip, sourcePath, sourceLength, sourceWriteTicks);
                        manuallyLoaded?.Invoke(clip);
                        if (MonMPPlugin.Instance?.World?.FurnitureRadios.Guest != true)
                            RadioCdNative.ResumeLoadedTrack(manager, clip);
                    }
                }
                if (!more) finished?.Invoke();
                return more;
            }
            public void Reset() => inner.Reset();
            public void Dispose() { try { (inner as IDisposable)?.Dispose(); } finally { finished?.Invoke(); } }
        }
    }

    [HarmonyPatch(typeof(EscapeMenu_GeneralV2), "LoadCD")]
    internal static class RadioCdManualLoadPatch
    {
        private static void Postfix(TMPro.TextMeshProUGUI ___CDPlayer_SongLoaded, ref IEnumerator __result)
        {
            if (__result != null) __result = new CdManualLoadScope<AudioClip>(__result,
                MonMPPlugin.Instance?.World?.FurnitureRadios.BeginManualCdLoad(), () =>
                {
                    if (___CDPlayer_SongLoaded != null)
                        MonMPPlugin.Instance?.World?.FurnitureRadios.ShowCdSongCount(___CDPlayer_SongLoaded);
                });
        }
    }
    [HarmonyPatch(typeof(EscapeMenu_GeneralV2), nameof(EscapeMenu_GeneralV2.UpdatePage))]
    internal static class RadioCdSongCountPatch
    {
        private static void Postfix(TMPro.TextMeshProUGUI ___CDPlayer_SongLoaded)
        {
            NativeCdMenuBinding.Remember(___CDPlayer_SongLoaded);
            MonMPPlugin.Instance?.World?.FurnitureRadios.ShowCdSongCount(___CDPlayer_SongLoaded);
        }
    }
    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.BuildPlayerMusicList))]
    internal static class RadioCdPlaylistRebuiltPatch
    {
        private static void Prefix() => MonMPPlugin.Instance?.World?.FurnitureRadios.NativeCdPlaylistRebuilding();
        private static void Postfix() => MonMPPlugin.Instance?.World?.FurnitureRadios.NativeCdPlaylistRebuilt();
    }
}
