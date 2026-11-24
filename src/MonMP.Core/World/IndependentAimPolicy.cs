using System;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    // Host admission for a tool ray sampled in independent aim mode
    // (a tracked controller pointing separately from the head). The declaration is not
    // authentication: it trades the head-camera envelope for a hand envelope around the same
    // admitted body root plus an occlusion rule, so a hand origin cannot sit behind a wall.
    // Pose freshness, tool leases, sequences, target reach and the host's own target raycast
    // are enforced by the callers exactly as for ordinary peers.
    public static class IndependentAimPolicy
    {
        // Head-camera slack (0.65 m) plus an extended arm. Measured from the walking root.
        public const float HorizontalReach = 1.25f;
        public const float MinimumHeight = -0.25f, MaximumHeight = 2.5f;
        public const float StandingHead = 1.6f, CrouchedHead = 0.95f, ChestFraction = 0.65f;

        public static bool EnvelopeFits(PlayerPose player, float x, float y, float z, float creeperOffset = 0)
        {
            if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(creeperOffset)) return false;
            float dx = x - player.X, dy = y - player.Y, dz = z - player.Z;
            return dx * dx + dz * dz <= HorizontalReach * HorizontalReach &&
                dy >= MinimumHeight - creeperOffset && dy <= MaximumHeight - creeperOffset;
        }

        public static float HeadHeight(PlayerPose player, float creeperOffset = 0) =>
            Math.Max(0.2f, (player.Crouched ? CrouchedHead : StandingHead) - creeperOffset);

        public static bool OriginFits(PlayerPose player, float x, float y, float z, float creeperOffset = 0, AimSegmentTest? clear = null)
        {
            if (!player.IndependentAim || player.Spectating || player.Rest != PlayerRest.None || !EnvelopeFits(player, x, y, z, creeperOffset)) return false;
            if (clear == null) return false;
            float head = HeadHeight(player, creeperOffset);
            try
            {
                // Either point of the admitted body column may see the hand: a low obstacle
                // (workbench, fender) can hide it from the chest while the head sees over it.
                return clear(player.X, player.Y + head, player.Z, x, y, z) ||
                    clear(player.X, player.Y + head * ChestFraction, player.Z, x, y, z);
            }
            catch { return false; }
        }

        // --- What counts as a solid between the body column and the hand -----------------------
        // The plugin's Unity raycast classifies each collider it crossed and asks these rules, so
        // the decision itself is testable without physics.

        // Sensors and player bodies are not walls. Without authenticated actor/tool
        // context, every other solid blocks: installed parts, furniture, loose/stacked
        // items and held objects. Spatial proximity never proves item ownership.
        public static bool HitBlocks(bool trigger, bool playerBody) => !trigger && !playerBody;

        // A full hit buffer cannot prove the path clear, so it refuses.
        public static bool PathClear(int hits, int capacity, Func<int, bool> hitBlocks)
        {
            if (hitBlocks == null || hits < 0 || capacity <= 0 || hits >= capacity) return false;
            for (int i = 0; i < hits; i++) if (hitBlocks(i)) return false;
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
