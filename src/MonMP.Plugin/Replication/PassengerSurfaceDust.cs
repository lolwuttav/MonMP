using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Effects;
using NWH.VehiclePhysics2.GroundDetection;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Surface dust is a view effect. Other peers still see this vehicle's trail; its guest
    // passengers do not need that trail crossing the cab, rack or bus camera.
    internal static class PassengerSurfaceDust
    {
        private static readonly FieldInfo? Systems = AccessTools.Field(typeof(SurfaceParticleManager), "particleSystems");
        private static readonly FieldInfo? Surface = AccessTools.Field(typeof(SurfaceParticleSystem), "surfacePreset");
        // Native Mono faulted in ConditionalWeakTable.TryGetValue, including with concrete
        // factories. These Unity callbacks run on the main thread; use bounded identity
        // buckets and immutable weak handles instead of the ephemeron-table path.
        private const int HistoryBucketCount = 256;
        private const int MaximumHistories = 1024;
        private static readonly WheelHistory?[] HistoryBuckets = new WheelHistory?[HistoryBucketCount];
        private static int historyCount, scavengeBucket;
        private static WeakReference<object>? saturatedSession;

        private sealed class WheelHistory
        {
            internal readonly int Hash;
            internal readonly WeakReference<SurfaceParticleSystem> Wheel;
            internal readonly DustHistory History;
            internal WheelHistory? Next;

            internal WheelHistory(SurfaceParticleSystem wheel, int hash, bool preserveInitialParticles, WheelHistory? next)
            {
                Hash = hash;
                Wheel = new WeakReference<SurfaceParticleSystem>(wheel);
                History = new DustHistory(preserveInitialParticles);
                Next = next;
            }
        }

        private static DustHistory? GetHistory(SurfaceParticleSystem system, object activeSession)
        {
            // One bucket per access releases dead keys and their buffers during normal play.
            // Runtime identity plus ReferenceEquals keeps virtual equality and hash collisions
            // from aliasing two native wheel systems. Both scans are capped by MaximumHistories.
            Scavenge(scavengeBucket);
            scavengeBucket = (scavengeBucket + 1) & (HistoryBucketCount - 1);
            int hash = RuntimeHelpers.GetHashCode(system);
            int bucket = hash & (HistoryBucketCount - 1);
            for (var entry = HistoryBuckets[bucket]; entry != null; entry = entry.Next)
                if (entry.Hash == hash && entry.Wheel.TryGetTarget(out var wheel) && ReferenceEquals(wheel, system))
                    return entry.History;

            if (historyCount == MaximumHistories)
            {
                // Make space for scene churn before declining a genuinely full live cache.
                for (int i = 0; i < HistoryBucketCount; i++) Scavenge(i);
                if (historyCount == MaximumHistories)
                {
                    if (saturatedSession == null || !saturatedSession.TryGetTarget(out var previous) || !ReferenceEquals(previous, activeSession))
                        saturatedSession = new WeakReference<object>(activeSession);
                    return null;
                }
            }
            // A previously untracked wheel may still hold smoke under a current Dust preset.
            // After saturation, retain initial particles conservatively when admitting a wheel.
            bool preserveInitial = saturatedSession != null && saturatedSession.TryGetTarget(out var saturated) && ReferenceEquals(saturated, activeSession);
            var created = new WheelHistory(system, hash, preserveInitial, HistoryBuckets[bucket]);
            HistoryBuckets[bucket] = created;
            historyCount++;
            return created.History;
        }

        private static void Scavenge(int bucket)
        {
            WheelHistory? previous = null;
            var entry = HistoryBuckets[bucket];
            while (entry != null)
            {
                var next = entry.Next;
                if (!entry.Wheel.TryGetTarget(out var wheel) || wheel.particlePS == null)
                {
                    if (previous == null) HistoryBuckets[bucket] = next;
                    else previous.Next = next;
                    historyCount--;
                }
                else previous = entry;
                entry = next;
            }
        }

        private readonly struct ParticleKey : IEquatable<ParticleKey>
        {
            private readonly uint seed, color;
            private readonly float lifetime;
            internal ParticleKey(ParticleSystem.Particle particle)
            {
                seed = particle.randomSeed; lifetime = particle.startLifetime;
                var tint = particle.startColor;
                color = (uint)(tint.r | tint.g << 8 | tint.b << 16 | tint.a << 24);
            }
            public bool Equals(ParticleKey other) => seed == other.seed && color == other.color && lifetime.Equals(other.lifetime);
            public override bool Equals(object? other) => other is ParticleKey key && Equals(key);
            public override int GetHashCode() => unchecked(((int)seed * 397 ^ (int)color) * 397 ^ lifetime.GetHashCode());
        }

        // One reusable buffer per native wheel system. Particle seeds remain stable over
        // their lifetime, so a switch to tire smoke cannot hide or misclassify old dust.
        private sealed class DustHistory
        {
            internal DustHistory(bool preserveInitialParticles) => this.preserveInitialParticles = preserveInitialParticles;
            // Never keep the session/world alive or retarget a weak handle: SetTarget previously
            // crashed Mono in GCHandle.set_Target on reconnect. Replace the weak reference itself.
            private WeakReference<object>? session;
            private bool preserveInitialParticles;
            internal bool PreviousDust;
            internal bool PassengerCleaned;
            private bool initialized;
            private ParticleSystem.Particle[] particles = new ParticleSystem.Particle[0];
            private Dictionary<ParticleKey, bool> kinds = new Dictionary<ParticleKey, bool>();
            private HashSet<ParticleKey> live = new HashSet<ParticleKey>();
            private readonly List<ParticleKey> expired = new List<ParticleKey>();

            internal void Prepare(object activeSession, SurfacePreset? previousSurface, ParticleSystem system)
            {
                if (session == null || !session.TryGetTarget(out var previous) || !ReferenceEquals(previous, activeSession))
                {
                    session = new WeakReference<object>(activeSession);
                    initialized = false; PassengerCleaned = false; kinds.Clear();
                }
                if (!initialized)
                {
                    if (preserveInitialParticles)
                    {
                        PreviousDust = false;
                        Observe(system);
                        preserveInitialParticles = false;
                    }
                    PreviousDust = previousSurface?.particleType == SurfacePreset.ParticleType.Dust;
                    initialized = true;
                }
            }

            internal void Observe(ParticleSystem system)
            {
                int capacity = system.main.maxParticles;
                if (particles.Length < capacity)
                {
                    particles = new ParticleSystem.Particle[capacity];
                    var previousKinds = kinds;
                    kinds = new Dictionary<ParticleKey, bool>(capacity);
                    foreach (var pair in previousKinds) kinds.Add(pair.Key, pair.Value);
                    live = new HashSet<ParticleKey>(capacity);
                    expired.Capacity = capacity;
                }
                int count = system.GetParticles(particles);
                live.Clear();
                for (int i = 0; i < count; i++)
                {
                    var key = new ParticleKey(particles[i]);
                    // Identical native properties cannot distinguish two live particles.
                    // Keep ambiguous particles rather than hide legitimate tire smoke.
                    if (!live.Add(key)) kinds[key] = false;
                    else if (!kinds.ContainsKey(key)) kinds.Add(key, PreviousDust);
                }
                expired.Clear();
                foreach (var pair in kinds)
                    if (!live.Contains(pair.Key)) expired.Add(pair.Key);
                foreach (var key in expired) kinds.Remove(key);
            }

            internal void RemoveDust(ParticleSystem system)
            {
                int count = system.GetParticles(particles), kept = 0;
                expired.Clear();
                for (int i = 0; i < count; i++)
                {
                    var key = new ParticleKey(particles[i]);
                    if (kinds.TryGetValue(key, out bool dust) && dust) { expired.Add(key); continue; }
                    particles[kept++] = particles[i];
                }
                foreach (var key in expired) kinds.Remove(key);
                if (kept != count) system.SetParticles(particles, kept);
            }
        }

        private static VehicleController? CurrentVehicle(Gameplay? gameplay)
        {
            var session = MonMPPlugin.Instance?.Session;
            if (session?.IsRunning != true || session.IsHost || gameplay == null) return null;
            if (gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger)
                return gameplay.Get_CurrentVehicleController as VehicleController;
            return MonMPPlugin.Instance?.World?.ExtraSeats.CurrentLocalPassengerVehicle;
        }

        internal static void BeforeUpdate(SurfaceParticleSystem system, SurfacePreset? previousSurface)
        {
            // Avoid invoking the native singleton getter during offline/scene teardown.
            var session = MonMPPlugin.Instance?.Session;
            if (session?.IsRunning != true || session.IsHost || system.particlePS == null) return;
            GetHistory(system, session)?.Prepare(session, previousSurface, system.particlePS);
        }

        internal static void AfterUpdate(SurfaceParticleSystem system, VehicleController? vehicle, SurfacePreset? surface)
        {
            var session = MonMPPlugin.Instance?.Session;
            if (session?.IsRunning != true || session.IsHost || system.particlePS == null) return;
            var history = GetHistory(system, session);
            bool dust = surface?.particleType == SurfacePreset.ParticleType.Dust;
            bool passenger = vehicle != null && CurrentVehicle(Singleton<Gameplay>.i) == vehicle;
            if (history == null)
            {
                // More than 1,024 live tracked wheels: preserve unclassified existing smoke,
                // but keep newly emitted dust out of this passenger's view.
                if (passenger && dust) StopDustEmission(system.particlePS);
                return;
            }
            // Native Update only changes emission settings. Snapshot existing particles
            // at a type transition, before Unity emits the next kind, or upon entry.
            // Ordinary unoccupied wheel updates never copy/classify particle buffers.
            if (dust != history.PreviousDust || passenger && !history.PassengerCleaned)
            {
                history.Observe(system.particlePS);
                if (passenger) history.RemoveDust(system.particlePS);
            }
            if (passenger && dust) StopDustEmission(system.particlePS);
            // Native Update has chosen the next emission type. Airborne wheels emit
            // nothing, but their already emitted dust stays classified until it dies.
            history.PreviousDust = dust;
            history.PassengerCleaned = passenger;
        }

        internal static void ClearOnSeatTransition(Gameplay gameplay)
        {
            var vehicle = CurrentVehicle(gameplay);
            var manager = vehicle?.effectsManager?.surfaceParticleManager;
            if (manager == null || Systems?.GetValue(manager) is not IEnumerable<SurfaceParticleSystem> systems) return;
            foreach (var system in systems)
            {
                if (system.particlePS == null) continue;
                var surface = Surface?.GetValue(system) as SurfacePreset;
                var session = MonMPPlugin.Instance!.Session!;
                var history = GetHistory(system, session);
                if (history != null)
                {
                    history.Prepare(session, surface, system.particlePS);
                    history.Observe(system.particlePS);
                    history.RemoveDust(system.particlePS);
                    history.PassengerCleaned = true;
                }
                if (surface?.particleType == SurfacePreset.ParticleType.Dust) StopDustEmission(system.particlePS);
            }
        }

        private static void StopDustEmission(ParticleSystem system)
        {
            var emission = system.emission;
            emission.rateOverTime = 0f;
            emission.rateOverDistance = 0f;
            // Keep it playing: native UpdateParticles sets rates again after getting up,
            // but never calls Play. Exhaust and wheel chunks use separate systems.
        }
    }
}
