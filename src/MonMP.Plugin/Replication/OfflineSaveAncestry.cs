using System;
using System.Globalization;
using System.IO;
using MonMP.Core.Persistence;
using MonMP.Core.Session;

namespace MonMP.Plugin.Replication
{
    internal sealed class OfflineSaveAncestry
    {
        private readonly string saveName;
        private readonly SaveAncestryInfo source;
        private OfflineSaveAncestry(string saveName, SaveAncestryInfo source) { this.saveName = saveName; this.source = source; }

        internal static OfflineSaveAncestry? BeforeSave()
        {
            try
            {
                string name = NativeSaveWriter.SelectedSave;
                int timestamp = NativeSaveWriter.SelectedTimestamp;
                if (timestamp <= 0) return null;
                string parent = SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, name);
                var source = SaveAncestry.Capture(Path.Combine(parent, timestamp.ToString(CultureInfo.InvariantCulture)),
                    timestamp, SessionOptions.SupportedGameHash);
                return source == null ? null : new OfflineSaveAncestry(name, source);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("MonMP offline save ancestry was unavailable; native saving remains available: " + ex.Message);
                return null;
            }
        }

        internal void AfterSave()
        {
            try
            {
                int timestamp = SaveSystem.Get_LastSaveTimestamp;
                if (NativeSaveWriter.SelectedSave != saveName || timestamp <= source.Timestamp) return;
                string parent = SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, saveName);
                var local = MonMPPlugin.Instance?.CaptureOfflinePersonalState();
                if (local != null)
                {
                    // A saved clock may be ahead of the current UTC clock. The new
                    // native observation must still supersede that player's old journal.
                    var history = LocalPersonalProfileRecovery.Load(NativeSaveWriter.PersistentRoot, saveName, source.Timestamp,
                        source.GameBuild, error => UnityEngine.Debug.LogWarning("MonMP optional offline personal history: " + error));
                    long seen = PersonalProfileHistory.AfterObservation(local.LastSeenUtc, history?.Find(local.Id)?.LastSeenUtc ?? 0);
                    local = new SidecarProfile(local.Id, local.DisplayName, local.ColorRgb, seen, local.State);
                }
                SaveAncestry.CommitDescendant(Path.Combine(parent, timestamp.ToString(CultureInfo.InvariantCulture)), timestamp, source,
                    local);
            }
            catch (Exception ex)
            {
                // Native files remain usable. Hosting treats unverified existing
                // multiplayer ancestry conservatively without blocking admission.
                UnityEngine.Debug.LogWarning("MonMP offline save ancestry could not be preserved: " + ex.Message);
            }
        }
    }
}
