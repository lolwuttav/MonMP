using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.Persistence
{
    // Prepare the existing selected generation before its first permanent-journal
    // write. An older build cannot safely read v2 history or save it as empty.
    public static class PermanentReceiptRetention
    {
        public static IReadOnlyList<TransactionReceipt> Prepare(string saveDirectory, string receiptDirectory,
            TransactionReceiptBinding binding, Action<SidecarCommitStage>? checkpoint = null, bool requireExistingHistory = false)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            var snapshot = SidecarStore.Load(saveDirectory, binding.GameBuild)
                ?? throw new InvalidDataException("A published sidecar is required for permanent receipt retention.");
            if (snapshot.Document.WorldId != binding.WorldId || snapshot.Document.Generation != binding.Generation)
                throw new InvalidDataException("Selected sidecar changed before receipt compatibility migration.");
            var retained = TransactionReceiptJournal.Load(receiptDirectory, binding);
            if (snapshot.SchemaVersion >= 2)
                return retained ?? throw new InvalidDataException("Permanent receipt history is missing for this save.");
            if (retained == null)
            {
                if (requireExistingHistory)
                    throw new InvalidDataException("Permanent receipt history is missing for this recovery source.");
                TransactionReceiptJournal.InitializeLegacyHistory(receiptDirectory, binding);
                retained = Array.Empty<TransactionReceipt>();
            }
            // Preserve every field and native fingerprint. Only the compatibility
            // schema changes; no pruning, generation/time change or native save.
            // This gate MUST publish before a v2 receipt root can be written.
            SidecarStore.Commit(saveDirectory, new SidecarSnapshot(snapshot.Document, snapshot.NativeSave),
                snapshot.Document.Generation, checkpoint);
            return retained;
        }
    }
}
