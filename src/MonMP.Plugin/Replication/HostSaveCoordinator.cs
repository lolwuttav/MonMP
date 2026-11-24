using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Persistence;
using MonMP.Core.Session;
using MonMP.Plugin.Diagnostics;

namespace MonMP.Plugin.Replication
{
    internal sealed class HostSaveCoordinator
    {
        private readonly SharedWorld world;
        private readonly PersonalProfileJournalWriter journalWriter;
        private SidecarDocument? previous;
        private int previousTimestamp;
        private SidecarDocument? profileIdentityHistory;
        private PersonalProfileHistory? personalHistory;
        private Guid worldId;
        private readonly Guid liveGeneration = Guid.NewGuid();
        private readonly string saveName;
        internal string BoundSaveName => saveName;
        private int anchor;
        private readonly Dictionary<Guid, SidecarProfile> journalProfiles = new Dictionary<Guid, SidecarProfile>();
        private PersonalProfileJournalBinding? journalBinding;
        private bool journalBlocked;
        private string lastJournalFailure = "";
        // Safe samples arrive once a second per player; they are coalesced and written at most once per
        // JournalInterval, so the host's frame never waits on a durable disk rewrite for each (#226).
        private const float JournalInterval = 5f;
        private bool journalDirty;
        private float nextJournalWrite;
        private readonly Dictionary<(Guid Profile, Guid Operation), TransactionReceipt> transactionReceipts =
            new Dictionary<(Guid, Guid), TransactionReceipt>();
        private TransactionReceiptBinding? transactionBinding;
        private TransactionReceiptBinding? inheritedTransactionBinding;
        // An unavailable durable history changes save metadata recovery, never
        // shop availability. Live operations remain protected in transactionReceipts.
        private bool transactionBlocked;
        private House_Location location;
        private string nativeOnlyReason = "";
        private bool immediateRecovery;
        private bool forceRecoveryWriting;
        private bool permadeathEnded;
        internal void EndForPermadeath() { DrainJournal(); permadeathEnded = true; journalDirty = false; }
        public bool Pending { get; private set; }
        public bool LastSaveSucceeded { get; private set; }
        public string LastSavedDirectory { get; private set; } = "";
        public string LastFailure { get; private set; } = "";
        internal IReadOnlyDictionary<string, SidecarSection> SelectedConfiguration
        {
            get
            {
                try { EnsureHistory(); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP ignored unavailable saved configuration: " + ex.Message); }
                return previous?.Configuration ?? new Dictionary<string, SidecarSection>();
            }
        }
        internal HostSaveCoordinator(SharedWorld world, PersonalProfileJournalWriter? journalWriter = null)
        {
            this.world = world;
            this.journalWriter = journalWriter ?? new PersonalProfileJournalWriter();
            saveName = NativeSaveWriter.SelectedSave;
            anchor = NativeSaveWriter.SelectedTimestamp;
        }
        public void Request(House_Location requestedLocation)
        {
            if (permadeathEnded) return;
            if (Pending || world.SaveRounds?.Phase == SavePhase.Collecting || world.SaveRounds?.Phase == SavePhase.Writing)
            { Report("A multiplayer save is already in progress."); return; }
            LastFailure = ""; LastSaveSucceeded = false; nativeOnlyReason = ""; immediateRecovery = forceRecoveryWriting = false; location = requestedLocation;
            if (NativeSaveWriter.SelectedSave != saveName || NativeSaveWriter.SelectedTimestamp != anchor)
            {
                LastFailure = "The selected save changed; restart the multiplayer session before saving.";
                Report(LastFailure);
                return;
            }
            try
            {
                EnsureHistory();
                if (transactionBlocked) nativeOnlyReason = "Permanent receipt history is unavailable.";
            }
            catch (Exception ex)
            {
                // Multiplayer history must never veto the native world checkpoint.
                // Keep its old files and mark the new checkpoint's history unavailable.
                nativeOnlyReason = ex.Message; transactionBlocked = true;
                if (worldId == Guid.Empty) worldId = Guid.NewGuid();
            }
            Pending = true;
            try { world.SaveRounds!.Begin(3); UnityEngine.Debug.Log("MonMP save status: collecting player state for the multiplayer save."); }
            catch (Exception ex)
            {
                nativeOnlyReason = ex.Message;
                immediateRecovery = true;
                world.PrepareRecoverySave();
                Update();
            }
        }
        private void EnsureHistory()
        {
                if (worldId == Guid.Empty)
                {
                    // Native new-game initialization uses -1. Never substitute the newest folder
                    // when the player explicitly loaded an older generation.
                    if (anchor != -1)
                    {
                        if (anchor <= 0) throw new InvalidDataException("Loaded save timestamp is invalid.");
                        string directory = Path.Combine(NativeSaveWriter.PersistentRoot, saveName, anchor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        NativeOnlySaveInfo? recovery;
                        try { recovery = SaveSlot.LoadNativeOnly(directory, SessionOptions.SupportedGameHash); }
                        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // Native state can still be loaded when its optional multiplayer
                            // metadata is unreadable. Unknown world identity cannot authorize
                            // another generation's profiles or permanent receipts.
                            worldId = Guid.NewGuid(); transactionBlocked = true; nativeOnlyReason = ex.Message;
                            Report("Saved multiplayer history was ignored; personal values use defaults: " + ex.Message); return;
                        }
                        if (recovery != null)
                        {
                            worldId = recovery.WorldId;
                            transactionBlocked = true; nativeOnlyReason = recovery.RecoveryReason;
                            // The source checkpoint remains intact. Its profiles are a
                            // fallback; never pretend they were captured at this timestamp.
                            if (recovery.SourceTimestamp > 0 && recovery.SourceTimestamp < recovery.Timestamp)
                            {
                                string source = Path.Combine(NativeSaveWriter.PersistentRoot, saveName,
                                    recovery.SourceTimestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                int sourceTimestamp = recovery.SourceTimestamp;
                                for (int depth = 0; depth < 32; depth++)
                                {
                                    NativeOnlySaveInfo? older;
                                    try
                                    {
                                        older = SaveSlot.LoadNativeOnly(source, SessionOptions.SupportedGameHash);
                                        if (older == null)
                                        {
                                            previous = SidecarStore.Load(source, SessionOptions.SupportedGameHash)?.Document;
                                            previousTimestamp = sourceTimestamp;
                                            if (previous == null)
                                            {
                                                // An offline descendant can retain an exact receipt
                                                // binding after ordinary native retention prunes its
                                                // original sidecar. Do not guess a different root.
                                                var ancestry = SaveAncestry.Load(source, SessionOptions.SupportedGameHash);
                                                if (ancestry != null && ancestry.WorldId == worldId)
                                                {
                                                    inheritedTransactionBinding = ancestry.Receipts;
                                                    var retained = SaveAncestry.FindSource(
                                                        SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, saveName),
                                                        ancestry, SessionOptions.SupportedGameHash, includeEmbeddedMetadata: ancestry.Receipts != null);
                                                    previous = retained?.Snapshot.Document;
                                                    previousTimestamp = retained?.Timestamp ?? 0;
                                                }
                                            }
                                            break;
                                        }
                                    }
                                    catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
                                    { Report("An unavailable source checkpoint was ignored: " + ex.Message); break; }
                                    if (older.WorldId != worldId || older.SourceTimestamp <= 0 || older.SourceTimestamp >= sourceTimestamp) break;
                                    sourceTimestamp = older.SourceTimestamp;
                                    source = Path.Combine(NativeSaveWriter.PersistentRoot, saveName,
                                        sourceTimestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                }
                                if (previous != null && previous.WorldId != worldId) previous = null;
                            }
                            // A native-only publication loses optional metadata, not
                            // necessarily its permanent claims. Reopen only an existing
                            // journal reached through verified same-world source metadata.
                            if (previous != null || inheritedTransactionBinding != null)
                            {
                                LoadTransactionJournal();
                                if (!transactionBlocked) nativeOnlyReason = "";
                            }
                            try
                            {
                                var profiles = SaveSlot.LoadRecoveryProfiles(directory, recovery);
                                if (profiles != null) { previous = profiles.Document; previousTimestamp = anchor; }
                            }
                            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                            { Report("Recovery profiles could not be read: " + ex.Message); }
                            LoadProfileJournal();
                            return;
                        }
                        try { previous = SidecarStore.Load(directory, SessionOptions.SupportedGameHash)?.Document; }
                        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
                        {
                            worldId = Guid.NewGuid(); transactionBlocked = true; nativeOnlyReason = ex.Message;
                            Report("Saved multiplayer history was ignored; personal values use defaults: " + ex.Message); return;
                        }
                        previousTimestamp = anchor;
                        if (previous == null)
                        {
                            try
                            {
                                var ancestry = SaveAncestry.Load(directory, SessionOptions.SupportedGameHash);
                                string parent = SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, saveName);
                                if (ancestry != null)
                                {
                                    worldId = ancestry.WorldId;
                                    inheritedTransactionBinding = ancestry.Receipts;
                                    try
                                    {
                                        var source = SaveAncestry.FindSource(parent, ancestry, SessionOptions.SupportedGameHash);
                                        previous = source?.Snapshot.Document; previousTimestamp = source?.Timestamp ?? 0;
                                        if (ancestry.Receipts == null && ancestry.MetadataSnapshot != null)
                                        {
                                            // Embedded degraded profiles do not identify a receipt root.
                                            var full = SaveAncestry.FindSource(parent, ancestry, SessionOptions.SupportedGameHash, includeEmbeddedMetadata: false);
                                            previous = full?.Snapshot.Document; previousTimestamp = full?.Timestamp ?? 0;
                                            LoadTransactionJournal();
                                            previous = source?.Snapshot.Document; previousTimestamp = source?.Timestamp ?? 0;
                                        }
                                        else LoadTransactionJournal();
                                    }
                                    catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
                                    {
                                        Report("An unavailable source checkpoint was ignored: " + ex.Message);
                                        if (inheritedTransactionBinding != null) LoadTransactionJournal();
                                    }
                                    if (inheritedTransactionBinding == null)
                                    {
                                        transactionBlocked = true;
                                        nativeOnlyReason = ancestry.UnavailableReason;
                                    }
                                    LoadProfileJournal();
                                    return;
                                }
                                if (SaveAncestry.HasMultiplayerHistory(parent))
                                    throw new InvalidDataException("This native generation has no verified link to the existing multiplayer receipt history.");
                            }
                            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
                            {
                                worldId = Guid.NewGuid(); transactionBlocked = true; nativeOnlyReason = ex.Message;
                                Report("Saved multiplayer ancestry is unavailable; joining and shops remain available: " + ex.Message);
                                return;
                            }
                        }
                    }
                    worldId = previous?.WorldId ?? Guid.NewGuid();
                    LoadTransactionJournal();
                    LoadProfileJournal();
                }
        }
        private void LoadTransactionJournal()
        {
            // Recovery starts blocked. Unlike first-time legacy migration, it must
            // prove an existing root rather than initialize missing receipt history.
            bool requireExistingHistory = transactionBlocked;
            transactionBinding = null; transactionBlocked = false; profileIdentityHistory = null;
            if ((previous == null && inheritedTransactionBinding == null) || anchor <= 0) return;
            try
            {
                string parent = SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, saveName);
                int receiptTimestamp = inheritedTransactionBinding?.SourceTimestamp ?? previousTimestamp;
                string directory = Path.Combine(NativeSaveWriter.PersistentRoot, saveName, receiptTimestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SidecarDocument? receiptDocument = previous;
                // Even if a later checkpoint is damaged, retain every claim that
                // can still be validated against the explicit selected ancestry.
                var sourceBinding = inheritedTransactionBinding ?? new TransactionReceiptBinding(
                    receiptDocument!.WorldId, receiptDocument.Generation, receiptTimestamp, receiptDocument.GameBuild);
                try
                {
                    var known = TransactionReceiptJournal.Load(parent, sourceBinding);
                    if (known != null)
                        foreach (var receipt in known)
                            if (!transactionReceipts.ContainsKey((receipt.Profile, receipt.Operation)))
                                transactionReceipts.Add((receipt.Profile, receipt.Operation), receipt);
                }
                catch (Exception ex) { Report("An unavailable transaction checkpoint was retained for recovery: " + ex.Message); }
                var newestSameWorld = SaveSlot.FindLatestValidSidecarAfter(NativeSaveWriter.PersistentRoot, saveName,
                    receiptTimestamp, SessionOptions.SupportedGameHash, worldId);
                if (newestSameWorld != null)
                {
                    // Keep world state and positions from the selected checkpoint. Permanent
                    // claims and profile authentication can survive a same-world rollback.
                    directory = newestSameWorld.DirectoryPath;
                    receiptTimestamp = newestSameWorld.Timestamp;
                    receiptDocument = newestSameWorld.Snapshot.Document;
                    profileIdentityHistory = receiptDocument;
                }
                var binding = newestSameWorld == null && inheritedTransactionBinding != null ? inheritedTransactionBinding :
                    new TransactionReceiptBinding(receiptDocument!.WorldId, receiptDocument.Generation, receiptTimestamp, receiptDocument.GameBuild);
                // An independent bound marker can retain permanent accounting even
                // when only its optional sidecar has been lost. Never recreate a root.
                var recovered = receiptDocument != null && receiptDocument.Generation == binding.Generation &&
                    receiptDocument.WorldId == binding.WorldId && File.Exists(Path.Combine(directory, SidecarStore.FileName))
                    ? PermanentReceiptRetention.Prepare(directory, parent, binding, requireExistingHistory: requireExistingHistory)
                    : TransactionReceiptJournal.Load(parent, binding) ?? throw new InvalidDataException("Permanent receipt history is missing for this save ancestry.");
                foreach (var receipt in recovered)
                {
                    var key = (receipt.Profile, receipt.Operation);
                    if (transactionReceipts.TryGetValue(key, out var known) && !known.Equals(receipt))
                        throw new InvalidDataException("Retained receipt identity changed during recovery.");
                    transactionReceipts[key] = receipt;
                }
                transactionBinding = binding;
            }
            catch (Exception ex)
            {
                transactionBinding = null; transactionBlocked = true;
                Report("Transaction history could not be loaded; shops remain available: " + ex.Message);
            }
        }
        // Called after store validation but before native cash/item mutation. Reserve
        // the operation in this live session first; unavailable optional disk history
        // must not disable shops or allow this operation's native effects to run twice.
        internal bool ClaimStorePurchase(Guid profile, Guid operation, ulong listing)
        {
            try { EnsureHistory(); }
            catch (Exception ex)
            {
                if (worldId == Guid.Empty) worldId = Guid.NewGuid();
                transactionBlocked = true;
                Report("Transaction history is unavailable; shops remain available: " + ex.Message);
            }
            if (NativeSaveWriter.SelectedSave != saveName || NativeSaveWriter.SelectedTimestamp != anchor)
                throw new InvalidOperationException("The selected save changed before the purchase.");
            var key = (profile, operation);
            if (transactionReceipts.ContainsKey(key)) return false;
            var receipt = new TransactionReceipt(profile, operation, TransactionReceipt.StorePurchase, listing);
            transactionReceipts.Add(key, receipt);
            if (anchor <= 0 || transactionBinding == null) return true;
            try
            {
                string parent = SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, saveName);
                if (!TransactionReceiptJournal.TryClaimExisting(parent, transactionBinding, receipt)) return false;
                transactionBlocked = false;
            }
            catch (Exception ex)
            {
                if (!transactionBlocked)
                    Report("Transaction history could not be written; shops remain available: " + ex.Message);
                transactionBlocked = true;
                // Native mutation has not begun. Continue this operation once;
                // its live reservation prevents any retry from applying it again.
            }
            return true;
        }
        private void RefreshDurableReceipts()
        {
            if (transactionBlocked) throw new InvalidOperationException("Receipt history is unavailable; saving cannot discard it.");
            if (transactionBinding == null) return; // Unsaved new world's history is in memory.
            try
            {
                string parent = SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, saveName);
                var retained = TransactionReceiptJournal.Load(parent, transactionBinding)
                    ?? throw new InvalidDataException("Permanent receipt history is missing for this save.");
                // A flushed/published claim can outlive a failed return to its caller.
                // Retain it when the next save rolls over to a new generation.
                foreach (var receipt in retained)
                {
                    var key = (receipt.Profile, receipt.Operation);
                    if (transactionReceipts.TryGetValue(key, out var previousReceipt) && !previousReceipt.Equals(receipt))
                        throw new InvalidDataException("Retained receipt identity changed before saving.");
                    transactionReceipts[key] = receipt;
                }
            }
            catch { transactionBlocked = true; throw; }
        }
        private void LoadProfileJournal()
        {
            DrainJournal();
            journalProfiles.Clear(); journalBinding = null; journalBlocked = false; journalDirty = false;
            if (worldId == Guid.Empty || anchor <= 0) return;
            string gameBuild = previous?.GameBuild ?? SessionOptions.SupportedGameHash;
            personalHistory = new PersonalProfileHistory(worldId, gameBuild);
            if (previous != null) personalHistory.AddPublication(previous, previousTimestamp);
            try
            {
                string parent = SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, saveName);
                personalHistory.LoadPublications(NativeSaveWriter.PersistentRoot, saveName,
                    error => Report("An unavailable personal checkpoint was ignored: " + error));
                journalBinding = new PersonalProfileJournalBinding(worldId, previous?.Generation ?? liveGeneration, anchor, gameBuild);
                IReadOnlyList<SidecarProfile>? recovered;
                PersonalProfileJournalBinding? recoveredBinding;
                try { recovered = PersonalProfileJournal.LoadForWorld(parent, worldId, gameBuild, out recoveredBinding); }
                catch (InvalidDataException ex)
                {
                    // Keep the expected binding so a later validated live sample can atomically replace the bad journal.
                    Report("Saved personal profile journal was ignored; using the published save: " + ex.Message); return;
                }
                if (recovered != null && recoveredBinding != null)
                {
                    personalHistory.AddJournal(recovered, recoveredBinding);
                    foreach (var profile in recovered) journalProfiles.Add(profile.Id, profile);
                }
            }
            catch (Exception ex)
            {
                journalProfiles.Clear(); journalBinding = null; journalBlocked = true;
                Report("Saved personal profile journal was ignored; using the published save: " + ex.Message);
            }
            finally
            {
                foreach (var profile in ProfilePruning.KeepRecent(personalHistory.Profiles,
                    protectedProfiles: world.LiveProfiles?.ConnectedProfileIds))
                {
                    // A periodic live observation can already exist before history is first
                    // requested. Loading disk history must not replace a newer live value.
                    if (world.LiveProfiles?.LastSeenUtc(profile.Id) is long seen && seen >= profile.LastSeenUtc) continue;
                    world.LiveProfiles?.RememberRecovered(profile);
                }
            }
        }
        internal void RecordSafeProfile(SidecarProfile profile)
        {
            if (permadeathEnded) return;
            try
            {
                EnsureHistory();
                if (anchor <= 0 || journalBlocked || journalBinding == null ||
                    NativeSaveWriter.SelectedSave != saveName || NativeSaveWriter.SelectedTimestamp != anchor) return;
                profile = world.Permadeath?.Stamp(profile) ?? profile;
                if (!PersonalProfileHistory.ValidPersonalState(profile)) return;
                long seen = PersonalProfileHistory.AfterObservation(profile.LastSeenUtc, personalHistory?.Find(profile.Id)?.LastSeenUtc ?? 0);
                if (world.LiveProfiles?.IsAccepted(profile.Id) == true) seen = world.LiveProfiles.AdvanceObservation(profile.Id, seen - 1);
                if (seen != profile.LastSeenUtc) profile = new SidecarProfile(profile.Id, profile.DisplayName, profile.ColorRgb, seen, profile.State);
                if (journalProfiles.TryGetValue(profile.Id, out var prior) && SameState(prior, profile) &&
                    prior.LastSeenUtc == profile.LastSeenUtc) return;
                // Equal payloads still refresh recency. Keep the interval-based disk
                // batching; a stationary connected player must not age out under churn.
                journalProfiles[profile.Id] = profile; journalDirty = true;
                personalHistory?.AddJournal(new[] { profile }, journalBinding);
            }
            catch (Exception ex)
            {
                if (lastJournalFailure != ex.Message)
                { lastJournalFailure = ex.Message; Report("Latest safe player state could not be journaled: " + ex.Message); }
            }
        }
        private static bool SameState(SidecarProfile a, SidecarProfile b) =>
            a.DisplayName == b.DisplayName && a.ColorRgb == b.ColorRgb && a.State.Count == b.State.Count &&
            a.State.All(p => b.State.TryGetValue(p.Key, out var other) && other.Version == p.Value.Version && other.Payload.SequenceEqual(p.Value.Payload));
        internal void FlushJournal(bool force)
        {
            if (force) DrainJournal();
            else ObserveJournalWrites();
            if (permadeathEnded) return;
            if (!journalDirty || (!force && UnityEngine.Time.unscaledTime < nextJournalWrite)) return;
            journalDirty = false; nextJournalWrite = UnityEngine.Time.unscaledTime + JournalInterval;
            try
            {
                if (anchor <= 0 || journalBlocked || journalBinding == null ||
                    NativeSaveWriter.SelectedSave != saveName || NativeSaveWriter.SelectedTimestamp != anchor) return;
                string parent = SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, saveName);
                var snapshot = new PersonalProfileJournalWriter.Snapshot(parent, journalBinding,
                    ProfilePruning.KeepRecent(journalProfiles.Values, protectedProfiles: world.LiveProfiles?.ConnectedProfileIds));
                if (force) { journalWriter.WriteSynchronously(snapshot); lastJournalFailure = ""; }
                else journalWriter.Submit(snapshot);
            }
            catch (Exception ex)
            {
                // Still unjournaled: retry at the next interval (or the forced shutdown flush) even if no field changes.
                journalDirty = true;
                if (lastJournalFailure != ex.Message)
                { lastJournalFailure = ex.Message; Report("Latest safe player state could not be journaled: " + ex.Message); }
            }
        }
        private void DrainJournal()
        {
            journalWriter.Drain();
            ObserveJournalWrites();
        }
        private void ObserveJournalWrites()
        {
            if (!journalWriter.TakeCompletion(out var failure)) return;
            if (failure == null) { lastJournalFailure = ""; return; }
            journalDirty = true;
            if (lastJournalFailure != failure.Message)
            { lastJournalFailure = failure.Message; Report("Latest safe player state could not be journaled: " + failure.Message); }
        }
        internal void RecordPermadeath(Guid id)
        {
            EnsureHistory();
            var profile = personalHistory?.Find(id);
            if (profile == null) return;
            RecordSafeProfile(profile);
            FlushJournal(true);
        }
        internal SidecarSection? SavedSection(Guid profile, string key)
        {
            EnsureHistory();
            if (key == SharedPermadeath.SectionKey && journalProfiles.TryGetValue(profile, out var live) &&
                live.State.TryGetValue(key, out var dead)) return dead;
            if (key == PlayerProfileState.SectionKey) return LatestPersonalState(profile)?.ToSection();
            if (previous != null && previous.Profiles.TryGetValue(profile, out var saved) && saved.State.TryGetValue(key, out var section)) return section;
            return key == SharedWorld.ProfileKeySection ? RetainedProfileKey(profile) : null;
        }
        internal IReadOnlyList<KeyValuePair<Guid, string>> SavedProfileIdentities()
        {
            EnsureHistory();
            return personalHistory?.Identities ?? Array.Empty<KeyValuePair<Guid, string>>();
        }
        private SidecarSection? RetainedProfileKey(Guid profile)
        {
            return profileIdentityHistory != null && profileIdentityHistory.Profiles.TryGetValue(profile, out var identity) &&
                identity.State.TryGetValue(SharedWorld.ProfileKeySection, out var key) && key.Version == 1 && key.Payload.Length == 32 ? key : null;
        }
        internal PlayerProfileState? Checkpoint(Guid profile)
        {
            EnsureHistory();
            return LatestPersonalState(profile);
        }
        internal ProfileRestoreMessage? FindProfile(Guid profile)
        {
            EnsureHistory();
            if (NativeSaveWriter.SelectedSave != saveName || NativeSaveWriter.SelectedTimestamp != anchor)
                throw new InvalidOperationException("Selected save changed before profile restoration.");
            var state = LatestPersonalState(profile);
            return state != null ? new ProfileRestoreMessage(worldId, previous?.Generation ?? liveGeneration, profile, state) : null;
        }
        private PlayerProfileState? LatestPersonalState(Guid profile)
        {
            var saved = personalHistory?.Find(profile);
            var live = world.LiveProfiles?.Find(profile);
            if (live != null && (world.LiveProfiles?.IsAccepted(profile) == true || saved == null || (world.LiveProfiles?.LastSeenUtc(profile) ?? 0) >= saved.LastSeenUtc)) return live;
            return saved != null ? PlayerProfileState.FromSection(saved.State[PlayerProfileState.SectionKey]) : null;
        }
        public void Update()
        {
            if (permadeathEnded) return;
            if (!Pending) FlushJournal(false);
            if (!Pending) return;
            var round = world.SaveRounds!;
            if (round.Phase == SavePhase.Failed && !immediateRecovery)
            {
                nativeOnlyReason = round.LocalFailure;
                immediateRecovery = true;
                world.PrepareRecoverySave();
            }
            if (round.Phase != SavePhase.Writing && !immediateRecovery) return;
            // Object.Destroy removes consumed machine inputs at the end of the frame.
            // Never serialize those inputs beside their newly completed output.
            if (!world.SaveSnapshotReady) return;
            bool published = false, defer = false;
            try
            {
                if (NativeSaveWriter.SelectedSave != saveName || NativeSaveWriter.SelectedTimestamp != anchor)
                    throw new InvalidOperationException("The selected save changed during collection.");
                bool recoveryWriting = forceRecoveryWriting || round.UsedRecovery || nativeOnlyReason.Length != 0;
                if (!world.SaveGate.Frozen)
                {
                    try { world.Profile.SettleForSave(); world.SaveGate.Freeze(recoveryWriting); }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning("MonMP save settlement recovery: " + ex.Message);
                        world.PrepareRecoverySave(); forceRecoveryWriting = recoveryWriting = true;
                        if (!world.SaveSnapshotReady) { defer = true; return; }
                    }
                }
                if (round.Phase == SavePhase.Writing && world.PersonalProfileReady && world.Profile.CanCapture)
                {
                    try { round.RefreshHostProfile(world.Profile.Capture()); }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP retained the host checkpoint: " + ex.Message); }
                }
                DrainJournal();
                using var slot = SaveSlot.Reserve(NativeSaveWriter.PersistentRoot, saveName, TimeUtils.GetUnixTime(), true,
                    sourceTimestamp: anchor, expectedWorld: worldId);
                var native = NativeSaveWriter.Write(slot, world, location, recovery: recoveryWriting);
                if (permadeathEnded) return;
                SidecarDocument? document = null;
                var acceptedProfiles = round.Phase == SavePhase.Writing ? round.AcceptedProfileIds.ToArray() : Array.Empty<Guid>();
                try
                {
                    document = CaptureMetadata(acceptedProfiles);
                    if (nativeOnlyReason.Length != 0) throw new InvalidOperationException(nativeOnlyReason);
                    RefreshDurableReceipts();
                    // Prepare claims for the prospective generation before publication. The old
                    // generation file remains intact if this staged save never publishes. Do
                    // this before the sidecar completes the stage, so crash recovery cannot
                    // promote a generation whose receipt journal was never written.
                    var nextTransactionBinding = new TransactionReceiptBinding(document.WorldId, document.Generation, slot.Timestamp, document.GameBuild);
                    TransactionReceiptJournal.Commit(SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, saveName),
                        nextTransactionBinding, transactionReceipts.Values);
                    SidecarStore.Commit(slot.StagingDirectory, new SidecarSnapshot(document, native));

#if MONMP_DEVELOPMENT
                    CrashRecoveryTest.KillAfterStagedCommit(slot);
#endif

                    slot.Publish(document.GameBuild, document.WorldId, rollingRetention: true); published = true;
                    transactionBinding = nextTransactionBinding; transactionBlocked = false;
                }
                catch (Exception ex) when (!published)
                {
                    nativeOnlyReason = ex.Message;
                    UnityEngine.Debug.LogWarning("MonMP multiplayer metadata recovery: " + ex);
                    // Optional profile/configuration publication must not permanently
                    // disable a healthy store journal. Re-read its exact durable root,
                    // retaining even claims whose return to the caller was uncertain.
                    if (!transactionBlocked && transactionBinding != null)
                    {
                        try { RefreshDurableReceipts(); }
                        catch (Exception receipts) { UnityEngine.Debug.LogWarning("MonMP recovery receipt validation: " + receipts); }
                    }
                    else transactionBlocked = true;
                    try
                    {
                        document ??= CaptureMetadata(acceptedProfiles, recovery: true);
                        slot.StageRecoveryProfiles(new SidecarSnapshot(document, native));
                    }
                    catch (Exception profiles)
                    {
                        document = null;
                        Report("The world will be saved, but guest profile data could not be staged: " + profiles.Message);
                    }
                    slot.PublishNativeOnly(SessionOptions.SupportedGameHash, worldId, native, nativeOnlyReason);
                    published = true;
                }
                LastSavedDirectory = slot.DestinationDirectory; anchor = slot.Timestamp;
                LastSaveSucceeded = true; LastFailure = "";
                // Publication is the success boundary. A notification/network bookkeeping
                // failure afterward cannot turn a complete disk checkpoint into a failed save.
                try { NativeSaveWriter.SelectTimestamp(anchor); AccessTools.Field(typeof(SaveSystem), "LastSaveTimestamp").SetValue(null, anchor); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP save selection update: " + ex.Message); }
                try { AdvanceProfileCheckpoint(document, acceptedProfiles); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP saved profile rollover: " + ex.Message); }
                try
                {
                    if (round.Phase == SavePhase.Writing) round.Complete();
                    else if (round.Phase == SavePhase.Collecting) round.Fail("World saved using the recovery checkpoint.");
                }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP save completion notification: " + ex.Message); }
                if (nativeOnlyReason.Length == 0 && document != null)
                {
                    try { slot.RetireOldCheckpoint(document.GameBuild, document.WorldId); }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP checkpoint retention skipped: " + ex.Message); }
                }
                Report(nativeOnlyReason.Length != 0 ? transactionBlocked
                        ? "World saved. Some multiplayer history could not be recovered; shops remain available."
                        : "World saved using available multiplayer history. Shops remain available." :
                    forceRecoveryWriting || round.UsedRecovery || round.RetainedProfileIds.Count != 0 ? "Multiplayer game saved using available player checkpoints." : "Multiplayer game saved.");
            }
            catch (Exception ex)
            {
                if (published) { LastSaveSucceeded = true; LastFailure = ""; }
                else LastFailure = ex.Message;
                try { if (round.Phase == SavePhase.Collecting || round.Phase == SavePhase.Writing) round.Fail(ex.Message); }
                catch (Exception completion) { UnityEngine.Debug.LogWarning("MonMP save failure notification: " + completion.Message); }
                Report(published ? "Save files were published, but completion failed: " + ex.Message : "Multiplayer save failed; the previous save is unchanged: " + ex.Message);
            }
            finally { if (!defer) { Pending = false; world.SaveGate.Dispose(); } }
        }
        private SidecarDocument CaptureMetadata(Guid[] accepted, bool recovery = false)
        {
            var round = world.SaveRounds!;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var retained = MergeJournalProfiles(previous?.Profiles.Values ?? Enumerable.Empty<SidecarProfile>());
            if (!recovery) retained = world.LiveProfiles!.MergeForSave(retained);
            var profiles = round.Phase == SavePhase.Writing ? round.MergeProfiles(retained, now) : retained;
            if (!recovery && world.ReconnectPositions != null) profiles = world.ReconnectPositions.Stamp(profiles);
            profiles = StampAcceptedObservations(world.StampProfileKeys(profiles), accepted);
            var protectedProfiles = accepted.Concat(world.LiveProfiles?.ConnectedProfileIds ?? Enumerable.Empty<Guid>());
            var document = new SidecarDocument(worldId, Guid.NewGuid(), now, SessionOptions.SupportedGameHash,
                SessionOptions.CurrentModVersion, recovery ? previous?.Configuration ?? new Dictionary<string, SidecarSection>() : world.Vehicles.CaptureConfiguration(previous?.Configuration),
                ProfilePruning.KeepRecent(profiles, protectedProfiles: protectedProfiles));
            foreach (var id in accepted)
                if (!document.Profiles.TryGetValue(id, out var profile) || !PersonalProfileHistory.ValidPersonalState(profile))
                    throw new InvalidDataException("Accepted personal state is missing before save publication.");
            return document;
        }
        private void AdvanceProfileCheckpoint(SidecarDocument? document, Guid[] accepted)
        {
            string gameBuild = SessionOptions.SupportedGameHash;
            personalHistory ??= new PersonalProfileHistory(worldId, gameBuild);
            journalBinding = new PersonalProfileJournalBinding(worldId, document?.Generation ?? liveGeneration, anchor, gameBuild);
            journalBlocked = false;
            if (document == null)
            {
                // No independently published profile data: do not reset the last good journal.
                journalDirty |= journalProfiles.Count != 0;
                return;
            }
            foreach (var id in accepted)
                try
                {
                    var profile = document.Profiles[id];
                    world.LiveProfiles!.Remember(id, PlayerProfileState.FromSection(profile.State[PlayerProfileState.SectionKey]));
                    world.LiveProfiles.AdvanceObservation(id, profile.LastSeenUtc - 1);
                }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP saved profile notification: " + ex.Message); }
            previous = document; previousTimestamp = anchor;
            personalHistory.AddPublication(document, anchor);
            journalProfiles.Clear(); journalDirty = true;
            try
            {
                journalWriter.WriteSynchronously(new PersonalProfileJournalWriter.Snapshot(
                    SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, saveName), journalBinding, Array.Empty<SidecarProfile>()));
                journalDirty = false; lastJournalFailure = "";
            }
            catch (Exception ex)
            {
                nextJournalWrite = UnityEngine.Time.unscaledTime + JournalInterval;
                lastJournalFailure = ex.Message;
                Report("Saved personal profile journal could not be reset; the published save remains available: " + ex.Message);
            }
        }
        private IReadOnlyList<SidecarProfile> MergeJournalProfiles(IEnumerable<SidecarProfile> published)
        {
            var merged = published.ToDictionary(p => p.Id);
            // Strip unsupported personal payloads before merging valid observations. Other
            // selected-checkpoint sections, especially reconnect positions, remain selected.
            foreach (var profile in merged.Values.ToArray())
            {
                if (!profile.State.ContainsKey(PlayerProfileState.SectionKey) || PersonalProfileHistory.ValidPersonalState(profile)) continue;
                var fields = profile.State.Where(p => p.Key != PlayerProfileState.SectionKey);
                merged[profile.Id] = new SidecarProfile(profile.Id, profile.DisplayName, profile.ColorRgb, profile.LastSeenUtc, fields);
            }
            foreach (var profile in ProfilePruning.KeepRecent(personalHistory?.Profiles ?? Array.Empty<SidecarProfile>(),
                protectedProfiles: world.LiveProfiles?.ConnectedProfileIds))
            {
                var fields = merged.TryGetValue(profile.Id, out var old)
                    ? old.State.ToDictionary(p => p.Key, p => p.Value)
                    : new Dictionary<string, SidecarSection>();
                fields[PlayerProfileState.SectionKey] = profile.State[PlayerProfileState.SectionKey];
                merged[profile.Id] = new SidecarProfile(profile.Id, profile.DisplayName, profile.ColorRgb, profile.LastSeenUtc, fields);
            }
            foreach (var profile in merged.Values.ToArray())
            {
                if (profile.State.ContainsKey(SharedWorld.ProfileKeySection) || RetainedProfileKey(profile.Id) is not SidecarSection identity) continue;
                var fields = profile.State.ToDictionary(p => p.Key, p => p.Value);
                fields[SharedWorld.ProfileKeySection] = identity;
                merged[profile.Id] = new SidecarProfile(profile.Id, profile.DisplayName, profile.ColorRgb, profile.LastSeenUtc, fields);
            }
            return Array.AsReadOnly(merged.Values.OrderBy(p => p.Id).ToArray());
        }
        private IReadOnlyList<SidecarProfile> StampAcceptedObservations(IReadOnlyList<SidecarProfile> profiles, IEnumerable<Guid> acceptedProfiles)
        {
            var accepted = new HashSet<Guid>(acceptedProfiles);
            return profiles.Select(profile =>
            {
                if (!accepted.Contains(profile.Id)) return profile;
                long prior = Math.Max(personalHistory?.Find(profile.Id)?.LastSeenUtc ?? 0, world.LiveProfiles?.LastSeenUtc(profile.Id) ?? 0);
                long seen = PersonalProfileHistory.AfterObservation(profile.LastSeenUtc, prior);
                return new SidecarProfile(profile.Id, profile.DisplayName, profile.ColorRgb, seen, profile.State);
            }).ToArray();
        }
        private static void Report(string message)
        {
            try { MonMPPlugin.Instance?.ReportSaveStatus(message); }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP save status notification: " + ex.Message); }
        }
    }
}
