using System;
using HarmonyLib;
using Leo;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // One compatible roaming-state fixture. It deliberately excludes treat,
    // friendship, car-bed and mismatched-taming authority.
    internal sealed class PairedLeoTest
    {
        private static Vector3 guestBaseline;
        private static float guestThirst;
        private static int guestThirstTicks;
        private static Leo_State guestBaselineState;
        private static bool guestAgentEnabled, guestColliderEnabled;
        private static int guestAnimatorState, guestAnimatorCustomValue;
        private static float guestAnimatorForward, guestAnimatorTurn;
        private readonly MonMPPlugin plugin;
        private readonly Leo_Manager native;
        private readonly bool travelOnly;
        private int phase;
        private float deadline;
        internal string Status => "Leo phase=" + phase + ", mirror=" + plugin.World?.Leo.Mirroring +
            ", state=" + native.Get_Leo_State + ", thirst=" + native.Get_TamedSM.Get_Thirst.ToString("F3");

        internal static void Prepare(Gameplay gameplay, bool host, bool travelOnly)
        {
            var leo = gameplay.Get_Leo_Manager ?? throw new InvalidOperationException("Leo test fixture has no native manager.");
            var machine = leo.Get_TamedSM ?? throw new InvalidOperationException("Leo test fixture has no tamed state machine.");
            var change = AccessTools.Method(typeof(Leo_Manager), "ChangeState") ??
                throw new InvalidOperationException("Leo test fixture cannot enter the native tamed state.");
            change.Invoke(leo, new object[] { machine });
            leo.Get_StayAtHome = false;
            leo.Get_AttachedHome = House_Location.Roulotte;
            leo.transform.position = gameplay.PlayerWalking.transform.position + (host ? Vector3.right : Vector3.left) * 2;
            machine.Get_Thirst = host ? 0.42f : 0.08f;
            machine.Get_Hunger = host ? 0.37f : 0.11f;
            machine.Get_TickSinceThirsty = host ? 21 : 7;
            machine.Get_TickSinceHungry = host ? 34 : 9;
            if (host) machine.enabled = false; // Keep the host's fixture in this one safe roaming state.
            else
            {
                var roamTimer = AccessTools.Field(typeof(RoamingPlayer), "Time_NextMovement") ??
                    throw new InvalidOperationException("Leo roaming timer field is missing.");
                roamTimer.SetValue(machine.roamingPlayerState, Time.timeSinceLevelLoad + 180f);
                guestBaseline = leo.transform.position;
                guestBaselineState = leo.Get_Leo_State;
                guestThirst = machine.Get_Thirst;
                guestThirstTicks = machine.Get_TickSinceThirsty;
                guestAgentEnabled = leo.GetComponent<UnityEngine.AI.NavMeshAgent>().enabled;
                guestColliderEnabled = leo.GetComponent<Collider>().enabled;
                var animator = leo.GetComponent<Animator>();
                guestAnimatorState = animator.GetInteger("State");
                guestAnimatorCustomValue = animator.GetInteger("customValue");
                guestAnimatorForward = animator.GetFloat("Forward");
                guestAnimatorTurn = animator.GetFloat("Turn");
            }
            if (!leo.Get_IsTamed || leo.Get_Leo_State != Leo_State.RoamingPlayer)
                throw new InvalidOperationException("Leo test fixture did not reach on-foot roaming state.");
        }

        internal PairedLeoTest(MonMPPlugin plugin, Gameplay gameplay, bool travelOnly)
        {
            this.plugin = plugin; native = gameplay.Get_Leo_Manager; this.travelOnly = travelOnly;
            if (native == null || plugin.World?.Leo.Bound != true) throw new InvalidOperationException("Shared Leo was not registered.");
        }

        internal bool Tick()
        {
            if (plugin.Session?.IsReady != true || plugin.World?.Channel.FullySynchronized != true) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 55;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Leo paired fixture timed out: " + Status);
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0 && plugin.Session!.Peers.Count >= 2)
            {
                if (travelOnly)
                {
                    native.Get_TamedSM.ChangeState(native.Get_TamedSM.followingPlayerState);
                    native.GetComponent<UnityEngine.AI.NavMeshAgent>().enabled = false;
                    native.transform.position += Vector3.right * 4;
                }
                native.Get_TamedSM.Get_Thirst = 0.63f;
                phase = 1;
            }
            if (phase == 1 && plugin.Session!.Peers.Count == 1)
            {
                // The guest's pet asked this host to show Leo's heart too.
                if (!travelOnly && plugin.World!.LeoHearts!.TestGuestShown < 1)
                    throw new InvalidOperationException("Host never showed Leo's heart for the guest's pet.");
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            var mirror = plugin.World!.Leo;
            var state = mirror.TestState;
            if (phase == 0)
            {
                if (!mirror.Mirroring || state == null || state.Thirst < 0.62f) return false;
                Leo_State expected = travelOnly ? Leo_State.FollowingPlayer : Leo_State.RoamingPlayer;
                if (!state.IsTamed || state.NativeState != (byte)expected || native.Get_Leo_State != expected ||
                    Vector3.Distance(native.transform.position, new Vector3(state.X, state.Y, state.Z)) > 0.25f ||
                    Math.Abs(native.Get_TamedSM.Get_Thirst - state.Thirst) > 0.02f ||
                    Math.Abs(native.Get_TamedSM.Get_Hunger - state.Hunger) > 0.02f ||
                    native.Get_TamedSM.Get_TickSinceThirsty != state.TicksSinceThirsty ||
                    Vector3.Distance(native.transform.position, guestBaseline) < 1)
                    throw new InvalidOperationException("Leo host " + expected + " state or needs did not apply: " + Status);
                // Petting happens next to Leo; the host checks the guest is in reach before showing the heart.
                var gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
                var walker = gameplay.PlayerWalking.transform;
                if (!travelOnly && Vector3.Distance(walker.position, native.transform.position) > 3f)
                {
                    Vector3 near = native.transform.position + Vector3.forward * 1.5f;
                    walker.position = near;
                    if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = near; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
                    return false;
                }
                int friendship = GameManager.Get_Friendship[24];
                int personalTicks = native.Get_TamedSM.Get_TickSinceThirsty;
                native.MouseDown(); native.Use(); native.TreatGiven(null!); native.CarBed(null!); native.PlayerIsSleeping(null!);
                if (GameManager.Get_Friendship[24] != friendship || native.Get_TamedSM.Get_TickSinceThirsty != personalTicks)
                    throw new InvalidOperationException("Guest Leo interaction changed friendship or native needs.");
                if (!travelOnly && !native.petHearth.gameObject.activeSelf)
                    throw new InvalidOperationException("Guest's pet did not pop Leo's heart locally.");
                phase = travelOnly ? 1 : 2;
            }
            // The host shows the guest's heart and publishes it; the guest sees the shared heart count move.
            if (phase == 2)
            {
                if (plugin.World!.LeoHearts!.TestHearts < 1) return false;
                phase = 1;
            }
            if (phase == 1)
            {
                plugin.EndPairedTestSession();
                if (native.Get_Leo_State != guestBaselineState || Vector3.Distance(native.transform.position, guestBaseline) > 0.25f ||
                    Math.Abs(native.Get_TamedSM.Get_Thirst - guestThirst) > 0.02f ||
                    native.Get_TamedSM.Get_TickSinceThirsty != guestThirstTicks ||
                    native.GetComponent<UnityEngine.AI.NavMeshAgent>().enabled != guestAgentEnabled ||
                    native.GetComponent<Collider>().enabled != guestColliderEnabled ||
                    native.GetComponent<Animator>().GetInteger("State") != guestAnimatorState ||
                    native.GetComponent<Animator>().GetInteger("customValue") != guestAnimatorCustomValue ||
                    Math.Abs(native.GetComponent<Animator>().GetFloat("Forward") - guestAnimatorForward) > 0.02f ||
                    Math.Abs(native.GetComponent<Animator>().GetFloat("Turn") - guestAnimatorTurn) > 0.02f)
                    throw new InvalidOperationException("Guest Leo baseline was not restored on disconnect.");
                return true;
            }
            return false;
        }
    }
}
