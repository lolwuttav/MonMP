using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UniStorm;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Runs against a real scene Actor and patched native talk/gift methods. The
    // host publishes a fixture event marker and known baselines so the guest can
    // stage its walking pose after receiving the shared NPC snapshot.
    internal sealed class PairedNpcTalkTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedNpcInteractions npcs;
        private Actor? actor;
        private ulong actorId;
        private ulong giftEntity;
        private ushort giftPrefab;
        private uint peer;
        private int phase;
        private float deadline, next;
        private bool actorStaged, giftMovedIntoActor, talkActionTested;
        private readonly bool coffeeGiftOnly;
        private int giftFriendshipBaseline = 12;
        private Vector3 originalActorLocalPosition, fixtureActorPosition, giftTargetPoint, giftTargetItemPosition;
        private Collider? giftTargetCollider;
        private float giftPoseDeadline;
        private readonly SharedClock clock;
        private readonly ClockState originalClock;
        private ClockState? hostFixtureClock;
        private bool coffeeScheduleStarted, coffeeScheduleReconciled, coffeeScheduleReady, coffeeClockRestored;
        private const float GiftScale = 0.867f;
        internal bool TalkActionTested => talkActionTested;
        internal bool CoffeeGiftOnly => coffeeGiftOnly;
        private NpcGiftKind GiftKind => coffeeGiftOnly ? NpcGiftKind.HomemadeCoffee : NpcGiftKind.MapleSyrup;
        private Type GiftItemType => coffeeGiftOnly ? typeof(Item_HomemadeCoffeeMug) : typeof(Item_MapleSyrupCan);
        private string GiftName => coffeeGiftOnly ? "Carole coffee" : "maple syrup";
        internal string Status => "NPC gift/talk phase=" + phase + "; gift=" + GiftName + "; actor=" + (actor == null ? "none" : actor.GetType().Name) +
            "; actor/player=" + (actor == null ? "none" : actor.transform.position.ToString("F1") + "/" + gameplay.PlayerWalking.transform.position.ToString("F1")) +
            "; fixtureActor=" + (actorStaged ? fixtureActorPosition.ToString("F1") : "none") +
            "; hostPeerRange=" + (plugin.TestOptions.IsHost && actor != null && peer != 0 ? npcs.TestPeerActorDistance(peer, actor) : "n/a") +
            "; friendship=" + (actor == null ? -1 : GameManager.Get_Friendship[(int)actor.actor_Type]) +
            "; talked=" + (actor != null && GameManager.Get_Friendship_TalkedToday[(int)actor.actor_Type]) +
            "; gifted=" + (actor != null && npcs.TestGiftedToday(actor)) +
            "; talkAction=" + talkActionTested + "; giftResult=" + npcs.TestLastGiftResult + "; result=" + npcs.TestLastResult +
            "; giftEntity=" + giftEntity + "; giftCandidates=" + (actor == null ? npcs.TestGiftCandidateSummaryFor(GiftKind) : "selected") +
            "; giftTarget=" + (giftTargetCollider == null ? "none" : giftTargetCollider.GetType().Name + "/" + giftTargetCollider.name +
                "@" + giftTargetPoint.ToString("F2") + "+/-" + giftTargetCollider.bounds.extents.ToString("F2")) +
            "; giftItemTarget=" + (giftMovedIntoActor ? giftTargetItemPosition.ToString("F2") : "none") +
            "; giftPoseEcho=" + GiftPoseEchoStatus + "; giftGate=" + npcs.TestLastGiftDiagnostics +
            "; coffeeSchedule=" + (coffeeGiftOnly ? (coffeeScheduleReady ? "ready" : coffeeScheduleStarted ? "waiting-for-bar" : "pending") +
                "/reconciled=" + coffeeScheduleReconciled +
                "/nativeMinute=" + clock.TestNative.MinuteOfDay + "/restoreMinute=" +
                (plugin.TestOptions.IsHost ? (hostFixtureClock?.MinuteOfDay.ToString() ?? "not-captured") : originalClock.MinuteOfDay.ToString()) : "n/a") +
            "; greeting=" + (npcs.TestCurrentState.Greeting ?? "");

        internal PairedNpcTalkTest(MonMPPlugin plugin, Gameplay gameplay, bool coffeeGiftOnly = false)
        {
            this.plugin = plugin; this.gameplay = gameplay; this.coffeeGiftOnly = coffeeGiftOnly;
            npcs = plugin.World!.NpcInteractions;
            clock = plugin.World.Clock;
            originalClock = clock.TestOriginal;
        }

        internal bool Tick()
        {
            if (plugin.Session?.IsReady != true || plugin.World?.Channel.FullySynchronized != true) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 120;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("NPC gift/talk test stalled: " + Status);
            if (actorStaged && actor != null) actor.transform.position = fixtureActorPosition;
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                if (coffeeGiftOnly && !EnsureCoffeeBarSchedule(hostSide: true)) return false;
                actor = npcs.TestFindSafeGiftActor(GiftKind);
                Require(actor != null, coffeeGiftOnly
                    ? "no active, non-route Actor_Carole has the reviewed coffee gift path; " + npcs.TestGiftCandidateSummaryFor(GiftKind)
                    : "no active, non-route Actor has the reviewed base syrup gift path; " + npcs.TestGiftCandidateSummaryFor(GiftKind));
                StageActor();
                actorId = npcs.TestActorId(actor!);
                Require(actorId != 0, "selected Actor has no shared identity.");
                talkActionTested = !coffeeGiftOnly && npcs.TestIsSafeActor(actor!);
                peer = plugin.Session.Peers.Keys.First(id => id != 0);
                npcs.TestResetLastResult();
                npcs.TestSetFixtureState(actor!, 12, talkedToday: false, markIdentity: true);
                var items = plugin.World!.Items;
                giftPrefab = items.TestTypePrefab(GiftItemType);
                var gift = items.TestSpawn(fixtureActorPosition + Vector3.right * 3.3f, giftPrefab);
                gift.isSpawnFromTheSaveLoadSystem = true;
                gift.transform.localScale = Vector3.one * GiftScale;
                gift.CreateBasics();
                giftEntity = items.EnsureHostBinding(gift);
                Require(giftEntity != 0, "host could not register the disposable " + GiftName + " fixture item.");
                items.PublishHostItem(giftEntity);
                phase = 1;
            }
            else if (phase == 1 && npcs.TestPeerNear(peer, actor!.transform.position, 4f))
            {
                // The guest moves near only after applying the host's fixture marker
                // and baseline, preventing a stale local save from starting either action.
                phase = talkActionTested ? 2 : 3;
            }
            else if (phase == 2 && npcs.TestLastResult == NpcTalkResult.Applied)
            {
                var state = npcs.TestCurrentState;
                int index = (int)actor!.actor_Type;
                Require(GameManager.Get_Friendship[index] == 13 && GameManager.Get_Friendship_TalkedToday[index],
                    "the host did not apply one native friendship increase and daily talk flag.");
                Require(state.SpeakerId == actorId && state.EventSequence > 0 && !string.IsNullOrEmpty(state.Greeting),
                    "the host did not capture the native localized greeting.");
                Require(OverheadMatches(actor!, state.Greeting), "the host native Actor did not display its captured greeting.");
                npcs.TestSetFixtureState(actor!, 13, talkedToday: false);
                giftFriendshipBaseline = 13;
                npcs.TestResetLastResult();
                npcs.TestResetLastGiftResult();
                phase = 3;
            }
            else if (phase == 3 && npcs.TestLastGiftResult == NpcGiftResult.Applied &&
                plugin.World!.Items.StateOf(giftEntity)?.Exists == false)
            {
                int index = (int)actor!.actor_Type;
                Require(GameManager.Get_Friendship[index] == giftFriendshipBaseline + 1 &&
                    !GameManager.Get_Friendship_TalkedToday[index] && npcs.TestGiftedToday(actor),
                    "host did not apply exactly one native " + GiftName + " gift and daily gift flag.");
                if (coffeeGiftOnly)
                {
                    var state = npcs.TestCurrentState;
                    Require(state.SpeakerId == actorId && state.EventSequence > 0 &&
                        IsLatestCaroleMoreGreeting(state.Greeting),
                        "host did not capture Carole's source-specific coffee greeting.");
                    Require(OverheadMatches(actor!, state.Greeting),
                        "host did not display the captured Carole coffee greeting.");
                }
                phase = 4;
            }
            else if (phase == 4 && !talkActionTested && plugin.Session!.Peers.Count == 1)
            {
                RestoreActor();
                if (coffeeGiftOnly) RestoreHostCoffeeClock();
                return true;
            }
            else if (phase == 4 && talkActionTested && npcs.TestLastResult == NpcTalkResult.OutOfRange)
            {
                int index = (int)actor!.actor_Type;
                Require(GameManager.Get_Friendship[index] == giftFriendshipBaseline + 1 && !GameManager.Get_Friendship_TalkedToday[index] &&
                    npcs.TestGiftedToday(actor), "the distant guest request changed host NPC state after the gift.");
                if (plugin.Session!.Peers.Count == 1) { RestoreActor(); return true; }
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                if (coffeeGiftOnly && !EnsureCoffeeBarSchedule(hostSide: false)) return false;
                var state = npcs.TestCurrentState;
                if (state.EventSequence == 0 || state.SpeakerId == 0 || state.Greeting.Length != 0) return false;
                actorId = state.SpeakerId;
                actor = npcs.TestActorById(actorId);
                Require(actor != null && npcs.TestIsSafeGiftActor(actor, GiftKind) &&
                    npcs.TestActorId(actor) == actorId, "the host's staged Actor identity is absent or gift-ineligible on the guest.");
                StageActor();
                Require(actorId != 0, "selected Actor has no shared identity.");
                talkActionTested = !coffeeGiftOnly && npcs.TestIsSafeActor(actor!);
                int index = (int)actor!.actor_Type;
                if (state.Friendship[index] != 12 || state.TalkedToday[index]) return false;
                Move(actor.transform.position + Vector3.right * 1.5f);
                phase = talkActionTested ? 1 : 2; next = Time.unscaledTime + 0.6f;
            }
            else if (phase == 1)
            {
                int index = (int)actor!.actor_Type;
                if (GameManager.Get_Friendship[index] != 12 || GameManager.Get_Friendship_TalkedToday[index]) return false;
                npcs.TestSpitNativeActor(actor);
                Require(GameManager.Get_Friendship[index] == 12 && !GameManager.Get_Friendship_TalkedToday[index],
                    "guest PlayerSpitOn ran native friendship/day side effects.");
                npcs.TestResetLastResult();
                npcs.TestClickNativeActor(actor);
                Require(GameManager.Get_Friendship[index] == 12 && !GameManager.Get_Friendship_TalkedToday[index],
                    "guest native Actor.MouseDown changed friendship/day state before host approval.");
                phase = 2;
            }
            else if (phase == 2)
            {
                int index = (int)actor!.actor_Type;
                var state = npcs.TestCurrentState;
                if (talkActionTested)
                {
                    if (GameManager.Get_Friendship[index] != 13 || npcs.TestLastResult != NpcTalkResult.Applied ||
                        state.SpeakerId != actorId || state.EventSequence == 0 || string.IsNullOrEmpty(state.Greeting)) return false;
                    Require(OverheadMatches(actor, state.Greeting), "the guest did not render the host's exact localized overhead greeting.");
                    if (GameManager.Get_Friendship_TalkedToday[index] || state.Friendship[index] != 13 || state.TalkedToday[index]) return false;
                    giftFriendshipBaseline = 13;
                }
                else if (GameManager.Get_Friendship[index] != 12 || GameManager.Get_Friendship_TalkedToday[index] ||
                    state.SpeakerId != actorId || state.Friendship[index] != 12 || state.TalkedToday[index]) return false;
                if (state.GiftedToday[index]) return false;
                var items = plugin.World!.Items;
                giftPrefab = items.TestTypePrefab(GiftItemType);
                giftEntity = items.TestTaggedItem(giftPrefab, GiftScale);
                if (giftEntity == 0 || items.StateOf(giftEntity)?.Exists != true) return false;
                Move(actor.transform.position + Vector3.right * 1.5f);
                items.TestGrab(giftEntity);
                phase = 3; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 3)
            {
                var items = plugin.World!.Items;
                var state = items.StateOf(giftEntity);
                if (state == null || state.Lease.Owner != plugin.Session!.LocalPeerId) return false;
                InteractableItems? gift = items.ItemOf(giftEntity);
                if (gift == null) return false;
                if (!giftMovedIntoActor)
                {
                    Physics.SyncTransforms();
                    giftTargetPoint = giftTargetCollider!.bounds.center;
                    gift.transform.position += giftTargetPoint - gift.itemCollider.bounds.center;
                    Physics.SyncTransforms();
                    Require(items.HostNpcGiftItemOverlapsActor(actor!, gift, out string overlapDiagnostics),
                        "guest fixture " + GiftName + " did not overlap the selected active Actor collider: " + overlapDiagnostics);
                    giftTargetItemPosition = gift.transform.position;
                    giftPoseDeadline = Time.unscaledTime + 5f;
                    giftMovedIntoActor = true;
                    next = Time.unscaledTime + 0.1f;
                    return false;
                }
                var authoritativeItem = items.StateOf(giftEntity);
                Require(Time.unscaledTime <= giftPoseDeadline,
                    "host did not echo the " + GiftName + " pose into the actor collider before timeout; " + GiftPoseEchoStatus);
                if (authoritativeItem == null || Vector3.Distance(new Vector3(authoritativeItem.Pose.X,
                    authoritativeItem.Pose.Y, authoritativeItem.Pose.Z), giftTargetItemPosition) > 0.15f) return false;
                Physics.SyncTransforms();
                Require(items.HostNpcGiftItemOverlapsActor(actor!, gift, out string echoedOverlapDiagnostics),
                    "guest gift item no longer overlaps the selected Actor collider after the host pose echo: " + echoedOverlapDiagnostics);
                int index = (int)actor!.actor_Type;
                Require(GameManager.Get_Friendship[index] == giftFriendshipBaseline && !npcs.TestGiftedToday(actor),
                    "guest applied friendship or daily gift state before host approval.");
                npcs.TestResetLastGiftResult();
                npcs.TestTriggerNativeGift(actor, gift.itemCollider);
                Require(GameManager.Get_Friendship[index] == giftFriendshipBaseline && !npcs.TestGiftedToday(actor),
                    "guest native Actor.OnTriggerEnter ran gift side effects before host approval.");
                phase = 4;
            }
            else if (phase == 4 && npcs.TestLastGiftResult == NpcGiftResult.Applied)
            {
                int index = (int)actor!.actor_Type;
                if (GameManager.Get_Friendship[index] != giftFriendshipBaseline + 1 || GameManager.Get_Friendship_TalkedToday[index] ||
                    !npcs.TestGiftedToday(actor)) return false;
                if (plugin.World!.Items.StateOf(giftEntity)?.Exists != false) return false;
                if (coffeeGiftOnly)
                {
                    var state = npcs.TestCurrentState;
                    Require(state.SpeakerId == actorId && state.EventSequence > 0 &&
                        !string.IsNullOrEmpty(state.Greeting),
                        "guest did not receive Carole's source-specific coffee greeting.");
                    Require(OverheadMatches(actor!, state.Greeting),
                        "guest did not display the host's exact Carole coffee greeting.");
                }
                if (!talkActionTested)
                {
                    RestoreActor();
                    plugin.EndPairedTestSession();
                    Require(plugin.World == null, "guest NPC gift test session did not close.");
                    Require(clock.TestRestored && clock.TestNative.Encode().SequenceEqual(originalClock.Encode()),
                        "disconnect did not restore the guest's original clock/day state.");
                    Require(npcs.TestOriginalStateRestored, "disconnect did not restore the guest's pre-session friendship/day arrays.");
                    return true;
                }
                Move(actor.transform.position + Vector3.right * 30f);
                phase = 5; next = Time.unscaledTime + 0.6f;
            }
            else if (phase == 4 && npcs.TestLastGiftResult.HasValue)
            {
                throw new InvalidOperationException("NPC gift/talk test: host rejected the staged native " + GiftName + " gift: " +
                    npcs.TestLastGiftResult.Value + "; " + Status);
            }
            else if (phase == 5)
            {
                int index = (int)actor!.actor_Type;
                if (GameManager.Get_Friendship[index] != giftFriendshipBaseline + 1 || GameManager.Get_Friendship_TalkedToday[index] ||
                    !npcs.TestGiftedToday(actor)) return false;
                npcs.TestSpitNativeActor(actor);
                Require(GameManager.Get_Friendship[index] == giftFriendshipBaseline + 1 && !GameManager.Get_Friendship_TalkedToday[index] &&
                    npcs.TestGiftedToday(actor), "guest PlayerSpitOn ran native NPC mutations after synchronization.");
                npcs.TestResetLastResult();
                npcs.TestClickNativeActor(actor);
                Require(GameManager.Get_Friendship[index] == giftFriendshipBaseline + 1 && !GameManager.Get_Friendship_TalkedToday[index] &&
                    npcs.TestGiftedToday(actor), "distant guest native click ran local NPC mutations.");
                phase = 6;
            }
            else if (phase == 6 && npcs.TestLastResult == NpcTalkResult.OutOfRange)
            {
                int index = (int)actor!.actor_Type;
                Require(GameManager.Get_Friendship[index] == giftFriendshipBaseline + 1 && !GameManager.Get_Friendship_TalkedToday[index] &&
                    npcs.TestGiftedToday(actor), "distant guest request was not rejected without local mutation.");
                RestoreActor();
                plugin.EndPairedTestSession();
                Require(plugin.World == null, "guest NPC test session did not close.");
                Require(npcs.TestOriginalStateRestored, "disconnect did not restore the guest's pre-session friendship/day arrays.");
                return true;
            }
            return false;
        }

        private void Move(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = position;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private bool EnsureCoffeeBarSchedule(bool hostSide)
        {
            if (!coffeeGiftOnly) return true;
            if (coffeeScheduleReady) return true;
            TickSystem? ticks = UnityEngine.Object.FindObjectOfType<TickSystem>();
            StoreManagers? stores = UnityEngine.Object.FindObjectOfType<StoreManagers>();
            UniStormSystem? sky = UnityEngine.Object.FindObjectOfType<UniStormSystem>();
            UniStormManager? uniStorm = UnityEngine.Object.FindObjectOfType<UniStormManager>();
            Require(ticks != null && stores != null && sky != null && uniStorm != null,
                "native clock or StoreManagers scheduler is unavailable for the bar schedule fixture.");

            if (hostSide && !coffeeScheduleStarted)
            {
                // Preserve the host's exact pre-fixture clock; the guest's SharedClock
                // already owns and restores its exact pre-session native clock.
                hostFixtureClock = clock.TestNative;
                coffeeScheduleStarted = true;
            }

            const int barOpenMinute = 18 * 60 + 1;
            if (hostSide && ticks!.Get_Tick < barOpenMinute)
            {
                // Reconstructed UniStormManager.SetTime sets the native date-day and
                // minute fraction. StoreManagers.Tick below performs the normal
                // schedule open/close pass; the fixture never opens a store or actor.
                uniStorm!.SetTime(sky!.Day, 18, 1);
                if (ticks.Get_Tick < barOpenMinute) return false;
            }
            else if (!hostSide && ticks!.Get_Tick < barOpenMinute)
            {
                // SharedClock publishes the host's test minute; wait for that native
                // time to arrive before asking this copy's scheduler to reconcile.
                return false;
            }

            if (!coffeeScheduleReconciled)
            {
                // StoreManagers.Tick visits every enabled scene Store, so incidental
                // stock/race/letter presentation is confined to the disposable no-save fixture copies.
                stores!.Tick(ticks!.Get_Tick, ticks.Get_DayOfTheWeek);
                coffeeScheduleReconciled = true;
            }
            coffeeScheduleReady = npcs.TestFindSafeGiftActor(GiftKind) != null;
            return coffeeScheduleReady;
        }

        private void RestoreHostCoffeeClock()
        {
            if (!coffeeGiftOnly || coffeeClockRestored || hostFixtureClock == null) return;
            clock.TestRestoreFixtureClock(hostFixtureClock);
            coffeeClockRestored = true;
            Require(clock.TestNative.Encode().SequenceEqual(hostFixtureClock.Encode()),
                "host fixture did not restore its exact pre-test clock/day state.");
        }

        private void StageActor()
        {
            // Keep the eligible real scene Actor at the same disposable fixture anchor on both copies.
            originalActorLocalPosition = actor!.transform.localPosition;
            fixtureActorPosition = gameplay.PlayerWalking.transform.position + Vector3.forward * 2f;
            actorStaged = true;
            actor.transform.position = fixtureActorPosition;
            Physics.SyncTransforms();
            giftTargetCollider = actor.GetComponentsInChildren<Collider>(true)
                .FirstOrDefault(collider => collider != null && collider.enabled && collider.gameObject.activeInHierarchy);
            Require(giftTargetCollider != null, "selected Actor has no enabled, active collider for the gift fixture.");
            giftTargetPoint = giftTargetCollider!.bounds.center;
        }

        private string GiftPoseEchoStatus
        {
            get
            {
                ItemState? state = giftEntity == 0 ? null : plugin.World?.Items.StateOf(giftEntity);
                return state == null ? "missing" : state.Lease.Owner + "@" +
                    new Vector3(state.Pose.X, state.Pose.Y, state.Pose.Z).ToString("F2");
            }
        }

        private void RestoreActor()
        {
            if (!actorStaged || actor == null) return;
            actorStaged = false;
            actor.transform.localPosition = originalActorLocalPosition;
            Physics.SyncTransforms();
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

        private static bool IsLatestCaroleMoreGreeting(string greeting)
        {
            // The native getter chooses a progress-dependent random localization key,
            // appends that key to this private history list, then resolves it. Reading
            // TEXT.Actors_Carole_MoreGreetings again would choose and record a different key.
            var field = typeof(TEXT).GetField("Actors_Carole_StringUsedTonight", BindingFlags.Static | BindingFlags.NonPublic);
            var usedTonight = field?.GetValue(null) as List<string>;
            if (usedTonight == null || usedTonight.Count == 0) return false;
            string key = usedTonight[usedTonight.Count - 1];
            return key.StartsWith("Actors_Carole_", StringComparison.Ordinal) && TEXT.GetText(key) == greeting;
        }

        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("NPC gift/talk test: " + reason); }
    }
}
