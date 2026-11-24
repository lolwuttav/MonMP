using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicles
    {
        // Physical callbacks use exact native serialized targets, independent of who drives.
        // Zero seat generation denotes walking access and requires a fresh nearby actor pose.
        internal bool TryPassengerIntent(Component control, VehicleControlMethod method, out PassengerVehicleIntent intent)
        {
            intent = default;
            if (session == null || applying || session.IsRunning != true || control == null) return false;
            PassengerVehicleControl kind = control is Vehicle_IgnitionSwitch ? PassengerVehicleControl.Ignition :
                control is Vehicle_Button_LightSwitch ? PassengerVehicleControl.LowBeam : control is Vehicle_Button_Highbeam ? PassengerVehicleControl.HighBeam :
                control is Vehicle_Button_HazardLights ? PassengerVehicleControl.Hazard : control is Vehicle_Button_ExtraLights ? PassengerVehicleControl.ExtraLights :
                control is Vehicle_Button_Shifter ? PassengerVehicleControl.Gear : control is Vehicle_Button_LightDomeOptions ? PassengerVehicleControl.Dome :
                control is Vehicle_Button_ParkingBrake ? PassengerVehicleControl.ParkingBrake : control is Vehicle_HighLowShifter ? PassengerVehicleControl.HighLow : 0;
            if (!PassengerVehicleIntent.Supported(kind, method)) return false;
            var target = PassengerControlTarget(control, kind);
            var b = bindings.Values.FirstOrDefault(value => ReferenceEquals(value.Vehicle, target));
            if (b == null || b.Dormant) return false;
            // The current local driver retains native driving input and physical callback behavior.
            if (gameplay.Get_PlayerState == PlayerState.InVehicle_Driver && gameplay.Get_CurrentVehicleController == b.Vehicle) return false;
            bool seated = gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger && gameplay.Get_CurrentVehicleController == b.Vehicle &&
                !b.PassengerExitPending && b.PassengerState.Occupied && b.PassengerState.Owner == session.LocalPeerId;
            if (!seated && gameplay.Get_PlayerState != PlayerState.Walking) return false;
            var controls = PassengerControls(b, kind);
            int index = Array.IndexOf(controls, control);
            if (index < 0 || index > byte.MaxValue) return false;
            intent = new PassengerVehicleIntent(0, b.Id, session.LocalPeerId, seated ? b.PassengerState.Generation : 0,
                b.State.Driver.Owner, b.State.Driver.Generation, kind, method, (byte)index);
            return true;
        }
        private static Type? PassengerControlType(PassengerVehicleControl kind)
        {
            return kind == PassengerVehicleControl.Ignition ? typeof(Vehicle_IgnitionSwitch) :
                kind == PassengerVehicleControl.LowBeam ? typeof(Vehicle_Button_LightSwitch) : kind == PassengerVehicleControl.HighBeam ? typeof(Vehicle_Button_Highbeam) :
                kind == PassengerVehicleControl.Hazard ? typeof(Vehicle_Button_HazardLights) : kind == PassengerVehicleControl.ExtraLights ? typeof(Vehicle_Button_ExtraLights) :
                kind == PassengerVehicleControl.Gear ? typeof(Vehicle_Button_Shifter) : kind == PassengerVehicleControl.Dome ? typeof(Vehicle_Button_LightDomeOptions) : kind == PassengerVehicleControl.ParkingBrake ? typeof(Vehicle_Button_ParkingBrake) :
                kind == PassengerVehicleControl.HighLow ? typeof(Vehicle_HighLowShifter) : null;
        }
        private static object? PassengerControlTarget(Component control, PassengerVehicleControl kind) =>
            control is Vehicle_Button_LightSwitch low ? low.vehicleController : control is Vehicle_Button_Highbeam high ? high.vehicleController :
            control is Vehicle_Button_HazardLights hazard ? hazard.vehicleController : AccessTools.Field(control.GetType(),
                kind == PassengerVehicleControl.Ignition || kind == PassengerVehicleControl.ExtraLights ||
                kind == PassengerVehicleControl.ParkingBrake || kind == PassengerVehicleControl.HighLow ? "vehicleController" : "vc")?.GetValue(control);
        private static Component[] PassengerControls(Binding b, PassengerVehicleControl kind)
        {
            Type? type = PassengerControlType(kind);
            if (type == null) return Array.Empty<Component>();
            // Include inactive alternatives in stable indexing; liveness is checked on use.
            return b.Vehicle.GetComponentsInChildren(type, true)
                .Where(control => ReferenceEquals(PassengerControlTarget(control, kind), b.Vehicle))
                .OrderBy(control => RuntimeProbe.CanonicalPath(control.transform), StringComparer.Ordinal).ToArray();
        }
        private static Component? PassengerControl(Binding b, PassengerVehicleControl kind, bool releasingIgnition = false, byte? index = null)
        {
            var controls = PassengerControls(b, kind);
            if (index.HasValue)
            {
                if (index.Value >= controls.Length) return null;
                var exact = controls[index.Value];
                return exact is Behaviour live && (releasingIgnition || live.isActiveAndEnabled && exact.gameObject.activeInHierarchy) ? exact : null;
            }
            var usable = controls.Where(control => control is Behaviour live &&
                (releasingIgnition || live.isActiveAndEnabled && control.gameObject.activeInHierarchy)).ToArray();
            return usable.Length == 1 ? usable[0] : null;
        }
        internal bool PassengerIntentCurrent(PassengerVehicleIntent intent)
        {
            if (!bindings.TryGetValue(intent.Vehicle, out var b) || b.Dormant ||
                b.State.Driver.Owner != intent.DriverOwner || b.State.Driver.Generation != intent.DriverGeneration) return false;
            if (!intent.Walking && (b.PassengerId == 0 || !b.PassengerState.Occupied ||
                b.PassengerState.Owner != intent.Actor || b.PassengerState.Generation != intent.SeatGeneration)) return false;
            return PassengerControl(b, intent.Control, index: intent.ControlIndex) != null;
        }
        internal bool AuthenticatePassengerIntent(uint peer, PassengerVehicleIntent intent)
        {
            if (session == null || !host || TransfersBlocked || !Ready || peer != intent.Actor || !PassengerIntentCurrent(intent)) return false;
            var b = bindings[intent.Vehicle];
            var control = PassengerControl(b, intent.Control, index: intent.ControlIndex)!;
            if (peer == 0)
                return intent.Walking ? intent.AllowsWalkingActor(peer, gameplay.Get_PlayerState == PlayerState.Walking && !gameplay.Get_IsSleeping, 0,
                    (gameplay.PlayerWalking.transform.position - control.transform.position).sqrMagnitude) :
                    gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger && gameplay.Get_CurrentVehicleController == b.Vehicle;
            if (!presence.TryGetValue(peer, out var seen) || Time.unscaledTime - seen.Time > 2) return false;
            return intent.Walking ? intent.AllowsWalkingActor(peer, seen.Pose.State == (byte)PlayerState.Walking, Time.unscaledTime - seen.Time,
                (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - control.transform.position).sqrMagnitude) :
                seen.Pose.State == (byte)PlayerState.InVehicle_Passenger;
        }
        // #353: seat authority can precede its owner's next pose packet. Only that exact
        // canonical seat/control stamp and a fresh preceding Walking pose may wait for it.
        internal bool CanDeferPassengerIntent(uint peer, PassengerVehicleIntent intent) =>
            !intent.Walking && peer != 0 && session != null && host && Ready && !TransfersBlocked && !gameplay.Get_IsSleeping &&
            peer == intent.Actor && PassengerIntentCurrent(intent) && presence.TryGetValue(peer, out var observed) &&
            Time.unscaledTime - observed.Time <= 2 && observed.Pose.State == (byte)PlayerState.Walking;
        internal bool PassengerWalkingActorCurrent(PassengerVehicleIntent intent)
        {
            if (!intent.Walking) return true;
            if (session == null || !bindings.TryGetValue(intent.Vehicle, out var b)) return false;
            var control = PassengerControl(b, intent.Control, index: intent.ControlIndex);
            if (control == null) return false;
            if (intent.Actor == session.LocalPeerId)
                return intent.AllowsWalkingActor(intent.Actor, gameplay.Get_PlayerState == PlayerState.Walking && !gameplay.Get_IsSleeping, 0,
                    (gameplay.PlayerWalking.transform.position - control.transform.position).sqrMagnitude);
            return presence.TryGetValue(intent.Actor, out var observed) && intent.AllowsWalkingActor(intent.Actor,
                observed.Pose.State == (byte)PlayerState.Walking, Time.unscaledTime - observed.Time,
                (new Vector3(observed.Pose.X, observed.Pose.Y, observed.Pose.Z) - control.transform.position).sqrMagnitude);
        }
        internal bool PassengerIntentAwake(PassengerVehicleIntent intent) =>
            bindings.TryGetValue(intent.Vehicle, out var b) && b.Vehicle.IsAwake;
        internal bool PassengerDeferralSleeping => gameplay.Get_IsSleeping;
        internal string PassengerIntentDiagnostic(uint peer, PassengerVehicleIntent intent)
        {
            string native = bindings.TryGetValue(intent.Vehicle, out var b)
                ? "seat=" + b.PassengerState.Owner + "/" + b.PassengerState.Generation + ",driver=" + b.State.Driver.Owner + "/" + b.State.Driver.Generation +
                  ",dormant=" + b.Dormant + ",controlLive=" + (PassengerControl(b, intent.Control) != null)
                : "unbound vehicle";
            string pose = presence.TryGetValue(peer, out var observed)
                ? "poseState=" + (PlayerState)observed.Pose.State + ",poseAge=" + (Time.unscaledTime - observed.Time).ToString("F3")
                : "pose=missing";
            return "peer=" + peer + ",actor=" + intent.Actor + ",request=" + intent.Control + "/" + intent.Method +
                ",expectedSeat=" + intent.SeatGeneration + ",expectedDriver=" + intent.DriverOwner + "/" + intent.DriverGeneration +
                ",ready=" + Ready + ",transfersBlocked=" + TransfersBlocked + ",current=" + PassengerIntentCurrent(intent) +
                "," + native + "," + pose + ",hostPlayer=" + gameplay.Get_PlayerState;
        }
        internal bool PassengerSimulationLocal(PassengerVehicleIntent intent) => session != null &&
            bindings.TryGetValue(intent.Vehicle, out var b) && IsLocal(b.State) && intent.Simulator == session.LocalPeerId &&
            b.State.Driver.Owner == intent.DriverOwner && b.State.Driver.Generation == intent.DriverGeneration;
        internal void ApplyPassengerControl(PassengerVehicleIntent intent, bool release = false)
        {
            if (!bindings.TryGetValue(intent.Vehicle, out var b)) return;
            // A held key may have become disabled since MouseDown. Its authenticated release
            // still clears the unique native key's held flag; disabled controls cannot receive new actions.
            var control = PassengerControl(b, intent.Control, releasingIgnition: release, index: intent.ControlIndex);
            if (control == null) return;
            bool before = applying; applying = true;
            try
            {
                // Native Powertrain skips sleeping LOD components. An authenticated passenger
                // may shift a parked host-simulated car before touching its ignition key.
                if (!release && !b.Vehicle.IsAwake) b.Vehicle.Wake();
                string method = release ? "MouseUp" : intent.Control == PassengerVehicleControl.Dome ? "ChangeLightMode" : intent.Method.ToString();
                AccessTools.Method(control.GetType(), method, Type.EmptyTypes).Invoke(control, null);
            }
            finally { applying = before; }
        }
        internal void PassengerPromptRefresh(Component control) => QueuePromptRefresh(control);
        internal bool IsPassengerControl(Component control, PassengerVehicleIntent intent) =>
            PassengerIntentCurrent(intent) && PassengerSimulationLocal(intent) && bindings.TryGetValue(intent.Vehicle, out var b) &&
            PassengerControl(b, intent.Control, index: intent.ControlIndex) == control;

#if MONMP_DEVELOPMENT
        internal Component? TestPassengerControl(PassengerVehicleControl kind) => PassengerControl(TestKonigBinding, kind);


        internal string TestPassengerControlDiagnostic(PassengerVehicleControl kind)
        {
            var b = TestKonigBinding; var type = PassengerControlType(kind);
            if (type == null) return kind + ": unsupported type";
            var candidates = b.Vehicle.GetComponentsInChildren(type, true);
            return kind + ": candidates=" + candidates.Length + "; liveBound=" + candidates.Count(control =>
                control is Behaviour behaviour && behaviour.isActiveAndEnabled && control.gameObject.activeInHierarchy &&
                ReferenceEquals(PassengerControlTarget(control, kind), b.Vehicle)) + "; " + string.Join("; ", candidates.Select(control =>
                RuntimeProbe.CanonicalPath(control.transform) + "[enabled=" + (control is Behaviour behaviour && behaviour.enabled) +
                ",active=" + control.gameObject.activeInHierarchy + ",bound=" + ReferenceEquals(PassengerControlTarget(control, kind), b.Vehicle) + "]"));
        }


        internal ulong TestPassengerControlVehicle => TestKonigBinding.Id;


        internal Action TestCapturePassengerFixtureRestore()
        {
            var b = TestKonigBinding;
            // The guest's pre-session native baseline predates replica suppression. Capturing its
            // current remote Rigidbody mode would reintroduce kinematic physics after teardown.
            var original = host ? Capture(b) : b.Original;
            return () =>
            {
                if (!disposed) throw new InvalidOperationException("Passenger fixture restoration must follow session teardown.");
                if (b.Vehicle == null) return;
                var ignition = PassengerControl(b, PassengerVehicleControl.Ignition) as Vehicle_IgnitionSwitch;
                ignition?.MouseUp(); ignition?.StopAllCoroutines();
                b.State = original;
                b.Vehicle.SetMultiplayerInstanceType(b.OriginalMode, false);
                ApplyBody(b, original);
                b.Vehicle.input.autoSetInput = b.AutoInput;
                b.Vehicle.freezeWhileIdle = b.FreezeIdle; b.Vehicle.freezeWhileAsleep = b.FreezeSleep;
                b.Vehicle.vehicleRigidbody.isKinematic = b.OriginalKinematic;
                b.Vehicle.vehicleRigidbody.constraints = b.OriginalConstraints;
                b.Vehicle.vehicleRigidbody.collisionDetectionMode = b.OriginalCollision;
            };
        }
#endif

    }
}
