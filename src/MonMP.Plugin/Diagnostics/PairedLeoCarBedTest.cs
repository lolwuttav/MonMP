using System;
using HarmonyLib;
using Leo;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using NWH;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Exercises the guest's native car-bed click through the host-authoritative Leo command,
    // then checks the native enter/exit transitions and session cleanup on both copies.
    internal sealed class PairedLeoCarBedTest
    {
        private static Vector3 baselinePosition, baselineScale, baselinePlayerPosition;
        private static Quaternion baselineRotation;
        private static Transform? baselineParent;
        private static Leo_State baselineState;
        private static bool baselineStayAtHome, baselineAgentEnabled, baselineColliderEnabled;
        private static House_Location baselineHome;
        private static float baselineThirst, baselineHunger, baselineAnimatorForward, baselineAnimatorTurn;
        private static int baselineThirstTicks, baselineHungerTicks, baselineAnimatorState, baselineAnimatorCustom;
        private static bool baselinePassengerDoorClosed;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly Leo_Manager native;
        private readonly Vehicle vehicle;
        private readonly ulong bedSocket;
        private readonly VehiclePartState originalBedPart;
        private readonly bool originalPassengerDoorClosed;
        private int phase;
        private float next, deadline, enteringDeadline;
        private bool started;

        internal string Status
        {
            get
            {
                var assigned = native.Get_TamedSM?.Get_CarBed;
                var target = Bed();
                var shared = plugin.World?.Leo.TestState;
                return "Leo car-bed phase=" + phase + "; state=" + native.Get_Leo_State + "; tamed=" + native.Get_IsTamed +
                    "; shared=" + shared?.NativeState + "/" + shared?.InVehicleId +
                    "; assignedBed=" + BedDescription(assigned) + "; targetBed=" + BedDescription(target) +
                    "; sameBed=" + ReferenceEquals(assigned, target) + "; doorClosed=" + vehicle.isPassengerDoorClosed +
                    "; bedInstalled=" + vehicle.vehicle_PartsManager?.DogBed?.isInstalled +
                    "; player-to-bed=" + (target == null ? -1f : Vector3.Distance(gameplay.PlayerWalking.transform.position, target.transform.position)).ToString("F1");
            }
        }

        private static string BedDescription(CarBed? bed) => bed == null ? "null" : bed.name + "@" +
            (bed.Get_Vehicle == null ? "no-vehicle" : bed.Get_Vehicle.vehicle_type.ToString());

        internal static void Prepare(Gameplay gameplay)
        {
            var leo = gameplay.Get_Leo_Manager ?? throw new InvalidOperationException("Leo car-bed fixture has no native manager.");
            var machine = leo.Get_TamedSM ?? throw new InvalidOperationException("Leo car-bed fixture has no tamed state machine.");
            var transition = AccessTools.Method(typeof(Leo_Manager), "ChangeState") ??
                throw new InvalidOperationException("Leo car-bed fixture cannot enter the native tamed state.");
            transition.Invoke(leo, new object[] { machine });
            leo.Get_StayAtHome = false;
            leo.Get_AttachedHome = House_Location.Roulotte;
            machine.enabled = true;
            leo.transform.position = gameplay.PlayerWalking.transform.position + Vector3.right * 2f;
            var roamTimer = AccessTools.Field(typeof(RoamingPlayer), "Time_NextMovement") ??
                throw new InvalidOperationException("Leo roaming timer field is missing.");
            roamTimer.SetValue(machine.roamingPlayerState, Time.timeSinceLevelLoad + 600f);
            if (!leo.Get_IsTamed || leo.Get_Leo_State != Leo_State.RoamingPlayer)
                throw new InvalidOperationException("Leo car-bed fixture did not reach native on-foot roaming state.");

            baselinePosition = leo.transform.position;
            baselineRotation = leo.transform.rotation;
            baselineScale = leo.transform.localScale;
            baselineParent = leo.transform.parent;
            baselinePlayerPosition = gameplay.PlayerWalking.transform.position;
            baselineState = leo.Get_Leo_State;
            baselineStayAtHome = leo.Get_StayAtHome;
            baselineHome = leo.Get_AttachedHome;
            baselineThirst = machine.Get_Thirst; baselineHunger = machine.Get_Hunger;
            baselineThirstTicks = machine.Get_TickSinceThirsty; baselineHungerTicks = machine.Get_TickSinceHungry;
            baselineAgentEnabled = leo.GetComponent<UnityEngine.AI.NavMeshAgent>().enabled;
            baselineColliderEnabled = leo.GetComponent<Collider>().enabled;
            var animator = leo.GetComponent<Animator>();
            baselineAnimatorState = animator.GetInteger("State"); baselineAnimatorCustom = animator.GetInteger("customValue");
            baselineAnimatorForward = animator.GetFloat("Forward"); baselineAnimatorTurn = animator.GetFloat("Turn");

            var konig = Singleton<GlobalClassManager>.i?.GetVehicles(Vehicle_Type.MainVehicle) ??
                throw new InvalidOperationException("Leo car-bed fixture has no Konig.");
            if (konig.vehicle_PartsManager?.DogBed == null)
                throw new InvalidOperationException("Leo car-bed fixture has no Konig dog-bed socket.");
            baselinePassengerDoorClosed = konig.isPassengerDoorClosed;
        }

        internal PairedLeoCarBedTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            native = gameplay.Get_Leo_Manager ?? throw new InvalidOperationException("Leo car-bed manager is unavailable.");
            vehicle = Singleton<GlobalClassManager>.i?.GetVehicles(Vehicle_Type.MainVehicle) ??
                throw new InvalidOperationException("Leo car-bed Konig is unavailable.");
            var world = plugin.World ?? throw new InvalidOperationException("Leo car-bed shared world is unavailable.");
            if (world.Leo.Bound != true || !world.VehicleParts.Ready)
                throw new InvalidOperationException("Leo car-bed shared components are not ready.");
            bedSocket = world.VehicleParts.TestSocketOfType(Car_PartsType.DogBed, Vehicle_Type.MainVehicle);
            originalBedPart = world.VehicleParts.TestOriginal(bedSocket);
            originalPassengerDoorClosed = baselinePassengerDoorClosed;
        }

        private CarBed? Bed() => vehicle.vehicle_PartsManager?.DogBed?.CustomObjectToEnableWhenPartIsInstalled?.GetComponent<CarBed>();

        internal bool Tick()
        {
            if (plugin.Session?.IsReady != true)
            {
                if (started) throw new InvalidOperationException("Leo car-bed session disconnected mid-fixture: " + Status);
                return false;
            }
            if (plugin.World?.Channel.FullySynchronized != true)
            {
                if (started) throw new InvalidOperationException("Leo car-bed world synchronization was lost mid-fixture: " + Status);
                return false;
            }
            started = true;
            if (deadline == 0) deadline = Time.unscaledTime + 110f;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Leo car-bed paired fixture timed out: " + Status);
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            var world = plugin.World!;
            var parts = world.VehicleParts;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                var installed = parts.TestEnsureInstalled(Car_PartsType.DogBed, Vehicle_Type.MainVehicle);
                if (installed.Socket != bedSocket) throw new InvalidOperationException("Konig dog-bed fixture resolved another socket.");
                if (vehicle.isPassengerDoorClosed)
                {
                    Vector3 at = world.Vehicles.Doors.TestKonigPosition(false) + vehicle.transform.forward * 1.5f + Vector3.up * 0.5f;
                    MovePlayer(at);
                    world.Vehicles.Doors.TestKonigClick(false);
                }
                phase = 1; next = Time.unscaledTime + 0.5f;
                return false;
            }
            if (phase == 1)
            {
                var bed = Bed();
                if (bed == null || !bed.gameObject.activeInHierarchy || vehicle.isPassengerDoorClosed) return false;
                if ((native.Get_Leo_State != Leo_State.RoamingPlayer && native.Get_Leo_State != Leo_State.FollowingPlayer) || !native.Get_IsTamed)
                    throw new InvalidOperationException("Host Leo left the safe on-foot fixture before the guest action: " + Status);
                phase = 2;
            }
            else if (phase == 2 && native.Get_Leo_State == Leo_State.CarBed)
            {
                var state = world.Leo.TestState;
                if (state?.InVehicleId == (int)Vehicle_Type.MainVehicle && ReferenceEquals(native.Get_TamedSM.Get_CarBed, Bed()))
                { phase = 3; next = Time.unscaledTime + 0.5f; }
                else if (enteringDeadline == 0) enteringDeadline = Time.unscaledTime + 15f;
                else if (Time.unscaledTime > enteringDeadline)
                    throw new InvalidOperationException("Host Leo entered the native car-bed state but never acquired its shared vehicle binding: " + Status);
            }
            else if (phase == 3 && IsOnFoot(native.Get_Leo_State))
            {
                if (world.Leo.TestState?.InVehicleId != -1)
                    throw new InvalidOperationException("Leo exit did not clear the shared vehicle identity: " + Status);
                phase = 4;
            }
            else if (phase == 4 && plugin.Session!.Peers.Count == 1)
            {
                RestoreHostLeo();
                if (!originalBedPart.Installed && vehicle.vehicle_PartsManager!.DogBed!.isInstalled)
                    parts.TestRemove(bedSocket);
                if (originalPassengerDoorClosed && !vehicle.isPassengerDoorClosed)
                    world.Vehicles.Doors.TestKonigClick(false);
                phase = 5; next = Time.unscaledTime + 1.5f;
                return false;
            }
            else if (phase == 5 && plugin.Session!.Peers.Count == 1)
            {
                var socket = vehicle.vehicle_PartsManager!.DogBed!;
                if (socket.isInstalled != originalBedPart.Installed || vehicle.isPassengerDoorClosed != originalPassengerDoorClosed)
                    return false;
                plugin.EndPairedTestSession();
                RestoreHostLeo();
                VerifyCleanup();
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            var world = plugin.World!;
            var mirror = world.Leo.TestState;
            if (phase == 0)
            {
                var bed = Bed();
                if (bed == null || !bed.gameObject.activeInHierarchy || vehicle.isPassengerDoorClosed || mirror == null ||
                    (mirror.NativeState != (byte)Leo_State.RoamingPlayer && mirror.NativeState != (byte)Leo_State.FollowingPlayer) || !mirror.IsTamed) return false;
                Vector3 at = bed.transform.position - vehicle.transform.forward * 1.8f + Vector3.up * 0.35f;
                MovePlayer(at);
                phase = 1; next = Time.unscaledTime + 1.5f;
                return false;
            }
            if (phase == 1)
            {
                var bed = Bed() ?? throw new InvalidOperationException("Guest did not receive the installed Konig dog bed.");
                bed.MouseDown(); // Exact native client callback: SharedLeo routes it to the host.
                phase = 2; deadline = Time.unscaledTime + 110f;
                return false;
            }
            if (phase == 2 && mirror?.NativeState == (byte)Leo_State.CarBed && mirror.InVehicleId == (int)Vehicle_Type.MainVehicle)
            {
                var bed = Bed() ?? throw new InvalidOperationException("Guest lost the Konig dog bed during Leo entry.");
                if (native.transform.parent != vehicle.transform ||
                    Vector3.Distance(native.transform.position, bed.Leo_Position.position) > 0.6f)
                    throw new InvalidOperationException("Guest did not present host Leo at its local Konig bed anchor: " + Status);
                phase = 3; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 3 && mirror?.NativeState == (byte)Leo_State.CarBed && mirror.InVehicleId == (int)Vehicle_Type.MainVehicle)
            {
                var bed = Bed() ?? throw new InvalidOperationException("Guest lost the Konig dog bed before Leo exit.");
                bed.MouseDown(); // Exercise the native exit click as a second host command.
                phase = 4; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 4 && mirror != null && IsOnFoot((Leo_State)mirror.NativeState))
            {
                if (mirror.InVehicleId != -1 || native.transform.parent == vehicle.transform)
                    throw new InvalidOperationException("Guest Leo remained attached to the car after exit: " + Status);
                plugin.EndPairedTestSession();
                VerifyGuestLeoBaseline();
                VerifyCleanup();
                return true;
            }
            return false;
        }

        private void RestoreHostLeo()
        {
            var machine = native.Get_TamedSM;
            machine.ChangeState(machine.roamingPlayerState);
            native.Get_StayAtHome = baselineStayAtHome; native.Get_AttachedHome = baselineHome;
            native.transform.SetParent(baselineParent, true);
            native.transform.SetPositionAndRotation(baselinePosition, baselineRotation);
            native.transform.localScale = baselineScale;
            machine.Get_Thirst = baselineThirst; machine.Get_Hunger = baselineHunger;
            machine.Get_TickSinceThirsty = baselineThirstTicks; machine.Get_TickSinceHungry = baselineHungerTicks;
            native.GetComponent<UnityEngine.AI.NavMeshAgent>().enabled = baselineAgentEnabled;
            native.GetComponent<Collider>().enabled = baselineColliderEnabled;
            var animator = native.GetComponent<Animator>();
            animator.SetInteger("customValue", baselineAnimatorCustom); animator.SetInteger("State", baselineAnimatorState);
            animator.SetFloat("Forward", baselineAnimatorForward); animator.SetFloat("Turn", baselineAnimatorTurn);
            if (native.Get_Leo_State != baselineState || native.transform.parent != baselineParent || Vector3.Distance(native.transform.position, baselinePosition) > 0.25f)
                throw new InvalidOperationException("Host Leo did not restore its isolated baseline after car-bed use.");
        }

        private void VerifyGuestLeoBaseline()
        {
            var animator = native.GetComponent<Animator>();
            if (native.Get_Leo_State != baselineState || native.transform.parent != baselineParent || Vector3.Distance(native.transform.position, baselinePosition) > 0.25f ||
                Quaternion.Angle(native.transform.rotation, baselineRotation) > 1f || native.Get_StayAtHome != baselineStayAtHome ||
                native.Get_AttachedHome != baselineHome || Math.Abs(native.Get_TamedSM.Get_Thirst - baselineThirst) > 0.02f ||
                Math.Abs(native.Get_TamedSM.Get_Hunger - baselineHunger) > 0.02f ||
                native.Get_TamedSM.Get_TickSinceThirsty != baselineThirstTicks || native.Get_TamedSM.Get_TickSinceHungry != baselineHungerTicks ||
                native.GetComponent<UnityEngine.AI.NavMeshAgent>().enabled != baselineAgentEnabled ||
                native.GetComponent<Collider>().enabled != baselineColliderEnabled ||
                animator.GetInteger("State") != baselineAnimatorState || animator.GetInteger("customValue") != baselineAnimatorCustom ||
                Math.Abs(animator.GetFloat("Forward") - baselineAnimatorForward) > 0.02f || Math.Abs(animator.GetFloat("Turn") - baselineAnimatorTurn) > 0.02f)
                throw new InvalidOperationException("Guest Leo state, pose, needs or presentation did not restore after disconnect: " + Status);
        }

        private void VerifyCleanup()
        {
            var socket = vehicle.vehicle_PartsManager?.DogBed ?? throw new InvalidOperationException("Konig dog-bed socket disappeared during cleanup.");
            if (socket.isInstalled != originalBedPart.Installed || vehicle.isPassengerDoorClosed != originalPassengerDoorClosed)
                throw new InvalidOperationException("Car-bed test did not restore the isolated DogBed/passenger-door fixture: bed " +
                    socket.isInstalled + "/" + originalBedPart.Installed + ", doorClosed " + vehicle.isPassengerDoorClosed + "/" + originalPassengerDoorClosed);
            gameplay.PlayerWalking.transform.position = baselinePlayerPosition;
            if (gameplay.Get_Player_Rigidbody != null)
            { gameplay.Get_Player_Rigidbody.position = baselinePlayerPosition; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }

        private void MovePlayer(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody != null)
            { gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }

        private static bool IsOnFoot(Leo_State state) => state == Leo_State.RoamingPlayer || state == Leo_State.FollowingPlayer;
    }
}
