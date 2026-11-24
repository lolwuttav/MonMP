using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // NPC friendship/day state is host-owned. Guest clicks are accepted for
    // plain base Actor conversations and exact source-reviewed greeting paths
    // for Actor_Jacques and Actor_Eric. Other actor-specific dialogue mutations
    // remain host-side.
    internal sealed class SharedNpcInteractions : IDisposable
    {
        private sealed class PeerPose
        {
            public PlayerPose Pose;
            public float Time;
        }

        private readonly bool host;
        private readonly SharedActorRoutes actorRoutes;
        private readonly Dictionary<Actor, ulong> idsByActor = new Dictionary<Actor, ulong>();
        private readonly Dictionary<ulong, Actor> actorsById = new Dictionary<ulong, Actor>();
        private readonly WorldRecord[] descriptors;
        private readonly HashSet<Actor> routeBoundActors = new HashSet<Actor>();
        private readonly FieldInfo? routeActorField = AccessTools.Field(typeof(AI_Actor_FollowRoute), "actor");
        private readonly Dictionary<uint, PeerPose> peerPoses = new Dictionary<uint, PeerPose>();
        private readonly HashSet<(uint Peer, Guid Request)> claims = new HashSet<(uint, Guid)>();
        private readonly Queue<(uint Peer, Guid Request)> claimOrder = new Queue<(uint, Guid)>();
        private readonly HashSet<(uint Peer, Guid Request)> giftClaims = new HashSet<(uint, Guid)>();
        private readonly Queue<(uint Peer, Guid Request)> giftClaimOrder = new Queue<(uint, Guid)>();
        private readonly HashSet<(uint Peer, Guid Request)> weedSaleClaims = new HashSet<(uint, Guid)>();
        private readonly Queue<(uint Peer, Guid Request)> weedSaleClaimOrder = new Queue<(uint, Guid)>();
        private readonly HashSet<(uint Peer, Guid Request)> motherPhoneClaims = new HashSet<(uint, Guid)>();
        private readonly Queue<(uint Peer, Guid Request)> motherPhoneClaimOrder = new Queue<(uint, Guid)>();
        private readonly Dictionary<(uint Peer, Guid Request), MotherPhoneCallReply> motherPhoneResults = new Dictionary<(uint, Guid), MotherPhoneCallReply>();
        private readonly Queue<(uint Peer, Guid Request)> motherPhoneResultOrder = new Queue<(uint, Guid)>();
        private readonly Dictionary<Guid, float> pending = new Dictionary<Guid, float>();
        private readonly Dictionary<Guid, (ulong Actor, float Time)> pendingDialogTalks = new Dictionary<Guid, (ulong, float)>();
        private readonly NpcDialogChoiceAuthority dialogChoices = new NpcDialogChoiceAuthority();
        private NpcDialogReply? pendingDialogOffer;
        private float pendingDialogOfferExpires;
        private readonly Dictionary<Guid, NpcDialogGreetingAssembler> dialogGreetings = new Dictionary<Guid, NpcDialogGreetingAssembler>();
        private readonly Dictionary<Actor, string?> guestDialogOriginalOptions = new Dictionary<Actor, string?>();
        private Actor? guestDialogActor;
        private Guid guestDialogToken, guestChoiceOperation;
        private float guestDialogExpires;
        private string guestDialogGreeting = "";
        private bool guestOpeningDialog, remoteDialogOffered, remoteGillesWindowOpened;
        private static readonly FieldInfo DialogCurrentActor = AccessTools.Field(typeof(UI_GenericWindow_Dialogs), "CurrentActor");
        private static readonly FieldInfo DialogGreeting = AccessTools.Field(typeof(UI_GenericWindow_Dialogs), "ActorGreetings");
        private static readonly MethodInfo DialogUpdateButtons = AccessTools.Method(typeof(UI_GenericWindow_Dialogs), "UpdateButtons", new[] { typeof(string) });
        private readonly Dictionary<Guid, (Furniture_Phone Phone, float Time)> pendingMotherPhoneCalls =
            new Dictionary<Guid, (Furniture_Phone, float)>();
        private readonly Dictionary<Guid, (ulong Actor, ulong Item, NpcGiftKind Kind, float Time)> pendingGiftRequests = new Dictionary<Guid, (ulong, ulong, NpcGiftKind, float)>();
        private readonly Dictionary<Guid, (ulong Actor, ulong Item, NpcWeedPackKind Kind, float Time)> pendingWeedSaleRequests = new Dictionary<Guid, (ulong, ulong, NpcWeedPackKind, float)>();
        private readonly HashSet<(ulong Actor, ulong Item)> pendingLocalGifts = new HashSet<(ulong, ulong)>();
        private readonly HashSet<(ulong Actor, ulong Item)> completedLocalGifts = new HashSet<(ulong, ulong)>();
        private readonly HashSet<(ulong Actor, ulong Item)> pendingLocalWeedSales = new HashSet<(ulong, ulong)>();
        private readonly HashSet<(ulong Actor, ulong Item)> completedLocalWeedSales = new HashSet<(ulong, ulong)>();
        private readonly int[] originalFriendship;
        private readonly bool[] originalCanBeFriends, originalTalkedToday, originalBoughtToday, originalGiftedToday;
        private NpcInteractionState current;
        private NetworkSession? session;
        private WorldChannel? channel;
        private Actor? captureActor, remoteActor;
        private Actor? authorizedGiftActor;
        private InteractableItems? authorizedGiftItem;
        private Actor? authorizedWeedSaleActor;
        private InteractableItems? authorizedWeedSaleItem;
        private ulong captureActorId;
        private string? captureGreeting, completedGreeting;
        private float nextCapture, nextPendingSweep;
        private bool friendshipUiDirty;
        private bool initialized, disposed;

#if MONMP_DEVELOPMENT
        private NpcTalkResult? testLastResult;


        private NpcGiftResult? testLastGiftResult;


        private string testLastGiftDiagnostics = "";


        private NpcWeedSaleResult? testLastWeedSaleResult;
#endif

        private static readonly MethodInfo? NativeActorGiftTrigger = AccessTools.Method(typeof(Actor), "OnTriggerEnter", new[] { typeof(Collider) });
        private static readonly FieldInfo? NativeWantBigPack = AccessTools.Field(typeof(Actor), "WantBigPack");
        private static readonly FieldInfo? NativeDistanceDisableTargets = AccessTools.Field(typeof(DisableWhenPlayerTooFar), "ToDisableWhenPlayerTooFar");
        private static readonly FieldInfo? NativeDistanceDisableRadius = AccessTools.Field(typeof(DisableWhenPlayerTooFar), "DistanceToDisable");
        private static readonly FieldInfo? NativeDistanceDisablePlayer = AccessTools.Field(typeof(DisableWhenPlayerTooFar), "Player");
        private static readonly FieldInfo? UniqueMotherGreetingsField = AccessTools.Field(typeof(TEXT), "DIALOG_UNIQUE_MOTHER");
        private const float PeerPoseMaxAge = 3f;
        // Every concrete native buyer reviewed against Actor.OnTriggerEnter/BuyThisPack.
        // Normand and Raymond also run the reviewed friendship/greeting award path.
        // Runtime BuyWeed, friendship, daily, pack, route and activity gates still apply.
        private static readonly HashSet<Type> ReviewedNativeWeedSaleActors = new HashSet<Type>
        {
            typeof(Actor_Tom), typeof(Actor_Luc), typeof(Actor_Martin), typeof(Actor_Twitch),
            typeof(Actor_Anthony), typeof(Actor_Antoine), typeof(Actor_Carole), typeof(Actor_Felix), typeof(Actor_Francis),
            typeof(Actor_Jacques), typeof(Actor_Michael), typeof(Actor_Normand), typeof(Actor_Raymond), typeof(Actor_Roger)
        };


#if MONMP_DEVELOPMENT
        internal NpcInteractionState TestCurrentState => current;


        internal NpcTalkResult? TestLastResult => testLastResult;


        internal NpcGiftResult? TestLastGiftResult => testLastGiftResult;


        internal string TestLastGiftDiagnostics => testLastGiftDiagnostics;


        internal NpcWeedSaleResult? TestLastWeedSaleResult => testLastWeedSaleResult;
#endif

        internal bool NewActionsBlocked { get; set; }
        // Keep late receipts correlated without blocking save/disconnect indefinitely.
        internal bool TransactionsSettled => pendingWeedSaleRequests.Values.All(value => Time.unscaledTime - value.Time >= 30f);

#if MONMP_DEVELOPMENT
        internal MotherPhoneCallReply? TestLastMotherPhoneReply { get; private set; }


        internal bool TestOriginalStateRestored => GameManager.Get_Friendship.SequenceEqual(originalFriendship) &&
            GameManager.Get_Friendship_CanBeFriendWithThisAcor.SequenceEqual(originalCanBeFriends) &&
            GameManager.Get_Friendship_TalkedToday.SequenceEqual(originalTalkedToday) &&
            GameManager.Get_Friendship_BoughtWeedToday.SequenceEqual(originalBoughtToday) &&
            GameManager.Get_Friendship_SyrupGiftedToday.SequenceEqual(originalGiftedToday);
#endif


        public SharedNpcInteractions(bool host, SharedActorRoutes actorRoutes)
        {
            this.host = host;
            this.actorRoutes = actorRoutes ?? throw new ArgumentNullException(nameof(actorRoutes));
            var descriptorList = new List<WorldRecord>();
            foreach (Actor actor in UnityEngine.Object.FindObjectsOfType<Actor>(true))
            {
                string path = ScenePath(actor.transform);
                ulong id = StaticId.Hash("monmp/npc/talk/actor/v2/" + path);
                if (id == NpcInteractionState.Entity || idsByActor.ContainsKey(actor) || actorsById.ContainsKey(id))
                    throw new InvalidOperationException("NPC interaction identity collision: " + path);
                idsByActor.Add(actor, id); actorsById.Add(id, actor);
                byte[] descriptor = Descriptor(actor);
                descriptorList.Add(new WorldRecord(id, 1, NpcInteractionState.Adapter, descriptor));
            }
            descriptors = descriptorList.OrderBy(record => record.Id).ToArray();
            foreach (AI_Actor_FollowRoute route in UnityEngine.Object.FindObjectsOfType<AI_Actor_FollowRoute>(true))
            {
                Actor? routeActor = routeActorField?.GetValue(route) as Actor;
                if (routeActor != null) routeBoundActors.Add(routeActor);
            }
            originalFriendship = (int[])GameManager.Get_Friendship.Clone();
            originalCanBeFriends = (bool[])GameManager.Get_Friendship_CanBeFriendWithThisAcor.Clone();
            originalTalkedToday = (bool[])GameManager.Get_Friendship_TalkedToday.Clone();
            originalBoughtToday = (bool[])GameManager.Get_Friendship_BoughtWeedToday.Clone();
            originalGiftedToday = (bool[])GameManager.Get_Friendship_SyrupGiftedToday.Clone();
            current = Capture(0, 0, "");
            ValidateNativeLayout(current);
        }

        private static byte[] Descriptor(Actor actor)
        {
            byte[] type = Encoding.UTF8.GetBytes(actor.GetType().FullName ?? actor.GetType().Name);
            if (type.Length > 240) throw new InvalidOperationException("NPC type name is too long.");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(0x3141504E); writer.Write((byte)type.Length); writer.Write(type); writer.Write((int)actor.actor_Type);
            return stream.ToArray();
        }

        // Same-name ordinals rather than raw sibling indices, which a played save shifts.
        private static string ScenePath(Transform target) => SharedBuildingDoors.StablePath(target);

        private static NpcInteractionState Capture(ulong sequence, ulong speaker, string greeting) =>
            new NpcInteractionState(sequence, speaker, greeting, GameManager.Get_Friendship,
                GameManager.Get_Friendship_CanBeFriendWithThisAcor, GameManager.Get_Friendship_TalkedToday,
                GameManager.Get_Friendship_BoughtWeedToday, GameManager.Get_Friendship_SyrupGiftedToday);

        private static readonly int NativeActorTypeCount = Enum.GetValues(typeof(Actor_Type)).Length;

        private bool MatchesNativeState()
        {
            return current.MatchesNative(GameManager.Get_Friendship,
                GameManager.Get_Friendship_CanBeFriendWithThisAcor, GameManager.Get_Friendship_TalkedToday,
                GameManager.Get_Friendship_BoughtWeedToday, GameManager.Get_Friendship_SyrupGiftedToday);
        }

        private static void ValidateNativeLayout(NpcInteractionState state)
        {
            if (state.Count != NativeActorTypeCount || NativeActorTypeCount > NpcInteractionState.MaxActorTypes)
                throw new InvalidOperationException("Unsupported native NPC friendship layout.");
        }

        public IEnumerable<WorldRecord> InitialRecords()
        {
            yield return new WorldRecord(NpcInteractionState.Entity, 1, NpcInteractionState.Adapter, current.Encode());
            foreach (WorldRecord descriptor in descriptors) yield return descriptor;
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
            channel.ReplyReceived += Reply; session.PoseReceived += PoseReceived; session.PeerLeft += PeerLeft;
        }

        private bool TryApply(WorldRecord record, bool showFeedback)
        {
            if (record.Id != NpcInteractionState.Entity || record.Adapter != NpcInteractionState.Adapter)
                throw new InvalidDataException("Unknown NPC interaction record.");
            var next = NpcInteractionState.Decode(record.Payload); ValidateNativeLayout(next);
            ulong previousSequence = current.EventSequence;
            int[] previousFriendship = current.Friendship;
            ApplyNative(next); current = next; initialized = true;
            friendshipUiDirty = true;
            if (!showFeedback) return true;
            for (int i = 0; i < next.Count; i++)
                if (next.Friendship[i] != previousFriendship[i])
                    UnityEngine.Object.FindObjectOfType<UI_FrontLeftPopup>()?.Friendship((Actor_Type)i, next.Friendship[i] < previousFriendship[i]);
            if (next.EventSequence != previousSequence && actorsById.TryGetValue(next.SpeakerId, out Actor speaker) &&
                speaker != null && speaker.isActiveAndEnabled && !string.IsNullOrEmpty(next.Greeting))
                speaker.TalkOverHead(next.Greeting);
            return true;
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || !records.TryGetValue(NpcInteractionState.Entity, out WorldRecord record)) return;
            TryApply(record, false);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Id == NpcInteractionState.Entity && record.Adapter == NpcInteractionState.Adapter)
                TryApply(record, true);
        }

        private static MethodInfo? Method(Type type, string name, params Type[] parameters) =>
            type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, parameters, null);

        // Greeting overrides reviewed as host-world reads or host-owned progression (ToDo unlocks,
        // store unlocks, bar and snack variants). Their text is captured and replayed on guests.
        private static readonly HashSet<Type> ReviewedGreetings = new HashSet<Type>
        {
            typeof(Actor_Jacques), typeof(Actor_Eric), typeof(Actor_Melanie), typeof(Actor_Francis), typeof(Actor_Normand),
            typeof(Actor_Antoine), typeof(Actor_Carole), typeof(Actor_Chef), typeof(Actor_DerbyDemolition), typeof(Actor_Roger), typeof(Actor_Police),
        };
        // Exact reviewed options: Francis's garage, Normand's donation and Gilles's
        // search window. Host-issued tokens authorize guest choices.
        private static readonly HashSet<Type> ReviewedChoiceActors = new HashSet<Type> { typeof(Actor_Francis), typeof(Actor_Normand), typeof(Actor_Gilles) };

        // Harmony prefix on UI_GenericWindowManager.OpenDialogs: during a guest's host-run talk the
        // host's own dialog window must not open; the greeting is spoken instead and captured.
        internal bool RedirectRemoteDialog(Actor actor, bool friendshipGotUp)
        {
            if (!host || remoteActor == null || remoteActor != actor) return false;
            remoteDialogOffered = ReviewedChoiceActors.Contains(actor.GetType()) && !string.IsNullOrEmpty(actor.PlayerOption) && DialogWorldEligible(actor);
            actor.TalkOverHead(actor.Greetings(friendshipGotUp));
            return true;
        }

        // Gilles can open his search directly from DialogStarted on repeat talks,
        // or through his dialog option. Keep both native callbacks off the host UI.
        internal bool RedirectRemoteGillesWindow()
        {
            if (!host || remoteActor?.GetType() != typeof(Actor_Gilles)) return false;
            remoteGillesWindowOpened = true;
            return true;
        }

        private static bool DialogWorldEligible(Actor actor) => actor.GetType() == typeof(Actor_Francis) ?
            GameManager.Get_Friendship[(int)actor.actor_Type] >= 25 && Singleton<HomeGarage>.i?.IsBuildStarted == false :
            actor.GetType() == typeof(Actor_Normand) ? GameManager.Get_Friendship[(int)actor.actor_Type] >= 50 &&
            Singleton<Gameplay>.i?.Speedway?.Get_DaySinceBuildStarted == -1 :
            actor.GetType() == typeof(Actor_Gilles) && actor.CanTalk() &&
            Singleton<Gameplay>.i?.Store_PoliceInterceptor is Store_PoliceInterceptor police &&
            (police.Get_AmountOfDaySinceLastSpeedingTicket == -1 || police.Get_AmountOfDaySinceLastSpeedingTicket >= 7);

        internal bool GuestDialogGreeting(Actor actor, out string text)
        {
            text = guestDialogGreeting;
            return !host && guestOpeningDialog && actor == guestDialogActor;
        }

        internal bool GuestDialogChoice(Actor actor, out string text)
        {
            text = "";
            if (host || disposed || session?.IsRunning != true) return false;
            var window = UnityEngine.Object.FindObjectOfType<UI_GenericWindow_Dialogs>();
            if (actor != guestDialogActor || guestDialogToken == Guid.Empty || Time.unscaledTime > guestDialogExpires ||
                window == null || !window.isActiveAndEnabled || DialogCurrentActor.GetValue(window) as Actor != actor ||
                channel?.FullySynchronized != true || MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            { text = "This dialog option has expired. Talk to this person again."; return true; }
            text = "Waiting for the host...";
            if (guestChoiceOperation != Guid.Empty) return true;
            guestChoiceOperation = Guid.NewGuid();
            channel.Request(NpcInteractionState.Entity, new NpcDialogChoiceRequest(guestChoiceOperation, idsByActor[actor], guestDialogToken).Encode(), true);
            return true;
        }

        private void TryOpenGuestDialog()
        {
            if (pendingDialogOffer is not NpcDialogReply offer) return;
            if (Time.unscaledTime > pendingDialogOfferExpires) { dialogGreetings.Remove(offer.Operation); pendingDialogOffer = null; return; }
            if (!dialogGreetings.TryGetValue(offer.Operation, out var assembled) || !assembled.TryRead(out string greeting)) return;
            dialogGreetings.Remove(offer.Operation);
            pendingDialogOffer = null;
            if (!actorsById.TryGetValue(offer.Actor, out Actor actor) || actor == null || !actor.isActiveAndEnabled ||
                !ReviewedChoiceActors.Contains(actor.GetType()) || string.IsNullOrEmpty(greeting)) return;
            guestDialogActor = actor; guestDialogToken = offer.Offer; guestDialogExpires = Time.unscaledTime + 25f;
            guestChoiceOperation = Guid.Empty; guestDialogGreeting = greeting;
            // Only the text and option are local. OpenDialogs normally calls Greetings,
            // so the exact override prefixes return the captured host greeting in this scope.
            if (!guestDialogOriginalOptions.ContainsKey(actor)) guestDialogOriginalOptions.Add(actor, actor.PlayerOption);
            actor.PlayerOption = actor is Actor_Francis ? TEXT.DIALOG_Francis_Option1 :
                actor is Actor_Gilles ? TEXT.DIALOG_Gilles_Option1 :
                TEXT.GetText("NORMAND_ButtonDonateToStartRacetrack") + " (<b>70 000$</b>)";
            guestOpeningDialog = true;
            try { UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>()?.OpenDialogs(actor, false); }
            finally { guestOpeningDialog = false; }
        }

        private void DialogChoiceCommand(uint peer, NpcDialogChoiceRequest request)
        {
            NpcDialogStatus status;
            try
            {
                status = dialogChoices.Execute(peer, request, Time.unscaledTime,
                    () => ValidateDialogChoice(peer, request.Actor),
                    () => ApplyDialogChoice(request.Actor));
            }
            catch (Exception ex) { Debug.LogError("MonMP host NPC dialog choice failed: " + ex); status = NpcDialogStatus.Unavailable; }
            channel!.Reply(peer, NpcInteractionState.Entity, new NpcDialogReply(request.Operation, request.Actor, request.Offer, current.EventSequence, status).Encode());
        }

        private NpcDialogStatus ValidateDialogChoice(uint peer, ulong actorId)
        {
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true || !actorsById.TryGetValue(actorId, out Actor actor) ||
                actor == null || !actor.isActiveAndEnabled || !IsPlainBaseConversation(actor) || !actor.CanTalkToHim || !DialogWorldEligible(actor))
                return NpcDialogStatus.Unavailable;
            if (!PeerNear(peer, actor.transform.position, 4f)) return NpcDialogStatus.OutOfRange;
            if (actor is Actor_Normand && Singleton<Cash_Manager>.i?.isCanAfford(70000f) != true) return NpcDialogStatus.NotEnoughCash;
            return NpcDialogStatus.Applied;
        }

        private NpcDialogStatus ApplyDialogChoice(ulong actorId)
        {
            Actor actor = actorsById[actorId];
            remoteGillesWindowOpened = false;
            remoteActor = actor;
            try { using (Patches.RemoteActionScope.Enter()) actor.PlayerClickedOnDialog(); }
            finally { remoteActor = null; }
            PublishStatsIfChanged();
            if (actor.GetType() == typeof(Actor_Gilles)) return remoteGillesWindowOpened ? NpcDialogStatus.Applied : NpcDialogStatus.Unavailable;
            return DialogWorldEligible(actor) ? NpcDialogStatus.Unavailable : NpcDialogStatus.Applied;
        }

        private bool IsPlainBaseConversation(Actor actor)
        {
            if (actor == null || !actor.gameObject.scene.IsValid() || !idsByActor.ContainsKey(actor)) return false;
            Type type = actor.GetType();
            int actorType = (int)actor.actor_Type;
            MethodInfo? greeting = Method(type, nameof(Actor.Greetings), typeof(bool));
            bool approvedGreetingOverride = ReviewedGreetings.Contains(type) && greeting?.DeclaringType == type;
            bool reviewedChoice = ReviewedChoiceActors.Contains(type);
            // Gilles inherits the friendship transaction, but his reviewed virtual
            // steps gate pending searches and choose the search/refusal presentation.
            bool reviewedGilles = type == typeof(Actor_Gilles);
            return NpcTalkPolicy.IsSafeHostConversation(
                isActor: typeof(Actor).IsAssignableFrom(type),
                baseMouseDown: Method(type, nameof(Actor.MouseDown))?.DeclaringType == typeof(Actor),
                baseCanTalk: reviewedGilles || Method(type, nameof(Actor.CanTalk))?.DeclaringType == typeof(Actor),
                baseActionOnClick: reviewedGilles || Method(type, nameof(Actor.ActionOnClick))?.DeclaringType == typeof(Actor),
                baseDialogStarted: reviewedChoice || Method(type, nameof(Actor.DialogStarted))?.DeclaringType == typeof(Actor),
                baseGreeting: Method(type, nameof(Actor.Greetings), typeof(bool))?.DeclaringType == typeof(Actor),
                baseOptionHandler: reviewedChoice || Method(type, nameof(Actor.PlayerClickedOnDialog))?.DeclaringType == typeof(Actor) &&
                    type.GetProperty(nameof(Actor.PlayerOption), BindingFlags.Instance | BindingFlags.Public)?.GetGetMethod()?.DeclaringType == typeof(Actor) &&
                    type.GetProperty(nameof(Actor.PlayerOption), BindingFlags.Instance | BindingFlags.Public)?.GetSetMethod()?.DeclaringType == typeof(Actor),
                canTalk: true, hasChoice: false, actorType: actorType,
                allowApprovedGreetingOverride: approvedGreetingOverride,
                isRouteBound: routeBoundActors.Contains(actor), hasSharedRouteBinding: actorRoutes.HasBinding(actor));
        }

        private bool IsSafeNativeGiftPath(Actor actor, NpcGiftKind kind)
        {
            if (actor == null || !actor.gameObject.scene.IsValid() || !idsByActor.ContainsKey(actor)) return false;
            Type type = actor.GetType();
            MethodInfo? ownTrigger = type == typeof(Actor) ? null :
                type.GetMethod("OnTriggerEnter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            // Gifts call the same virtual Greetings(true) as a successful conversation.
            // The exact reviewed overrides include host-owned unlocks; capture their text
            // and let the existing progress adapters publish their world effects.
            bool baseTalkGate = type.GetProperty(nameof(Actor.CanTalkToHim), BindingFlags.Instance | BindingFlags.Public)
                ?.GetGetMethod()?.DeclaringType == typeof(Actor);
            MethodInfo? giftGreeting = Method(type, nameof(Actor.Greetings), typeof(bool));
            bool approvedGiftGreeting = giftGreeting?.DeclaringType == typeof(Actor) ||
                (ReviewedGreetings.Contains(type) && giftGreeting?.DeclaringType == type);
            bool approvedAcceptance = kind switch
            {
                NpcGiftKind.MapleSyrup => Method(type, nameof(Actor.canGiveMapleSyrupCan), typeof(Item_MapleSyrupCan))?.DeclaringType == typeof(Actor) ||
                    (type == typeof(Actor_Police) && Method(type, nameof(Actor.canGiveMapleSyrupCan), typeof(Item_MapleSyrupCan))?.DeclaringType == typeof(Actor_Police)),
                NpcGiftKind.MapleWine => Method(type, nameof(Actor.canGiveMapleWineBottle), typeof(Item_MapleWineBottle))?.DeclaringType == typeof(Actor),
                // Reconstructed sources show Carole is the sole AcceptCoffeeHasGift override.
                NpcGiftKind.HomemadeCoffee => type == typeof(Actor_Carole) && actor.AcceptCoffeeHasGift,
                _ => false
            };
            return baseTalkGate && approvedGiftGreeting && NpcGiftPolicy.IsSafeHostGiftPath(typeof(Actor).IsAssignableFrom(type),
                ownTrigger == null && NativeActorGiftTrigger != null, approvedAcceptance, (int)actor.actor_Type, kind);
        }


#if MONMP_DEVELOPMENT
        internal Actor? TestFindSafeActor() => actorsById.OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .FirstOrDefault(TestIsSafeActor);


        internal bool TestIsSafeGiftActor(Actor actor) => TestIsSafeGiftActor(actor, NpcGiftKind.MapleSyrup);


        internal bool TestIsSafeGiftActor(Actor actor, NpcGiftKind kind) => actor != null && actor.isActiveAndEnabled &&
            !routeBoundActors.Contains(actor) && IsSafeNativeGiftPath(actor, kind) && actor.CanTalkToHim;


        internal string TestGiftCandidateSummary => TestGiftCandidateSummaryFor(NpcGiftKind.MapleSyrup);


        internal string TestGiftCandidateSummaryFor(NpcGiftKind kind)
        {
            Actor[] catalog = actorsById.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
            string details = string.Join("|", catalog.Select(actor =>
            {
                bool safePath = IsSafeNativeGiftPath(actor, kind);
                bool canTalk = safePath && actor.CanTalkToHim;
                bool eligible = actor.isActiveAndEnabled && !routeBoundActors.Contains(actor) && canTalk;
                return actor.GetType().Name + "/" + actor.actor_Type + ":active=" + actor.isActiveAndEnabled +
                    ":route=" + routeBoundActors.Contains(actor) + ":scene=" + actor.gameObject.scene.IsValid() +
                    ":safe" + kind + "=" + safePath + ":canTalk=" + (safePath ? canTalk.ToString() : "not-invoked") +
                    ":eligible=" + eligible;
            }));
            return "kind=" + kind + ";catalog=" + catalog.Length + "; " + details;
        }


        internal Actor? TestFindSafeGiftActor() => TestFindSafeGiftActor(NpcGiftKind.MapleSyrup);


        internal Actor? TestFindSafeGiftActor(NpcGiftKind kind)
        {
            // Prefer Anthony for the base syrup path; coffee is only exact Actor_Carole.
            return actorsById.OrderBy(pair => pair.Key).Select(pair => pair.Value)
                .Where(actor => TestIsSafeGiftActor(actor, kind))
                .OrderBy(actor => kind == NpcGiftKind.HomemadeCoffee
                    ? (actor.GetType() == typeof(Actor_Carole) ? 0 : 1)
                    : (actor is Actor_Anthony ? 0 : 1))
                .FirstOrDefault();
        }


        internal string TestWeedSaleCandidateSummary
        {
            get
            {
                Actor[] candidates = actorsById.Where(pair => pair.Value != null).OrderBy(pair => pair.Key)
                    .Select(pair => pair.Value).ToArray();
                AI_Actor_FollowRoute[] routes = UnityEngine.Object.FindObjectsOfType<AI_Actor_FollowRoute>(true);
                string details = string.Join("|", candidates.Select(actor =>
                {
                    ulong id = idsByActor.TryGetValue(actor, out ulong actorId) ? actorId : 0;
                    AI_Actor_FollowRoute[] owners = routes.Where(route => route != null &&
                        routeActorField?.GetValue(route) as Actor == actor).ToArray();
                    string routeInfo = owners.Length == 0 ? "none" : string.Join(",", owners.Select(route =>
                        "enabled=" + route.enabled + ":active=" + route.gameObject.activeInHierarchy +
                        ":tick=" + route.TickStartWalking + ":walking=" + route.Get_IsWalking +
                        ":waypoint=" + route.Get_currentWayPoint + ":back=" + route.Get_IsWalkingBack));
                    string wantBig = NativeWantBigPack?.GetValue(actor) is bool wantsBig ? wantsBig.ToString() : "unavailable";
                    int index = (int)actor.actor_Type;
                    bool hasState = index >= 0 && index < GameManager.Get_Friendship.Length &&
                        index < GameManager.Get_Friendship_BoughtWeedToday.Length;
                    return id + ":" + actor.GetType().Name + "/" + actor.actor_Type +
                        ":path=" + ScenePath(actor.transform) +
                        ":sceneValid=" + actor.gameObject.scene.IsValid() +
                        ":activeSelf=" + actor.gameObject.activeSelf +
                        ":activeHierarchy=" + actor.gameObject.activeInHierarchy +
                        ":enabled=" + actor.isActiveAndEnabled +
                        ":route=" + routeInfo +
                        ":reviewedType=" + ReviewedNativeWeedSaleActors.Contains(actor.GetType()) +
                        ":fixtureEligibility=" + TestWeedSaleCandidateReason(actor) +
                        ":activationSources=" + TestWeedSaleActivationSummary(actor) +
                        ":fixturePriority=" + SaleFixturePriority(actor) +
                        ":buy=" + actor.BuyWeed + ":bonus=" + actor.BuyWeedGetFriendshipUp +
                        ":needsSleep=" + actor.NeedToSleepToSellWeedAgain +
                        ":minFriendship=" + actor.MinimumFriendshipToBuyWeed +
                        ":friendship=" + (hasState ? GameManager.Get_Friendship[index].ToString() : "unavailable") +
                        ":boughtToday=" + (hasState ? GameManager.Get_Friendship_BoughtWeedToday[index].ToString() : "unavailable") +
                        ":dailyEligibility=" + TestWeedSaleFixtureDailyEligibility(actor) +
                        ":wantBig=" + wantBig + ":position=" + actor.transform.position.ToString("F1");
                }));
                TickSystem? tick = UnityEngine.Object.FindObjectOfType<TickSystem>();
                return "actorCount=" + candidates.Length + "; tick=" + (tick == null ? "unknown" : tick.Get_Tick.ToString()) +
                    "; day=" + (tick == null ? "unknown" : tick.Get_DayOfTheWeek.ToString()) + "; " + details;
            }
        }


        internal string TestPeerActorDistance(uint peer, Actor actor)
        {
            if (!peerPoses.TryGetValue(peer, out PeerPose? pose)) return "no pose";
            Vector3 remote = new Vector3(pose.Pose.X, pose.Pose.Y, pose.Pose.Z);
            return Vector3.Distance(remote, actor.transform.position).ToString("F2") +
                " at " + remote.ToString("F1") + "/" + actor.transform.position.ToString("F1") +
                " age=" + (Time.realtimeSinceStartup - pose.Time).ToString("F2");
        }


        internal Actor_Jacques? TestFindJacquesActor() => actorsById.OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .OfType<Actor_Jacques>()
            .FirstOrDefault(actor => !routeBoundActors.Contains(actor));


        internal Actor_Eric? TestFindActiveEricActor() => actorsById.OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .OfType<Actor_Eric>()
            .FirstOrDefault(actor => actor.isActiveAndEnabled && !routeBoundActors.Contains(actor) &&
                IsPlainBaseConversation(actor) && actor.CanTalkToHim && string.IsNullOrEmpty(actor.PlayerOption));


        internal string TestEricActivationSummary
        {
            get
            {
                Actor_Eric[] candidates = actorsById.OrderBy(pair => pair.Key)
                    .Select(pair => pair.Value).OfType<Actor_Eric>().ToArray();
                string details = string.Join("|", candidates.Select(actor =>
                    ScenePath(actor.transform) + ":activeSelf=" + actor.gameObject.activeSelf +
                    ":activeInHierarchy=" + actor.gameObject.activeInHierarchy +
                    ":enabled=" + actor.enabled + ":route=" + routeBoundActors.Contains(actor)));
                return "Actor_Eric instances=" + candidates.Length + "; " + details;
            }
        }
#endif


        private string NativeWeedSalePathReason(Actor actor)
        {
            if (actor == null) return "missing-actor";
            Type type = actor.GetType();
            if (!ReviewedNativeWeedSaleActors.Contains(type)) return "unreviewed-class";
            if (!actor.BuyWeed) return "BuyWeed-false";
            if (!ApprovedWeedSaleFriendshipEffects(actor)) return "unreviewed-sale-friendship-effects";
            bool inheritsBaseTrigger = type.GetMethod("OnTriggerEnter", BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly) == null && NativeActorGiftTrigger != null;
            if (!inheritsBaseTrigger) return "custom-or-unavailable-trigger";
            bool inheritsBaseRefusal = type.GetMethod(nameof(Actor.ThisActorWontBuyWeed), BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly) == null;
            if (!inheritsBaseRefusal) return "custom-refusal-handler";
            if ((int)actor.actor_Type <= 0 || (int)actor.actor_Type >= NpcInteractionState.MaxActorTypes)
                return "invalid-Actor_Type";
            return "eligible";
        }

        private static bool ApprovedWeedSaleFriendshipEffects(Actor actor)
        {
            if (!actor.BuyWeedGetFriendshipUp) return true;
            Type type = actor.GetType();
            MethodInfo? greeting = Method(type, nameof(Actor.Greetings), typeof(bool));
            return (type == typeof(Actor_Normand) && greeting?.DeclaringType == typeof(Actor_Normand)) ||
                (type == typeof(Actor_Raymond) && greeting?.DeclaringType == typeof(Actor));
        }

        private bool IsSafeNativeWeedSalePath(Actor actor)
        {
            return actor != null && actor.gameObject.scene.IsValid() && idsByActor.ContainsKey(actor) && NpcWeedSalePolicy.IsSafeHostSalePath(
                ReviewedNativeWeedSaleActors.Contains(actor.GetType()), actor.BuyWeed, actor.BuyWeedGetFriendshipUp,
                actor.GetType().GetMethod("OnTriggerEnter", BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly) == null && NativeActorGiftTrigger != null,
                actor.GetType().GetMethod(nameof(Actor.ThisActorWontBuyWeed), BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly) == null,
                (int)actor.actor_Type, ApprovedWeedSaleFriendshipEffects(actor));
        }

        private bool IsSafeNativeWeedRefusalPath(Actor actor)
        {
            if (actor == null || !idsByActor.ContainsKey(actor) || !actor.gameObject.scene.IsValid() || actor.BuyWeed ||
                actor.GetType().Assembly != typeof(Actor).Assembly || (int)actor.actor_Type <= 0) return false;
            var trigger = actor.GetType().GetMethod("OnTriggerEnter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            // Private base Unity callbacks are inherited even when reflection on the derived type finds none.
            if (trigger != null && trigger.DeclaringType != typeof(Actor)) return false;
            var refusal = Method(actor.GetType(), nameof(Actor.ThisActorWontBuyWeed), typeof(InteractableItems));
            return refusal?.DeclaringType == typeof(Actor) ||
                (actor.GetType() == typeof(Actor_Eric) && refusal?.DeclaringType == typeof(Actor_Eric)) ||
                (actor.GetType() == typeof(Actor_Police) && refusal?.DeclaringType == typeof(Actor_Police));
        }


#if MONMP_DEVELOPMENT
        internal bool TestIsSafeWeedSaleActor(Actor actor) => actor != null && actor.isActiveAndEnabled &&
            !routeBoundActors.Contains(actor) && IsSafeNativeWeedSalePath(actor);


        private string TestWeedSaleCandidateReason(Actor actor)
        {
            if (actor == null) return "destroyed";
            if (!actor.gameObject.scene.IsValid()) return "invalid-scene";
            if (!actor.gameObject.activeSelf) return "inactive-self";
            if (!actor.gameObject.activeInHierarchy) return "inactive-parent";
            if (!actor.isActiveAndEnabled) return "disabled";
            if (routeBoundActors.Contains(actor)) return "route-bound";
            return NativeWeedSalePathReason(actor);
        }
#endif


        // Fixture-only preflight mirrors the native buyer's current daily and
        // friendship gates. NeedToSleepToSellWeedAgain controls how the game
        // resets BoughtWeedToday; it is not itself a pre-sale requirement.

#if MONMP_DEVELOPMENT
        internal string TestWeedSaleFixtureDailyEligibility(Actor actor)
        {
            if (actor == null) return "destroyed";
            int index = (int)actor.actor_Type;
            if (index <= 0 || index >= GameManager.Get_Friendship.Length ||
                index >= GameManager.Get_Friendship_BoughtWeedToday.Length)
                return "invalid-native-daily-state";
            if (GameManager.Get_Friendship_BoughtWeedToday[index])
                return actor.NeedToSleepToSellWeedAgain
                    ? "already-bought-today-needs-native-sleep"
                    : "already-bought-today";
            if (GameManager.Get_Friendship[index] < actor.MinimumFriendshipToBuyWeed)
                return "below-minimum-friendship";
            return "eligible";
        }


        internal Actor? TestFindSafeWeedSaleActor() => actorsById.OrderBy(pair => SaleFixturePriority(pair.Value))
            .ThenBy(pair => pair.Key).Select(pair => pair.Value)
            .FirstOrDefault(actor => TestIsSafeWeedSaleActor(actor) && TestWeedSaleFixtureDailyEligibility(actor) == "eligible");


        internal Actor? TestFindWeedSaleFixtureActor(Transform expectedLocalPlayer, float safetyMargin, out string selectionDetails)
        {
            Actor? activeBuyer = TestFindSafeWeedSaleActor();
            if (activeBuyer != null)
            {
                selectionDetails = "already-active; id=" + ActorId(activeBuyer) + "; type=" + activeBuyer.GetType().Name;
                return activeBuyer;
            }

            var rejectedActivations = new List<string>();
            foreach (Actor candidate in actorsById.OrderBy(pair => SaleFixturePriority(pair.Value))
                .ThenBy(pair => pair.Key).Select(pair => pair.Value))
            {
                if (candidate == null || !candidate.gameObject.scene.IsValid() || routeBoundActors.Contains(candidate) ||
                    NativeWeedSalePathReason(candidate) != "eligible" ||
                    TestWeedSaleFixtureDailyEligibility(candidate) != "eligible") continue;
                if (TryFindNormalActivationSource(candidate, expectedLocalPlayer, safetyMargin,
                    out _, out string sourceDetails))
                {
                    selectionDetails = "minute-event proximity activation; id=" + ActorId(candidate) +
                        "; type=" + candidate.GetType().Name + "; " + sourceDetails;
                    return candidate;
                }
                rejectedActivations.Add(ActorId(candidate) + ":" + candidate.GetType().Name + ":" + sourceDetails);
            }

            selectionDetails = "no active buyer or source-backed distance activation; activationChecks=" +
                string.Join("|", rejectedActivations) + "; candidates=" + TestWeedSaleCandidateSummary;
            return null;
        }
#endif


        // Dealer groups sit in their store's open-hours objects. When a closed store is the only thing
        // hiding an eligible buyer's distance controller, the fixture opens it through the native
        // lifecycle (which store presence then mirrors to guests) and closes it again afterwards.

#if MONMP_DEVELOPMENT
        private readonly List<Stores> fixtureOpenedStores = new List<Stores>();


        private float? fixtureOriginalTime;


        internal string TestOpenStoresGatingWeedSaleBuyers()
        {
            if (!host) return "guest";
            var opened = new List<string>();
            var stores = UnityEngine.Object.FindObjectsOfType<Stores>(true);
            foreach (Actor candidate in actorsById.Values)
            {
                if (candidate == null || !candidate.gameObject.scene.IsValid() || routeBoundActors.Contains(candidate) ||
                    NativeWeedSalePathReason(candidate) != "eligible" || TestWeedSaleFixtureDailyEligibility(candidate) != "eligible") continue;
                foreach (var controller in UnityEngine.Object.FindObjectsOfType<DisableWhenPlayerTooFar>(true))
                {
                    if (controller == null || controller.gameObject.activeInHierarchy || !IsDisabledTargetAncestor(candidate.transform, controller)) continue;
                    foreach (var store in stores)
                    {
                        if (store == null || store.isOpen || fixtureOpenedStores.Contains(store) || !store.EnableWhenStoreIsOpen.Any(gate => gate != null &&
                            (controller.transform == gate.transform || controller.transform.IsChildOf(gate.transform)))) continue;
                        // StoreManagers closes an open store every minute outside its hours, so the
                        // host clock moves into them first; restore puts the original time back.
                        var sky = UniStorm.UniStormSystem.Instance;
                        if (sky != null && store.StoreClosingHour > store.StoreOpeningHour)
                        {
                            fixtureOriginalTime ??= sky.m_TimeFloat;
                            sky.m_TimeFloat = Mathf.Min(store.StoreOpeningHour + 30, store.StoreClosingHour - 1) / 1440f;
                        }
                        store.OpenStore();
                        fixtureOpenedStores.Add(store);
                        opened.Add(ScenePath(store.transform));
                    }
                }
            }
            return opened.Count == 0 ? "none" : string.Join(",", opened);
        }
#endif

        // A frozen fixture clock raises no minute event, so run the native minute tick of the
        // distance controllers that own this actor, exactly as the next game minute would.
        private static readonly MethodInfo? NativeDistanceTick = AccessTools.Method(typeof(DisableWhenPlayerTooFar), "Tick");

#if MONMP_DEVELOPMENT
        internal void TestTickDistanceControllers(Actor actor)
        {
            if (actor == null || NativeDistanceTick == null) return;
            int minute = UniStorm.UniStormSystem.Instance == null ? 0 : UniStorm.UniStormSystem.Instance.Hour * 60 + UniStorm.UniStormSystem.Instance.Minute;
            foreach (var controller in UnityEngine.Object.FindObjectsOfType<DisableWhenPlayerTooFar>(true))
                if (controller != null && controller.isActiveAndEnabled && IsDisabledTargetAncestor(actor.transform, controller))
                    NativeDistanceTick.Invoke(controller, new object[] { minute });
        }


        internal string TestDistanceControllerSummary(Actor actor)
        {
            if (actor == null) return "no-actor";
            var parts = new List<string>();
            foreach (var controller in UnityEngine.Object.FindObjectsOfType<DisableWhenPlayerTooFar>(true))
            {
                if (controller == null || !IsDisabledTargetAncestor(actor.transform, controller)) continue;
                var player = NativeDistanceDisablePlayer?.GetValue(controller) as Transform;
                parts.Add(controller.name + ":live=" + controller.isActiveAndEnabled + ":player=" + (player == null ? "null" : player.name + "@" + player.position.ToString("F1")) +
                    ":dist=" + (player == null ? "n/a" : Vector3.Distance(controller.transform.position, player.position).ToString("F1")) +
                    ":gate=" + (player == null ? "n/a" : MonMP.Plugin.Patches.DistanceGatedPresencePatch.GateDistance(controller.transform.position, player.position).ToString("F1")) +
                    ":actorSelf=" + actor.gameObject.activeSelf);
            }
            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }


        internal void TestRestoreFixtureStores()
        {
            var sky = UniStorm.UniStormSystem.Instance;
            if (fixtureOriginalTime is float time && sky != null) sky.m_TimeFloat = time;
            fixtureOriginalTime = null;
            foreach (var store in fixtureOpenedStores)
                if (store != null && store.isOpen) store.CloseStore();
            fixtureOpenedStores.Clear();
        }


        internal bool TestCanActivateWeedSaleActor(Actor actor, Transform expectedLocalPlayer, float safetyMargin,
            out Vector3 playerAnchor, out string details)
        {
            playerAnchor = default;
            if (actor == null)
            { details = "actor is missing"; return false; }
            string pathReason = NativeWeedSalePathReason(actor);
            string dailyReason = TestWeedSaleFixtureDailyEligibility(actor);
            if (!actor.gameObject.scene.IsValid() || routeBoundActors.Contains(actor) ||
                pathReason != "eligible" || dailyReason != "eligible")
            { details = "actor is invalid, route-bound or ineligible; path=" + pathReason + "; daily=" + dailyReason; return false; }
            if (TestIsSafeWeedSaleActor(actor))
            { playerAnchor = actor.transform.position; details = "already active and non-route"; return true; }
            return TryFindNormalActivationSource(actor, expectedLocalPlayer, safetyMargin, out playerAnchor, out details);
        }


        internal void TestPublishWeedSaleFixtureSpeaker(Actor actor)
        {
            if (!host || actor == null || !idsByActor.TryGetValue(actor, out ulong actorId) || actorId == 0 ||
                !actor.gameObject.scene.IsValid() || routeBoundActors.Contains(actor) ||
                NativeWeedSalePathReason(actor) != "eligible" || TestWeedSaleFixtureDailyEligibility(actor) != "eligible")
                throw new InvalidOperationException("NPC weed-sale fixture cannot publish an ineligible buyer identity.");
            ulong sequence = current.EventSequence == ulong.MaxValue ? 1 : current.EventSequence + 1;
            current = Capture(sequence, actorId, "");
            ValidateNativeLayout(current);
            channel?.Publish(NpcInteractionState.Entity, current.Encode());
        }


        private static bool TryFindNormalActivationSource(Actor actor, Transform? expectedLocalPlayer, float safetyMargin,
            out Vector3 playerAnchor, out string details)
        {
            playerAnchor = default;
            var candidates = UnityEngine.Object.FindObjectsOfType<DisableWhenPlayerTooFar>(true)
                .Where(source => source != null && IsDisabledTargetAncestor(actor.transform, source))
                .OrderBy(source => Vector3.Distance(source.transform.position, actor.transform.position));
            string lastReason = !actor.gameObject.activeSelf
                ? "actor-activeSelf-false; a parent-only distance target cannot activate this child"
                : "no DisableWhenPlayerTooFar target owns this actor";
            foreach (DisableWhenPlayerTooFar source in candidates)
            {
                Transform? assignedPlayer = NativeDistanceDisablePlayer?.GetValue(source) as Transform;
                float radius = NativeDistanceDisableRadius?.GetValue(source) is float value ? value : float.NaN;
                float actorDistance = Vector3.Distance(source.transform.position, actor.transform.position);
                GameObject[] targets = DistanceDisableTargets(source);
                bool targetsActorSelf = targets.Any(target => target == actor.gameObject);
                string candidateDetails = "controller=" + ScenePath(source.transform) +
                    "; enabled=" + source.enabled + "; activeSelf=" + source.gameObject.activeSelf +
                    "; activeHierarchy=" + source.gameObject.activeInHierarchy +
                    "; controllerChain=" + HierarchyActivity(source.transform) +
                    "; actorChain=" + HierarchyActivity(actor.transform) +
                    "; started=" + (assignedPlayer != null) +
                    "; playerBound=" + (expectedLocalPlayer == null || assignedPlayer == expectedLocalPlayer) +
                    "; range=" + radius.ToString("F1") + "; actorDistance=" + actorDistance.ToString("F1") +
                    "; safetyMargin=" + safetyMargin.ToString("F1") + "; actorActiveSelf=" + actor.gameObject.activeSelf +
                    "; targetsActorSelf=" + targetsActorSelf;
                if (!source.enabled || !source.gameObject.activeInHierarchy)
                { lastReason = candidateDetails + "; reason=controller-inactive-in-hierarchy"; continue; }
                if (!actor.gameObject.activeSelf && !targetsActorSelf)
                { lastReason = candidateDetails + "; reason=actor-activeSelf-false-and-controller-only-toggles-ancestor"; continue; }
                if (assignedPlayer == null)
                { lastReason = candidateDetails + "; reason=controller-Start-not-run"; continue; }
                if (expectedLocalPlayer != null && assignedPlayer != expectedLocalPlayer)
                { lastReason = candidateDetails + "; reason=controller-bound-to-different-player"; continue; }
                if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0 || radius > 100000)
                { lastReason = candidateDetails + "; reason=invalid-distance"; continue; }
                if (actorDistance + safetyMargin >= radius)
                { lastReason = candidateDetails + "; reason=actor-outside-activation-margin"; continue; }

                playerAnchor = actor.transform.position;
                details = candidateDetails + "; eligible=true";
                return true;
            }
            details = lastReason + "; eligible=false; controllers=" + TestWeedSaleActivationSummary(actor);
            return false;
        }
#endif


        private static GameObject[] DistanceDisableTargets(DisableWhenPlayerTooFar source) =>
            NativeDistanceDisableTargets?.GetValue(source) as GameObject[] ?? Array.Empty<GameObject>();

        private static string HierarchyActivity(Transform target)
        {
            var nodes = new List<string>();
            for (Transform? current = target; current != null; current = current.parent)
                nodes.Add(current.name + ":self=" + current.gameObject.activeSelf + ":hierarchy=" + current.gameObject.activeInHierarchy);
            nodes.Reverse();
            return string.Join("/", nodes);
        }

        private static bool IsDisabledTargetAncestor(Transform actor, DisableWhenPlayerTooFar source)
        {
            if (actor == null || source == null) return false;
            GameObject[] targets = DistanceDisableTargets(source);
            return targets.Any(target => target != null &&
                (actor == target.transform || actor.IsChildOf(target.transform)));
        }


#if MONMP_DEVELOPMENT
        private static string TestWeedSaleActivationSummary(Actor actor)
        {
            if (actor == null) return "destroyed";
            var sources = UnityEngine.Object.FindObjectsOfType<DisableWhenPlayerTooFar>(true)
                .Where(source => source != null && IsDisabledTargetAncestor(actor.transform, source))
                .Select(source =>
                {
                    Transform? player = NativeDistanceDisablePlayer?.GetValue(source) as Transform;
                    float range = NativeDistanceDisableRadius?.GetValue(source) is float value ? value : float.NaN;
                    string targets = string.Join(",", DistanceDisableTargets(source).Where(target => target != null)
                        .Select(target => ScenePath(target.transform) + ":targetsActorSelf=" + (target == actor.gameObject)));
                    return ScenePath(source.transform) + ":enabled=" + source.enabled + ":activeSelf=" +
                        source.gameObject.activeSelf + ":activeHierarchy=" + source.gameObject.activeInHierarchy +
                        ":controllerChain=" + HierarchyActivity(source.transform) +
                        ":actorChain=" + HierarchyActivity(actor.transform) + ":playerInitialized=" + (player != null) +
                        ":range=" + range.ToString("F1") + ":actorDistance=" +
                        Vector3.Distance(source.transform.position, actor.transform.position).ToString("F1") +
                        ":actorActiveSelf=" + actor.gameObject.activeSelf + ":targets=" + targets;
                }).ToArray();
            return sources.Length == 0 ? "none" : string.Join(",", sources);
        }


        private static int SaleFixturePriority(Actor? actor) => actor == null ? int.MaxValue :
            actor.GetType() == typeof(Actor_Luc) ? 0 :
            actor.GetType() == typeof(Actor_Twitch) ? 1 : actor.GetType() == typeof(Actor_Martin) ? 2 :
            actor.GetType() == typeof(Actor_Tom) ? 3 : 4;


        internal bool TestBoughtWeedToday(Actor actor) => actor != null &&
            GameManager.Get_Friendship_BoughtWeedToday[(int)actor.actor_Type];


        internal bool TestWantBigWeedPack(Actor actor) => actor != null && NativeWantBigPack?.GetValue(actor) is bool wantsBig && wantsBig;


        internal void TestSetWantBigWeedPack(Actor actor, bool wantsBig)
        {
            if (!host || actor == null || !TestIsSafeWeedSaleActor(actor) || NativeWantBigPack == null)
                throw new InvalidOperationException("NPC weed-sale fixture cannot set this actor's pack preference.");
            NativeWantBigPack.SetValue(actor, wantsBig);
        }


        internal void TestRestoreWantBigWeedPack(Actor actor, bool wantsBig)
        {
            if (!host || actor == null || routeBoundActors.Contains(actor) || !IsSafeNativeWeedSalePath(actor) || NativeWantBigPack == null)
                throw new InvalidOperationException("NPC weed-sale fixture cannot restore this actor's pack preference.");
            NativeWantBigPack.SetValue(actor, wantsBig);
        }


        internal void TestTriggerNativeWeedSale(Actor actor, Collider itemCollider)
        {
            if (NativeActorGiftTrigger == null) throw new InvalidOperationException("Native Actor.OnTriggerEnter(Collider) is unavailable.");
            NativeActorGiftTrigger.Invoke(actor, new object[] { itemCollider });
        }


        internal void TestResetLastWeedSaleResult() => testLastWeedSaleResult = null;


        internal bool TestIsSafeActor(Actor actor) => actor != null && actor.isActiveAndEnabled && !routeBoundActors.Contains(actor) &&
            IsPlainBaseConversation(actor) && actor.CanTalkToHim && string.IsNullOrEmpty(actor.PlayerOption);
#endif


        internal ulong ActorId(Actor actor) => idsByActor.TryGetValue(actor, out ulong id) ? id : 0;


        internal Actor? ActorById(ulong id) => actorsById.TryGetValue(id, out Actor actor) ? actor : null;


        internal bool PeerNear(uint peer, Vector3 position, float maxDistance)
        {
            if (!peerPoses.TryGetValue(peer, out PeerPose? pose)) return false;
            float age = Time.realtimeSinceStartup - pose.Time;
            if (age < 0 || age > 3f) return false;
            Vector3 remote = new Vector3(pose.Pose.X, pose.Pose.Y, pose.Pose.Z);
            return (remote - position).sqrMagnitude <= maxDistance * maxDistance;
        }


#if MONMP_DEVELOPMENT
        internal void TestSetFixtureState(Actor actor, int friendship, bool talkedToday, bool markIdentity = false)
        {
            if (!host || actor == null || !idsByActor.ContainsKey(actor) || friendship < 0 || friendship > 49)
                throw new InvalidOperationException("NPC fixture state is invalid or not host-owned.");
            int index = (int)actor.actor_Type;
            if (index <= 0 || index >= GameManager.Get_Friendship.Length)
                throw new InvalidOperationException("NPC fixture actor type is outside the friendship table.");
            GameManager.Get_Friendship[index] = friendship;
            GameManager.Get_Friendship_CanBeFriendWithThisAcor[index] = true;
            GameManager.Get_Friendship_TalkedToday[index] = talkedToday;
            GameManager.Get_Friendship_BoughtWeedToday[index] = false;
            GameManager.Get_Friendship_SyrupGiftedToday[index] = false;
            if (markIdentity)
            {
                ulong sequence = current.EventSequence == ulong.MaxValue ? 1 : current.EventSequence + 1;
                current = Capture(sequence, idsByActor[actor], "");
                ValidateNativeLayout(current);
                channel?.Publish(NpcInteractionState.Entity, current.Encode());
            }
            else PublishStatsIfChanged();
        }


        internal void TestRestoreFixtureState(NpcInteractionState state)
        {
            if (!host) throw new InvalidOperationException("Only the host can restore the NPC fixture state.");
            ValidateNativeLayout(state); ApplyNative(state); current = state;
            channel?.Publish(NpcInteractionState.Entity, current.Encode());
        }


        internal void TestClickNativeActor(Actor actor) => actor.MouseDown();


        internal void TestSpitNativeActor(Actor actor) => actor.PlayerSpitOn();


        internal void TestResetLastResult() => testLastResult = null;


        internal bool TestGiftedToday(Actor actor) => actor != null && GameManager.Get_Friendship_SyrupGiftedToday[(int)actor.actor_Type];


        internal void TestTriggerNativeGift(Actor actor, Collider itemCollider)
        {
            if (NativeActorGiftTrigger == null) throw new InvalidOperationException("Native Actor.OnTriggerEnter(Collider) is unavailable.");
            NativeActorGiftTrigger.Invoke(actor, new object[] { itemCollider });
        }


        internal void TestResetLastGiftResult() { testLastGiftResult = null; testLastGiftDiagnostics = ""; }
#endif


        // Weed packs and gifts both arrive through Actor.OnTriggerEnter. Route
        // the sale path first, then retain the established gift path unchanged.
        internal bool BeginActorItemTrigger(Actor actor, Collider other)
        {
            if (TryNativeWeedSaleItem(other, out InteractableItems saleItem, out NpcWeedPackKind saleKind))
                return BeginWeedSaleTrigger(actor, saleItem, saleKind);
            return BeginGiftTrigger(actor, other);
        }

        private bool BeginWeedSaleTrigger(Actor actor, InteractableItems item, NpcWeedPackKind kind)
        {
            if (disposed || session?.IsRunning != true) return true;
            if (host)
            {
                if (authorizedWeedSaleActor == actor && authorizedWeedSaleItem == item) return true;
                var items = MonMPPlugin.Instance?.World?.Items;
                return items != null && items.TryGetNpcWeedSaleIdentity(item, out _, out _, out uint nativeOwner, out _) &&
                    (nativeOwner == session!.LocalPeerId || nativeOwner == ItemLease.Unowned);
            }

            if (NewActionsBlocked || !channel!.FullySynchronized || MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            {
                ShowWeedSaleFailure(NpcWeedSaleResult.Unavailable);
                return false;
            }
            bool refusal = IsSafeNativeWeedRefusalPath(actor);
            if ((!refusal && routeBoundActors.Contains(actor)) || (!refusal && !IsSafeNativeWeedSalePath(actor)))
            {
                ShowWeedSaleFailure(NpcWeedSaleResult.UnsupportedSale);
                return false;
            }
            var sharedItems = MonMPPlugin.Instance?.World?.Items;
            if (sharedItems == null || session == null ||
                !sharedItems.TryGetNpcWeedSaleIdentity(item, out ulong itemId, out uint generation, out uint owner, out _) ||
                owner != session.LocalPeerId)
            {
                ShowWeedSaleFailure(NpcWeedSaleResult.Unavailable);
                return false;
            }
            ulong actorId = idsByActor[actor];
            var pair = (Actor: actorId, Item: itemId);
            if (completedLocalWeedSales.Contains(pair) || pendingLocalWeedSales.Contains(pair)) return false;
            if (pendingWeedSaleRequests.Count >= 64)
            {
                ShowWeedSaleFailure(NpcWeedSaleResult.Unavailable);
                return false;
            }
            if (!sharedItems.FlushLocalItemPose(item)) { ShowWeedSaleFailure(NpcWeedSaleResult.Unavailable); return false; }
            pendingLocalWeedSales.Add(pair);
            var requestId = Guid.NewGuid();
            pendingWeedSaleRequests.Add(requestId, (actorId, itemId, kind, Time.unscaledTime));
            channel.Request(NpcInteractionState.Entity,
                new NpcWeedSaleRequest(requestId, actorId, itemId, generation, kind).Encode(), true);
            return false;
        }

        private static bool TryNativeWeedSaleItem(Collider other, out InteractableItems item, out NpcWeedPackKind kind)
        {
            if (other != null && other.GetComponent<Item_WeedPreRollPack>() is Item_WeedPreRollPack small)
            { item = small; kind = NpcWeedPackKind.Small; return true; }
            if (other != null && other.GetComponent<Item_WeedPreRollBigPack>() is Item_WeedPreRollBigPack big)
            { item = big; kind = NpcWeedPackKind.Big; return true; }
            item = null!; kind = default; return false;
        }

        // A guest's local Actor trigger is a request only. The host invokes the
        // same reconstructed native handler on its item twin after validating
        // the actor, live lease generation, player pose and actual collider overlap.
        internal bool BeginGiftTrigger(Actor actor, Collider other)
        {
            if (disposed || session?.IsRunning != true) return true;
            if (!TryNativeGiftItem(other, out InteractableItems item, out NpcGiftKind kind)) return host;
            if (host)
            {
                if (authorizedGiftActor == actor && authorizedGiftItem == item) return true;
                var items = MonMPPlugin.Instance?.World?.Items;
                return items != null && items.TryGetNpcGiftIdentity(item, out _, out _, out uint nativeOwner, out _) &&
                    (nativeOwner == 0 || nativeOwner == ItemLease.Unowned);
            }

            if (NewActionsBlocked || !channel!.FullySynchronized || MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            {
                ShowGiftFailure(NpcGiftResult.Unavailable);
                return false;
            }
            if (routeBoundActors.Contains(actor) || !IsSafeNativeGiftPath(actor, kind))
            {
                ShowGiftFailure(NpcGiftResult.UnsupportedGift);
                return false;
            }
            var sharedItems = MonMPPlugin.Instance?.World?.Items;
            if (sharedItems == null || session == null ||
                !sharedItems.TryGetNpcGiftIdentity(item, out ulong itemId, out uint generation, out uint owner, out _) ||
                owner != session.LocalPeerId)
            {
                ShowGiftFailure(NpcGiftResult.Unavailable);
                return false;
            }
            ulong actorId = idsByActor[actor];
            var pair = (Actor: actorId, Item: itemId);
            if (completedLocalGifts.Contains(pair) || pendingLocalGifts.Contains(pair)) return false;
            if (pendingGiftRequests.Count >= 64) { ShowGiftFailure(NpcGiftResult.Unavailable); return false; }
            if (!sharedItems.FlushLocalItemPose(item)) { ShowGiftFailure(NpcGiftResult.Unavailable); return false; }
            pendingLocalGifts.Add(pair);
            var requestId = Guid.NewGuid();
            pendingGiftRequests.Add(requestId, (actorId, itemId, kind, Time.unscaledTime));
            channel.Request(NpcInteractionState.Entity,
                new NpcGiftRequest(requestId, actorId, itemId, generation, kind).Encode(), true);
            return false;
        }

        private static bool TryNativeGiftItem(Collider other, out InteractableItems item, out NpcGiftKind kind)
        {
            if (other != null && other.GetComponent<Item_MapleSyrupCan>() is Item_MapleSyrupCan syrup)
            { item = syrup; kind = NpcGiftKind.MapleSyrup; return true; }
            if (other != null && other.GetComponent<Item_MapleWineBottle>() is Item_MapleWineBottle wine)
            { item = wine; kind = NpcGiftKind.MapleWine; return true; }
            if (other != null && other.GetComponent<Item_HomemadeCoffeeMug>() is Item_HomemadeCoffeeMug coffee)
            { item = coffee; kind = NpcGiftKind.HomemadeCoffee; return true; }
            item = null!; kind = default; return false;
        }

        // Harmony prefix. A guest always suppresses native Actor.MouseDown; only
        // safe conversations are forwarded through the host world command path.
        internal bool BeginClick(Actor actor)
        {
            if (disposed || session?.IsRunning != true) return true;
            if (host)
            {
                if (remoteActor == actor)
                {
                    BeginCapture(actor);
                    return true;
                }
                if (IsPlainBaseConversation(actor) && actor.CanTalk() && string.IsNullOrEmpty(actor.PlayerOption)) BeginCapture(actor);
                return true;
            }
            if (NewActionsBlocked) { ShowFailure(NpcTalkResult.Unavailable); return false; }
            if (actor is Actor_Anthony)
            {
                MonMPPlugin.Instance?.World?.MeetRace?.OpenGuestAnthonyMenu(actor, idsByActor.ContainsKey(actor));
                return false;
            }
            if (!IsPlainBaseConversation(actor)) { ShowFailure(NpcTalkResult.UnsupportedConversation); return false; }
            if (!channel!.FullySynchronized)
            {
                ShowFailure(NpcTalkResult.Unavailable);
                return false;
            }
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true) return false;
            if (ReviewedChoiceActors.Contains(actor.GetType()) && pendingDialogTalks.Count != 0) return false;
            var requestId = Guid.NewGuid();
            if (pending.Count >= 64) { ShowFailure(NpcTalkResult.Unavailable); return false; }
            pending.Add(requestId, Time.unscaledTime);
            if (ReviewedChoiceActors.Contains(actor.GetType())) pendingDialogTalks[requestId] = (idsByActor[actor], Time.unscaledTime);
            channel.Request(NpcInteractionState.Entity, new NpcTalkRequest(requestId, idsByActor[actor]).Encode(), true);
            return false;
        }

        private void BeginCapture(Actor actor)
        {
            if (!idsByActor.TryGetValue(actor, out ulong id)) return;
            captureActor = actor; captureActorId = id; captureGreeting = null; completedGreeting = null;
        }

        internal void CaptureGreeting(Actor actor, string text)
        {
            if (captureActor == actor && !string.IsNullOrEmpty(text)) captureGreeting = BoundGreeting(text);
        }

        internal void EndClick(Actor actor)
        {
            if (!host || captureActor != actor) return;
            string? greeting = captureGreeting;
            ulong actorId = captureActorId;
            captureActor = null; captureActorId = 0; captureGreeting = null;
            completedGreeting = greeting;
            if (!string.IsNullOrEmpty(greeting))
            {
                ulong sequence = current.EventSequence == ulong.MaxValue ? 1 : current.EventSequence + 1;
                current = Capture(sequence, actorId, greeting);
                ValidateNativeLayout(current);
                channel?.Publish(NpcInteractionState.Entity, current.Encode());
            }
            else PublishStatsIfChanged();
        }

        private static string BoundGreeting(string text)
        {
            if (Encoding.UTF8.GetByteCount(text) <= NpcInteractionState.MaxGreetingBytes) return text;
            var result = new StringBuilder(); int bytes = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                string value;
                int charBytes;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    value = text.Substring(i, 2);
                    charBytes = 4;
                    i++;
                }
                else
                {
                    value = c.ToString();
                    charBytes = Encoding.UTF8.GetByteCount(value);
                }
                if (bytes + charBytes > NpcInteractionState.MaxGreetingBytes) break;
                result.Append(value); bytes += charBytes;
            }
            return result.ToString();
        }

        // Native host petting can happen between the sampled capture and a join's
        // activation boundary. Publish its committed result in the same callback.
        internal void HostFriendshipChanged()
        {
            if (!host || disposed || session?.IsRunning != true) return;
            PublishStatsIfChanged();
        }

        private void PublishStatsIfChanged()
        {
            if (channel == null || MatchesNativeState()) return;
            var latest = Capture(current.EventSequence, current.SpeakerId, current.Greeting);
            ValidateNativeLayout(latest);
            current = latest; channel.Publish(NpcInteractionState.Entity, current.Encode());
        }

        private void PoseReceived(uint peer, PlayerPose pose) => peerPoses[peer] = new PeerPose { Pose = pose, Time = Time.realtimeSinceStartup };
        private void PeerLeft(uint peer)
        {
            peerPoses.Remove(peer);
            dialogChoices.RemovePeer(peer);
            foreach (var key in lastReaction.Keys.Where(k => k.Peer == peer).ToArray()) lastReaction.Remove(key);
            foreach (var claim in claims.Where(key => key.Peer == peer).ToArray()) claims.Remove(claim);
            foreach (var claim in giftClaims.Where(key => key.Peer == peer).ToArray()) giftClaims.Remove(claim);
            foreach (var claim in weedSaleClaims.Where(key => key.Peer == peer).ToArray()) weedSaleClaims.Remove(claim);
            foreach (var claim in motherPhoneClaims.Where(key => key.Peer == peer).ToArray()) motherPhoneClaims.Remove(claim);
            foreach (var key in motherPhoneResults.Keys.Where(key => key.Peer == peer).ToArray()) motherPhoneResults.Remove(key);
        }

        private static ulong MotherPhoneId(Furniture_Phone phone) =>
            phone == null || !phone.gameObject.scene.IsValid() ? 0 :
            StaticId.Hash("monmp/social/mother-phone/v1/" + SharedBuildingDoors.StablePath(phone.transform));

        // A guest has already completed the native dial/ring flow. Keep the interaction on that
        // exact scene phone and leave its saved guest friendship/unlock values untouched.
        internal Guid RequestMotherPhoneCall(Furniture_Phone phone)
        {
            if (disposed || host || session?.IsRunning != true) return Guid.Empty;
            var plugin = MonMPPlugin.Instance;
            if (NewActionsBlocked || channel?.FullySynchronized != true || plugin?.World?.SaveGate.Active == true)
            { ShowMotherPhoneFailure(); return Guid.Empty; }
            ulong phoneId = MotherPhoneId(phone);
            if (phoneId == 0 || !phone.isActiveAndEnabled || !phone.CanUsePhoneButton)
            { ShowMotherPhoneFailure(); return Guid.Empty; }
            if (pendingMotherPhoneCalls.Count >= 32)
            { ShowMotherPhoneFailure(); return Guid.Empty; }
            var request = new MotherPhoneCallRequest(Guid.NewGuid(), phoneId);
            pendingMotherPhoneCalls.Add(request.RequestId, (phone, Time.unscaledTime));
            channel.Request(NpcInteractionState.Entity, request.Encode(), true);
            return request.RequestId;
        }

        private void MotherPhoneCommand(uint peer, MotherPhoneCallRequest request)
        {
            var key = (Peer: peer, Request: request.RequestId);
            if (motherPhoneResults.TryGetValue(key, out var cached))
            { channel!.Reply(peer, NpcInteractionState.Entity, cached.Encode()); return; }
            if (!motherPhoneClaims.Add(key))
            {
                ReplyMotherPhone(peer, new MotherPhoneCallReply(request.RequestId, MotherPhoneCallStatus.Unavailable,
                    MotherPhoneVoiceKind.None, 0, 0));
                return;
            }
            motherPhoneClaimOrder.Enqueue(key);
            if (motherPhoneClaimOrder.Count > 8192) motherPhoneClaims.Remove(motherPhoneClaimOrder.Dequeue());

            var plugin = MonMPPlugin.Instance;
            bool freshPose = peerPoses.TryGetValue(peer, out PeerPose? pose) &&
                pose != null &&
                Time.realtimeSinceStartup - pose.Time >= 0 && Time.realtimeSinceStartup - pose.Time <= PeerPoseMaxAge &&
                (pose.Pose.State == PlayerPose.WalkingState || pose.Pose.State == (byte)PlayerState.InFurnitureChair) &&
                pose.Pose.Rest == PlayerRest.None && !pose.Pose.Spectating;
            Furniture_Phone[] phones = UnityEngine.Object.FindObjectsOfType<Furniture_Phone>(true)
                .Where(candidate => candidate != null && candidate.isActiveAndEnabled && MotherPhoneId(candidate) == request.PhoneId)
                .Take(2).ToArray();
            Furniture_Phone? phone = phones.Length == 1 ? phones[0] : null;
            if (!host || disposed || peer == 0 || session?.Peers.ContainsKey(peer) != true || channel?.FullySynchronized != true ||
                plugin?.World?.SaveGate.Active == true || !freshPose || phone == null || pose == null ||
                (new Vector3(pose.Pose.X, pose.Pose.Y, pose.Pose.Z) - phone.transform.position).sqrMagnitude > 4f)
            {
                ReplyMotherPhone(peer, new MotherPhoneCallReply(request.RequestId, MotherPhoneCallStatus.Unavailable,
                    MotherPhoneVoiceKind.None, 0, 0));
                return;
            }

            int mother = (int)Actor_Type.Mother;
            var plan = MotherPhoneCallPlan.Create(GameManager.Get_Friendship_TalkedToday[mother],
                GameManager.Get_Friendship[mother], GameManager.Get_FryingBasketUnlocked);
            if (!plan.Applied)
            {
                ReplyMotherPhone(peer, new MotherPhoneCallReply(request.RequestId, MotherPhoneCallStatus.AlreadyTalkedToday,
                    MotherPhoneVoiceKind.None, (byte)plan.Friendship, 0));
                return;
            }

            GameManager.Get_Friendship[mother] = plan.Friendship;
            if (plan.Friendship > current.Friendship[mother])
                UnityEngine.Object.FindObjectOfType<UI_FrontLeftPopup>()?.Friendship(Actor_Type.Mother);
            if (plan.UnlockBasket)
            {
                try { UnityEngine.Object.FindObjectOfType<Furniture_Fryer>()?.BasketUnlocked(); }
                catch (Exception ex) { Debug.LogWarning("MonMP Mother phone basket unlock failed: " + ex.Message); }
                GameManager.Get_FryingBasketUnlocked = true;
            }
            GameManager.Get_Friendship_TalkedToday[mother] = true;

            MotherPhoneVoiceKind voiceKind = plan.UniqueGreeting ? MotherPhoneVoiceKind.Unique : MotherPhoneVoiceKind.Random;
            int voiceIndex = 0;
            Actor_VoiceLines? motherVoices = VoiceLinesField?.GetValue(Singleton<SOUND_ActorVoices>.i) is Actor_VoiceLines[] lines
                ? lines.FirstOrDefault(line => line != null && line.Actor_Type == Actor_Type.Mother) : null;
            if (plan.UniqueGreeting)
                voiceIndex = Mathf.Clamp(plan.Friendship - 34, 0, 16);
            else if (motherVoices?.randomClip is { Length: > 0 })
                voiceIndex = UnityEngine.Random.Range(0, motherVoices.randomClip.Length);
            PublishStatsIfChanged();
            ReplyMotherPhone(peer, new MotherPhoneCallReply(request.RequestId, MotherPhoneCallStatus.Applied,
                voiceKind, (byte)plan.Friendship, (byte)voiceIndex));
        }

        private void ReplyMotherPhone(uint peer, MotherPhoneCallReply reply)
        {
            var key = (Peer: peer, Request: reply.RequestId);
            motherPhoneResults[key] = reply; motherPhoneResultOrder.Enqueue(key);
            while (motherPhoneResultOrder.Count > 8192)
            {
                var expired = motherPhoneResultOrder.Dequeue();
                motherPhoneResults.Remove(expired);
            }
            channel?.Reply(peer, NpcInteractionState.Entity, reply.Encode());
        }

        private static void ShowMotherPhoneFailure() =>
            UnityEngine.Object.FindObjectOfType<UI_ErrorMessage>()?.Show("This call cannot be completed right now.");

        private static void PlayMotherPhoneBusy()
        {
            try
            {
                Singleton<SOUND_Manager>.i?.PlaySound_PlayerVoice_Sacre();
                Singleton<SOUND_Manager>.i?.PlaySound_Phone_Line(active: true, playLine: false);
            }
            catch (Exception ex) { Debug.LogWarning("MonMP Mother phone busy response failed: " + ex.Message); }
        }

        private static void PlayMotherPhoneReply(MotherPhoneCallReply reply)
        {
            float duration = 10f;
            try
            {
                if (reply.VoiceKind != MotherPhoneVoiceKind.None && VoiceLinesField?.GetValue(Singleton<SOUND_ActorVoices>.i) is Actor_VoiceLines[] lines)
                {
                    Actor_VoiceLines? mother = lines.FirstOrDefault(line => line != null && line.Actor_Type == Actor_Type.Mother);
                    AudioClip[]? clips = reply.VoiceKind == MotherPhoneVoiceKind.Unique ? mother?.uniqueClip : mother?.randomClip;
                    if (clips != null && reply.VoiceIndex < clips.Length && clips[reply.VoiceIndex] != null)
                    {
                        var clip = clips[reply.VoiceIndex];
                        Singleton<SOUND_Manager>.i?.Play_Actor_OneShot(clip);
                        duration = clip.length;
                    }
                }
                string greeting;
                if (reply.VoiceKind == MotherPhoneVoiceKind.Unique && UniqueMotherGreetingsField?.GetValue(null) is string[] unique &&
                    reply.Friendship < unique.Length) greeting = unique[reply.Friendship];
                else if (reply.VoiceKind == MotherPhoneVoiceKind.Random)
                    greeting = TEXT.GetActorGreetings(reply.VoiceIndex, Actor_Type.Mother);
                else greeting = TEXT.GetActorGreetings(Actor_Type.Mother);
                string name = UnityEngine.Localization.Settings.LocalizationSettings.StringDatabase.GetLocalizedString(
                    "UI_General", "FRIENDSHIP_Name_Mother", null,
                    UnityEngine.Localization.Settings.FallbackBehavior.UseProjectSettings);
                Singleton<UI_EventMessage>.i?.Show(greeting, name, duration + 3f, UI_EventMessage.EventIconType.Phone);
            }
            catch (Exception ex) { Debug.LogWarning("MonMP Mother phone reply display failed: " + ex.Message); }
        }

        [MonMP.Core.World.WorldCommandAdapters(NpcInteractionState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != NpcInteractionState.Entity || peer == 0 || channel == null || session == null ||
                payload == null || payload.Length == 0) return;
            if (payload[0] == NpcWeedSaleRequest.Marker)
            {
                if (!NpcWeedSaleRequest.Is(payload))
                { Debug.LogWarning("MonMP invalid NPC weed-sale request size or marker."); return; }
                NpcWeedSaleRequest saleRequest;
                try { saleRequest = NpcWeedSaleRequest.Decode(payload); }
                catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid NPC weed-sale request: " + ex.Message); return; }
                if (NewActionsBlocked) { ReplyWeedSaleTo(peer, saleRequest.RequestId, NpcWeedSaleResult.Unavailable); return; }
                try { WeedSaleCommand(peer, saleRequest); }
                catch (Exception ex)
                { Debug.LogException(ex); ReplyWeedSaleTo(peer, saleRequest.RequestId, NpcWeedSaleResult.Unavailable); }
                return;
            }
            if (NpcGiftRequest.HasMarker(payload))
            {
                if (!NpcGiftRequest.Is(payload))
                { Debug.LogWarning("MonMP invalid NPC gift request size or marker."); return; }
                NpcGiftRequest giftRequest;
                try { giftRequest = NpcGiftRequest.Decode(payload); }
                catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid NPC gift request: " + ex.Message); return; }
                if (NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true)
                { ReplyGiftTo(peer, giftRequest.RequestId, NpcGiftResult.Unavailable); return; }
                try { GiftCommand(peer, giftRequest); }
                catch (Exception ex)
                { Debug.LogException(ex); ReplyGiftTo(peer, giftRequest.RequestId, NpcGiftResult.Unavailable); }
                return;
            }
            if (payload[0] == ReactionMarker)
            {
                if (NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;
                if (payload.Length == ReactionSize) ReactionCommand(peer, BitConverter.ToUInt64(payload, 1), payload[9]);
                return;
            }
            if (payload[0] == MotherPhoneCallRequest.Marker)
            {
                MotherPhoneCallRequest motherRequest;
                try { motherRequest = MotherPhoneCallRequest.Decode(payload); }
                catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid Mother phone request: " + ex.Message); return; }
                MotherPhoneCommand(peer, motherRequest);
                return;
            }
            if (payload[0] == NpcDialogChoiceRequest.Marker)
            {
                try { DialogChoiceCommand(peer, NpcDialogChoiceRequest.Decode(payload)); }
                catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid NPC dialog choice: " + ex.Message); }
                return;
            }
            if (!NpcTalkRequest.Is(payload)) return;
            var request = NpcTalkRequest.Decode(payload);
            TalkCommand(peer, request);
        }

        private void TalkCommand(uint peer, NpcTalkRequest request)
        {
            if (!host || peer == 0 || channel == null || session == null) return;
            if (NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            { ReplyTo(peer, request.RequestId, NpcTalkResult.Unavailable); return; }
            var claim = (Peer: peer, Request: request.RequestId);
            if (!claims.Add(claim))
            { ReplyTo(peer, request.RequestId, NpcTalkResult.DuplicateRequest); return; }
            claimOrder.Enqueue(claim);
            if (claimOrder.Count > 8192) claims.Remove(claimOrder.Dequeue());
            actorsById.TryGetValue(request.ActorId, out Actor actor);
            bool safeConversation = actor != null && IsPlainBaseConversation(actor);
            bool active = actor != null && actor.isActiveAndEnabled;
            bool fresh = peerPoses.TryGetValue(peer, out PeerPose? pose) && Time.realtimeSinceStartup - pose.Time >= 0 && Time.realtimeSinceStartup - pose.Time <= 3f;
            PlayerPose playerPose = fresh ? pose!.Pose : default;
            // #166: a passenger talking to Martin as he drives: their pose trails the car, so measure from their seat.
            if (fresh && actor != null && MonMPPlugin.Instance?.World?.MartinCar?.SeatFor(peer) is Transform seat &&
                (seat.position - actor.transform.position).sqrMagnitude <= 3f * 3f)
                playerPose = new PlayerPose(seat.position.x, seat.position.y, seat.position.z, playerPose.Yaw, playerPose.Pitch);
            NpcTalkResult decision = NpcTalkPolicy.ValidateHostRequest(safeConversation, active, fresh,
                safeConversation && active && actor!.CanTalk(), safeConversation && active && !ReviewedChoiceActors.Contains(actor!.GetType()) && !string.IsNullOrEmpty(actor.PlayerOption), playerPose,
                actor == null ? 0 : actor.transform.position.x, actor == null ? 0 : actor.transform.position.y,
                actor == null ? 0 : actor.transform.position.z, 4f);

#if MONMP_DEVELOPMENT
            testLastResult = decision;
#endif

            if (decision != NpcTalkResult.Applied)
            { ReplyTo(peer, request.RequestId, decision); return; }

            int friendshipBeforeTalk = GameManager.Get_Friendship[(int)actor!.actor_Type];
            completedGreeting = null; remoteActor = actor; remoteDialogOffered = false; remoteGillesWindowOpened = false;
            try
            {
                using (Patches.RemoteActionScope.Enter())
                using (SpatialSounds.AtPeer(peer)) actor!.MouseDown();
            }
            catch (Exception ex)
            {
                Debug.LogError("MonMP host NPC conversation failed: " + ex);
                ReplyTo(peer, request.RequestId, NpcTalkResult.Unavailable); return;
            }
            finally { remoteActor = null; }
            bool fullFriendshipAward = GameManager.Get_Friendship[(int)actor!.actor_Type] > friendshipBeforeTalk &&
                GameManager.IsPlayerFullFriendshipWithEveryone;
            if (remoteGillesWindowOpened)
            {
                channel.Reply(peer, NpcInteractionState.Entity, new NpcDialogReply(request.RequestId, request.ActorId,
                    Guid.NewGuid(), current.EventSequence, NpcDialogStatus.Applied, fullFriendshipAward).Encode());
                completedGreeting = null; return;
            }
            if (remoteDialogOffered && !string.IsNullOrEmpty(completedGreeting))
            {
                Guid token = dialogChoices.Issue(peer, request.ActorId, Time.unscaledTime);
                if (token != Guid.Empty)
                {
                    foreach (var part in NpcDialogGreetingPart.Split(request.RequestId, completedGreeting!))
                        channel.Reply(peer, NpcInteractionState.Entity, part.Encode());
                    channel.Reply(peer, NpcInteractionState.Entity, new NpcDialogReply(request.RequestId, request.ActorId, token,
                        current.EventSequence, NpcDialogStatus.Offered, fullFriendshipAward).Encode());
                    completedGreeting = null; return;
                }
            }
            ReplyTo(peer, request.RequestId, string.IsNullOrEmpty(completedGreeting) ? NpcTalkResult.Unavailable : NpcTalkResult.Applied, fullFriendshipAward);
            completedGreeting = null;
        }

        private void WeedSaleCommand(uint peer, NpcWeedSaleRequest request)
        {
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            { ReplyWeedSaleTo(peer, request.RequestId, NpcWeedSaleResult.Unavailable); return; }
            var claim = (Peer: peer, Request: request.RequestId);
            if (!weedSaleClaims.Add(claim)) { ReplyWeedSaleTo(peer, request.RequestId, NpcWeedSaleResult.DuplicateRequest); return; }
            weedSaleClaimOrder.Enqueue(claim);
            if (weedSaleClaimOrder.Count > 8192) weedSaleClaims.Remove(weedSaleClaimOrder.Dequeue());

            actorsById.TryGetValue(request.ActorId, out Actor actor);
            bool active = actor != null && actor.isActiveAndEnabled;
            bool safeSalePath = actor != null && !routeBoundActors.Contains(actor) && IsSafeNativeWeedSalePath(actor);
            var sharedItems = MonMPPlugin.Instance?.World?.Items;
            InteractableItems? item = null;
            bool itemOwnedAndCurrent = sharedItems != null &&
                sharedItems.HostAuthorizeNpcWeedSaleItem(peer, request.ItemId, request.ItemGeneration, request.Kind, out item);
            bool fresh = peerPoses.TryGetValue(peer, out PeerPose? pose) &&
                Time.realtimeSinceStartup - pose.Time >= 0 && Time.realtimeSinceStartup - pose.Time <= 3f;
            PlayerPose playerPose = fresh ? pose!.Pose : default;
            // HostAuthorize settles the latest accepted hand pose. Native giving/selling
            // is an item-trigger contact; the actor's pivot is not a reach boundary.
            Vector3 contactPosition = item == null ? Vector3.zero : item.transform.position;
            bool itemNearPeer = sharedItems != null &&
                sharedItems.HostItemInRange(peer, request.ItemId, NpcGiftPolicy.HeldItemReach);
            if (actor != null && IsSafeNativeWeedRefusalPath(actor))
            {
                var refusal = NpcWeedSalePolicy.ValidateHostRequest(true, active, fresh, itemOwnedAndCurrent,
                    true, false, true, itemNearPeer,
                    item != null && sharedItems!.HostNpcWeedSaleItemOverlapsActor(actor, item), playerPose,
                    contactPosition.x, contactPosition.y, contactPosition.z, NpcGiftPolicy.HeldItemReach);
                if (refusal != NpcWeedSaleResult.Applied) { ReplyWeedSaleTo(peer, request.RequestId, refusal); return; }
                WeedRefusalCommand(peer, request, actor, item!);
                return;
            }
            int index = actor == null ? -1 : (int)actor.actor_Type;
            bool validIndex = index > 0 && index < GameManager.Get_Friendship_BoughtWeedToday.Length;
            bool actorCanBuy = safeSalePath && active && validIndex && actor!.BuyWeed &&
                GameManager.Get_Friendship[index] >= actor.MinimumFriendshipToBuyWeed;
            bool alreadyBought = validIndex && GameManager.Get_Friendship_BoughtWeedToday[index];
            bool wantsBig = actor != null && NativeWantBigPack?.GetValue(actor) is bool preference && preference;
            bool expectedPackSize = actor != null && (request.Kind == NpcWeedPackKind.Big) == wantsBig;
            bool itemOverlapsActor = itemOwnedAndCurrent && actor != null && item != null &&
                sharedItems!.HostNpcWeedSaleItemOverlapsActor(actor, item);
            NpcWeedSaleResult decision = NpcWeedSalePolicy.ValidateHostRequest(safeSalePath, active, fresh,
                itemOwnedAndCurrent, actorCanBuy, alreadyBought, expectedPackSize, itemNearPeer, itemOverlapsActor,
                playerPose, contactPosition.x, contactPosition.y, contactPosition.z, NpcGiftPolicy.HeldItemReach);

#if MONMP_DEVELOPMENT
            testLastWeedSaleResult = decision;
#endif

            // Once geometry/ownership pass, execute native refusals too. These exact
            // buyers only show an error; the native handler retains its gate ordering.
            bool nativeRefusal = decision == NpcWeedSaleResult.NotFriendEnough ||
                decision == NpcWeedSaleResult.NotBuyingToday || decision == NpcWeedSaleResult.WrongPackSize;
            if (decision != NpcWeedSaleResult.Applied && !nativeRefusal)
            { ReplyWeedSaleTo(peer, request.RequestId, decision); return; }

            bool nativeReturned = false;
            authorizedWeedSaleActor = actor;
            authorizedWeedSaleItem = item;
            int friendshipBeforeSale = GameManager.Get_Friendship[index];
            float incomeBeforeSale = GameManager.Get_PlayerStats[3];
            var cash = Singleton<Cash_Manager>.i;
            int cashBeforeSale = cash.GetCash;
            BeginCapture(actor!);
            try
            {
                using (Patches.RemoteActionScope.Enter())
                using (SpatialSounds.AtPeer(peer))
                    NativeActorGiftTrigger!.Invoke(actor, new object[] { item!.itemCollider });
                nativeReturned = true;
            }
            catch (Exception ex)
            {
                Debug.LogError("MonMP host NPC weed sale failed: " + ex);
            }
            finally
            {
                // Native income stays in the shared host total; the receipt does not add it again.
                authorizedWeedSaleActor = null;
                authorizedWeedSaleItem = null;
                EndClick(actor!);
            }
            bool saleApplied = nativeReturned && !nativeRefusal && GameManager.Get_Friendship_BoughtWeedToday[index];
            int incomeCents = Math.Max(0, cash.GetCash - cashBeforeSale);
            // Native cash credits precede its UI and counter lines. A presentation
            // exception must not omit an already committed payout from shared totals.
            GameManager.Get_PlayerStats[3] = Math.Max(GameManager.Get_PlayerStats[3], incomeBeforeSale + incomeCents);
            if (saleApplied) sharedItems!.HostConsumeNpcItem(peer, request.ItemId, request.ItemGeneration);
            ReplyWeedSaleTo(peer, request.RequestId, saleApplied ? NpcWeedSaleResult.Applied :
                nativeReturned && nativeRefusal ? decision : NpcWeedSaleResult.Unavailable,
                saleApplied && GameManager.Get_Friendship[index] > friendshipBeforeSale && GameManager.IsPlayerFullFriendshipWithEveryone, incomeCents);
            completedGreeting = null;
        }

        private void WeedRefusalCommand(uint peer, NpcWeedSaleRequest request, Actor actor, InteractableItems item)
        {
            NpcWeedSaleResult result = NpcWeedSaleResult.Unavailable;
            BeginCapture(actor);
            try
            {
                using var quiet = Patches.RemoteActionScope.Enter();
                using var spatial = SpatialSounds.AtPeer(peer);
                if (actor is Actor_Police police)
                {
                    // The native police body, preserving the host's own hands and joint inventory.
                    if (PoliceSpot?.GetValue(police) is not Police_SpotDetails spot || spot == null) return;
                    police.TalkOverHead(TEXT.GetText("ACTOR_Police_DoNotBuyWeed"));
                    Singleton<Gameplay>.i.Player.CheckIfHoldingItemToCancel(item);
                    UnityEngine.Object.Destroy(item.gameObject);
                    result = NpcWeedSaleResult.Confiscated;
                    MonMPPlugin.Instance?.World?.Items.HostConsumeNpcItem(peer, request.ItemId, request.ItemGeneration);
                    using var fine = NativePoliceFinePresentation.Begin(peer, ReplyPoliceFine);
                    Singleton<Gameplay>.i.Store_PoliceInterceptor.Ticket_Drug(spot);
                }
                else
                {
                    actor.ThisActorWontBuyWeed(item); // Base overhead refusal, or Eric's native voice plus refusal.
                    result = NpcWeedSaleResult.Refused;
                }
            }
            catch (Exception ex) { Debug.LogWarning("MonMP native NPC weed refusal failed: " + ex.Message); }
            finally { EndClick(actor); ReplyWeedSaleTo(peer, request.RequestId, result); }
        }

        private void GiftCommand(uint peer, NpcGiftRequest request)
        {
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            { ReplyGiftTo(peer, request.RequestId, NpcGiftResult.Unavailable); return; }
            var claim = (Peer: peer, Request: request.RequestId);
            if (!giftClaims.Add(claim)) { ReplyGiftTo(peer, request.RequestId, NpcGiftResult.DuplicateRequest); return; }
            giftClaimOrder.Enqueue(claim);
            if (giftClaimOrder.Count > 8192) giftClaims.Remove(giftClaimOrder.Dequeue());

            actorsById.TryGetValue(request.ActorId, out Actor actor);
            bool active = actor != null && actor.isActiveAndEnabled;
            bool safeGiftPath = actor != null && !routeBoundActors.Contains(actor) && IsSafeNativeGiftPath(actor, request.Kind);
            var sharedItems = MonMPPlugin.Instance?.World?.Items;
            InteractableItems? item = null;
            bool itemOwnedAndCurrent = sharedItems != null &&
                sharedItems.HostAuthorizeNpcGiftItem(peer, request.ItemId, request.ItemGeneration, request.Kind, out item);
            bool fresh = peerPoses.TryGetValue(peer, out PeerPose? pose) &&
                Time.realtimeSinceStartup - pose.Time >= 0 && Time.realtimeSinceStartup - pose.Time <= 3f;
            PlayerPose playerPose = fresh ? pose!.Pose : default;
            int index = actor == null ? -1 : (int)actor.actor_Type;
            bool validIndex = index > 0 && index < GameManager.Get_Friendship_SyrupGiftedToday.Length;
            bool actorCanReceive = safeGiftPath && active && validIndex && actor!.CanTalkToHim;
            bool alreadyGifted = validIndex && GameManager.Get_Friendship_SyrupGiftedToday[index];
            // Police's reviewed acceptance method handles the daily flag itself and
            // returns false even after accepting a ticket-reduction gift. Tickets
            // belong to the shared host world, outside personal player profiles.
            bool policeSyrup = actor?.GetType() == typeof(Actor_Police) && request.Kind == NpcGiftKind.MapleSyrup;
            string itemRangeDiagnostics = sharedItems == null ? "items=unavailable" : "not-checked";
            bool itemNearPeer = sharedItems != null &&
                sharedItems.HostItemInRange(peer, request.ItemId, NpcGiftPolicy.HeldItemReach, out itemRangeDiagnostics);
            string overlapDiagnostics = "not-checked";
            bool itemOverlapsActor = itemOwnedAndCurrent && actor != null && item != null &&
                sharedItems!.HostNpcGiftItemOverlapsActor(actor, item, out overlapDiagnostics);
            Vector3 playerPosition = fresh ? new Vector3(playerPose.X, playerPose.Y, playerPose.Z) : Vector3.zero;
            float actorDistance = fresh && actor != null ? Vector3.Distance(playerPosition, actor.transform.position) : -1f;
            float poseAge = pose == null ? -1f : Time.realtimeSinceStartup - pose.Time;
            Vector3 itemPosition = item == null ? Vector3.zero : item.transform.position;
            NpcGiftResult decision = NpcGiftPolicy.ValidateHostRequest(safeGiftPath, active, fresh,
                itemOwnedAndCurrent, actorCanReceive, alreadyGifted && !policeSyrup, itemNearPeer, itemOverlapsActor, playerPose,
                itemPosition.x, itemPosition.y, itemPosition.z, NpcGiftPolicy.HeldItemReach);

#if MONMP_DEVELOPMENT
            testLastGiftResult = decision;


            testLastGiftDiagnostics = "result=" + decision + ";actor=" + (actor == null ? "missing" : actor.GetType().Name + "/" + actor.actor_Type) +
                ";safe=" + safeGiftPath + ";active=" + active + ";peerPose=" + (pose == null ? "missing" : playerPosition.ToString("F2")) +
                ";poseAge=" + poseAge.ToString("F2") + ";playerActorDistance=" + actorDistance.ToString("F2") +
                ";itemOwnedCurrent=" + itemOwnedAndCurrent + ";itemPosition=" + itemPosition.ToString("F2") +
                ";itemNearPeer=" + itemNearPeer + "[" + itemRangeDiagnostics + "]" +
                ";itemOverlapsActor=" + itemOverlapsActor + "[" + overlapDiagnostics + "]" +
                ";actorCanReceive=" + actorCanReceive + ";alreadyGifted=" + alreadyGifted;
#endif

            if (decision != NpcGiftResult.Applied)
            { ReplyGiftTo(peer, request.RequestId, decision); return; }

            bool nativeReturned = false;
            authorizedGiftActor = actor;
            authorizedGiftItem = item;
            int friendshipBeforeGift = GameManager.Get_Friendship[index];
            BeginCapture(actor!);
            try
            {
                using (Patches.RemoteActionScope.Enter())
                using (SpatialSounds.AtPeer(peer))
                    NativeActorGiftTrigger!.Invoke(actor, new object[] { item!.itemCollider });
                nativeReturned = true;
            }
            catch (Exception ex)
            {
                Debug.LogError("MonMP host NPC gift failed: " + ex);
            }
            finally
            {
                authorizedGiftActor = null;
                authorizedGiftItem = null;
                EndClick(actor!);
            }

            bool giftApplied = nativeReturned && !alreadyGifted && GameManager.Get_Friendship_SyrupGiftedToday[index];
            if (giftApplied) sharedItems!.HostConsumeNpcItem(peer, request.ItemId, request.ItemGeneration);
            ReplyGiftTo(peer, request.RequestId, giftApplied ? NpcGiftResult.Applied :
                nativeReturned && policeSyrup ? NpcGiftResult.Refused : NpcGiftResult.Unavailable,
                giftApplied && GameManager.Get_Friendship[index] > friendshipBeforeGift && GameManager.IsPlayerFullFriendshipWithEveryone);
            completedGreeting = null;
        }

        // ---- Guest spit, smoke and vomit reactions (#10, #18) ------------------------------------
        // The native callbacks fire on the guest's own machine (its spit drop, its joint's smoke, its
        // vomit, a police officer close to it) where the reaction itself is host-owned. The host
        // runs the NPC's native reaction for that guest: its words and voice reach everyone through
        // the greeting event, friendship through the shared state, a police ticket into the shared
        // world. A police officer's smoke reaction is rebuilt so it never trashes the host's joint.
        private const byte ReactionMarker = 0xB7;
        private const int ReactionSize = 10;
        private const byte ReactionSpit = 1, ReactionSmoke = 2, ReactionVomit = 3;
        private readonly Dictionary<(uint Peer, ulong Actor, byte Kind), float> lastReaction = new Dictionary<(uint, ulong, byte), float>();
        private Actor? reactionActor;
        private uint reactionPeer;
        private static readonly FieldInfo? PoliceSmokedTimer = AccessTools.Field(typeof(Actor_Police), "Timer_LastTimeSmokedOn");
        private static readonly FieldInfo? PoliceSpot = AccessTools.Field(typeof(Actor_Police), "Police_SpotDetails");

        internal void GuestReaction(Actor actor, string method)
        {
            if (host || disposed || NewActionsBlocked || session?.IsRunning != true || channel?.FullySynchronized != true || actor == null || !idsByActor.TryGetValue(actor, out ulong id)) return;
            byte kind = method == nameof(Actor.PlayerSpitOn) ? ReactionSpit : method == nameof(Actor.PlayerSmokedOn) ? ReactionSmoke :
                method == nameof(Actor.PlayerVomitOn) ? ReactionVomit : (byte)0;
            if (kind == 0) return;
            // Native PlayerSpitOn grants this first; the host's run of it is achievement-quiet.
            if (kind == ReactionSpit)
            {
                try { Singleton<Steam_AchievementManager>.i?.AchievementDone(AchievementType.SpitOnSomeone); }
                catch (Exception ex) { Debug.LogWarning("MonMP spit achievement failed: " + ex.Message); }
            }
            var key = (Peer: 0u, Actor: id, Kind: kind);
            if (lastReaction.TryGetValue(key, out float last) && Time.unscaledTime - last < 1f) return;
            lastReaction[key] = Time.unscaledTime;
            // The officer's smoke reaction trashes this player's joint and empties its joint count here;
            // the host runs its words and drug ticket.
            if (kind == ReactionSmoke && actor is Actor_Police)
            {
                Singleton<Gameplay>.i.Player.Get_VisualHands.IfJointInHandTrashIt();
                GameManager.Get_WeedJointAmount = 0;
            }
            var payload = new byte[ReactionSize];
            payload[0] = ReactionMarker; BitConverter.GetBytes(id).CopyTo(payload, 1); payload[9] = kind;
            channel.Request(NpcInteractionState.Entity, payload, true);
        }

        private void ReactionCommand(uint peer, ulong id, byte kind)
        {
            if (kind < ReactionSpit || kind > ReactionVomit || !actorsById.TryGetValue(id, out Actor actor) || actor == null || !actor.isActiveAndEnabled) return;
            if (!peerPoses.TryGetValue(peer, out PeerPose? pose) || Time.realtimeSinceStartup - pose.Time > 3f) return;
            var at = new Vector3(pose.Pose.X, pose.Pose.Y, pose.Pose.Z);
            if ((at - actor.transform.position).sqrMagnitude > 10f * 10f) return;
            var key = (Peer: peer, Actor: id, Kind: kind);
            if (lastReaction.TryGetValue(key, out float last) && Time.unscaledTime - last < 1f) return;
            lastReaction[key] = Time.unscaledTime;
            BeginCapture(actor);
            reactionActor = actor; reactionPeer = peer;
            try
            {
                using var quiet = Patches.RemoteActionScope.Enter();
                using var spatial = SpatialSounds.AtPeer(peer);
                using var fine = NativePoliceFinePresentation.Begin(peer, ReplyPoliceFine);
                if (kind == ReactionSmoke && actor is Actor_Police police) PoliceSmokedOnGuest(police);
                else if (kind == ReactionSpit) actor.PlayerSpitOn();
                else if (kind == ReactionSmoke) actor.PlayerSmokedOn();
                else actor.PlayerVomitOn();
            }
            catch (Exception ex) { Debug.LogWarning("MonMP guest NPC reaction failed: " + ex.Message); }
            finally { reactionActor = null; reactionPeer = 0; }
            EndClick(actor);
        }

        // Actor_Police.PlayerSmokedOn without the host-player parts (its hands and joint count).
        private static void PoliceSmokedOnGuest(Actor_Police police)
        {
            if (PoliceSmokedTimer?.GetValue(police) is float timer && timer + 10f > Time.realtimeSinceStartup) return;
            PoliceSmokedTimer?.SetValue(police, Time.realtimeSinceStartup);
            police.TalkOverHead(TEXT.GetText("ACTOR_Police_PlayerSmokeCloseBy"));
            if (PoliceSpot?.GetValue(police) is Police_SpotDetails spot && spot != null) Singleton<Gameplay>.i.Store_PoliceInterceptor.Ticket_Drug(spot);
        }

        // ---- NPC voices (#13) -----------------------------------------------------------------
        // The native greeting (or reaction) picks a random voice clip on the host; its text is shared
        // through the greeting event and the clip, by its place in the actor's voice lines, here.
        private static readonly FieldInfo? VoiceLinesField = AccessTools.Field(typeof(SOUND_ActorVoices), "ActorVoices");
        private static Actor_VoiceLines? VoiceLines(Actor actor)
        {
            if (!(VoiceLinesField?.GetValue(Singleton<SOUND_ActorVoices>.i) is Actor_VoiceLines[] lines) || lines.Length == 0) return null;
            return lines.FirstOrDefault(l => l != null && l.Actor_Type == actor.actor_Type) ?? lines[0];
        }
        private static AudioClip[]? VoiceSet(Actor_VoiceLines lines, byte set) => set switch
        {
            0 => lines.randomClip, 1 => lines.spitOnClip, 2 => lines.uniqueClip, 3 => lines.CommentingProductClip, _ => null
        };

        internal void RelayVoice(Actor actor, AudioClip clip)
        {
            if (!host || disposed || SharedPresentationEvents.Replaying || actor == null || clip == null ||
                (captureActor != actor && reactionActor != actor) || !idsByActor.TryGetValue(actor, out ulong id)) return;
            var lines = VoiceLines(actor);
            if (lines == null) return;
            for (byte set = 0; set < 4; set++)
            {
                int index = VoiceSet(lines, set) is AudioClip[] clips ? Array.IndexOf(clips, clip) : -1;
                if (index < 0 || index > 255) continue;
                MonMPPlugin.Instance?.World?.Events?.HostAnnounce(PresentationKind.NpcVoice, (byte)index, id, actor.transform.position, set, 0, false);
                return;
            }
        }

        internal void RelayProductComment(Actor actor, int index)
        {
            if (!host || disposed || SharedPresentationEvents.Replaying || actor == null ||
                !idsByActor.TryGetValue(actor, out ulong id) || index < 0 || index > byte.MaxValue) return;
            var lines = VoiceLines(actor);
            if (lines == null || !(VoiceSet(lines, 3) is AudioClip[] clips) || index >= clips.Length || clips[index] == null) return;
            MonMPPlugin.Instance?.World?.Events?.HostAnnounce(PresentationKind.NpcVoice, (byte)index, id,
                actor.transform.position, 3, 0, false);
        }

        internal void PlayVoice(PresentationEvent e)
        {
            if (host || !actorsById.TryGetValue(e.Target, out Actor actor) || actor == null || !actor.isActiveAndEnabled) return;
            var lines = VoiceLines(actor);
            if (lines == null || !(VoiceSet(lines, (byte)e.Value) is AudioClip[] clips) || e.Variant >= clips.Length || clips[e.Variant] == null) return;
            if (actor.ActorVoice == null) actor.CreateActorVoiceSource();
            if (e.Value == 3) actor.ActorVoice!.PlayOneShot(clips[e.Variant]);
            else actor.ActorVoice_Play(clips[e.Variant]);
        }

        private void ReplyTo(uint peer, Guid request, NpcTalkResult result, bool fullFriendshipAward = false)
        {
#if MONMP_DEVELOPMENT
testLastResult = result;
#endif
 channel?.Reply(peer, NpcInteractionState.Entity, new NpcTalkReply(request, result, result == NpcTalkResult.Applied && fullFriendshipAward).Encode()); }

        private void ReplyGiftTo(uint peer, Guid request, NpcGiftResult result, bool fullFriendshipAward = false)
        {
#if MONMP_DEVELOPMENT
testLastGiftResult = result;
#endif
 channel?.Reply(peer, NpcInteractionState.Entity, new NpcGiftReply(request, result, result == NpcGiftResult.Applied && fullFriendshipAward).Encode()); }

        private void ReplyWeedSaleTo(uint peer, Guid request, NpcWeedSaleResult result, bool fullFriendshipAward = false, int incomeCents = 0)
        {
#if MONMP_DEVELOPMENT
testLastWeedSaleResult = result;
#endif
 channel?.Reply(peer, NpcInteractionState.Entity, new NpcWeedSaleReply(request, result, result == NpcWeedSaleResult.Applied && fullFriendshipAward, incomeCents).Encode()); }

        private void ReplyPoliceFine(uint peer, byte[] payload) => channel?.Reply(peer, NpcInteractionState.Entity, payload);

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != NpcInteractionState.Entity) return;
            if (NativePoliceFinePresentation.Apply(payload)) return;
            if (NpcDialogGreetingPart.Is(payload))
            {
                try
                {
                    var part = NpcDialogGreetingPart.Decode(payload);
                    if (!pendingDialogTalks.ContainsKey(part.Operation) && pendingDialogOffer?.Operation != part.Operation) return;
                    if (!dialogGreetings.TryGetValue(part.Operation, out var assembled))
                    {
                        if (dialogGreetings.Count >= 64) return;
                        dialogGreetings.Add(part.Operation, new NpcDialogGreetingAssembler(part));
                    }
                    else assembled.Add(part);
                    TryOpenGuestDialog();
                }
                catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid NPC offered greeting: " + ex.Message); }
                return;
            }
            if (NpcDialogReply.Is(payload))
            {
                NpcDialogReply dialog;
                try { dialog = NpcDialogReply.Decode(payload); }
                catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid NPC dialog reply: " + ex.Message); return; }
                if (ApplyGillesWindowReply(dialog)) return;
                if (dialog.Status == NpcDialogStatus.Offered)
                {
                    if (!pendingDialogTalks.TryGetValue(dialog.Operation, out var talk) || talk.Actor != dialog.Actor ||
                        Time.unscaledTime - talk.Time > 5f) return;
                    pendingDialogTalks.Remove(dialog.Operation); pending.Remove(dialog.Operation);
                    ShowFullFriendshipAchievement(dialog.FullFriendship);
                    pendingDialogOffer = dialog; pendingDialogOfferExpires = talk.Time + 5f; TryOpenGuestDialog(); return;
                }
                if (dialog.Operation != guestChoiceOperation || dialog.Offer != guestDialogToken || guestDialogActor == null ||
                    !idsByActor.TryGetValue(guestDialogActor, out ulong guestActorId) || dialog.Actor != guestActorId) return;
                guestChoiceOperation = Guid.Empty; guestDialogToken = Guid.Empty;
                guestDialogActor.PlayerOption = "";
                string answer = dialog.Status switch
                {
                    NpcDialogStatus.Applied => guestDialogActor is Actor_Francis ? TEXT.Actors_Francis_GarageCommandDetails : TEXT.DIALOG_NORMAND_DonationDone,
                    NpcDialogStatus.NotEnoughCash => TEXT.GetText("ERROR_NotEnoughCash"),
                    NpcDialogStatus.OutOfRange => "Move closer and talk to this person again.",
                    _ => "This dialog option is no longer available. Talk to this person again."
                };
                var window = UnityEngine.Object.FindObjectOfType<UI_GenericWindow_Dialogs>();
                if (window != null && window.isActiveAndEnabled && DialogCurrentActor.GetValue(window) as Actor == guestDialogActor)
                {
                    if (DialogGreeting.GetValue(window) is TMPro.TextMeshProUGUI greeting) greeting.text = answer;
                    DialogUpdateButtons.Invoke(window, new object?[] { null });
                }
                else guestDialogActor.TalkOverHead(answer);
                return;
            }
            if (MotherPhoneCallReply.Is(payload))
            {
                MotherPhoneCallReply motherReply;
                try { motherReply = MotherPhoneCallReply.Decode(payload); }
                catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid Mother phone reply: " + ex.Message); return; }

#if MONMP_DEVELOPMENT
                TestLastMotherPhoneReply = motherReply;
#endif

                if (!pendingMotherPhoneCalls.TryGetValue(motherReply.RequestId, out var pending) || !pendingMotherPhoneCalls.Remove(motherReply.RequestId)) return;
                if (Time.unscaledTime - pending.Time > 5f || session?.IsRunning != true || pending.Phone == null ||
                    !pending.Phone.isActiveAndEnabled || !pending.Phone.CanUsePhoneButton ||
                    MonMP.Plugin.Patches.GuestPhoneCall.MotherRequestId != motherReply.RequestId) return;
                if (motherReply.Status == MotherPhoneCallStatus.Applied) PlayMotherPhoneReply(motherReply);
                else if (motherReply.Status == MotherPhoneCallStatus.AlreadyTalkedToday) PlayMotherPhoneBusy();
                else if (motherReply.Status == MotherPhoneCallStatus.Unavailable) ShowMotherPhoneFailure();
                return;
            }
            if (NpcWeedSaleReply.Is(payload))
            {
                NpcWeedSaleReply saleReply;
                try { saleReply = NpcWeedSaleReply.Decode(payload); }
                catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid NPC weed-sale reply: " + ex.Message); return; }

#if MONMP_DEVELOPMENT
                testLastWeedSaleResult = saleReply.Result;
#endif

                ApplyWeedSaleReply(saleReply);
                return;
            }
            if (NpcGiftReply.Is(payload))
            {
                NpcGiftReply giftReply;
                try { giftReply = NpcGiftReply.Decode(payload); }
                catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid NPC gift reply: " + ex.Message); return; }

#if MONMP_DEVELOPMENT
                testLastGiftResult = giftReply.Result;
#endif

                ApplyGiftReply(giftReply);
                return;
            }
            if (!NpcTalkReply.Is(payload)) return;
            NpcTalkReply reply;
            try { reply = NpcTalkReply.Decode(payload); }
            catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid NPC conversation reply: " + ex.Message); return; }

#if MONMP_DEVELOPMENT
            testLastResult = reply.Result;
#endif

            ApplyTalkReply(reply);
        }

        private bool ApplyGillesWindowReply(NpcDialogReply dialog)
        {
            if (dialog.Status != NpcDialogStatus.Applied || !actorsById.TryGetValue(dialog.Actor, out Actor actor) ||
                actor?.GetType() != typeof(Actor_Gilles)) return false;
            // Repeat talks open the search directly in native DialogStarted. The
            // first talk instead offers a token for the native search dialog option.
            if (pendingDialogTalks.TryGetValue(dialog.Operation, out var talk) && talk.Actor == dialog.Actor)
            {
                pendingDialogTalks.Remove(dialog.Operation); pending.Remove(dialog.Operation);
                if (Time.unscaledTime - talk.Time > 5f) return true;
                ShowFullFriendshipAchievement(dialog.FullFriendship);
            }
            else
            {
                if (dialog.Operation != guestChoiceOperation || dialog.Offer != guestDialogToken || actor != guestDialogActor) return false;
                guestChoiceOperation = Guid.Empty; guestDialogToken = Guid.Empty;
                actor.PlayerOption = "";
                var window = UnityEngine.Object.FindObjectOfType<UI_GenericWindow_Dialogs>();
                if (Time.unscaledTime > guestDialogExpires || window == null || !window.isActiveAndEnabled ||
                    DialogCurrentActor.GetValue(window) as Actor != actor) return true;
            }
            if (session?.IsRunning == true && MonMPPlugin.Instance?.World?.SaveGate.Active != true)
                MonMPPlugin.Instance?.World?.Gilles.GuestOpenWindow();
            return true;
        }

        private void ApplyGiftReply(NpcGiftReply reply)
        {
            if (!pendingGiftRequests.TryGetValue(reply.RequestId, out var request)) return;
            pendingGiftRequests.Remove(reply.RequestId);
            var pair = (Actor: request.Actor, Item: request.Item);
            pendingLocalGifts.Remove(pair);
            if (completedLocalGifts.Contains(pair)) return;
            if (reply.Result == NpcGiftResult.Applied)
            {
                completedLocalGifts.Add(pair);
                MonMPPlugin.Instance?.World?.Items.GuestConsumeNpcItem(request.Item);
                actorsById.TryGetValue(request.Actor, out Actor actor);
                ShowGiftSuccess(actor, request.Kind, reply.FullFriendship);
            }
            else ShowGiftFailure(reply.Result);
        }

        private void ApplyTalkReply(NpcTalkReply reply)
        {
            pendingDialogTalks.Remove(reply.RequestId);
            if (!pending.Remove(reply.RequestId)) return;
            if (reply.Result == NpcTalkResult.Applied) { ShowFullFriendshipAchievement(reply.FullFriendship); return; }
            ShowFailure(reply.Result);
        }

        private static void ShowFailure(NpcTalkResult result)
        {
            string? message = result switch
            {
                NpcTalkResult.OutOfRange => "Move closer to talk.",
                NpcTalkResult.StalePlayerPose => "Your position is still syncing. Try again.",
                NpcTalkResult.UnsupportedConversation => "This conversation is not synchronized yet.",
                _ => null
            };
            if (message != null) UnityEngine.Object.FindObjectOfType<UI_ErrorMessage>()?.Show(message);
        }

        private static void ShowGiftFailure(NpcGiftResult result)
        {
            // Police refusals already carry the native overhead reaction; replayed
            // requests must not create a second local failure popup.
            if (result == NpcGiftResult.Refused || result == NpcGiftResult.DuplicateRequest) return;
            string message = result switch
            {
                NpcGiftResult.OutOfRange => "Move closer to give that gift.",
                NpcGiftResult.StalePlayerPose => "Your position is still syncing. Try again.",
                NpcGiftResult.UnsupportedGift => "This gift interaction is not synchronized yet.",
                NpcGiftResult.AlreadyGiftedToday => "You already gave this person a gift today.",
                _ => "That person cannot accept this gift right now."
            };
            UnityEngine.Object.FindObjectOfType<UI_ErrorMessage>()?.Show(message);
        }

        private static void ShowGiftSuccess(Actor? actor, NpcGiftKind kind, bool fullFriendship)
        {
            if (actor == null) { ShowFullFriendshipAchievement(fullFriendship); return; }
            Singleton<SOUND_Manager>.i?.Play_General_MouseClick();
            if (actor.GetType() == typeof(Actor_Police) && kind == NpcGiftKind.MapleSyrup) return;
            Singleton<UI_EventMessage>.i?.Show(TEXT.GetText("ACTOR_GiftedSyrup"), actor.actor_Type.ToString(), 3f, UI_EventMessage.EventIconType.Talk);
            if (kind == NpcGiftKind.MapleSyrup)
                Singleton<Steam_AchievementManager>.i?.AchievementDone(AchievementType.Friendship_GiftAMapleSyrup);
            ShowFullFriendshipAchievement(fullFriendship);
        }

        private static void ShowFullFriendshipAchievement(bool fullFriendship)
        {
            if (fullFriendship)
                Singleton<Steam_AchievementManager>.i?.AchievementDone(AchievementType.Friendship_50WithEveryone);
        }

        private void ApplyWeedSaleReply(NpcWeedSaleReply reply)
        {
            if (!pendingWeedSaleRequests.TryGetValue(reply.RequestId, out var request)) return;
            pendingWeedSaleRequests.Remove(reply.RequestId);
            var pair = (Actor: request.Actor, Item: request.Item);
            pendingLocalWeedSales.Remove(pair);
            if (completedLocalWeedSales.Contains(pair)) return;
            if (reply.Result == NpcWeedSaleResult.Refused) return; // Native overhead text/voice arrives through existing relays.
            if (reply.Result == NpcWeedSaleResult.Confiscated)
            {
                completedLocalWeedSales.Add(pair);
                MonMPPlugin.Instance?.World?.Items.GuestConsumeNpcItem(request.Item);
                Singleton<Gameplay>.i?.Player.Get_VisualHands.IfJointInHandTrashIt();
                GameManager.Get_WeedJointAmount = 0;
                return;
            }
            // The host's native income is replicated by SharedStatistics, including
            // committed payment when a later native presentation callback throws.
            if (reply.Result == NpcWeedSaleResult.Applied)
            {
                completedLocalWeedSales.Add(pair);
                MonMPPlugin.Instance?.World?.Items.GuestConsumeNpcItem(request.Item);
                actorsById.TryGetValue(request.Actor, out Actor actor);
                ShowWeedSaleSuccess(actor, request.Kind, reply.FullFriendship);
            }
            else ShowWeedSaleFailure(reply.Result, request.Kind);
        }

        private static void ShowWeedSaleSuccess(Actor? actor, NpcWeedPackKind kind, bool fullFriendship)
        {
            if (actor != null)
                Singleton<UI_EventMessage>.i?.Show(TEXT.GetText(kind == NpcWeedPackKind.Big ? "WEEDBUYER_ThanksBigPack" : "WEEDBUYER_ThanksSmallPack"),
                    actor.actor_Type.ToString(), 3f, UI_EventMessage.EventIconType.Talk);
            ShowFullFriendshipAchievement(fullFriendship);
        }

        private static void ShowWeedSaleFailure(NpcWeedSaleResult result, NpcWeedPackKind kind = NpcWeedPackKind.Small)
        {
            if (result == NpcWeedSaleResult.DuplicateRequest) return;
            string message = result switch
            {
                NpcWeedSaleResult.OutOfRange => "Move closer to sell that pack.",
                NpcWeedSaleResult.StalePlayerPose => "Your position is still syncing. Try again.",
                NpcWeedSaleResult.UnsupportedSale => "This weed sale is not synchronized yet.",
                NpcWeedSaleResult.NotFriendEnough => TEXT.GetText("WEEDBUYER_NotFriendEnough"),
                NpcWeedSaleResult.NotBuyingToday => TEXT.GetText("WEEDBUYER_AlreadyBoughtToday"),
                NpcWeedSaleResult.WrongPackSize => TEXT.GetText(kind == NpcWeedPackKind.Big ? "WEEDBUYER_IWantASmallPack" : "WEEDBUYER_IWantABigPack"),
                _ => "That person cannot buy this pack right now."
            };
            UnityEngine.Object.FindObjectOfType<UI_ErrorMessage>()?.Show(message);
        }

        private void ApplyNative(NpcInteractionState state)
        {
            int[] friendship = state.Friendship; bool[] canFriends = state.CanBeFriends;
            bool[] talked = state.TalkedToday, bought = state.BoughtWeedToday, gifted = state.GiftedToday;
            for (int i = 0; i < state.Count; i++)
            {
                GameManager.Get_Friendship[i] = friendship[i]; GameManager.Get_Friendship_CanBeFriendWithThisAcor[i] = canFriends[i];
                GameManager.Get_Friendship_TalkedToday[i] = talked[i]; GameManager.Get_Friendship_BoughtWeedToday[i] = bought[i];
                GameManager.Get_Friendship_SyrupGiftedToday[i] = gifted[i];
            }
        }

        private void RefreshFriendshipSlots()
        {
            if (!friendshipUiDirty) return;
            // Native UpdatePage refreshes these only when the page opens. A join
            // can open it before the final delta, leaving its progress bars stale.
            // Include inactive zero-friendship slots so the first point reveals them.
            foreach (var slot in UnityEngine.Object.FindObjectsOfType<EscapeMenu_Friend_Slot>(true))
                if (slot != null) slot.UpdateSlot();
            friendshipUiDirty = false;
        }

        public void Update()
        {
            if (disposed || session?.IsRunning != true || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.25f;
            if (host) PublishStatsIfChanged();
            else if (initialized && !MatchesNativeState())
            {
                // Preserve validation of corrupt native data before restoring the
                // host snapshot, as the previous capture-and-encode comparison did.
                _ = Capture(current.EventSequence, current.SpeakerId, current.Greeting);
                ApplyNative(current);
                friendshipUiDirty = true;
            }
            if (!host)
            {
                RefreshFriendshipSlots();
                TryOpenGuestDialog();
            }
            if (Time.unscaledTime >= nextPendingSweep)
            {
                nextPendingSweep = Time.unscaledTime + 1f;
                if (pending.Count != 0)
                    foreach (var expired in pending.Where(pair => Time.unscaledTime - pair.Value >= 120f).Select(pair => pair.Key).ToArray())
                        pending.Remove(expired);
                if (pendingDialogTalks.Count != 0)
                    foreach (var expiredTalk in pendingDialogTalks.Where(pair => Time.unscaledTime - pair.Value.Time > 5f).Select(pair => pair.Key).ToArray())
                    { pendingDialogTalks.Remove(expiredTalk); dialogGreetings.Remove(expiredTalk); }
                if (pendingGiftRequests.Count != 0)
                    foreach (var expired in pendingGiftRequests.Where(pair => Time.unscaledTime - pair.Value.Time >= 120f).ToArray())
                    {
                        pendingGiftRequests.Remove(expired.Key);
                        pendingLocalGifts.Remove((expired.Value.Actor, expired.Value.Item));
                    }
                if (pendingWeedSaleRequests.Count != 0)
                    foreach (var expired in pendingWeedSaleRequests.Where(pair => Time.unscaledTime - pair.Value.Time >= 120f).ToArray())
                    {
                        pendingWeedSaleRequests.Remove(expired.Key);
                        pendingLocalWeedSales.Remove((expired.Value.Actor, expired.Value.Item));
                        ShowWeedSaleFailure(NpcWeedSaleResult.Unavailable);
                    }
                if (pendingMotherPhoneCalls.Count != 0)
                    foreach (var expired in pendingMotherPhoneCalls.Where(pair => Time.unscaledTime - pair.Value.Time >= 5f).Select(pair => pair.Key).ToArray())
                    { pendingMotherPhoneCalls.Remove(expired); ShowMotherPhoneFailure(); }
            }
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply;
            }
            if (session != null) { session.PoseReceived -= PoseReceived; session.PeerLeft -= PeerLeft; }
            if (!host)
            {
                foreach (var option in guestDialogOriginalOptions) if (option.Key != null) option.Key.PlayerOption = option.Value;
                for (int i = 0; i < originalFriendship.Length; i++)
                {
                    GameManager.Get_Friendship[i] = originalFriendship[i]; GameManager.Get_Friendship_CanBeFriendWithThisAcor[i] = originalCanBeFriends[i];
                    GameManager.Get_Friendship_TalkedToday[i] = originalTalkedToday[i]; GameManager.Get_Friendship_BoughtWeedToday[i] = originalBoughtToday[i];
                    GameManager.Get_Friendship_SyrupGiftedToday[i] = originalGiftedToday[i];
                }
            }
            idsByActor.Clear(); actorsById.Clear(); peerPoses.Clear(); claims.Clear(); claimOrder.Clear();
            giftClaims.Clear(); giftClaimOrder.Clear(); pending.Clear();
            weedSaleClaims.Clear(); weedSaleClaimOrder.Clear(); pendingGiftRequests.Clear(); pendingWeedSaleRequests.Clear();
            motherPhoneClaims.Clear(); motherPhoneClaimOrder.Clear(); motherPhoneResults.Clear(); motherPhoneResultOrder.Clear(); pendingMotherPhoneCalls.Clear();
            pendingLocalGifts.Clear(); completedLocalGifts.Clear(); pendingLocalWeedSales.Clear(); completedLocalWeedSales.Clear();
            pendingDialogTalks.Clear(); dialogChoices.Clear(); pendingDialogOffer = null; guestDialogActor = null;
            dialogGreetings.Clear();
            guestDialogOriginalOptions.Clear();
            channel = null; session = null;
        }

#if MONMP_DEVELOPMENT
        internal ulong TestActorId(Actor actor) => ActorId(actor);
        internal Actor? TestActorById(ulong id) => ActorById(id);
        internal bool TestPeerNear(uint peer, Vector3 position, float maxDistance) => PeerNear(peer, position, maxDistance);
#endif
    }
}
