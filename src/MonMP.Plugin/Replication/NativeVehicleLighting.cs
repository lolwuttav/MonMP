using System;
using System.Collections.Generic;
using HarmonyLib;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Effects;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class NativeVehicleLighting
    {
        private static readonly AccessTools.FieldRef<LightsMananger, bool> Hazard = AccessTools.FieldRefAccess<LightsMananger, bool>("_hazardLightsOn");
        private static readonly AccessTools.FieldRef<LightsMananger, bool> Left = AccessTools.FieldRefAccess<LightsMananger, bool>("_leftBlinkersOn");
        private static readonly AccessTools.FieldRef<LightsMananger, bool> Right = AccessTools.FieldRefAccess<LightsMananger, bool>("_rightBlinkersOn");
        private static readonly AccessTools.FieldRef<LightsMananger, float> LeftTime = AccessTools.FieldRefAccess<LightsMananger, float>("_leftBlinkerTurnOnTime");
        private static readonly AccessTools.FieldRef<LightsMananger, float> RightTime = AccessTools.FieldRefAccess<LightsMananger, float>("_rightBlinkerTurnOnTime");
        private static readonly AccessTools.FieldRef<Vehicle_Button_ExtraLights, VehicleController> ExtraVehicle =
            AccessTools.FieldRefAccess<Vehicle_Button_ExtraLights, VehicleController>("vehicleController");
        private static readonly AccessTools.FieldRef<Vehicle_Button_ExtraLights, Vector3> ExtraOn =
            AccessTools.FieldRefAccess<Vehicle_Button_ExtraLights, Vector3>("RotationOn");
        private static readonly AccessTools.FieldRef<Vehicle_Button_ExtraLights, Vector3> ExtraOff =
            AccessTools.FieldRefAccess<Vehicle_Button_ExtraLights, Vector3>("RotationOff");
        private sealed class ExtraVisuals
        {
            internal Vehicle_Button_ExtraLights[] Controls = Array.Empty<Vehicle_Button_ExtraLights>();
            internal bool Applied, On, PartsApplied, PartsOn, PartsWarned;
            internal float CheckedAt, PartsCheckedAt;
        }
        // This game's Mono can throw InvalidCastException inside a weak table.
        // Keep this small scene cache bounded and clear it with its vehicle owner.
        private static readonly Dictionary<VehicleController, ExtraVisuals> ExtraSwitches =
            new Dictionary<VehicleController, ExtraVisuals>();
        internal static void ResetExtraVisuals() => ExtraSwitches.Clear();
        // Preserve protocol bit order without allocating a temporary array on every vehicle sample.
        // Read each native field afresh so replacement lamp groups are observed immediately.
        private static VehicleLight Lamp(LightsMananger m, int index) => index switch
        {
            0 => m.brakeLights, 1 => m.tailLights, 2 => m.reverseLights, 3 => m.lowBeamLights,
            4 => m.highBeamLights, 5 => m.leftBlinkers, 6 => m.rightBlinkers, 7 => m.extraLights,
            8 => m.interiorLights, 9 => m.checkEngineLight, 10 => m.neutralLight, 11 => m.reverseLight,
            12 => m.overHeatingLight, 13 => m.frontParkingLightLeft, 14 => m.frontParkingLightRight,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
        internal static VehicleLightingState Capture(VehicleController vehicle)
        {
            var m = vehicle.effectsManager.lightsManager; ushort mask = 0;
            for (int i = 0; i < 15; i++) if (Lamp(m, i)?.On == true) mask |= (ushort)(1 << i);
            byte controls = (byte)((Hazard(m) ? 1 : 0) | (Left(m) ? 2 : 0) | (Right(m) ? 4 : 0));
            if (vehicle.InteriorLightMode < 0 || vehicle.InteriorLightMode > 2) throw new InvalidOperationException("Unsupported native interior light mode.");
            // Native activation resets each blinker clock. An inactive side has no visible
            // phase; advancing it here made every parked vehicle's record change at 20 Hz,
            // replaying the entire vehicle state on guests even with every lamp switched off.
            byte leftPhase = (controls & 3) != 0 ? Phase(LeftTime(m)) : (byte)0;
            byte rightPhase = (controls & 5) != 0 ? Phase(RightTime(m)) : (byte)0;
            return new VehicleLightingState(mask, controls, leftPhase, rightPhase, (byte)vehicle.InteriorLightMode);
        }
        // NWH's lights module only switches the cabin lamp while the vehicle is awake (LightsMananger.Update).
        // A parked vehicle whose host player is elsewhere sleeps, so a guest's dome-mode click or door
        // left the lamp as it was on every peer (#164 follow-up). The same rule, run by the host.
        internal static void RefreshInterior(VehicleController vehicle)
        {
            var m = vehicle.effectsManager?.lightsManager;
            if (m == null || m.interiorLights == null || (m.Active && vehicle.VehicleMultiplayerInstanceType == NWH.Vehicle.MultiplayerInstanceType.Local)) return;
            bool on = vehicle.InteriorLightMode == 1 || vehicle.InteriorLightMode == 0 && !(vehicle.isDriverDoorClosed && vehicle.isPassengerDoorClosed);
            if (on == m.interiorLights.On) return;
            if (on) m.interiorLights.TurnOn(); else m.interiorLights.TurnOff();
            Debug.Log("MonMP cabin lamp " + (on ? "on" : "off") + " on sleeping " + vehicle.vehicle_type + " (mode " + vehicle.InteriorLightMode + ")");
        }
        private static byte Phase(float started) => (byte)Mathf.Min(255, Mathf.FloorToInt(Mathf.Repeat(Time.realtimeSinceStartup - started, 1f) * 256));
        internal static void Apply(VehicleController vehicle, VehicleLightingState state)
        {
            var m = vehicle.effectsManager.lightsManager;
            if (vehicle.InteriorLightMode != state.InteriorMode)
            {
                vehicle.InteriorLightMode = state.InteriorMode;
                foreach (var control in vehicle.GetComponentsInChildren<Vehicle_Button_LightDomeOptions>()) control.UpdateVisual();
            }
            bool restoreTail = m.brakeLights.On && (state.Lamps & 1) == 0 && (state.Lamps & 2) != 0;
            for (int i = 0; i < 15; i++) Lamp(m, i)?.SetState((state.Lamps & (1 << i)) != 0);
            // Native brake and tail groups can share renderers. Match the native
            // brake-release refresh so clearing the brake does not erase the tail.
            if (restoreTail) { m.tailLights.TurnOff(); m.tailLights.TurnOn(); }
            Hazard(m) = (state.Controls & 1) != 0; Left(m) = (state.Controls & 2) != 0; Right(m) = (state.Controls & 4) != 0;
            LeftTime(m) = Time.realtimeSinceStartup - state.LeftPhase / 256f;
            RightTime(m) = Time.realtimeSinceStartup - state.RightPhase / 256f;
            // SetState updates native lamps/materials; this updates the physical dashboard switch without sound.
            m.button_LightSwitch?.updateVisual();
            // OlTruck's bed-light switch only turns in UpdateVisualSound, which runs after a local
            // click. Applying the lamp state skips that callback, so observers need its visual half.
            ApplyExtraVisuals(vehicle, m.extraLights?.On == true);
            vehicle.input.states.lowBeamLights = vehicle.input.states.highBeamLights = vehicle.input.states.hazardLights = false;
            vehicle.input.states.leftBlinker = vehicle.input.states.rightBlinker = false;
            vehicle.input.states.extraLights = false;
            // Last, and isolated: these lamps are cosmetic part extras. Nothing above may depend on them.
            ApplyPartLights(vehicle, m.lowBeamLights?.On == true);
        }
        private static ExtraVisuals VisualsOf(VehicleController vehicle)
        {
            if (!ExtraSwitches.TryGetValue(vehicle, out var visuals))
            {
                if (ExtraSwitches.Count >= 256) ExtraSwitches.Clear();
                ExtraSwitches.Add(vehicle, visuals = new ExtraVisuals());
            }
            return visuals;
        }
        // Truck rack, light bumper and autometer lamps belong to their installed parts, not to the
        // replicated lamp groups. Only native LightsMananger.Update on a local simulator switches
        // them, from the low-beam toggle, so a replicated "off" left them lit. The periodic repeat
        // covers a part whose native toggle ran while this copy simulated the vehicle; the calls
        // are idempotent.
        private static void ApplyPartLights(VehicleController vehicle, bool on)
        {
            var visuals = VisualsOf(vehicle);
            if (visuals.PartsApplied && visuals.PartsOn == on && Time.unscaledTime - visuals.PartsCheckedAt < 2f) return;
            visuals.PartsApplied = true; visuals.PartsOn = on; visuals.PartsCheckedAt = Time.unscaledTime;
            var parts = vehicle.vehicle_PartsManager;
            if (parts == null) return;
            // Native Parking_Lights dereferences every lamp socket and installed lamp part without a
            // guard (a vehicle type lacking an autometer or bumper socket, a part without its lamp
            // component). Natively that only ends LightsMananger.Update for one frame. Here it ended
            // the whole replicated vehicle apply, which then retried against the vehicle's own driver
            // on every host echo: the car snapped back along its path and its light switches reverted.
            try { parts.Parking_Lights(on); }
            catch (Exception ex)
            {
                if (visuals.PartsWarned) return;
                visuals.PartsWarned = true;
                Debug.LogWarning("MonMP could not switch installed part lights on " + vehicle.vehicle_type + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        private static void ApplyExtraVisuals(VehicleController vehicle, bool on)
        {
            var visuals = VisualsOf(vehicle);
            bool refresh = !visuals.Applied || Time.unscaledTime - visuals.CheckedAt >= 2f;
            if (!refresh && visuals.On == on) return;
            if (refresh)
            {
                visuals.Controls = vehicle.GetComponentsInChildren<Vehicle_Button_ExtraLights>(true);
                visuals.CheckedAt = Time.unscaledTime;
            }
            foreach (var control in visuals.Controls)
                if (control != null && ReferenceEquals(ExtraVehicle(control), vehicle))
                    control.transform.localEulerAngles = on ? ExtraOn(control) : ExtraOff(control);
            visuals.Applied = true; visuals.On = on;
        }
    }
}
