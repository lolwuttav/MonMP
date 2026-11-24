#if MONMP_DEVELOPMENT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MonMP.Plugin.Networking;
using Newtonsoft.Json;
using Steamworks;

namespace MonMP.Plugin.Diagnostics
{
    // Opt-in isolated runtime qualification. This borrows the game's initialized
    // Steam context and callback pump; it never starts/stops the Steam API itself.
    // Native socket pairs exercise ABI/lanes/fragmentation, not WAN P2P or SDR.
    internal sealed class SteamRuntimeSmoke : IDisposable
    {
        private readonly string output;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<object> cycles = new List<object>();
        private readonly List<string> notes = new List<string>();
        private readonly List<ProbeMessage> messages = new List<ProbeMessage>();
        private readonly HashSet<int> received = new HashSet<int>();
        private readonly Dictionary<string, int> lastReliable = new Dictionary<string, int>();
        private readonly List<object> serviceSnapshots = new List<object>();
        private SteamSessionService? service;
        private SteamTransport? host;
        private SteamNativeBackend? pairBackend;
        private SteamNativeBackend? cancelledBackend;
        private HSteamNetConnection pairA, pairB;
        private int cycle, pairMode;
        private int cancelPhase;
        private bool cancellationRecoveryPassed, localTestsPassed, steamServicesReady;
        private object? cancellationResult;
        private string lastServiceSnapshot = "";
        private bool pairsStarted, disposed;
        private double phaseAt;
        private double servicesAt;
        internal bool Finished { get; private set; }
        internal bool Passed { get; private set; }
        private sealed class ProbeMessage
        {
            internal int Id, Sequence, Lane;
            internal uint Destination;
            internal bool Reliable;
            internal byte[] Bytes = Array.Empty<byte>();
        }
        internal SteamRuntimeSmoke(string outputDirectory)
        { output = Path.Combine(Path.GetFullPath(outputDirectory), "steam-smoke-result.json"); Directory.CreateDirectory(Path.GetDirectoryName(output)!); }

        internal void Tick()
        {
            if (disposed || Finished) return;
            try
            {
                if (service == null)
                {
                    string error = SteamNativeBackend.AvailabilityError();
                    if (error.Length != 0)
                    { if (clock.Elapsed.TotalSeconds >= 35) Finish(false, error); return; }
                    service = new SteamSessionService(notes.Add); notes.Add("Borrowed the game's initialized Steam context; actual AppID=" + SteamUtils.GetAppID().m_AppId);
                    servicesAt = clock.Elapsed.TotalSeconds;
                }
                service.Poll();
                if (cycle < 3)
                {
                    if (host == null)
                    {
                        host = service.CreateHost("MonMP isolated Steam diagnostic", 2, cycle == 2);
                        host.Start(); phaseAt = clock.Elapsed.TotalSeconds;
                    }
                    host.Poll();
                    if (host.Failure.Length != 0) { Finish(false, host.Failure); return; }
                    if (host.Ready)
                    {
                        ulong lobby = host.LobbyId; var id = new CSteamID(lobby);
                        if (!SteamSessionMetadata.Validate(key => SteamMatchmaking.GetLobbyData(id, key),
                            SteamMatchmaking.GetLobbyOwner(id).m_SteamID, out _, out _, out var error))
                        { Finish(false, "Runtime lobby metadata failed validation: " + error); return; }
                        cycles.Add(new { cycle = cycle + 1, lobby, forceRelayConfiguration = cycle == 2,
                            members = SteamMatchmaking.GetNumLobbyMembers(id), metadataValidated = true, nativeListenCreated = true,
                            services = service.Diagnostics() });
                        host.Dispose(); host = null; cycle++; return;
                    }
                    if (clock.Elapsed.TotalSeconds - phaseAt >= 30) { Finish(false, "Steam lobby creation timed out in cycle " + (cycle + 1) + ". " + service.Diagnostics()); return; }
                    return;
                }
                if (!cancellationRecoveryPassed)
                { CheckCancellationRecovery(); return; }
                if (pairMode >= 2)
                { CheckServices(); return; }
                if (!pairsStarted) StartPair();
                ReceivePair(pairA); ReceivePair(pairB);
                if (received.Count == messages.Count)
                {
                    notes.Add((pairMode == 0 ? "Buffered" : "UDP loopback") + " native socket pair passed " + messages.Count + " bidirectional messages on three lanes, including 32768-byte reliable fragmentation.");
                    ClosePair(); pairMode++;
                    if (pairMode == 2) { localTestsPassed = true; CheckServices(); }
                }
                else if (clock.Elapsed.TotalSeconds - phaseAt >= 15)
                    Finish(false, "Native socket pair timed out: received " + received.Count + "/" + messages.Count + " messages; mode=" + pairMode);
            }
            catch (Exception ex) { Finish(false, ex.ToString()); }
        }
        private void CheckCancellationRecovery()
        {
            if (cancelPhase == 0)
            {
                cancelledBackend = new SteamNativeBackend(); cancelledBackend.CreateLobby(2); cancelledBackend.Dispose();
                if (SteamNativeBackend.PendingCleanupCount != 1) throw new InvalidOperationException("Cancelled lobby request was not retained for late-result cleanup.");
                try
                {
                    using var unexpected = new SteamNativeBackend();
                    throw new InvalidOperationException("A new native lobby backend was allowed before cancelled membership cleanup completed.");
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("Steam is still finishing a cancelled lobby request", StringComparison.Ordinal)) { }
                cancelPhase = 1; phaseAt = clock.Elapsed.TotalSeconds; return;
            }
            if (cancelPhase == 1)
            {
                if (SteamNativeBackend.PendingCleanupCount != 0)
                {
                    if (clock.Elapsed.TotalSeconds - phaseAt >= 30) throw new InvalidOperationException("Cancelled Steam lobby operation did not finish cleanup in 30 seconds.");
                    return;
                }
                if (cancelledBackend!.LastCompletedLobbyId != 0 && cancelledBackend.RetiredLobbyLeaveCalls != 1)
                    throw new InvalidOperationException("The retired lobby completion did not call LeaveLobby exactly once.");
                host = service!.CreateHost("MonMP diagnostic cancellation recovery", 2); host.Start(); phaseAt = clock.Elapsed.TotalSeconds; cancelPhase = 2;
            }
            host!.Poll();
            if (host.Failure.Length != 0) throw new InvalidOperationException("New lobby after cancellation failed: " + host.Failure);
            if (host.Ready)
            {
                cancellationResult = new { retiredLobby = cancelledBackend!.LastCompletedLobbyId,
                    leaveCalls = cancelledBackend.RetiredLobbyLeaveCalls, blockedRetryUntilCleanup = true,
                    newHostLobby = host.LobbyId, newHostReady = true, pendingCleanup = SteamNativeBackend.PendingCleanupCount };
                host.Dispose(); host = null; cancellationRecoveryPassed = true;
                notes.Add("Cancelled asynchronous lobby creation retained its result, left the retired membership, blocked premature retry, and recovered a new host lobby.");
            }
            else if (clock.Elapsed.TotalSeconds - phaseAt >= 30) throw new InvalidOperationException("New lobby after cancellation timed out.");
        }
        private void CheckServices()
        {
            var auth = SteamNetworkingSockets.GetAuthenticationStatus(out var authentication);
            var relay = SteamNetworkingUtils.GetRelayNetworkStatus(out var network);
            string signature = auth + "|" + relay + "|" + network.m_eAvailNetworkConfig + "|" + network.m_eAvailAnyRelay + "|" + authentication.m_debugMsg + "|" + network.m_debugMsg;
            if (signature != lastServiceSnapshot)
            {
                lastServiceSnapshot = signature;
                serviceSnapshots.Add(new { elapsedSeconds = clock.Elapsed.TotalSeconds, authentication = auth.ToString(),
                    authenticationDetail = authentication.m_debugMsg, relay = relay.ToString(),
                    networkConfiguration = network.m_eAvailNetworkConfig.ToString(), anyRelay = network.m_eAvailAnyRelay.ToString(), relayDetail = network.m_debugMsg });
            }
            steamServicesReady = auth == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current &&
                relay == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current && network.m_eAvailNetworkConfig == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current;
            bool terminal = IsTerminal(auth) && (IsTerminal(relay) || IsFailure(network.m_eAvailNetworkConfig));
            if (steamServicesReady)
                Finish(true, "Native lobby cycles, cancellation recovery, socket-pair ABI/lanes/fragmentation, Steam authentication and SDR service readiness passed. This does not establish a remote P2P or relay connection.");
            else if (terminal || clock.Elapsed.TotalSeconds - servicesAt >= 60)
                Finish(false, "Local native lobby/socket-pair tests passed, but Steam networking services are not ready: " + signature);
        }
        private static bool IsFailure(ESteamNetworkingAvailability value) => value == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_CannotTry || value == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Failed;
        private static bool IsTerminal(ESteamNetworkingAvailability value) => value == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current || IsFailure(value);
        private void StartPair()
        {
            pairBackend = new SteamNativeBackend();
            var identityA = new SteamNetworkingIdentity(); identityA.SetSteamID(SteamUser.GetSteamID());
            var identityB = identityA;
            if (!SteamNetworkingSockets.CreateSocketPair(out pairA, out pairB, pairMode == 1, ref identityA, ref identityB))
                throw new InvalidOperationException("Steam native CreateSocketPair failed for mode " + pairMode);
            if (!pairBackend.Configure(pairA.m_HSteamNetConnection) || !pairBackend.Configure(pairB.m_HSteamNetConnection))
                throw new InvalidOperationException("ABI-correct three-lane configuration failed for the native pair.");
            messages.Clear(); received.Clear(); lastReliable.Clear();
            int next = 1;
            foreach (var direction in new[] { (From: pairA, To: pairB), (From: pairB, To: pairA) })
                foreach (int lane in new[] { 0, 1, 2 })
                    for (int sequence = 0; sequence < 4; sequence++)
                    {
                        bool reliable = lane != 1; int size = sequence == 0 ? 23 : sequence == 3 && reliable ? 32768 : 1024;
                        var bytes = new byte[size]; int id = next++;
                        for (int n = 4; n < size; n++) bytes[n] = (byte)(id * 19 + n * 7);
                        Array.Copy(BitConverter.GetBytes(id), bytes, 4);
                        var message = new ProbeMessage { Id = id, Sequence = sequence, Lane = lane, Reliable = reliable,
                            Destination = direction.To.m_HSteamNetConnection, Bytes = bytes };
                        int result = pairBackend.Send(direction.From.m_HSteamNetConnection, bytes, reliable, lane);
                        if (result != (int)EResult.k_EResultOK) throw new InvalidOperationException("Native pointer-array SendMessages failed: " + result);
                        messages.Add(message);
                    }
            pairsStarted = true; phaseAt = clock.Elapsed.TotalSeconds;
        }
        private void ReceivePair(HSteamNetConnection connection)
        {
            var pointers = new IntPtr[32]; int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(connection, pointers, pointers.Length);
            if (count < 0) throw new InvalidOperationException("Native pair receive handle is invalid.");
            try
            {
                for (int n = 0; n < count; n++)
                {
                    var native = SteamNetworkingMessage_t.FromIntPtr(pointers[n]);
                    if (native.m_cbSize < 4 || native.m_cbSize > 32768) throw new InvalidOperationException("Native pair message boundary/size invalid.");
                    var bytes = new byte[native.m_cbSize]; Marshal.Copy(native.m_pData, bytes, 0, bytes.Length); int id = BitConverter.ToInt32(bytes, 0);
                    var expected = messages.FirstOrDefault(message => message.Id == id);
                    if (expected == null || expected.Destination != connection.m_HSteamNetConnection ||
                        expected.Lane != native.m_idxLane || expected.Reliable != ((native.m_nFlags & Constants.k_nSteamNetworkingSend_Reliable) != 0) ||
                        !expected.Bytes.SequenceEqual(bytes) || !received.Add(id))
                        throw new InvalidOperationException("Native pair lost a message boundary, lane, payload, reliability flag or duplicated a message.");
                    if (expected.Reliable)
                    {
                        string key = expected.Destination + ":" + expected.Lane;
                        int previous = lastReliable.TryGetValue(key, out var before) ? before : -1;
                        if (expected.Sequence != previous + 1) throw new InvalidOperationException("Native reliable lane delivered out of order.");
                        lastReliable[key] = expected.Sequence;
                    }
                }
            }
            finally { for (int n = 0; n < count; n++) if (pointers[n] != IntPtr.Zero) SteamNetworkingMessage_t.Release(pointers[n]); }
        }
        private void ClosePair()
        {
            if (pairA != HSteamNetConnection.Invalid) { SteamNetworkingSockets.CloseConnection(pairA, 1000, "MonMP diagnostic complete", false); pairA = HSteamNetConnection.Invalid; }
            if (pairB != HSteamNetConnection.Invalid) { SteamNetworkingSockets.CloseConnection(pairB, 1000, "MonMP diagnostic complete", false); pairB = HSteamNetConnection.Invalid; }
            pairBackend?.Dispose(); pairBackend = null; pairsStarted = false;
        }
        private void Finish(bool passed, string detail)
        {
            if (Finished) return; Passed = passed; Finished = true;
            try { ClosePair(); } catch (Exception ex) { notes.Add("Pair cleanup: " + ex.Message); Passed = false; }
            try { host?.Dispose(); service?.Dispose(); } catch (Exception ex) { notes.Add("Session cleanup: " + ex.Message); Passed = false; }
            File.WriteAllText(output, JsonConvert.SerializeObject(new { passed = Passed, detail, appId = SteamSessionMetadata.AppId,
                completedUtc = DateTime.UtcNow, elapsedSeconds = clock.Elapsed.TotalSeconds, lobbyCycles = cycles, notes,
                localTestsPassed, steamServicesReady, cancellationRecoveryPassed, cancellationResult, serviceSnapshots,
                nativeSocketPairModesCompleted = pairMode,
                unverified = new[] { "Friend invite delivery/acceptance", "Two different Steam accounts", "Internet direct P2P", "Valve SDR relay connection", "Paired gameplay/late joins/reconnects/load" } }, Formatting.Indented));
        }
        public void Dispose()
        { if (disposed) return; if (!Finished) Finish(false, "Steam diagnostic was interrupted before completion."); disposed = true; }
    }
}
#endif
