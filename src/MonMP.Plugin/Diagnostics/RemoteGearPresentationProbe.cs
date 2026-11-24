using System;
using HarmonyLib;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Powertrain;
using NWH.VehiclePhysics2.VehicleGUI;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal static class RemoteGearPresentationProbe
    {
        internal static int Verify(VehicleController vehicle, int receivedGear)
        {
            if (vehicle.input.IsOn || vehicle.VehicleMultiplayerInstanceType != NWH.Vehicle.MultiplayerInstanceType.Remote)
                throw new InvalidOperationException("Gear presentation probe requires a remote replica with player input disabled.");
            var transmission = vehicle.powertrain.transmission;
            if (transmission.Gear != receivedGear)
                throw new InvalidOperationException("Remote native transmission differs from the received gear.");
            string expected = transmission.TransmissionType == TransmissionComponent.Type.AutomaticSequential && receivedGear > 0
                ? "D" : transmission.GearName;
            int knobs = 0;
            foreach (var dash in vehicle.GetComponentsInChildren<DashGUIController>(true))
            {
                if (dash.vehicleController != vehicle || !dash.useDigitalGearGauge) continue;
                // With the ignition off the native dashboard owns the blank readout.
                if (vehicle.Get_IgnitionPosition > 0 && dash.digitalGearGauge != null)
                {
                    if (dash.digitalGearGauge.stringValue != expected)
                        throw new InvalidOperationException("Remote digital gear stayed at '" + dash.digitalGearGauge.stringValue + "'; expected '" + expected + "' before observer entry.");
                }
                if (!dash.useVisualGearKnob) continue;
                knobs += VerifyKnob(dash.visualGearKnob_Manual, expected);
                knobs += VerifyKnob(dash.visualGearKnob_Auto, expected);
            }
            // The stock ATV has no digital gear display. Validate every configured
            // display/knob, while the truck fixture separately requires knob evidence.
            return knobs;
        }

        private static int VerifyKnob(Vehicle_Shifter? shifter, string expected)
        {
            if (shifter == null) return 0;
            var knob = AccessTools.Field(typeof(Vehicle_Shifter), "Knob").GetValue(shifter) as Transform;
            var positions = AccessTools.Field(typeof(Vehicle_Shifter), "shifter_Positions").GetValue(shifter) as Vehicle_Shifter_Position[];
            if (knob == null || positions == null) throw new InvalidOperationException("Native shifter has no serialized knob/positions.");
            foreach (var position in positions)
            {
                if (position.GearName != expected) continue;
                float error = Quaternion.Angle(knob.localRotation, Quaternion.Euler(position.Rotation));
                if (error > 0.1f)
                    throw new InvalidOperationException("Remote shifter rotation differs from native '" + expected + "' position by " + error.ToString("F2") + " degrees before observer entry.");
                return 1;
            }
            throw new InvalidOperationException("Native shifter has no serialized position for '" + expected + "'.");
        }
    }
}
