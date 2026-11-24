using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using NWH;
using NWH.Common.Input;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Input;
using NWH.VehiclePhysics2.Powertrain;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace MonMP.Plugin.Diagnostics
{
    // Drives the guest's real NWH input and physics path with an isolated virtual
    // keyboard. Unlike PairedVehicleInputProbe, success requires actual movement,
    // braking and a transmission gear change after the input events.
    internal sealed class PairedVehicleInputResponseProbe : IDisposable
    {
        private const int MaxShiftCheckSamples = 12;
        private static PairedVehicleInputResponseProbe? activeShiftTrace;
        private static readonly FieldInfo ShiftCheckValidField = AccessTools.Field(typeof(TransmissionComponent), "shiftCheckValid");
        private static readonly FieldInfo ExternalShiftChecksValidField = AccessTools.Field(typeof(TransmissionComponent), "externalShiftChecksValid");
        private static readonly FieldInfo ClutchEngagedField = AccessTools.Field(typeof(TransmissionComponent), "clutchEngaged");
        private static readonly FieldInfo NoWheelSpinField = AccessTools.Field(typeof(TransmissionComponent), "noWheelSpin");
        private static readonly FieldInfo NoWheelSkidField = AccessTools.Field(typeof(TransmissionComponent), "noWheelSkid");
        private static readonly FieldInfo NoWheelAirField = AccessTools.Field(typeof(TransmissionComponent), "noWheelAir");
        private static readonly MethodInfo CheckForShiftMethod = AccessTools.Method(typeof(TransmissionComponent), "CheckForShift",
            new[] { typeof(VehicleController), typeof(bool), typeof(bool), typeof(bool) });
        private readonly VehicleController vehicle;
        private readonly InputSystemVehicleInputProvider provider;
        private readonly InputActionAsset vehicleAsset;
        private readonly InputActionAsset playerAsset;
        private readonly InputActionMap vehicleControlsMap;
        private readonly InputAction throttle;
        private readonly InputAction brakes;
        private readonly InputAction clutch;
        private readonly InputAction shiftUp;
        private readonly Harmony shiftTraceHarmony = new Harmony("MonMP.VehicleInputResponseProbe." + Guid.NewGuid().ToString("N"));
        private readonly List<string> shiftSignalSamples = new List<string>(MaxShiftCheckSamples);
        private readonly UnityEngine.InputSystem.Utilities.ReadOnlyArray<InputDevice>? originalVehicleDevices;
        private readonly UnityEngine.InputSystem.Utilities.ReadOnlyArray<InputDevice>? originalPlayerDevices;
        private readonly bool originalMouseInput;
        private readonly bool originalAutoSetInput;
        private readonly bool originalVehicleMapEnabled;
        private Keyboard? keyboard;
        private InputSettings.BackgroundBehavior? originalBackgroundBehavior;
        private Key throttleKey, brakeKey, shiftUpKey;
        private int phase;
        private float deadline;
        private float startForwardSpeed, brakingForwardSpeed;
        private Vector3 startPosition, startForward;
        private bool throttlePerformed, brakesPerformed, shiftUpPerformed;
        private bool shiftProviderReported, disposed;
        private bool shiftSignalSeenAtCheck, shiftSignalSeenWithClutchGate;
        private int shiftCheckCount;
        private string lastShiftCheckSample = "none";

        internal string Evidence { get; private set; } = "";

        internal PairedVehicleInputResponseProbe(VehicleController vehicle)
        {
            this.vehicle = vehicle ?? throw new ArgumentNullException(nameof(vehicle));
            var assets = GameAssets.i ?? throw new InvalidOperationException("Guest GameAssets is missing.");
            var registeredProviders = InputProvider.Instances
                ?? throw new InvalidOperationException("NWH global InputProvider.Instances is missing.");
            var providers = registeredProviders.OfType<InputSystemVehicleInputProvider>()
                .Where(candidate => candidate != null && candidate.isActiveAndEnabled).ToArray();
            if (providers.Length != 1)
                throw new InvalidOperationException("Expected exactly one active global InputSystemVehicleInputProvider; found " + providers.Length + ".");

            provider = providers[0];
            vehicleAsset = assets.vehicleInputs.asset;
            playerAsset = assets.inputs.asset;
            var controls = assets.vehicleInputs.VehicleControls;
            vehicleControlsMap = controls.Get();
            throttle = controls.Throttle;
            brakes = controls.Brakes;
            clutch = controls.Clutch;
            shiftUp = controls.ShiftUp;
            originalVehicleDevices = vehicleAsset.devices;
            originalPlayerDevices = playerAsset.devices;
            originalMouseInput = provider.mouseInput;
            originalAutoSetInput = vehicle.input.autoSetInput;
            originalVehicleMapEnabled = vehicleControlsMap.enabled;
        }

        internal bool Tick()
        {
            try
            {
                if (phase == 0) { Begin(); return false; }
                if (phase == 1)
                {
                    shiftProviderReported |= provider.ShiftUp();
                    if (shiftUpPerformed && shiftProviderReported && shiftSignalSeenAtCheck &&
                        vehicle.powertrain.transmission.Gear == 1)
                    {
                        // ManualShift can apply a gear independently of the clutch gate.
                        // Record that gate, then test whether the engaged gear transmits torque.
                        if (vehicle.vehicle_PartsManager.ParkingBrake != null)
                            vehicle.vehicle_PartsManager.ParkingBrake.LoadFromSave = 0;
                        vehicle.brakes.handbrakeValue = 0;
                        Release(); phase = 2; deadline = Time.unscaledTime + 0.8f;
                    }
                    else if (Time.unscaledTime >= deadline)
                    {
                        string reason = !shiftSignalSeenAtCheck
                            ? "virtual ShiftUp did not reach TransmissionComponent.CheckForShift"
                            : "ShiftUp reached CheckForShift, but the native manual transmission did not change from neutral to first gear";
                        throw new InvalidOperationException("Paired native vehicle input response: " + reason + "; " + ShiftFailureState());
                    }
                }
                else if (phase == 2)
                {
                    if (IsReleased(shiftUp))
                    {
                        startPosition = vehicle.transform.position;
                        startForward = vehicle.transform.forward;
                        startForwardSpeed = ForwardSpeed();
                        Press(throttleKey); phase = 3; deadline = Time.unscaledTime + 5f;
                    }
                    else EnsureBeforeDeadline("guest ShiftUp action did not release");
                }
                else if (phase == 3)
                {
                    bool inputReachedHandler = InputMatches(throttle, throttlePerformed) &&
                        provider.Throttle() > 0.5f && vehicle.input.Throttle > 0.5f;
                    float forwardSpeed = ForwardSpeed();
                    float forwardTravel = Vector3.Dot(vehicle.transform.position - startPosition, startForward);
                    if (inputReachedHandler && forwardSpeed >= Math.Max(0.6f, startForwardSpeed + 0.6f) && forwardTravel >= 0.25f)
                    {
                        Release(); phase = 4; deadline = Time.unscaledTime + 0.8f;
                    }
                    else EnsureBeforeDeadline("virtual throttle reached NWH but did not produce forward vehicle motion; " +
                        "handler=" + vehicle.input.Throttle.ToString("F2") + ", forwardSpeed=" + forwardSpeed.ToString("F2") +
                        ", forwardTravel=" + forwardTravel.ToString("F2"));
                }
                else if (phase == 4)
                {
                    if (IsReleased(throttle))
                    {
                        brakingForwardSpeed = ForwardSpeed();
                        Require(brakingForwardSpeed > 0.5f, "Vehicle lost its throttle-driven motion before the brake test.");
                        Press(brakeKey); phase = 5; deadline = Time.unscaledTime + 5f;
                    }
                    else EnsureBeforeDeadline("virtual throttle did not release through the native input provider");
                }
                else if (phase == 5)
                {
                    bool inputReachedHandler = InputMatches(brakes, brakesPerformed) &&
                        provider.Brakes() > 0.5f && vehicle.input.Brakes > 0.5f;
                    float forwardSpeed = ForwardSpeed();
                    if (inputReachedHandler && forwardSpeed <= brakingForwardSpeed - 0.5f)
                    {
                        Release(); phase = 6; deadline = Time.unscaledTime + 0.8f;
                    }
                    else EnsureBeforeDeadline("virtual brake reached NWH but did not reduce forward vehicle speed; " +
                        "handler=" + vehicle.input.Brakes.ToString("F2") + ", before=" + brakingForwardSpeed.ToString("F2") +
                        ", current=" + forwardSpeed.ToString("F2"));
                }
                else if (phase == 6)
                {
                    if (IsReleased(brakes))
                    {
                        Evidence = "vehicle=" + vehicle.vehicle_type + "; transmission=Manual; CheckForShift saw ShiftUp; clutch gate seen open=" + shiftSignalSeenWithClutchGate +
                            "; VehicleControls originally enabled=" + originalVehicleMapEnabled + "; ShiftUp changed gear 0->1" +
                            "; throttle moved forward " + Vector3.Dot(vehicle.transform.position - startPosition, startForward).ToString("F2") + "m" +
                            "; brake reduced forward speed from " + brakingForwardSpeed.ToString("F2") + "m/s to " + ForwardSpeed().ToString("F2") + "m/s";
                        Debug.Log("MONMP_NATIVE_VEHICLE_INPUT_RESPONSE: " + Evidence);
                        Dispose();
                        return true;
                    }
                    EnsureBeforeDeadline("virtual brake action did not release");
                }
                return false;
            }
            catch (Exception probeFailure)
            {
                try { Dispose(); }
                catch (Exception cleanupFailure)
                {
                    probeFailure.Data["MonMP.VehicleInputResponse.CleanupFailure"] = cleanupFailure.ToString();
                }
                throw;
            }
        }

        private void Begin()
        {
            Require(vehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Local && !vehicle.vehicleRigidbody.isKinematic,
                "Guest does not own local vehicle physics.");
            Require(vehicle.input.IsOn && vehicle.input.Active && vehicle.input.autoSetInput,
                "Native NWH vehicle input is not active for the guest.");
            Require(vehicle.isEngineRunning, "Native engine is not running before the response test.");
            Require(vehicle.powertrain.transmission.TransmissionType == TransmissionComponent.Type.Manual &&
                vehicle.powertrain.transmission.ForwardGearCount >= 1 && vehicle.powertrain.transmission.Gear == 0,
                "Response test requires a manual transmission in neutral with a forward gear.");
            Require(Mathf.Abs(ForwardSpeed()) < 0.25f, "Response test must begin with a stationary vehicle.");

            var allowedPlayerDevices = (originalPlayerDevices ?? InputSystem.devices).ToArray();
            // Two game windows run side by side and only one has focus. By default the Input System
            // disables and resets keyboards in an unfocused player, dropping the synthetic keys.
            originalBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            keyboard = InputSystem.AddDevice<Keyboard>("MonMP paired vehicle response");
            vehicleAsset.devices = new UnityEngine.InputSystem.Utilities.ReadOnlyArray<InputDevice>(new InputDevice[] { keyboard });
            playerAsset.devices = new UnityEngine.InputSystem.Utilities.ReadOnlyArray<InputDevice>(
                allowedPlayerDevices.Where(device => device != keyboard).ToArray());
            provider.mouseInput = false;

            throttleKey = BoundKey(throttle, "Throttle");
            brakeKey = BoundKey(brakes, "Brakes");
            shiftUpKey = BoundKey(shiftUp, "ShiftUp");
            Require(throttleKey != brakeKey && throttleKey != shiftUpKey && brakeKey != shiftUpKey,
                "Vehicle input bindings overlap; refusing to synthesize ambiguous controls.");
            throttle.performed += OnThrottle;
            brakes.performed += OnBrakes;
            shiftUp.performed += OnShiftUp;
            InstallShiftTrace();
            // Inject while the just-bound virtual keyboard is still the active vehicle input
            // device. Deferring to a later frame made this event unreliable in hidden paired runs.
            Press(shiftUpKey);
            phase = 1; deadline = Time.unscaledTime + 2f;
        }

        private Key BoundKey(InputAction action, string label)
        {
            var control = action.controls.OfType<KeyControl>().FirstOrDefault(candidate => candidate.device == keyboard);
            if (control == null)
                throw new InvalidOperationException("VehicleControls." + label + " has no keyboard binding for the virtual keyboard.");
            return control.keyCode;
        }

        private bool InputMatches(InputAction action, bool callbackObserved) =>
            callbackObserved && action.ReadValue<float>() > 0.5f;

        private static bool IsReleased(InputAction action) => action.type == InputActionType.Button
            ? !action.IsPressed()
            : action.ReadValue<float>() < 0.1f;

        private float ForwardSpeed() => Vector3.Dot(vehicle.vehicleRigidbody.velocity, startForward == Vector3.zero ? vehicle.transform.forward : startForward);

        private void Press(Key key) => Queue(new KeyboardState(key));
        private void Release() => Queue(new KeyboardState());

        private void Queue(KeyboardState state)
        {
            if (keyboard == null) throw new InvalidOperationException("Vehicle response virtual keyboard was not created.");
            InputSystem.QueueStateEvent(keyboard, state);
        }

        private void EnsureBeforeDeadline(string reason)
        {
            if (Time.unscaledTime >= deadline)
                throw new InvalidOperationException("Paired native vehicle input response: " + reason + "; phase=" + phase +
                    "; status=" + PhaseStatus() + ".");
        }

        private string PhaseStatus() =>
            "vehicle=" + vehicle.vehicle_type + "; mapEnabled=" + vehicleControlsMap.enabled +
            "; throttle(action/provider/handler/callback)=" + throttle.ReadValue<float>().ToString("F2") + "/" +
            provider.Throttle().ToString("F2") + "/" + vehicle.input.Throttle.ToString("F2") + "/" + throttlePerformed +
            "; brake(action/provider/handler/callback)=" + brakes.ReadValue<float>().ToString("F2") + "/" +
            provider.Brakes().ToString("F2") + "/" + vehicle.input.Brakes.ToString("F2") + "/" + brakesPerformed +
            "; clutch(action/provider/handler/actual/automatic)=" + clutch.ReadValue<float>().ToString("F2") + "/" +
            provider.Clutch().ToString("F2") + "/" + vehicle.input.Clutch.ToString("F2") + "/" +
            vehicle.powertrain.clutch.clutchEngagement.ToString("F2") + "/" + vehicle.powertrain.clutch.isAutomatic +
            "; clutchSettings(inverted/auto)=" + vehicle.input.invertClutch + "/" + vehicle.powertrain.isAutoClutch +
            "; shift(action/provider/handler/callback)=" + shiftUp.ReadValue<float>().ToString("F2") + "/" +
            provider.ShiftUp() + "/" + vehicle.input.ShiftUp + "/" + shiftUpPerformed +
            "; gear=" + vehicle.powertrain.transmission.Gear + "; engineRunning=" + vehicle.isEngineRunning +
            "; rpm=" + vehicle.powertrain.engine.RPM.ToString("F0") + "; engineThrottle=" +
            vehicle.powertrain.engine.ThrottlePosition.ToString("F2") + "; handbrake(input/actual)=" +
            vehicle.input.Handbrake.ToString("F2") + "/" + vehicle.brakes.handbrakeValue.ToString("F2") + "; parking=" +
            (vehicle.vehicle_PartsManager.ParkingBrake?.LoadFromSave.ToString() ?? "missing") +
            "; body(kinematic/awake)=" + vehicle.vehicleRigidbody.isKinematic + "/" + vehicle.IsAwake +
            "; speed=" + ForwardSpeed().ToString("F2");

        private void OnThrottle(InputAction.CallbackContext context) => throttlePerformed = context.ReadValue<float>() > 0.5f;
        private void OnBrakes(InputAction.CallbackContext context) => brakesPerformed = context.ReadValue<float>() > 0.5f;
        private void OnShiftUp(InputAction.CallbackContext context)
        {
            shiftUpPerformed = context.ReadValue<float>() > 0.5f;
            shiftProviderReported = provider.ShiftUp();
        }

        private void InstallShiftTrace()
        {
            if (CheckForShiftMethod == null || ShiftCheckValidField == null || ExternalShiftChecksValidField == null ||
                ClutchEngagedField == null || NoWheelSpinField == null || NoWheelSkidField == null || NoWheelAirField == null)
                throw new InvalidOperationException("NWH TransmissionComponent shift-check fields or method are unavailable; cannot capture a useful ShiftUp failure trace.");
            if (activeShiftTrace != null)
                throw new InvalidOperationException("Another VehicleInputResponse shift trace is already active in this process.");

            activeShiftTrace = this;
            try
            {
                shiftTraceHarmony.Patch(CheckForShiftMethod,
                    prefix: new HarmonyMethod(typeof(PairedVehicleInputResponseProbe), nameof(CheckForShiftPrefix)),
                    postfix: new HarmonyMethod(typeof(PairedVehicleInputResponseProbe), nameof(CheckForShiftPostfix)));
                PatchHealth.VerifyManualPatchInstalled(shiftTraceHarmony, CheckForShiftMethod);
            }
            catch
            {
                activeShiftTrace = null;
                shiftTraceHarmony.UnpatchSelf();
                throw;
            }
        }

        private static void CheckForShiftPrefix(TransmissionComponent __instance, VehicleController vc, bool wheelSpin,
            bool wheelSkid, bool wheelAir, out ShiftCheckCapture __state)
        {
            var trace = activeShiftTrace;
            __state = default;
            if (trace == null || trace.phase != 1 || vc != trace.vehicle || __instance != vc.powertrain.transmission) return;

            var input = vc.input;
            __state = new ShiftCheckCapture(true, __instance.Gear, input.ShiftUp, input.states.shiftUp,
                trace.shiftUp.triggered, trace.provider.ShiftUp(), trace.provider.Clutch(), input.Clutch,
                input.states.clutch, vc.powertrain.clutch.clutchEngagement);
        }

        private static void CheckForShiftPostfix(TransmissionComponent __instance, VehicleController vc, bool wheelSpin,
            bool wheelSkid, bool wheelAir, ShiftCheckCapture __state)
        {
            var trace = activeShiftTrace;
            if (trace == null || !__state.Captured || vc != trace.vehicle || __instance != vc.powertrain.transmission) return;

            string sample = "gear=" + __state.Gear + "->" + __instance.Gear +
                ",actionAtCheck=" + __state.ActionTriggered + "/" + trace.shiftUp.IsPressed() + "/" + trace.shiftUp.ReadValue<float>().ToString("F2") +
                "@" + (trace.shiftUp.activeControl?.path ?? "-") +
                ",providerShiftUp=" + __state.ProviderShiftUp + ",handlerShiftUp=" + __state.HandlerShiftUp +
                "/state:" + __state.StateShiftUp + ",wheelSpin/skid/air=" + wheelSpin + "/" + wheelSkid + "/" + wheelAir +
                ",clutchAction/control/provider/handler/state/actual=" + trace.clutch.ReadValue<float>().ToString("F2") + "/" +
                (trace.clutch.activeControl?.path ?? "-") + "/" +
                __state.ProviderClutch.ToString("F2") + "/" + __state.HandlerClutch.ToString("F2") + "/" +
                __state.StateClutch.ToString("F2") + "/" + __state.ClutchEngagement.ToString("F2") +
                ",gates=wheelSpin:" + ReadGate(NoWheelSpinField, __instance) + "/skid:" + ReadGate(NoWheelSkidField, __instance) +
                "/air:" + ReadGate(NoWheelAirField, __instance) + "/clutch:" + ReadGate(ClutchEngagedField, __instance) +
                "/shift:" + ReadGate(ShiftCheckValidField, __instance) + "/external:" + ReadGate(ExternalShiftChecksValidField, __instance) +
                ",shifting=" + __instance.IsShifting;

            trace.shiftCheckCount++;
            trace.lastShiftCheckSample = sample;
            bool shiftSignal = __state.ActionTriggered || __state.ProviderShiftUp || __state.HandlerShiftUp || __state.StateShiftUp;
            bool clutchGateOpen = string.Equals(ReadGate(ClutchEngagedField, __instance), "True", StringComparison.OrdinalIgnoreCase);
            if (shiftSignal)
            {
                trace.shiftSignalSeenAtCheck = true;
                if (clutchGateOpen) trace.shiftSignalSeenWithClutchGate = true;
                if (trace.shiftSignalSamples.Count == MaxShiftCheckSamples) trace.shiftSignalSamples.RemoveAt(0);
                trace.shiftSignalSamples.Add(sample);
            }
        }

        private static string ReadGate(FieldInfo field, object target) =>
            field.GetValue(target)?.ToString() ?? "null";

        private string ShiftFailureState()
        {
            var transmission = vehicle.powertrain.transmission;
            string checks = "count=" + shiftCheckCount + "; signal=[" +
                (shiftSignalSamples.Count == 0 ? "none" : string.Join(" || ", shiftSignalSamples)) +
                "]; last=[" + lastShiftCheckSample + "]";
            return "vehicle=" + vehicle.vehicle_type + "; bindings ShiftUp=" + Bindings(shiftUp) + ", Clutch=" + Bindings(clutch) +
                "; vehicleMap(enabled/original)=" + vehicleControlsMap.enabled + "/" + originalVehicleMapEnabled +
                "; shiftCheckSignal/clutchGate=" + shiftSignalSeenAtCheck + "/" + shiftSignalSeenWithClutchGate +
                "; action(triggered/pressed/value/control)=" + shiftUp.triggered + "/" + shiftUp.IsPressed() + "/" +
                shiftUp.ReadValue<float>().ToString("F2") + "/" + (shiftUp.activeControl?.path ?? "-") +
                "; callback(performed/provider)=" + shiftUpPerformed + "/" + shiftProviderReported +
                "; providerShiftUp=" + provider.ShiftUp() + "; handlerShiftUp=" + vehicle.input.ShiftUp + "/state:" + vehicle.input.states.shiftUp +
                "; clutch(action/provider/handler/state/actual)=" + clutch.ReadValue<float>().ToString("F2") + "/" + provider.Clutch().ToString("F2") +
                "/" + vehicle.input.Clutch.ToString("F2") + "/" + vehicle.input.states.clutch.ToString("F2") + "/" +
                vehicle.powertrain.clutch.clutchEngagement.ToString("F2") +
                "; transmission(type/gear/forward/isShifting)=" + transmission.TransmissionType + "/" + transmission.Gear + "/" +
                transmission.ForwardGearCount + "/" + transmission.IsShifting + "; CheckForShift samples=[" + checks + "]";
        }

        private static string Bindings(InputAction action) =>
            string.Join(",", action.bindings.Select(binding => binding.path).Distinct());

        private readonly struct ShiftCheckCapture
        {
            internal readonly bool Captured;
            internal readonly int Gear;
            internal readonly bool HandlerShiftUp, StateShiftUp, ActionTriggered, ProviderShiftUp;
            internal readonly float ProviderClutch, HandlerClutch, StateClutch, ClutchEngagement;

            internal ShiftCheckCapture(bool captured, int gear, bool handlerShiftUp, bool stateShiftUp, bool actionTriggered,
                bool providerShiftUp, float providerClutch, float handlerClutch, float stateClutch, float clutchEngagement)
            {
                Captured = captured; Gear = gear; HandlerShiftUp = handlerShiftUp; StateShiftUp = stateShiftUp;
                ActionTriggered = actionTriggered; ProviderShiftUp = providerShiftUp; ProviderClutch = providerClutch;
                HandlerClutch = handlerClutch; StateClutch = stateClutch; ClutchEngagement = clutchEngagement;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Exception? cleanupFailure = null;
            TryCleanup(ref cleanupFailure, () => throttle.performed -= OnThrottle);
            TryCleanup(ref cleanupFailure, () => brakes.performed -= OnBrakes);
            TryCleanup(ref cleanupFailure, () => shiftUp.performed -= OnShiftUp);
            if (activeShiftTrace == this) activeShiftTrace = null;
            TryCleanup(ref cleanupFailure, shiftTraceHarmony.UnpatchSelf);
            if (keyboard != null)
            {
                Keyboard attachedKeyboard = keyboard;
                TryCleanup(ref cleanupFailure, () => vehicleAsset.devices = originalVehicleDevices);
                TryCleanup(ref cleanupFailure, () => playerAsset.devices = originalPlayerDevices);
                TryCleanup(ref cleanupFailure, () => InputSystem.RemoveDevice(attachedKeyboard));
                keyboard = null;
            }
            if (originalBackgroundBehavior.HasValue)
                TryCleanup(ref cleanupFailure, () => InputSystem.settings.backgroundBehavior = originalBackgroundBehavior.Value);
            TryCleanup(ref cleanupFailure, () => provider.mouseInput = originalMouseInput);
            TryCleanup(ref cleanupFailure, () => vehicle.input.autoSetInput = originalAutoSetInput);
            if (cleanupFailure != null) throw cleanupFailure;
        }

        private static void TryCleanup(ref Exception? failure, Action cleanup)
        {
            try { cleanup(); }
            catch (Exception exception) { failure ??= exception; }
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Paired native vehicle input response: " + reason);
        }
    }
}
