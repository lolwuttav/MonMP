using System;
using System.Linq;
using MonMP.Plugin.Replication;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal static class NativeLightingGuardTest
    {
        internal static void Verify(MonMPPlugin plugin)
        {
            var world = plugin.World!; var shared = plugin.Vehicles!;
            var button = UnityEngine.Object.FindObjectsOfType<Vehicle_Button_LightSwitch>()
                .First(b => shared.Handles(b.vehicleController));
            var vehicle = button.vehicleController;
            var state = NativeVehicleLighting.Capture(vehicle);
            vehicle.input.states.extraLights = true;
            NativeVehicleLighting.Apply(vehicle, state);
            Require(!vehicle.input.states.extraLights, "Restored lighting retained queued extra-light input.");
            bool before = vehicle.input.states.lowBeamLights;
            world.SaveGate.Begin();
            try
            {
                button.MouseDown(); button.MouseRightDown();
                Require(vehicle.input.states.lowBeamLights == before, "Save gate admitted native lighting commands.");
            }
            finally { world.SaveGate.Dispose(); }
            var fixture = new GameObject("MonMPUnregisteredLightingFixture"); fixture.SetActive(false);
            var control = new GameObject("MonMPExternalLightingControl"); control.SetActive(false);
            try
            {
                var unregistered = fixture.AddComponent<VehicleController>();
                var external = control.AddComponent<Vehicle_Button_LightSwitch>();
                external.vehicleController = unregistered;
                Require(!shared.Handles(unregistered), "Synthetic vehicle unexpectedly registered.");
                // No native Start or sound setup: reaching the native body would fail.
                // The separate control object also proves routing uses the actual target,
                // rather than assuming the controller must be a parent of the button.
                external.MouseDown(); external.MouseRightDown();
                Require(!unregistered.input.states.lowBeamLights, "Unregistered vehicle accepted lighting input.");
            }
            finally { UnityEngine.Object.Destroy(control); UnityEngine.Object.Destroy(fixture); }
        }
        private static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Lighting guards: " + message); }
    }
}
