using System;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using MonMP.Core.Session;
using MonMP.Core.Transport;
using MonMP.Plugin.Networking;

namespace MonMP.Plugin
{
    public sealed partial class MonMPPlugin
    {
        private ConfigEntry<TransportKind> connectionTransport = null!;
        private ConfigEntry<bool> steamForceRelay = null!;
        private SteamSessionService? steamSessions;
        private SteamTransport? steamTransport;
        private float nextConnectionStatus;
        private string connectionRoute = "", lastSteamAvailability = "";
        private bool connectionsDisposed;
        private string joinFailureReason = "", retrySteamLobbyId = "";
        private DirectHostEndpoint retryDirectEndpoint;
        private bool retryTargetAvailable, retryUsesSteam, joinFailurePending;

        // This belongs to the plugin, rather than a scene-owned canvas. A guest
        // loader may destroy that canvas while returning after a failed join.
        private void BeginMenuJoinAttempt(bool host, bool steam, ulong lobby, DirectHostEndpoint endpoint)
        {
            ClearMenuJoinFailure();
            retryTargetAvailable = !host;
            retryUsesSteam = steam;
            retrySteamLobbyId = steam && !host ? lobby.ToString(CultureInfo.InvariantCulture) : "";
            retryDirectEndpoint = endpoint;
        }

        private void RetainGuestJoinFailure(string reason)
        {
            if (!retryTargetAvailable || GuestWorld?.SuppressDisk != true || GuestWorld.Cancelled) return;
            joinFailureReason = reason; joinFailurePending = true;
        }

        private string GuestJoinSceneStopReason(string fallback) =>
            joinFailureReason.Length != 0 && Session == null ? joinFailureReason : fallback;

        internal bool MenuJoinFailurePending => joinFailurePending;
        internal string MenuJoinFailureReason => joinFailureReason;
        internal bool MenuRetryUsesSteam => retryUsesSteam;
        internal string MenuRetrySteamLobbyId => retrySteamLobbyId;
        internal DirectHostEndpoint MenuRetryDirectEndpoint => retryDirectEndpoint;
        internal void MenuJoinFailureShown() => joinFailurePending = false;
        internal void MenuDismissJoinFailure() => ClearMenuJoinFailure();
        private void ClearMenuJoinFailure()
        {
            joinFailureReason = ""; joinFailurePending = false; retryTargetAvailable = false;
            retrySteamLobbyId = ""; retryDirectEndpoint = default;
        }

        private void InitializeConnections()
        {
            connectionTransport = Config.Bind("Connection", "Transport", TransportKind.Steam,
                "Select Steam (friends lobbies/invites, Steam chooses direct/Valve relay) or Direct (LiteNetLib IP/LAN) before starting a session.");
            steamForceRelay = Config.Bind("Connection", "SteamForceRelay", false,
                "Diagnostic opt-in: require Valve relay on this session's Steam connections. No global Steam setting is changed. Availability still depends on the game's Steam services.");
            steamSessions = new SteamSessionService(message => Logger.LogInfo(message));
            steamSessions.JoinRequested += ShowSteamJoin;
        }

        internal bool MenuUseSteam
        {
            get => Session?.IsRunning == true ? Session.ActiveTransport == TransportKind.Steam :
                connectionTransport.Value != TransportKind.Direct;
            set
            {
                if (Session?.IsRunning == true || GuestWorld != null || LocalDisconnectPending) return;
                connectionTransport.Value = value ? TransportKind.Steam : TransportKind.Direct;
                if (value) StopDiscovery();
            }
        }
        internal bool MenuSteamAvailable => !connectionsDisposed && steamSessions?.AvailabilityError.Length == 0;
        internal string MenuSteamStatus => steamSessions?.Status ?? "Steam connection service is unavailable.";
        internal string MenuSteamLobbyId => steamSessions?.ActiveLobbyId is ulong id && id != 0
            ? id.ToString(CultureInfo.InvariantCulture) : "";
        internal string MenuTransportStatus => Session?.IsRunning == true
            ? Session.TransportDescription + (connectionRoute.Length == 0 ? "" : " · " + connectionRoute)
            : MenuUseSteam ? MenuSteamStatus : "Direct IP / LAN";

        internal void MenuSteamHost(string name, string hostPassword)
        {
            nameText = name; password = hostPassword ?? ""; previewAcknowledged = true;
            StartSession(true, true);
        }
        internal void MenuSteamJoin(string name, string lobbyId, string joinPassword)
        {
            if (!ulong.TryParse(lobbyId?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ||
                !SteamSessionService.IsLobbyId(id))
            { status = "Enter a valid Steam lobby ID or accept an invite from your friend."; return; }
            nameText = name; password = joinPassword ?? ""; previewAcknowledged = true;
            StartSession(false, true, id);
        }
        internal void MenuSteamInvite()
        {
            try
            {
                if (steamSessions?.InviteFriends() != true)
                    status = "Steam invite overlay is unavailable. Wait for the lobby, enable the Steam overlay, or share the lobby ID.";
            }
            catch (Exception ex) { status = "Steam invite overlay unavailable: " + ex.Message; }
        }

        internal void LogSteamInvite(string message) => Logger.LogInfo("Steam invite: " + message);

        private void ShowSteamJoin(ulong lobbyId)
        {
            if (!SteamSessionService.IsLobbyId(lobbyId)) return;
            ClearMenuJoinFailure();
            // The invitation carries no password or authority. The UI keeps it pending
            // until the existing session has ended and the native main menu is ready.
            nativeMenus ??= new UI.NativeMenus(this);
            status = "Steam invitation received; opening the join screen at the main menu.";
            nativeMenus.ShowSteamInvite(lobbyId.ToString(CultureInfo.InvariantCulture));
        }

        private void TickConnections()
        {
            if (connectionsDisposed || steamSessions == null) return;
            try
            {
                steamSessions.Poll();
                if (steamTransport != null && steamSessions.ActiveFailure.Length != 0)
                { StopSession(steamSessions.ActiveFailure); return; }
                if (UnityEngine.Time.unscaledTime >= nextConnectionStatus)
                {
                    nextConnectionStatus = UnityEngine.Time.unscaledTime + 2;
                    string availability = steamSessions.AvailabilityError;
                    if (availability != lastSteamAvailability)
                    {
                        lastSteamAvailability = availability;
                        Logger.LogInfo(availability.Length == 0 ? "Steam service ready (Mon Bazou AppID 1520370)." : availability);
                    }
                    if (steamTransport != null && availability.Length != 0)
                    { StopSession(availability); return; }
                    string routes = Session?.IsRunning == true
                        ? string.Join(", ", Session.Peers.Keys.Where(id => id != Session.LocalPeerId)
                            .Select(id => Session.MetricsFor(id).Route).Where(route => !string.IsNullOrEmpty(route)).Distinct()) : "";
                    if (routes != connectionRoute)
                    { connectionRoute = routes; if (routes.Length > 0) Logger.LogInfo("MonMP connection route: " + routes); }
                }
            }
            catch (Exception ex)
            {
                if (steamTransport != null) StopSession("Steam session failed: " + ex.Message);
                else Logger.LogWarning("Steam availability check failed: " + ex.Message);
            }
        }

        private void DisposeConnections()
        {
            if (connectionsDisposed) return;
            connectionsDisposed = true;
            SessionCleanup("SteamInvites", () => steamSessions?.Dispose()); steamSessions = null;
        }
    }
}
