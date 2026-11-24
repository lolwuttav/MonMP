using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Persistence;
using MonMP.Core.Session;
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
using MonMP.Core.Testing;
#endif
using MonMP.Core.Transport;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using MonMP.Plugin.Replication;
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
using MonMP.Plugin.Testing;
#endif
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin
{
    [BepInPlugin(PluginId, "MonMP", SessionOptions.CurrentModVersion)]
    [BepInProcess("Mon Bazou.exe")]
    public sealed partial class MonMPPlugin : BaseUnityPlugin
    {
        public const string PluginId = "local.monmp.multiplayer";
        internal static MonMPPlugin? Instance { get; private set; }
        internal void LogSprayWarning(string message) => Logger.LogWarning(message);
        internal void LogCdWarning(string message) => Logger.LogWarning("CD music: " + message);
        internal void LogCdInfo(string message) => Logger.LogInfo("CD music: " + message);
        internal void LogRadioWarning(string message) => Logger.LogWarning("Radio: " + message);
        internal void LogRadioInfo(string message) => Logger.LogInfo("Radio: " + message);
        internal NetworkSession? Session { get; private set; }
        internal SharedWorld? World { get; private set; }
        internal GuestWorldLoader? GuestWorld { get; private set; }
        internal SharedLights? Lights => World?.Lights;
        internal SharedItems? Items => World?.Items;
        internal SharedVehicles? Vehicles => World?.Vehicles;
        internal SharedBoatControls? BoatControls => World?.BoatControls;
        internal SharedBoatMotion? BoatMotion => World?.BoatMotion;
        internal ConsumptionInbox Consumption { get; } = new ConsumptionInbox();
        private Guid reconnectEpoch;
        private Guid reconnectProfileId;
        private PlayerProfileState? reconnectProfile;
        internal PlayerProfileState? FindReconnectProfile(Guid epoch) => epoch != Guid.Empty && epoch == reconnectEpoch && Session?.ProfileId == reconnectProfileId ? reconnectProfile : null;
        internal void RememberReconnectProfile(Guid epoch, PlayerProfileState state)
        { if (epoch != Guid.Empty && Session != null) { reconnectEpoch = epoch; reconnectProfileId = Session.ProfileId; reconnectProfile = state; } }

#if MONMP_DEVELOPMENT
        internal RuntimeInteractionAudit? InteractionAudit { get; private set; }
        private ConfigEntry<bool> strictPendingInteractions = null!;
        private ConfigEntry<bool> vehicleInputTraceEnabled = null!;
        private ConfigEntry<bool> debugOverlay = null!;
#endif
        private UI.PlayerStatus? playerStatus;
        private UI.TimeSkipPresentation? timeSkipPresentation;
        private Action? nativeMenuTick, mapMarkerTick, physicsTick, underglowLateTick, carryLateTick, playerStatusTick;
        private Action? sessionTick, remotePlayerTick, selfViewTick;
#if MONMP_DEVELOPMENT
        private UI.HoldPositionEditor? holdPositionEditor;
        private UI.HoldPositionEditor? seatPositionEditor;
        private UI.HoldPositionEditor? seatFootLiveProbe;
        private UI.EmotePoseEditor? emotePoseEditor;
        private UI.CargoBoundsEditor? cargoBoundsEditor;
#endif
        internal HeldGripOverrides HeldGrips { get; private set; } = null!;
        internal VehicleSeatPoseOverrides SeatPoses { get; private set; } = null!;
        internal CargoBoundsOverrides CargoBounds { get; private set; } = null!;
        internal EmotePoseOverrides EmotePoses { get; private set; } = null!;
#if MONMP_DEVELOPMENT
        internal bool HoldPositionEditorOpen => holdPositionEditor?.Visible == true || seatPositionEditor?.Visible == true || emotePoseEditor?.Visible == true || cargoBoundsEditor?.Visible == true;
#else
        internal bool HoldPositionEditorOpen => false;
#endif
        internal UI.PlayerActivity Activity { get; } = new UI.PlayerActivity();
        internal SharedCheats? Cheats { get; private set; }
        internal UI.SocialPings? Pings { get; private set; }
        public bool ClientCheatsAllowed => Session?.IsRunning != true || Session.IsHost || Cheats?.CanUse == true;
        internal bool MenuCheatsEnabled => Session?.Cheats.Enabled == true;
        internal bool MenuCanEnableCheats => Session?.IsHost == true && !MenuCheatsEnabled && Gameplay?.Get_isHardMode == false;
        internal bool MenuRequestEnableCheats() => Cheats?.RequestEnableFromSession() == true;
        internal bool MenuCheatsGranted(uint peer) => Session?.Cheats.Granted(peer) == true;
        internal bool MenuShowPlayersOnMap => Session?.Settings.ShowPlayersOnMap ?? true;
        internal void MenuSetShowPlayersOnMap(bool show) { if (Session?.IsHost == true) Session.SetShowPlayersOnMap(show); }
        internal bool MenuTruckExtraSeatsEnabled => Session?.Settings.TruckExtraSeatsEnabled ?? true;
        internal void MenuSetTruckExtraSeatsEnabled(bool enabled) { if (Session?.IsHost == true) Session.SetTruckExtraSeatsEnabled(enabled); }
        internal bool MenuFreezeVehicleCargo => Session?.Settings.FreezeVehicleCargo == true;
        internal void MenuSetFreezeVehicleCargo(bool enabled) { if (Session?.IsHost == true) Session.SetFreezeVehicleCargo(enabled); }
        internal void MenuSetCheatPermission(uint peer, bool allowed) { if (Session?.IsHost == true) Session.SetCheatPermission(peer, allowed); }
        internal void PointRemotePlayer(uint peer) => remotePlayers.PointAt(peer);
        internal UI.CaptionChat? Chat { get; private set; }
        internal bool ChatBlocksInput => Chat?.BlocksInput == true;
        internal bool ExtensionUiBlocksInput { get; set; }
        // Local VR bridge (Integration.LocalVrBridge): a registered provider's modal UI joins the same gate
        // as chat and the MonMP menu. All three are false/absent without a live, fresh, active provider.
        internal bool BridgeMenuOpen => menuVisible || HoldPositionEditorOpen || ExtensionUiBlocksInput || timeSkipPresentation?.BlocksInput == true;
        internal UI.SpectatorCamera? BridgeSpectator => spectatorCamera;
        internal bool ExternalViewOwned => Integration.LocalInteraction.ExternalView;
        internal bool SpectatorUiBlocked => menuVisible || ChatBlocksInput || HoldPositionEditorOpen || ExtensionUiBlocksInput || timeSkipPresentation?.BlocksInput == true ||
            Integration.LocalInteraction.BlocksInput;
        internal void UpdateTimeSkipPresentation()
        {
            timeSkipPresentation ??= new UI.TimeSkipPresentation(this);
            timeSkipPresentation.Tick();
        }
        internal bool SocialUiBlocked => SpectatorUiBlocked || PauseMenuOpen || Spectating;
        internal bool Spectating => spectatorCamera?.Active == true;
        internal bool NativeHostScreenOpen => nativeMenus?.HostOpen == true;
        internal bool OverrideNativeSorry => emoteWheelKey != null && emoteWheelKey.Value == KeyCode.T &&
            Session?.IsRunning == true && Session.IsReady && World?.Channel.FullySynchronized == true &&
            World.SaveGate.Active != true && GuestWorld?.HoldPresentation != true &&
            gameplay != null && UI.EmoteGameplayPolicy.IsWheelDisplayState(gameplay.Get_PlayerState) &&
            !gameplay.Get_IsDead && !gameplay.Get_IsSleeping;
#if MONMP_DEVELOPMENT
        internal bool MapCalibrationOwnsF7 => mapCalibrationKey != null && mapCalibrationKey.Value == KeyCode.F7 &&
            Session?.IsRunning == true && Session.IsReady && gameplay?.Player?.Get_VisualHands.Get_IsMapOpen == true;
        internal string MapMarkerSetupStatus => playerMapMarkers?.SetupStatus ?? "Host or join a synchronized multiplayer world to set up map player markers.";
        internal bool MapMarkerSetupAvailable => Session?.IsRunning == true && Session.IsReady &&
            World?.Channel.FullySynchronized == true && World.SaveGate.Active != true && GuestWorld?.HoldPresentation != true &&
            gameplay?.Get_PlayerState == PlayerState.Walking && !gameplay.Get_isGamePaused && !gameplay.Get_IsDead && !gameplay.Get_IsSleeping;
        internal bool RequestMapMarkerSetup(out string message)
        {
            if (!MapMarkerSetupAvailable || SocialUiBlocked)
            { message = "Map setup requires synchronized walking gameplay. Close other menus first."; return false; }
            ReleaseSocialInput();
            UpdatePlayerMapMarkers();
            if (playerMapMarkers == null) { message = "Map markers are unavailable in this session."; return false; }
            return playerMapMarkers.RequestSetup(out message);
        }
#endif
        internal bool LocalSorryActive => gameplay?.Player?.Get_VisualHands is PlayerVisualHands hands &&
            HandsAnimatorField?.GetValue(hands) is Animator animator && animator.isActiveAndEnabled && animator.GetBool("Sorry");
        internal void ReleaseSocialInput()
        {
            emoteWheel?.CancelForUi();
            wheelEmoteActions?.Reset();
            Patches.SocialInputPatches.Reset();
#if MONMP_DEVELOPMENT
            playerMapMarkers?.CancelForUi();
#endif
        }
        private ConfigEntry<KeyCode> emoteWheelKey = null!;
#if MONMP_DEVELOPMENT
        private ConfigEntry<KeyCode> mapCalibrationKey = null!;
#endif
        private ConfigEntry<string> mapCalibration = null!;
        private UI.EmoteGesturePreview? emoteGesturePreview;
        private UI.EmoteWheel? emoteWheel;
        private UI.WheelEmoteActions? wheelEmoteActions;
        private UI.SpectatorCamera? spectatorCamera;
        private UI.ExtraSeatCamera? extraSeatCamera;
        private GuestModeLease? guestModeLease;
        private Gameplay? permadeathGameplay;
        private BasicNeeds_Manager? permadeathNeeds;
        private static readonly System.Reflection.FieldInfo NativeDeadField = AccessTools.Field(typeof(Gameplay), "isDead");
        private UI.PlayerMapMarkers? playerMapMarkers;
        private NetworkSession? mapMarkerSession;
#if MONMP_DEVELOPMENT
        private bool DebugOverlay => debugOverlay.Value || TestOptions.Enabled;
#endif
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
        internal LocalTestOptions TestOptions { get; private set; } = new LocalTestOptions();
#endif

        private readonly Runtime.RuntimeCompatibility compatibility = new Runtime.RuntimeCompatibility();
#if MONMP_DEVELOPMENT
        private readonly RuntimeProbe probe = new RuntimeProbe();
#endif
        private readonly RemotePlayers remotePlayers = new RemotePlayers();
        // This player's own body, shown only in a vehicle's third-person camera (rt-12 report). A separate
        // set, so name tags, seat checks and nearest-player sounds never see it.
        private readonly RemotePlayers selfView = new RemotePlayers();
        private uint? selfShown;
        private Harmony? harmony;
#if MONMP_DEVELOPMENT
        private RuntimeSmoke? smoke;
        private SteamRuntimeSmoke? steamSmoke;
        private GameplayProbe? gameplayProbe;
        private SavedWorldInventoryTest? savedWorldInventoryTest;
        private VehicleInputTrace? vehicleInputTrace;
        private PairedGameplayTest? pairedTest;
        private OfflineSaveTest? offlineSaveTest;
        private CrashRecoveryTest? crashRecoveryTest;
#endif
        // Hooks for the separate director mod (MonMP.Director), which records the demo reel on test copies.
#if MONMP_DEVELOPMENT
        internal static event Action<Gameplay?>? ExtensionTick;
        internal static event Action<Gameplay?>? ExtensionLateTick;
        internal static bool CleanFrame { get; set; }
        internal static bool CleanFrameNames { get; set; }
        internal static bool CleanFrameNativeMap { get; set; }
        internal RuntimeProbe Probe => probe;
        private MenuJoinTest? menuJoinTest;
        private NativeSignEmoteFixture? nativeSignEmoteFixture;
#else
        internal static bool CleanFrame => false;
        internal static bool CleanFrameNativeMap => false;
#endif
        private EscapeMenuV2? pendingExitMenu;
        private SharedWorld? pendingExitWorld;
        private int pendingExitButton;
        internal bool HostExitPending => pendingExitWorld != null;
        private EscapeMenuV2? failedExitMenu;
        private SharedWorld? failedExitWorld;
        private int failedExitButton;
        private SharedWorld? preparedDisconnectWorld, pendingDisconnectWorld;
        private NetworkSession? pendingDisconnectSession;
        private EscapeMenuV2? pendingDisconnectMenu;
        private int pendingDisconnectButton;
        private float pendingDisconnectDeadline;
        private string pendingDisconnectReason = "";
        internal bool LocalDisconnectPending => pendingDisconnectWorld != null;
        internal bool CanLeaveWithoutSaving => !HostExitPending && Session?.IsRunning == true && Session.IsHost &&
            failedExitMenu != null && failedExitWorld == World;
        internal void LeaveWithoutSaving()
        {
            if (!CanLeaveWithoutSaving) return;
            var menu = failedExitMenu!; int button = failedExitButton;
            StopSession("Host explicitly left without saving; the last multiplayer save is unchanged.");
            AccessTools.Field(typeof(EscapeMenuV2), "QuitButtonClicked").SetValue(menu, button);
            menu.Button_QuitGameConfirmation();
        }
        private LanDiscovery? lan;
        private float nextLanSearch;
        private string lanStatus = "";

#if MONMP_DEVELOPMENT
        private Vector2 lanScroll;


        private Vector2 peerScroll;
#endif

        private void ShowSelf(Gameplay gameplay, PlayerPose pose)
        {
            var state = gameplay.Get_PlayerState;
            var vehicle = gameplay.Get_CurrentVehicleController;
            // The host's own peer id is 0, as its seats record it.
            uint self = Session?.LocalPeerId ?? 0;
            bool thirdPerson = vehicle != null && vehicle._CurrentCameraIndex > 0 &&
                (state == PlayerState.InVehicle_Driver || state == PlayerState.InVehicle_Passenger);
            thirdPerson |= extraSeatCamera?.Active == true;
            // A VR provider owns the local view: the headset is always the first-person eye, so keep
            // the head-hidden shadow body and never the duplicate full body beside the viewer.
            if (ExternalViewOwned) thirdPerson = false;
            Vector3 viewPosition = extraSeatCamera?.Active == true ? extraSeatCamera.ViewPosition : gameplay.Player.mainCamera.transform.position;
            var view = pose.Spectating ? LocalAvatarView.Disabled : LocalAvatarPresentationPolicy.Select(
                Session?.IsRunning == true, thirdPerson,
                Vector3.Distance(viewPosition, new Vector3(pose.X, pose.Y + 1.6f, pose.Z)),
                Vector3.Distance(viewPosition, new Vector3(pose.X, pose.Y, pose.Z)));
            if (view == LocalAvatarView.Disabled)
            {
                if (selfShown is uint shown) { selfView.Remove(shown); selfShown = null; }
                return;
            }
            if (selfShown != self)
            {
                if (selfShown is uint old) selfView.Remove(old);
                var profile = Guid.TryParse(profileId.Value, out var parsed) ? parsed : Guid.Empty;
                selfView.Add(new PeerInfo(self, profile, string.IsNullOrWhiteSpace(displayName.Value) ? "Player" : displayName.Value, ProfileColor(profile)));
                selfShown = self;
            }
            selfView.SetLocalAvatarView(self, view);
            selfView.Apply(self, pose);
        }


#if MONMP_DEVELOPMENT
        internal int RemoteAvatarCount => remotePlayers.VisibleCount;
#endif

        internal System.Collections.Generic.IEnumerable<(string Name, Vector3 Head)> RemoteNameTags => Session?.IsRunning == true ? remotePlayers.NameTags() : Array.Empty<(string, Vector3)>();
        internal Vector3 ProjectPresentation(Camera camera, Vector3 world) => extraSeatCamera?.Project(camera, world) ?? camera.WorldToScreenPoint(world);
        internal bool RemoteSeatTaken(Vector3 seat) => Session?.IsRunning == true && remotePlayers.SeatTaken(seat, 0.4f);

#if MONMP_DEVELOPMENT
        internal float RemoteAvatarHeadHeight(uint peer) => remotePlayers.TestHeadHeight(peer);
#endif

        internal Vector3? RemoteAvatarHeadPosition(uint peer) => remotePlayers.HeadPosition(peer);
        internal Vector3? RemoteAvatarMapPosition(uint peer) => remotePlayers.MapPosition(peer);

#if MONMP_DEVELOPMENT
        internal Vector3? RemoteAvatarRenderedHeadPosition(uint peer) => remotePlayers.TestHeadPosition(peer);


        internal PlayerPose? RemoteAvatarRawPose(uint peer) => remotePlayers.TestRawPose(peer);


        internal bool RemoteAvatarSpectating(uint peer) => remotePlayers.TestIsSpectating(peer);


        internal string TestRemoteSpectatorVisual(uint peer) => remotePlayers.TestSpectatorVisual(peer);
#endif

        internal Transform? RemoteAvatar(uint peer) => Session?.IsRunning == true ? remotePlayers.AvatarRoot(peer) : null;

#if MONMP_DEVELOPMENT
        internal int RemoteAvatarShirt(uint peer) => remotePlayers.TestShirt(peer);


        internal Transform? RemoteAvatarRoot(uint peer) => remotePlayers.TestRoot(peer);


        internal Vector3? RemoteAvatarHips(uint peer) => remotePlayers.TestHips(peer);


        internal string RemoteAvatarBody(uint peer) => remotePlayers.TestBody(peer);


        internal bool RemoteAvatarStockBody(uint peer) => remotePlayers.TestStockBody(peer);


        internal string RemoteAvatarPreviewPoses(uint peer, string directory) => remotePlayers.TestPreviewPoses(peer, directory);


        internal string TestHeldGripEditor(string directory)
        {
            if (gameplay?.Player == null) throw new InvalidOperationException("Held-grip preview requires a loaded native player.");
            using var editor = new UI.HoldPositionEditor(HeldGrips, ItemPrefabCatalog.Load(probe.DirectoryPath), gameplay);
            return editor.TestSmoke(directory);
        }


        internal string TestVehicleSeatEditor(string directory)
        {
            if (gameplay?.Player == null) throw new InvalidOperationException("Seat preview requires a loaded native player.");
            using var editor = new UI.HoldPositionEditor(HeldGrips, SeatPoses, World, gameplay);
            return editor.TestSeatSmoke(directory);
        }


        internal string? TestSeatFootLiveStep(string directory)
        {
            if (gameplay?.Player == null) throw new InvalidOperationException("Seat foot preview requires a loaded native player.");
            seatFootLiveProbe ??= new UI.HoldPositionEditor(HeldGrips, SeatPoses, World, gameplay);
            string? result = seatFootLiveProbe.TestSeatFootLiveStep(directory);
            if (result != null) { seatFootLiveProbe.Dispose(); seatFootLiveProbe = null; }
            return result;
        }


        internal string RemoteAvatarEmotes(uint peer) => remotePlayers.TestEmotes(peer);


        internal float RemoteAvatarSorry(uint peer) => remotePlayers.TestSorry(peer);


        internal bool RemoteAvatarHandRaised(uint peer) => remotePlayers.TestHandRaised(peer);


        internal string RemoteAvatarVoiceText(uint peer) => remotePlayers.TestVoiceText(peer);

        internal (int Spawned, int Landed) RemoteSpitDrops => (remotePlayers.TestSpitDropsSpawned, remotePlayers.TestSpitDropsLanded);

        internal string RemoteAvatarHandProps(uint peer) => remotePlayers.TestHandProps(peer);


        internal PlayerHands RemoteAvatarHands(uint peer) => remotePlayers.TestHands(peer);
#endif

        internal bool RemoteAvatarToolActive(uint peer) => remotePlayers.ToolActive(peer);

#if MONMP_DEVELOPMENT
        internal bool TestRemoteAvatarNear(uint peer, Vector3 position, float radius) => remotePlayers.TestPoseNear(peer, position, radius);


        internal bool RemoteAvatarFlashlightInHand(uint peer) => remotePlayers.TestFlashlightInHand(peer);


        internal PlayerRest RemoteAvatarRest(uint peer) => remotePlayers.TestRest(peer);


        internal bool RemoteAvatarLying(uint peer) => remotePlayers.TestLying(peer);


        internal int RemoteFootsteps => remotePlayers.TestFootsteps;


        internal bool? TestCrouchOverride { get; set; }
#endif

        private BasicNeeds_Manager? needs;
        private ulong riderCarrier;
        private Vector3 riderLocal;
        private static readonly System.Reflection.FieldInfo? FlashlightField = AccessTools.Field(typeof(PlayerVisualHands), "isFlashLightActive");
        private static readonly System.Reflection.FieldInfo? HandsAnimatorField = AccessTools.Field(typeof(PlayerVisualHands), "animator");

#if MONMP_DEVELOPMENT
        internal byte? TestShirtOverride { get; set; }
#endif

        private Gameplay? gameplay;
        internal Gameplay? Gameplay => gameplay;
        private ConfigEntry<string> displayName = null!, profileId = null!, profileKey = null!, hostAddress = null!, recentHostsConfig = null!, savedServersConfig = null!;
        private SavedServerList savedServers = SavedServerList.Empty;
        private UI.NativeMenus? nativeMenus;
        internal bool PauseMenuOpen => nativeMenus?.PauseOpen == true;
        private ConfigEntry<int> port = null!;
        private ConfigEntry<bool> ipv6 = null!, advertiseLan = null!, mapRouterPort = null!;
        private readonly RouterMappingHost routerMapping = new RouterMappingHost();
        private RecentHostHistory recentHosts = RecentHostHistory.Empty;

#if MONMP_DEVELOPMENT
        private Vector2 recentHostsScroll;


        private bool recentHostsExpanded;


        private string recentHostStatus = "";
#endif

        private string nameText = "", addressText = "", portText = "", password = "", status = "Starting";
        private bool menuVisible, sessionWasRunning, previewAcknowledged;
#if MONMP_DEVELOPMENT
        private bool ownsPause, cursorVisible;
#endif
        private bool previousRunInBackground;
#if MONMP_DEVELOPMENT
        private bool smokeRequested, smokeFinished;
        private CursorLockMode cursorLock;
#endif
        private float nextLookup, nextPose, lastSaveNotice = -10;
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
        private float nextTestPreferencesSave;
#endif
#if MONMP_DEVELOPMENT
        private Rect window = new Rect(30, 40, 520, 580);
#endif
        private string sessionScene = "";
#if MONMP_DEVELOPMENT
        private string DiagnosticsDirectory => probe.DirectoryPath;
        // Director runs host through BeginPairedTestSession without a paired/menu test object.
        private bool directedTestSession;
        private bool IsAutomatedLanTest => pairedTest != null || menuJoinTest != null || directedTestSession;
#else
        private string DiagnosticsDirectory => "";
        private bool IsAutomatedLanTest => false;
#endif
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
        private bool IsLocalTest => TestOptions.Enabled;
#else
        private bool IsLocalTest => false;
#endif

#if MONMP_PERFORMANCE_CAPTURE
        private Runtime.PerformanceCapture? performanceCapture;
        private readonly Runtime.CaptureAvatarRendering captureAvatarRendering = new Runtime.CaptureAvatarRendering();
        private readonly Runtime.CaptureClientRendering captureClientRendering = new Runtime.CaptureClientRendering();

        private void CaptureAvatarShortcut(bool running)
        {
            if (!running || performanceCapture == null || Session?.IsHost != true)
            {
                if (captureAvatarRendering.Suppressed) captureAvatarRendering.Reset();
                return;
            }
            var keyboard = Keyboard.current;
            if (Application.isFocused && keyboard != null &&
                (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed) && keyboard.f9Key.wasPressedThisFrame && !SocialUiBlocked)
            {
                captureAvatarRendering.Toggle(remotePlayers, selfView);
                Logger.LogInfo("Performance capture A/B: " + captureAvatarRendering.Status + "; " + captureAvatarRendering.Inventory);
            }
        }

        private void CaptureClientShortcut(bool running, string synchronization)
        {
            if (!running || performanceCapture == null || Session?.IsHost != false || synchronization != "ready")
            {
                if (captureClientRendering.Sparse) captureClientRendering.Reset();
                return;
            }
            var keyboard = Keyboard.current;
            if (Application.isFocused && keyboard != null &&
                (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed) && keyboard.f10Key.wasPressedThisFrame && !SocialUiBlocked)
            {
                captureClientRendering.Toggle();
                Logger.LogInfo("Performance capture client rendering A/B: " + captureClientRendering.Status);
            }
        }

        private string CaptureSynchronization(bool running, bool loaded)
        {
            if (!running) return loaded ? "offline" : "unloaded";
            if (Session!.IsHost) return "host";
            if (!loaded) return "native-loading";
            if (World == null) return "world-setup";
            if (!World.Channel.FullySynchronized) return "snapshot";
            if (World.Channel.RepairPending) return "repair";
            if (!World.PersonalProfileReady) return "profile";
            if (GuestWorld?.Activated == false) return "activation";
            return "ready";
        }

        private void DisablePerformanceCapture(string message)
        {
            Runtime.UpdateLoop.Capture = null;
            MonMP.Core.Session.CaptureTiming.Sink = null;
            try { captureAvatarRendering.Reset(); } catch (Exception) { }
            try { captureClientRendering.Reset(); } catch (Exception) { }
            var failedCapture = performanceCapture;
            performanceCapture = null;
            try { failedCapture?.Dispose(); } catch (Exception) { }
            Logger.LogWarning(message);
        }
#endif
        private void Awake()
        {
            Instance = this;
            Integration.LocalVrBridge.BindContext();
#if MONMP_DEVELOPMENT
            smokeRequested = Environment.GetCommandLineArgs().Contains("--monmp-smoke-test");
#endif
            try
            {
#if MONMP_LOCAL_TEST_ISOLATION
                TestOptions = LocalTestOptions.ParseIsolatedCapture(Environment.GetCommandLineArgs());
#elif MONMP_DEVELOPMENT
                TestOptions = LocalTestOptions.Parse(Environment.GetCommandLineArgs());
#endif
                StartSupportCapture();
                displayName = Config.Bind("Player", "DisplayName", PlayerNamePolicy.DefaultName, "Choose a custom player name before joining a server (48 UTF-8 bytes maximum).");
                profileId = Config.Bind("Player", "ProfileId", Guid.NewGuid().ToString("D"), "Local resume hint. A unique saved player name can also resume from another PC; simultaneous copies receive separate profiles.");
                profileKey = Config.Bind("Player", "ProfileKey", Guid.NewGuid().ToString("D"), "Legacy private resume metadata. Missing or changed keys do not prevent friends from joining.");
                hostAddress = Config.Bind("Connection", "HostAddress", "127.0.0.1", "Direct host IP or hostname.");
                port = Config.Bind("Connection", "Port", 27020, new ConfigDescription("Host UDP port.", new AcceptableValueRange<int>(1, 65535)));
                recentHostsConfig = Config.Bind("Connection", "RecentHosts", "", "Last 8 direct host IPs or hostnames and ports. No passwords are stored.");
                recentHosts = RecentHostHistory.Parse(recentHostsConfig.Value);
                savedServersConfig = Config.Bind("Connection", "SavedServers", "", "Servers saved in the multiplayer menu (name|host|port;...). No passwords are stored.");
                savedServers = SavedServerList.Parse(savedServersConfig.Value);
                ipv6 = Config.Bind("Connection", "EnableIPv6", true, "Enable IPv6 alongside IPv4. No external traversal service is used.");
                advertiseLan = Config.Bind("Connection", "AdvertiseOnLan", false, "Answer local network discovery on UDP 27021 while hosting. No password or profile details are advertised.");
                mapRouterPort = Config.Bind("Connection", "MapRouterPort", false, "When hosting, request a temporary UDP mapping for the gameplay port from the default gateway. Router acceptance does not verify external reachability.");
                InitializeConnections();
                InitializeAppearance();
                nameText = displayName.Value; addressText = hostAddress.Value; portText = port.Value.ToString();
                profileId.Value = ProfileIdentityPolicy.ReadOrCreate(profileId.Value).ToString("D");
                profileKey.Value = ProfileIdentityPolicy.ReadOrCreate(profileKey.Value).ToString("D");
#if MONMP_DEVELOPMENT
                strictPendingInteractions = Config.Bind("Diagnostics", "StrictPendingInteractions", false, "During multiplayer, block interaction entry points whose concrete type is pending/unlisted in the coverage manifest. Diagnostic mode logs them without blocking by default.");
                debugOverlay = Config.Bind("Diagnostics", "DebugOverlay", false, "Enable the diagnostic F8 overlay and F9 world inventory. Isolated local test copies also enable these controls.");
#endif
                emoteWheelKey = Config.Bind("Social", "EmoteWheelKey", KeyCode.T, "Hold to open the emote wheel; release over a wedge to choose, or in the center/outside to cancel. T replaces native Sorry during synchronized walking or seated multiplayer.");
                if (emoteWheelKey.Value == KeyCode.F6) emoteWheelKey.Value = KeyCode.T;
#if MONMP_DEVELOPMENT
                mapCalibrationKey = Config.Bind("Social", "MapCalibrationKey", KeyCode.F7, "While the native map is open, refine player markers using five to sixteen world landmarks.");
#endif
                mapCalibration = Config.Bind("Social", "MapCalibration", UI.BuiltInMapCalibration.Value, "Saved map marker transform for the supported paper map. Empty uses the packaged default. Calibration editing is available in Development builds only.");
                HeldGrips = new HeldGripOverrides(Path.Combine(Paths.ConfigPath, "MonMP-held-grips.json"));
                if (HeldGrips.Count > 0 || HeldGrips.LoadStatus.Contains("retired")) Logger.LogInfo("MonMP held grips: " + HeldGrips.LoadStatus);
                SeatPoses = new VehicleSeatPoseOverrides(Path.Combine(Paths.ConfigPath, "MonMP-seat-poses.json"));
                CargoBounds = new CargoBoundsOverrides(Path.Combine(Paths.ConfigPath, "MonMP-cargo-bounds.json"));
                if (CargoBounds.Count > 0 || CargoBounds.LoadStatus.StartsWith("Cargo bounds were not", StringComparison.Ordinal))
                    Logger.LogInfo("MonMP cargo bounds: " + CargoBounds.LoadStatus);
                var cargoBounds = CargoBounds;
                VehicleCargoSpace.DefinedBounds = frame => cargoBounds.TryGetRuntime(frame, out var boxes) ? boxes : null;
                EmotePoses = new EmotePoseOverrides(Path.Combine(Paths.ConfigPath, "MonMP-emote-poses.json"));
                EmotePoseOverrides.Active = EmotePoses;
#if MONMP_DEVELOPMENT
                vehicleInputTraceEnabled = Config.Bind("Diagnostics", "VehicleInputTrace", false,
                    "Log raw Unity controls, vehicle action events, NWH input, shared vehicle samples and transmission state while a player drives. Observation only; no controls are changed.");
                vehicleInputTrace = new VehicleInputTrace(() => vehicleInputTraceEnabled.Value, message => Logger.LogInfo(message));
                probe.Validate(); status = probe.Status;
                InteractionAudit = new RuntimeInteractionAudit(probe.Coverage, probe.DirectoryPath,
                    () => Session?.IsRunning == true, () => strictPendingInteractions.Value);
                if (TestOptions.Enabled && !probe.Compatible) throw new InvalidOperationException("Unsupported game build; refusing a test launch without save isolation.");
#endif
                compatibility.Validate(); status = compatibility.Status;
#if MONMP_LOCAL_TEST_ISOLATION
                if (!compatibility.Compatible) throw new InvalidOperationException("Unsupported game build; refusing unisolated performance-capture startup.");
#endif
#if MONMP_PERFORMANCE_CAPTURE
                Runtime.CaptureNativeSource? captureNative = null;
                try
                {
                    string captureDirectory = Path.Combine(Paths.BepInExRootPath, "MonMP");
                    Directory.CreateDirectory(captureDirectory);
                    captureNative = new Runtime.CaptureNativeSource();
                    performanceCapture = new Runtime.PerformanceCapture(Path.Combine(captureDirectory, "performance-capture.txt"), SessionOptions.CurrentModVersion,
                        native: captureNative);
                    performanceCapture.InstallPlayerLoop();
                    Runtime.UpdateLoop.Capture = performanceCapture;
                    // Core dispatch timings inside Session.Tick become detail scopes.
                    MonMP.Core.Session.CaptureTiming.Sink = (name, ticks) => Runtime.UpdateLoop.Capture?.RecordDetail(name, ticks);
                    Logger.LogWarning("MonMP performance capture build active; not a player release. Bounded reports are written every ten seconds.");
                }
                catch (Exception captureError)
                {
                    try { captureNative?.Dispose(); } catch (Exception) { }
                    DisablePerformanceCapture("MonMP performance capture could not start; gameplay continues: " + captureError.Message);
                }
#endif
                UpdateProfiler.Log = message => Logger.LogError(message);
                if (compatibility.Compatible)
                {
                    harmony = new Harmony(PluginId);
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
                    if (TestOptions.Enabled)
                    {
                        LocalTestIsolation.Install();
                        displayName.Value = TestOptions.IsHost ? "LocalHost" : "Local" + TestOptions.Instance;
                        connectionTransport.Value = TransportKind.Direct;
                        hostAddress.Value = "127.0.0.1"; port.Value = TestOptions.Port;
                        nameText = displayName.Value; addressText = hostAddress.Value; portText = port.Value.ToString();
                        Logger.LogInfo("Local test " + TestOptions.Instance + ": isolated " + LocalTestIsolation.PatchedMethods + " managed save/settings methods. " + LocalTestIsolation.SaveRoot);
                    }
#endif
                    RecoverSelectedSaveBeforeLoad();
                    harmony.PatchAll(typeof(MonMPPlugin).Assembly);
                    Replication.SpatialSounds.PeerPosition = peer => Session?.IsRunning == true ? remotePlayers.Position(peer) : null;
                    Replication.SpatialSounds.NearestRemote = from => Session?.IsRunning == true ? remotePlayers.Nearest(from) : null;
                    Chat = new UI.CaptionChat(this);
                    spectatorCamera = new UI.SpectatorCamera(this);
                    extraSeatCamera = new UI.ExtraSeatCamera(this);
#if MONMP_DEVELOPMENT
                    PatchHealth.VerifyInstalled(probe.DirectoryPath);
#else
                    PatchHealth.VerifyInstalled(null);
#endif
                    emoteGesturePreview = new UI.EmoteGesturePreview();
                    wheelEmoteActions = new UI.WheelEmoteActions(this);
                    emoteWheel = new UI.EmoteWheel(this, () => emoteWheelKey.Value,
                        slot => wheelEmoteActions?.TryPerform(slot), emoteGesturePreview);
                }
                SceneManager.activeSceneChanged += SceneChanged;
#if MONMP_DEVELOPMENT
                if (smokeRequested && harmony != null) smoke = new RuntimeSmoke();
                if (Environment.GetCommandLineArgs().Contains("--monmp-steam-smoke-test"))
                {
                    if (!TestOptions.Enabled || !LocalTestIsolation.Installed) throw new InvalidOperationException("Steam runtime qualification requires an isolated local test instance.");
                    steamSmoke = new SteamRuntimeSmoke(probe.DirectoryPath);
                }
                if (Environment.GetCommandLineArgs().Contains("--monmp-gameplay-probe"))
                {
                    if (!TestOptions.Enabled || !LocalTestIsolation.Installed) throw new InvalidOperationException("Gameplay probe requires an isolated local test instance.");
                    gameplayProbe = new GameplayProbe(probe);
                }
                var commandLine = Environment.GetCommandLineArgs();
                if (commandLine.Contains("--monmp-saved-world-inventory-test"))
                {
                    if (!TestOptions.Enabled || !LocalTestIsolation.Installed) throw new InvalidOperationException("Saved-world inventory test requires an isolated local test instance.");
                    if (new[] { "--monmp-gameplay-probe", "--monmp-offline-save-test", "--monmp-coordinated-reload-test", "--monmp-paired-test", "--monmp-menu-join-test" }.Any(flag => commandLine.Contains(flag)))
                        throw new InvalidOperationException("Saved-world inventory test cannot run alongside another offline probe.");
                    string stage = ReadSingleArgument(commandLine, "--monmp-inventory-stage=");
                    string saveName = ReadSingleArgument(commandLine, "--monmp-inventory-save-name=");
                    string timestampText = ReadSingleArgument(commandLine, "--monmp-inventory-timestamp=");
                    if (!int.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int timestamp))
                        throw new InvalidOperationException("Saved-world inventory timestamp is invalid.");
                    savedWorldInventoryTest = new SavedWorldInventoryTest(this, probe, stage, saveName, timestamp);
                }
                if (Environment.GetCommandLineArgs().Contains("--monmp-paired-test"))
                {
                    if (!TestOptions.Enabled || !LocalTestIsolation.Installed) throw new InvalidOperationException("Paired gameplay testing requires isolated local test instances.");
                    pairedTest = new PairedGameplayTest(this, probe);
                }
                if (Environment.GetCommandLineArgs().Contains("--monmp-offline-save-test") || Environment.GetCommandLineArgs().Contains("--monmp-coordinated-reload-test"))
                {
                    if (!TestOptions.Enabled || !LocalTestIsolation.Installed) throw new InvalidOperationException("Offline save test requires an isolated local test instance.");
                    offlineSaveTest = new OfflineSaveTest(this, probe, Environment.GetCommandLineArgs().Contains("--monmp-coordinated-reload-test"));
                }
                if (Environment.GetCommandLineArgs().Contains("--monmp-menu-join-test"))
                {
                    if (!TestOptions.Enabled || !LocalTestIsolation.Installed) throw new InvalidOperationException("Menu join test requires isolated copies.");
                    menuJoinTest = new MenuJoinTest(this, probe);
                }
                if (commandLine.Contains("--monmp-crash-recovery-create") || commandLine.Contains("--monmp-crash-recovery-verify"))
                {
                    if (!TestOptions.Enabled || !LocalTestIsolation.Installed)
                        throw new InvalidOperationException("Staged-save crash recovery requires an isolated local test instance.");
                    if (commandLine.Contains("--monmp-crash-recovery-create") && commandLine.Contains("--monmp-crash-recovery-verify"))
                        throw new InvalidOperationException("Select one staged-save crash recovery phase.");
                    crashRecoveryTest = new CrashRecoveryTest(this, probe, commandLine);
                }
                // Automated runs keep subsystem failures fatal; normal play contains them.
                UpdateProfiler.Strict = pairedTest != null || offlineSaveTest != null || menuJoinTest != null || crashRecoveryTest != null ||
                    savedWorldInventoryTest != null || smokeRequested;
                UpdateProfiler.Directory = probe.DirectoryPath;
                UpdateProfiler.Log = message => Logger.LogError(message);
                UpdateProfiler.Extra = () => ProfileFrameContext() + (World?.ReceiveReport() ?? "");
                UpdateProfiler.ResetExtra = () => { try { World?.Channel.ReceiveCost.Clear(); World?.Channel.CommandCost.Clear(); } catch (InvalidOperationException) { } };
#endif
                Logger.LogInfo(status + ". Open MULTIPLAYER from the main menu to join, or from the pause menu to host.");
            }
            catch (Exception ex)
            {
#if MONMP_DEVELOPMENT
                bool testRequested = Environment.GetCommandLineArgs().Any(a => a.StartsWith("--monmp-test-", StringComparison.Ordinal));
                if (!testRequested) harmony?.UnpatchSelf();
#else
                harmony?.UnpatchSelf();
#endif
                harmony = null;
                status = "MonMP startup failed; sessions disabled. See BepInEx/LogOutput.log.";
                Logger.LogError(ex);
#if MONMP_DEVELOPMENT
                if (testRequested)
                {
                    try
                    {
                        Directory.CreateDirectory(probe.DirectoryPath);
                        File.WriteAllText(Path.Combine(probe.DirectoryPath, "startup-error.txt"), ex.ToString());
                    }
                    catch { }
                    Logger.LogError("Test isolation startup failed; closing this test instance.");
                    Application.Quit(1);
                }
#endif
#if MONMP_LOCAL_TEST_ISOLATION
                // Required call-site isolation must never degrade to primary saves
                // or PlayerPrefs, including when startup failed before Install.
                Logger.LogError("Required local-test isolation could not start; closing this isolated capture instance.");
                Application.Quit(1);
#endif
            }
        }

        private void RecoverSelectedSaveBeforeLoad()
        {
            // Awake precedes the vanilla world load. A loaded scene must never be
            // switched to native files recovered from a different generation.
            string root = NativeSaveWriter.PersistentRoot;
            if (Directory.Exists(root))
            {
                try
                {
                    int preserved = SaveSlot.PreserveLegacyStages(root);
                    if (preserved != 0) Logger.LogWarning("Preserved " + preserved + " interrupted multiplayer save stage(s) outside native save enumeration.");
                }
                catch (Exception ex) { Logger.LogWarning("Interrupted save-stage preservation was skipped: " + ex.Message); }
            }
            string saveName = NativeSaveWriter.SelectedSave;
            if (string.IsNullOrWhiteSpace(saveName)) return;
            if (!Directory.Exists(root) || !Directory.Exists(Path.Combine(root, saveName))) return;
            try
            {
                int selected = NativeSaveWriter.SelectedTimestamp;
                var recovered = SaveSlot.Recover(root, saveName, SessionOptions.SupportedGameHash);
                if (recovered.Count == 0) return;
                var candidates = recovered.Where(slot => slot.SourceTimestamp != 0 && slot.SourceTimestamp == selected).ToArray();
                string timestamps = string.Join(", ", recovered.Select(slot => slot.Timestamp.ToString(CultureInfo.InvariantCulture)));
                Logger.LogWarning("Recovered complete multiplayer save timestamp(s) " + timestamps + ".");
                if (candidates.Length == 1)
                {
                    int candidate = candidates[0].Timestamp;
                    string parent = Path.Combine(root, saveName);
                    bool newerExists = Directory.EnumerateFileSystemEntries(parent).Any(entry =>
                        int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out int stamp) && stamp > candidate);
                    if (!newerExists)
                    {
                        NativeSaveWriter.SelectTimestamp(candidate);
                        status = "Recovered complete multiplayer save " + candidate.ToString(CultureInfo.InvariantCulture) + " for loading.";
                        Logger.LogWarning(status);
                        try {
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
                            if (LocalTestIsolation.Installed) LocalTestIsolation.Save(); else
#endif
                            PlayerPrefs.Save(); }
                        catch (Exception ex) { Logger.LogWarning("Recovered save selection could not be persisted: " + ex.Message); }
                        return;
                    }
                }
                status = "Recovered complete multiplayer save timestamp(s) " + timestamps + "; choose the desired save before hosting.";
                Logger.LogWarning(status);
            }
            catch (Exception ex)
            {
                // A locked or damaged save folder must not prevent ordinary game startup.
                Logger.LogWarning("Multiplayer staged-save recovery was skipped: " + ex.Message);
            }
        }

        private void Update()
        {
            try
            {
                TickAppearance();
#if MONMP_PERFORMANCE_CAPTURE
                bool captureRunning = Session?.IsRunning == true;
                try
                {
                    bool captureLoaded = gameplay?.Player != null && Patches.NativeLoadGate.Ready;
                    string captureSynchronization = CaptureSynchronization(captureRunning, captureLoaded);
                    CaptureAvatarShortcut(captureRunning);
                    CaptureClientShortcut(captureRunning, captureSynchronization);
                    performanceCapture?.SetExperiment(captureClientRendering.Experiment != "baseline" ?
                        captureClientRendering.Experiment : captureAvatarRendering.Experiment);
                    performanceCapture?.SetRenderingSettings(Application.targetFrameRate, QualitySettings.vSyncCount, Screen.width, Screen.height,
                        captureClientRendering.ActualInterval);
                    performanceCapture?.BeginFrame(captureRunning ? Session!.IsHost ? "hosting" : "client" : "unhosted",
                        captureRunning ? Math.Max(0, Session!.Peers.Count - 1) : 0, Application.isFocused, gameplay?.Get_isGamePaused == true,
                        worldLoaded: captureLoaded, synchronization: captureSynchronization,
                        synchronizedGuests: captureRunning && Session!.IsHost ? World?.Channel.CaptureSynchronizedPeerCount ?? 0 : -1);
                    performanceCapture?.ObserveScheduledRender(captureClientRendering.ScheduledRender);
                }
                catch (Exception captureError)
                {
                    DisablePerformanceCapture("MonMP performance capture disabled; gameplay continues: " + captureError.Message);
                }
#endif
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
                if (TestOptions.Enabled && Time.unscaledTime >= nextTestPreferencesSave)
                {
                    nextTestPreferencesSave = Time.unscaledTime + 2;
                    LocalTestIsolation.Save();
#if MONMP_DEVELOPMENT
                    if (!smokeRequested) EnsureTestWindow();
#else
                    EnsureTestWindow();
#endif
                }
#endif
#if MONMP_DEVELOPMENT
                smoke?.Tick();
                steamSmoke?.Tick();
                if (steamSmoke?.Finished == true)
                {
                    Logger.LogInfo(steamSmoke.Passed ? "MONMP_STEAM_SMOKE_PASS" : "MONMP_STEAM_SMOKE_FAIL");
                    Application.Quit(steamSmoke.Passed ? 0 : 1); return;
                }
                if (smokeRequested && !smokeFinished && (Time.unscaledTime > 5 && smoke?.Passed == true || Time.unscaledTime > 15))
                {
                    smokeFinished = true;
                    string result = harmony != null && compatibility.Compatible && smoke?.Passed == true ? "MONMP_SMOKE_PASS: plugin loaded, critical patches installed, UDP handshake and player pose verified under Unity Mono; no save loaded by MonMP." : "MONMP_SMOKE_FAIL: startup or Unity Mono UDP checks failed.";
                    // BepInEx's buffered log can lose its last message when Unity immediately quits.
                    File.WriteAllText(Path.Combine(probe.DirectoryPath, "smoke-result.txt"), result);
                    Logger.LogInfo(result);
                    smoke?.Dispose(); smoke = null;
                    Application.Quit();
                    return;
                }
#endif
                if (Time.unscaledTime >= nextLookup)
                {
                    nextLookup = Time.unscaledTime + 1;
                    // Never access Singleton<Gameplay>.i: its getter creates a gameplay singleton in menus.
                    if (gameplay == null) gameplay = FindObjectOfType<Gameplay>();
                }
#if MONMP_DEVELOPMENT
                if (!DebugOverlay && menuVisible) ToggleMenu();
                if (DebugOverlay && Keyboard.current?.f8Key.wasPressedThisFrame == true &&
                    (menuVisible || HoldPositionEditorOpen || !SocialUiBlocked))
                {
                    if (HoldPositionEditorOpen) { holdPositionEditor?.Hide(); seatPositionEditor?.Hide(); emotePoseEditor?.Hide(); cargoBoundsEditor?.Hide(); } else ToggleMenu();
                }
                holdPositionEditor?.Tick();
                seatPositionEditor?.Tick();
                emotePoseEditor?.Tick();
                cargoBoundsEditor?.Tick();
                LatencySimulationKeys();
#endif
                if (harmony != null && compatibility.Compatible)
                {
                    nativeMenus ??= new UI.NativeMenus(this);
                    UpdateProfiler.Measure("NativeMenus", nativeMenuTick ??= () => nativeMenus?.Tick(gameplay));
                }
#if MONMP_DEVELOPMENT
                UpdateProfiler.BeginFrame();
#endif
                // Before the camera owners: they yield local view writes while a VR provider owns it.
                Integration.LocalVrBridge.FrameTick(this);
                if (spectatorCamera != null) UpdateProfiler.Measure("SpectatorCamera", spectatorCamera.Tick);
                extraSeatCamera?.Tick();
#if MONMP_DEVELOPMENT
                gameplayProbe?.Tick(gameplay);
                savedWorldInventoryTest?.Tick(gameplay);
                if (!SocialUiBlocked && DebugOverlay && Keyboard.current?.f9Key.wasPressedThisFrame == true) DumpWorld();
#endif
                if (Session != null) UpdateProfiler.Measure("Session", sessionTick ??= () => Session?.Tick());
                ApplyGuestMode();
                TickConnections();
                lan?.Poll();
                routerMapping.Tick(Session?.IsHost == true && Session.IsRunning);
                bool running = Session?.IsRunning == true;
                if (sessionWasRunning && !running)
                {
                    UpdateLocalDisconnect();
                    if (Session != null || World != null) StopSession(Session?.Status ?? status);
                }
                sessionWasRunning = running;
                GuestWorld?.Tick(gameplay);
                if (!running && GuestWorld?.CanDiscard == true) GuestWorld = null;
                if (running && GuestWorld == null && (gameplay == null || SceneKey() != sessionScene)) StopSession("Gameplay scene changed; session ended.");
                wheelEmoteActions?.Tick();
                emoteWheel?.Tick();
                Cheats?.Tick();
                Pings?.Tick();
                // Tool effects are sampled every frame and held briefly, so a tap shorter than the
                // pose interval (rapid firing) still reaches observers.
                if (Session?.IsReady == true && gameplay?.Player != null && ToolEffectsPlaying(gameplay.Player.Get_ActiveTool)) toolSeenAt = Time.unscaledTime;
                if (Session?.IsReady == true && (GuestWorld == null || GuestWorld.Activated) && gameplay?.Player != null && Time.unscaledTime >= nextPose)
                {
                    // On a steady 20 Hz grid: restarting the interval from now misses by a hair at low frame
                    // rates and halves the rate (a 20 fps copy sent every other frame).
                    nextPose = Mathf.Max(nextPose + 0.05f, Time.unscaledTime);
                    var player = gameplay.Player;
                    var camera = player.mainCamera;
                    if (camera != null)
                    {
                        // Verified in loaded gameplay: FirstPersonWalker_Audio is the feet/root anchor.
                        var position = gameplay.PlayerWalking != null ? gameplay.PlayerWalking.transform.position : camera.transform.position - Vector3.up * 1.6f;
                        float pitch = Mathf.DeltaAngle(0, camera.transform.eulerAngles.x);
                        bool crouched = gameplay.Get_PlayerState == PlayerState.Walking &&
#if MONMP_DEVELOPMENT
                            (TestCrouchOverride ?? (gameplay.PlayerWalking != null && gameplay.PlayerWalking.Get_IsCrouch));
#else
                            (gameplay.PlayerWalking != null && gameplay.PlayerWalking.Get_IsCrouch);
#endif
                        byte shirt = checked((byte)player.Get_VisualHands.Get_ShirtColor);
#if MONMP_DEVELOPMENT
                        shirt = TestShirtOverride ?? shirt;
#endif
                        bool flashlight = FlashlightField?.GetValue(player.Get_VisualHands) is true;
                        // The native Sorry gesture holds its hand up while the key is down.
                        var handsAnimator = HandsAnimatorField?.GetValue(player.Get_VisualHands) as Animator;
                        bool animating = handsAnimator != null && handsAnimator.isActiveAndEnabled;
                        bool sorry = animating && handsAnimator!.GetBool("Sorry");
                        var hands = PlayerHands.None;
                        if (animating)
                        {
                            if (handsAnimator!.GetBool("Map")) hands |= PlayerHands.Map;
                            if (handsAnimator.GetBool("PortableConsole")) hands |= PlayerHands.Console;
                            if (handsAnimator.GetBool("JointInHand")) hands |= PlayerHands.Joint;
                            if (handsAnimator.GetBool("SmokingJoint")) hands |= PlayerHands.Smoking;
                        }
                        // Scene compressor hoses have no SharedItems lease. Their separately
                        // accepted equipment record needs the idle equipped pose as well.
                        bool spectating = spectatorCamera?.Active == true;
                        if (!spectating && player.Get_ActiveTool is InteractableTools equippedTool && equippedTool.isEquiped)
                            hands |= PlayerHands.Equipped;
                        if (!spectating && (Time.unscaledTime - toolSeenAt < 0.2f ||
                            player.Get_ActiveTool is Tool_Air_TirePump sceneAir && sceneAir.isEquiped ||
                            player.Get_ActiveTool is Tool_Air_Impact impact && impact.isEquiped))
                            hands |= PlayerHands.Tool;
                        float yaw = camera.transform.eulerAngles.y;
                        // A VR provider may report the HMD's world yaw/pitch when the native camera
                        // transform is not the head. Controller aim never enters the legacy head aim.
                        if (Integration.LocalInteraction.TryHead(out float headYaw, out float headPitch)) { yaw = headYaw; pitch = headPitch; }
                        var rest = gameplay.Get_IsDead ? PlayerRest.Down : gameplay.Get_IsSleeping ? PlayerRest.Sleeping : PlayerRest.None;
                        if (rest == PlayerRest.Sleeping)
                        {
                            // A bed's sleep position may lay the walker down; send the eyes and the
                            // direction along the bed towards the feet instead of the walker frame.
                            var sleepPosition = World?.Sleep.LocalSleepingBed?.SleepPosition;
                            position = sleepPosition != null ? SleepingBedPose.Eyes(sleepPosition) : camera.transform.position;
                            var along = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up);
                            if (along.sqrMagnitude < 0.25f) along = -Vector3.ProjectOnPlane(camera.transform.up, Vector3.up);
                            if (along.sqrMagnitude > 1e-4f) yaw = Quaternion.LookRotation(along).eulerAngles.y;
                            pitch = 0;
                        }
                        var emote = Patches.LocalEmotes.NextPose();
                        if (needs == null) needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>();
                        byte drunk = 0; bool hungover = false;
                        if (needs != null)
                        {
                            try { drunk = (byte)Mathf.Clamp(Mathf.RoundToInt(needs.GetRealDrunkness * PlayerPose.MaxDrunk), 0, PlayerPose.MaxDrunk); hungover = needs.Get_HangoverDebuff > 0; }
                            catch (NullReferenceException) { }
                        }
                        var pose = new PlayerPose(position.x, position.y, position.z, yaw, Mathf.Clamp(pitch, -90, 90), (byte)gameplay.Get_PlayerState, crouched, shirt, flashlight,
                            sorry, emote.Sequence, emote.Kind, emote.Variant,
                            hands | (!spectating && Cursor.lockState == CursorLockMode.Locked && !gameplay.Get_isGamePaused &&
                                Singleton<GameAssets>.i?.inputs?.Player.Zoom.ReadValue<float>() > 0 ? PlayerHands.Zoom : PlayerHands.None),
                            rest, drunk, hungover, model: MenuPlayerModel, appearance: MenuAppearance).WithRecentAction(Activity.RecentAction);
                        if (spectating)
                        {
                            // Keep the native walker root authoritative; the separate head point
                            // follows the orbit camera only for remote head-only presentation.
                            var head = camera.transform.position;
                            pose = SpectatorPoseBuilder.FromNative(pose, head.x, head.y, head.z);
                        }
                        // Standing on a shared vehicle's bed or a bus floor: also send the feet in its space (#249).
                        if (gameplay.Get_PlayerState == PlayerState.Walking && rest == PlayerRest.None && gameplay.PlayerWalking != null &&
                            Items?.Carriers?.StandingOn(position, gameplay.PlayerWalking.transform, out var carrier, out var carrierFrame) == true)
                        {
                            var local = carrierFrame.InverseTransformPoint(position);
                            // Carried by a presented (interpolated) vehicle, the feet slip a few centimetres
                            // over its bed each frame; watchers saw that as walking in place (#249). Without
                            // move input the feet hold their spot on the carrier until they really drift.
                            var assets = Singleton<GameAssets>.i;
                            bool moving = assets?.inputs == null || Mathf.Abs(assets.inputs.Player.MoveVertical.ReadValue<float>()) > 0.1f ||
                                Mathf.Abs(assets.inputs.Player.MoveHorizontal.ReadValue<float>()) > 0.1f;
                            if (!moving && riderCarrier == carrier && (local - riderLocal).sqrMagnitude < 0.35f * 0.35f) local = riderLocal;
                            riderCarrier = carrier; riderLocal = local;
                            if (local.sqrMagnitude < PlayerPose.MaxCarrierOffset * PlayerPose.MaxCarrierOffset / 4) pose = pose.WithCarrier(carrier, local.x, local.y, local.z);
                        }
                        else riderCarrier = 0;
                        // Publish the current aim mode. Commands carry the mode sampled with their own ray.
                        if (!spectating && rest == PlayerRest.None && Integration.LocalInteraction.IndependentAim)
                            pose = pose.WithIndependentAim(true);
                        Session.SendPose(pose);
                        UpdateProfiler.Measure("SelfViewPose", () => ShowSelf(gameplay, pose));
                    }
                }
                World?.Update();
                UpdateProfiler.Run("RemotePlayers", remotePlayerTick ??= remotePlayers.Update);
                UpdateProfiler.Measure("MapMarkers", mapMarkerTick ??= UpdatePlayerMapMarkers);
                if (selfShown != null) UpdateProfiler.Run("SelfView", selfViewTick ??= selfView.Update);
#if MONMP_PERFORMANCE_CAPTURE
                try { performanceCapture?.EndFrame(Time.unscaledDeltaTime); }
                catch (Exception captureError)
                {
                    DisablePerformanceCapture("MonMP performance capture disabled; gameplay continues: " + captureError.Message);
                }
#endif
#if MONMP_DEVELOPMENT
                if (running) UpdateProfiler.EndFrame(Time.unscaledTime, Time.unscaledDeltaTime,
                    Application.isFocused, gameplay?.Get_isGamePaused == true);
                vehicleInputTrace?.Tick(gameplay, Session, Vehicles);
#endif
                UpdateLocalDisconnect();
                UpdateHostExit();
#if MONMP_DEVELOPMENT
                pairedTest?.Tick(gameplay);
                offlineSaveTest?.Tick(gameplay);
                menuJoinTest?.Tick(gameplay);
                crashRecoveryTest?.Tick(gameplay);
                ExtensionTick?.Invoke(gameplay);
#endif
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
#if MONMP_DEVELOPMENT
                pairedTest?.Fail(ex.ToString());
                offlineSaveTest?.Fail(ex.ToString());
                savedWorldInventoryTest?.Fail(ex.ToString());
                menuJoinTest?.Fail(ex.ToString());
#endif
                StopSession("Session stopped after an error. See BepInEx/LogOutput.log.");
#if MONMP_DEVELOPMENT
                if (smokeRequested) { smoke?.Dispose(); smoke = null; smokeFinished = true; Logger.LogError("MONMP_SMOKE_FAIL: runtime exception."); Application.Quit(); }
#endif
            }
        }

        private void UpdatePlayerMapMarkers()
        {
            if (Session?.IsRunning != true) return;
            if (mapMarkerSession != Session)
            {
                playerMapMarkers?.Dispose();
                playerMapMarkers = new UI.PlayerMapMarkers();
                playerMapMarkers.Bind(Session, () => gameplay?.Player?.Get_VisualHands,
                    RemoteAvatar, () => gameplay!.PlayerWalking != null
                        ? gameplay.PlayerWalking.transform.position
                        : gameplay.Player.mainCamera.transform.position - Vector3.up * 1.6f,
                    () => Session?.IsReady == true && World?.Channel.FullySynchronized == true &&
                        !SocialUiBlocked && (!CleanFrame || CleanFrameNativeMap) && World?.SaveGate.Active != true && GuestWorld?.HoldPresentation != true &&
                        gameplay?.Player?.mainCamera != null && !gameplay.Get_isGamePaused &&
                        !gameplay.Get_IsDead && !gameplay.Get_IsSleeping,
                    mapCalibration,
#if MONMP_DEVELOPMENT
                    mapCalibrationKey,
#endif
                    message => Logger.LogInfo(message), remotePlayers.MapPosition);
                mapMarkerSession = Session;
            }
            playerMapMarkers?.Tick();
        }


#if MONMP_DEVELOPMENT
        private static string ReadSingleArgument(string[] arguments, string prefix)
        {
            var matches = arguments.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].Length == prefix.Length)
                throw new InvalidOperationException("Expected one command-line value for " + prefix);
            return matches[0].Substring(prefix.Length);
        }
#endif


        private void OnGUI()
        {
#if MONMP_PERFORMANCE_CAPTURE
            using var guiCapture = Runtime.CaptureDetail.Measure("OnGUI.Total");
            if ((performanceCapture != null && Session?.IsRunning == true && Session.IsHost) || captureAvatarRendering.Experiment != "baseline")
                GUI.Label(new Rect(12, 44, Mathf.Max(200, Screen.width - 24), 26), captureAvatarRendering.Status);
            if ((performanceCapture != null && Session?.IsRunning == true && !Session.IsHost) || captureClientRendering.Experiment != "baseline")
                GUI.Label(new Rect(12, 70, Mathf.Max(200, Screen.width - 24), 26), captureClientRendering.Status);
#endif
#if MONMP_DEVELOPMENT
            if (CleanFrame)
            {
                if (CleanFrameNames && Session?.IsRunning == true) remotePlayers.DrawNames(gameplay?.Player?.mainCamera);
                return;
            }
#endif
            emoteWheel?.Draw();
#if MONMP_DEVELOPMENT
            playerMapMarkers?.Draw();
#endif
            if (Session?.IsRunning == true)
            {
#if MONMP_DEVELOPMENT
                bool synchronizing = Session.State == SessionState.Connected && World?.Channel.FullySynchronized != true;
                string phase = synchronizing ? "Synchronizing world" : Session.State.ToString();
                string latency = Session.IsHost ? "Host" : "RTT " + Session.MetricsFor(0).RoundTripTimeMs + " ms";
                if (DebugOverlay) GUI.Box(new Rect(Screen.width / 2f - 300, 6, 600, 30), "MonMP diagnostics | " + phase + " | " + Session.Peers.Count + " players | " + latency + " | F8" +
                    (latencySimulation.Enabled ? " | SIM " + latencySimulation.DelayMilliseconds + "±" + latencySimulation.JitterMilliseconds + " ms" : ""));
#endif
                remotePlayers.DrawNames(gameplay?.Player?.mainCamera);
            }
#if MONMP_DEVELOPMENT
            if (holdPositionEditor?.Visible == true) holdPositionEditor.Draw();
            else if (seatPositionEditor?.Visible == true) seatPositionEditor.Draw();
            else if (emotePoseEditor?.Visible == true) emotePoseEditor.Draw();
            else if (cargoBoundsEditor?.Visible == true) cargoBoundsEditor.Draw();
            else if (DebugOverlay && menuVisible) window = GUILayout.Window(19032026, window, DrawMenu, "MonMP " + SessionOptions.CurrentModVersion + " — diagnostics");
#endif
        }


#if MONMP_DEVELOPMENT
        private string ProfileFrameContext()
        {
            var body = gameplay?.PlayerWalking?.GetComponent<Rigidbody>();
            return "frame settings: vSync=" + QualitySettings.vSyncCount + " targetFPS=" + Application.targetFrameRate +
                " fixedStep=" + (Time.fixedDeltaTime * 1000f).ToString("0.00") + "ms maxStep=" +
                (Time.maximumDeltaTime * 1000f).ToString("0.00") + "ms inputUpdate=" + InputSystem.settings.updateMode +
                " walkingInterpolation=" + (body != null ? body.interpolation.ToString() : "unavailable") + "\n";
        }
#endif


        // Per-frame handlers keep running after an error, so repeats are summarized instead of
        // writing a stack trace every frame or physics step.
        private Runtime.RepeatingErrorLog? frameErrors;
        private void FrameError(string site, Exception ex) =>
            (frameErrors ??= new Runtime.RepeatingErrorLog(message => Logger.LogError(message))).Report(site, ex, Time.unscaledTime);

        private void FixedUpdate()
        {
#if MONMP_PERFORMANCE_CAPTURE
            using var fixedCapture = Runtime.CaptureDetail.Measure("FixedUpdate.Total");
#endif
            Replication.WorldScans.NotePhysicsStep();
            try
            {
                UpdateProfiler.Measure("Physics", physicsTick ??= () =>
                {
                    // Remote carriers record this step's kinematic targets first; pinned
                    // cargo then follows the same step instead of trailing one behind.
                    KinematicCarrierMotion.BeginStep();
                    Vehicles?.FixedUpdate(); World?.BoatMotion.FixedUpdate(); World?.Trailers.FixedUpdate(); World?.Items.BeforePhysics();
                });
            }
            catch (Exception ex) { FrameError("physics step", ex); }
        }

        private void LateUpdate()
        {
#if MONMP_PERFORMANCE_CAPTURE
            using var lateCapture = Runtime.CaptureDetail.Measure("LateUpdate.Total");
#endif
            try
            {
#if MONMP_PERFORMANCE_CAPTURE
                using var barrowCapture = Runtime.CaptureDetail.Measure("LateUpdate.Wheelbarrows");
#endif
                World?.Wheelbarrows.ConstrainLocalMotion(); SharedCottagePurchase.Active?.ConstrainBarrowMotion();
            }
            catch (Exception ex) { FrameError("wheelbarrow constraint", ex); }
            try { UpdateProfiler.Measure("UnderglowLate", underglowLateTick ??= () => World?.Underglow.LateUpdate()); }
            catch (Exception ex) { FrameError("underglow late update", ex); }
            try
            {
#if MONMP_PERFORMANCE_CAPTURE
                using var volumeCapture = Runtime.CaptureDetail.Measure("LateUpdate.VehicleVolume");
#endif
                Patches.VehicleVolumePatch.Enforce(gameplay);
            }
            catch (Exception ex) { FrameError("vehicle volume", ex); }
            try { UpdateProfiler.Measure("LateCarry", carryLateTick ??= () => Items?.LateCarry()); }
            catch (Exception ex) { FrameError("late carry", ex); }
            try { wheelEmoteActions?.LateTick(); }
            catch (Exception ex) { FrameError("emote wheel late update", ex); }
#if MONMP_DEVELOPMENT
            try
            {
                if (nativeSignEmoteFixture == null && NativeSignEmoteFixture.Requested)
                    nativeSignEmoteFixture = new NativeSignEmoteFixture(this);
                nativeSignEmoteFixture?.Tick(gameplay);
            }
            catch (Exception ex) { FrameError("sign emote fixture", ex); }
            try { ExtensionLateTick?.Invoke(gameplay); }
            catch (Exception ex) { FrameError("extension late update", ex); }
#endif
            try
            {
#if MONMP_PERFORMANCE_CAPTURE
                using var remoteCapture = Runtime.CaptureDetail.Measure("LateUpdate.RemotePlayers");
#endif
                remotePlayers.LateTick();
            }
            catch (Exception ex) { FrameError("remote players late update", ex); }
            try
            {
#if MONMP_PERFORMANCE_CAPTURE
                using var selfCapture = Runtime.CaptureDetail.Measure("LateUpdate.SelfView");
#endif
                if (selfShown != null) selfView.LateTick();
            }
            catch (Exception ex) { FrameError("self view late update", ex); }
            try { extraSeatCamera?.LateTick(); }
            catch (Exception ex) { Logger.LogWarning("Extra seat camera failed: " + ex.Message); extraSeatCamera?.Reset(); }
            try { if (spectatorCamera != null) UpdateProfiler.Measure("SpectatorCameraLate", spectatorCamera.LateTick); }
            catch (Exception ex) { Logger.LogWarning("Spectator camera late update failed: " + ex.Message); spectatorCamera?.Stop(); }
            try { spectatorCamera?.Draw(); }
            catch (Exception ex) { Logger.LogWarning("Spectator hint update failed: " + ex.Message); }
            Activity.TickLocal(gameplay, Session?.IsRunning == true && !SocialUiBlocked &&
                gameplay?.Get_isGamePaused != true && gameplay?.Get_IsDead != true && gameplay?.Get_IsSleeping != true);
            UpdateTimeSkipPresentation();
            playerStatus ??= new UI.PlayerStatus();
            UpdateProfiler.Measure("PlayerStatus", playerStatusTick ??= () => playerStatus.Tick(this));
            Chat?.Tick();
            Pings?.LateTick();
            frameErrors?.Flush(Time.unscaledTime);
#if MONMP_PERFORMANCE_CAPTURE
            try
            {
                if (!(performanceCapture != null && Session?.IsRunning == true && Session.IsHost) && captureAvatarRendering.Suppressed)
                    captureAvatarRendering.Reset();
                captureAvatarRendering.Tick(remotePlayers, selfView);
                bool captureClientEligible = performanceCapture != null && Session?.IsRunning == true && !Session.IsHost &&
                    CaptureSynchronization(true, gameplay?.Player != null && Patches.NativeLoadGate.Ready) == "ready";
                captureClientRendering.Tick(captureClientEligible);
            }
            catch (Exception captureError)
            {
                DisablePerformanceCapture("MonMP avatar capture comparison disabled; gameplay continues: " + captureError.Message);
            }
#endif
#if MONMP_DEVELOPMENT
            // Automated copies never need mouse look or audible game ambience. Unity's native
            // walker can relock the OS cursor after the launcher hides its window.
            if (TestOptions.Enabled && (smokeRequested || pairedTest != null || gameplayProbe != null ||
                offlineSaveTest != null || savedWorldInventoryTest != null || menuJoinTest != null || crashRecoveryTest != null || nativeSignEmoteFixture != null))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                AudioListener.volume = 0f;
            }
#endif
        }


#if MONMP_DEVELOPMENT
        private void DrawMenu(int id)
        {
            var label = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };
            GUILayout.Label(status, label);
            if (TestOptions.Enabled) GUILayout.Label("Local test: " + TestOptions.Instance + " | Saves isolated to this copy", label);
            GUILayout.Space(8);
            GUILayout.Label("This preview shares selected items, vehicles, buildings and world systems. Check the shipped README for current coverage and use test saves.", label);
            if (Lights != null) GUILayout.Label("Shared lights: " + Lights.Count + (Lights.Ready ? " ready" : " synchronizing"), label);
            if (World?.Sleep.LocalWaiting == true && GUILayout.Button("Get out of bed")) World.Sleep.RequestGetUp();
            if (World != null) GUILayout.Label("Shared doors: " + World.Doors.Count + (World.Doors.Ready ? " ready" : " synchronizing"), label);
            if (Items != null) GUILayout.Label("Shared items: " + Items.Count + (Items.Ready ? " ready" : " synchronizing"), label);
            if (Vehicles != null) GUILayout.Label("Shared vehicles: " + Vehicles.Count + (Vehicles.Ready ? " ready" : " synchronizing"), label);
            if (World != null)
            {
                GUILayout.Label("World state: " + World.SynchronizationStatus + " | Last repair: " + World.LastRepairReason, label);
                GUILayout.Label("Shared records: " + World.Channel.RecordCount + " | Snapshot queue: " + World.Channel.PendingSnapshotPackets + " | Pending changes: " + World.Channel.PendingDeltas, label);
                if (Session?.IsHost == false && GUILayout.Button("Repair shared world state")) World.RepairWorld();
            }
            if (Session?.IsRunning == true)
            {
                GUILayout.Label("Session: " + (Session.IsHost ? "host" : "guest") + " | peer " + Session.LocalPeerId + " | epoch " + Session.Epoch.ToString("N").Substring(0, 8), label);
                GUILayout.Label("Traffic: " + (Session.BytesSentPerSecond / 1024d).ToString("F1") + " KiB/s up, " +
                    (Session.BytesReceivedPerSecond / 1024d).ToString("F1") + " KiB/s down", label);
                peerScroll = GUILayout.BeginScrollView(peerScroll, GUILayout.MaxHeight(110));
                foreach (var peer in Session.Peers.Values.Where(p => p.Id != Session.LocalPeerId).OrderBy(p => p.Id))
                {
                    var metrics = Session.MetricsFor(peer.Id);
                    GUILayout.Label(peer.DisplayName + " (#" + peer.Id + "): RTT " + metrics.RoundTripTimeMs +
                        " ms | jitter " + metrics.JitterMs.ToString("F0") + " ms | loss " +
                        metrics.PacketLossPercent.ToString("F1") + "% | reliable queue " + metrics.ReliableQueuePackets, label);
                }
                GUILayout.EndScrollView();
                GUILayout.Label("Last peer event: " + Session.LastPeerEvent, label);
            }
            DrawLatencySimulation(label);
            bool running = Session?.IsRunning == true;
            GUI.enabled = !running;
            nameText = Field("Player name", nameText, 48);
            addressText = Field("Host IP / name", addressText, 200);
            portText = Field("UDP port", portText, 5);
            if (recentHosts.Entries.Count != 0)
            {
                if (GUILayout.Button((recentHostsExpanded ? "Hide" : "Show") + " recent hosts (" + recentHosts.Entries.Count + ")"))
                    recentHostsExpanded = !recentHostsExpanded;
                if (recentHostsExpanded)
                {
                    GUILayout.Label("Selecting a host only fills the fields:", label);
                    recentHostsScroll = GUILayout.BeginScrollView(recentHostsScroll, GUILayout.MaxHeight(88));
                    foreach (var endpoint in recentHosts.Entries)
                    {
                        if (GUILayout.Button(endpoint.Display, new GUIStyle(GUI.skin.button) { richText = false }))
                        {
                            addressText = endpoint.Host;
                            portText = endpoint.Port.ToString(CultureInfo.InvariantCulture);
                            recentHostStatus = "Selected " + endpoint.Display + ". Review the fields and choose Join preview to connect.";
                            recentHostsExpanded = false;
                        }
                    }
                    GUILayout.EndScrollView();
                }
                if (recentHostStatus.Length > 0) GUILayout.Label(recentHostStatus, label);
            }
            GUILayout.BeginHorizontal(); GUILayout.Label("Password", GUILayout.Width(120));
            password = GUILayout.PasswordField(password, '*', 64); GUILayout.EndHorizontal();
            advertiseLan.Value = GUILayout.Toggle(advertiseLan.Value, "Advertise this host on the local network");
            mapRouterPort.Value = GUILayout.Toggle(mapRouterPort.Value, "Request a temporary router mapping while hosting");
            if (routerMapping.Status.Length != 0) GUILayout.Label(routerMapping.Status, label);
            if (GUILayout.Button("Find LAN sessions")) SearchLan();
            if (!running && lan != null)
            {
                GUILayout.Label(lanStatus, label);
                lanScroll = GUILayout.BeginScrollView(lanScroll, GUILayout.MaxHeight(100));
                foreach (var host in lan.Hosts.OrderBy(h => h.Session.Name).ThenBy(h => h.Endpoint.ToString()))
                {
                    GUI.enabled = host.Session.Compatible;
                    string details = host.Session.Name + " | " + host.Session.Players + " players | " + host.Endpoint +
                        (host.Session.PasswordRequired ? " | password" : "") + (host.Session.Compatible ? "" : " | incompatible build");
                    if (GUILayout.Button(details, new GUIStyle(GUI.skin.button) { richText = false }))
                    { addressText = host.Endpoint.Address.ToString(); portText = host.Endpoint.Port.ToString(); lanStatus = "Selected " + host.Session.Name; }
                }
                GUILayout.EndScrollView(); GUI.enabled = true;
            }
            previewAcknowledged = GUILayout.Toggle(previewAcknowledged, "I understand this preview shares only the features listed above.");
            bool canStart = !running && previewAcknowledged && harmony != null && compatibility.Compatible && GuestWorld == null;
            GUI.enabled = canStart && gameplay?.Player != null && Patches.NativeLoadGate.Ready;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Host preview")) StartSession(true);
            GUI.enabled = canStart && (gameplay?.Player != null && Patches.NativeLoadGate.Ready || FindObjectOfType<MainMenu_Manager>() != null);
            if (GUILayout.Button("Join preview")) StartSession(false);
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (gameplay?.Player == null) GUILayout.Label("Load a test save to host. Joining from this menu loads the host's scene without reading your save.", label);
            if (running)
            {
                GUILayout.Label("Players: " + string.Join(", ", Session!.Peers.Values.Select(p => p.DisplayName)), label);
                if (GUILayout.Button("Disconnect")) MenuDisconnect();
            }
            if (GUILayout.Button("Write hierarchy and interactable report (F9)")) DumpWorld();
            GUI.enabled = gameplay?.Player != null && Patches.NativeLoadGate.Ready;
            if (GUILayout.Button("Calibrate held-item grips"))
            {
                emotePoseEditor?.Hide();
                seatPositionEditor?.Hide();
                holdPositionEditor ??= new UI.HoldPositionEditor(HeldGrips, ItemPrefabCatalog.Load(probe.DirectoryPath), gameplay!);
                holdPositionEditor.Show();
            }
            if (GUILayout.Button("Calibrate vehicle seat poses"))
            {
                emotePoseEditor?.Hide();
                holdPositionEditor?.Hide();
                seatPositionEditor ??= new UI.HoldPositionEditor(HeldGrips, SeatPoses, World, gameplay!);
                seatPositionEditor.Show();
            }
            if (GUILayout.Button("Calibrate emote poses"))
            {
                holdPositionEditor?.Hide(); seatPositionEditor?.Hide();
                emotePoseEditor ??= new UI.EmotePoseEditor(EmotePoses, gameplay!);
                emotePoseEditor.Show();
            }
            if (GUILayout.Button("Define vehicle cargo freeze bounds"))
            {
                holdPositionEditor?.Hide(); seatPositionEditor?.Hide(); emotePoseEditor?.Hide();
                if (menuVisible) ToggleMenu();
                cargoBoundsEditor ??= new UI.CargoBoundsEditor(this, CargoBounds, gameplay!);
                cargoBoundsEditor.Show();
            }
            GUI.enabled = true;
            if (GUILayout.Button("Close (F8)")) ToggleMenu();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }
#endif


        private static string Field(string label, string value, int length)
        {
            GUILayout.BeginHorizontal(); GUILayout.Label(label, GUILayout.Width(120));
            string result = GUILayout.TextField(value, length); GUILayout.EndHorizontal(); return result;
        }

        private void StartSession(bool host, bool? useSteam = null, ulong lobbyId = 0)
        {
            try
            {
                if (!previewAcknowledged || harmony == null || !compatibility.Compatible) return;
                if (Session?.IsRunning == true) { status = "Disconnect from the current session before starting another."; return; }
                if (LocalDisconnectPending) { status = "Disconnecting; wait for ownership to return to the host."; return; }
                bool steam = useSteam ?? MenuUseSteam;
                if (steam && !MenuSteamAvailable) throw new InvalidOperationException(MenuSteamStatus);
                if (steam && !host && lobbyId == 0) throw new ArgumentException("Enter a Steam lobby ID or accept a friend invite.");
                if (!host) nameText = PlayerNamePolicy.RequireCustom(nameText);
                bool menuJoin = !host && SceneManager.GetActiveScene().buildIndex == 1 && FindObjectOfType<MainMenu_Manager>() != null;
                if (!menuJoin && gameplay?.Player == null) return;
                if (!menuJoin && gameplay?.Get_IsDead == true) throw new InvalidOperationException("Return to the main menu before starting a new session after death.");
                if (!menuJoin && !Patches.NativeLoadGate.Ready) throw new InvalidOperationException("Wait for the native world to finish loading before starting a session.");
                if (!menuJoin && SceneManager.GetActiveScene().buildIndex != 2) throw new InvalidOperationException("Sessions require the master world; finish the tutorial first.");
                if (GuestWorld != null) throw new InvalidOperationException("Wait until the remote world has returned to the main menu.");
                int parsed = port.Value;
                if (!steam && (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed < 1 || parsed > 65535)) throw new ArgumentException("UDP port must be 1 to 65535.");
                DirectHostEndpoint endpoint = default;
                if (!steam && !host && !DirectHostEndpoint.TryCreate(addressText, parsed, out endpoint))
                    throw new ArgumentException("Enter a valid IPv4 address, IPv6 address, or hostname and UDP port.");
                Wire.ValidateName(nameText.Trim());
                StopSession("Starting session...");
                BeginMenuJoinAttempt(host, steam, lobbyId, endpoint);
                displayName.Value = nameText.Trim();
                if (!steam) { hostAddress.Value = addressText.Trim(); port.Value = parsed; }
                sessionScene = SceneKey();
                if (menuJoin) GuestWorld = new GuestWorldLoader(this);
                else World = new SharedWorld(gameplay!, host, DiagnosticsDirectory, Consumption);
                var spawn = Replication.NativePlayerSpawn.For(gameplay);
                Guid localProfile = ProfileIdentityPolicy.ReadOrCreate(profileId.Value);
                if (host) localProfile = RecoverHostProfile(localProfile, displayName.Value);
                var options = new SessionOptions
                {
                    DisplayName = displayName.Value, ProfileId = localProfile, GameHash = compatibility.GameHash,
                    Scene = sessionScene, Password = password, EnableIPv6 = ipv6.Value, WorldCatalog = World?.CatalogHash ?? "",
                    LoadWorldAfterHandshake = menuJoin, HardMode = gameplay?.Get_isHardMode == true, Permadeath = gameplay?.Get_isPermadeath == true,
                    HardModeLayout = gameplay != null ? Replication.HardModeWorldLayout.Capture(gameplay) : (byte)0,
                    HiddenVehicles = Replication.HardModeWorldLayout.CaptureHiddenVehicles(),
                    StoredVehicles = host ? Replication.HardModeWorldLayout.SessionStored : 0,
                    Spawn = spawn
                };
                options.ColorRgb = ProfileColor(options.ProfileId);
                profileId.Value = options.ProfileId.ToString("D");
                options.ProfileKey = ProfileIdentityPolicy.ReadOrCreate(profileKey.Value);
                profileKey.Value = options.ProfileKey.ToString("D");
                if (host && World != null) options.SavedProfileIdentities = World.SavedProfileIdentities;
                Session = new NetworkSession(options);
                Cheats = new SharedCheats(this, Session);
                Pings = new UI.SocialPings(this, Session, slot => wheelEmoteActions?.TryPerform(slot) == true);
                World?.Attach(Session);
                Session.Diagnostic += message => Logger.LogWarning(message);
                Session.StatusChanged += message =>
                {
                    status = message; Logger.LogInfo(message);
                    // Welcome may resolve a different identity in this host's world or
                    // separate a live duplicate. Keep the installation's own identity
                    // stable for its local saves and the next reconnect attempt.
                    if (message.StartsWith("Invalid") && Session?.LastPacketError.Length > 0)
                        Logger.LogWarning("Packet validation detail: " + Session.LastPacketError);
                };
                // Welcome announces the host before snapshot dispatch in this same
                // transport poll. Food visuals must already see the host's mode.
                Session.PeerJoined += peer => { ApplyGuestMode(); remotePlayers.Add(peer); Logger.LogInfo("Peer joined: " + peer.Id + " " + peer.DisplayName); };
                Session.PeerLeft += remotePlayers.Remove;
                Session.PeerLeft += _ =>
                {
                    if (Session?.IsHost != true) return;
                    Logger.LogInfo("Peer event: " + Session.LastPeerEvent);
                    BeginDisconnectReport("Peer disconnected: " + Session.LastPeerEvent);
                    FinishDisconnectReport();
                };
                Session.PoseSampleReceived += remotePlayers.ApplyTimed;
                Activity.Clear();
                Session.PoseReceived += Activity.Observe;
                Session.PeerLeft += Activity.Remove;
                Session.ChatReceived += message => { Chat?.Receive(message); remotePlayers.Caption(message.PeerId, message.Text); };
                previousRunInBackground = Application.runInBackground;
                if (host)
                {
                    if (steam) Session.Host(
#if MONMP_DEVELOPMENT
                        DebugSessionTransport(
#endif
                        steamTransport = steamSessions!.CreateHost(displayName.Value, NetworkSession.MaxGuests + 1, steamForceRelay.Value)
#if MONMP_DEVELOPMENT
                        )
#endif
                        );
#if MONMP_DEVELOPMENT
                    else Session.Host(DebugSessionTransport(new LiteNetTransport(true, ipv6.Value, parsed)));
#else
                    else Session.Host(parsed);
#endif
                    Cheats.Started();
                    World!.PaintPanels.HostStarted();
                }
                else
                {
                    if (steam) Session.Join(
#if MONMP_DEVELOPMENT
                        DebugSessionTransport(
#endif
                        steamTransport = steamSessions!.CreateGuest(lobbyId, steamForceRelay.Value)
#if MONMP_DEVELOPMENT
                        )
#endif
                        );
#if MONMP_DEVELOPMENT
                    else Session.Join(DebugSessionTransport(new LiteNetTransport(false, ipv6.Value, 0, endpoint.Host, endpoint.Port)));
#else
                    else Session.Join(endpoint.Host, endpoint.Port);
#endif
                    Cheats.Started();
                    if (!steam && !IsLocalTest)
                    {
                        try
                        {
                            recentHosts = recentHosts.Remember(endpoint.Host, endpoint.Port);
                            recentHostsConfig.Value = recentHosts.Serialize();
                            Config.Save();
                        }
                        catch (Exception ex) { Logger.LogWarning("Joined host, but could not save recent-host history: " + ex.Message); }
                    }
                }
                if (!steam && host && mapRouterPort.Value && !IsLocalTest) routerMapping.Start(Session.Port);
                if (!steam && host && (advertiseLan.Value || IsAutomatedLanTest)) StartAdvertisement();
                Logger.LogInfo("MonMP active transport: " + Session.TransportDescription);
                Application.runInBackground = true;
                sessionWasRunning = true;
                if (gameplay?.Get_isGamePaused == true) Time.timeScale = 1;
            }
            catch (Exception ex) { StopSession(ex.Message); Logger.LogWarning(ex.ToString()); }
        }

        private static uint ProfileColor(Guid id)
        {
            var bytes = id.ToByteArray();
            return (uint)((80 + bytes[0] % 176) << 16 | (80 + bytes[1] % 176) << 8 | (80 + bytes[2] % 176));
        }
        internal bool AllowNativeExit(EscapeMenuV2 menu)
        {
            if (Session?.IsRunning != true) return true;
            if (LocalDisconnectPending) return false;
            if (!Session.IsHost)
            {
                if (World == null) { StopSession("Guest left the world."); return true; }
                BeginLocalDisconnect("Guest left the world.", menu); return false;
            }
            if (HostExitPending) return false;
            var world = World;
            if (world?.Saves == null) { ReportSaveStatus("Cannot leave yet: the host world is not ready to save."); return false; }
            PrepareLocalDisconnect();
            world.Saves.Request(House_Location.SpecialMenu);
            if (!world.Saves.Pending)
            {
                if (world.Saves.LastSaveSucceeded)
                { StopSession("Host saved and left the world."); return true; }
                CancelLocalDisconnect();
                if (world.Saves.LastFailure != "")
                {
                    failedExitMenu = menu; failedExitWorld = world;
                    failedExitButton = (int)AccessTools.Field(typeof(EscapeMenuV2), "QuitButtonClicked").GetValue(menu)!;
                    ReportSaveStatus("Exit cancelled: the multiplayer save could not start (" + world.Saves.LastFailure +
                        "). Try again to retry, or choose Multiplayer > Leave without saving.");
                }
                return false;
            }
            failedExitMenu = null; failedExitWorld = null;
            pendingExitButton = (int)AccessTools.Field(typeof(EscapeMenuV2), "QuitButtonClicked").GetValue(menu)!;
            pendingExitMenu = menu; pendingExitWorld = world;
            Logger.LogInfo("Save status: Saving multiplayer progress before leaving...");
            return false;
        }
        private void UpdateHostExit()
        {
            if (!HostExitPending) return;
            var world = pendingExitWorld!;
            if (World != world || pendingExitMenu == null || Session?.IsHost != true)
            { pendingExitWorld = null; pendingExitMenu = null; CancelLocalDisconnect(); return; }
            if (world.Saves!.Pending) return;
            var menu = pendingExitMenu; int button = pendingExitButton;
            pendingExitWorld = null; pendingExitMenu = null;
            if (world.Saves.LastFailure != "" || !world.Saves.LastSaveSucceeded)
            {
                CancelLocalDisconnect();
                failedExitMenu = menu; failedExitWorld = world; failedExitButton = button;
                string reason = world.Saves.LastFailure != "" ? world.Saves.LastFailure : "the save round ended " + world.SaveRounds!.Phase;
                ReportSaveStatus("Exit cancelled: the multiplayer save failed (" + reason + "). Try again to retry, or choose Multiplayer > Leave without saving.");
                return;
            }
            StopSession("Host saved and left the world.");
            AccessTools.Field(typeof(EscapeMenuV2), "QuitButtonClicked").SetValue(menu, button);
            menu.Button_QuitGameConfirmation();
        }
        // The native multiplayer menus (main menu join, pause menu host) go through these.
        internal string MenuStatus => GuestWorld?.Cancelled == true && Session?.IsRunning != true
            ? status + (status.EndsWith(".") ? "" : ".") + " Returning to the main menu..." :
            steamTransport != null && Session?.IsRunning == true && steamSessions?.Ready != true ? steamSessions?.Status ?? status : status;
        internal string MenuPlayerName => displayName.Value;
        internal int MenuPort => port.Value;
        internal bool MenuAdvertiseLan => advertiseLan.Value;
        internal bool MenuMapRouter => mapRouterPort.Value;
        internal string MenuRouterStatus => routerMapping.Status;
        internal bool MenuSessionRunning => Session?.IsRunning == true;
        internal bool MenuIsHost => Session?.IsHost == true && Session.IsRunning;
        internal bool MenuCanHost => harmony != null && compatibility.Compatible && gameplay?.Player != null && !gameplay.Get_IsDead && Patches.NativeLoadGate.Ready &&
            SceneManager.GetActiveScene().buildIndex == 2 && GuestWorld == null && Session?.IsRunning != true;
        internal bool MenuCanQueueSpectate(uint peer) => spectatorCamera?.CanQueueTarget(peer) == true;
        internal bool MenuStartSpectate(uint peer)
        {
            if (spectatorCamera?.CanQueueTarget(peer) != true || nativeMenus?.ResumeForSpectate() != true) return false;
            // Native ResumeGame has restored walking input/cursor. Recheck the
            // target and gameplay gates before taking the camera lease now.
            return spectatorCamera.StartTarget(peer);
        }
        internal bool MenuCancelQueuedSpectate(uint peer) => spectatorCamera?.CancelQueuedTarget(peer) == true;
        internal void BeginGuestPermadeath()
        {
            World?.Permadeath?.Died();
            EnforcePermadeathSpectator();
        }
        private void ApplyGuestMode()
        {
            if (guestModeLease == null && GuestWorld == null && gameplay != null &&
                Session?.IsRunning == true && !Session.IsHost && Session.HostWorld != null)
                guestModeLease = new GuestModeLease(gameplay, Session.HostWorld.HardMode, Session.HostWorld.Permadeath);
        }
        internal Func<bool> EndHostPermadeath(SharedWorld origin)
        {
            var dyingGame = gameplay;
            string dyingSave = origin.Saves?.BoundSaveName ?? NativeSaveWriter.SelectedSave;
            if (World == origin && Session?.IsHost == true)
            {
                origin.Saves?.EndForPermadeath();
                if (dyingGame != null) NativeDeadField.SetValue(dyingGame, true);
                StopSession("The host died in permadeath; the world has ended.");
            }
            // A menu transition must never redirect deletion to a newly selected save.
            return () => dyingGame != null && gameplay == dyingGame && NativeSaveWriter.SelectedSave == dyingSave;
        }
        internal void EnforcePermadeathSpectator()
        {
            if (Session?.IsHost != false || World?.Permadeath?.LocalDead != true || gameplay?.PlayerWalking == null) return;
            if (permadeathGameplay != gameplay)
            {
                permadeathGameplay = gameplay;
                permadeathNeeds = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>();
                gameplay.Player.Get_VisualHands.Sleep_Start();
                gameplay.GameConsole_Toggle(false, true);
                Patches.LocalEmotes.Reset();
                World.FlushLocalOwnership();
            }
            NativeDeadField.SetValue(gameplay, true);
            if (permadeathNeeds != null) permadeathNeeds.Dead = true;
            gameplay.PlayerWalking.playerCameraController.enabled = false;
            gameplay.PlayerWalking.playerInteractable.enabled = false;
            gameplay.PlayerWalking.enabled = false;
            spectatorCamera?.ForcePermadeath();
        }
        internal bool MenuSpectateQueuedFor(uint peer) => spectatorCamera?.QueuedTarget == peer;

#if MONMP_DEVELOPMENT
        internal bool TestQueueSpectator(uint peer) => spectatorCamera?.QueueTarget(peer) == true;


        internal void TestOpenHostScreenForSpectate() => (nativeMenus ??= new UI.NativeMenus(this)).TestOpenHostForSpectate();


        internal bool TestSpectatorActive => spectatorCamera?.Active == true;


        internal uint? TestSpectatorTarget => spectatorCamera?.CurrentTarget;


        internal Camera? TestSpectatorCamera => spectatorCamera?.LeasedCamera;


        internal bool TestSetSpectatorOrbitForProbe(float yawDelta) => spectatorCamera?.TestSetOrbitForProbe(yawDelta) == true;


        internal string TestSelfViewSnapshot()
        {
            var camera = gameplay?.Player?.mainCamera;
            if (selfShown is not uint id || camera == null || gameplay?.PlayerWalking == null) return "missing";
            var root = gameplay.PlayerWalking.transform.position;
            var cameraPosition = camera.transform.position;
            return "camera=" + cameraPosition.ToString("F3") + ";feet=" + root.ToString("F3") +
                ";cameraToFeet=" + Vector3.Distance(cameraPosition, root).ToString("F3", CultureInfo.InvariantCulture) +
                ";view=" + selfView.TestLocalView(id);
        }


        internal bool TestStartSpectator(uint peer) => spectatorCamera?.TryStartForTest(peer) == true;


        internal void TestStopSpectator() => spectatorCamera?.Stop();
#endif

        internal string[] MenuPlayers => Session?.IsRunning == true ? Session.Peers.Values.OrderBy(p => p.Id).Select(p => p.DisplayName).ToArray() : Array.Empty<string>();
        internal SavedServerList MenuServers => savedServers;
        internal System.Collections.Generic.IReadOnlyList<DirectHostEndpoint> MenuRecentHosts => recentHosts.Entries;
        // One row per game: a host answers on every interface it has (loopback, LAN, VPN adapters),
        // so its replies are grouped by session. Prefer loopback for a local game, then an
        // observed address on the physical LAN rather than sorting virtual adapters first.
        private UI.LanSubnet[] menuLanSubnets = Array.Empty<UI.LanSubnet>();
        private float nextMenuLanInterfaces;
        internal System.Collections.Generic.IEnumerable<DiscoveredHost> MenuLanHosts
        {
            get
            {
                if (Session?.IsRunning == true || lan == null) return Enumerable.Empty<DiscoveredHost>();
                if (Time.unscaledTime >= nextMenuLanInterfaces)
                {
                    nextMenuLanInterfaces = Time.unscaledTime + 5;
                    var subnets = new System.Collections.Generic.List<UI.LanSubnet>();
                    try
                    {
                        foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                            try
                            {
                                var properties = adapter.GetIPProperties();
                                bool physical = UI.LanServerAddressPolicy.IsPhysical(adapter.Name, adapter.Description,
                                    adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ||
                                    adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211);
                                bool gateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                                foreach (var address in properties.UnicastAddresses)
                                    if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                        subnets.Add(new UI.LanSubnet(address.Address, address.IPv4Mask, physical, gateway));
                            }
                            catch (System.Net.NetworkInformation.NetworkInformationException) { }
                        }
                    }
                    catch (System.Net.NetworkInformation.NetworkInformationException) { }
                    menuLanSubnets = subnets.ToArray();
                }
                return lan.Hosts.Where(h => h.SeenOnLan).GroupBy(h => h.Session.SessionId).Select(g =>
                    g.OrderBy(h => UI.LanServerAddressPolicy.Rank(h.Endpoint.Address, menuLanSubnets))
                    .ThenBy(h => h.Endpoint.Address.ToString(), StringComparer.Ordinal).First());
            }
        }
        internal System.Collections.Generic.IEnumerable<DiscoveredHost> MenuDiscoveredHosts =>
            Session?.IsRunning == true || lan == null ? Enumerable.Empty<DiscoveredHost>() : lan.Hosts;
        internal void MenuSaveServers(SavedServerList list)
        {
            savedServers = list;
            savedServersConfig.Value = list.Serialize();
            try { Config.Save(); } catch (Exception ex) { Logger.LogWarning("Could not save the server list: " + ex.Message); }
        }
        // Browsing: a LAN query every two seconds while the multiplayer menu is open.
        internal void MenuBrowseLan()
        {
            if (Session?.IsRunning == true || GuestWorld != null || Time.unscaledTime < nextLanSearch) return;
            SearchLan();
            nextLanSearch = Time.unscaledTime + 2;
        }
        internal void MenuProbeSavedServers(System.Collections.Generic.IEnumerable<IPEndPoint> endpoints)
        {
            if (Session?.IsRunning == true || GuestWorld != null) return;
            try
            {
                if (lan == null) { lan = new LanDiscovery(); lan.Error += message => lanStatus = message; }
                foreach (var endpoint in endpoints.Take(SavedServerList.MaxEntries * 4)) lan.Probe(endpoint);
            }
            catch (Exception ex) { lanStatus = "Saved server status probe unavailable: " + ex.Message; }
        }
        internal void MenuStopBrowsing() { if (Session?.IsRunning != true) StopDiscovery(); }
        internal void MenuJoin(string name, DirectHostEndpoint endpoint, string joinPassword)
        {
            nameText = name; addressText = endpoint.Host; portText = endpoint.Port.ToString(CultureInfo.InvariantCulture);
            password = joinPassword ?? ""; previewAcknowledged = true;
            StartSession(false, false);
        }
        internal void MenuHost(string name, int hostPort, string hostPassword, bool openToLan, bool mapRouter)
        {
            nameText = name; portText = hostPort.ToString(CultureInfo.InvariantCulture); password = hostPassword ?? "";
            advertiseLan.Value = openToLan; mapRouterPort.Value = mapRouter; previewAcknowledged = true;
            StartSession(true, false);
        }
        internal void MenuDisconnect()
        {
            ClearMenuJoinFailure();
            BeginLocalDisconnect(Session?.IsHost == true ? "Stopped hosting." : "Disconnected.");
        }
        internal void MenuRepair() => World?.RepairWorld();
        internal void MenuKick(uint peer) => Session?.Kick(peer);
        internal string MenuJoinAddress => Session?.ActiveTransport == TransportKind.Steam ? MenuSteamLobbyId : MenuIsHost ? Dns.GetHostName() + ":" + MenuPort :
            (addressText.Contains(":") ? "[" + addressText.Trim('[', ']') + "]" : addressText) + ":" + MenuPort;

#if MONMP_DEVELOPMENT
        internal void DirectorDisplayName(string name) { displayName.Value = name; nameText = name; }


        internal UI.MultiplayerScreen? DirectorMultiplayer() => (nativeMenus ??= new UI.NativeMenus(this)).DirectorMultiplayer();


        internal void BeginPairedTestSession() { previewAcknowledged = true; directedTestSession = true; StartSession(TestOptions.IsHost); }


        internal void BeginRejectedMenuTestSession()
        {
            string previous = password;
            try { password = "deliberately-wrong-test-password"; previewAcknowledged = true; StartSession(false); }
            finally { password = previous; }
        }


        internal void EndPairedTestSession()
        {
            var stoppedWorld = World;
            ClearMenuJoinFailure();
            StopSession("Paired test disconnect.");
            if (TestOptions.Enabled && (World != null || Session != null || stoppedWorld?.IsDisposed == false ||
                stoppedWorld?.SaveGate.Active == true || SharedKegPurchase.Active != null || SharedHoistPullers.Current != null ||
                SharedDealerMachines.Active != null || SharedVehicleSales.Active != null || SharedBarDrinkPurchase.Active != null))
                throw new InvalidOperationException("Paired disconnect left world/session, save gate, purchase, or hoist state active.");
        }
#endif

        internal void RegisterGuestWorld(Gameplay game)
        {
            if (Session?.State != SessionState.LoadingWorld || GuestWorld == null) throw new InvalidOperationException("Guest is not loading a host world.");
            sessionScene = SceneKey();
            World = new SharedWorld(game, false, DiagnosticsDirectory, Consumption); World.Attach(Session);
            Session.CompleteWorldLoad(sessionScene, World.CatalogHash);
        }
        internal void ReportGuestWorldReady(GuestWorldLoader loader)
        {
            string evidence = "MONMP_GUEST_WORLD_READY: native save reads blocked=" + loader.BlockedSaveReads +
                "; scene objects preserved=" + loader.PreservedSceneObjects + "; input holds=" + loader.HeldInputFrames + "; snapshot catch-up applied.";
            status = "Host world ready."; Logger.LogInfo(evidence);
#if MONMP_DEVELOPMENT
            File.WriteAllText(Path.Combine(probe.DirectoryPath, "guest-world.txt"), evidence);
#endif
        }
        // A tool in use shows particles or the chainsaw's native cut loop (#150).
        private InteractableTools? effectTool;
        private ParticleSystem[] effectParticles = Array.Empty<ParticleSystem>();
        private float toolSeenAt = -10f;
        private bool ToolEffectsPlaying(InteractableTools? tool)
        {
            if (tool == null) return false;
            if (tool is Tool_Chainsaw) return SharedItems.ChainsawCutSourcePlaying(tool);
            if (tool != effectTool) { effectTool = tool; effectParticles = tool.GetComponentsInChildren<ParticleSystem>(true); }
            foreach (var particles in effectParticles) if (particles != null && particles.isEmitting) return true;
            return false;
        }

        private static string SceneKey() { var scene = SceneManager.GetActiveScene(); return scene.buildIndex + ":" + scene.path; }
        private bool stoppingSession;
        private bool applicationQuitting;
        private void SessionCleanup(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { Logger.LogError("Session cleanup " + name + " failed: " + ex); }
        }
        private void PrepareLocalDisconnect()
        {
            var world = World;
            if (world == null || preparedDisconnectWorld == world) return;
            preparedDisconnectWorld = world;
            world.PrepareLocalDisconnect();
        }
        private void CancelLocalDisconnect()
        {
            var world = preparedDisconnectWorld; preparedDisconnectWorld = null;
            // A failed host save leaves the session usable. Keep already applied brakes
            // and anchor settings, but let new driver input and a later exit prepare again.
            if (world != null && World == world) SessionCleanup("CancelDisconnect", world.CancelLocalDisconnect);
        }
        private void BeginLocalDisconnect(string reason, EscapeMenuV2? menu = null)
        {
            if (LocalDisconnectPending) return;
            var world = World; var session = Session;
            if (world == null || session?.IsRunning != true) { StopSession(reason); return; }
            pendingDisconnectWorld = world; pendingDisconnectSession = session;
            pendingDisconnectMenu = menu;
            pendingDisconnectButton = menu != null ? (int)AccessTools.Field(typeof(EscapeMenuV2), "QuitButtonClicked").GetValue(menu)! : 0;
            pendingDisconnectDeadline = Time.unscaledTime + 5f; pendingDisconnectReason = reason;
            SessionCleanup("PrepareDisconnect", PrepareLocalDisconnect);
            SessionCleanup("DisconnectActions", () => world.BlockNewTransactions(true));
            SessionCleanup("DisconnectOwnership", world.FlushLocalOwnership);
            status = "Disconnecting; returning shared ownership to the host...";
        }
        private void UpdateLocalDisconnect()
        {
            var world = pendingDisconnectWorld; var session = pendingDisconnectSession;
            if (world == null) return;
            // A return scene or another session owns its own cleanup. An old request must
            // never stop a replacement session or invoke its captured native exit menu.
            if (World != world || Session != session || session == null)
            { pendingDisconnectWorld = null; pendingDisconnectSession = null; pendingDisconnectMenu = null; return; }
            if (session.IsRunning)
            {
                SessionCleanup("DisconnectOwnership", world.FlushLocalOwnership);
                if (!world.LocalFlushComplete && Time.unscaledTime < pendingDisconnectDeadline) return;
                if (!world.LocalFlushComplete) Logger.LogWarning("Disconnect ownership did not settle within five seconds; the host will revoke remaining leases.");
            }
            var menu = pendingDisconnectMenu; int button = pendingDisconnectButton; string reason = pendingDisconnectReason;
            StopSession(reason);
            if (menu != null)
            {
                AccessTools.Field(typeof(EscapeMenuV2), "QuitButtonClicked").SetValue(menu, button);
                menu.Button_QuitGameConfirmation();
            }
        }
        private void StopSession(string reason)
        {
#if MONMP_PERFORMANCE_CAPTURE
            SessionCleanup("CaptureAvatarRendering", captureAvatarRendering.Reset);
            SessionCleanup("CaptureClientRendering", captureClientRendering.Reset);
#endif
            if (stoppingSession) return;
            BeginDisconnectReport(reason);
            bool returnDeadGuestToMenu = GuestWorld == null && permadeathGameplay != null && Session?.IsHost == false;
            RetainGuestJoinFailure(reason);
            stoppingSession = true;
            try
            {
                SessionCleanup("PrepareDisconnect", PrepareLocalDisconnect);
                // Native pause/death can outlast this session. Keep its captured
                // input lease until normal Update can safely hand those maps back.
                SessionCleanup("TimeSkip", () => timeSkipPresentation?.Stop());
                pendingDisconnectWorld = null; pendingDisconnectSession = null; pendingDisconnectMenu = null;
                pendingExitWorld = null; pendingExitMenu = null;
                failedExitWorld = null; failedExitMenu = null;
#if MONMP_DEVELOPMENT
                TestCrouchOverride = null;
                TestShirtOverride = null;
#endif
                SessionCleanup("Chat", () => Chat?.Reset());
                SessionCleanup("EmoteWheel", () => emoteWheel?.Reset());
                SessionCleanup("SpectatorCamera", () => spectatorCamera?.Reset());
                SessionCleanup("ExtraSeatCamera", () => extraSeatCamera?.Reset());
                SessionCleanup("WheelEmoteActions", () => wheelEmoteActions?.Reset());
                SessionCleanup("NativeSocialInput", Patches.SocialInputPatches.Reset);
                SessionCleanup("EmotePreview", () => emoteGesturePreview?.Clear());
                SessionCleanup("MapMarkers", () => playerMapMarkers?.Dispose());
                playerMapMarkers = null; mapMarkerSession = null;
                SessionCleanup("LocalEmotes", Patches.LocalEmotes.Reset);
                SessionCleanup("SocialPings", () => Pings?.Dispose()); Pings = null;
                SessionCleanup("GuestMode", () => guestModeLease?.Dispose()); guestModeLease = null;
                SessionCleanup("CheatState", () => Cheats?.Dispose()); Cheats = null;
#if MONMP_DEVELOPMENT
                SessionCleanup("InteractionAudit", () => InteractionAudit?.Flush());
#endif
                SessionCleanup("Discovery", StopDiscovery);
                SessionCleanup("RouterMapping", routerMapping.Stop);
                SessionCleanup("GuestWorld", () => GuestWorld?.Cancel());
                SessionCleanup("World", () => World?.Dispose());
                SessionCleanup("Session", () => Session?.Stop(reason));
                steamTransport = null;
                connectionRoute = "";
                SessionCleanup("RemotePlayers", remotePlayers.Dispose);
                SessionCleanup("SelfView", selfView.Dispose);
                SessionCleanup("HeldSceneModels", HeldGripOverrides.ClearSceneModels);
            }
            finally
            {
                World = null; Session = null; selfShown = null; preparedDisconnectWorld = null;
                try
                {
                    SessionCleanup("OfflineState", () => { if (sessionWasRunning) RestoreOfflineState(); });
                    SessionCleanup("GuestWorldDiscard", () => { if (GuestWorld?.CanDiscard == true) GuestWorld = null; });
                }
                finally
                {
                    sessionWasRunning = false; status = reason; stoppingSession = false;
                    FinishDisconnectReport();
                    permadeathGameplay = null; permadeathNeeds = null;
                    if (returnDeadGuestToMenu && !applicationQuitting)
                    {
                        var loader = UnityEngine.Object.FindObjectOfType<LevelLoader>();
                        if (loader != null) loader.LoadMainMenuScene(); else SceneManager.LoadScene(1);
                    }
                }
            }
        }
        private void StopDiscovery()
        {
            try { lan?.Dispose(); }
            finally { lan = null; nextLanSearch = 0; }
        }
        private void StartAdvertisement()
        {
            try
            {
                lan = new LanDiscovery(() => new LanSession(Session!.Epoch, displayName.Value, Session.Port, Session.Peers.Count, password.Length > 0), LanDiscovery.DefaultPort);
                lan.Error += message => { lanStatus = message; Logger.LogWarning(message); };
                Logger.LogInfo("LAN discovery listening on UDP " + lan.LocalPort);
            }
            catch (Exception ex) { lanStatus = "LAN advertisement unavailable: " + ex.Message; Logger.LogWarning(lanStatus); }
        }
        private void SearchLan(IPEndPoint? destination = null)
        {
            if (Session?.IsRunning == true) return;
            try
            {
                if (lan == null) { lan = new LanDiscovery(); lan.Error += message => lanStatus = message; }
                lan.Search(destination); lanStatus = "Searching the local network; choose a session, then Join preview.";
                nextLanSearch = Time.unscaledTime + 1;
            }
            catch (Exception ex) { lanStatus = "LAN search unavailable: " + ex.Message; StopDiscovery(); }
        }

#if MONMP_DEVELOPMENT
        internal bool DiscoverPairedHost()
        {
            var found = lan?.Hosts.FirstOrDefault(h => h.Session.Compatible && h.Session.Port == TestOptions.Port && IPAddress.IsLoopback(h.Endpoint.Address));
            if (found != null)
            {
                addressText = found.Endpoint.Address.ToString(); portText = found.Endpoint.Port.ToString();
                Logger.LogInfo("Paired LAN discovery selected " + found.Session.Name + " at " + found.Endpoint);
                return true;
            }
            if (Time.unscaledTime >= nextLanSearch) SearchLan(new IPEndPoint(IPAddress.Loopback, LanDiscovery.DefaultPort));
            return false;
        }
#endif

        private void RestoreOfflineState()
        {
            Application.runInBackground = previousRunInBackground;
            if (gameplay != null && gameplay.Get_isGamePaused) Time.timeScale = 0;
        }

#if MONMP_DEVELOPMENT
        private void ToggleMenu()
        {
            if (!menuVisible) ReleaseSocialInput();
            menuVisible = !menuVisible;
            if (menuVisible)
            {
                cursorLock = Cursor.lockState; cursorVisible = Cursor.visible;
                ownsPause = gameplay != null && !gameplay.Get_isGamePaused;
                if (ownsPause) gameplay!.PauseGame();
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            }
            else
            {
                holdPositionEditor?.Hide();
                seatPositionEditor?.Hide();
                emotePoseEditor?.Hide();
                if (ownsPause && gameplay != null) gameplay.ResumeGame();
                ownsPause = false; Cursor.lockState = cursorLock; Cursor.visible = cursorVisible;
            }
        }


        private void DumpWorld()
        {
            try { status = probe.DumpWorld(); InteractionAudit?.Flush(); Logger.LogInfo(status); }
            catch (Exception ex) { status = "World report failed: " + ex.Message; Logger.LogError(ex); }
        }
#endif

        private void SceneChanged(Scene previous, Scene next)
        {
#if MONMP_PERFORMANCE_CAPTURE
            SessionCleanup("CaptureAvatarRendering", captureAvatarRendering.Reset);
            SessionCleanup("CaptureClientRendering", captureClientRendering.Reset);
#endif
#if MONMP_DEVELOPMENT
            SessionCleanup("HoldPositionEditor", () => holdPositionEditor?.Dispose()); holdPositionEditor = null;
            SessionCleanup("SeatPositionEditor", () => seatPositionEditor?.Dispose()); seatPositionEditor = null;
            SessionCleanup("SeatFootLiveProbe", () => seatFootLiveProbe?.Dispose()); seatFootLiveProbe = null;
            SessionCleanup("EmotePoseEditor", () => emotePoseEditor?.Dispose()); emotePoseEditor = null;
            SessionCleanup("CargoBoundsEditor", () => cargoBoundsEditor?.Dispose()); cargoBoundsEditor = null;
#endif
            bool expectedGuestWorld = GuestWorld?.SuppressDisk == true && !GuestWorld.Activated && next.buildIndex == 2;
            if (!expectedGuestWorld) StopSession(GuestJoinSceneStopReason("Scene changed; offline."));
            if (next.buildIndex == 1) GuestWorld = null;
            gameplay = null; nextLookup = 0;
            // A newly loaded world must not receive effects requested by the previous character.
            Consumption.Clear();
            HeldGripOverrides.ClearSceneModels();
            // The new scene owns its cursor and pause state.
            menuVisible = false;
#if MONMP_DEVELOPMENT
            ownsPause = false;
#endif
        }
        internal void ReportSaveBlocked()
        {
            if (Time.unscaledTime - lastSaveNotice < 5) return;
            lastSaveNotice = Time.unscaledTime;
            status = GuestWorld?.SuppressDisk == true ? "This remote world cannot be saved locally. Disconnect to return to the main menu." :
                "Guest saving is disabled during the preview session. Disconnect to save your local world.";
            Logger.LogWarning(status);
        }
        internal void ReportItemUseUnavailable(string reason)
        {
            status = reason;
            FindObjectOfType<UI_ErrorMessage>()?.Show(reason);
        }
        internal void ReportSaveStatus(string message)
        {
            status = message; Logger.LogInfo("Save status: " + message);
            FindObjectOfType<UI_EventMessage>()?.Show(message, "MonMP", 5f, UI_EventMessage.EventIconType.Heart);
        }
        private void OnDestroy()
        {
            // Only the current instance owns the global provider and occlusion evaluator.
            if (ReferenceEquals(Instance, this))
                SessionCleanup("LocalVrBridge", Integration.LocalVrBridge.UnbindContext);
            StopSupportCapture();
#if MONMP_PERFORMANCE_CAPTURE
            SessionCleanup("CaptureAvatarRendering", captureAvatarRendering.Dispose);
            SessionCleanup("CaptureClientRendering", captureClientRendering.Dispose);
#endif
            SceneManager.activeSceneChanged -= SceneChanged;
            if (!applicationQuitting) StopSession("Plugin unloaded.");
            DisposeConnections();
            nativeMenus?.Destroy(); nativeMenus = null;
            UI.MenuKit.ReleaseOwnedSprites();
            StockAvatarBody.ClearModelTemplates();
#if MONMP_DEVELOPMENT
            SessionCleanup("NativeSignEmoteFixture", () => nativeSignEmoteFixture?.Dispose()); nativeSignEmoteFixture = null;
            SessionCleanup("EmotePoseEditor", () => emotePoseEditor?.Dispose()); emotePoseEditor = null;
            SessionCleanup("CargoBoundsEditor", () => cargoBoundsEditor?.Dispose()); cargoBoundsEditor = null;
            SessionCleanup("HoldPositionEditor", () => holdPositionEditor?.Dispose()); holdPositionEditor = null;
            SessionCleanup("SeatPositionEditor", () => seatPositionEditor?.Dispose()); seatPositionEditor = null;
            SessionCleanup("SeatFootLiveProbe", () => seatFootLiveProbe?.Dispose()); seatFootLiveProbe = null;
#endif
            SessionCleanup("Chat", () => Chat?.Dispose()); Chat = null;
            SessionCleanup("PlayerStatus", () => playerStatus?.Destroy()); playerStatus = null;
            SessionCleanup("TimeSkip", () => timeSkipPresentation?.Dispose()); timeSkipPresentation = null;
#if MONMP_DEVELOPMENT
            SessionCleanup("VehicleInputTrace", () => vehicleInputTrace?.Dispose()); vehicleInputTrace = null;
            SessionCleanup("Smoke", () => smoke?.Dispose()); smoke = null;
            SessionCleanup("SteamSmoke", () => steamSmoke?.Dispose()); steamSmoke = null;
#endif
            SessionCleanup("SpectatorCamera", () => spectatorCamera?.Reset()); spectatorCamera = null;
            SessionCleanup("ExtraSeatCamera", () => extraSeatCamera?.Reset()); extraSeatCamera = null;
            SessionCleanup("EmoteWheel", () => emoteWheel?.Dispose()); emoteWheel = null;
            SessionCleanup("WheelEmoteActions", () => wheelEmoteActions?.Dispose()); wheelEmoteActions = null;
            if (ReferenceEquals(EmotePoseOverrides.Active, EmotePoses)) EmotePoseOverrides.Active = null;
            SessionCleanup("EmotePreview", () => emoteGesturePreview?.Dispose()); emoteGesturePreview = null;
#if MONMP_DEVELOPMENT
            SessionCleanup("Menu", () => { if (menuVisible) ToggleMenu(); });
#endif
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
            SessionCleanup("TestPreferences", () => { if (TestOptions.Enabled) LocalTestIsolation.Save(); });
#endif
            // Other game objects can save settings during shutdown. Keep test isolation
            // installed until process exit rather than reverting those calls to shared data.
            SessionCleanup("Patches", () => { if (!IsLocalTest) harmony?.UnpatchSelf(); });
            if (ReferenceEquals(Instance, this)) Instance = null;
#if MONMP_PERFORMANCE_CAPTURE
            if (ReferenceEquals(Runtime.UpdateLoop.Capture, performanceCapture)) Runtime.UpdateLoop.Capture = null;
            try { performanceCapture?.Dispose(); }
            catch (Exception captureError) { Logger.LogWarning("MonMP final performance report unavailable: " + captureError.Message); }
            performanceCapture = null;
#endif
        }

        private void OnApplicationQuit()
        {
            if (applicationQuitting) return;
            applicationQuitting = true;
            StopSession("Application quitting.");
            DisposeConnections();
        }


#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
        private void EnsureTestWindow()
        {
            if (!Application.isBatchMode &&
                (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != TestOptions.Width || Screen.height != TestOptions.Height))
                Screen.SetResolution(TestOptions.Width, TestOptions.Height, FullScreenMode.Windowed);
            Application.runInBackground = true;
        }
#endif

    }
}
