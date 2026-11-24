using System;
using MonMP.Core.Persistence;
using MonMP.Core.Session;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin
{
    public sealed partial class MonMPPlugin
    {
        internal SidecarProfile? CaptureOfflinePersonalState()
        {
            try
            {
                var loadedGameplay = FindObjectOfType<Gameplay>() ?? throw new InvalidOperationException("Loaded gameplay is missing.");
                Guid id = ProfileIdentityPolicy.ReadOrCreate(profileId.Value);
                string name = PlayerNamePolicy.ValidationError(displayName.Value) == null ? displayName.Value : "Player";
                return new SidecarProfile(id, name, ProfileColor(id), DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    new[] { new System.Collections.Generic.KeyValuePair<string, SidecarSection>(PlayerProfileState.SectionKey,
                        NativePlayerProfile.CaptureLoaded(loadedGameplay).ToSection()) });
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Offline personal observation was unavailable: " + ex.Message);
                return null;
            }
        }

        private Guid RecoverHostProfile(Guid requested, string name)
        {
            // StartSession has already excluded a running session. Use the same
            // validated history as guest recovery before peer 0 is constructed.
            Guid resolved = requested;
            try
            {
                if (World == null) return requested;
                resolved = LocalPersonalProfileRecovery.Resolve(requested, name, World.SavedProfileIdentities());
                if (resolved != requested && World.Saves?.Checkpoint(resolved) is PlayerProfileState state)
                {
                    // Ordinary hosting must keep the player's ongoing offline state.
                    // Only a newly recovered identity needs its personal values here.
                    NativePlayerProfile.RestoreLoaded(gameplay!, state);
                    Logger.LogInfo("Recovered the local host's saved personal profile by its unique player name.");
                }
            }
            catch (Exception ex) { Logger.LogWarning("Local host profile recovery was unavailable; hosting can continue: " + ex.Message); }
            return resolved;
        }

        internal void RestoreLoadedPersonalState()
        {
            // This is called only by native LoadPlayer, never when hosting an existing scene.
            if (GuestWorld?.SuppressDisk == true || Session?.IsRunning == true) return;
            try
            {
                var history = LocalPersonalProfileRecovery.Load(NativeSaveWriter.PersistentRoot, NativeSaveWriter.SelectedSave,
                    NativeSaveWriter.SelectedTimestamp, SessionOptions.SupportedGameHash,
                    error => Logger.LogWarning("An unavailable personal checkpoint or journal was ignored: " + error));
                if (history == null) return;
                Guid resolved = LocalPersonalProfileRecovery.Resolve(ProfileIdentityPolicy.ReadOrCreate(profileId.Value), displayName.Value, history.Identities);
                profileId.Value = resolved.ToString("D");
                var profile = history.Find(resolved);
                if (profile == null) return;
                var loadedGameplay = FindObjectOfType<Gameplay>() ?? throw new InvalidOperationException("Loaded gameplay is missing.");
                NativePlayerProfile.RestoreLoaded(loadedGameplay, PlayerProfileState.FromSection(profile.State[PlayerProfileState.SectionKey]));
                Logger.LogInfo("Restored local personal state from the latest valid same-world personal history.");
            }
            catch (Exception ex)
            {
                // Optional personal metadata must never terminate native loading.
                // Permanent receipt validation remains on the host save path.
                Logger.LogError("Multiplayer personal state could not be loaded: " + ex.Message);
                ReportSaveStatus("Multiplayer personal state could not be loaded; see the log.");
            }
        }
    }
}
