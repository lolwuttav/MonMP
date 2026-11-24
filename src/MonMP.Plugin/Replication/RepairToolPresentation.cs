using System;
using System.Collections.Generic;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    internal static class RepairToolPresentation
    {
        internal const byte Rust = 1, Dent = 2, Solution = 3;
        private static readonly System.Reflection.FieldInfo LastUse = AccessTools.Field(typeof(Tool_RustSolution), "LastTimeUsed") ??
            throw new InvalidOperationException("Unsupported rust solution cooldown field.");
        private static readonly System.Reflection.FieldInfo Particle = AccessTools.Field(typeof(Tool_RustSolution), "PuishParticle") ??
            throw new InvalidOperationException("Unsupported rust solution particle field.");
        private static readonly List<(GameObject Root, float Until)> puffs = new List<(GameObject, float)>();
        private static Click? active;
        private static readonly Dictionary<byte, int> localCounts = new Dictionary<byte, int>(), remoteCounts = new Dictionary<byte, int>();

#if MONMP_DEVELOPMENT
        internal static int TestLocal(byte kind) => localCounts.TryGetValue(kind, out var count) ? count : 0;


        internal static int TestRemote(byte kind) => remoteCounts.TryGetValue(kind, out var count) ? count : 0;
#endif


        internal sealed class Click
        {
            internal Click? Previous;
            internal InteractableTools Tool = null!;
            internal byte Kind;
            internal bool Accepted;
            internal float Last;
            internal Vector3 At;
            internal ulong Target;
        }
        internal static Click? Begin(InteractableTools tool, byte kind)
        {
            if (PresentationReplays.Events?.IsHost != true || SharedPresentationEvents.Replaying || !tool.isEquiped) return null;
            var click = new Click { Previous = active, Tool = tool, Kind = kind, At = tool.transform.position,
                Target = MonMPPlugin.Instance?.World?.Items.EntityOf(tool) ?? 0,
                Last = kind == Solution ? (float)LastUse.GetValue(tool) : 0 };
            active = click;
            return click;
        }
        internal static void Result(byte kind, bool accepted)
        {
            if (active != null && active.Kind == kind && accepted) active.Accepted = true;
        }
        internal static void End(Click? click, bool completed)
        {
            if (click == null) return;
            if (ReferenceEquals(active, click)) active = click.Previous;
            if (!completed) return;
            bool accepted = click.Kind == Solution ? (float)LastUse.GetValue(click.Tool) != click.Last : click.Accepted;
            if (accepted)
            {

#if MONMP_DEVELOPMENT
                RecordLocal(click.Kind);
#endif

                PresentationReplays.Events?.Post(PresentationKind.RepairTool, click.Kind, click.Target, click.At);
            }
        }
        internal static void Announce(byte kind, ulong target, Vector3 at, uint actor) =>
            PresentationReplays.Events?.HostAnnounce(PresentationKind.RepairTool, kind, target, at, 0, actor, playHere: true);

        internal static void Local(byte kind)
        {
            using (SpatialSounds.Local()) Sound(kind);

#if MONMP_DEVELOPMENT
            RecordLocal(kind);
#endif

        }
#if MONMP_DEVELOPMENT
        internal static void RecordLocal(byte kind) => localCounts[kind] = TestLocal(kind) + 1;
#endif
        private static void Sound(byte kind)
        {
            var sound = Singleton<SOUND_Manager>.i;
            if (sound == null) return;
            switch (kind)
            {
                case Rust: sound.PlayOneShot2D(sound.Pack_GrabPlastic, _randomPitch: true); break;
                case Dent: sound.PlayOneShot2D(sound.Tool_Hammering, _randomPitch: true); break;
                case Solution: sound.PlayOneShot2D(sound.Tool_BottleSpray, _randomPitch: true); break;
            }
        }
        internal static void Play(PresentationEvent e)
        {
            if (e.Variant < Rust || e.Variant > Solution) return;
            var at = new Vector3(e.X, e.Y, e.Z);
            using (SpatialSounds.At(at, e.Actor)) Sound(e.Variant);
#if MONMP_DEVELOPMENT
            remoteCounts[e.Variant] = TestRemote(e.Variant) + 1;
#endif
            if (e.Variant == Solution) Puff(at, e.Target);
        }
        private static void Puff(Vector3 at, ulong toolId)
        {
            if (puffs.Count >= 16) return;
            ParticleSystem? original = null;
            var tool = MonMPPlugin.Instance?.World?.Items.ItemOf(toolId) as Tool_RustSolution;
            if (tool != null) original = Particle.GetValue(tool) as ParticleSystem;
            if (original == null) return;
            // Copy only particle modules and renderer settings, never the item hierarchy or scripts.
            var root = new GameObject("MonMP rust solution puff");
            puffs.Add((root, Time.unscaledTime + 3f));
            root.transform.position = at;
            root.transform.rotation = original.transform.rotation;
            var copy = root.AddComponent<ParticleSystem>();
            copy.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = copy.main; var sourceMain = original.main;
            main.loop = false; main.playOnAwake = false; main.duration = Mathf.Min(2f, sourceMain.duration);
            main.startLifetime = sourceMain.startLifetime; main.startSpeed = sourceMain.startSpeed;
            main.startSize = sourceMain.startSize; main.startColor = sourceMain.startColor;
            main.maxParticles = Mathf.Min(256, sourceMain.maxParticles);
            var emission = copy.emission; var sourceEmission = original.emission;
            emission.enabled = sourceEmission.enabled; emission.rateOverTime = sourceEmission.rateOverTime;
            var bursts = new ParticleSystem.Burst[sourceEmission.burstCount]; sourceEmission.GetBursts(bursts); emission.SetBursts(bursts);
            var shape = copy.shape; var sourceShape = original.shape;
            shape.enabled = sourceShape.enabled; shape.shapeType = sourceShape.shapeType;
            shape.angle = sourceShape.angle; shape.radius = sourceShape.radius;
            shape.scale = sourceShape.scale; shape.position = sourceShape.position; shape.rotation = sourceShape.rotation;
            shape.mesh = sourceShape.mesh;
            var renderer = copy.GetComponent<ParticleSystemRenderer>(); var sourceRenderer = original.GetComponent<ParticleSystemRenderer>();
            if (sourceRenderer != null)
            {
                renderer.sharedMaterial = sourceRenderer.sharedMaterial; renderer.renderMode = sourceRenderer.renderMode;
                renderer.mesh = sourceRenderer.mesh; renderer.alignment = sourceRenderer.alignment;
            }
            copy.Play();
        }
        internal static void Tick()
        {
            for (int i = puffs.Count - 1; i >= 0; i--)
                if (puffs[i].Root == null || Time.unscaledTime >= puffs[i].Until)
                { if (puffs[i].Root != null) Object.Destroy(puffs[i].Root); puffs.RemoveAt(i); }
        }
        internal static void Reset()
        {
            foreach (var puff in puffs) if (puff.Root != null) Object.Destroy(puff.Root);
            puffs.Clear(); active = null; localCounts.Clear(); remoteCounts.Clear();
        }
    }
}
