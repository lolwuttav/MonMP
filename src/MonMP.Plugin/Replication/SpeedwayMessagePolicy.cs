using System;

namespace MonMP.Plugin.Replication
{
    // These are the notices raised by native Speedway start-line entry/cooldown paths.
    // They describe the local driver's eligibility or movement and belong only on that
    // driver's screen; results and other event messages remain shared.
    internal static class SpeedwayMessagePolicy
    {
        internal static bool IsLocalOnlyEntryNotice(byte title, string? text, string notDriving,
            string trialCountdown, string championshipCountdown, string derbyCountdown,
            string derbyVehicle, string derbyFacing)
        {
            if (string.IsNullOrEmpty(text) || title < 1 || title > 5) return false;
            if (!string.IsNullOrEmpty(notDriving) && text.IndexOf(notDriving, StringComparison.Ordinal) >= 0) return true;

            if (title == 5 && (string.Equals(text, derbyVehicle, StringComparison.Ordinal) ||
                string.Equals(text, derbyFacing, StringComparison.Ordinal))) return true;
            string countdown = title <= 3 ? trialCountdown : title == 4 ? championshipCountdown : derbyCountdown;
            return !string.IsNullOrEmpty(countdown) && string.Equals(text, countdown, StringComparison.Ordinal);
        }
    }
}
