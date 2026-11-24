using System.Collections.Generic;
using MonMP.Core.Protocol;
using UnityEngine.InputSystem;
using UnityEngine;

namespace MonMP.Plugin.UI
{
    // Shared presence follows avatar movement and gameplay actions. Camera-only input and stationary
    // menu/chat activity do not refresh it. The recent-action pose bit keeps every peer's display
    // and sleep quorum in agreement without a separate message.
    internal sealed class PlayerActivity
    {
        internal const float AfkSeconds = 150f;
        private const float ActionPulseSeconds = 0.4f;
        private readonly Dictionary<uint, (Vector3 Position, float Active)> remote =
            new Dictionary<uint, (Vector3, float)>();
        private Vector3 localPosition;
        private float localActive, actionPulseUntil;

        internal bool RecentAction => Time.unscaledTime < actionPulseUntil;

        internal void RecordAction()
        {
            localActive = Time.unscaledTime;
            actionPulseUntil = localActive + ActionPulseSeconds;
        }

        internal void Observe(uint peer, PlayerPose pose)
        {
            var at = new Vector3(pose.X, pose.Y, pose.Z);
            float now = Time.unscaledTime;
            if (!remote.TryGetValue(peer, out var last))
            {
                remote[peer] = (at, now);
                return;
            }
            bool action = pose.RecentAction;
            bool moved = pose.Rest == PlayerRest.None && PlayerActivityPolicy.IsActive(last.Position.x, last.Position.y, last.Position.z,
                at.x, at.y, at.z, false);
            if (action || moved) remote[peer] = (at, now);
        }

        internal void Remove(uint peer) => remote.Remove(peer);
        internal void Clear() { remote.Clear(); localActive = Time.unscaledTime; actionPulseUntil = 0; }

        internal void TickLocal(Gameplay? gameplay, bool allowGameplayActions)
        {
            float now = Time.unscaledTime;
            var walker = gameplay != null ? gameplay.PlayerWalking : null;
            var position = walker != null ? walker.transform.position : localPosition;
            bool action = allowGameplayActions && IsGameplayActionPressed(gameplay);
            bool canMove = gameplay?.Get_IsSleeping != true && gameplay?.Get_IsDead != true;
            bool active = action || canMove && PlayerActivityPolicy.IsActive(localPosition.x, localPosition.y, localPosition.z,
                position.x, position.y, position.z, false);
            if (active) { localActive = now; localPosition = position; }
            if (action) actionPulseUntil = now + ActionPulseSeconds;
        }

        private static bool IsGameplayActionPressed(Gameplay? gameplay)
        {
            if (gameplay == null) return false;
            var assets = Singleton<GameAssets>.i;
            if (assets == null || assets.inputs == null) return false;
            var player = assets.inputs.Player;
            if (Pressed(player.MoveVertical) || Pressed(player.MoveHorizontal) || Pressed(player.Use) ||
                Pressed(player.LeftClick) || Pressed(player.RightClick) || Pressed(player.Jump) ||
                Pressed(player.Crouch) || Pressed(player.Run) || Pressed(player.Smoke) ||
                Pressed(player.Sorry) || Pressed(player.Spit) || Pressed(player.Slur) || Pressed(player.FlashLight) ||
                Pressed(player.HideUI) || Pressed(player.Scroll) || Pressed(player.Map) || Pressed(player.GameConsole)) return true;

            // VehicleControls is enabled only while the native vehicle controller owns input.
            // Include its stationary gameplay controls, while excluding the camera toggle.
            var vehicleMap = assets.vehicleInputs?.asset?.FindActionMap("Vehicle Controls", false);
            if (vehicleMap != null && vehicleMap.enabled)
                foreach (var action in vehicleMap.actions)
                    if (!string.Equals(action.name, "ChangeCamera", System.StringComparison.OrdinalIgnoreCase) && Pressed(action))
                        return true;
            return false;
        }

        private static bool Pressed(InputAction action) => action.enabled && (action.IsPressed() || action.WasPressedThisFrame());

        internal float IdleSeconds(uint peer, uint localPeer)
        {
            float now = Time.unscaledTime;
            if (peer == localPeer) return now - localActive;
            return remote.TryGetValue(peer, out var last) ? now - last.Active : 0f;
        }

        internal bool IsAfk(uint peer, uint localPeer) => IdleSeconds(peer, localPeer) >= AfkSeconds;
    }
}
