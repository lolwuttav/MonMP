using MonMP.Core.Protocol;

namespace MonMP.Plugin.Replication
{
    internal static class NativeCheatMenuPolicy
    {
        // SpecialMenu resumes only these shared actions. Teleporting the player is
        // local and keeps its native walking-state guard and menu behavior.
        internal static bool ResumesAfter(CheatFeedback feedback) => !feedback.Error &&
            (feedback.Action == SharedCheatAction.TeleportVehicle || feedback.Action == SharedCheatAction.Weather ||
             feedback.Action == SharedCheatAction.Gravity || feedback.Action == SharedCheatAction.WaterPlants ||
             feedback.Action == SharedCheatAction.DisinfectPlants || feedback.Action == SharedCheatAction.Refuel);
    }
}
