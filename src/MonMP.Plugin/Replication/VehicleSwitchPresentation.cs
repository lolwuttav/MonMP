using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Native callers play once on the vehicle simulator. Live events replay only the sound on
    // observers; saved light/ignition state remains silent. Source identities exclude the spawned
    // vehicle's scene-root path and use its registered entity plus a relative same-name ordinal.
    internal static class VehicleSwitchPresentation
    {
        private const byte VehicleOn = 1, VehicleOff = 2, GenericOn = 3, GenericOff = 4,
            ParkingOn = 5, ParkingOff = 6, ParkingLock = 7, ParkingUnlock = 8,
            TransferFourWheel = 9, TransferTwoWheel = 10;
        private static readonly AccessTools.FieldRef<Vehicle_HighLowShifter, int> TransferPosition =
            AccessTools.FieldRefAccess<Vehicle_HighLowShifter, int>("CurrentPosition");
        private sealed class Source
        {
            internal ulong Id;
            internal Component Control = null!;
            internal VehicleController Vehicle = null!;
            internal bool Generic, Dome, Parking, Transfer;
        }

#if MONMP_DEVELOPMENT
        internal static int TestPosted { get; private set; }


        internal static int TestPlayed { get; private set; }


        internal static bool TestSoundObserved { get; private set; }
#endif

        private static readonly Dictionary<ulong, int> posted = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, int> played = new Dictionary<ulong, int>();
        private static readonly HashSet<ulong> sounded = new HashSet<ulong>();

#if MONMP_DEVELOPMENT
        internal static int TestPostedFor(ulong source) => posted.TryGetValue(source, out var count) ? count : 0;


        internal static int TestPlayedFor(ulong source) => played.TryGetValue(source, out var count) ? count : 0;


        internal static bool TestSoundObservedFor(ulong source) => sounded.Contains(source);


        internal static ulong TestSourceId(Component control) => TrySource(control, out var source) ? source.Id : 0;
#endif


        internal static void Native(SOUND_Vehicle_Switch sound, bool on)
        {
            if (sound == null) return;
            var controls = sound.GetComponents<Component>().Where(c => c != null &&
                (c.GetType() == typeof(Vehicle_IgnitionSwitch) || c.GetType() == typeof(Vehicle_Button_LightSwitch) ||
                 c.GetType() == typeof(Vehicle_Button_Highbeam))).ToArray();
            if (controls.Length == 1) Post(controls[0], on ? VehicleOn : VehicleOff);
        }
        internal static void NativeExtra(Vehicle_Button_ExtraLights control)
        {
            if (TrySource(control, out var source))
                Post(control, source.Vehicle.effectsManager.lightsManager.extraLights.On ? GenericOn : GenericOff);
        }
        internal static void NativeDome(Vehicle_Button_LightDomeOptions control) => Post(control, GenericOn);
        internal static void NativeParking(SOUND_Vehicle_ParkingBrake sound, bool on)
        {
            if (sound == null) return;
            var controls = sound.GetComponents<Vehicle_Button_ParkingBrake>();
            if (controls.Length == 1) Post(controls[0], on ? ParkingOn : ParkingOff);
        }
        internal static void NativeParkingLock(Vehicle_Button_ParkingBrake control, int before)
        {
            // Use can also lift an inactive brake, whose own On callback posts the ratchet cue.
            // The separate lock/unlock click always follows that callback in native order.
            int after = control.LoadFromSave;
            if (after != before) Post(control, after == 2 ? ParkingLock : ParkingUnlock);
        }
        internal static int CurrentTransferPosition(Vehicle_HighLowShifter control) => TransferPosition(control);
        internal static void NativeTransfer(Vehicle_HighLowShifter control, int before)
        {
            int after = TransferPosition(control);
            if (after != before && (after == 0 || after == 1))
                Post(control, after == 1 ? TransferFourWheel : TransferTwoWheel);
        }

        private static void Post(Component control, byte variant)
        {
            var plugin = MonMPPlugin.Instance;
            var vehicles = plugin?.World?.Vehicles;
            if (SharedPresentationEvents.Replaying || PresentationReplays.Events?.Live != true ||
                vehicles == null || plugin?.Session?.IsRunning != true || !TrySource(control, out var source) ||
                !Allowed(source, variant) || !vehicles.CanControl(source.Vehicle) ||
                !vehicles.TryGetDriverState(source.Vehicle, out var state)) return;
            if (!plugin.Session.IsHost && state.Driver.Owner != plugin.Session.LocalPeerId) return;
            // PresentationEvent.Value is bounded to 100000 and precisely represents every integer
            // in that range. Refuse an unsupported lease counter rather than losing authentication.
            if (state.Driver.Generation > 100000) return;

#if MONMP_DEVELOPMENT
            TestPosted++;

            posted[source.Id] = TestPostedFor(source.Id) + 1;
#endif
            PresentationReplays.Post(PresentationKind.VehicleSwitch, variant, source.Id,
                control.transform.position, state.Driver.Generation);
        }

        internal static bool AcceptCue(uint peer, PresentationEvent e)
        {
            var plugin = MonMPPlugin.Instance;
            var vehicles = plugin?.World?.Vehicles;
            if (plugin?.Session?.IsHost != true || peer == 0 || e.Kind != (byte)PresentationKind.VehicleSwitch || vehicles == null ||
                !Find(e.Target, out var source) || !Allowed(source, e.Variant) ||
                !vehicles.TryGetDriverState(source.Vehicle, out var state) || state.Driver.Owner != peer ||
                state.Driver.Generation > 100000 || e.Value != state.Driver.Generation) return false;
            // The host's smoothed remote vehicle may lag a fast driver's reported cue position.
            // Authorization binds the exact live source and lease; replay uses that bound source's
            // current position, never the guest's coordinates. The bus still bounds avatar range.
            return true;
        }

        internal static void Play(PresentationEvent e)
        {
            if (e.Kind != (byte)PresentationKind.VehicleSwitch || !Find(e.Target, out var source) || !Allowed(source, e.Variant)) return;
            var sound = UnityEngine.Object.FindObjectOfType<SOUND_Manager>();
            if (sound == null) return;
            int routedBefore = SpatialSounds.Rerouted;
            bool heard = false;
            using (SpatialSounds.At(source.Control.transform.position))
            {
                if (e.Variant == GenericOn) sound.PlaySound_SwitchOn();
                else if (e.Variant == GenericOff) sound.PlaySound_SwitchOff();
                else if (e.Variant == ParkingLock) sound.PlaySound_PartInstalled();
                else if (e.Variant == ParkingUnlock) sound.PlaySound_PartRemoved();
                else
                {
                    var clip = e.Variant == ParkingOn ? sound.Vehicle_ParkingBrake_On :
                        e.Variant == ParkingOff ? sound.Vehicle_ParkingBrake_Off :
                        source.Transfer ? AccessTools.Field(typeof(Vehicle_HighLowShifter),
                            e.Variant == TransferFourWheel ? "Clip_4x4" : "Clip_2x4")?.GetValue(source.Control) as AudioClip :
                        e.Variant == VehicleOn ? sound.Vehicle_Switch_On : sound.Vehicle_Switch_Off;
                    if (clip == null) return;
                    var emitter = sound.PlayOneShot2D(clip, _randomPitch: true);
                    heard = emitter != null && emitter.isPlaying;
                }
            }
            heard |= SpatialSounds.Rerouted > routedBefore;

#if MONMP_DEVELOPMENT
            TestSoundObserved |= heard;
#endif

            if (heard) sounded.Add(source.Id);
#if MONMP_DEVELOPMENT
            played[source.Id] = TestPlayedFor(source.Id) + 1;

            TestPlayed++;
#endif

        }

        private static bool Allowed(Source source, byte variant) => source.Parking
            ? variant == ParkingOn || variant == ParkingOff || variant == ParkingLock || variant == ParkingUnlock
            : source.Transfer ? variant == TransferFourWheel || variant == TransferTwoWheel
            : source.Generic ? variant == GenericOn || !source.Dome && variant == GenericOff
            : variant == VehicleOn || variant == VehicleOff;

        private static bool Find(ulong id, out Source source)
        {
            source = null!;
            var vehicles = MonMPPlugin.Instance?.World?.Vehicles;
            if (vehicles == null) return false;
            Source? found = null;
            foreach (var vehicle in vehicles.BoundControllers)
            {
                if (vehicle == null || !vehicle.gameObject.activeInHierarchy) continue;
                foreach (var control in vehicle.GetComponentsInChildren<Component>(true))
                {
                    if (!TrySource(control, out var candidate) || candidate.Vehicle != vehicle || candidate.Id != id) continue;
                    if (found != null) return false; // Ambiguous identities fail closed.
                    found = candidate;
                }
            }
            if (found == null) return false;
            source = found; return true;
        }

        private static bool TrySource(Component? control, out Source source)
        {
            source = null!;
            if (control == null || control is not Behaviour behaviour || !behaviour.isActiveAndEnabled ||
                !control.gameObject.scene.IsValid()) return false;
            Type type = control.GetType();
            bool generic = type == typeof(Vehicle_Button_ExtraLights) || type == typeof(Vehicle_Button_LightDomeOptions);
            bool parking = type == typeof(Vehicle_Button_ParkingBrake), transfer = type == typeof(Vehicle_HighLowShifter);
            if (!generic && type != typeof(Vehicle_IgnitionSwitch) && type != typeof(Vehicle_Button_LightSwitch) &&
                type != typeof(Vehicle_Button_Highbeam) && !parking && !transfer) return false;
            if (control.GetComponents(type).Length != 1) return false;
            var vehicles = MonMPPlugin.Instance?.World?.Vehicles;
            var vehicle = control.GetComponentInParent<VehicleController>();
            if (vehicles == null || vehicle == null || !vehicle.gameObject.activeInHierarchy) return false;
            ulong entity = vehicles.EntityOf(vehicle);
            if (entity == 0) return false;
            string field = type == typeof(Vehicle_Button_LightDomeOptions) ? "vc" : "vehicleController";
            if (!ReferenceEquals(AccessTools.Field(type, field)?.GetValue(control), vehicle)) return false;
            if (parking && control.GetComponent<SOUND_Vehicle_ParkingBrake>() == null) return false;
            if (!generic && !parking && !transfer && control.GetComponent<SOUND_Vehicle_Switch>() == null) return false;
            string path = RelativePath(control.transform, vehicle.transform);
            source = new Source { Id = StaticId.Hash(entity.ToString("X16", CultureInfo.InvariantCulture) +
                "/vehicle-switch-cue-v1/" + type.Name + "/" + path), Control = control, Vehicle = vehicle,
                Generic = generic, Dome = type == typeof(Vehicle_Button_LightDomeOptions), Parking = parking, Transfer = transfer };
            return true;
        }

        private static string RelativePath(Transform transform, Transform root)
        {
            var segments = new Stack<string>();
            for (var current = transform; current != root; current = current.parent)
            {
                int ordinal = 0;
                for (int i = 0; i < current.parent.childCount; i++)
                {
                    var sibling = current.parent.GetChild(i);
                    if (sibling == current) break;
                    if (sibling.name == current.name) ordinal++;
                }
                segments.Push(StaticId.Segment(current.name, ordinal));
            }
            return string.Join("/", segments);
        }
        internal static void Reset()
        {
#if MONMP_DEVELOPMENT
TestPosted = TestPlayed = 0;

 TestSoundObserved = false;
#endif
 posted.Clear(); played.Clear(); sounded.Clear(); }
    }
}
