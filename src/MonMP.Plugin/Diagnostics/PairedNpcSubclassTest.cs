using System;
using System.Linq;
using System.Reflection;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Exercises exact, reviewed NPC greeting overrides through a real guest click.
    internal sealed class PairedNpcSubclassTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedNpcInteractions npcs;
        private Actor? actor;
        private ulong actorId;
        private uint peer;
        private int phase;
        private float deadline, next;
        private readonly bool ericMilestone;
        private bool originalActive, originalCanBeFriend, originalTalkedToday, originalBoughtToday, originalGiftedToday;
        private bool hasOriginalPlayerPosition;
        private Vector3 originalPlayerPosition, originalPlayerVelocity;
        private int originalFriendship;
        internal string Status => "NPC subclass phase=" + phase + "; actor=" +
            (actor == null ? "pending" : actor.GetType().Name) + "; friendship=" +
            (actor == null ? -1 : GameManager.Get_Friendship[(int)actor.actor_Type]) +
            "; talked=" + (actor != null && GameManager.Get_Friendship_TalkedToday[(int)actor.actor_Type]) +
            "; result=" + npcs.TestLastResult + "; greeting=" + npcs.TestCurrentState.Greeting;

        internal PairedNpcSubclassTest(MonMPPlugin plugin, Gameplay gameplay, bool ericMilestone = false)
        { this.plugin = plugin; this.gameplay = gameplay; this.ericMilestone = ericMilestone; npcs = plugin.World!.NpcInteractions; }

        private int StartFriendship => ericMilestone ? 49 : 24;
        private int MilestoneFriendship => StartFriendship + 1;
        private string ExpectedGreeting => ericMilestone ? TEXT.Actors_Eric_ReachLevel50 : TEXT.Actors_Jaques_ReachLevel25;

        internal bool Tick()
        {
            if (plugin.Session?.IsReady != true || plugin.World?.Channel.FullySynchronized != true) return false;
            if (deadline == 0) deadline = Time.unscaledTime + (ericMilestone ? 600f : 120f);
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("NPC subclass test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                actor = ericMilestone ? npcs.TestFindActiveEricActor() : npcs.TestFindJacquesActor();
                if (ericMilestone)
                {
                    Require(actor != null && actor.isActiveAndEnabled && actor.gameObject.activeInHierarchy,
                        "preflight failed: this save has no naturally active, non-route Actor_Eric eligible for the reviewed talk path; the fixture will not activate an NPC. " +
                        npcs.TestEricActivationSummary);
                }
                else if (actor == null) return false;
                Require(actor != null, ericMilestone
                    ? "the scene has no naturally active, non-route Actor_Eric that can accept a talk."
                    : "the scene has no non-route Actor_Jacques instance.");
                actorId = npcs.TestActorId(actor!);
                Require(actorId != 0, ericMilestone ? "Actor_Eric has no shared identity." : "Actor_Jacques has no shared identity.");
                peer = plugin.Session.Peers.Keys.First(id => id != 0);
                int index = (int)actor!.actor_Type;
                originalActive = actor.gameObject.activeSelf;
                originalFriendship = GameManager.Get_Friendship[index];
                originalCanBeFriend = GameManager.Get_Friendship_CanBeFriendWithThisAcor[index];
                originalTalkedToday = GameManager.Get_Friendship_TalkedToday[index];
                originalBoughtToday = GameManager.Get_Friendship_BoughtWeedToday[index];
                originalGiftedToday = GameManager.Get_Friendship_SyrupGiftedToday[index];
                if (ericMilestone)
                {
                    bool anotherEligibleFriendBelow50 = GameManager.Get_Friendship_CanBeFriendWithThisAcor
                        .Select((canBeFriend, i) => (canBeFriend, i))
                        .Any(pair => pair.i != index && pair.canBeFriend && GameManager.Get_Friendship[pair.i] < 50);
                    Require(anotherEligibleFriendBelow50,
                        "the Eric milestone fixture could award the permanent all-friends achievement.");
                }
                else
                {
                    actor.gameObject.SetActive(true);
                    Require(actor.isActiveAndEnabled, "Actor_Jacques could not be activated for the isolated fixture.");
                }
                npcs.TestResetLastResult();
                npcs.TestSetFixtureState(actor, StartFriendship, talkedToday: false, markIdentity: true);
                Require(actor.CanTalk(), ericMilestone
                    ? "Actor_Eric cannot accept the staged friendship-50 conversation."
                    : "Actor_Jacques cannot accept the staged base conversation.");
                phase = 1;
            }
            else if (phase == 1 && npcs.TestPeerNear(peer, actor!.transform.position, 4f))
            {
                phase = 2;
            }
            else if (phase == 2 && npcs.TestLastResult == NpcTalkResult.Applied)
            {
                NpcInteractionState state = npcs.TestCurrentState;
                int index = (int)actor!.actor_Type;
                Require(GameManager.Get_Friendship[index] == MilestoneFriendship && GameManager.Get_Friendship_TalkedToday[index],
                    "the host did not apply one native friendship increase and daily-talk flag.");
                Require(state.SpeakerId == actorId && state.Greeting == ExpectedGreeting,
                    "the host did not run and capture the exact source-reviewed NPC milestone greeting.");
                Require(OverheadMatches(actor, state.Greeting), "the host Actor did not display the special greeting.");
                npcs.TestSetFixtureState(actor, StartFriendship, talkedToday: false);
                npcs.TestResetLastResult();
                phase = 3;
            }
            else if (phase == 3 && npcs.TestLastResult == NpcTalkResult.OutOfRange)
            {
                int index = (int)actor!.actor_Type;
                Require(GameManager.Get_Friendship[index] == StartFriendship && !GameManager.Get_Friendship_TalkedToday[index],
                    "the distant guest request changed host friendship/day state.");
                if (plugin.Session!.Peers.Count == 1)
                {
                    RestoreHostFixture();
                    return true;
                }
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                NpcInteractionState state = npcs.TestCurrentState;
                if (state.EventSequence == 0 || state.SpeakerId == 0 || state.Greeting.Length != 0) return false;
                actorId = state.SpeakerId;
                actor = npcs.TestActorById(actorId);
                bool expectedType = actor != null && (ericMilestone
                    ? actor.GetType() == typeof(Actor_Eric)
                    : actor.GetType() == typeof(Actor_Jacques));
                Require(expectedType && npcs.TestActorId(actor!) == actorId,
                    "the host's staged NPC identity is absent or has the wrong reviewed type on the guest.");
                int index = (int)actor!.actor_Type;
                if (state.Friendship[index] != StartFriendship || state.TalkedToday[index]) return false;
                originalActive = actor.gameObject.activeSelf;
                originalPlayerPosition = gameplay.PlayerWalking.transform.position;
                originalPlayerVelocity = gameplay.Get_Player_Rigidbody == null
                    ? Vector3.zero : gameplay.Get_Player_Rigidbody.velocity;
                hasOriginalPlayerPosition = true;
                if (!ericMilestone) actor.gameObject.SetActive(true);
                Require(actor.isActiveAndEnabled && npcs.TestIsSafeActor(actor),
                    "the staged NPC is not eligible for the host-routed talk path.");
                Move(actor.transform.position + Vector3.right * 1.5f);
                phase = 1; next = Time.unscaledTime + 0.6f;
            }
            else if (phase == 1)
            {
                int index = (int)actor!.actor_Type;
                if (GameManager.Get_Friendship[index] != StartFriendship || GameManager.Get_Friendship_TalkedToday[index]) return false;
                npcs.TestClickNativeActor(actor);
                Require(GameManager.Get_Friendship[index] == StartFriendship && !GameManager.Get_Friendship_TalkedToday[index],
                    "guest Actor.MouseDown mutated friendship before the host approved it.");
                phase = 2;
            }
            else if (phase == 2)
            {
                int index = (int)actor!.actor_Type;
                NpcInteractionState state = npcs.TestCurrentState;
                if (state.EventSequence == 0 || GameManager.Get_Friendship[index] != MilestoneFriendship ||
                    !GameManager.Get_Friendship_TalkedToday[index] || npcs.TestLastResult != NpcTalkResult.Applied ||
                    state.SpeakerId != actorId || state.Greeting != ExpectedGreeting) return false;
                Require(OverheadMatches(actor, state.Greeting),
                    "the guest did not render the host's exact NPC milestone greeting.");
                Move(actor.transform.position + Vector3.right * 30f);
                phase = 3; next = Time.unscaledTime + 0.6f;
            }
            else if (phase == 3)
            {
                int index = (int)actor!.actor_Type;
                if (GameManager.Get_Friendship[index] != StartFriendship || GameManager.Get_Friendship_TalkedToday[index]) return false;
                npcs.TestClickNativeActor(actor);
                Require(GameManager.Get_Friendship[index] == StartFriendship && !GameManager.Get_Friendship_TalkedToday[index],
                    "distant guest Actor.MouseDown changed local friendship/day state.");
                phase = 4;
            }
            else if (phase == 4 && npcs.TestLastResult == NpcTalkResult.OutOfRange)
            {
                int index = (int)actor!.actor_Type;
                Require(GameManager.Get_Friendship[index] == StartFriendship && !GameManager.Get_Friendship_TalkedToday[index],
                    "the distant guest request was not rejected without local mutation.");
                if (!ericMilestone) actor.gameObject.SetActive(originalActive);
                RestorePlayer();
                plugin.EndPairedTestSession();
                Require(plugin.World == null, "guest NPC subclass test session did not close.");
                Require(npcs.TestOriginalStateRestored,
                    "disconnect did not restore the guest's pre-session friendship/day arrays.");
                return true;
            }
            return false;
        }

        private void RestoreHostFixture()
        {
            if (actor == null) return;
            int index = (int)actor.actor_Type;
            GameManager.Get_Friendship[index] = originalFriendship;
            GameManager.Get_Friendship_CanBeFriendWithThisAcor[index] = originalCanBeFriend;
            GameManager.Get_Friendship_TalkedToday[index] = originalTalkedToday;
            GameManager.Get_Friendship_BoughtWeedToday[index] = originalBoughtToday;
            GameManager.Get_Friendship_SyrupGiftedToday[index] = originalGiftedToday;
            if (!ericMilestone) actor.gameObject.SetActive(originalActive);
        }

        private void Move(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = position;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private void RestorePlayer()
        {
            if (!hasOriginalPlayerPosition) return;
            gameplay.PlayerWalking.transform.position = originalPlayerPosition;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = originalPlayerPosition;
            gameplay.Get_Player_Rigidbody.velocity = originalPlayerVelocity;
        }

        private static bool OverheadMatches(Actor actor, string greeting)
        {
            ActorOverHeadText overhead = actor.actorOverHeadText;
            if (overhead == null || !overhead.gameObject.activeSelf) return false;
            var field = typeof(ActorOverHeadText).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic);
            object? view = field?.GetValue(overhead);
            var text = view?.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            return text?.GetValue(view) as string == "<mark=#00000044>" + greeting + "</mark>";
        }

        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("NPC subclass test: " + reason); }
    }
}
