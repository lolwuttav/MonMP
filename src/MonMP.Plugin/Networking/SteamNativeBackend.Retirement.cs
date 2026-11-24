using System;
using System.Collections.Generic;

namespace MonMP.Plugin.Networking
{
    internal sealed partial class SteamNativeBackend
    {
        private static readonly HashSet<SteamNativeBackend> delayedCleanup = new HashSet<SteamNativeBackend>();
        // Let the game-owned callback pump finish ordinary cancellations first. A lost
        // callback can delay a retry for five seconds. Keep listening for late membership
        // for a further two minutes, including while a replacement session is running.
        private const int RetryGraceSeconds = 5, ResultRetentionSeconds = 125;
        private static SteamNativeBackend? activeBackend;
        private double retiredAt;
        private ulong requestedLobby;
        internal static int PendingCleanupCount => delayedCleanup.Count;

        private static bool CleanupBlocksRetry()
        {
            double now = CleanupNow;
            foreach (var retired in delayedCleanup)
                if (now - retired.retiredAt < RetryGraceSeconds) return true;
            return false;
        }

        private void RetirePendingLobby()
        {
            if (ReferenceEquals(activeBackend, this)) activeBackend = null;
            if (creatingLobby || joiningLobby)
            {
                retiredAt = CleanupNow;
                delayedCleanup.Add(this);
            }
        }

        private void LeaveRetiredLobby(ulong lobby)
        {
            // Steam membership belongs to the account. A late success for an older join
            // must not eject a newer session that requested or already owns the same lobby.
            if (activeBackend != null && (activeBackend.requestedLobby == lobby || activeBackend.joinedLobbies.Contains(lobby)))
                activeBackend.joinedLobbies.Add(lobby);
            else
            {
                try { LeaveLobby(lobby); RetiredLobbyLeaveCalls++; }
                catch (Exception ex) { ReportRetiredLobbyFailure(ex); }
            }
        }

        internal static void PollRetiredLobbies()
        {
            if (delayedCleanup.Count == 0) return;
            double now = CleanupNow;
            SteamNativeBackend? expired = null;
            foreach (var retired in delayedCleanup)
                if (now - retired.retiredAt >= ResultRetentionSeconds) { expired = retired; break; }
            if (expired == null) return;
            try
            {
                ulong lobby = expired.RecoverRetiredLobby();
                if (lobby != 0) expired.LeaveRetiredLobby(lobby);
            }
            finally
            {
                expired.creatingLobby = expired.joiningLobby = false;
                expired.FinishDelayedCleanup();
            }
        }

        private void FinishDelayedCleanup()
        {
            if (creatingLobby || joiningLobby) return;
            delayedCleanup.Remove(this); created.Dispose(); entered.Dispose();
        }
    }
}
