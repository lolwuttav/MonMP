using System.Collections.Generic;
using HarmonyLib;
using NWH.VehiclePhysics2.Sound;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // NWH snapshots the vehicle mixer's "attenuation" once when a vehicle initializes and writes that
    // value (or a fixed -5 dB interior level) back whenever the camera enters or leaves a vehicle. A
    // multiplayer session initializes and swaps vehicles long after Gameplay applied the player's
    // Vehicles volume, so the first seat change reset it to 0 dB: full volume regardless of the vanilla
    // option (#121). Keep both levels relative to the player's own setting.
    [HarmonyPatch(typeof(SoundManager), nameof(SoundManager.Update))]
    internal static class VehicleVolumePatch
    {
        private static readonly AccessTools.FieldRef<SoundManager, float> Original =
            AccessTools.FieldRefAccess<SoundManager, float>("_originalAttenuation");
        private static readonly Dictionary<SoundManager, float> nativeInterior = new Dictionary<SoundManager, float>();
        private static int frame = -1;
        private static float userDb;

        internal static float UserDb()
        {
            if (frame == Time.frameCount) return userDb;
            frame = Time.frameCount;
            float value;
#if MONMP_LOCAL_TEST_ISOLATION
            if (Testing.LocalTestIsolation.Installed)
                value = Testing.LocalTestIsolation.HasKey("Audio_VehiclesVolume") ? Mathf.Clamp01(Testing.LocalTestIsolation.GetInt("Audio_VehiclesVolume") / 100f) : 1f;
            else
#endif
                value = PlayerPrefs.HasKey("Audio_VehiclesVolume") ? Mathf.Clamp01(PlayerPrefs.GetInt("Audio_VehiclesVolume") / 100f) : 1f;
            return userDb = value > 0f ? Mathf.Log10(value) * 20f : -80f;
        }

        private static void Prefix(SoundManager __instance)
        {
            if (MonMPPlugin.Instance?.Session?.IsRunning != true || __instance == null) return;
            float db = UserDb();
            Original(__instance) = db;
            if (!nativeInterior.TryGetValue(__instance, out float interior))
            {
                if (nativeInterior.Count > 256) nativeInterior.Clear();
                nativeInterior[__instance] = interior = __instance.interiorAttenuation;
            }
            __instance.interiorAttenuation = Mathf.Max(-80f, db + interior);
            if (__instance.VehicleController != null && __instance.VehicleController.cameraInsideVehicle)
            { insideFrame = Time.frameCount; insideDb = __instance.interiorAttenuation; }
        }

        private static int insideFrame = -10;
        private static float insideDb;

        // NWH writes the shared mixer only on camera transitions, so any other write (a vehicle
        // initialized before the settings loaded, a swapped seat) left the host at 0 dB whatever the
        // Vehicles slider said (#121, still loud after the first fix). Hold it at the player's level
        // every frame, except while the menu has muted the game. A joining guest's session runs in the
        // main menu, where Singleton<Gameplay>.i would create a blank Gameplay that the world then binds
        // to (stuck on "Loading settings"): take the plugin's found instance instead.
        internal static void Enforce(Gameplay? gameplay)
        {
            if (MonMPPlugin.Instance?.Session?.IsRunning != true) return;
            if (gameplay == null || gameplay.Get_isGamePaused) return;
            var mixer = Singleton<GameAssets>.i?.AUDIO_VehicleMixer?.audioMixer;
            if (mixer == null || !mixer.GetFloat("attenuation", out float current)) return;
            float expected = Expected;
            if (Mathf.Abs(current - expected) > 0.05f) mixer.SetFloat("attenuation", expected);
        }

        internal static float Expected => Time.frameCount - insideFrame <= 1 ? insideDb : UserDb();
    }
}
