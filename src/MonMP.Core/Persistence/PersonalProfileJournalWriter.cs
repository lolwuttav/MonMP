using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MonMP.Core.Persistence
{
    // One active durable write and one latest replacement. No game objects,
    // callbacks into the caller, or unbounded task/completion queue are retained.
    public sealed class PersonalProfileJournalWriter
    {
        public sealed class Snapshot
        {
            public string Directory { get; }
            public PersonalProfileJournalBinding Binding { get; }
            public IReadOnlyList<SidecarProfile> Profiles { get; }
            public Snapshot(string directory, PersonalProfileJournalBinding binding, IEnumerable<SidecarProfile> profiles)
            {
                Directory = directory ?? throw new ArgumentNullException(nameof(directory));
                Binding = binding ?? throw new ArgumentNullException(nameof(binding));
                // Profiles and sections are immutable; copy only the collection
                // so later main-thread observations cannot change this write.
                Profiles = Array.AsReadOnly(profiles.ToArray());
            }
        }

        private readonly object sync = new object();
        private readonly Action<Snapshot> write;
        private Snapshot? pending;
        private bool running;
        private int completed;
        private Exception? failure;

        public PersonalProfileJournalWriter(Action<Snapshot>? write = null) => this.write = write ?? Commit;
        private static void Commit(Snapshot snapshot) =>
            PersonalProfileJournal.Commit(snapshot.Directory, snapshot.Binding, snapshot.Profiles);

        public void Submit(Snapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            lock (sync)
            {
                pending = snapshot;
                if (running) return;
                running = true;
                try
                {
                    if (!ThreadPool.QueueUserWorkItem(Run))
                        throw new InvalidOperationException("The personal journal worker could not be scheduled.");
                }
                catch { running = false; pending = null; throw; }
            }
        }

        private void Run(object? ignored)
        {
            while (true)
            {
                Snapshot snapshot;
                lock (sync) { snapshot = pending!; pending = null; }
                Exception? error = null;
                try { write(snapshot); }
                catch (Exception ex) { error = ex; }
                lock (sync)
                {
                    // Retain a failure until the main thread observes it, even
                    // if a newer pending snapshot succeeds in the meantime.
                    if (error != null) failure = error;
                    Volatile.Write(ref completed, 1);
                    if (pending != null) continue;
                    running = false;
                    Monitor.PulseAll(sync);
                    return;
                }
            }
        }

        public bool TakeCompletion(out Exception? error)
        {
            error = null;
            if (Volatile.Read(ref completed) == 0) return false;
            lock (sync)
            {
                if (completed == 0) return false;
                error = failure; failure = null; completed = 0;
                return true;
            }
        }

        public void Drain()
        {
            lock (sync) while (running) Monitor.Wait(sync);
        }

        // Binding rollover and forced shutdown flushes remain synchronous.
        // Their publication can never be followed by an older queued write.
        public void WriteSynchronously(Snapshot snapshot)
        {
            Drain();
            write(snapshot);
        }
    }
}
