using System;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public delegate bool AimSegmentTest(float ax, float ay, float az, float bx, float by, float bz);

    public static class ToolAimPolicy
    {
        public static bool Fits(PlayerPose pose, bool independent, float x, float y, float z, float yaw, float pitch,
            AimSegmentTest? clear, bool allowCreeper = false, bool allowChair = false)
        {
            pose = pose.WithIndependentAim(independent);
            return CameraOriginFits(pose, x, y, z, allowCreeper, clear) && CameraAimFits(pose, yaw, pitch, allowCreeper, allowChair);
        }

        // PlayerPose position is the walking-controller root. The native first-person
        // camera moves vertically during crouch, but stays within this bounded envelope.
        // Horizontal slack covers small camera/controller offsets without allowing a
        // client to place its ray origin at an arbitrary nearby wall or vehicle.
        public static bool CameraOriginFits(PlayerPose player, float cameraX, float cameraY, float cameraZ, bool allowCreeper = false, AimSegmentTest? clear = null)
        {
            if (!Finite(cameraX) || !Finite(cameraY) || !Finite(cameraZ)) return false;
            float dx = cameraX - player.X, dy = cameraY - player.Y, dz = cameraZ - player.Z;
            // AdvancedWalkerController.EnterCreeper lowers the camera by 1.32 m.
            float offset = allowCreeper && player.State == 3 ? 1.32f : 0;
            // The caller selects the mode sampled with the ray. Hand origins also require line of sight.
            if (player.IndependentAim) return IndependentAimPolicy.OriginFits(player, cameraX, cameraY, cameraZ, offset, clear);
            return dx * dx + dz * dz <= 0.65f * 0.65f && dy >= 0.35f - offset && dy <= 2.4f - offset;
        }

        public static bool CameraAimFits(PlayerPose player, float cameraYaw, float cameraPitch, bool allowCreeper = false, bool allowChair = false)
        {
            // PlayerState.Walking is wire value zero. Allow a wide angular delta so
            // a 10-20 Hz pose cannot reject normal quick camera movement.
            if ((player.State != 0 && !(allowCreeper && player.State == ItemInteractionPosePolicy.CreeperState) &&
                !(allowChair && player.State == ItemInteractionPosePolicy.ChairState)) || !Finite(cameraYaw) || cameraYaw < 0 || cameraYaw > 360 ||
                !Finite(cameraPitch) || cameraPitch < -90 || cameraPitch > 90) return false;
            // A tracked controller points independently of the head; its origin is bounded and
            // occlusion-checked by CameraOriginFits instead. Player-state rules above still apply.
            if (player.IndependentAim) return true;
            float yawDifference = Math.Abs(cameraYaw - player.Yaw) % 360f;
            if (yawDifference > 180f) yawDifference = 360f - yawDifference;
            return yawDifference <= 90f && Math.Abs(cameraPitch - player.Pitch) <= 60f;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
