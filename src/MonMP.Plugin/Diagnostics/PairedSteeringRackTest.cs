using System;
using System.IO;
using MonMP.Core.Persistence;
using MonMP.Core.World;
using MonMP.Plugin.Patches;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Paired Phase 75: the guest invokes each native Konig steering-rack tuning control once.
    // The host validates every one-step request, writes a disposable native save, reloads it
    // through the menu, and verifies the native part fields before restoring the fixture state.
    internal sealed class PairedSteeringRackTest
    {
        private readonly MonMPPlugin plugin;
        private readonly string saveName = "MonMPSteeringRack-" + Guid.NewGuid().ToString("N");
        private readonly string originalSaveName;
        private readonly int originalSaveTimestamp;
        private int phase, observedCommands, nextControl;
        private float next, reloadSettleAt;
        private ulong socket;
        private VehiclePartState baseline, expected, finalExpected, lastCommand;
        private int[] directions = Array.Empty<int>();
        private SharedVehicleParts? parts;
        private Part_SteeringRack? rack;
        private bool watchingCommands, saveStarted;
        private int savedTimestamp;
        private string commandFailure = "";
        internal string Status => "steering rack phase=" + phase + "; controls=" + observedCommands + "/4" +
            "; next=" + nextControl + "; save=" + saveStarted;

        internal PairedSteeringRackTest(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            originalSaveName = LocalTestIsolation.GetString("SaveName");
            originalSaveTimestamp = LocalTestIsolation.GetInt("SaveTimestamp");
            if (plugin.TestOptions.IsHost)
            {
                LocalTestIsolation.SetString("SaveName", saveName);
                LocalTestIsolation.SetInt("SaveTimestamp", -1);
            }
        }

        internal bool Tick(Gameplay? gameplay)
        {
            if (Time.unscaledTime < next) return false;
            if (plugin.TestOptions.IsHost) return Host(gameplay);
            return Guest();
        }

        private bool Host(Gameplay? gameplay)
        {
            if (phase == 0)
            {
                if (plugin.World?.VehicleParts.Ready != true || plugin.Session?.Peers.Count != 2) return false;
                parts = plugin.World.VehicleParts;
                InitializeNativeBaseline(parts);
                if (!parts.TestRecord(socket).Equals(baseline))
                    throw new InvalidOperationException("Steering rack host record differs from its native baseline.");
                lastCommand = baseline;
                plugin.World.Channel.CommandRequested += HostCommandRequested;
                watchingCommands = true;
                phase = 1;
                return false;
            }
            if (phase == 1)
            {
                if (commandFailure.Length != 0) throw new InvalidOperationException("Steering rack host request validation failed: " + commandFailure);
                if (observedCommands < 4) return false;
                if (!expected.Equals(finalExpected) || !parts!.TestNative(socket).Equals(expected) || !parts.TestRecord(socket).Equals(expected))
                    throw new InvalidOperationException("Host native steering-rack values do not match the four guest controls.");
                phase = 2;
                return false;
            }
            if (phase == 2)
            {
                if (plugin.Session?.Peers.Count != 2) return false;
                SaveSystem.Save(House_Location.SpecialMenu);
                if (plugin.World?.Saves?.Pending != true)
                    throw new InvalidOperationException("Native steering-rack save did not enter the coordinated save pipeline: " +
                        (plugin.World?.Saves?.LastFailure ?? "save coordinator unavailable"));
                saveStarted = true;
                phase = 3;
                return false;
            }
            if (phase == 3)
            {
                var saves = plugin.World?.Saves;
                if (saves == null || saves.Pending) return false;
                if (saves.LastFailure.Length != 0 || plugin.World?.SaveRounds?.Phase != SavePhase.Complete)
                    throw new InvalidOperationException("Native steering-rack save failed: " + saves.LastFailure);
                savedTimestamp = SaveSystem.Get_LastSaveTimestamp;
                if (savedTimestamp <= 0 || LocalTestIsolation.GetString("SaveName") != saveName)
                    throw new InvalidOperationException("Disposable native save did not publish its selected name and timestamp.");
                VerifyNativeSaveRecord(expected);
                StopWatchingCommands();
                plugin.EndPairedTestSession();
                Require(gameplay?.Player != null, "Host gameplay vanished before native reload.");
                UnityEngine.Object.FindObjectOfType<EscapeMenuV2>().Button_QuitGameConfirmation();
                phase = 4;
                next = Time.unscaledTime + 4;
                return false;
            }
            if (phase == 4)
            {
                if (gameplay != null || UnityEngine.Object.FindObjectOfType<MainMenu_Manager>() == null) return false;
                LocalTestIsolation.SetString("SaveName", saveName);
                LocalTestIsolation.SetInt("SaveTimestamp", savedTimestamp);
                UnityEngine.Object.FindObjectOfType<MainMenu_Manager>().StartGame(false);
                phase = 5;
                reloadSettleAt = Time.unscaledTime + 5;
                return false;
            }
            if (phase == 5)
            {
                if (gameplay?.Player == null || gameplay.Get_isGamePaused || !NativeLoadGate.Ready || Time.unscaledTime < reloadSettleAt) return false;
                plugin.BeginPairedTestSession();
                phase = 6;
                return false;
            }
            if (phase == 6)
            {
                if (plugin.World?.VehicleParts.Ready != true) return false;
                parts = plugin.World.VehicleParts;
                socket = parts.TestSocketOfType(Car_PartsType.SteeringRack);
                rack = parts.SocketOf(socket)?.partInstalled as Part_SteeringRack;
                if (rack == null) throw new InvalidOperationException("Reloaded native Konig has no steering rack.");
                var reloaded = parts.TestNative(socket);
                if (!reloaded.Equals(expected))
                    throw new InvalidOperationException("Host native reload lost guest rack values: expected " + Describe(expected) + ", got " + Describe(reloaded) + ".");
                RestoreNativeControls(rack, directions);
                if (!parts.TestNative(socket).Equals(baseline))
                    throw new InvalidOperationException("Host fixture could not restore the Konig rack baseline after reload.");
                plugin.EndPairedTestSession();
                LocalTestIsolation.SetString("SaveName", originalSaveName);
                LocalTestIsolation.SetInt("SaveTimestamp", originalSaveTimestamp);
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                if (plugin.World?.VehicleParts.Ready != true || plugin.World.Channel.FullySynchronized != true) return false;
                parts = plugin.World.VehicleParts;
                InitializeNativeBaseline(parts);
                if (!parts.TestRecord(socket).Equals(baseline))
                    throw new InvalidOperationException("Guest did not receive the host's native steering-rack baseline.");
                rack = parts.SocketOf(socket)?.partInstalled as Part_SteeringRack;
                if (rack == null) throw new InvalidOperationException("Guest Konig steering-rack component is missing.");
                phase = 1;
                ApplyNextGuestControl();
                return false;
            }
            if (phase == 1)
            {
                if (plugin.Session?.IsRunning != true)
                    throw new InvalidOperationException("Guest session ended before the host steering-rack echo. expected=" + Describe(expected) +
                        " actual=" + Describe(parts!.TestNative(socket)));
                if (!parts!.TestRecordAvailable(socket)) return false;
                if (!parts!.TestRecord(socket).Equals(expected)) return false;
                if (!parts.TestNative(socket).Equals(expected))
                    throw new InvalidOperationException("Guest rack did not retain the host echo for a native control.");
                nextControl++;
                if (nextControl < 4)
                {
                    next = Time.unscaledTime + 0.35f;
                    phase = 2;
                    return false;
                }
                next = Time.unscaledTime + 0.5f;
                phase = 3;
                return false;
            }
            if (phase == 2)
            {
                phase = 1;
                ApplyNextGuestControl();
                return false;
            }
            if (phase == 3)
            {
                if (plugin.Session?.IsRunning == true) return false;
                if (!parts!.TestNative(socket).Equals(parts.TestOriginal(socket)))
                    throw new InvalidOperationException("Guest disconnect did not restore its local native steering-rack baseline.");
                return true;
            }
            return false;
        }

        private void InitializeNativeBaseline(SharedVehicleParts value)
        {
            socket = value.TestSocketOfType(Car_PartsType.SteeringRack);
            rack = value.SocketOf(socket)?.partInstalled as Part_SteeringRack;
            if (rack == null) throw new InvalidOperationException("The stock Konig steering-rack socket must contain an installed Part_SteeringRack.");
            baseline = value.TestNative(socket);
            if (!baseline.HasValidSteeringRackValues)
                throw new InvalidOperationException("The stock Konig steering rack has unsupported native tuning values: " + Describe(baseline));
            finalExpected = AddAllFourSteps(baseline, out directions);
            expected = baseline;
        }

        private void ApplyNextGuestControl()
        {
            var before = parts!.TestNative(socket);
            if (nextControl != 0 && !before.Equals(expected))
                throw new InvalidOperationException("Guest rack changed before the next host echo.");
            int direction = directions[nextControl];
            switch (nextControl)
            {
                case 0: rack!.KnobToeAngle_FL(direction); break;
                case 1: rack!.KnobToeAngle_FR(direction); break;
                case 2: rack!.KnobAdjustMaxSteerAngle(direction); break;
                case 3: rack!.KnobAdjustSteerSpeed(direction); break;
            }
            expected = ApplyOne(expected, nextControl, direction);
            if (!parts.TestNative(socket).Equals(expected))
                throw new InvalidOperationException("Guest native steering-rack control did not make the expected one-step change.");
            phase = 1;
        }

        private void HostCommandRequested(uint peer, ulong entity, byte[] payload)
        {
            if (entity != socket || peer == 0 || commandFailure.Length != 0) return;
            try
            {
                var state = VehiclePartState.Decode(payload);
                if (!VehiclePartCommandPolicy.IsValidTransition(lastCommand, state, true) ||
                    !parts!.TestNative(socket).Equals(state) || !parts.TestRecord(socket).Equals(state))
                    throw new InvalidDataException("A guest rack request was not applied as one native host step.");
                expected = ApplyOne(expected, observedCommands, directions[observedCommands]);
                if (!state.Equals(expected)) throw new InvalidDataException("Rack values diverged from the expected native control order.");
                lastCommand = state;
                observedCommands++;
                if (observedCommands > 4) throw new InvalidDataException("Guest sent too many tuning commands.");
            }
            catch (Exception ex) { commandFailure = ex.Message; }
        }

        private void VerifyNativeSaveRecord(VehiclePartState state)
        {
            var vehicle = SaveSystem.GetThisVehicleFromLastSave(Vehicle_Type.MainVehicle)
                ?? throw new InvalidOperationException("Native save did not contain the Konig vehicle record.");
            var rackData = vehicle.SteeringRack
                ?? throw new InvalidOperationException("Native save did not contain the Konig steering-rack GameObjectData.");
            Require(rackData.value1 == state.Value1 && rackData.value2 == state.Value2 &&
                rackData.intValue1 == state.IntValue1 && rackData.intValue2 == state.IntValue2,
                "Native GameObjectData did not preserve all four guest-authored steering-rack values.");
            Require(vehicle.ToeAngle != null && vehicle.ToeAngle.Length >= 2 &&
                vehicle.ToeAngle[0] == state.IntValue1 && vehicle.ToeAngle[1] == state.IntValue2,
                "Native Konig toe-angle save fields diverged from the rack part payload.");
        }

        private static VehiclePartState AddAllFourSteps(VehiclePartState start, out int[] stepDirections)
        {
            stepDirections = new[]
            {
                Direction(start.IntValue1, 100), Direction(start.IntValue2, 100),
                Direction(start.Value1, 70), Direction(start.Value2, 35)
            };
            var state = start;
            for (int i = 0; i < stepDirections.Length; i++) state = ApplyOne(state, i, stepDirections[i]);
            return state;
        }

        private static int Direction(int value, int max) => value < max ? 1 : -1;
        private static int Direction(float value, int max) => value < max ? 1 : -1;

        private static VehiclePartState ApplyOne(VehiclePartState state, int control, int direction) => control switch
        {
            0 => new VehiclePartState(true, state.Prefab, state.FullyTightened, state.Value1, state.Value2, state.IntValue1 + direction, state.IntValue2),
            1 => new VehiclePartState(true, state.Prefab, state.FullyTightened, state.Value1, state.Value2, state.IntValue1, state.IntValue2 + direction),
            2 => new VehiclePartState(true, state.Prefab, state.FullyTightened, state.Value1 + direction, state.Value2, state.IntValue1, state.IntValue2),
            3 => new VehiclePartState(true, state.Prefab, state.FullyTightened, state.Value1, state.Value2 + direction, state.IntValue1, state.IntValue2),
            _ => throw new ArgumentOutOfRangeException(nameof(control))
        };

        private static void RestoreNativeControls(Part_SteeringRack target, int[] stepDirections)
        {
            target.KnobToeAngle_FL(-stepDirections[0]);
            target.KnobToeAngle_FR(-stepDirections[1]);
            target.KnobAdjustMaxSteerAngle(-stepDirections[2]);
            target.KnobAdjustSteerSpeed(-stepDirections[3]);
        }

        private void StopWatchingCommands()
        {
            if (!watchingCommands) return;
            if (plugin.World != null) plugin.World.Channel.CommandRequested -= HostCommandRequested;
            watchingCommands = false;
        }

        internal void Abort()
        {
            try
            {
                StopWatchingCommands();
                var activeParts = plugin.World?.VehicleParts;
                var target = plugin.TestOptions.IsHost ? baseline : parts?.TestOriginal(socket) ?? VehiclePartState.Empty;
                var activeSocket = activeParts?.TestSocketOfType(Car_PartsType.SteeringRack) ?? socket;
                var activeRack = activeParts?.SocketOf(activeSocket)?.partInstalled as Part_SteeringRack;
                if (!plugin.TestOptions.IsHost)
                {
                    if (plugin.Session?.IsRunning == true) plugin.EndPairedTestSession();
                    var localRack = parts?.SocketOf(socket)?.partInstalled as Part_SteeringRack ?? rack;
                    var localState = parts?.TestNative(socket) ?? VehiclePartState.Empty;
                    if (localRack != null && localState.Installed && target.Installed)
                        RestoreToState(localRack, localState, target);
                }
                else
                {
                    var current = activeParts?.TestNative(activeSocket) ?? VehiclePartState.Empty;
                    if (activeRack != null && current.Installed && target.Installed && current.Prefab == target.Prefab)
                        RestoreToState(activeRack, current, target);
                    else if (activeParts == null && rack != null && parts != null)
                    {
                        current = parts.TestNative(socket);
                        var currentRack = parts.SocketOf(socket)?.partInstalled as Part_SteeringRack;
                        if (currentRack != null && current.Installed && target.Installed && current.Prefab == target.Prefab)
                            RestoreToState(currentRack, current, target);
                    }
                    if (plugin.Session?.IsRunning == true) plugin.EndPairedTestSession();
                    LocalTestIsolation.SetString("SaveName", originalSaveName);
                    LocalTestIsolation.SetInt("SaveTimestamp", originalSaveTimestamp);
                }
            }
            catch { }
        }

        private static void RestoreToState(Part_SteeringRack target, VehiclePartState current, VehiclePartState original)
        {
            for (int i = 0; i < 4; i++)
            {
                int delta = i switch
                {
                    0 => original.IntValue1 - current.IntValue1,
                    1 => original.IntValue2 - current.IntValue2,
                    2 => (int)(original.Value1 - current.Value1),
                    3 => (int)(original.Value2 - current.Value2),
                    _ => 0
                };
                int direction = Math.Sign(delta);
                for (int step = 0; step < Math.Abs(delta); step++) InvokeControl(target, i, direction);
            }
        }

        private static void InvokeControl(Part_SteeringRack target, int control, int direction)
        {
            switch (control)
            {
                case 0: target.KnobToeAngle_FL(direction); break;
                case 1: target.KnobToeAngle_FR(direction); break;
                case 2: target.KnobAdjustMaxSteerAngle(direction); break;
                case 3: target.KnobAdjustSteerSpeed(direction); break;
            }
        }

        private static string Describe(VehiclePartState state) => state.Installed
            ? state.Prefab + ":" + state.Value1 + "/" + state.Value2 + "/" + state.IntValue1 + "/" + state.IntValue2
            : "empty";
        private static void Require(bool ok, string message)
        { if (!ok) throw new InvalidOperationException("Steering rack paired test: " + message); }
    }
}
