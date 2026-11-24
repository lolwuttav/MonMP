using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Native Start creates these sources after a replica can already have its flags.
    // Reconcile actual playback, including child objects reactivated by distance loading.
    internal static class SugarShackPresentation
    {
        internal static readonly string[] AudioFields = { "ElectricMotor", "BoilingWater", "RunningWater_ValveEvap", "RunningWater_ValveSyrup",
            "Evap_Valve_On", "Evap_Valve_Off", "Syrup_Valve_On", "Syrup_Valve_Off", "SyrupPress" };
        private static readonly FieldInfo[] sources = Array.ConvertAll(AudioFields, name => AccessTools.Field(typeof(SOUND_SugarShack), name));
        internal static bool Ready(SOUND_SugarShack? sound)
        {
            if (sound == null) return false;
            foreach (var field in sources) if (!(field.GetValue(sound) is AudioSource source) || source == null) return false;
            return true;
        }
        internal static void Reconcile(SOUND_SugarShack? sound, bool vacuum, bool water, bool syrup, bool boiling)
        {
            if (sound == null || !Ready(sound)) return;
            Loop(sound!, 0, vacuum, sound!.StartVacuumPump, sound.StopVacuumPump);
            Loop(sound!, 1, boiling, sound.StartBoiling, sound.StopBoiling);
            Loop(sound!, 2, water, sound.ValveEvap_Start, sound.ValveEvap_Stop);
            Loop(sound!, 3, syrup, sound.ValveSyrup_Start, sound.ValveSyrup_Stop);
        }
        private static void Loop(SOUND_SugarShack sound, int index, bool on, Action start, Action stop)
        {
            if (!(sources[index].GetValue(sound) is AudioSource source) || source == null || !source.gameObject.activeInHierarchy) return;
            if (on && !source.isPlaying) start();
            else if (!on && source.isPlaying) stop();
        }
    }
}
