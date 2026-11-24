using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NWH.Common.Input;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Input;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace MonMP.Plugin.Diagnostics
{
    // Opt-in observation only. This never changes action maps, providers, vehicle input or focus.
    internal sealed class VehicleInputTrace : IDisposable
    {
        private readonly Func<bool> enabled;
        private readonly Action<string> log;
        private readonly List<(InputAction Action, Action<InputAction.CallbackContext> Handler)> hooks =
            new List<(InputAction, Action<InputAction.CallbackContext>)>();
        private readonly Dictionary<string, float> lastActionLog = new Dictionary<string, float>(StringComparer.Ordinal);
        private InputActionMap? hookedMap;
        private Gameplay? gameplay;
        private NetworkSession? session;
        private SharedVehicles? vehicles;
        private float nextSample;
        private bool disposed, failed;

        internal VehicleInputTrace(Func<bool> enabled, Action<string> log)
        {
            this.enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        internal void Tick(Gameplay? gameplay, NetworkSession? session, SharedVehicles? vehicles)
        {
            if (disposed || failed) return;
            this.gameplay = gameplay;
            this.session = session;
            this.vehicles = vehicles;
            bool active = enabled() && session?.IsRunning == true && vehicles?.Ready == true;
            try
            {
                RefreshActionHooks(active);
                if (!active || Time.unscaledTime < nextSample) return;
                nextSample = Time.unscaledTime + 0.25f;

                var current = gameplay?.Get_CurrentVehicleController as VehicleController;
                foreach (var vehicle in vehicles!.BoundControllers)
                {
                    uint owner = vehicles.DrivenBy(vehicle);
                    if (owner == ItemLease.Unowned) continue;
                    bool localDriver = owner == session!.LocalPeerId && current == vehicle &&
                        gameplay?.Get_PlayerState == PlayerState.InVehicle_Driver;
                    bool observeRemoteDriver = session.IsHost && owner != session.LocalPeerId;
                    if (!localDriver && !observeRemoteDriver) continue;
                    WriteSample(vehicle, owner, localDriver);
                }
            }
            catch (Exception ex)
            {
                failed = true;
                DetachActionHooks();
                log("MONMP_VEHICLE_INPUT_TRACE_ERROR: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void RefreshActionHooks(bool active)
        {
            var assets = active ? UnityEngine.Object.FindObjectOfType<GameAssets>() : null;
            var map = assets?.vehicleInputs?.asset?.FindActionMap("Vehicle Controls", false);
            if (map == hookedMap) return;
            DetachActionHooks();
            hookedMap = map;
            if (map == null) return;

            foreach (var action in map.actions)
            {
                Action<InputAction.CallbackContext> handler = context => OnAction(action, context);
                action.performed += handler;
                action.canceled += handler;
                hooks.Add((action, handler));
            }
            log("MONMP_VEHICLE_INPUT_TRACE: action hooks attached; map=" + map.name + "; enabled=" + map.enabled);
        }

        private void OnAction(InputAction action, InputAction.CallbackContext context)
        {
            if (!IsLocalDriver()) return;
            string name = action.name;
            if (action.type != InputActionType.Button)
            {
                if (lastActionLog.TryGetValue(name, out float last) && Time.unscaledTime - last < 0.25f) return;
                lastActionLog[name] = Time.unscaledTime;
            }
            log("MONMP_VEHICLE_ACTION: role=" + Role() + " action=" + name + " phase=" + context.phase +
                " control=" + (context.control?.path ?? "none") + " t=" + F(Time.unscaledTime));
        }

        private void WriteSample(VehicleController vehicle, uint owner, bool localDriver)
        {
            bool hasState = vehicles!.TryGetDriverState(vehicle, out VehicleState state);
            string role = Role();
            string localInput = "remote-owner";
            if (localDriver)
            {
                var assets = UnityEngine.Object.FindObjectOfType<GameAssets>();
                var map = assets?.vehicleInputs?.asset?.FindActionMap("Vehicle Controls", false);
                var provider = (InputProvider.Instances ?? new List<InputProvider>())
                    .OfType<InputSystemVehicleInputProvider>()
                    .FirstOrDefault(candidate => candidate != null && candidate.isActiveAndEnabled);
                string actions = map == null ? "missing" :
                    "on=" + map.enabled + ",throttle=" + ActionValue(map.FindAction("Throttle", false)) +
                    ",brakes=" + ActionValue(map.FindAction("Brakes", false)) +
                    ",clutch=" + ActionValue(map.FindAction("Clutch", false)) +
                    ",steering=" + ActionValue(map.FindAction("Steering", false)) +
                    ",shiftUp=" + ActionValue(map.FindAction("ShiftUp", false)) +
                    ",shiftDown=" + ActionValue(map.FindAction("ShiftDown", false)) +
                    ",direct=" + PressedDirectShiftActions(map);
                string providerState = provider == null ? "missing" :
                    "throttle=" + F(provider.Throttle()) + ",brakes=" + F(provider.Brakes()) +
                    ",clutch=" + F(provider.Clutch()) + ",mouseInput=" + provider.mouseInput +
                    ",steering=" + F(provider.Steering()) + ",shiftUp=" + provider.ShiftUp() +
                    ",shiftDown=" + provider.ShiftDown() + ",shiftInto=" + provider.ShiftInto();
                string pressed = RawPressed();
                localInput = "raw=" + pressed + " actions=[" + actions + "] provider=[" + providerState + "]";
            }

            var input = vehicle.input;
            var transmission = vehicle.powertrain.transmission;
            string handler = localDriver
                ? "handler=[on=" + input.IsOn + ",active=" + input.Active + ",auto=" + input.autoSetInput +
                    ",throttle=" + F(input.Throttle) + ",brakes=" + F(input.Brakes) + ",clutch=" + F(input.Clutch) +
                    ",clutchEngagement=" + F(vehicle.powertrain.clutch.clutchEngagement) +
                    ",invertClutch=" + input.invertClutch + ",autoClutch=" + vehicle.powertrain.isAutoClutch +
                    ",steering=" + F(input.Steering) +
                    ",shiftUp=" + input.ShiftUp + ",shiftDown=" + input.ShiftDown + ",shiftInto=" + input.ShiftInto + "]"
                : "applied=[throttle=" + F(input.states.throttle) + ",brakes=" + F(input.states.brakes) +
                    ",steering=" + F(input.states.steering) + ",shiftUp=" + input.states.shiftUp +
                    ",shiftDown=" + input.states.shiftDown + ",shiftInto=" + input.states.shiftInto + "]";
            log("MONMP_VEHICLE_INPUT_SAMPLE: role=" + role + " vehicle=" + vehicle.vehicle_type +
                " owner=" + owner + " localDriver=" + localDriver + " mode=" + vehicle.VehicleMultiplayerInstanceType +
                " " + localInput + " " + handler + " transmission=[type=" + transmission.TransmissionType +
                ",gear=" + transmission.Gear + ",shifting=" + transmission.IsShifting +
                ",rpm=" + F(vehicle.powertrain.engine.RPM) + "]");

            if (hasState)
                log("MONMP_VEHICLE_SHARED_SAMPLE: role=" + role + " source=" + (session!.IsHost ? "host-accepted" : "host-echo") +
                    " vehicle=" + vehicle.vehicle_type + " owner=" + state.Driver.Owner + " generation=" + state.Driver.Generation +
                    " throttle=" + F(state.Throttle) + " brakes=" + F(state.Brake) + " steering=" + F(state.Steering) +
                    " gear=" + state.Gear + " speed=" + F(new Vector3(state.Velocity.X, state.Velocity.Y, state.Velocity.Z).magnitude));
        }

        private bool IsLocalDriver()
        {
            var vehicle = gameplay?.Get_CurrentVehicleController as VehicleController;
            return session?.IsRunning == true && vehicle != null && gameplay?.Get_PlayerState == PlayerState.InVehicle_Driver &&
                vehicles?.DrivenBy(vehicle) == session.LocalPeerId;
        }

        private string Role() => session?.IsHost == true ? "host" : "guest";

        private static string ActionValue(InputAction? action)
        {
            if (action == null) return "missing";
            try
            {
                string value = action.type == InputActionType.Button
                    ? (action.IsPressed() ? "1" : "0")
                    : F(action.ReadValue<float>());
                return value + "@" + (action.activeControl?.path ?? "-");
            }
            catch { return "unavailable"; }
        }

        private static string PressedDirectShiftActions(InputActionMap map)
        {
            var pressed = map.actions.Where(action => action.name.StartsWith("ShiftInto", StringComparison.Ordinal) && action.IsPressed())
                .Select(action => action.name + "@" + (action.activeControl?.path ?? "-"));
            return string.Join(",", pressed.DefaultIfEmpty("-"));
        }

        private static string RawPressed()
        {
            var activeDevices = new List<string>();
            foreach (var device in InputSystem.devices)
            {
                var activeControls = new List<string>();
                foreach (var control in device.allControls)
                {
                    if (control is ButtonControl button && button.isPressed)
                        activeControls.Add(control.path);
                    else if (!(control is ButtonControl))
                    {
                        try { if (control.EvaluateMagnitude() > 0.2f) activeControls.Add(control.path); }
                        catch { }
                    }
                    if (activeControls.Count >= 6) break;
                }
                if (activeControls.Count > 0)
                    activeDevices.Add(device.displayName + ":" + string.Join(",", activeControls));
            }
            return activeDevices.Count == 0 ? "-" : string.Join(";", activeDevices);
        }

        private static string F(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        private void DetachActionHooks()
        {
            foreach (var (action, handler) in hooks)
            {
                action.performed -= handler;
                action.canceled -= handler;
            }
            hooks.Clear();
            hookedMap = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            DetachActionHooks();
        }
    }
}
