using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Steamworks;

namespace MonMP.Plugin.Networking
{
    // One service per plugin lifetime, so invitations also arrive at the main menu.
    // Session transports own only their own lobbies/sockets and callback handles.
    internal sealed class SteamSessionService : IDisposable
    {
        private Callback<GameLobbyJoinRequested_t>? lobbyInvite;
        private Callback<GameRichPresenceJoinRequested_t>? presenceInvite;
        private readonly Queue<ulong> intents = new Queue<ulong>();
        private readonly Action<string>? log;
        private bool disposed, commandLineRead;
        private SteamTransport? active;
        private readonly Stopwatch availabilityClock = Stopwatch.StartNew();
        private double nextAvailability;
        private string availabilityError = "The game's Steam integration is starting.";
        internal SteamSessionService(Action<string>? log = null)
        {
            this.log = log;
            // The bundled Callback<T>.Create only registers a managed delegate.
            // Register before SteamManager's first RunCallbacks, not after login
            // checks or our first Update: an early invite is otherwise discarded.
            try
            {
                lobbyInvite = Callback<GameLobbyJoinRequested_t>.Create(change =>
                    QueueJoin(change.m_steamIDLobby.m_SteamID, "Steam lobby callback"));
                presenceInvite = Callback<GameRichPresenceJoinRequested_t>.Create(change =>
                {
                    if (SteamSessionMetadata.TryParseJoin(change.m_rgchConnect, out var id)) QueueJoin(id, "Steam presence callback");
                    else log?.Invoke("Steam invite: ignored an unsupported presence join string.");
                });
                log?.Invoke("Steam invite callbacks registered (game-owned callback pump).");
            }
            catch { lobbyInvite?.Dispose(); presenceInvite?.Dispose(); throw; }
        }
        internal event Action<ulong>? JoinRequested;
        // Menus/presence read this every frame. Observing the native singleton
        // requires a Unity scene search; do that at most once every two seconds.
        internal string AvailabilityError
        {
            get
            {
                if (disposed) return "The Steam connection service has closed.";
                if (availabilityClock.Elapsed.TotalSeconds >= nextAvailability)
                { nextAvailability = availabilityClock.Elapsed.TotalSeconds + 2; availabilityError = SteamNativeBackend.AvailabilityError(); }
                return availabilityError;
            }
        }
        internal string Status => active != null && !active.IsDisposed ? active.Status : AvailabilityError.Length == 0 ? "Steam is ready." : AvailabilityError;
        internal ulong ActiveLobbyId => active != null && !active.IsDisposed ? active.LobbyId : 0;
        internal bool IsHosting => active != null && !active.IsDisposed && active.IsHosting;
        internal bool Ready => active?.Ready == true && !active.IsDisposed;
        internal bool ActiveReady => Ready;
        internal string ActiveFailure => active?.Failure ?? "";

        internal void Poll()
        {
            if (disposed) return;
            SteamNativeBackend.PollRetiredLobbies();
            if (!commandLineRead)
            {
                commandLineRead = true; var args = Environment.GetCommandLineArgs();
                for (int n = 1; n + 1 < args.Length; n++)
                    if (args[n] == "+connect_lobby" && ulong.TryParse(args[n + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    { QueueJoin(id, "Steam launch arguments"); break; }
            }
            // Do not consume an intent before its UI subscriber is attached.
            if (JoinRequested != null && intents.Count != 0)
            {
                ulong id = intents.Peek();
                JoinRequested.Invoke(id);
                if (!disposed && intents.Count != 0 && intents.Peek() == id) intents.Dequeue();
                log?.Invoke("Steam invite: dispatched lobby " + id + " to the join UI.");
            }
        }
        internal bool QueueJoin(ulong lobby, string source = "session join request")
        {
            if (disposed) return false;
            if (!IsLobbyId(lobby)) { log?.Invoke("Steam invite: rejected invalid lobby ID from " + source + "."); return false; }
            if (lobby == ActiveLobbyId || intents.Contains(lobby)) return true;
            if (intents.Count >= 8) { log?.Invoke("Steam invite: pending join queue is full; try the invite again."); return false; }
            intents.Enqueue(lobby);
            log?.Invoke("Steam invite: received lobby " + lobby + " from " + source + ".");
            return true;
        }
        internal static bool IsLobbyId(ulong lobby)
        { var id = new CSteamID(lobby); return id.IsValid() && id.IsLobby(); }
        internal SteamTransport CreateHost(string name, int maxPlayers = 16, bool forceRelay = false)
        { return Create(true, 0, name, maxPlayers, forceRelay); }
        internal SteamTransport CreateGuest(ulong lobbyId, bool forceRelay = false)
        {
            if (!IsLobbyId(lobbyId)) throw new ArgumentException("Enter a valid Steam lobby ID.", nameof(lobbyId));
            return Create(false, lobbyId, "", 16, forceRelay);
        }
        private SteamTransport Create(bool host, ulong lobby, string name, int maximum, bool forceRelay)
        {
            if (disposed) throw new ObjectDisposedException(nameof(SteamSessionService));
            if (active != null && !active.IsDisposed) throw new InvalidOperationException("End the current Steam session before starting another.");
            var backend = new SteamNativeBackend();
            var result = new SteamTransport(backend, host, lobby, name, maximum, forceRelay);
            result.Diagnostic += message => log?.Invoke(message);
            active = result; return result;
        }
        internal bool InviteFriends()
        {
            if (!IsHosting || !Ready || ActiveLobbyId == 0 || AvailabilityError.Length != 0 || !SteamUtils.IsOverlayEnabled()) return false;
            SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(ActiveLobbyId)); return true;
        }
        internal string Diagnostics() => active?.NativeDiagnostics() ?? AvailabilityError;
        public void Dispose()
        {
            if (disposed) return; disposed = true; intents.Clear();
            lobbyInvite?.Dispose(); presenceInvite?.Dispose(); lobbyInvite = null; presenceInvite = null;
            active?.Dispose(); active = null;
        }
    }
}
