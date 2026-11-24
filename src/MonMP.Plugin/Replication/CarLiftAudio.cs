using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Replication
{
    // Audio only: never invoke the guest's hydraulic input/physics methods.
    internal static class CarLiftAudio
    {
        private static readonly FieldInfo Sound = AccessTools.Field(typeof(Furniture_CarLift), "SOUND_CarLift");
        private static readonly FieldInfo Motor = AccessTools.Field(typeof(SOUND_CarLift), "HydraulicMotor");
        private static readonly Dictionary<Furniture_CarLift, float> directions = new Dictionary<Furniture_CarLift, float>();
        internal static void Apply(Furniture_CarLift lift, float direction)
        {
            if (lift == null) return;
            if (direction == 0 && !directions.ContainsKey(lift)) return;
            var sound = Sound.GetValue(lift) as SOUND_CarLift;
            // Spawn snapshots can precede SOUND_CarLift.Start. Retry from the live item pass.
            if (sound == null || Motor.GetValue(sound) == null) return;
            if (directions.TryGetValue(lift, out float previous) && previous == direction) return;
            if (direction > 0) sound.StartHydraulic();
            else if (direction < 0) sound.StartLiftDown();
            else sound.StopHydraulic();
            if (direction == 0) directions.Remove(lift); else directions[lift] = direction;
        }
        internal static void Forget(Furniture_CarLift lift)
        {
            if (!directions.Remove(lift) || lift == null) return;
            if (Sound.GetValue(lift) is SOUND_CarLift sound && sound != null && Motor.GetValue(sound) != null)
            {
                try { sound.StopHydraulic(); }
                catch (System.Exception ex) { UnityEngine.Debug.LogException(ex); }
            }
        }
        internal static void Clear()
        { foreach (var lift in new List<Furniture_CarLift>(directions.Keys)) Forget(lift); directions.Clear(); }
    }
}
