using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Change a puff's simulation frame without moving particles that have already been emitted.
    // Keep the last native frame matrix so a destroyed vehicle can release its puff into world space.
    internal sealed class RemoteExhaleSpace
    {
        private readonly ParticleSystem particles;
        private ParticleSystem.Particle[] buffer = new ParticleSystem.Particle[0];
        private Transform? anchor;
        private bool anchored;
        private Matrix4x4 frameToWorld = Matrix4x4.identity;
        private Quaternion frameRotation = Quaternion.identity;

        internal RemoteExhaleSpace(ParticleSystem particles) => this.particles = particles;

        internal void KeepVehicle(Transform? vehicle)
        {
            RememberFrame();
            if (anchored && (anchor == null || vehicle != anchor)) SetAnchor(null);
        }

        private void RememberFrame()
        {
            if (anchored && anchor != null)
            { frameToWorld = anchor.localToWorldMatrix; frameRotation = anchor.rotation; }
        }

        internal void SetAnchor(Transform? next)
        {
            RememberFrame();
            bool nextAnchored = next != null;
            if (anchored == nextAnchored && (!anchored || anchor == next)) return;
            int capacity = particles.main.maxParticles;
            if (buffer.Length < capacity) buffer = new ParticleSystem.Particle[capacity];
            int count = particles.GetParticles(buffer);
            var toFrame = nextAnchored ? next!.worldToLocalMatrix : Matrix4x4.identity;
            var positionChange = toFrame * frameToWorld;
            var rotationChange = (nextAnchored ? Quaternion.Inverse(next!.rotation) : Quaternion.identity) * frameRotation;
            for (int i = 0; i < count; i++)
            {
                var particle = buffer[i];
                particle.position = positionChange.MultiplyPoint3x4(particle.position);
                particle.velocity = positionChange.MultiplyVector(particle.velocity);
                particle.rotation3D = (rotationChange * Quaternion.Euler(particle.rotation3D)).eulerAngles;
                particle.axisOfRotation = rotationChange * particle.axisOfRotation;
                particle.angularVelocity3D = rotationChange * particle.angularVelocity3D;
                buffer[i] = particle;
            }
            var main = particles.main;
            main.customSimulationSpace = next;
            main.simulationSpace = nextAnchored ? ParticleSystemSimulationSpace.Custom : ParticleSystemSimulationSpace.World;
            particles.SetParticles(buffer, count);
            anchor = next; anchored = nextAnchored;
            frameToWorld = nextAnchored ? next!.localToWorldMatrix : Matrix4x4.identity;
            frameRotation = nextAnchored ? next!.rotation : Quaternion.identity;
        }
    }
}
