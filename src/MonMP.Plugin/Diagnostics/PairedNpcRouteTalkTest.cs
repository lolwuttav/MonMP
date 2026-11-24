using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Focused coverage for the deliberately narrow route-bound talk exception.
    // Actor eligibility is evaluated through the exact production structural gate;
    // the fixture never broadens SharedNpcInteractions' accepted conversation set.
    internal sealed class PairedNpcRouteTalkTest
    {
        private sealed class RouteCandidate
        {
            internal Actor Actor = null!;
            internal readonly List<string> Controllers = new List<string>();
        }

        private sealed class Eligibility
        {
            internal bool RouteBound, SharedBinding, ProductionGate, Active, SceneValid, CanTalkToHim;
            internal bool CanTalkToHimEvaluated;
            internal bool ReversibleStartState;
            internal ulong RouteEntity;
            internal ActorRouteState? RouteState;
            internal string Error = "";
            internal bool Eligible => RouteBound && SharedBinding && ProductionGate && ReversibleStartState && SceneValid &&
                CanTalkToHimEvaluated && CanTalkToHim && string.IsNullOrEmpty(Error);
        }

        private const int FixtureFriendship = 12;
        private const float NearOffset = 1.5f;
        private const float CloseDistance = 4f;
        private const int MaximumCloseAttempts = 6;
        private static readonly FieldInfo? RouteActorField = typeof(AI_Actor_FollowRoute).GetField("actor",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo? ProductionConversationGate = typeof(SharedNpcInteractions).GetMethod(
            "IsPlainBaseConversation", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo? PublishStats = typeof(SharedNpcInteractions).GetMethod(
            "PublishStatsIfChanged", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedNpcInteractions npcs;
        private readonly SharedActorRoutes routes;
        private uint peer;
        private readonly string candidateReportPath;
        private readonly int[] originalFriendship;
        private readonly bool[] originalCanBeFriends, originalTalkedToday, originalBoughtToday, originalGiftedToday;
        private Actor? actor;
        private ulong actorId;
        private ulong routeEntityId;
        private int actorIndex = -1;
        private int phase, closeAttempts;
        private float deadline, next;
        private bool playerStaged, guestFar, routeStarted, routeStopped, hostEligibilityChecked;
        private float guestConvergedSince = -1f;
        private Vector3 originalActorPosition;
        private Quaternion originalActorRotation;
        private bool hasOriginalActorPose;
        private Vector3 originalPlayerPosition, originalRigidbodyPosition, originalRigidbodyVelocity;
        private bool hasOriginalRigidbody;
        private string candidateCatalog = "not catalogued";

        internal string Status => "NPC route talk phase=" + phase + "; actor=" + (actor == null ? "none" :
            actor.GetType().Name + "/" + actor.actor_Type + "@" + actor.transform.position.ToString("F1")) +
            "; actorId=" + actorId + "; routeEntity=" + routeEntityId + "; attempts=" + closeAttempts + "; player=" + gameplay.PlayerWalking.transform.position.ToString("F1") +
            "; peerRange=" + (plugin.TestOptions.IsHost && actor != null && peer != 0 ? npcs.TestPeerActorDistance(peer, actor) : "n/a") +
            "; friendship=" + (actorIndex < 0 ? -1 : GameManager.Get_Friendship[actorIndex]) +
            "; talked=" + (actorIndex >= 0 && GameManager.Get_Friendship_TalkedToday[actorIndex]) +
            "; result=" + npcs.TestLastResult + "; greeting=" + (npcs.TestCurrentState.Greeting ?? "");

        internal PairedNpcRouteTalkTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            npcs = plugin.World?.NpcInteractions ?? throw new InvalidOperationException("NPC interactions are not attached.");
            routes = plugin.World.ActorRoutes;
            if (ProductionConversationGate == null || RouteActorField == null || PublishStats == null)
                throw new InvalidOperationException("Native route-talk fixture reflection points are unavailable.");
            originalFriendship = (int[])GameManager.Get_Friendship.Clone();
            originalCanBeFriends = (bool[])GameManager.Get_Friendship_CanBeFriendWithThisAcor.Clone();
            originalTalkedToday = (bool[])GameManager.Get_Friendship_TalkedToday.Clone();
            originalBoughtToday = (bool[])GameManager.Get_Friendship_BoughtWeedToday.Clone();
            originalGiftedToday = (bool[])GameManager.Get_Friendship_SyrupGiftedToday.Clone();
            originalPlayerPosition = gameplay.PlayerWalking.transform.position;
            if (gameplay.Get_Player_Rigidbody != null)
            {
                hasOriginalRigidbody = true;
                originalRigidbodyPosition = gameplay.Get_Player_Rigidbody.position;
                originalRigidbodyVelocity = gameplay.Get_Player_Rigidbody.velocity;
            }
            peer = plugin.TestOptions.IsHost ? plugin.Session?.Peers.Keys.FirstOrDefault(id => id != 0) ?? 0 : 0;
            candidateReportPath = Path.Combine(Paths.BepInExRootPath, "MonMP", "diagnostics", "npc-route-talk-candidates.txt");
        }

        internal bool Tick()
        {
            if (plugin.Session?.IsReady != true || plugin.World?.Channel.FullySynchronized != true) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 120f;
            if (Time.unscaledTime > deadline)
                throw new InvalidOperationException("Route-bound NPC talk stalled: " + Status + "; candidates=" + candidateReportPath);

            if (playerStaged && actor != null)
            {
                if (plugin.TestOptions.IsHost || !guestFar) MovePlayer(actor.transform.position + Vector3.right * NearOffset);
                else MovePlayer(actor.transform.position + Vector3.right * 30f);
            }
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                if (!TrySelectEligibleRouteActor(out actor, out candidateCatalog))
                {
                    WriteCandidateCatalog("BLOCKED: no idle/inactive route-bound Actor passed the exact production structural gate, reversible start-state, and native CanTalkToHim baseline gates.");
                    throw new InvalidOperationException("No safe route-bound NPC conversation candidate was found. Production safety was left unchanged; inspect " +
                        candidateReportPath + " for every bound route actor and its failed native conversation gate.");
                }

                actorId = npcs.TestActorId(actor!);
                actorIndex = (int)actor!.actor_Type;
                if (actorId == 0 || actorIndex <= 0 || actorIndex >= originalFriendship.Length)
                    throw new InvalidOperationException("Selected route actor lacks a valid shared identity or friendship index.");
                routeEntityId = routes.TestEntityForActor(actor);
                if (routeEntityId == 0 || routes.TestState(routeEntityId) == null)
                    throw new InvalidOperationException("Selected route actor has no reversible shared route-test identity.");
                peer = plugin.Session.Peers.Keys.First(id => id != 0);
                playerStaged = true;
                MovePlayer(actor.transform.position + Vector3.right * NearOffset);
                npcs.TestResetLastResult();
                npcs.TestSetFixtureState(actor, FixtureFriendship, talkedToday: false, markIdentity: true);
                Require(ExactProductionGate(actor) && !actor.isActiveAndEnabled,
                    "selected route actor stopped passing the exact structural gate before route activation.");
                originalActorPosition = actor.transform.position;
                originalActorRotation = actor.transform.rotation;
                hasOriginalActorPose = true;
                routeStarted = true; // Covers partial activation if StartWalking throws midway.
                bool started = routes.TestStart(routeEntityId);
                ActorRouteState? routeState = routes.TestState(routeEntityId);
                Require(started && routeStarted && routeState!.Value.Active && routeState.Value.Walking && actor.isActiveAndEnabled,
                    "SharedActorRoutes.TestStart did not reversibly activate the selected route actor.");
                WriteCandidateCatalog("SELECTED: " + actor.GetType().Name + "/" + actor.actor_Type + " id=" + actorId +
                    " path=" + TransformPath(actor.transform) + " controllers=[" + string.Join(",", FindControllerPaths(actor)) +
                    "] routeEntity=" + routeEntityId + "; SharedActorRoutes.TestStart activated the route; native CanTalk and PlayerOption checks are deferred until activation and the guest must receive its active pose before clicking.");
                phase = 1;
                next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 1)
            {
                if (!hostEligibilityChecked)
                {
                    ActorRouteState? routeState = routes.TestState(routeEntityId);
                    bool sceneValid = actor!.gameObject.scene.IsValid();
                    bool active = actor.isActiveAndEnabled;
                    bool routeBound = routes.HasBinding(actor);
                    bool structuralGate = ExactProductionGate(actor);
                    bool canTalkToHim = actor.CanTalkToHim;
                    bool canTalk = actor.CanTalk();
                    bool noChoice = string.IsNullOrEmpty(actor.PlayerOption);
                    bool routeActive = routeState.HasValue && routeState.Value.Active;
                    bool routeWalking = routeState.HasValue && routeState.Value.Walking;
                    bool nativeEligible = sceneValid && active && routeBound && structuralGate && canTalkToHim && canTalk && noChoice && routeActive && routeWalking;
                    if (!nativeEligible)
                    {
                        string gates = GateSummary(sceneValid, active, routeBound, routeActive, routeWalking,
                            structuralGate, canTalkToHim, canTalk, noChoice);
                        WriteCandidateCatalog("BLOCKED: selected route actor activated, but an exact native conversation gate failed: " + gates);
                        throw new InvalidOperationException("Activated host route actor did not pass exact native conversation eligibility: " + gates);
                    }
                    hostEligibilityChecked = true;
                    WriteCandidateCatalog("SELECTED: " + actor!.GetType().Name + "/" + actor.actor_Type + " id=" + actorId +
                        " path=" + TransformPath(actor.transform) + " routeEntity=" + routeEntityId +
                        "; host activation and exact native conversation eligibility passed; guest active-pose convergence is required before click.");
                }
                if (npcs.TestPeerNear(peer, actor!.transform.position, CloseDistance)) phase = 2;
            }
            else if (phase == 2 && npcs.TestLastResult == NpcTalkResult.Applied)
            {
                NpcInteractionState state = npcs.TestCurrentState;
                Require(GameManager.Get_Friendship[actorIndex] == FixtureFriendship + 1 && GameManager.Get_Friendship_TalkedToday[actorIndex],
                    "host did not apply exactly one friendship increase and daily-talk flag.");
                Require(state.SpeakerId == actorId && state.EventSequence > 0 && !string.IsNullOrEmpty(state.Greeting),
                    "host did not capture the native route actor's localized greeting.");
                Require(OverheadMatches(actor!, state.Greeting), "host native Actor did not display its captured route-actor greeting.");
                phase = 3;
                next = Time.unscaledTime + 0.1f;
            }
            else if (phase == 3 && npcs.TestLastResult == NpcTalkResult.OutOfRange)
            {
                NpcInteractionState state = npcs.TestCurrentState;
                Require(GameManager.Get_Friendship[actorIndex] == FixtureFriendship + 1 && GameManager.Get_Friendship_TalkedToday[actorIndex] &&
                    state.Friendship[actorIndex] == FixtureFriendship + 1 && state.TalkedToday[actorIndex] && state.SpeakerId == actorId,
                    "distant request changed route-actor friendship, daily state, or greeting speaker.");
                StopRoute();
                phase = 4;
            }
            else if (phase == 4 && plugin.Session!.Peers.Count == 1)
            {
                RestoreNpcState();
                RestorePlayer();
                plugin.EndPairedTestSession();
                Require(plugin.World == null, "host route-talk test session did not close.");
                Require(npcs.TestOriginalStateRestored, "host route-talk fixture did not restore the original friendship/day arrays.");
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                NpcInteractionState state = npcs.TestCurrentState;
                if (state.EventSequence == 0 || state.SpeakerId == 0 || !string.IsNullOrEmpty(state.Greeting)) return false;
                actorId = state.SpeakerId;
                actor = npcs.TestActorById(actorId);
                Require(actor != null && routes.HasBinding(actor) && ExactProductionGate(actor),
                    "host-selected route actor is missing, unbound, or rejected by the exact production base-conversation gate on the guest.");
                routeEntityId = routes.TestEntityForActor(actor!);
                Require(routeEntityId != 0, "host-selected route actor has no guest route binding.");
                actorIndex = (int)actor!.actor_Type;
                Require(actorIndex > 0 && actorIndex < state.Count && state.Friendship[actorIndex] == FixtureFriendship && !state.TalkedToday[actorIndex],
                    "host route-talk marker did not include the fixture friendship/day baseline.");
                playerStaged = true;
                MovePlayer(actor.transform.position + Vector3.right * NearOffset);
                phase = 1;
                next = Time.unscaledTime + 0.1f;
            }
            else if (phase == 1)
            {
                MovePlayer(actor!.transform.position + Vector3.right * NearOffset);
                Require(GameManager.Get_Friendship[actorIndex] == FixtureFriendship && !GameManager.Get_Friendship_TalkedToday[actorIndex],
                    "guest did not start from the host's reversible friendship/day baseline.");
                ActorRouteState? routeState = routes.TestState(routeEntityId);
                Vector3? routePosition = routes.TestPosition(routeEntityId);
                bool? replicaActive = routes.TestActive(routeEntityId);
                if (!routeState.HasValue || !routeState.Value.Active || !routeState.Value.Walking || replicaActive != true || !actor.isActiveAndEnabled || routePosition == null)
                { guestConvergedSince = -1f; return false; }
                Vector3 hostPose = new Vector3(routeState.Value.X, routeState.Value.Y, routeState.Value.Z);
                if (Vector3.Distance(routePosition.Value, hostPose) > 0.5f)
                { guestConvergedSince = -1f; return false; }
                if (guestConvergedSince < 0f) guestConvergedSince = Time.unscaledTime;
                if (Time.unscaledTime - guestConvergedSince < 0.5f) return false;
                bool sceneValid = actor.gameObject.scene.IsValid();
                bool active = actor.isActiveAndEnabled;
                bool routeBound = routes.HasBinding(actor);
                bool structuralGate = ExactProductionGate(actor);
                bool canTalkToHim = actor.CanTalkToHim;
                bool canTalk = actor.CanTalk();
                bool noChoice = string.IsNullOrEmpty(actor.PlayerOption);
                bool routeActive = routeState.Value.Active;
                bool routeWalking = routeState.Value.Walking;
                Require(sceneValid && active && routeBound && routeActive && routeWalking && structuralGate && canTalkToHim && canTalk && noChoice,
                    "guest's active, pose-converged route actor failed native conversation eligibility: " +
                    GateSummary(sceneValid, active, routeBound, routeActive, routeWalking, structuralGate, canTalkToHim, canTalk, noChoice));
                npcs.TestResetLastResult();
                npcs.TestClickNativeActor(actor);
                closeAttempts++;
                Require(GameManager.Get_Friendship[actorIndex] == FixtureFriendship && !GameManager.Get_Friendship_TalkedToday[actorIndex],
                    "guest native Actor.MouseDown mutated friendship/day before host approval.");
                phase = 2;
                next = Time.unscaledTime + 0.35f;
            }
            else if (phase == 2)
            {
                if (npcs.TestLastResult == NpcTalkResult.Applied)
                {
                    NpcInteractionState state = npcs.TestCurrentState;
                    if (GameManager.Get_Friendship[actorIndex] != FixtureFriendship + 1 || !GameManager.Get_Friendship_TalkedToday[actorIndex] ||
                        state.Friendship[actorIndex] != FixtureFriendship + 1 || !state.TalkedToday[actorIndex] ||
                        state.SpeakerId != actorId || string.IsNullOrEmpty(state.Greeting)) return false;
                    Require(OverheadMatches(actor!, state.Greeting), "guest did not display the exact host localized route-actor greeting.");
                    guestFar = true;
                    phase = 3;
                    next = Time.unscaledTime + 0.7f;
                }
                else if (npcs.TestLastResult == NpcTalkResult.OutOfRange)
                {
                    if (closeAttempts >= MaximumCloseAttempts)
                        throw new InvalidOperationException("Could not keep pace with the moving route actor for a close talk after " +
                            closeAttempts + " attempts. " + Status + "; candidates=" + candidateReportPath);
                    npcs.TestResetLastResult();
                    MovePlayer(actor!.transform.position + Vector3.right * NearOffset);
                    guestConvergedSince = -1f;
                    phase = 1;
                    next = Time.unscaledTime + 0.35f;
                }
                else if (npcs.TestLastResult.HasValue && npcs.TestLastResult != NpcTalkResult.Applied)
                    throw new InvalidOperationException("Host rejected an otherwise eligible route-bound talk: " + npcs.TestLastResult + "; " + Status);
            }
            else if (phase == 3)
            {
                guestFar = true;
                MovePlayer(actor!.transform.position + Vector3.right * 30f);
                if (Time.unscaledTime < next) return false;
                NpcInteractionState state = npcs.TestCurrentState;
                Require(GameManager.Get_Friendship[actorIndex] == FixtureFriendship + 1 && GameManager.Get_Friendship_TalkedToday[actorIndex] &&
                    state.Friendship[actorIndex] == FixtureFriendship + 1 && state.TalkedToday[actorIndex] && state.SpeakerId == actorId,
                    "guest NPC state changed before the distant range-rejection probe.");
                npcs.TestResetLastResult();
                npcs.TestClickNativeActor(actor!);
                Require(GameManager.Get_Friendship[actorIndex] == FixtureFriendship + 1 && GameManager.Get_Friendship_TalkedToday[actorIndex],
                    "distant guest native Actor.MouseDown mutated local friendship/day before host approval.");
                phase = 4;
            }
            else if (phase == 4 && npcs.TestLastResult == NpcTalkResult.OutOfRange)
            {
                NpcInteractionState state = npcs.TestCurrentState;
                Require(GameManager.Get_Friendship[actorIndex] == FixtureFriendship + 1 && GameManager.Get_Friendship_TalkedToday[actorIndex] &&
                    state.Friendship[actorIndex] == FixtureFriendship + 1 && state.TalkedToday[actorIndex] && state.SpeakerId == actorId,
                    "distant guest route-talk request changed local NPC state.");
                phase = 5;
                next = Time.unscaledTime + 0.1f;
            }
            else if (phase == 5)
            {
                ActorRouteState? routeState = routes.TestState(routeEntityId);
                Vector3? routePosition = routes.TestPosition(routeEntityId);
                bool? routeActive = routes.TestActive(routeEntityId);
                if (!routeState.HasValue || routeState.Value.Active || routeState.Value.Walking || routeActive != false || routePosition == null)
                    return false;
                Vector3 stoppedPose = new Vector3(routeState.Value.X, routeState.Value.Y, routeState.Value.Z);
                if (Vector3.Distance(routePosition.Value, stoppedPose) > 0.05f) return false;
                RestorePlayer();
                plugin.EndPairedTestSession();
                Require(plugin.World == null, "guest route-talk test session did not close.");
                Require(npcs.TestOriginalStateRestored, "guest route-talk disconnect did not restore its original friendship/day arrays.");
                return true;
            }
            return false;
        }

        private bool TrySelectEligibleRouteActor(out Actor? selected, out string catalog)
        {
            var candidates = CollectRouteCandidates();
            var rows = new List<string>();
            selected = null;
            foreach (RouteCandidate candidate in candidates.OrderBy(item => TransformPath(item.Actor.transform), StringComparer.Ordinal))
            {
                Eligibility eligibility = EvaluateEligibility(candidate.Actor);
                rows.Add(CandidateLine(candidate.Actor, eligibility, string.Join(",", candidate.Controllers)));
                if (selected == null && eligibility.Eligible) selected = candidate.Actor;
            }
            catalog = "Route-bound NPC talk candidate catalog: controllers=" + UnityEngine.Object.FindObjectsOfType<AI_Actor_FollowRoute>(true).Length +
                "; distinct route actors=" + candidates.Count + "; structural gate=SharedNpcInteractions.IsPlainBaseConversation; " +
                "fixture baseline friendship=" + FixtureFriendship + ", talkedToday=false\n" +
                (rows.Count == 0 ? "<no route controller referenced an Actor>" : string.Join("\n", rows));
            return selected != null;
        }

        private List<RouteCandidate> CollectRouteCandidates()
        {
            var byActor = new Dictionary<Actor, RouteCandidate>();
            if (RouteActorField == null) return new List<RouteCandidate>();
            foreach (AI_Actor_FollowRoute route in UnityEngine.Object.FindObjectsOfType<AI_Actor_FollowRoute>(true))
            {
                Actor? routeActor = RouteActorField.GetValue(route) as Actor;
                if (routeActor == null) continue;
                if (!byActor.TryGetValue(routeActor, out RouteCandidate? candidate))
                { candidate = new RouteCandidate { Actor = routeActor }; byActor.Add(routeActor, candidate); }
                candidate.Controllers.Add(TransformPath(route.transform));
            }
            return byActor.Values.ToList();
        }

        private Eligibility EvaluateEligibility(Actor target)
        {
            var result = new Eligibility
            {
                RouteBound = true,
                SharedBinding = routes.HasBinding(target),
                RouteEntity = routes.TestEntityForActor(target),
                ProductionGate = ExactProductionGate(target),
                Active = target.isActiveAndEnabled,
                SceneValid = target.gameObject.scene.IsValid()
            };
            result.RouteState = result.RouteEntity == 0 ? null : routes.TestState(result.RouteEntity);
            result.ReversibleStartState = !result.Active && result.RouteState.HasValue &&
                !result.RouteState.Value.Active && !result.RouteState.Value.Walking;
            int index = (int)target.actor_Type;
            if (!result.RouteBound) result.Error = "not route-bound";
            else if (!result.SharedBinding) result.Error = "route actor has no shared route binding";
            else if (result.RouteEntity == 0 || !result.RouteState.HasValue) result.Error = "route actor has no shared route-test state";
            else if (!result.ProductionGate) result.Error = "rejected by exact production base-conversation gate";
            else if (!result.SceneValid) result.Error = "Actor scene is invalid";
            else if (!result.ReversibleStartState) result.Error = "route actor is not idle/inactive for reversible TestStart/TestStop";
            else if (index <= 0 || index >= originalFriendship.Length) result.Error = "Actor type is outside the friendship table";
            else
            {
                StageNativeType(index);
                try
                {
                    result.CanTalkToHim = target.CanTalkToHim;
                    result.CanTalkToHimEvaluated = true;
                    if (!result.CanTalkToHim)
                        result.Error = "native CanTalkToHim=false under fixture friendship/day baseline; base Actor.CanTalk short-circuits to false";
                }
                catch (Exception ex) { result.Error = "CanTalkToHim query threw " + ex.GetType().Name + ": " + ex.Message; }
                finally { RestoreNativeType(index); }
            }
            // The source save keeps route actors inactive. Their native CanTalk path may
            // depend on activation, so defer CanTalk and PlayerOption until TestStart.
            // CanTalkToHim is a direct eligibility gate: a false override means the base
            // Actor.CanTalk implementation itself will reject the conversation.
            return result;
        }

        private bool ExactProductionGate(Actor target)
        {
            try { return ProductionConversationGate?.Invoke(npcs, new object[] { target }) is bool safe && safe; }
            catch { return false; }
        }

        private static string CandidateLine(Actor target, Eligibility eligibility, string controllerPaths)
        {
            int index = (int)target.actor_Type;
            bool validIndex = index > 0 && index < GameManager.Get_Friendship.Length;
            Type type = target.GetType();
            PropertyInfo? playerOption = type.GetProperty(nameof(Actor.PlayerOption), BindingFlags.Instance | BindingFlags.Public);
            string methodGates = "MouseDown=" + MethodOwner(type, nameof(Actor.MouseDown)) +
                ",CanTalk=" + MethodOwner(type, nameof(Actor.CanTalk)) +
                ",CanTalkToHim.get=" + (type.GetProperty(nameof(Actor.CanTalkToHim), BindingFlags.Instance | BindingFlags.Public)
                    ?.GetGetMethod()?.DeclaringType?.Name ?? "<missing>") +
                ",ActionOnClick=" + MethodOwner(type, nameof(Actor.ActionOnClick)) +
                ",DialogStarted=" + MethodOwner(type, nameof(Actor.DialogStarted)) +
                ",Greetings=" + MethodOwner(type, nameof(Actor.Greetings), typeof(bool)) +
                ",PlayerClickedOnDialog=" + MethodOwner(type, nameof(Actor.PlayerClickedOnDialog)) +
                ",PlayerOption.get=" + (playerOption?.GetGetMethod()?.DeclaringType?.Name ?? "<missing>") +
                ",PlayerOption.set=" + (playerOption?.GetSetMethod()?.DeclaringType?.Name ?? "<missing>");
            return target.GetType().Name + "/" + target.actor_Type + " path=" + TransformPath(target.transform) +
                " controllers=[" + controllerPaths + "] active=" + eligibility.Active + " scene=" + eligibility.SceneValid +
                " routeBound=" + eligibility.RouteBound + " sharedBinding=" + eligibility.SharedBinding +
                " routeEntity=" + eligibility.RouteEntity + " routeActive=" + (eligibility.RouteState?.Active.ToString() ?? "unknown") +
                " routeWalking=" + (eligibility.RouteState?.Walking.ToString() ?? "unknown") +
                " reversibleStartState=" + eligibility.ReversibleStartState +
                " productionGate=" + eligibility.ProductionGate +
                " methodGates=[" + methodGates + "]" +
                " CanTalkToHim=" + (eligibility.CanTalkToHimEvaluated ? eligibility.CanTalkToHim.ToString() : "not-evaluated") +
                " CanTalkAndPlayerOption=deferred-until-active" +
                " friendship=" + (validIndex ? GameManager.Get_Friendship[index].ToString() : "invalid") +
                " talkedToday=" + (validIndex ? GameManager.Get_Friendship_TalkedToday[index].ToString() : "invalid") +
                " eligible=" + eligibility.Eligible + (eligibility.Error.Length == 0 ? "" : " rejected=" + eligibility.Error);
        }

        private static string MethodOwner(Type type, string method, params Type[] parameters) =>
            type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public, null, parameters, null)?.DeclaringType?.Name ?? "<missing>";

        private static string GateSummary(bool scene, bool active, bool routeBound, bool routeActive, bool routeWalking,
            bool structural, bool canTalkToHim, bool canTalk, bool noChoice) =>
            "scene=" + scene + ",active=" + active + ",routeBinding=" + routeBound + ",routeActive=" + routeActive +
            ",routeWalking=" + routeWalking + ",productionStructuralGate=" + structural + ",CanTalkToHim=" + canTalkToHim +
            ",CanTalk=" + canTalk + ",PlayerOptionEmpty=" + noChoice;

        private List<string> FindControllerPaths(Actor target) => CollectRouteCandidates()
            .Where(candidate => candidate.Actor == target)
            .SelectMany(candidate => candidate.Controllers)
            .ToList();

        private void WriteCandidateCatalog(string lead)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(candidateReportPath)!);
            File.WriteAllText(candidateReportPath, lead + "\n" + candidateCatalog);
        }

        private void StageNativeType(int index)
        {
            GameManager.Get_Friendship[index] = FixtureFriendship;
            GameManager.Get_Friendship_CanBeFriendWithThisAcor[index] = true;
            GameManager.Get_Friendship_TalkedToday[index] = false;
            GameManager.Get_Friendship_BoughtWeedToday[index] = false;
            GameManager.Get_Friendship_SyrupGiftedToday[index] = false;
        }

        private void RestoreNativeType(int index)
        {
            GameManager.Get_Friendship[index] = originalFriendship[index];
            GameManager.Get_Friendship_CanBeFriendWithThisAcor[index] = originalCanBeFriends[index];
            GameManager.Get_Friendship_TalkedToday[index] = originalTalkedToday[index];
            GameManager.Get_Friendship_BoughtWeedToday[index] = originalBoughtToday[index];
            GameManager.Get_Friendship_SyrupGiftedToday[index] = originalGiftedToday[index];
        }

        private void RestoreNpcState()
        {
            if (actorIndex < 0) return;
            RestoreNativeType(actorIndex);
            PublishStats?.Invoke(npcs, null);
        }

        internal void Dispose()
        {
            try { StopRoute(); }
            finally { RestoreNpcState(); RestorePlayer(); }
        }

        private void StopRoute()
        {
            if (!plugin.TestOptions.IsHost || !routeStarted || routeStopped || routeEntityId == 0) return;
            ActorRouteState? before = routes.TestState(routeEntityId);
            if (before == null) throw new InvalidOperationException("Started NPC test route disappeared before cleanup.");
            try
            {
                if (before.Value.Active || before.Value.Walking)
                {
                    if (!routes.TestStop(routeEntityId))
                        throw new InvalidOperationException("SharedActorRoutes.TestStop could not restore the started NPC test route.");
                }
            }
            finally
            {
                if (hasOriginalActorPose && actor != null)
                    actor.transform.SetPositionAndRotation(originalActorPosition, originalActorRotation);
            }
            routeStopped = true;
            ActorRouteState? after = routes.TestState(routeEntityId);
            Require(after.HasValue && !after.Value.Active && !after.Value.Walking,
                "SharedActorRoutes.TestStop left the fixture route active during cleanup.");
        }

        private void MovePlayer(Vector3 position)
        {
            MovePlayer(position, position, Vector3.zero);
        }

        private void MovePlayer(Vector3 transformPosition, Vector3 bodyPosition, Vector3 velocity)
        {
            gameplay.PlayerWalking.transform.position = transformPosition;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = bodyPosition;
            gameplay.Get_Player_Rigidbody.velocity = velocity;
        }

        private void RestorePlayer()
        {
            if (!playerStaged) return;
            playerStaged = false;
            MovePlayer(originalPlayerPosition, hasOriginalRigidbody ? originalRigidbodyPosition : originalPlayerPosition,
                hasOriginalRigidbody ? originalRigidbodyVelocity : Vector3.zero);
        }

        private static string TransformPath(Transform target)
        {
            var names = new List<string>();
            for (Transform? current = target; current != null; current = current.parent) names.Add(current.name);
            names.Reverse();
            return string.Join("/", names);
        }

        private static bool OverheadMatches(Actor target, string greeting)
        {
            ActorOverHeadText overhead = target.actorOverHeadText;
            if (overhead == null || !overhead.gameObject.activeSelf) return false;
            var field = typeof(ActorOverHeadText).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic);
            object? view = field?.GetValue(overhead);
            var text = view?.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            return text?.GetValue(view) as string == "<mark=#00000044>" + greeting + "</mark>";
        }

        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("NPC route-talk test: " + reason); }
    }
}
