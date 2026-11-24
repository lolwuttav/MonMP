using System;
using System.Collections.Generic;
using System.Globalization;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.Transport;

namespace MonMP.Plugin.Networking
{
    internal static class SteamCloseReasons
    {
        // Steam's 128-byte native end-debug field includes its terminating NUL.
        internal const int MaximumUtf8Bytes = 127;
        internal static string Normalize(string reason) => TransportDisconnectReasons.Normalize(reason, MaximumUtf8Bytes);
    }

    // These values describe MonMP, not Steam partner configuration. Never substitute
    // Spacewar (480) or another application's identity for the game's real AppID.
    internal static class SteamSessionMetadata
    {
        internal const uint AppId = 1520370;
        internal const int VirtualPort = 0;
        internal static Dictionary<string, string> Create(ulong host, string name, string nonce) => new Dictionary<string, string>
        {
            ["monmp"] = "1", ["monmp_app"] = AppId.ToString(CultureInfo.InvariantCulture),
            ["monmp_version"] = SessionOptions.CurrentModVersion, ["monmp_wire"] = Wire.Version.ToString(CultureInfo.InvariantCulture),
            ["monmp_game"] = SessionOptions.SupportedGameHash, ["monmp_host"] = host.ToString(CultureInfo.InvariantCulture),
            ["monmp_session"] = nonce, ["monmp_name"] = name, ["monmp_state"] = "open"
        };

        internal static bool Validate(Func<string, string> data, ulong owner, out ulong host, out string nonce, out string error)
        {
            host = 0; nonce = ""; error = "The lobby is not a compatible MonMP session.";
            if (data("monmp") != "1" || data("monmp_app") != AppId.ToString(CultureInfo.InvariantCulture) ||
                data("monmp_version") != SessionOptions.CurrentModVersion || data("monmp_wire") != Wire.Version.ToString(CultureInfo.InvariantCulture) ||
                data("monmp_game") != SessionOptions.SupportedGameHash || data("monmp_state") != "open") return false;
            if (!ulong.TryParse(data("monmp_host"), NumberStyles.None, CultureInfo.InvariantCulture, out host) || host == 0 || host != owner)
            { error = "The original MonMP host has left; host migration is not supported."; return false; }
            nonce = data("monmp_session");
            if (!Guid.TryParseExact(nonce, "N", out var session) || session == Guid.Empty)
            { error = "The MonMP lobby session identifier is invalid."; return false; }
            error = ""; return true;
        }

        internal static bool TryParseJoin(string text, out ulong lobby)
        {
            lobby = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && parts[0] == "+connect_lobby" &&
                ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out lobby) && lobby != 0;
        }
        internal static string JoinString(ulong lobby) => "+connect_lobby " + lobby.ToString(CultureInfo.InvariantCulture);
    }

    internal readonly struct SteamConnectionEvent
    {
        internal readonly uint Handle, Listener;
        internal readonly ulong Identity;
        internal readonly int State, EndReason;
        internal readonly string Detail;
        internal SteamConnectionEvent(uint handle, uint listener, ulong identity, int state, int endReason = 0, string detail = "")
        { Handle = handle; Listener = listener; Identity = identity; State = state; EndReason = endReason; Detail = detail; }
    }
    internal readonly struct SteamPacket
    {
        internal readonly uint Connection;
        internal readonly byte[] Bytes;
        internal readonly bool Reliable;
        internal readonly int Lane;
        internal readonly double ReceiveAgeSeconds;
        internal readonly long ReceiveAgeSampledAt;
        internal SteamPacket(uint connection, byte[] bytes, bool reliable, int lane, double receiveAgeSeconds = -1, long receiveAgeSampledAt = 0)
        {
            Connection = connection; Bytes = bytes; Reliable = reliable; Lane = lane; ReceiveAgeSeconds = receiveAgeSeconds;
            ReceiveAgeSampledAt = receiveAgeSeconds >= 0 ? (receiveAgeSampledAt > 0 ? receiveAgeSampledAt : System.Diagnostics.Stopwatch.GetTimestamp()) : 0;
        }
    }
    internal readonly struct SteamLinkMetrics
    {
        internal readonly int Ping, PendingBytes, SentUnackedBytes;
        internal readonly double Loss;
        internal readonly int SendRateBytesPerSecond;
        internal readonly double QueueTimeMs;
        internal readonly double RemoteLoss;
        internal readonly string Route;
        // The untouched native connection-level queue time, for diagnostics only.
        // Negative means the backend did not supply one.
        internal readonly long NativeQueueMicroseconds;
        internal SteamLinkMetrics(int ping, int pendingBytes, double loss, string route, int sentUnackedBytes = 0,
            int sendRateBytesPerSecond = 0, double queueTimeMs = -1, double remoteLoss = -1, long nativeQueueMicroseconds = -1)
        {
            NativeQueueMicroseconds = nativeQueueMicroseconds;
            Ping = ping; PendingBytes = Math.Max(0, pendingBytes); SentUnackedBytes = Math.Max(0, sentUnackedBytes); Loss = loss; Route = route;
            SendRateBytesPerSecond = Math.Max(0, sendRateBytesPerSecond);
            QueueTimeMs = double.IsNaN(queueTimeMs) || double.IsInfinity(queueTimeMs) || queueTimeMs < 0 ? -1 : queueTimeMs;
            RemoteLoss = double.IsNaN(remoteLoss) || double.IsInfinity(remoteLoss) || remoteLoss < 0 || remoteLoss > 100 ? -1 : remoteLoss;
        }
    }

    // Steam leaves the connection-level queue time at INT64_MAX whenever a
    // connection has more than one lane, and MonMP always configures three.
    // Trust the native value only when it is plausible; otherwise estimate the
    // wait from what is still pending at the current send rate.
    internal static class SteamQueueTime
    {
        internal const long MaximumNativeMicroseconds = 3600L * 1000 * 1000;
        internal static double Milliseconds(long nativeMicroseconds, int pendingBytes, int sendRateBytesPerSecond)
        {
            if (nativeMicroseconds >= 0 && nativeMicroseconds < MaximumNativeMicroseconds) return nativeMicroseconds / 1000.0;
            return sendRateBytesPerSecond > 0 ? Math.Max(0, pendingBytes) * 1000.0 / sendRateBytesPerSecond : -1;
        }
    }

    // Native callbacks only enqueue adapter work. No application event may escape
    // the adapter until its owning NetworkSession calls Poll().
    internal interface ISteamBackend : IDisposable
    {
        ulong LocalIdentity { get; }
        event Action<ulong, string>? LobbyCreated;
        event Action<ulong, string>? LobbyEntered;
        event Action<ulong>? LobbyChanged;
        event Action<SteamConnectionEvent>? ConnectionChanged;
        void CreateLobby(int members);
        void JoinLobby(ulong lobby);
        void LeaveLobby(ulong lobby);
        ulong LobbyOwner(ulong lobby);
        bool LobbyMember(ulong lobby, ulong identity);
        string LobbyData(ulong lobby, string key);
        bool SetLobbyData(ulong lobby, string key, string value);
        void SetLobbyJoinable(ulong lobby, bool joinable);
        void SetJoinPresence(ulong lobby);
        void ClearJoinPresence();
        uint Listen(bool forceRelay);
        uint Connect(ulong identity, bool forceRelay);
        bool Accept(uint connection);
        bool Configure(uint connection);
        void Close(uint connection, string reason, bool linger);
        void CloseListener(uint listener);
        IReadOnlyList<SteamPacket> Receive(int maximum);
        int Send(uint connection, byte[] bytes, bool reliable, int lane);
        SteamLinkMetrics Metrics(uint connection);
    }
}
