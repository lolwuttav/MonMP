using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Leo;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using UnityEngine.AI;

namespace MonMP.Plugin.Replication
{
    // The host owns tamed on-foot roaming and following-player travel. Guest state
    // transitions stay inside these presentation-only native states. Every other host
    // state (wild, bed, car bed, eating, pet) is shown as a puppet: the guest's native AI
    // is suspended and Leo follows the host's smoothed pose and animator state.
    internal sealed class SharedLeo : IDisposable
    {
        private static readonly System.Reflection.FieldInfo ActiveMachine = AccessTools.Field(typeof(Leo_Manager), "_smActive");
        private static readonly System.Reflection.FieldInfo ActiveState = AccessTools.Field(typeof(StateMachine), "currentState");
        private readonly Leo_Manager? manager;
        private readonly TamedSM? tamed;
        private readonly StateMachine? originalMachine;
        private readonly BaseState? originalState;
        private readonly NavMeshAgent? agent;
        private readonly Collider? collider;
        private readonly Animator? animator;
        private readonly bool host;
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly SharedVehicles vehicles;
        private readonly LeoCommandClaims commandClaims = new LeoCommandClaims();
        private readonly Dictionary<(uint Peer, Guid Operation), LeoWorldReply> commandResults = new Dictionary<(uint, Guid), LeoWorldReply>();
        private readonly Dictionary<Guid, float> pendingCarBed = new Dictionary<Guid, float>();
        private readonly Dictionary<Guid, float> pendingOrders = new Dictionary<Guid, float>();
        private string? shownPrompt;
        private static readonly AccessTools.FieldRef<UI_UseKeyText, GameObject> UniversalPrompt = AccessTools.FieldRefAccess<UI_UseKeyText, GameObject>("GO_Universal");
        // Whoever last ordered Leo to follow (0: the host). The host's native AI follows one player
        // transform; it is pointed at that player's avatar each frame.
        private uint leader;
        internal Transform? LeaderAvatar { get; private set; }
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> leaderPoses = new Dictionary<uint, (PlayerPose, float)>();
        private GameObject? leaderTarget;
        private uint leaderTargetPeer;
        private float nextLeaderProjection;
        // Untamed Leo's native lure checks the host's hands only. A guest holding treats near him is the
        // lurer: his scared state machine targets that guest's avatar, and patched checks read the
        // guest's hands on the host (#251). The guest who tames him becomes his leader.
        private uint lurer;
        private static readonly AccessTools.FieldRef<Leo_Manager, ScaredSM> ScaredOf = AccessTools.FieldRefAccess<Leo_Manager, ScaredSM>("scaredSM");
        internal bool GuestLuring => host && !disposed && lurer != 0 && manager != null && !manager.Get_IsTamed;
        internal bool LurerHoldsTreats => GuestLuring && items.HostHoldsTreats(lurer);
        internal bool ConsumeLurerTreat() => GuestLuring && items.HostConsumeHeldTreats(lurer);
        private readonly LeoWorldState? original;
        private readonly bool originalAgentEnabled;
        private readonly bool originalColliderEnabled;
        private readonly Transform? originalParent;
        private readonly Vector3 originalPosition;
        private readonly Quaternion originalRotation;
        private readonly int originalAnimatorState, originalAnimatorCustomValue;
        private readonly float originalAnimatorForward, originalAnimatorTurn;
        private NetworkSession? session;
        private WorldChannel? channel;
        private LeoWorldState? current;
        private bool mirroring, puppet, guestChanged, disposed;
        private Vector3 targetPosition; private Quaternion targetRotation; private bool hasTarget;
        private Transform? targetParent;
        private CarBed? puppetCarBed;
        private float nextCapture;
        // Host-only edges of Leo's native presentation (#11, #22): the treat in his mouth, the car-bed
        // whistles, the needs popup and a treat he ate. Everyone else replays them.
        private bool lastBite, lastNeeds, watched;
        private Leo_State lastState;
        private static readonly System.Reflection.FieldInfo? NeedTypeField = AccessTools.Field(typeof(PetNeeds), "NeedType");
        // The guest whose treat the host is feeding Leo (0: the host's own).
        internal static uint TreatGiver;
        private const byte LeoBiteOff = 0, LeoBiteOn = 1;

        internal bool Bound => manager != null && tamed != null && agent != null && original != null;
        internal bool GuestBlocks => !disposed && LeoCarBedAuthorityPolicy.BlocksNativeCallback(session?.IsRunning == true, host);
        internal bool MirrorsNativeAi(StateMachine candidate) => GuestBlocks && candidate != null && manager != null &&
            ((mirroring && ReferenceEquals(candidate, tamed)) || (puppet && candidate.gameObject == manager.gameObject));
        internal bool MirrorsNativeTick(Leo_Manager candidate) => GuestBlocks && (mirroring || puppet) && ReferenceEquals(candidate, manager);
        internal bool IsBound(Leo_Manager candidate) => candidate != null && ReferenceEquals(candidate, manager);
        internal bool Mirroring => mirroring;
        // The host's Leo: a menu-joined guest's own native Leo is still wild, so its tamed flag can't be trusted.
        internal bool SharedTamed => host ? manager?.Get_IsTamed == true : current?.IsTamed == true;
        internal bool SharedPetting => host ? manager?.Get_Leo_State == Leo_State.PetState : current?.NativeState == (byte)Leo_State.PetState;
        internal bool TryGuestUiState(out LeoWorldState state)
        {
            state = current!;
            return GuestBlocks && Bound && (mirroring || puppet) && current != null;
        }
        internal bool TryGuestDogBedRemoval(Vehicle_PartSocket socket, out bool allowed)
        {
            allowed = false;
            if (!TryGuestUiState(out var state) || socket.car_PartsType != Car_PartsType.DogBed ||
                !socket.isInstalled || socket.partInstalled is not Part_DogBed || socket.vehicleAttached?.Get_Vehicle == null) return false;
            // The puppet does not enter the host's native Leo state machine.
            // Only this local preflight uses its shared state; the host still
            // runs the complete native check before accepting any removal.
            if (socket.boltHolesDetails.Any(hole => hole.isBoltInstalled)) return true;
            allowed = !state.IsTamed || (state.NativeState != (byte)Leo_State.CarBed_Entering &&
                (state.NativeState != (byte)Leo_State.CarBed || state.InVehicleId >= 0 &&
                    state.InVehicleId != (int)socket.vehicleAttached.Get_Vehicle.vehicle_type));
            return true;
        }
        internal string GuestNameSuffix(Leo_Manager candidate, string native)
        {
            if (!IsBound(candidate) || !TryGuestUiState(out var state)) return native;
            string home = state.AttachedHome switch
            {
                (byte)House_Location.Roulotte => TEXT.GetText("Leo_Home_Roulotte"),
                (byte)House_Location.Cottage => TEXT.GetText("Leo_Home_Cottage"),
                (byte)House_Location.Speedway => TEXT.GetText("Leo_Home_Speedway"),
                _ => ""
            };
            string status = state.StayAtHome ? "\n<size=70%>[<color=#AAFFAA>" + TEXT.GetText("Leo_RoamingHome") + "</color>]</size>" : "";
            return status + "<line-height=65%>\n<size=60%><color=#E2E2E2>" + home + "</color></size>";
        }
        private void RefreshGuestName(bool changed)
        {
            var player = gameplay.Player;
            if (changed && player != null && manager != null && player.Get_CurrentInteractable == manager)
                player.ResetCurrentInteractable(manager);
        }

#if MONMP_DEVELOPMENT
        internal LeoWorldState? TestState => current;
#endif


        internal SharedLeo(Gameplay gameplay, bool host, string diagnostics, SharedItems items, SharedVehicles vehicles)
        {
            this.host = host; this.gameplay = gameplay;
            this.items = items; this.vehicles = vehicles;
            var candidates = UnityEngine.Object.FindObjectsOfType<Leo_Manager>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid()).ToArray();
            if (candidates.Length == 1 && gameplay.Get_Leo_Manager == candidates[0])
            {
                var candidate = candidates[0];
                var nativeTamed = candidate.Get_TamedSM;
                var nativeAgent = candidate.GetComponent<NavMeshAgent>();
                var nativeCollider = candidate.GetComponent<Collider>();
                var nativeAnimator = candidate.GetComponent<Animator>();
                if (nativeTamed != null && nativeAgent != null && nativeCollider != null && nativeAnimator != null &&
                    ActiveMachine?.GetValue(candidate) is StateMachine machine && ActiveState?.GetValue(machine) is BaseState state)
                {
                    manager = candidate; tamed = nativeTamed; agent = nativeAgent; collider = nativeCollider; animator = nativeAnimator;
                    originalMachine = machine; originalState = state;
                    original = current = Capture();
                    originalAgentEnabled = nativeAgent.enabled;
                    originalColliderEnabled = nativeCollider.enabled;
                    originalParent = candidate.transform.parent;
                    originalPosition = candidate.transform.position; originalRotation = candidate.transform.rotation;
                    originalAnimatorState = nativeAnimator.GetInteger("State");
                    originalAnimatorCustomValue = nativeAnimator.GetInteger("customValue");
                    originalAnimatorForward = nativeAnimator.GetFloat("Forward");
                    originalAnimatorTurn = nativeAnimator.GetFloat("Turn");
                }
            }

#if MONMP_DEVELOPMENT
            File.WriteAllText(Path.Combine(diagnostics, "shared-leo.txt"),
                "Role=" + (host ? "host" : "guest") + "\nBound=" + Bound +
                "\nIdentity=" + (Bound ? LeoWorldState.Entity.ToString("X16") : "unavailable") +
                "\nInitialState=" + (original?.NativeState.ToString() ?? "unavailable"));
#endif

        }

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            if (original != null) yield return new WorldRecord(LeoWorldState.Entity, 1, LeoWorldState.Adapter, original.Encode());
        }

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            world.SnapshotApplied += Snapshot; world.Changed += Changed;
            world.CommandRequested += Command; world.ReplyReceived += Reply;
            connected.PoseReceived += LeaderPose;
            connected.PeerLeft += LeaderLeft;
        }

        internal bool RequestCarBed(CarBed bed)
        {
            if (host || disposed || session?.IsRunning != true) return false;
            if (channel?.FullySynchronized != true || !Bound)
            { Report("Wait for the shared Leo world to finish loading before using a car bed."); return true; }
            if (pendingCarBed.Count != 0) return true;
            ulong target = vehicles.EntityOf(bed == null ? null : bed.Get_Vehicle);
            if (target == 0)
            { Report("This vehicle is not available to Leo in the shared world."); return true; }
            var request = new LeoWorldCommand(Guid.NewGuid(), LeoCommandAction.UseCarBed, target);
            pendingCarBed[request.Operation] = Time.unscaledTime + 4f;
            channel!.Request(LeoWorldState.Entity, request.Encode(), true);
            return true;
        }

        // The native Use order on the guest's copy of Leo: follow when he stays home, otherwise stay home
        // (on home land). The host applies it to its own Leo with the same native state changes (#251).
        internal void RequestOrder()
        {
            if (host || disposed || session?.IsRunning != true) return;
            if (channel?.FullySynchronized != true || !Bound || current == null)
            { Report("Wait for the shared Leo world to finish loading before giving Leo an order."); return; }
            if (!current.IsTamed || pendingOrders.Count != 0) return;
            var action = current.NativeState == (byte)Leo_State.RoamingHome ? LeoCommandAction.FollowPlayer : LeoCommandAction.ToggleStayAtHome;
            var request = new LeoWorldCommand(Guid.NewGuid(), action);
            pendingOrders[request.Operation] = Time.unscaledTime + 4f;
            channel!.Request(LeoWorldState.Entity, request.Encode(), true);
        }

        // The native hover prompt reads this guest's own copy of Leo, which replays only his on-foot states
        // and may not count as tamed here: at home it showed no follow order (#251). Prompt from the
        // shared state instead, as the native hover does.
        internal void GuestHover(Leo_Manager leo)
        {
            shownPrompt = null;
            ShowGuestPrompt(leo);
        }

        // The native prompt is shown once, when the crosshair first lands on Leo. On a guest his state
        // arrives from the host afterwards (an order's reply comes before the new state), and mirrored
        // state changes can clear the prompt, so the guest refreshes it while Leo stays hovered (#251).
        private void RefreshGuestPrompt()
        {
            if (host || manager == null) return;
            if (gameplay.Player == null || gameplay.Player.Get_CurrentInteractable != manager) { shownPrompt = null; return; }
            ShowGuestPrompt(manager);
        }

        private void ShowGuestPrompt(Leo_Manager leo)
        {
            if (host || disposed || session?.IsRunning != true || current == null || !current.IsTamed) return;
            if (gameplay.Player == null || !gameplay.Player.Get_IfCanEquipTool) return;
            var keys = Singleton<UI_UseKeyText>.i;
            if (keys == null) return;
            var inputs = Singleton<GameAssets>.i.inputs.Player;
            var state = (Leo_State)current.NativeState;
            string key; UnityEngine.InputSystem.InputAction input;
            if (state == Leo_State.RoamingHome) { key = "Leo_OrderFollowPlayer"; input = inputs.Use; }
            else if ((state == Leo_State.EatingState || state == Leo_State.FollowingPlayer || state == Leo_State.RoamingPlayer ||
                state == Leo_State.SleepingInBedState) && gameplay.IsOnHomeLand(leo.transform.position))
            { key = "Leo_OrderToRoamingHome"; input = inputs.Use; }
            else if (state != Leo_State.PetState) { key = "Leo_Pet"; input = inputs.LeftClick; }
            else { if (shownPrompt != null) keys.HideUniversal(); shownPrompt = null; return; }
            var shown = UniversalPrompt(keys);
            if (key == shownPrompt && shown != null && shown.activeSelf) return;
            keys.ShowUniversal(input, TEXT.GetText(key), 1, 0);
            shownPrompt = key;
        }

        private LeoWorldActionStatus Order(uint peer, LeoCommandAction action)
        {
            if (!Bound || channel?.FullySynchronized != true || items.TransfersBlocked || session?.Peers.ContainsKey(peer) != true)
                return LeoWorldActionStatus.NotReady;
            if (!manager!.Get_IsTamed) return LeoWorldActionStatus.NotTamed;
            if (!items.HostPeerNear(peer, manager.transform.position, 6f)) return LeoWorldActionStatus.TooFar;
            var state = manager.Get_Leo_State;
            if (action == LeoCommandAction.FollowPlayer)
            {
                if (state != Leo_State.RoamingHome) return LeoWorldActionStatus.InvalidState;
                manager.Get_StayAtHome = false;
                leader = peer; FollowLeader();
                tamed!.ChangeState(tamed.roamingPlayerState);
            }
            else
            {
                if (state != Leo_State.EatingState && state != Leo_State.FollowingPlayer && state != Leo_State.RoamingPlayer && state != Leo_State.SleepingInBedState)
                    return LeoWorldActionStatus.InvalidState;
                if (!gameplay.IsOnHomeLand(manager.transform.position)) return LeoWorldActionStatus.InvalidTarget;
                manager.Get_StayAtHome = true;
                manager.Get_AttachedHome = gameplay.ClosestHouse(manager.transform.position);
                tamed!.ChangeState(tamed.roamingHome);
            }
            current = Capture();
            channel!.Publish(LeoWorldState.Entity, current.Encode());
            return LeoWorldActionStatus.Accepted;
        }

        // The host's own native follow order.
        internal void HostOrdered(Leo_Manager native)
        {
            if (host && !disposed && IsBound(native)) { leader = 0; FollowLeader(); }
        }

        internal IDisposable? BeginHostOrder(Leo_Manager native) =>
            host && !disposed && Bound && IsBound(native) ? new HostOrderScope(this) : null;

        private sealed class HostOrderScope : IDisposable
        {
            private SharedLeo? owner;
            private readonly uint previousLeader;
            private readonly Leo_State previousState;
            private readonly bool previousStay;
            private readonly Furniture_Bed? previousBed;
            private readonly CarBed? previousCarBed;
            internal HostOrderScope(SharedLeo owner)
            {
                this.owner = owner;
                previousLeader = owner.leader; previousState = owner.manager!.Get_Leo_State;
                previousStay = owner.manager.Get_StayAtHome;
                previousBed = owner.tamed!.Get_Bed; previousCarBed = owner.tamed.Get_CarBed;
                owner.HostOrdered(owner.manager);
            }
            public void Dispose()
            {
                var current = owner;
                owner = null;
                if (current == null || current.disposed || current.manager == null || current.tamed == null) return;
                // Native Use/CarBed/Sleep can refuse an order. A no-op or an
                // exception before mutation must not steal a client's follower.
                bool changed = current.manager.Get_Leo_State != previousState || current.manager.Get_StayAtHome != previousStay ||
                    !ReferenceEquals(current.tamed.Get_Bed, previousBed) || !ReferenceEquals(current.tamed.Get_CarBed, previousCarBed);
                if (!changed) current.leader = previousLeader;
                current.FollowLeader();
            }
        }

        // A guest's pet still belongs to the host's Leo: native Pet grants the daily
        // friendship point, marks him petted and enters his appreciation animation.
        // Only the guest gets the personal anxiety relief they already applied locally.
        internal bool PetFromGuest(uint peer)
        {
            if (!host || disposed || !Bound || session?.IsRunning != true || channel?.FullySynchronized != true ||
                items.TransfersBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true ||
                session.Peers.ContainsKey(peer) != true || peer == session.LocalPeerId ||
                !manager!.isActiveAndEnabled || !manager.gameObject.activeInHierarchy || !manager.Get_IsTamed ||
                manager.Get_Leo_State == Leo_State.PetState || !ReferenceEquals(ActiveMachine?.GetValue(manager), tamed) ||
                !items.HostPeerNear(peer, manager.transform.position, 6f)) return false;
            LeoGuestPet.Apply(tamed!);
            MonMPPlugin.Instance?.World?.NpcInteractions?.HostFriendshipChanged();
            return true;
        }

        private void UpdateLure()
        {
            if (!host || manager == null) return;
            if (manager.Get_IsTamed)
            {
                if (lurer != 0) { leader = lurer; lurer = 0; }
                return;
            }
            var scared = ScaredOf(manager);
            if (scared == null) return;
            var hostPlayer = gameplay.Player;
            var hostItem = hostPlayer == null ? null : hostPlayer.Get_HoldingItem != null ? (InteractableItems)hostPlayer.Get_HoldingItem : hostPlayer.Get_ActiveTool;
            bool hostLures = hostItem != null && hostItem.GetComponent<Tool_DogTreats>() != null;
            var current = lurer != 0 && session?.Peers.ContainsKey(lurer) == true ? MonMPPlugin.Instance?.RemoteAvatar(lurer) : null;
            if (current == null) lurer = 0;
            if (hostLures) lurer = 0;
            else if (lurer == 0 || !items.HostHoldsTreats(lurer))
            {
                // The nearest guest holding treats within the native 40 m; a lurer who put them away
                // stays the target, so Leo gives up on them natively.
                float best = 40f * 40f;
                foreach (var peer in session?.Peers.Keys.ToArray() ?? Array.Empty<uint>())
                {
                    if (peer == session!.LocalPeerId || !items.HostHoldsTreats(peer)) continue;
                    var avatar = MonMPPlugin.Instance?.RemoteAvatar(peer);
                    if (avatar == null) continue;
                    float distance = (avatar.position - manager.transform.position).sqrMagnitude;
                    if (distance < best) { best = distance; lurer = peer; current = avatar; }
                }
            }
            var target = lurer != 0 ? current : hostPlayer != null ? hostPlayer.transform : null;
            if (target != null && scared.player != target) scared.player = target;
        }

        private void LeaderPose(uint peer, PlayerPose pose)
        {
            if (host) leaderPoses[peer] = (pose, Time.unscaledTime);
        }

        private void LeaderLeft(uint peer)
        {
            leaderPoses.Remove(peer);
            if (leader == peer) { leader = 0; FollowLeader(); }
        }

        private void FollowLeader()
        {
            if (!host || tamed == null || manager == null) return;
            if (leader != 0 && session?.Peers.ContainsKey(leader) != true) leader = 0;
            if (leader != 0)
            {
                if (!leaderPoses.TryGetValue(leader, out var observed) || Time.unscaledTime - observed.Time > 5f)
                {
                    // A temporary gap must not make an invalid host order
                    // leave the native target switched back to the host.
                    if (leaderTarget != null && leaderTargetPeer == leader)
                    { LeaderAvatar = leaderTarget.transform; if (tamed.player != LeaderAvatar) tamed.player = LeaderAvatar; }
                    return;
                }
                if (leaderTarget == null)
                    leaderTarget = new GameObject("MonMP Leo follow target") { hideFlags = HideFlags.HideAndDontSave };
                if (leaderTargetPeer != leader) { leaderTargetPeer = leader; nextLeaderProjection = 0; }
                // Sample at most ten times per second; visual interpolation/seat
                // calibration must never be the native AI's player position.
                if (Time.unscaledTime >= nextLeaderProjection)
                {
                    nextLeaderProjection = Time.unscaledTime + 0.1f;
                    var pose = observed.Pose;
                    var position = new Vector3(pose.X, pose.Y, pose.Z);
                    var carrier = pose.Carrier != 0 ? items.Carriers?.Resolve(pose.Carrier) : null;
                    if (carrier != null) position = carrier.TransformPoint(new Vector3(pose.CarrierX, pose.CarrierY, pose.CarrierZ));
                    // No nearby mesh: retain the actual received position so
                    // native distance/leash decisions still see a distant player.
                    position = LeoFollowTarget.Near(agent, position, position);
                    leaderTarget.transform.SetPositionAndRotation(position, Quaternion.Euler(0, pose.Yaw, 0));
                }
                LeaderAvatar = leaderTarget.transform;
            }
            else LeaderAvatar = null;
            var target = LeaderAvatar != null ? LeaderAvatar : gameplay.Player != null ? gameplay.Player.transform : null;
            if (target != null && tamed.player != target) tamed.player = target;
        }

        internal bool TryLeaderRoamDestination(out Vector3 destination)
        {
            destination = default;
            if (!host || leader == 0 || LeaderAvatar == null || manager == null) return false;
            destination = LeoFollowTarget.Roam(agent, LeaderAvatar.position, LeaderAvatar.forward, manager.transform.position);
            return true;
        }

        private LeoWorldState Capture()
        {
            var native = manager!;
            var needs = tamed!;
            var p = native.transform.position; var r = native.transform.eulerAngles;
            float forward = animator!.GetFloat("Forward"), turn = animator.GetFloat("Turn");
            // Native roaming/following divides movement by deltaTime, which is zero
            // in the pause menu. Non-finite cosmetic parameters must not block hosting.
            if (float.IsNaN(forward) || float.IsInfinity(forward)) forward = 0f;
            if (float.IsNaN(turn) || float.IsInfinity(turn)) turn = 0f;
            int vehicle = -1;
            if (native.Get_IsTamed && native.Get_Leo_State == Leo_State.CarBed && needs.Get_CarBed != null && needs.Get_CarBed.Get_Vehicle != null)
                vehicle = (int)needs.Get_CarBed.Get_Vehicle.vehicle_type;
            return new LeoWorldState(native.Get_IsTamed, (byte)native.Get_Leo_State, native.Get_StayAtHome,
                (byte)native.Get_AttachedHome, vehicle, p.x, p.y, p.z, r.x, r.y, r.z,
                needs.Get_Thirst, needs.Get_Hunger,
                (uint)Math.Max(0, needs.Get_TickSinceThirsty), (uint)Math.Max(0, needs.Get_TickSinceHungry),
                Mathf.Clamp(forward, 0f, 100f), Mathf.Clamp(turn, -1f, 1f),
                (sbyte)Mathf.Clamp(animator.GetInteger("State"), -1, 127), (sbyte)Mathf.Clamp(animator.GetInteger("customValue"), -1, 127));
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (!host && records.TryGetValue(LeoWorldState.Entity, out var record)) Apply(record);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == LeoWorldState.Adapter) Apply(record);
        }

        private void Apply(WorldRecord record)
        {
            if (!Bound || record.Id != LeoWorldState.Entity || record.Adapter != LeoWorldState.Adapter)
                throw new InvalidDataException("Unknown shared Leo identity.");
            bool wasTamed = current?.IsTamed == true, known = current != null;
            var received = LeoWorldState.Decode(record.Payload);
            bool labelChanged = !TryGuestUiState(out _) || current!.StayAtHome != received.StayAtHome ||
                current.AttachedHome != received.AttachedHome || current.IsTamed != received.IsTamed;
            current = received;
            // Native Leo_Manager grants this whenever Leo is tamed, including on load.
            if (!wasTamed && current.IsTamed) GuestAchievements.Award(AchievementType.TameLeo);
            // Tamed while this guest played (by any player): the native tamed message and jingle.
            if (known && !wasTamed && current.IsTamed)
            {
                try
                {
                    Singleton<UI_EventMessage>.i.Show(TEXT.GetText("Leo_SuccessfullyTamed"), "", 10f, UI_EventMessage.EventIconType.Heart);
                    Singleton<SOUND_Manager>.i.PlaySound_BuggyBuildCompleted();
                }
                catch (Exception ex) { Debug.LogWarning("MonMP Leo tamed message failed: " + ex.Message); }
            }
            // Leaving a car bed puts the puppet back under its scene parent before the checks.
            if (puppet && current.InVehicleId == -1 && manager!.transform.parent != originalParent)
                manager.transform.SetParent(originalParent, true);
            if (CanMirror(current))
            {
                puppet = false;
                mirroring = true;
                if (ApplyNative(current)) { RefreshGuestName(labelChanged); return; }
                mirroring = false;
            }
            ApplyPuppet(current);
            RefreshGuestName(labelChanged);
        }

        private void ApplyPuppet(LeoWorldState state)
        {
            if (!puppet) { mirroring = false; puppet = true; hasTarget = false; }
            guestChanged = true;
            if (agent!.enabled) agent.enabled = false;
            ApplyGuestCollider(state);
            Transform? parent = originalParent;
            Vector3 position = new Vector3(state.X, state.Y, state.Z);
            Quaternion rotation = Quaternion.Euler(state.EulerX, state.EulerY, state.EulerZ);
            if (state.InVehicleId != -1)
            {
                // Ride in this guest's copy of the car so the dog moves with it between updates.
                try
                {
                    var vehicle = Singleton<GlobalClassManager>.i.GetVehicles((Vehicle_Type)state.InVehicleId);
                    var bed = vehicle?.vehicle_PartsManager?.DogBed?.CustomObjectToEnableWhenPartIsInstalled?.GetComponent<CarBed>();
                    if (vehicle != null && bed != null && bed.Leo_Position != null)
                    {
                        if (!ReferenceEquals(puppetCarBed, bed)) bed.seat?.CallLeoInSeat();
                        puppetCarBed = bed;
                        parent = vehicle.transform; position = bed.Leo_Position.position; rotation = bed.Leo_Position.rotation;
                    }
                }
                catch (Exception ex) { Debug.LogWarning("MonMP Leo car bed lookup failed: " + ex.Message); }
            }
            else puppetCarBed = null;
            if (manager!.transform.parent != parent) { manager.transform.SetParent(parent, true); hasTarget = false; }
            SetTarget(position, rotation, parent);
            PresentAnimator(state);
        }

        private void PresentAnimator(LeoWorldState state)
        {
            animator!.SetFloat("Forward", state.AnimationForward);
            animator.SetFloat("Turn", state.AnimationTurn);
            if (state.AnimationState < 0) return;
            if (animator.GetInteger("State") != state.AnimationState ||
                (state.AnimationCustom >= 0 && animator.GetInteger("customValue") != state.AnimationCustom))
            {
                if (state.AnimationCustom >= 0) animator.SetInteger("customValue", state.AnimationCustom);
                animator.SetInteger("State", state.AnimationState);
                animator.SetTrigger("StateOn");
            }
        }

        // Targets are kept in the parent's space so a car-bed ride stays glued to the car.
        private void SetTarget(Vector3 worldPosition, Quaternion worldRotation, Transform? parent)
        {
            targetParent = parent;
            targetPosition = parent != null ? parent.InverseTransformPoint(worldPosition) : worldPosition;
            targetRotation = parent != null ? Quaternion.Inverse(parent.rotation) * worldRotation : worldRotation;
            if (!hasTarget || Vector3.Distance(manager!.transform.position, worldPosition) > 6f)
            {
                manager!.transform.SetPositionAndRotation(worldPosition, worldRotation);
                hasTarget = true;
            }
        }

        private void Smooth()
        {
            if (!hasTarget || manager == null) return;
            var parent = targetParent;
            if (manager.transform.parent != parent) return;
            Vector3 position = parent != null ? parent.TransformPoint(targetPosition) : targetPosition;
            Quaternion rotation = parent != null ? parent.rotation * targetRotation : targetRotation;
            float blend = 1f - Mathf.Exp(-12f * Time.deltaTime);
            manager.transform.SetPositionAndRotation(Vector3.Lerp(manager.transform.position, position, blend),
                Quaternion.Slerp(manager.transform.rotation, rotation, blend));
        }

        private bool CanMirror(LeoWorldState state)
        {
            if (manager == null || tamed == null || agent == null || original == null || originalState == null ||
                !ReferenceEquals(originalMachine, tamed) || !original.IsTamed || original.InVehicleId != -1 || originalParent != null ||
                !state.IsTamed || !manager.Get_IsTamed || state.InVehicleId != -1 ||
                !ReferenceEquals(manager.transform.parent, originalParent) || !ReferenceEquals(ActiveMachine?.GetValue(manager), tamed) ||
                !SafeOnFootState(state.NativeState)) return false;
            // RoamingHome resolves one of three built-in scene transforms by this enum.
            return state.NativeState != (byte)Leo_State.RoamingHome || state.AttachedHome <= (byte)House_Location.Speedway;
        }

        private static bool SafeOnFootState(byte state) => state == (byte)Leo_State.RoamingHome ||
            state == (byte)Leo_State.RoamingPlayer || state == (byte)Leo_State.FollowingPlayer;

        private void ApplyGuestCollider(LeoWorldState state)
        {
            if (host || collider == null || !state.IsTamed) return;
            // Native on-foot states disable this collider on exit before entering
            // the car-bed sequence. A puppet never runs those AI transitions.
            bool carBed = state.NativeState == (byte)Leo_State.CarBed ||
                state.NativeState == (byte)Leo_State.CarBed_Entering || state.NativeState == (byte)Leo_State.CarBed_Exiting;
            // A snapshot can skip the walking state between a ride and food/bed.
            // These native Enter callbacks also enable interaction explicitly.
            bool interactive = SafeOnFootState(state.NativeState) || state.NativeState == (byte)Leo_State.EatingState ||
                state.NativeState == (byte)Leo_State.GoToFoodBowlState || state.NativeState == (byte)Leo_State.SleepingInBedState;
            if (carBed && collider.enabled) collider.enabled = false;
            else if (interactive && !collider.enabled) collider.enabled = true;
        }

        private BaseState? ResolveSafeState(byte state) => state switch
        {
            (byte)Leo_State.RoamingHome => tamed?.roamingHome,
            (byte)Leo_State.RoamingPlayer => tamed?.roamingPlayerState,
            (byte)Leo_State.FollowingPlayer => tamed?.followingPlayerState,
            _ => null
        };

        private bool ApplyNative(LeoWorldState state)
        {
            BaseState? targetState = ResolveSafeState(state.NativeState);
            if (targetState == null || !ReferenceEquals(ActiveMachine?.GetValue(manager), tamed)) return false;
            guestChanged = true;
            manager!.Get_StayAtHome = state.StayAtHome;
            manager.Get_AttachedHome = (House_Location)state.AttachedHome;
            if (manager.Get_Leo_State != (Leo_State)state.NativeState)
            {
                // These three Enter/Exit methods only configure the local collider,
                // NavMeshAgent and animator; references to a host bed/car never appear.
                tamed!.ChangeState(targetState);
                if (manager.Get_Leo_State != (Leo_State)state.NativeState) return false;
            }
            // The native state may already match the new on-foot snapshot after
            // a puppet car ride, so ChangeState/Enter can legitimately be skipped.
            ApplyGuestCollider(state);
            guestChanged = true;
            tamed!.Get_Thirst = state.Thirst; tamed.Get_Hunger = state.Hunger;
            tamed.Get_TickSinceThirsty = (int)state.TicksSinceThirsty;
            tamed.Get_TickSinceHungry = (int)state.TicksSinceHungry;
            if (agent!.enabled) agent.enabled = false;
            SetTarget(new Vector3(state.X, state.Y, state.Z), Quaternion.Euler(state.EulerX, state.EulerY, state.EulerZ), originalParent);
            animator!.SetFloat("Forward", state.AnimationForward);
            animator.SetFloat("Turn", state.AnimationTurn);
            return true;
        }

        private void StopMirror()
        {
            if (!mirroring && !puppet && !guestChanged) return;
            mirroring = false; puppet = false; hasTarget = false;
            if (manager != null && manager.transform.parent != originalParent) manager.transform.SetParent(originalParent, true);
            RestoreGuestBaseline();
            if (agent != null) agent.enabled = originalAgentEnabled;
        }

        private void RestoreGuestBaseline()
        {
            if (host || !guestChanged || manager == null || tamed == null || original == null) return;
            if (ReferenceEquals(originalMachine, tamed) && originalState != null &&
                ReferenceEquals(ActiveMachine?.GetValue(manager), tamed))
            {
                manager.transform.SetParent(originalParent, true);
                manager.transform.SetPositionAndRotation(originalPosition, originalRotation);
                manager.Get_StayAtHome = original.StayAtHome;
                manager.Get_AttachedHome = (House_Location)original.AttachedHome;
                tamed.Get_Thirst = original.Thirst; tamed.Get_Hunger = original.Hunger;
                tamed.Get_TickSinceThirsty = (int)original.TicksSinceThirsty;
                tamed.Get_TickSinceHungry = (int)original.TicksSinceHungry;
                // Re-enter the local state to refresh its private timers and path after
                // mirroring, then restore exact visible values that Enter may touch.
                tamed.ChangeState(originalState);
                manager.transform.SetParent(originalParent, true);
                manager.transform.SetPositionAndRotation(originalPosition, originalRotation);
                manager.Get_StayAtHome = original.StayAtHome;
                manager.Get_AttachedHome = (House_Location)original.AttachedHome;
                tamed.Get_Thirst = original.Thirst; tamed.Get_Hunger = original.Hunger;
                tamed.Get_TickSinceThirsty = (int)original.TicksSinceThirsty;
                tamed.Get_TickSinceHungry = (int)original.TicksSinceHungry;
            }
            if (collider != null) collider.enabled = originalColliderEnabled;
            if (animator != null)
            {
                animator.ResetTrigger("StateOn");
                animator.SetInteger("State", originalAnimatorState);
                animator.SetInteger("customValue", originalAnimatorCustomValue);
                animator.SetFloat("Forward", originalAnimatorForward);
                animator.SetFloat("Turn", originalAnimatorTurn);
            }
            guestChanged = false;
        }

        internal void Update()
        {
            if (disposed || !Bound || session?.IsRunning != true) return;
            if (pendingCarBed.Count > 0) foreach (var operation in pendingCarBed.Where(value => Time.unscaledTime >= value.Value).Select(value => value.Key).ToArray())
            { pendingCarBed.Remove(operation); Report("The host did not respond to Leo's car-bed request."); }
            if (pendingOrders.Count > 0) foreach (var operation in pendingOrders.Where(value => Time.unscaledTime >= value.Value).Select(value => value.Key).ToArray())
            { pendingOrders.Remove(operation); Report("The host did not respond to Leo's order."); }
            UpdateLure();
            FollowLeader();
            RefreshGuestPrompt();
            if (host)
            {
                WatchPresentation();
                if (Time.unscaledTime < nextCapture) return;
                nextCapture = Time.unscaledTime + 0.1f;
                current = Capture(); channel!.Publish(LeoWorldState.Entity, current.Encode());
            }
            else if (mirroring && current != null)
            {
                if (!CanMirror(current) || !ApplyNative(current)) ApplyPuppet(current);
                Smooth();
            }
            else if (puppet) Smooth();
        }

        [MonMP.Core.World.WorldCommandAdapters(LeoWorldState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != LeoWorldState.Entity || peer == 0 || channel == null || session == null) return;
            LeoWorldCommand request;
            try { request = LeoWorldCommand.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (request.Action != LeoCommandAction.UseCarBed && request.Action != LeoCommandAction.FollowPlayer &&
                request.Action != LeoCommandAction.ToggleStayAtHome) return;
            var key = (peer, request.Operation);
            var claim = commandClaims.Claim(peer, request.Operation);
            if (claim == LeoCommandClaim.Duplicate)
            {
                if (commandResults.TryGetValue(key, out var old) && session.Peers.ContainsKey(peer)) channel.Reply(peer, entity, old.Encode());
                return;
            }
            LeoWorldActionStatus status = claim == LeoCommandClaim.Capacity
                ? LeoWorldActionStatus.Capacity
                : request.Action == LeoCommandAction.UseCarBed ? UseCarBed(peer, request.Target) : Order(peer, request.Action);
            var reply = new LeoWorldReply(request.Operation, request.Action, status);
            commandResults[key] = reply;
            if (session.Peers.ContainsKey(peer)) channel.Reply(peer, entity, reply.Encode());
        }

        private LeoWorldActionStatus UseCarBed(uint peer, ulong target)
        {
            if (!Bound || channel?.FullySynchronized != true || items.TransfersBlocked || session?.Peers.ContainsKey(peer) != true)
                return LeoWorldActionStatus.NotReady;
            if (!manager!.Get_IsTamed) return LeoWorldActionStatus.NotTamed;
            var vehicle = vehicles.VehicleFor(target);
            var bed = vehicle?.vehicle_PartsManager?.DogBed?.CustomObjectToEnableWhenPartIsInstalled?.GetComponent<CarBed>();
            if (vehicle == null || bed == null || !bed.gameObject.activeInHierarchy || bed.Leo_Position == null || bed.Leo_ExitLocation == null ||
                bed.Get_Vehicle != vehicle) return LeoWorldActionStatus.InvalidTarget;
            if (!items.HostPeerNear(peer, bed.transform.position, 4.5f)) return LeoWorldActionStatus.TooFar;

            if (manager.Get_Leo_State == Leo_State.CarBed)
            {
                if (tamed!.Get_CarBed != bed) return LeoWorldActionStatus.InvalidTarget;
                if (bed.Get_Vehicle.isPassengerDoorClosed) return LeoWorldActionStatus.DoorClosed;
                if (bed.Get_Vehicle.Speed > 1f) return LeoWorldActionStatus.TooFast;
                tamed.ChangeState(tamed.carBedState_Exiting);
                // Out of the client's car he follows that client, not a host who may be far away (he
                // went home when the host was over 50 m off).
                leader = peer; FollowLeader();
            }
            else
            {
                Leo_State state = manager.Get_Leo_State;
                if (state != Leo_State.RoamingPlayer && state != Leo_State.FollowingPlayer && state != Leo_State.PetState && state != Leo_State.RoamingHome)
                    return LeoWorldActionStatus.InvalidState;
                if (state == Leo_State.RoamingHome && !items.HostPeerNear(peer, manager.transform.position, 50f))
                    return LeoWorldActionStatus.TooFar;
                if (bed.Get_Vehicle.isPassengerDoorClosed) return LeoWorldActionStatus.DoorClosed;
                manager.Get_StayAtHome = false;
                tamed!.Get_CarBed = bed;
                leader = peer; FollowLeader();
                tamed.ChangeState(tamed.carBedState_Entering);
            }
            current = Capture();
            channel!.Publish(LeoWorldState.Entity, current.Encode());
            return LeoWorldActionStatus.Accepted;
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != LeoWorldState.Entity || payload == null || payload.Length != LeoWorldReply.Size) return;
            LeoWorldReply reply;
            try { reply = LeoWorldReply.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (pendingOrders.Remove(reply.Operation))
            {
                if (reply.Status == LeoWorldActionStatus.Accepted)
                {
                    if (reply.Action == LeoCommandAction.FollowPlayer) Singleton<SOUND_Manager>.i?.PlaySound_PartRemoved();
                    else Singleton<SOUND_Manager>.i?.PlaySound_PartTempInstalled();
                    gameplay.Player?.ResetCurrentInteractable();
                    return;
                }
                Report(reply.Status switch
                {
                    LeoWorldActionStatus.TooFar => "Move closer to Leo to give him an order.",
                    LeoWorldActionStatus.NotTamed => "Leo must be tamed before he takes orders.",
                    LeoWorldActionStatus.InvalidTarget => "Leo can only be sent home on your land.",
                    LeoWorldActionStatus.InvalidState => "Leo cannot take that order right now.",
                    _ => "The host could not apply Leo's order."
                });
                return;
            }
            if (!pendingCarBed.Remove(reply.Operation) || reply.Status == LeoWorldActionStatus.Accepted) return;
            Report(reply.Status switch
            {
                LeoWorldActionStatus.TooFar => "Move closer to Leo and the car bed.",
                LeoWorldActionStatus.NotTamed => "Leo must be tamed before he can use a car bed.",
                LeoWorldActionStatus.DoorClosed => "Open the passenger door before using Leo's car bed.",
                LeoWorldActionStatus.TooFast => "The vehicle must be stopped before Leo can leave the car bed.",
                LeoWorldActionStatus.InvalidState => "Leo cannot use a car bed in his current state.",
                _ => "The host could not apply Leo's car-bed request."
            });
        }

        private void WatchPresentation()
        {
            var leo = manager!;
            bool bite = leo.TreatInMouth != null && leo.TreatInMouth.activeSelf;
            bool needs = leo.petNeeds != null && leo.petNeeds.gameObject.activeSelf;
            var state = leo.Get_Leo_State;
            if (watched)
            {
                var events = PresentationReplays.Events;
                if (bite != lastBite) events?.HostAnnounce(PresentationKind.LeoBite, bite ? LeoBiteOn : LeoBiteOff, LeoWorldState.Entity, leo.transform.position, 0, 0, false);
                if (state != lastState && state == Leo_State.CarBed_Entering)
                    events?.HostAnnounce(PresentationKind.LeoWhistle, 0, LeoWorldState.Entity, leo.transform.position, 0, 0, false);
                else if (state != lastState && state == Leo_State.CarBed_Exiting)
                    events?.HostAnnounce(PresentationKind.LeoWhistle, 1, LeoWorldState.Entity, leo.transform.position, 0, 0, false);
                if (needs && !lastNeeds && NeedTypeField?.GetValue(leo.petNeeds) is int type && type >= 0 && type < 256)
                    events?.HostAnnounce(PresentationKind.LeoNeeds, (byte)type, LeoWorldState.Entity, leo.transform.position, 0, 0, false);
            }
            watched = true; lastBite = bite; lastNeeds = needs; lastState = state;
        }

        // The host's tamed Leo ate a treat: everyone but its giver hears it and sees the heart.
        internal void HostTreatEaten()
        {
            if (!host || disposed || manager == null || session?.IsRunning != true) return;
            PresentationReplays.Events?.HostAnnounce(PresentationKind.LeoEat, 0, LeoWorldState.Entity, manager.transform.position, 0, TreatGiver, false);
        }

        internal void Present(PresentationEvent e)
        {
            if (host || disposed || !Bound) return;
            var leo = manager!;
            switch ((PresentationKind)e.Kind)
            {
                case PresentationKind.LeoBite:
                    if (e.Variant == LeoBiteOn) leo.SOUND_Leo?.Play_Leo_BiteChomp();
                    if (leo.TreatInMouth != null) leo.TreatInMouth.SetActive(e.Variant == LeoBiteOn);
                    break;
                case PresentationKind.LeoWhistle:
                    using (SpatialSounds.At(leo.transform.position))
                    {
                        var sound = Singleton<SOUND_Manager>.i;
                        sound.PlayOneShot2D(e.Variant == 0 ? sound.Player_Whistle : sound.Player_ShortWhistle, _randomPitch: true);
                    }
                    break;
                case PresentationKind.LeoNeeds:
                    if (leo.petNeeds != null && e.Variant >= 1 && e.Variant <= 2) leo.petNeeds.Initialized(e.Variant);
                    break;
                case PresentationKind.LeoEat:
                    leo.SOUND_Leo?.Play_LeoEatTreat();
                    if (leo.petHearth != null) leo.petHearth.gameObject.SetActive(true);
                    break;
            }
        }

        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            StopMirror();
            if (host && tamed != null && gameplay.Player != null) tamed.player = gameplay.Player.transform;
            if (host && manager != null && gameplay.Player != null && ScaredOf(manager) is { } scared) scared.player = gameplay.Player.transform;
            if (session != null) { session.PoseReceived -= LeaderPose; session.PeerLeft -= LeaderLeft; }
            leader = 0; LeaderAvatar = null; lurer = 0;
            leaderPoses.Clear();
            if (leaderTarget != null) UnityEngine.Object.Destroy(leaderTarget);
            leaderTarget = null;
            if (agent != null) agent.enabled = originalAgentEnabled;
            session = null; channel = null; pendingCarBed.Clear(); pendingOrders.Clear(); commandResults.Clear(); commandClaims.Clear(); puppetCarBed = null;
        }
    }
}
