using System;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // After the one-use output ticket is consumed, only its existing short-lived visual
    // may move. Older reports cannot rewind that visual or renew its native lifetime.
    internal static class FishingTrashCosmeticRefresh
    {
        internal static bool TryApply(FishingTrashCosmeticState previous, float expiresAt, uint peer,
            FishingTrashCosmeticRequest request, float now, out FishingTrashCosmeticState state, out float expiry)
        {
            state = previous; expiry = expiresAt;
            if (now >= expiresAt || previous.Owner != peer || previous.Rod != request.Rod ||
                previous.Generation != request.Generation || previous.Ticket != request.Ticket ||
                previous.Template != request.Template || request.RemainingMilliseconds == 0 ||
                request.RemainingMilliseconds >= previous.RemainingMilliseconds) return false;
            float seconds = (previous.RemainingMilliseconds - request.RemainingMilliseconds) * 0.001f;
            float elapsed = (6000 - request.RemainingMilliseconds) * 0.001f;
            float priorElapsed = (6000 - previous.RemainingMilliseconds) * 0.001f;
            // Native trash starts a new gravity body at release. Permit its full six-second
            // fall plus bounded carrier/contact motion, even after the angler walks away.
            float gravity = Physics.gravity.magnitude;
            if (float.IsNaN(gravity) || float.IsInfinity(gravity)) return false;
            float travel = 1f + 32f * seconds + 0.5f * gravity * (elapsed * elapsed - priorElapsed * priorElapsed);
            float x = request.Pose.X - previous.Pose.X, y = request.Pose.Y - previous.Pose.Y, z = request.Pose.Z - previous.Pose.Z;
            if (x * x + y * y + z * z > travel * travel) return false;
            expiry = Math.Min(expiresAt, now + request.RemainingMilliseconds * 0.001f);
            state = new FishingTrashCosmeticState(previous.Rod, previous.Owner, previous.Generation,
                previous.Ticket, previous.Template, request.Pose, request.RemainingMilliseconds);
            return true;
        }
    }
}
