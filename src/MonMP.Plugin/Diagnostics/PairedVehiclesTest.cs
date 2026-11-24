using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using NWH;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedVehiclesTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private int phase;
        private float nextAction;
        private int exitFrame;
        private Vector3 startedAt;
        private VehicleState? lastDriven;
        private uint driver;
        private PairedVehicleInputProbe? inputProbe;
        private readonly bool truck;
        private int doorsPhase;
        private bool sawLighting;
        private bool sawRemoteWheelSpin;
        private bool sawRemoteGearPresentation;
        private bool sawRemoteSurface;
        private readonly RemoteSurfacePresentationProbe surfaceProbe = new RemoteSurfacePresentationProbe();
        private bool sawRemoteHandbrakeHold, sawRemoteHandbrakeRelease, sawRemoteParkingEngaged;
        private float doorPresentationDeadline;
        private readonly bool truckDoorsOnly;
        private bool lateDoorProbeComplete;
        private string truckDoorsStatus = "";
        public string Status => truckDoorsOnly && truckDoorsStatus.Length != 0 ? truckDoorsStatus : (truck ? "Truck" : "ATV") + " phase " + phase + "; doors=" + doorsPhase + "; owner=" + plugin.Vehicles?.TestState.Driver.Owner +
            (truck && plugin.Vehicles != null ? "; driverDoor=" + plugin.Vehicles.Doors.TestState().Open + "/" + plugin.Vehicles.Doors.TestState().Angle +
                "; seatDistance=" + Vector3.Distance(gameplay.PlayerWalking.transform.position, plugin.Vehicles.TestSeat) : "");
        public PairedVehiclesTest(MonMPPlugin plugin, Gameplay gameplay, bool truck = false, bool truckDoorsOnly = false)
        {
            if (truckDoorsOnly && !truck) throw new ArgumentException("The door-only vehicle fixture requires the truck path.", nameof(truckDoorsOnly));
            this.plugin = plugin; this.gameplay = gameplay; this.truck = truck; this.truckDoorsOnly = truckDoorsOnly;
            NativeRegistryTest.Verify(plugin.World!);
        }
        private Vehicle_Button_ParkingBrake? brake;
        private int brakeBefore;
        private float controlDeadline;
        private int domeModeBefore, domeModeTarget, domeRequestCount, hostDomeModeTarget, domeRounds;
        public bool Tick()
        {
            if (Time.unscaledTime < nextAction) return false;
            if (!plugin.TestOptions.IsHost && phase == 9) { plugin.BeginPairedTestSession(); phase = 10; return false; }
            var shared = plugin.Vehicles;
            if (shared?.Ready != true || plugin.World?.PersonalProfileReady != true) return false;
            Require(shared.Count == 4 + (shared.TestScrapyardKonigSupported ? 1 : 0) + (shared.TestPrinzBound ? 1 : 0) + (shared.TestLeMissileBound ? 1 : 0) + (shared.TestRacecarBound ? 1 : 0) && shared.TestBuggyReserved,
                "Expected the starting Konig, stock ATV, truck, reserved buggy and any supported native scrapyard Konig, Prinz, Le Missile and racecar; bound=" + shared.Count + ".");
            shared.TestType = truck ? Vehicle_Type.OlTruck : Vehicle_Type.SmollATV;
            Require(shared.TestAccessoryMode, "Truck accessory retained the wrong simulation mode.");
            if (truckDoorsOnly && !lateDoorProbeComplete)
            {
                shared.Doors.TestLateOpenRegistration();
                lateDoorProbeComplete = true;
            }
            if (truck && doorsPhase != 5) { PrepareDoors(shared); return false; }
            if (truckDoorsOnly) return TickTruckDoorsOnly(shared);
            if (plugin.TestOptions.IsHost) return Host(shared);
            var v = shared.TestVehicle;
            var activeInputProbe = inputProbe;
            if (activeInputProbe != null)
            {
                try
                {
                    if (activeInputProbe.Tick())
                    {
                        activeInputProbe.Dispose(); inputProbe = null;
                        BeginScriptedControls(v);
                        phase = 5; nextAction = Time.unscaledTime + 2;
                    }
                }
                catch
                {
                    activeInputProbe.Dispose(); inputProbe = null;
                    throw;
                }
                return false;
            }
            if (phase == 0)
            {
                startedAt = v.transform.position;
                Require(v.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Remote && v.vehicleRigidbody.isKinematic,
                    "Guest did not receive a kinematic ATV baseline.");
                MoveTo(shared.TestSeat + Vector3.right * 30); phase = 1; nextAction = Time.unscaledTime + 0.7f;
            }
            else if (phase == 1) { shared.TestEnter(); phase = 2; nextAction = Time.unscaledTime + 1f; }
            else if (phase == 2)
            {
                Require(shared.TestState.Driver.Owner == ItemLease.Unowned && gameplay.Get_CurrentVehicleController == null, "Distant guest acquired the ATV.");
                // The host's explicit refusal ends the claim long before its four-second timeout.
                Require(!shared.TestEntryPending, "The host's seat refusal did not end the distant guest's pending claim within one second.");
                Debug.Log("MonMP vehicle seat refusal: distant driver claim ended within 1 s");
                MoveTo(shared.TestSeat + Vector3.right * 1.5f); phase = 20; nextAction = Time.unscaledTime + 0.7f;
            }
            // A walking guest works the parking brake of the parked vehicle through the host (#138).
            else if (phase == 20)
            {
                brake = v.vehicle_PartsManager.ParkingBrake;
                if (brake == null) { Debug.Log("MonMP vehicle controls: no parking brake on " + v.vehicle_type); BeginDomeModeTest(shared); return false; }
                brakeBefore = shared.TestState.ParkingBrake;
                if (brakeBefore > 0) brake.MouseRightDown(); else brake.MouseDown();
                controlDeadline = Time.unscaledTime + 6f; phase = 21;
            }
            else if (phase == 21 || phase == 22)
            {
                int want = phase == 21 ? (brakeBefore > 0 ? 0 : 1) : Math.Min(brakeBefore, 1);
                if (shared.TestState.ParkingBrake == want && brake!.LoadFromSave == want)
                {
                    Debug.Log("MonMP vehicle controls: guest parking brake " + (phase == 21 ? "toggled" : "restored") + " to " + want + " without driving");
                    if (phase == 22) { BeginDomeModeTest(shared); return false; }
                    if (want > 0) brake.MouseRightDown(); else brake.MouseDown();
                    controlDeadline = Time.unscaledTime + 6f; phase = 22;
                }
                else Require(Time.unscaledTime < controlDeadline, "A walking guest could not work the parked vehicle's parking brake (#138): state " +
                    shared.TestState.ParkingBrake + ", native " + brake!.LoadFromSave + ", wanted " + want + ", requests " + shared.TestControlRequests);
            }
            else if (phase == 23)
            {
                Require(shared.TestKonigDomeModeControl != null, "Main vehicle cabin light mode button missing (#145).");
                Require(shared.TestKonigState.Driver.Owner == ItemLease.Unowned && gameplay.Get_CurrentVehicleController == null,
                    "Dome-light walk-up fixture is not a walking guest with an unoccupied vehicle (#145).");
                domeModeBefore = shared.TestKonigVehicle.InteriorLightMode;
                domeModeTarget = (domeModeBefore + 1) % 3;
                domeRequestCount = DomeRequests(shared);
                shared.TestKonigDomeModeControl!.ChangeLightMode();
                controlDeadline = Time.unscaledTime + 6f; phase = 24;
            }
            else if (phase == 24)
            {
                if (DomeRequests(shared) == domeRequestCount + 1 &&
                    shared.TestKonigVehicle.InteriorLightMode == domeModeTarget &&
                    shared.TestKonigState.Lighting.InteriorMode == domeModeTarget)
                {
                    Debug.Log("MonMP vehicle controls: walking guest changed the host's cabin light mode to " + domeModeTarget + " (#145)");
                    // Keep clicking until "always on", then the lamp itself must light on this peer.
                    if (domeModeTarget != 1 && ++domeRounds < 3) { phase = 23; nextAction = Time.unscaledTime + 1.2f; return false; }
                    if (domeModeTarget == 1) { phase = 26; controlDeadline = Time.unscaledTime + 4f; return false; }
                    MoveTo(shared.TestSeat + Vector3.right * 1.5f); phase = 25; nextAction = Time.unscaledTime + 0.7f;
                }
                else Require(Time.unscaledTime < controlDeadline, "A walking guest could not change the parked vehicle's cabin light mode (#145): before " +
                    domeModeBefore + ", wanted " + domeModeTarget + ", native " + shared.TestKonigVehicle.InteriorLightMode +
                    ", state " + shared.TestKonigState.Lighting.InteriorMode + ", requests " + DomeRequests(shared));
            }
            else if (phase == 26)
            {
                var lamp = shared.TestKonigVehicle.effectsManager.lightsManager.interiorLights;
                if (lamp == null || lamp.On)
                {
                    Debug.Log("MonMP vehicle controls: cabin lamp " + (lamp == null ? "absent" : "lit") + " in always-on mode on the walking guest");
                    MoveTo(shared.TestSeat + Vector3.right * 1.5f); phase = 25; nextAction = Time.unscaledTime + 0.7f;
                }
                else Require(Time.unscaledTime < controlDeadline, "The cabin lamp stayed dark after the guest set always-on (#164).");
            }
            else if (phase == 25) phase = 3;
            // A second click on a still-pending seat cancels it instead of requesting it again.
            else if (phase == 3) { if (shared.TestEntryPending) return false; shared.TestEnter(); phase = 4; }
            else if (phase == 4 && shared.TestState.Driver.Owner == plugin.Session!.LocalPeerId)
            {
                Require(gameplay.Get_CurrentVehicleController == v && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver &&
                    v.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Local && !v.vehicleRigidbody.isKinematic && v.input.autoSetInput,
                    "Driver grant did not restore native input, seat and physics.");
                Require(UnityEngine.Object.FindObjectOfType<GameAssets>()?.vehicleInputs.VehicleControls.enabled == true && v.input.IsOn,
                    "Guest driver vehicle controls stayed disabled after world activation.");
                if (truck) { shared.Doors.TestClick(); shared.Doors.TestClick(false); }
                PrepareInputProbe(v);
                inputProbe = new PairedVehicleInputProbe(v);
            }
            else if (phase == 5)
            {
                Require(v.isEngineRunning, "Native ATV engine did not start.");
                if (truck) Require(!shared.Doors.TestState().Open && !shared.Doors.TestState(false).Open && shared.Doors.TestSynchronized,
                    "Truck doors did not close through host hinge physics while the guest drove.");
                v.Get_IgnitionPosition = 1; v.powertrain.transmission.ShiftInto(1, true); v.input.Throttle = 0.6f;
                if (truck)
                {
                    var button = v.GetComponentInChildren<Vehicle_Button_LightSwitch>();
                    Require(button != null, "Truck headlight switch missing.");
                    if (!v.effectsManager.lightsManager.lowBeamLights.On) button!.MouseDown();
                    var dome = v.GetComponentInChildren<Vehicle_Button_LightDomeOptions>();
                    // Some stock vehicles expose the saved mode without a cabin switch.
                    if (dome == null) v.InteriorLightMode = 1;
                    else for (int attempt = 0; attempt < 3 && v.InteriorLightMode != 1; attempt++) dome.ChangeLightMode();
                    Debug.Log("MONMP_LIGHTING_FIXTURE: cabinModeButton=" + (dome != null));
                    Require(v.InteriorLightMode == 1, "Native cabin light mode was not selected.");
                }
                phase = 6; nextAction = Time.unscaledTime + 3;
            }
            else if (phase == 6)
            {
                Require(Vector3.Distance(v.transform.position, startedAt) > 0.3f, "Native driven ATV did not move. " + Describe(v));
                v.input.Throttle = 0; v.input.Brakes = 1;
                if (truck)
                {
                    Require(v.effectsManager.lightsManager.lowBeamLights.On, "Driver headlight input did not turn on native lamps.");
                    var high = v.GetComponentInChildren<Vehicle_Button_Highbeam>();
                    var hazard = v.GetComponentInChildren<Vehicle_Button_HazardLights>();
                    // The stock cabin does not provide every separate switch type.
                    // Exercise its native keyboard/input route where no switch exists.
                    if (high != null) high.MouseDown(); else v.input.states.highBeamLights = true;
                    if (hazard != null) hazard.MouseDown(); else v.input.states.hazardLights = true;
                    Debug.Log("MONMP_LIGHTING_FIXTURE: highBeamButton=" + (high != null) + "; hazardButton=" + (hazard != null) +
                        "; highTarget=" + (high != null && high.vehicleController == v) + "; autoInput=" + v.input.autoSetInput +
                        "; flags=" + v.input.states.highBeamLights + "/" + v.input.states.hazardLights + "; before=" + NativeVehicleLighting.Capture(v).Lamps + "/" + NativeVehicleLighting.Capture(v).Controls);
                }
                if (truck)
                {
                    Require(v.vehicle_PartsManager.ParkingBrake != null, "Truck momentary handbrake fixture has no native lever.");
                    v.vehicle_PartsManager.ParkingBrake!.LoadFromSave = 0;
                    v.input.states.handbrake = 0.6f;
                    phase = 27; nextAction = Time.unscaledTime + 1.5f;
                }
                else
                {
                    if (v.vehicle_PartsManager.ParkingBrake != null) v.vehicle_PartsManager.ParkingBrake.LoadFromSave = 1;
                    v.brakes.handbrakeValue = 1;
                    phase = 13; nextAction = Time.unscaledTime + 1.5f;
                }
            }
            else if (phase == 27)
            {
                v.input.states.handbrake = 0;
                phase = 28; nextAction = Time.unscaledTime + 1.5f;
            }
            else if (phase == 28)
            {
                v.vehicle_PartsManager.ParkingBrake.LoadFromSave = 1;
                v.brakes.handbrakeValue = 1;
                phase = 13; nextAction = Time.unscaledTime + 1.5f;
            }
            else if (phase == 13)
            {
                if (truck) Require((NativeVehicleLighting.Capture(v).Lamps & 24) == 24 && NativeVehicleLighting.Capture(v).Controls == 7,
                    "Native driver high-beam/hazard controls did not latch: lamps=" + NativeVehicleLighting.Capture(v).Lamps +
                    "; controls=" + NativeVehicleLighting.Capture(v).Controls + "; autoInput=" + v.input.autoSetInput + "; " + Describe(v));
                if (truck) Require(v.InteriorLightMode == 1 && v.effectsManager.lightsManager.interiorLights.On,
                    "Closed truck cabin did not retain the driver's always-on light mode.");
                v.input.autoSetInput = true;
                if (truck)
                {
                    shared.TestExit();
                    Require(gameplay.Get_CurrentVehicleController == v && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver,
                        "The truck driver exited through closed doors.");
                    shared.Doors.TestClick();
                    controlDeadline = Time.unscaledTime + 8f; phase = 29; nextAction = Time.unscaledTime + 0.2f;
                    return false;
                }
                shared.TestExit(); phase = 7; nextAction = Time.unscaledTime + 1;
            }
            else if (phase == 29)
            {
                if (!shared.Doors.TestState().Open || v.isDriverDoorClosed)
                {
                    Require(Time.unscaledTime < controlDeadline, "The truck driver door did not open for native seat exit.");
                    return false;
                }
                shared.TestExit(); phase = 7; nextAction = Time.unscaledTime + 1;
            }
            else if (phase == 7 && shared.TestState.Driver.Owner == ItemLease.Unowned)
            {
                Require(gameplay.Get_PlayerState == PlayerState.Walking && gameplay.PlayerWalking.transform.parent == null &&
                    v.vehicleRigidbody.isKinematic, "Exit did not restore walking and remote vehicle physics.");
                MoveTo(shared.TestSeat + Vector3.right * 1.5f); phase = 8; nextAction = Time.unscaledTime + 0.7f;
                if (truck) phase = 14;
            }
            else if (phase == 14)
            {
                // Publish the new walking position before requesting the exterior handle.
                if (!shared.Doors.TestState().Open) shared.Doors.TestClick();
                phase = 8; nextAction = Time.unscaledTime + 0.8f;
            }
            else if (phase == 8)
            {
                if (shared.TestState.Driver.Owner != plugin.Session!.LocalPeerId) { if (!shared.TestEntryPending) shared.TestEnter(); nextAction = Time.unscaledTime + 0.4f; return false; }
                phase = 12; nextAction = Time.unscaledTime + 0.3f;
            }
            else if (phase == 12)
            {
                var registry = plugin.World!.Entities;
                plugin.EndPairedTestSession();
                Require(registry.Count == 0, "Disconnect retained native registry bindings.");
                Require(shared.TestRestored && gameplay.Get_PlayerState == PlayerState.Walking,
                    "Guest disconnect did not restore original vehicles and walking controls: player=" + gameplay.Get_PlayerState + "; " + shared.TestRestoreDiagnostic);
                phase = 9; nextAction = Time.unscaledTime + 1;
            }
            else if (phase == 10)
            {
                if (truck) Require((shared.TestState.Lighting.Lamps & 24) == 24 && shared.TestState.Lighting.Controls == 7 &&
                    NativeVehicleLighting.Capture(v).Controls == 7, "Reconnect lost headlights or hazard latches.");
                if (truck) Require(shared.TestState.Lighting.InteriorMode == 1 && v.InteriorLightMode == 1,
                    "Reconnect lost the cabin light mode.");
                Require(shared.TestState.Driver.Owner == ItemLease.Unowned && v.vehicleRigidbody.isKinematic &&
                    Vector3.Distance(v.transform.position, startedAt) > 0.3f, "Reconnect missed released ATV state or resurrected the original position.");
                if (truck) Require(shared.Doors.TestState().Open && shared.Doors.TestSynchronized && shared.TestState.Accessory.HasValue,
                    "Truck reconnect missed the open door or accessory baseline.");
                phase = 11; nextAction = Time.unscaledTime + 0.8f;
            }
            else if (phase == 11) return true;
            return false;
        }
        private bool TickTruckDoorsOnly(SharedVehicles shared)
        {
            Require(shared.TestVehicle.vehicle_type == Vehicle_Type.OlTruck, "Truck door suite did not select the stock truck path.");
            if (plugin.TestOptions.IsHost)
            {
                if (phase == 0) { phase = 1; nextAction = Time.unscaledTime + 0.1f; return false; }
                if (phase == 1 && plugin.Session?.Peers.Count == 1 && plugin.RemoteAvatarCount == 0)
                {
                    Require(shared.Doors.TestState().Open && shared.Doors.TestState(false).Open && shared.Doors.TestSynchronized,
                        "Host did not retain both guest-opened truck door poses before disconnect.");
                    plugin.EndPairedTestSession();
                    return true;
                }
                return false;
            }

            if (phase == 0)
            {
                Require(shared.Doors.TestState().Open && shared.Doors.TestState(false).Open && shared.Doors.TestSynchronized &&
                    shared.Doors.TestSawPresentationInterpolation,
                    "Guest did not observe synchronized truck doors moving through an intermediate angle.");
                shared.Doors.TestResetPresentationObservation(false);
                MoveTo(shared.Doors.TestPositionFor(false)); phase = 2; nextAction = Time.unscaledTime + 1;
                truckDoorsStatus = "Truck driver door interpolated; checking passenger door close/open presentation.";
                return false;
            }
            if (phase == 1)
            {
                var registry = plugin.World!.Entities;
                plugin.EndPairedTestSession();
                Require(registry.Count == 0, "Truck door disconnect retained native registry bindings.");
                Require(shared.TestRestored && gameplay.Get_PlayerState == PlayerState.Walking,
                    "Truck door disconnect did not restore the guest's vehicle, door and hinge baseline.");
                return true;
            }
            if (phase == 2)
            {
                shared.Doors.TestClick(false); phase = 3; doorPresentationDeadline = Time.unscaledTime + 8;
                return false;
            }
            if (phase == 3)
            {
                if (Time.unscaledTime >= doorPresentationDeadline) Require(false, "Truck passenger door did not settle while closing.");
                if (shared.Doors.TestState(false).Open || !shared.Doors.TestSynchronized) return false;
                Require(shared.Doors.TestPassengerSawPresentationInterpolation,
                    "Guest truck passenger door did not interpolate through an intermediate angle while closing.");
                shared.Doors.TestResetPresentationObservation(false);
                MoveTo(shared.Doors.TestPositionFor(false)); phase = 4; nextAction = Time.unscaledTime + 1;
                return false;
            }
            if (phase == 4)
            {
                shared.Doors.TestClick(false); phase = 5; doorPresentationDeadline = Time.unscaledTime + 8;
                return false;
            }
            if (phase == 5)
            {
                var passenger = shared.Doors.TestState(false);
                if (Time.unscaledTime >= doorPresentationDeadline)
                    Require(false, "Truck passenger door did not move beyond 5 degrees and settle while reopening; angle=" + passenger.Angle +
                        "; interpolation=" + shared.Doors.TestPassengerSawPresentationInterpolation + ".");
                if (!passenger.Open || Mathf.Abs(passenger.Angle) <= 5 || !shared.Doors.TestSynchronized) return false;
                Require(shared.Doors.TestPassengerSawPresentationInterpolation,
                    "Guest truck passenger door did not interpolate through an intermediate angle while opening.");
                phase = 1; nextAction = Time.unscaledTime + 0.25f;
                truckDoorsStatus = "Both truck door hinges interpolated; verifying local rollback.";
                return false;
            }
            return false;
        }
        private void PrepareDoors(SharedVehicles shared)
        {
            var doors = shared.Doors;
            if (plugin.TestOptions.IsHost)
            {
                if (doorsPhase == 0 && doors.TestState().Open)
                {
                    MoveTo(shared.TestSeat + Vector3.right * 1.5f); doors.TestClick(false);
                    doorsPhase = 1; nextAction = Time.unscaledTime + 0.7f;
                }
                else if (doorsPhase == 1) { Require(doors.TestState(false).Open, "Host passenger door interaction failed."); doorsPhase = 5; }
                return;
            }
            if (doorsPhase == 0) { MoveTo(doors.TestPosition + Vector3.right * 30); doorsPhase = 1; nextAction = Time.unscaledTime + 0.7f; }
            else if (doorsPhase == 1) { doors.TestClick(); doorsPhase = 2; nextAction = Time.unscaledTime + 0.7f; }
            else if (doorsPhase == 2)
            {
                Require(!doors.TestState().Open, "Distant guest opened the truck door.");
                MoveTo(doors.TestPosition + Vector3.right * 1.5f); doorsPhase = 3; nextAction = Time.unscaledTime + 0.7f;
            }
            else if (doorsPhase == 3) { doors.TestResetPresentationObservation(); doors.TestClick(); doorsPhase = 4; doorPresentationDeadline = Time.unscaledTime + 8; }
            else if (doorsPhase == 4)
            {
                if (Time.unscaledTime >= doorPresentationDeadline) Require(false, "Truck door did not settle on the guest.");
                if (doors.TestState().Open && doors.TestState(false).Open && Mathf.Abs(doors.TestState().Angle) > 5 && doors.TestSynchronized)
                {
                    Require(doors.TestSawPresentationInterpolation, "Guest truck door transform did not move through an intermediate rendered angle.");
                    doorsPhase = 5;
                }
            }
        }
        private bool Host(SharedVehicles shared)
        {
            var v = shared.TestVehicle;
            if (phase == 0)
            {
                Require(shared.TestKonigDomeModeControl != null, "Main vehicle cabin light mode button missing (#145).");
                hostDomeModeTarget = (shared.TestKonigVehicle.InteriorLightMode + 1) % 3;
                startedAt = v.transform.position; MoveTo(shared.TestSeat + Vector3.right * 1.5f);
                shared.TestEnter();
                Require(shared.TestState.Driver.Owner == 0 && gameplay.Get_CurrentVehicleController == v, "Native host seat entry failed.");
                shared.TestExit();
                Require(shared.TestState.Driver.Owner == ItemLease.Unowned, "Native host seat release failed.");
                phase = 6; nextAction = Time.unscaledTime + 0.3f; exitFrame = Time.frameCount;
            }
            else if (phase == 6)
            {
                // The same-frame exit is deferred to a next-frame coroutine, which runs after this
                // Update; one long frame can cover the whole 0.3 s wait.
                if (Time.frameCount <= exitFrame + 1) return false;
                Require(gameplay.Get_CurrentVehicleController == null && gameplay.PlayerWalking.mover.Get_RigidBody != null,
                    "Same-frame entry/exit did not restore native walking physics (state=" + gameplay.Get_PlayerState + "; current=" +
                    (gameplay.Get_CurrentVehicleController == null ? "none" : gameplay.Get_CurrentVehicleController.name) + "; body=" + (gameplay.PlayerWalking.mover.Get_RigidBody != null) + "; " + shared.TestExitTrace + ").");
                phase = 1;
            }
            else if (phase == 1 && shared.TestState.Driver.Owner != ItemLease.Unowned)
            {
                // The guest steps the mode until always-on (1) to check the lamp, so it may be past our first step.
                int mode = shared.TestKonigVehicle.InteriorLightMode;
                Require(shared.TestDomeModesApplied + plugin.World!.PassengerControls.Applied > 0 && (mode == hostDomeModeTarget || mode == 1) &&
                    shared.TestKonigState.Lighting.InteriorMode == mode,
                    "Host did not apply and publish the walking guest's cabin light mode request (#145).");
                driver = shared.TestState.Driver.Owner;
                MoveTo(shared.TestSeat + Vector3.right * 1.5f); shared.TestEnter();
                Require(driver != 0 && shared.TestState.Driver.Owner == driver && gameplay.Get_CurrentVehicleController == null &&
                    v.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Remote && v.vehicleRigidbody.isKinematic,
                    "Host stole the guest's seat or retained simulation authority.");
                shared.TestResetWheelSpinVisualDiagnostics();
                surfaceProbe.Begin(v);
                phase = 2;
            }
            else if (phase == 2)
            {
                if (shared.TestState.Driver.Owner == driver)
                {
                    lastDriven = shared.TestState;
                    int surfaceWheels = surfaceProbe.Observe(v, out string surfaceEvidence);
                    if (surfaceWheels > 0)
                    {
                        if (!sawRemoteSurface) Debug.Log("MONMP_REMOTE_SURFACE: cleared observer cache refreshed from native ground hits; grounded wheels checked=" + surfaceWheels + "; " + surfaceEvidence);
                        sawRemoteSurface = true;
                    }
                    if (truck && lastDriven.ParkingBrake == 0 && lastDriven.HandbrakeInput >= 0.5f)
                    {
                        VerifyRemoteHandbrake(v, lastDriven);
                        sawRemoteHandbrakeHold = true;
                    }
                    else if (truck && sawRemoteHandbrakeHold && lastDriven.ParkingBrake == 0 && lastDriven.HandbrakeInput == 0)
                    {
                        VerifyRemoteHandbrake(v, lastDriven);
                        sawRemoteHandbrakeRelease = true;
                    }
                    else if (truck && sawRemoteHandbrakeRelease && lastDriven.ParkingBrake > 0 && lastDriven.HandbrakeInput == 0)
                    {
                        VerifyRemoteHandbrake(v, lastDriven);
                        if (!sawRemoteParkingEngaged)
                            Debug.Log("MONMP_REMOTE_HANDBRAKE: held raw input, released lever and persistent engagement matched native rotation with observer input disabled; tolerance=0.1deg");
                        sawRemoteParkingEngaged = true;
                    }
                    if (lastDriven.Gear == 1)
                    {
                        int knobs = RemoteGearPresentationProbe.Verify(v, lastDriven.Gear);
                        Require(!truck || knobs > 0, "OlTruck remote gear fixture did not validate a native shifter knob.");
                        if (!sawRemoteGearPresentation)
                            Debug.Log("MONMP_REMOTE_GEAR: vehicle=" + v.vehicle_type + "; received/nativeGear=" + lastDriven.Gear +
                                "; input.IsOn=false; configured digital gear strings matched; native shifter rotations checked=" + knobs + "; tolerance=0.1deg");
                        sawRemoteGearPresentation = true;
                    }
                    // The wheel field is updated inside NWH's physics substep; comparing it
                    // from Update with a newer network sample makes this check timing-sensitive.
                    // Require evidence that the visual postfix ran for all four wheels and
                    // applied at least one meaningful transmitted spin sample instead.
                    if (lastDriven.WheelSpin.MaxAbsAngularVelocity >= 5f && shared.TestRemoteWheelSpinVisualApplied)
                        sawRemoteWheelSpin = true;
                    if (truck && (lastDriven.Lighting.Lamps & 24) == 24 && lastDriven.Lighting.Controls == 7)
                    {
                        var lights = NativeVehicleLighting.Capture(v);
                        Require(lights.Lamps == lastDriven.Lighting.Lamps && lights.Controls == 7, "Host native lamps differ from driver state.");
                        Require(lights.InteriorMode == 1 && v.InteriorLightMode == 1, "Host replica lost cabin light mode.");
                        // Walk-up controls now intentionally route to the current driver.
                        // Do not repeatedly switch the driver's lamps off while observing
                        // their replication; PassengerControls covers those interactions.
                        Require(!v.input.states.lowBeamLights && !v.input.states.hazardLights, "Nondriver host queued lighting mutations.");
                        sawLighting = true;
                    }
                    return false;
                }
                if (truck) Require(sawLighting && NativeVehicleLighting.Capture(v).Controls == 7,
                    "Driver-to-host handoff lost lighting control latches.");
                if (truck) Require(sawRemoteHandbrakeHold && sawRemoteHandbrakeRelease && sawRemoteParkingEngaged,
                    "Remote truck lever did not present momentary hold, release and persistent engagement before handoff.");
                Require(sawRemoteGearPresentation, "Remote first-gear dashboard/shifter presentation was not observed with player input disabled.");
                Require(sawRemoteSurface, "No grounded remote wheel restored its surface preset/index from independently resolved native ground hits during the drive.");
                Require(sawRemoteWheelSpin,
                    "Host replica did not apply the guest's four-wheel angular velocity during the paired drive fixture. " +
                    shared.TestWheelSpinVisualDiagnostic(lastDriven?.WheelSpin ?? default));
                if (truck) Require(v.InteriorLightMode == 1, "Driver exit reset cabin light mode.");
                Require(lastDriven != null && lastDriven.EngineAngularVelocity > 0 && Vector3.Distance(v.transform.position, startedAt) > 0.3f &&
                    !v.vehicleRigidbody.isKinematic && v.input.autoSetInput, "Driver exit did not hand moving ATV physics/mechanics back to host.");
                var before = shared.TestState;
                shared.TestStale(driver, lastDriven!);
                Require(shared.TestState.Driver.Generation == before.Driver.Generation && shared.TestState.Driver.Owner == ItemLease.Unowned,
                    "A stale driver packet reclaimed authority.");
                phase = 3;
            }
            else if (phase == 3 && shared.TestState.Driver.Owner == driver) phase = 4;
            else if (phase == 4 && plugin.Session!.Peers.Count == 1)
            {
                Require(shared.TestState.Driver.Owner == ItemLease.Unowned && !v.vehicleRigidbody.isKinematic && v.input.autoSetInput,
                    "Disconnect did not reclaim ATV physics and driver seat.");
                phase = 5;
            }
            else if (phase == 5 && plugin.Session!.Peers.Count == 2) return true;
            return false;
        }
        private void BeginDomeModeTest(SharedVehicles shared)
        {
            var dome = shared.TestKonigDomeModeControl;
            Require(dome != null, "Main vehicle cabin light mode button missing (#145).");
            MoveTo(dome!.transform.position + shared.TestKonigVehicle.transform.right * 1.5f);
            phase = 23; nextAction = Time.unscaledTime + 0.8f;
        }
        private void MoveTo(Vector3 p)
        {
            gameplay.PlayerWalking.transform.position = p; gameplay.Get_Player_Rigidbody.position = p; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static void PrepareInputProbe(NWH.VehiclePhysics2.VehicleController v)
        {
            // The input assertions are signal-only: neutral, engine-off, and brakes held prevent motion.
            v.input.Throttle = 0; v.input.Brakes = 0; v.input.Handbrake = 0;
            if (v.vehicle_PartsManager.ParkingBrake != null) v.vehicle_PartsManager.ParkingBrake.LoadFromSave = 1;
            v.brakes.handbrakeValue = 1; v.powertrain.transmission.ShiftInto(0, true); v.Get_IgnitionPosition = 0;
        }
        private void BeginScriptedControls(NWH.VehiclePhysics2.VehicleController v)
        {
            // The native InputSystem actions were exercised by PairedVehicleInputProbe;
            // this scripted pass now retains deterministic real engine/wheel physics coverage.
            v.input.autoSetInput = false; v.input.Throttle = 0; v.input.Brakes = 0; v.input.Handbrake = 0;
            // NWH uses 1 for a released/engaged clutch; zero overrides auto-clutch
            // and leaves the driveline disconnected even with the engine running.
            v.input.states.clutch = 1; // Normalized state bypasses the asset's input inversion setting.
            Require(v.input.Clutch == 1, "Scripted clutch input did not engage.");
            if (v.vehicle_PartsManager.ParkingBrake != null) v.vehicle_PartsManager.ParkingBrake.LoadFromSave = 0;
            v.brakes.handbrakeValue = 0; v.powertrain.transmission.ShiftInto(0, true);
            v.Get_IgnitionPosition = 2;
        }
        private void VerifyRemoteHandbrake(NWH.VehiclePhysics2.VehicleController vehicle, VehicleState state)
        {
            Require(!vehicle.input.IsOn && vehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Remote,
                "Handbrake presentation probe requires a remote observer with player input disabled.");
            var lever = vehicle.vehicle_PartsManager.ParkingBrake ?? vehicle.brakes.Button_ParkingBrake;
            Require(lever != null && lever.LoadFromSave == state.ParkingBrake,
                "Remote truck persistent parking state differs from the driver record.");
            Require(Mathf.Abs(vehicle.input.Handbrake - state.HandbrakeInput) < 0.002f,
                "Remote handbrake input was inverted again or replaced with effective braking.");
            float expected = -lever!.maxAgle * (state.ParkingBrake > 0 ? 1f : state.HandbrakeInput);
            float actual = lever.isYAxis ? lever.transform.localEulerAngles.y : lever.transform.localEulerAngles.x;
            Require(Mathf.Abs(Mathf.DeltaAngle(actual, expected)) <= 0.1f,
                "Remote truck lever stayed at " + actual + " degrees; expected " + expected +
                " from raw input " + state.HandbrakeInput + " and persistent parking " + state.ParkingBrake + ".");
        }
        // Shared passenger controls now handle recognized walk-up dome clicks too.
        // Keep the legacy fallback covered while counting the current routing path.
        private int DomeRequests(SharedVehicles shared) => shared.TestDomeModeRequests + plugin.World!.PassengerControls.Requested;

        private static string Describe(NWH.VehiclePhysics2.VehicleController v) =>
            "mode=" + v.VehicleMultiplayerInstanceType + " input=" + v.input.IsOn + "/" + v.input.Active + "/" + v.input.autoSetInput +
            " throttle=" + v.input.Throttle + " brake=" + v.input.Brakes + " parking=" + v.brakes.handbrakeValue +
            " gear=" + v.powertrain.transmission.Gear + " clutch=" + v.input.Clutch + " rpm=" + v.powertrain.engine.RPM +
            " constraints=" + v.vehicleRigidbody.constraints + " kinematic=" + v.vehicleRigidbody.isKinematic + " awake=" + v.IsAwake +
            " brakeLink=" + (v.vehicle_PartsManager.ParkingBrake != null) + "/" + v.brakes.Button_ParkingBrake?.Get_isLocked +
            " wheels=" + string.Join(",", v.Wheels.Select(w => w.wheelController.enabled + "/" + w.wheelController.UsingExternalUpdate + "/" +
                w.wheelController.visualOnlyUpdate + "/" + w.wheelController.motorTorque + "/" + w.wheelController.rpm));
        private void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Paired " + (truck ? "truck" : "ATV") + " test: " + reason);
        }
    }
}
