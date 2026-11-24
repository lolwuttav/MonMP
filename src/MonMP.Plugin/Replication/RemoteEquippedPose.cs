using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The caller's explicit equipped metadata selects this path. A tool's CLR type alone
    // cannot distinguish native equip from carrying the same object in a world pose.
    internal sealed class RemoteEquippedPose
    {
        private static readonly FieldInfo? Locations = AccessTools.Field(typeof(PlayerInteractable), "toolLocations");
        private readonly Dictionary<(ToolsType Type, string Variant), Transform> anchors = new Dictionary<(ToolsType, string), Transform>();
        private PlayerInteractable? player;
        private Transform? camera;
        private Vector3 playerFromCameraPosition;
        private Quaternion playerFromCameraRotation;
        private bool frameReady;

        internal bool TryGet(InteractableItems item, Gameplay gameplay, Vector3 eyes, Quaternion look,
            out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = Quaternion.identity;
            if (item == null || !(item is InteractableTools tool) || gameplay == null) return false;
            var current = gameplay.Player;
            if (current == null || current.mainCamera == null) return false;
            var currentCamera = current.mainCamera.transform;
            if (player != current || camera != currentCamera)
            {
                player = current; camera = currentCamera; anchors.Clear(); frameReady = false;
            }
            var key = (Type: tool.toolsType, Variant: HeldGripOverrides.VariantOf(tool));
            if (!anchors.TryGetValue(key, out var location) || location == null)
            {
                location = null;
                if (tool.toolsType == ToolsType.HoistPuller) location = current.Tool_HoistPuller_Location;
                else if (Locations?.GetValue(current) is InteractableToolLocation[] locations)
                {
                    int skip = key.Variant == "spanner-second" ? 1 : 0;
                    foreach (var candidate in locations)
                        if (candidate != null && candidate.toolsType == tool.toolsType)
                        {
                            if (skip-- > 0) continue;
                            location = candidate.Location; break;
                        }
                }
                if (location == null) return false;
                anchors[key] = location;
            }
            if (!frameReady)
            {
                playerFromCameraPosition = currentCamera.InverseTransformPoint(current.transform.position);
                playerFromCameraRotation = Quaternion.Inverse(currentCamera.rotation) * current.transform.rotation;
                frameReady = true;
            }
            Quaternion playerRotation = look * playerFromCameraRotation;
            Vector3 playerPosition = eyes + look * playerFromCameraPosition;
            // Native EquipTool copies local values, even when the anchor has another parent.
            position = playerPosition + playerRotation * location.localPosition;
            rotation = playerRotation * location.localRotation;
            // Native casting rewrites the equipped rod's localEulerAngles as Original - right*angle.
            if (item is Tool_FishingRod rod && RemoteFishing.SwingDegrees(rod) is float swing && swing != 0f)
                rotation = playerRotation * Quaternion.Euler(location.localEulerAngles - Vector3.right * swing);
            return true;
        }
    }
}
