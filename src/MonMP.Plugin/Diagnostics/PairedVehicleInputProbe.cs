using System;
using System.Linq;
using NWH.Common.Input;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Input;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace MonMP.Plugin.Diagnostics
{
    // Injects isolated keyboard events into the guest's real VehicleControls map.
    // The virtual keyboard never moves or captures the OS cursor and is removed after the probe.
    internal sealed class PairedVehicleInputProbe : IDisposable
    {
        private readonly VehicleController vehicle;
        private readonly InputSystemVehicleInputProvider provider;
        private readonly InputActionAsset vehicleAsset;
        private readonly InputActionAsset playerAsset;
        private readonly InputAction throttle;
        private readonly InputAction brakes;
        private readonly InputAction shiftUp;
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
        private bool throttlePerformed, brakesPerformed, shiftUpPerformed, shiftProviderReported, disposed;

        internal PairedVehicleInputProbe(VehicleController vehicle)
        {
            this.vehicle = vehicle ?? throw new ArgumentNullException(nameof(vehicle));
            var assets = GameAssets.i
                ?? throw new InvalidOperationException("Guest GameAssets is missing.");
            // NWH registers scene providers in this global list; vehicle handlers use that
            // same list, and InputSystemVehicleInputProvider binds GameAssets.i.vehicleInputs.
            var registeredProviders = InputProvider.Instances
                ?? throw new InvalidOperationException("NWH global InputProvider.Instances is missing.");
            var activeNativeProviders = registeredProviders.OfType<InputSystemVehicleInputProvider>()
                .Where(candidate => candidate != null && candidate.isActiveAndEnabled)
                .ToArray();
            if (activeNativeProviders.Length != 1)
                throw new InvalidOperationException("Expected exactly one active global InputSystemVehicleInputProvider in NWH InputProvider.Instances; found " + activeNativeProviders.Length + ".");
            provider = activeNativeProviders[0];
            vehicleAsset = assets.vehicleInputs.asset;
            playerAsset = assets.inputs.asset;
            var controls = assets.vehicleInputs.VehicleControls;
            throttle = controls.Throttle;
            brakes = controls.Brakes;
            shiftUp = controls.ShiftUp;
            originalVehicleDevices = vehicleAsset.devices;
            originalPlayerDevices = playerAsset.devices;
            originalMouseInput = provider.mouseInput;
            originalAutoSetInput = vehicle.input.autoSetInput;
            originalVehicleMapEnabled = controls.enabled;
        }

        internal bool Tick()
        {
            try
            {
                if (phase == 0) { Begin(); return false; }
                if (phase == 7) { BindAndPressThrottle(); return false; }
                if (phase == 1)
                {
                    if (InputMatches(throttle, throttlePerformed) && provider.Throttle() > 0.5f && vehicle.input.Throttle > 0.5f)
                    {
                        Release(); phase = 2; deadline = Time.unscaledTime + 0.8f;
                    }
                    else EnsureBeforeDeadline("throttle did not reach the native vehicle input handler");
                }
                else if (phase == 2)
                {
                    if (IsReleased(throttle) && provider.Throttle() < 0.1f && vehicle.input.Throttle < 0.1f)
                    {
                        Press(brakeKey); phase = 3; deadline = Time.unscaledTime + 0.8f;
                    }
                    else EnsureBeforeDeadline("throttle did not release through the native input provider");
                }
                else if (phase == 3)
                {
                    if (InputMatches(brakes, brakesPerformed) && provider.Brakes() > 0.5f && vehicle.input.Brakes > 0.5f)
                    {
                        Release(); phase = 4; deadline = Time.unscaledTime + 0.8f;
                    }
                    else EnsureBeforeDeadline("brake did not reach the native vehicle input handler");
                }
                else if (phase == 4)
                {
                    if (IsReleased(brakes) && provider.Brakes() < 0.1f && vehicle.input.Brakes < 0.1f)
                    {
                        Press(shiftUpKey); phase = 5; deadline = Time.unscaledTime + 0.8f;
                    }
                    else EnsureBeforeDeadline("brake did not release through the native input provider");
                }
                else if (phase == 5)
                {
                    if (shiftUpPerformed && shiftProviderReported && shiftUp.IsPressed())
                    {
                        Release(); phase = 6; deadline = Time.unscaledTime + 0.8f;
                    }
                    else EnsureBeforeDeadline("ShiftUp action did not reach the native input provider");
                }
                else if (phase == 6)
                {
                    if (IsReleased(shiftUp))
                    {
                        Debug.Log("MONMP_NATIVE_VEHICLE_INPUT: throttle and brake reached VehicleInputHandler; ShiftUp reached InputSystemVehicleInputProvider; keys=" +
                            throttleKey + "/" + brakeKey + "/" + shiftUpKey);
                        Dispose();
                        return true;
                    }
                    EnsureBeforeDeadline("ShiftUp action did not release");
                }
                return false;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void Begin()
        {
            Require(originalVehicleMapEnabled, "VehicleControls was disabled before native input probe.");
            Require(originalAutoSetInput, "Native vehicle input autoSetInput was disabled before probe.");
            var allowedPlayerDevices = (originalPlayerDevices ?? InputSystem.devices).ToArray();
            // Only one of the two side-by-side game windows has focus; an unfocused player would
            // otherwise disable and reset the synthetic keyboard.
            originalBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            var syntheticKeyboard = InputSystem.AddDevice<Keyboard>("MonMP paired vehicle input");
            keyboard = syntheticKeyboard;
            vehicleAsset.devices = new UnityEngine.InputSystem.Utilities.ReadOnlyArray<InputDevice>(new InputDevice[] { syntheticKeyboard });
            // Keep synthetic W/S/R events out of Player, including any live movement bindings.
            playerAsset.devices = new UnityEngine.InputSystem.Utilities.ReadOnlyArray<InputDevice>(
                allowedPlayerDevices.Where(device => device != syntheticKeyboard).ToArray());
            provider.mouseInput = false;
            phase = 7; deadline = Time.unscaledTime + 0.8f;
        }

        private void BindAndPressThrottle()
        {
            throttleKey = BoundKey(throttle, "Throttle");
            brakeKey = BoundKey(brakes, "Brakes");
            shiftUpKey = BoundKey(shiftUp, "ShiftUp");
            Require(throttleKey != brakeKey && throttleKey != shiftUpKey && brakeKey != shiftUpKey,
                "Vehicle input bindings overlap; refusing to synthesize ambiguous controls.");
            throttle.performed += OnThrottle;
            brakes.performed += OnBrakes;
            shiftUp.performed += OnShiftUp;

            Press(throttleKey);
            phase = 1; deadline = Time.unscaledTime + 0.8f;
        }

        private Key BoundKey(InputAction action, string label)
        {
            var control = action.controls.OfType<KeyControl>().FirstOrDefault(candidate => candidate.device == keyboard);
            if (control == null)
                throw new InvalidOperationException("VehicleControls." + label + " has no keyboard key binding for the virtual keyboard.");
            return control.keyCode;
        }

        private bool InputMatches(InputAction action, bool callbackObserved) =>
            callbackObserved && action.ReadValue<float>() > 0.5f;

        private static bool IsReleased(InputAction action) => action.type == InputActionType.Button
            ? !action.IsPressed()
            : action.ReadValue<float>() < 0.1f;

        private void Press(Key key) => Queue(new KeyboardState(key));

        private void Release() => Queue(new KeyboardState());

        private void Queue(KeyboardState state)
        {
            if (keyboard == null) throw new InvalidOperationException("Vehicle input virtual keyboard was not created.");
            InputSystem.QueueStateEvent(keyboard, state);
        }

        private void EnsureBeforeDeadline(string reason)
        {
            if (Time.unscaledTime >= deadline)
                throw new InvalidOperationException("Paired native vehicle input probe: " + reason + ".");
        }

        private void OnThrottle(InputAction.CallbackContext context) => throttlePerformed = context.ReadValue<float>() > 0.5f;
        private void OnBrakes(InputAction.CallbackContext context) => brakesPerformed = context.ReadValue<float>() > 0.5f;
        private void OnShiftUp(InputAction.CallbackContext context)
        {
            shiftUpPerformed = context.ReadValue<float>() > 0.5f;
            shiftProviderReported = provider.ShiftUp();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            throttle.performed -= OnThrottle;
            brakes.performed -= OnBrakes;
            shiftUp.performed -= OnShiftUp;
            if (keyboard != null)
            {
                vehicleAsset.devices = originalVehicleDevices;
                playerAsset.devices = originalPlayerDevices;
                InputSystem.RemoveDevice(keyboard);
            }
            if (originalBackgroundBehavior.HasValue) InputSystem.settings.backgroundBehavior = originalBackgroundBehavior.Value;
            provider.mouseInput = originalMouseInput;
            vehicle.input.autoSetInput = originalAutoSetInput;
            keyboard = null;
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Paired native vehicle input probe: " + reason);
        }
    }
}
