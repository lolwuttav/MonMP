using System;
using System.Collections.Generic;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.GroundDetection;
using NWH.WheelController3D;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Resolves current visual ground hits through the native detector into independent local
    // variables, then checks the caches consumed by tire sounds, dust and skidmarks.
    internal sealed class RemoteSurfacePresentationProbe
    {
        private readonly Dictionary<WheelController, (SurfacePreset Preset, int Index, float MismatchedSince)> previous =
            new Dictionary<WheelController, (SurfacePreset, int, float)>();

        internal void Begin(VehicleController vehicle)
        {
            previous.Clear();
            if (vehicle.VehicleMultiplayerInstanceType != NWH.Vehicle.MultiplayerInstanceType.Remote)
                throw new InvalidOperationException("Surface presentation probe requires a remote observer.");
            // Force a real runtime refresh instead of accepting a coincidentally correct surface
            // left over from the observer's earlier local simulation.
            foreach (var wheel in vehicle.powertrain.wheels)
            {
                wheel.surfaceMapIndex = int.MinValue;
                wheel.surfacePreset = null;
            }
        }

        internal int Observe(VehicleController vehicle, out string evidence)
        {
            evidence = "";
            var detection = vehicle.groundDetection;
            if (vehicle.VehicleMultiplayerInstanceType != NWH.Vehicle.MultiplayerInstanceType.Remote ||
                detection?.Initialized != true || !detection.IsEnabled || detection.groundDetectionPreset?.fallbackSurfacePreset == null) return 0;
            int checkedWheels = 0;
            foreach (var wheel in vehicle.powertrain.wheels)
            {
                var controller = wheel.wheelController;
                if (controller == null) continue;
                if (!controller.isGrounded || controller.wheelHit?.raycastHit.transform == null) { previous.Remove(controller); continue; }
                int expectedIndex = -1;
                SurfacePreset expected = null!;
                if (!detection.GetCurrentSurfaceMap(controller, ref expectedIndex, ref expected) || expected == null) { previous.Remove(controller); continue; }
                // The detector may change after this frame's sound update at a material boundary.
                // Two uncapped render frames can be shorter than one physics step.
                // A stable mismatch must survive at least two physics steps and 100 ms.
                bool stable = previous.TryGetValue(controller, out var before) && before.Preset == expected && before.Index == expectedIndex;
                if (wheel.surfacePreset != expected || wheel.surfaceMapIndex != expectedIndex)
                {
                    float since = stable && before.MismatchedSince > 0 ? before.MismatchedSince : Time.unscaledTime;
                    previous[controller] = (expected, expectedIndex, since);
                    if (Time.unscaledTime - since < Mathf.Max(0.1f, Time.fixedDeltaTime * 2f)) continue;
                    throw new InvalidOperationException("Remote grounded wheel surface cache differs from native hit resolution: wheel=" + controller.name +
                        ", expected=" + expected.name + "/" + expectedIndex + ", actual=" + (wheel.surfacePreset == null ? "none" : wheel.surfacePreset.name) + "/" + wheel.surfaceMapIndex);
                }
                previous[controller] = (expected, expectedIndex, 0);
                if (!stable) continue;
                checkedWheels++;
                if (evidence.Length == 0) evidence = "wheel=" + controller.name + "; hit=" + controller.wheelHit.raycastHit.transform.name +
                    "; preset=" + expected.name + "; index=" + expectedIndex;
            }
            return checkedWheels;
        }
    }
}
