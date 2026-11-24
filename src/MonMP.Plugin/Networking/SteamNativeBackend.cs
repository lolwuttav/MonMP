using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MonMP.Core.Transport;
using Steamworks;

namespace MonMP.Plugin.Networking
{
    internal sealed partial class SteamNativeBackend : ISteamBackend
    {
        private readonly Callback<SteamNetConnectionStatusChangedCallback_t> connections = null!;
        private readonly Callback<LobbyChatUpdate_t> members = null!;
        private readonly Callback<LobbyDataUpdate_t> data = null!;
        private readonly CallResult<LobbyCreated_t> created = null!;
        private readonly CallResult<LobbyEnter_t> entered = null!;
        private HSteamNetPollGroup pollGroup;
        private readonly IntPtr[] incoming = new IntPtr[32];
        private bool disposed;
        private bool creatingLobby, joiningLobby;
        private readonly HashSet<ulong> joinedLobbies = new HashSet<ulong>();
        private static readonly System.Diagnostics.Stopwatch cleanupClock = System.Diagnostics.Stopwatch.StartNew();
        private static double CleanupNow => cleanupClock.Elapsed.TotalSeconds;
        private SteamAPICall_t lobbyCall;
        internal ulong LastCompletedLobbyId { get; private set; }
        internal int RetiredLobbyLeaveCalls { get; private set; }
        internal string RelayStatus { get; private set; } = "";
        public ulong LocalIdentity => SteamUser.GetSteamID().m_SteamID;
        public event Action<ulong, string>? LobbyCreated;
        public event Action<ulong, string>? LobbyEntered;
        public event Action<ulong>? LobbyChanged;
        public event Action<SteamConnectionEvent>? ConnectionChanged;

        internal SteamNativeBackend()
        {
            string error = AvailabilityError();
            if (error.Length != 0) throw new InvalidOperationException(error);
            // Lobby membership is process-global: an old cancelled JoinLobby result
            // must finish cleanup before a retry can join that same lobby again.
            PollRetiredLobbies();
            if (CleanupBlocksRetry())
                throw new InvalidOperationException("Steam is still finishing a cancelled lobby request. Wait a moment before starting another session.");
            try
            {
            connections = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(change =>
            {
                if (!disposed) ConnectionChanged?.Invoke(new SteamConnectionEvent(change.m_hConn.m_HSteamNetConnection,
                    change.m_info.m_hListenSocket.m_HSteamListenSocket, change.m_info.m_identityRemote.GetSteamID64(),
                    (int)change.m_info.m_eState, change.m_info.m_eEndReason, change.m_info.m_szEndDebug));
            });
            members = Callback<LobbyChatUpdate_t>.Create(change => { if (!disposed) LobbyChanged?.Invoke(change.m_ulSteamIDLobby); });
            data = Callback<LobbyDataUpdate_t>.Create(change => { if (!disposed) LobbyChanged?.Invoke(change.m_ulSteamIDLobby); });
            created = CallResult<LobbyCreated_t>.Create((result, failed) =>
            {
                creatingLobby = false;
                LastCompletedLobbyId = result.m_ulSteamIDLobby;
                if (!failed && result.m_eResult == EResult.k_EResultOK && result.m_ulSteamIDLobby != 0) joinedLobbies.Add(result.m_ulSteamIDLobby);
                if (disposed)
                {
                    try
                    {
                        if (!failed && result.m_eResult == EResult.k_EResultOK && result.m_ulSteamIDLobby != 0)
                            LeaveRetiredLobby(result.m_ulSteamIDLobby);
                    }
                    finally { FinishDelayedCleanup(); }
                    return;
                }
                LobbyCreated?.Invoke(result.m_ulSteamIDLobby, failed ? "Steam lobby creation failed (I/O)." : ResultError("create lobby", result.m_eResult));
            });
            entered = CallResult<LobbyEnter_t>.Create((result, failed) =>
            {
                joiningLobby = false;
                LastCompletedLobbyId = result.m_ulSteamIDLobby;
                if (!failed && result.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess && result.m_ulSteamIDLobby != 0) joinedLobbies.Add(result.m_ulSteamIDLobby);
                if (disposed)
                {
                    try
                    {
                        if (!failed && result.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess && result.m_ulSteamIDLobby != 0)
                            LeaveRetiredLobby(result.m_ulSteamIDLobby);
                    }
                    finally { FinishDelayedCleanup(); }
                    return;
                }
                LobbyEntered?.Invoke(result.m_ulSteamIDLobby, failed ? "Steam lobby join failed (I/O)." :
                    result.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess ? "" :
                    "Steam lobby join failed: " + (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse);
            });
            pollGroup = SteamNetworkingSockets.CreatePollGroup();
            if (pollGroup == HSteamNetPollGroup.Invalid) { Dispose(); throw new InvalidOperationException("Steam could not allocate the MonMP receive poll group."); }
            SteamNetworkingUtils.InitRelayNetworkAccess();
            SteamNetworkingSockets.InitAuthentication();
            activeBackend = this;
            }
            catch { Dispose(); throw; }
        }

        internal static string AvailabilityError()
        {
            try
            {
                // SteamManager.Initialized creates its singleton if absent. Check for
                // the game's existing component first; MonMP never initializes Steam.
                if (UnityEngine.Object.FindObjectOfType<SteamManager>() == null || !SteamManager.Initialized)
                    return "The game's Steam integration is not initialized. Start Mon Bazou with Steam running, or choose Direct IP/LAN.";
                uint app = SteamUtils.GetAppID().m_AppId;
                if (app != SteamSessionMetadata.AppId) return "Steam reports AppID " + app + "; Mon Bazou requires AppID 1520370.";
                if (!SteamUser.BLoggedOn()) return "Steam is offline. Sign in to Steam, or choose Direct IP/LAN.";
                return "";
            }
            catch (Exception ex) { return "The game's Steam bindings are unavailable: " + ex.Message; }
        }
        private static string ResultError(string operation, EResult result) => result == EResult.k_EResultOK ? "" : "Steam could not " + operation + ": " + result;
        public void CreateLobby(int limit)
        {
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, limit);
            if (call == SteamAPICall_t.Invalid) { LobbyCreated?.Invoke(0, "Steam refused to begin lobby creation."); return; }
            lobbyCall = call; creatingLobby = true; created.Set(call);
        }
        public void JoinLobby(ulong lobby)
        {
            var call = SteamMatchmaking.JoinLobby(new CSteamID(lobby));
            if (call == SteamAPICall_t.Invalid) { LobbyEntered?.Invoke(0, "Steam refused to begin the lobby join."); return; }
            lobbyCall = call; requestedLobby = lobby; joiningLobby = true; entered.Set(call);
        }
        public void LeaveLobby(ulong lobby)
        { joinedLobbies.Remove(lobby); SteamMatchmaking.LeaveLobby(new CSteamID(lobby)); }
        public ulong LobbyOwner(ulong lobby) => SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID;
        public bool LobbyMember(ulong lobby, ulong identity)
        {
            var id = new CSteamID(lobby); int count = SteamMatchmaking.GetNumLobbyMembers(id);
            for (int n = 0; n < count; n++) if (SteamMatchmaking.GetLobbyMemberByIndex(id, n).m_SteamID == identity) return true;
            return false;
        }
        public string LobbyData(ulong lobby, string key) => SteamMatchmaking.GetLobbyData(new CSteamID(lobby), key);
        public bool SetLobbyData(ulong lobby, string key, string value) => SteamMatchmaking.SetLobbyData(new CSteamID(lobby), key, value);
        public void SetLobbyJoinable(ulong lobby, bool joinable) => SteamMatchmaking.SetLobbyJoinable(new CSteamID(lobby), joinable);
        public void SetJoinPresence(ulong lobby) => SteamFriends.SetRichPresence("connect", SteamSessionMetadata.JoinString(lobby));
        public void ClearJoinPresence() => SteamFriends.SetRichPresence("connect", null!);

        private static SteamNetworkingConfigValue_t[] Options(bool relay)
        {
            var list = new List<SteamNetworkingConfigValue_t>
            {
                IntOption(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, 20000),
                IntOption(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, 15000),
                IntOption(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, SteamTransport.NativeSendBufferBytes),
                IntOption(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, SteamTransport.MinimumSendRateBytesPerSecond),
                IntOption(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, SteamTransport.SendRateBytesPerSecond)
            };
            // Disabling ICE is the supported per-listener/per-connection way to
            // require SDR. Normal play leaves Steam's own routing defaults intact.
            if (relay) list.Add(IntOption(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
                Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable));
            return list.ToArray();
        }
        private static SteamNetworkingConfigValue_t IntOption(ESteamNetworkingConfigValue key, int value) => new SteamNetworkingConfigValue_t
        { m_eValue = key, m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, m_val = new SteamNetworkingConfigValue_t.OptionValue { m_int32 = value } };
        public uint Listen(bool relay)
        {
            var options = Options(relay);
            var result = SteamNetworkingSockets.CreateListenSocketP2P(SteamSessionMetadata.VirtualPort, options.Length, options);
            return result.m_HSteamListenSocket;
        }
        public uint Connect(ulong target, bool relay)
        {
            var identity = new SteamNetworkingIdentity(); identity.SetSteamID64(target); var options = Options(relay);
            return SteamNetworkingSockets.ConnectP2P(ref identity, SteamSessionMetadata.VirtualPort, options.Length, options).m_HSteamNetConnection;
        }
        public bool Accept(uint handle) => SteamNetworkingSockets.AcceptConnection(new HSteamNetConnection(handle)) == EResult.k_EResultOK;
        public bool Configure(uint handle)
        {
            var connection = new HSteamNetConnection(handle);
            // The game's 20.1.0 wrapper declares OUT scalars here although native
            // takes const arrays. The narrow ABI-correct wrapper passes a pinned
            // priority array (CD media below world/poses) and NULL default weights.
            var priorities = (int[])SteamTransport.LanePriorities.Clone();
            var pinned = GCHandle.Alloc(priorities, GCHandleType.Pinned);
            EResult lanes;
            try { lanes = ConfigureLanes(Sockets012(), connection, priorities.Length, pinned.AddrOfPinnedObject(), IntPtr.Zero); }
            finally { pinned.Free(); }
            return lanes == EResult.k_EResultOK && SteamNetworkingSockets.SetConnectionPollGroup(connection, pollGroup);
        }
        public void Close(uint connection, string reason, bool linger) => SteamNetworkingSockets.CloseConnection(new HSteamNetConnection(connection),
            (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic, SteamCloseReasons.Normalize(reason), linger);
        public void CloseListener(uint listener) => SteamNetworkingSockets.CloseListenSocket(new HSteamListenSocket(listener));

        public IReadOnlyList<SteamPacket> Receive(int maximum)
        {
            List<SteamPacket>? packets = null;
            int received = 0;
            while (received < maximum)
            {
                int count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, incoming, Math.Min(incoming.Length, maximum - received));
                if (count < 0) throw new InvalidOperationException("Steam receive poll group became invalid.");
                if (count == 0) break;
                try
                {
                    packets ??= new List<SteamPacket>();
                    long sampledAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    long receivedNow = (long)SteamNetworkingUtils.GetLocalTimestamp();
                    for (int n = 0; n < count; n++)
                    {
                        var message = SteamNetworkingMessage_t.FromIntPtr(incoming[n]);
                        bool reliable = (message.m_nFlags & Constants.k_nSteamNetworkingSend_Reliable) != 0;
                        // Invalid sizes are returned as an empty packet so the adapter
                        // closes that peer without allocating attacker-sized buffers.
                        byte[] bytes = message.m_cbSize >= TransportMessageRules.MinimumMessageBytes && message.m_cbSize <= TransportMessageRules.MaximumMessageBytes
                            ? new byte[message.m_cbSize] : Array.Empty<byte>();
                        if (bytes.Length != 0) Marshal.Copy(message.m_pData, bytes, 0, bytes.Length);
                        // Steam stamps receipt independently of Unity polling.
                        // Preserve this local age when a slow frame batches data.
                        long receivedAt = (long)message.m_usecTimeReceived;
                        double age = receivedAt > 0 && receivedAt <= receivedNow
                            ? (receivedNow - receivedAt) / 1000000.0 : -1;
                        packets.Add(new SteamPacket(message.m_conn.m_HSteamNetConnection, bytes, reliable, message.m_idxLane, age, sampledAt));
                    }
                }
                finally
                {
                    // A failure while reading one message must also release every
                    // remaining native pointer already returned in this batch.
                    for (int n = 0; n < count; n++)
                        if (incoming[n] != IntPtr.Zero) { SteamNetworkingMessage_t.Release(incoming[n]); incoming[n] = IntPtr.Zero; }
                }
                received += count;
            }
            return packets ?? (IReadOnlyList<SteamPacket>)Array.Empty<SteamPacket>();
        }

        public int Send(uint connection, byte[] bytes, bool reliable, int lane)
        {
            IntPtr pointer = SteamNetworkingUtils.AllocateMessage(bytes.Length);
            if (pointer == IntPtr.Zero) return (int)EResult.k_EResultLimitExceeded;
            bool transferred = false;
            try
            {
                var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                Marshal.Copy(bytes, 0, message.m_pData, bytes.Length);
                message.m_conn = new HSteamNetConnection(connection); message.m_cbSize = bytes.Length;
                message.m_nFlags = reliable ? Constants.k_nSteamNetworkingSend_ReliableNoNagle : Constants.k_nSteamNetworkingSend_UnreliableNoDelay;
                message.m_idxLane = (ushort)lane;
                Marshal.StructureToPtr(message, pointer, false);
                var result = new long[1];
                // Native takes an array of pointers. The bundled generated struct[]
                // overload is not ABI-compatible and must never be called.
                SendNativeMessages(Sockets012(), 1, new[] { pointer }, result);
                transferred = true; // Steam owns and releases it, including failures.
                return result[0] < 0 ? (int)-result[0] : (int)EResult.k_EResultOK;
            }
            finally { if (!transferred) SteamNetworkingMessage_t.Release(pointer); }
        }
        public SteamLinkMetrics Metrics(uint connection)
        {
            var handle = new HSteamNetConnection(connection);
            string route = "Steam route pending";
            if (SteamNetworkingSockets.GetConnectionInfo(handle, out var info))
            {
                if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                    route = (info.m_nFlags & Constants.k_nSteamNetworkConnectionInfoFlags_Relayed) != 0 ? "Valve relay (SDR)" :
                        (info.m_nFlags & Constants.k_nSteamNetworkConnectionInfoFlags_LoopbackBuffers) != 0 ? "Steam loopback" : "Direct P2P";
            }
            var status = new SteamNetConnectionRealTimeStatus_t();
            var unusedLane = new SteamNetConnectionRealTimeLaneStatus_t();
            EResult result = SteamNetworkingSockets.GetConnectionRealTimeStatus(handle, ref status, 0, ref unusedLane);
            if (result != EResult.k_EResultOK) return new SteamLinkMetrics(0, 0, 0, route);
            int pending = Math.Max(0, status.m_cbPendingReliable), rate = Math.Max(0, status.m_nSendRateBytesPerSecond);
            long nativeQueue = (long)status.m_usecQueueTime;
            return new SteamLinkMetrics(Math.Max(0, status.m_nPing), pending,
                Math.Max(0, DeliveryLoss(status.m_flConnectionQualityLocal)), route,
                Math.Max(0, status.m_cbSentUnackedReliable), rate,
                SteamQueueTime.Milliseconds(nativeQueue, pending, rate), DeliveryLoss(status.m_flConnectionQualityRemote), nativeQueue);
        }
        internal static double DeliveryLoss(float quality) => float.IsNaN(quality) || float.IsInfinity(quality) || quality < 0 || quality > 1 ?
            -1 : (1.0 - quality) * 100;
        internal string ServiceDiagnostics()
        {
            var auth = SteamNetworkingSockets.GetAuthenticationStatus(out var authentication);
            var relay = SteamNetworkingUtils.GetRelayNetworkStatus(out var network);
            RelayStatus = "authentication=" + auth + " (" + authentication.m_debugMsg + "); relay=" + relay +
                "; config=" + network.m_eAvailNetworkConfig + "; anyrelay=" + network.m_eAvailAnyRelay + " (" + network.m_debugMsg + ")";
            return RelayStatus;
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            // Steam cannot cancel the actual asynchronous lobby operation. Keep
            // only its CallResult alive until it finishes, then immediately leave
            // a late-created/joined lobby instead of leaking membership on cancel.
            RetirePendingLobby();
            if (!creatingLobby) created?.Dispose(); if (!joiningLobby) entered?.Dispose();
            connections?.Dispose(); members?.Dispose(); data?.Dispose();
            // Also covers disposal between a native result arriving and its queued
            // adapter callback running, when SteamTransport does not know the ID yet.
            foreach (ulong lobby in new List<ulong>(joinedLobbies)) LeaveLobby(lobby);
            if (pollGroup != HSteamNetPollGroup.Invalid) { SteamNetworkingSockets.DestroyPollGroup(pollGroup); pollGroup = HSteamNetPollGroup.Invalid; }
            // The game's SteamManager continues to own Init/RunCallbacks/Shutdown.
        }
        // Only inspect the API handle opened by this backend, and only when its registered
        // result never arrived. Normal callback dispatch stays with the game's SteamManager.
        private ulong RecoverRetiredLobby()
        {
            try
            {
                if (SteamUtils.IsAPICallCompleted(lobbyCall, out bool failed) && !failed)
                {
                    if (creatingLobby && ReadRetiredResult(LobbyCreated_t.k_iCallback, out LobbyCreated_t made) &&
                        made.m_eResult == EResult.k_EResultOK) return made.m_ulSteamIDLobby;
                    if (joiningLobby && ReadRetiredResult(LobbyEnter_t.k_iCallback, out LobbyEnter_t enteredLobby) &&
                        enteredLobby.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                        return enteredLobby.m_ulSteamIDLobby;
                }
                if (joiningLobby && requestedLobby != 0 && LobbyMember(requestedLobby, LocalIdentity)) return requestedLobby;
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP could not check a retired Steam lobby request: " + ex.Message); }
            return 0;
        }

        private static void ReportRetiredLobbyFailure(Exception ex) =>
            UnityEngine.Debug.LogWarning("MonMP could not leave a retired Steam lobby: " + ex.Message);

        private bool ReadRetiredResult<T>(int callback, out T result) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            IntPtr storage = Marshal.AllocHGlobal(size);
            try
            {
                if (SteamUtils.GetAPICallResult(lobbyCall, storage, size, callback, out bool failed) && !failed)
                {
                    result = Marshal.PtrToStructure<T>(storage);
                    return true;
                }
                result = default; return false;
            }
            finally { Marshal.FreeHGlobal(storage); }
        }

        [DllImport("steam_api64", EntryPoint = "SteamAPI_SteamNetworkingSockets_SteamAPI_v012", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr Sockets012();
        [DllImport("steam_api64", EntryPoint = "SteamAPI_ISteamNetworkingSockets_ConfigureConnectionLanes", CallingConvention = CallingConvention.Cdecl)]
        private static extern EResult ConfigureLanes(IntPtr sockets, HSteamNetConnection connection, int lanes, IntPtr priorities, IntPtr weights);
        [DllImport("steam_api64", EntryPoint = "SteamAPI_ISteamNetworkingSockets_SendMessages", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SendNativeMessages(IntPtr sockets, int count, [In] IntPtr[] messages, [Out] long[] result);
    }
}
