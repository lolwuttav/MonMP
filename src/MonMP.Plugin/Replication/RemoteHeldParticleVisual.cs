using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Move only native particle-only children into the final tool presentation frame.
    // Native Play/Stop, renderer state and simulation remain the sole effect owner.
    // In particular, already emitted world-space water/spray keeps its trajectory.
    internal sealed class RemoteHeldParticleVisual : IDisposable
    {
        private sealed class Effect
        {
            internal Transform Source = null!, Copy = null!;
            internal Transform? Parent;
            internal Vector3 NativePosition, PresentedPosition;
            internal Quaternion NativeRotation, PresentedRotation;
            internal bool Rebased;
        }
        private readonly List<Effect> effects = new List<Effect>();

        internal void Bind(Transform nativeRoot, Transform original, Transform target)
        {
            // The item root and any gameplay/physics subtree always keep their native
            // transforms. Current water, flame/smoke and bottle/paint spray prefabs
            // contain only Transform, ParticleSystem and ParticleSystemRenderer here.
            if (original == nativeRoot || original.GetComponent<ParticleSystem>() == null) return;
            foreach (var effect in effects)
                if (original == effect.Source || original.IsChildOf(effect.Source)) return;
            foreach (var component in original.GetComponentsInChildren<Component>(true))
            {
                if (!(component is Transform || component is ParticleSystem || component is ParticleSystemRenderer)) return;
                if (component is ParticleSystem system &&
                    (system.trigger.enabled || system.collision.enabled &&
                        (system.collision.sendCollisionMessages || system.collision.colliderForce != 0))) return;
            }
            effects.Add(new Effect { Source = original, Copy = target, Parent = original.parent });
        }

        // Restore before the ordinary node copy samples native local transforms.
        // The parent stays native, so parent movement does not change this restore.
        internal void Restore()
        {
            List<Exception>? failures = null;
            foreach (var effect in effects)
            {
                try { Restore(effect); }
                catch (Exception ex) { (failures ??= new List<Exception>()).Add(ex); }
            }
            if (failures != null) throw new AggregateException("Remote held particle restore encountered failures.", failures);
        }

        private static void Restore(Effect effect)
        {
            if (!effect.Rebased) return;
            effect.Rebased = false;
            var source = effect.Source;
            if (source == null || source.parent != effect.Parent) return;
            // Native code may have changed an emitter while we owned its last pose.
            // Restore only the individual values still equal to our last write.
            if (source.localPosition == effect.PresentedPosition) source.localPosition = effect.NativePosition;
            if (source.localRotation == effect.PresentedRotation) source.localRotation = effect.NativeRotation;
        }

        internal void Present()
        {
            Restore();
            foreach (var effect in effects)
            {
                var source = effect.Source;
                if (source == null || effect.Copy == null || source.parent != effect.Parent) continue;
                effect.NativePosition = source.localPosition;
                effect.NativeRotation = source.localRotation;
                source.SetPositionAndRotation(effect.Copy.position, effect.Copy.rotation);
                effect.PresentedPosition = source.localPosition;
                effect.PresentedRotation = source.localRotation;
                effect.Rebased = true;
            }
        }

        internal void Reset()
        {
            try { Restore(); }
            finally { effects.Clear(); }
        }

        public void Dispose() => Reset();
    }
}
