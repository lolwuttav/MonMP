using System;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Integration
{
    // Read-only session, gate and spectator context for the registered VR provider, plus the
    // per-frame lifecycle tracking that resets MonMP-held VR state. Nothing here hands out a
    // registry, a session object or anything mutable.
    public static partial class LocalVrBridge
    {
        private static MonMPPlugin? Plugin => OnMainThread ? MonMPPlugin.Instance : null;
        private static bool lastRunning, lastVrUi;
        private static int lastScene = int.MinValue;
        private static readonly Replication.WorldToolAim offlineAim = new Replication.WorldToolAim();

        public static bool Available => Plugin != null;
        public static string ModVersion => SessionOptions.CurrentModVersion;
        public static int WireVersion => Wire.Version;

        public static bool SessionRunning => Plugin?.Session?.IsRunning == true;
        public static bool IsHost => Plugin?.Session is { IsRunning: true, IsHost: true };
        public static bool SessionReady => Plugin?.Session is { IsRunning: true, IsReady: true };
        public static bool WorldSynchronized => SessionRunning && Plugin?.World?.Channel.FullySynchronized == true;

        public static bool NativePaused => Plugin?.Gameplay?.Get_isGamePaused == true;
        public static bool PauseMenuOpen => Plugin?.PauseMenuOpen == true;
        public static bool ChatOpen => Plugin?.ChatBlocksInput == true;
        public static bool MenuOpen => Plugin?.BridgeMenuOpen == true;
        public static bool SaveGateActive => Plugin?.World?.SaveGate.Active == true;
        public static bool PresentationHeld => Plugin?.GuestWorld?.HoldPresentation == true;
        public static bool LocalDead => Plugin?.Gameplay?.Get_IsDead == true;
        public static bool LocalSleeping => Plugin?.Gameplay?.Get_IsSleeping == true;

        // Spectator state stays MonMP's: the lease, forced permadeath policy and gameplay
        // restrictions are unchanged under XR. Only the camera transform writes are yielded,
        // so the provider decides how to present these.
        public static bool Spectating => Plugin?.Spectating == true;
        public static bool ForcedSpectator => Plugin?.BridgeSpectator?.Forced == true;
        public static uint? SpectatorTargetPeer => Plugin?.BridgeSpectator?.CurrentTarget;
        public static Vector3? SpectatorFocus => SpectatorView(out _, out _, out var focus) ? focus : (Vector3?)null;
        public static Vector3? SpectatorSuggestedViewPosition => SpectatorView(out var position, out _, out _) ? position : (Vector3?)null;
        public static Quaternion? SpectatorSuggestedViewRotation => SpectatorView(out _, out var rotation, out _) ? rotation : (Quaternion?)null;

        private static bool SpectatorView(out Vector3 position, out Quaternion rotation, out Vector3 focus)
        {
            position = focus = default; rotation = Quaternion.identity;
            var spectator = Plugin?.BridgeSpectator;
            return spectator != null && spectator.SuggestedView(out position, out rotation, out focus);
        }

        // One shared answer to "may local gameplay input act now". Native cancel/up for an action
        // that is already held is deliberately not part of this gate.
        public static bool GameplayInputAllowed
        {
            get
            {
                var plugin = Plugin;
                var gameplay = plugin?.Gameplay;
                if (plugin == null || gameplay == null || gameplay.Get_isGamePaused || plugin.PauseMenuOpen ||
                    plugin.SpectatorUiBlocked || plugin.Spectating || gameplay.Get_IsDead || gameplay.Get_IsSleeping ||
                    plugin.World?.SaveGate.Active == true || plugin.GuestWorld?.HoldPresentation == true) return false;
                var session = plugin.Session;
                return session?.IsRunning != true || session.IsReady && plugin.World?.Channel.FullySynchronized == true;
            }
        }

        // --- Controller seat exit --------------------------------------------------------------
        // MonMP's seat adapters, in the order a click would reach them. Static delegates: polling
        // IsSeatedLocalSeat every frame allocates nothing.
        private static readonly LocalVrSeatExit.Source[] seatSources =
        {
            new LocalVrSeatExit.Source("extra seat", () => Plugin?.World?.ExtraSeats.LocalExitChair() != null,
                () => Plugin?.World?.ExtraSeats.LocalExitChair()?.MouseDown()),
            new LocalVrSeatExit.Source("furniture seat", () => Plugin?.World?.FurnitureSeats.HasLocalExitSeat == true,
                () => Plugin?.World?.FurnitureSeats.ClickLocalExitSeat()),
            new LocalVrSeatExit.Source("vehicle seat", () => Plugin?.Vehicles?.HasLocalExitSeat == true,
                () => Plugin?.Vehicles?.ClickLocalExitSeat()),
        };

        // Restrictions under which a desktop player could not click a seat either. The save gate
        // is not one of them: the adapters allow leaving a seat while a save settles.
        private static bool SeatExitRestricted()
        {
            var plugin = Plugin;
            var gameplay = plugin?.Gameplay;
            return plugin == null || gameplay == null || gameplay.Get_isGamePaused || plugin.PauseMenuOpen ||
                plugin.SpectatorUiBlocked || plugin.Spectating || gameplay.Get_IsDead || gameplay.Get_IsSleeping ||
                plugin.GuestWorld?.HoldPresentation == true;
        }

        // The local player sits in a seat MonMP can leave through TryExitLocalSeat: a MonMP-added
        // vehicle/stool seat, a shared furniture seat, or a shared driver/König passenger seat.
        // False without a fresh active provider, in single-player and for seats MonMP does not
        // bind (the provider then re-clicks the native seat itself).
        public static bool IsSeatedLocalSeat => LocalVrSeatExit.Seated(XrOwnsLocalView, seatSources, Warn);

        // Leave that seat by the same re-click of the occupied seat a desktop player makes.
        // True means handled or refused: the caller must not start another transition.
        // False means MonMP has no seat to handle.
        public static bool TryExitLocalSeat() => LocalVrSeatExit.TryExit(XrOwnsLocalView, seatExitRestricted, seatSources, Warn);
        private static readonly Func<bool> seatExitRestricted = SeatExitRestricted;

        // Controller tool aim needs nothing from a host or in single-player (tools run natively);
        // a guest needs an admitted connection, which always carries this build's wire revision.
        public static bool SupportsIndependentToolAim
        {
            get
            {
                var plugin = Plugin;
                if (plugin == null) return false;
                var session = plugin.Session;
                return session?.IsRunning != true || session.IsHost || session.IsReady;
            }
        }

        // The same envelope and occlusion rule a host applies to this player's tool origin.
        public static bool ToolAimOriginAdmissible(Vector3 origin)
        {
            var gameplay = Plugin?.Gameplay;
            var walker = gameplay?.PlayerWalking;
            if (gameplay == null || walker == null) return false;
            var root = walker.transform.position;
            byte state = (byte)gameplay.Get_PlayerState;
            PlayerPose pose;
            try
            {
                pose = new PlayerPose(root.x, root.y, root.z, 0, 0, state,
                    state == PlayerPose.WalkingState && walker.Get_IsCrouch).WithIndependentAim(true);
            }
            catch (System.IO.InvalidDataException) { return false; }
            return ToolAimPolicy.CameraOriginFits(pose, origin.x, origin.y, origin.z, allowCreeper: true, clear: Plugin?.World?.ToolAim.CheckSegment ?? offlineAim.CheckSegment);
        }

        internal static void BindContext()
        {
            Bind();
            ToolAimSupported = () => SupportsIndependentToolAim;
        }

        internal static void UnbindContext()
        {
            ToolAimSupported = () => false;
            Unbind();
        }

        // Once per plugin Update, before the camera owners run.
        internal static void FrameTick(MonMPPlugin plugin)
        {
            bool running = plugin.Session?.IsRunning == true;
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (running != lastRunning || scene != lastScene)
            {
                lastRunning = running; lastScene = scene;
                AdvanceLifecycle();
            }
            bool ui = VrUiBlocksGameplay;
            // Entering a VR modal ends MonMP's own held social input, as chat does.
            if (ui && !lastVrUi) plugin.ReleaseSocialInput();
            lastVrUi = ui;
        }

    }
}
