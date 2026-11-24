using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Source-grounded sale acceptance: a guest's carried big pre-roll is rejected
    // at range, then sold to a source-reviewed plain buyer activated by the
    // game's own distance controller, through native Actor.OnTriggerEnter.
    // The runner uses copies and preserves the buyer's daily-sale eligibility.
    internal sealed class PairedNpcWeedSaleTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedNpcInteractions npcs;
        private Actor? actor;
        private ulong actorId, packEntity;
        private ushort packPrefab;
        private uint peer;
        private int phase;
        private float deadline, next;
        private bool playerStaged, packMoved;
        private Vector3 originalPlayerPosition, originalPlayerVelocity, fixtureActorPosition, salePackPosition;
        private Quaternion originalPlayerRotation;
        private NpcInteractionState? originalNpcState;
        private bool originalWantsBig;
        private int guestStartingCash, startingFriendship;
        private string candidateSnapshot = "not inspected";
        private string activationDetails = "not selected";
        private float guestActivationWait, grabRetryAt, nearPackDeadline;
        private bool nearPackStaged;
        private const float PackScale = 0.826f;
        private const int BigPackPayoutCents = 50000; // Actor.BuyThisPack pays 500 for a big pack
        private const float InitialPackOffset = 8f;
        private const float ActivationSafetyMargin = InitialPackOffset + 4f;

        internal string Status => "NPC weed sale phase=" + phase + "; actor=" + (actor == null ? "none" : actor.GetType().Name) +
            "; actor/player=" + (actor == null ? "none" : actor.transform.position.ToString("F1") + "/" + gameplay.PlayerWalking.transform.position.ToString("F1")) +
            "; playerStaged=" + playerStaged +
            "; live=actorActive:" + (actor != null && actor.gameObject.activeInHierarchy) + ",safe:" + (actor != null && npcs.TestIsSafeWeedSaleActor(actor)) +
            (plugin.TestOptions.IsHost && actor != null && peer != 0 ? ",peerNear:" + npcs.TestPeerNear(peer, actor.transform.position, 4.0f) : "") +
            ",controllers:" + (actor == null ? "none" : npcs.TestDistanceControllerSummary(actor)) +
            "; activation=" + activationDetails +
            "; bought=" + (actor != null && npcs.TestBoughtWeedToday(actor)) +
            "; eligibility=" + (actor == null ? "no-buyer" : npcs.TestWeedSaleFixtureDailyEligibility(actor)) +
            "; sale=" + npcs.TestLastWeedSaleResult +
            "; pack=" + packEntity + "; event=" + npcs.TestCurrentState.EventSequence +
            "; candidates=" + (actor == null ? npcs.TestWeedSaleCandidateSummary : candidateSnapshot);
        internal string BuyerType => actor == null ? "unknown" : actor.GetType().Name;

        internal PairedNpcWeedSaleTest(MonMPPlugin plugin, Gameplay gameplay)
        { this.plugin = plugin; this.gameplay = gameplay; npcs = plugin.World!.NpcInteractions; }

        internal bool Tick()
        {
            if (plugin.Session?.IsReady != true || plugin.World?.Channel.FullySynchronized != true) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 180f;
            try
            {
                if (Time.unscaledTime > deadline) throw new InvalidOperationException("NPC weed-sale test stalled: " + Status);
                if (Time.unscaledTime < next) return false;
                return plugin.TestOptions.IsHost ? Host() : Guest();
            }
            catch
            {
                if (plugin.TestOptions.IsHost) CleanupHostFixture();
                else
                {
                    RestoreLocalPlayer();
                    if (plugin.World != null) plugin.EndPairedTestSession();
                }
                throw;
            }
        }

        private bool Host()
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                candidateSnapshot = npcs.TestWeedSaleCandidateSummary;
                Debug.Log("MonMP NPC weed-sale candidate catalog (host): " + candidateSnapshot);
                string storeGate = npcs.TestOpenStoresGatingWeedSaleBuyers();
                actor = npcs.TestFindWeedSaleFixtureActor(gameplay.Player.transform, ActivationSafetyMargin, out activationDetails);
                activationDetails = "storeGate=" + storeGate + "; " + activationDetails;
                Require(actor != null, "no active buyer or source-backed proximity activation is available; " + activationDetails + "; " + candidateSnapshot);
                Actor stagedActor = actor!;
                actorId = npcs.TestActorId(stagedActor);
                Debug.Log("MonMP NPC weed-sale selected buyer (host): id=" + actorId + "; type=" + stagedActor.GetType().Name + "; " + activationDetails);
                Require(actorId != 0, "the reviewed buyer has no shared actor identity.");
                Require(npcs.TestWeedSaleFixtureDailyEligibility(stagedActor) == "eligible",
                    "the reviewed buyer is not currently eligible for a native sale: " + npcs.TestWeedSaleFixtureDailyEligibility(stagedActor));
                originalNpcState = npcs.TestCurrentState;
                originalWantsBig = npcs.TestWantBigWeedPack(stagedActor);
                startingFriendship = GameManager.Get_Friendship[(int)stagedActor.actor_Type];
                fixtureActorPosition = stagedActor.transform.position;
                CaptureAndMovePlayer(fixtureActorPosition);
                peer = plugin.Session.Peers.Keys.First(id => id != 0);
                npcs.TestPublishWeedSaleFixtureSpeaker(stagedActor);
                phase = 1;
                next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 1)
            {
                Actor stagedActor = actor!;
                if (!npcs.TestIsSafeWeedSaleActor(stagedActor)) npcs.TestTickDistanceControllers(stagedActor);
                if (!npcs.TestIsSafeWeedSaleActor(stagedActor) || !npcs.TestPeerNear(peer, stagedActor.transform.position, 4.0f))
                { next = Time.unscaledTime + 0.5f; return false; }
                npcs.TestSetWantBigWeedPack(stagedActor, true);
                packPrefab = plugin.World!.Items.TestTypePrefab(typeof(Item_WeedPreRollBigPack));
                InteractableItems pack = plugin.World.Items.TestSpawn(fixtureActorPosition + Vector3.right * InitialPackOffset, packPrefab);
                pack.isSpawnFromTheSaveLoadSystem = true;
                pack.transform.localScale = Vector3.one * PackScale;
                pack.CreateBasics();
                packEntity = plugin.World.Items.EnsureHostBinding(pack);
                Require(packEntity != 0, "host could not register the disposable pre-roll sale fixture.");
                plugin.World.Items.PublishHostItem(packEntity);
                phase = 2;
            }
            else if (phase == 2 && npcs.TestLastWeedSaleResult == NpcWeedSaleResult.OutOfRange)
            {
                int index = (int)actor!.actor_Type;
                Require(!npcs.TestBoughtWeedToday(actor) && GameManager.Get_Friendship[index] == startingFriendship &&
                    plugin.World!.Items.StateOf(packEntity)?.Exists == true,
                    "the distant guest sale changed the buyer or consumed the pack.");
                npcs.TestResetLastWeedSaleResult();
                phase = 3;
            }
            else if (phase == 2 && npcs.TestLastWeedSaleResult.HasValue)
                throw new InvalidOperationException("NPC weed-sale activation convergence failed; expected host OutOfRange, got " + npcs.TestLastWeedSaleResult + "; " + Status);
            else if (phase == 3 && npcs.TestLastWeedSaleResult == NpcWeedSaleResult.Applied &&
                npcs.TestBoughtWeedToday(actor!) && npcs.TestCurrentState.BoughtWeedToday[(int)actor!.actor_Type] &&
                plugin.World!.Items.StateOf(packEntity)?.Exists == false)
            {
                phase = 4;
            }
            else if (phase == 3 && npcs.TestLastWeedSaleResult == NpcWeedSaleResult.OutOfRange)
                // The staged pack can re-enter the buyer trigger natively before the near attempt; those
                // distant contacts are refused again and do not decide the near sale.
                npcs.TestResetLastWeedSaleResult();
            else if (phase == 3 && npcs.TestLastWeedSaleResult == NpcWeedSaleResult.Applied)
                return false; // the removal and daily state converge over the next frames
            else if (phase == 3 && npcs.TestLastWeedSaleResult.HasValue)
                throw new InvalidOperationException("NPC weed-sale fixture: host rejected the near sale: " + Status);
            else if (phase == 4 && plugin.Session!.Peers.Count == 1)
            {
                CleanupHostFixture();
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                NpcInteractionState state = npcs.TestCurrentState;
                if (state.EventSequence == 0 || state.SpeakerId == 0 || state.Greeting.Length != 0) return false;
                actorId = state.SpeakerId; actor = npcs.TestActorById(actorId);
                Require(actor != null, "the host-selected buyer has no local actor counterpart.");
                int actorIndex = (int)actor!.actor_Type;
                Require(actorIndex > 0 && actorIndex < state.Friendship.Length &&
                    actorIndex < state.BoughtWeedToday.Length && !state.BoughtWeedToday[actorIndex] &&
                    state.Friendship[actorIndex] >= actor.MinimumFriendshipToBuyWeed,
                    "the replicated buyer state is not eligible for a native sale; friendship=" +
                    (actorIndex > 0 && actorIndex < state.Friendship.Length ? state.Friendship[actorIndex].ToString() : "invalid") +
                    "; boughtToday=" + (actorIndex > 0 && actorIndex < state.BoughtWeedToday.Length ? state.BoughtWeedToday[actorIndex].ToString() : "invalid") +
                    "; needsSleep=" + actor.NeedToSleepToSellWeedAgain);
                startingFriendship = state.Friendship[actorIndex];
                candidateSnapshot = npcs.TestWeedSaleCandidateSummary;
                Debug.Log("MonMP NPC weed-sale candidate catalog (guest): " + candidateSnapshot);
                Vector3 activationAnchor = default;
                bool canActivate = actor != null && npcs.TestCanActivateWeedSaleActor(actor, gameplay.Player.transform,
                    ActivationSafetyMargin, out activationAnchor, out activationDetails);
                // The host may have just opened the buyer's store; its presence record arrives within a second or two.
                if (!canActivate && guestActivationWait == 0) guestActivationWait = Time.unscaledTime + 6f;
                if (!canActivate && Time.unscaledTime < guestActivationWait) { next = Time.unscaledTime + 0.5f; return false; }
                Require(canActivate,
                    "the host-selected buyer is absent or has no source-backed local proximity activation; " + activationDetails + "; " + candidateSnapshot);
                Debug.Log("MonMP NPC weed-sale selected buyer (guest): id=" + actorId + "; type=" + actor!.GetType().Name + "; " + activationDetails);
                fixtureActorPosition = actor!.transform.position;
                CaptureAndMovePlayer(activationAnchor);
                phase = 1;
                next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 1)
            {
                Require(actor != null, "the selected buyer disappeared while waiting for native activation.");
                if (!npcs.TestIsSafeWeedSaleActor(actor!)) npcs.TestTickDistanceControllers(actor!);
                if (!npcs.TestIsSafeWeedSaleActor(actor!))
                { next = Time.unscaledTime + 0.5f; return false; }
                NpcInteractionState state = npcs.TestCurrentState;
                if (npcs.TestWeedSaleFixtureDailyEligibility(actor!) != "eligible")
                    throw new InvalidOperationException("NPC weed-sale fixture: native buyer eligibility changed while activating: " +
                        npcs.TestWeedSaleFixtureDailyEligibility(actor!) + "; " + candidateSnapshot);
                packPrefab = plugin.World!.Items.TestTypePrefab(typeof(Item_WeedPreRollBigPack));
                packEntity = plugin.World.Items.TestTaggedItem(packPrefab, PackScale);
                if (packEntity == 0 || plugin.World.Items.StateOf(packEntity)?.Exists != true) return false;
                int index = (int)actor!.actor_Type;
                if (state.Friendship[index] != startingFriendship || state.BoughtWeedToday[index]) return false;
                InteractableItems? pack = plugin.World.Items.ItemOf(packEntity);
                if (pack == null) return false;
                Physics.SyncTransforms();
                if (!TryFindOverlappingPackPosition(actor, pack, out salePackPosition, out string overlapDetails))
                {
                    throw new InvalidOperationException("NPC weed-sale test: no collider pair can satisfy the production overlap check; " + overlapDetails + "; " + candidateSnapshot);
                }
                guestStartingCash = plugin.World.Economy.TestState.CashCents;
                // The host judges the grab against this player's replicated pose, so request it
                // only after the move has had time to arrive.
                Move(pack.transform.position);
                phase = 2; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 2)
            {
                var items = plugin.World!.Items;
                var pack = items.ItemOf(packEntity);
                if (pack == null) return false;
                if (items.StateOf(packEntity)?.Lease.Owner != plugin.Session!.LocalPeerId)
                {
                    if (Time.unscaledTime >= grabRetryAt && !gameplay.Player.IsHoldingAnObject)
                    { items.TestGrab(packEntity); grabRetryAt = Time.unscaledTime + 2f; }
                    next = Time.unscaledTime + 0.2f;
                    return false;
                }
                if (!packMoved)
                {
                    pack.transform.position = salePackPosition;
                    packMoved = true;
                    next = Time.unscaledTime + 0.7f;
                    return false;
                }
                npcs.TestResetLastWeedSaleResult();
                npcs.TestTriggerNativeWeedSale(actor!, pack.itemCollider);
                phase = 3;
            }
            else if (phase == 3 && npcs.TestLastWeedSaleResult == NpcWeedSaleResult.OutOfRange)
            {
                int index = (int)actor!.actor_Type;
                Require(!npcs.TestBoughtWeedToday(actor) && GameManager.Get_Friendship[index] == startingFriendship &&
                    plugin.World!.Items.StateOf(packEntity)?.Exists == true,
                    "the guest applied the rejected distant sale locally.");
                Move(fixtureActorPosition + Vector3.right * 1.5f);
                next = Time.unscaledTime + 0.7f;
                phase = 4;
            }
            else if (phase == 3 && npcs.TestLastWeedSaleResult.HasValue)
                throw new InvalidOperationException("NPC weed-sale activation convergence failed; expected OutOfRange, got " + npcs.TestLastWeedSaleResult + "; " + Status);
            else if (phase == 4)
            {
                var items = plugin.World!.Items;
                var pack = items.ItemOf(packEntity);
                if (pack == null || items.StateOf(packEntity)?.Lease.Owner != plugin.Session!.LocalPeerId) return false;
                // Hold the pack inside the buyer again, as the player would, and sell only once the
                // host has echoed that pose; its overlap check reads the host's copy.
                if (!nearPackStaged)
                {
                    Physics.SyncTransforms();
                    Require(TryFindOverlappingPackPosition(actor!, pack, out salePackPosition, out string overlapDetails),
                        "no collider pair can satisfy the near-sale overlap check; " + overlapDetails);
                    pack.transform.position = salePackPosition;
                    nearPackDeadline = Time.unscaledTime + 5f;
                    nearPackStaged = true;
                    next = Time.unscaledTime + 0.1f;
                    return false;
                }
                var echoed = items.StateOf(packEntity);
                Require(Time.unscaledTime <= nearPackDeadline, "host did not echo the staged near-sale pack pose before timeout.");
                if (echoed == null || Vector3.Distance(new Vector3(echoed.Pose.X, echoed.Pose.Y, echoed.Pose.Z), salePackPosition) > 0.15f) return false;
                npcs.TestResetLastWeedSaleResult();
                npcs.TestTriggerNativeWeedSale(actor!, pack.itemCollider);
                phase = 5;
            }
            else if (phase == 5 && npcs.TestLastWeedSaleResult == NpcWeedSaleResult.Applied)
            {
                int index = (int)actor!.actor_Type;
                if (!npcs.TestBoughtWeedToday(actor) || !npcs.TestCurrentState.BoughtWeedToday[index] ||
                    plugin.World!.Items.StateOf(packEntity)?.Exists != false ||
                    plugin.World.Economy.TestState.CashCents != guestStartingCash + BigPackPayoutCents) return false;
                RestoreLocalPlayer();
                plugin.EndPairedTestSession();
                Require(npcs.TestOriginalStateRestored && plugin.World == null,
                    "guest disconnect did not restore its original NPC state.");
                return true;
            }
            else if (phase == 5 && npcs.TestLastWeedSaleResult == NpcWeedSaleResult.OutOfRange && Time.unscaledTime < nearPackDeadline + 5f)
                npcs.TestResetLastWeedSaleResult();
            else if (phase == 5 && npcs.TestLastWeedSaleResult.HasValue)
                throw new InvalidOperationException("NPC weed-sale test: host rejected the near eligible sale: " + Status);
            return false;
        }

        private void CleanupHostFixture()
        {
            if (actor != null && originalNpcState != null) npcs.TestRestoreWantBigWeedPack(actor, originalWantsBig);
            if (originalNpcState != null) npcs.TestRestoreFixtureState(originalNpcState);
            npcs.TestRestoreFixtureStores();
            RestoreLocalPlayer();
        }

        private void CaptureAndMovePlayer(Vector3 position)
        {
            Require(gameplay.Get_PlayerState == PlayerState.Walking,
                "player-proximity fixture requires a walking player so it can restore position safely.");
            if (!playerStaged)
            {
                originalPlayerPosition = gameplay.PlayerWalking.transform.position;
                originalPlayerRotation = gameplay.PlayerWalking.transform.rotation;
                originalPlayerVelocity = gameplay.Get_Player_Rigidbody == null ? Vector3.zero : gameplay.Get_Player_Rigidbody.velocity;
                playerStaged = true;
            }
            Move(position);
        }

        private void RestoreLocalPlayer()
        {
            if (!playerStaged) return;
            playerStaged = false;
            Move(originalPlayerPosition);
            gameplay.PlayerWalking.transform.rotation = originalPlayerRotation;
            if (gameplay.Get_Player_Rigidbody != null) gameplay.Get_Player_Rigidbody.velocity = originalPlayerVelocity;
        }

        private static bool TryFindOverlappingPackPosition(Actor actor, InteractableItems pack,
            out Vector3 position, out string details)
        {
            position = default;
            Collider? packCollider = pack.itemCollider;
            if (packCollider == null)
            { details = "pack collider is missing"; return false; }

            Collider[] actorColliders = actor.GetComponentsInChildren<Collider>(true);
            string candidates = string.Join(",", actorColliders.Select(collider => collider == null ? "null" :
                collider.name + "/" + collider.GetType().Name + ":enabled=" + collider.enabled +
                ":active=" + collider.gameObject.activeInHierarchy + ":trigger=" + collider.isTrigger +
                ":bounds=" + collider.bounds.ToString()));
            Vector3 packBoundsOffset = packCollider.bounds.center - pack.transform.position;
            foreach (Collider actorCollider in actorColliders
                .Where(collider => collider != null && collider.enabled && collider.gameObject.activeInHierarchy)
                // Dealers carry only their sale trigger, which is what the native OnTriggerEnter uses.
                .OrderBy(collider => collider.isTrigger)
                .ThenByDescending(collider => collider.bounds.size.x * collider.bounds.size.y * collider.bounds.size.z))
            {
                Vector3 target = actorCollider.bounds.center - packBoundsOffset;
                Vector3 delta = target - pack.transform.position;
                Bounds targetBounds = packCollider.bounds;
                targetBounds.center += delta;
                if (!targetBounds.Intersects(actorCollider.bounds)) continue;
                if (!Physics.ComputePenetration(packCollider, packCollider.transform.position + delta, packCollider.transform.rotation,
                    actorCollider, actorCollider.transform.position, actorCollider.transform.rotation, out _, out _)) continue;
                position = target;
                details = "overlap=" + actorCollider.name + "/" + actorCollider.GetType().Name +
                    "; pack=" + packCollider.name + "/" + packCollider.GetType().Name +
                    "; position=" + position.ToString("F2");
                return true;
            }

            details = "pack=" + packCollider.name + "/" + packCollider.GetType().Name +
                ":bounds=" + packCollider.bounds.ToString() + "; actorColliders=" + candidates;
            return false;
        }

        private void Move(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("NPC weed-sale test: " + reason); }
    }
}
