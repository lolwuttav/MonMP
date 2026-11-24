using System.IO;
using MonMP.Core.Protocol;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class NativePlayerSpawn
    {
        // CMF.AdvancedWalkerController.HandleUnderMapTeleport uses this same threshold.
        // Do not invent tighter map/terrain bounds that would reject a valid saved root.
        internal const float MinimumY = -150f;
        private static Gameplay? scene;
        private static PlayerPose? authored;

        // SaveSystem.Load runs before its coroutine can call LoadPlayer/Gameplay.LoadFromSave.
        // With no player save, native loading leaves this scene-authored root in place.
        internal static void Capture(Gameplay? gameplay)
        {
            if (gameplay == null || scene == gameplay) return;
            scene = gameplay; authored = null;
            if (gameplay.PlayerWalking?.playerCameraController == null) return;
            var at = gameplay.PlayerWalking.transform.position;
            var spawn = new PlayerPose(at.x, at.y, at.z, gameplay.PlayerWalking.playerCameraController.transform.eulerAngles.y, 0);
            if (Usable(spawn)) authored = spawn;
        }

        internal static PlayerPose For(Gameplay? gameplay)
        {
            if (gameplay != null && scene == gameplay && authored.HasValue) return authored.Value;
            // A missing startup capture must never borrow a host's live/saved player root.
            // The native under-map/death recovery destination remains a usable fallback.
            var home = gameplay?.HouseSpawnLocation;
            if (home != null)
            {
                var at = home.position;
                var spawn = new PlayerPose(at.x, at.y, at.z, home.eulerAngles.y, 0);
                if (Usable(spawn)) return spawn;
            }
            // Menu-side session options are not advertised as a host world. The real host
            // always has the loaded scene capture or its native home recovery transform.
            return new PlayerPose(0, 0, 0, 0, 0);
        }

        private static bool Usable(PlayerPose pose)
        {
            if (pose.Y < MinimumY) return false;
            try { pose.Validate(); return true; }
            catch (InvalidDataException) { return false; }
        }

        internal static void PlaceWalking(Gameplay gameplay, PlayerPose spawn)
        {
            var at = new Vector3(spawn.X, spawn.Y, spawn.Z);
            var walker = gameplay.PlayerWalking.transform;
            if (walker.parent != null) walker.SetParent(null, true);
            walker.SetPositionAndRotation(at, Quaternion.identity);
            gameplay.PlayerWalking.playerCameraController.SetRotationAngles(0f, spawn.Yaw);
            gameplay.PlayerWalking.CancelVelocity();
            if (gameplay.Get_Player_Rigidbody != null)
            {
                gameplay.Get_Player_Rigidbody.position = at;
                gameplay.Get_Player_Rigidbody.rotation = Quaternion.identity;
                gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                gameplay.Get_Player_Rigidbody.angularVelocity = Vector3.zero;
            }
        }
    }
}
