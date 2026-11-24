using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MonMP.Core.Persistence;
using MonMP.Core.Session;

namespace MonMP.Plugin.Replication
{
    // The selected native checkpoint establishes the world. Optional personal history
    // may be newer, but never supplies world configuration, positions or receipts.
    internal static class LocalPersonalProfileRecovery
    {
        internal static PersonalProfileHistory? Load(string persistentRoot, string saveName, int timestamp,
            string gameBuild, Action<string> ignored)
        {
            if (timestamp <= 0) return null;
            string directory = Path.Combine(persistentRoot, saveName, timestamp.ToString(CultureInfo.InvariantCulture));
            // A deliberately native-only checkpoint has a validated world/fingerprint
            // marker instead of a sidecar. It authorizes the same personal recovery
            // history as HostSaveCoordinator, without reopening permanent receipts.
            var recovery = SaveSlot.LoadNativeOnly(directory, gameBuild);
            var selected = recovery == null ? SidecarStore.Load(directory, gameBuild) : null;
            var ancestry = recovery == null && selected == null ? SaveAncestry.Load(directory, gameBuild) : null;
            Guid worldId = recovery?.WorldId ?? selected?.Document.WorldId ?? ancestry?.WorldId ?? Guid.Empty;
            if (worldId == Guid.Empty) return null;
            var history = new PersonalProfileHistory(worldId, gameBuild);
            if (selected != null) history.AddPublication(selected.Document, timestamp);
            if (ancestry != null)
            {
                try
                {
                    var source = SaveAncestry.FindSource(SaveSlot.GetSaveParentPath(persistentRoot, saveName), ancestry, gameBuild);
                    if (source != null) history.AddPublication(source.Snapshot.Document, source.Timestamp);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
                { ignored(ex.Message); }
            }
            if (recovery != null) AddRecoverySource(history, persistentRoot, saveName, gameBuild, recovery, ignored);
            try { history.LoadPublications(persistentRoot, saveName, ignored); }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
            { ignored(ex.Message); }
            try
            {
                string parent = SaveSlot.GetSaveParentPath(persistentRoot, saveName);
                var journal = PersonalProfileJournal.LoadForWorld(parent, worldId, gameBuild, out var binding);
                if (journal != null && binding != null) history.AddJournal(journal, binding);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
            { ignored(ex.Message); }
            return history;
        }

        private static void AddRecoverySource(PersonalProfileHistory history, string persistentRoot, string saveName,
            string gameBuild, NativeOnlySaveInfo recovery, Action<string> ignored)
        {
            int sourceTimestamp = recovery.SourceTimestamp;
            if (sourceTimestamp <= 0 || sourceTimestamp >= recovery.Timestamp) return;
            for (int depth = 0; depth < 32; depth++)
            {
                string source = Path.Combine(persistentRoot, saveName, sourceTimestamp.ToString(CultureInfo.InvariantCulture));
                try
                {
                    var older = SaveSlot.LoadNativeOnly(source, gameBuild);
                    if (older == null)
                    {
                        var publication = SidecarStore.Load(source, gameBuild);
                        if (publication != null) history.AddPublication(publication.Document, sourceTimestamp);
                        return;
                    }
                    if (older.WorldId != recovery.WorldId || older.SourceTimestamp <= 0 || older.SourceTimestamp >= sourceTimestamp) return;
                    sourceTimestamp = older.SourceTimestamp;
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
                { ignored(ex.Message); return; }
            }
        }

        internal static Guid Resolve(Guid requested, string name, IEnumerable<KeyValuePair<Guid, string>> identities) =>
            ProfileIdentityPolicy.Resolve(requested, PlayerNamePolicy.ValidationError(name) == null ? name : "",
                identities, Array.Empty<Guid>());
    }
}
