using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MonMP.Core.Transport
{
    public enum RouterMappingProtocol
    {
        None,
        Pcp,
        NatPmp,
        Upnp
    }

    public enum RouterMappingStatus
    {
        Mapped,
        Renewed,
        Deleted,
        NoMapping,
        AlreadyMapped,
        NoGateway,
        TimedOut,
        Unsupported,
        Rejected,
        ProtocolError,
        TransportError
    }

    public sealed class RouterMappingResult
    {
        private static readonly TimeSpan MaximumConservativeLifetime = TimeSpan.FromHours(24);
        public RouterMappingStatus Status { get; }
        public RouterMappingProtocol Protocol { get; }
        public IPAddress? GatewayAddress { get; }
        public IPAddress? ExternalAddress { get; }
        public ushort? ExternalPort { get; }
        // Lifetime is capped at 24 hours for conservative renewal scheduling, as
        // recommended by RFC 6887 §15 for implausibly long server grants.
        public TimeSpan Lifetime { get; }
        public TimeSpan GrantedLifetime { get; }
        public DateTime? ExpiresAtUtc { get; }
        public bool IsPermanent { get; }
        public int? ProtocolResultCode { get; }
        public string Message { get; }
        public bool IsSuccess => Status == RouterMappingStatus.Mapped || Status == RouterMappingStatus.Renewed || Status == RouterMappingStatus.Deleted;
        public bool HasActiveMapping => (Status == RouterMappingStatus.Mapped || Status == RouterMappingStatus.Renewed) && (IsPermanent || Lifetime > TimeSpan.Zero);

        internal RouterMappingResult(RouterMappingStatus status, RouterMappingProtocol protocol, IPAddress? gatewayAddress,
            IPAddress? externalAddress, ushort? externalPort, TimeSpan lifetime, int? protocolResultCode, string message, bool permanent = false)
        {
            Status = status;
            Protocol = protocol;
            GatewayAddress = gatewayAddress;
            ExternalAddress = externalAddress;
            ExternalPort = externalPort;
            GrantedLifetime = lifetime;
            IsPermanent = permanent;
            Lifetime = lifetime > MaximumConservativeLifetime ? MaximumConservativeLifetime : lifetime;
            ExpiresAtUtc = Lifetime > TimeSpan.Zero ? DateTime.UtcNow.Add(Lifetime) : (DateTime?)null;
            ProtocolResultCode = protocolResultCode;
            Message = message;
        }
    }

    public sealed class RouterPortMapperOptions
    {
        // Supplying GatewayAddress or GatewayResolver is useful on networks where the
        // OS does not expose the relevant route, and gives tests a loopback-only seam.
        public IPAddress? GatewayAddress { get; set; }
        public Func<IPAddress?>? GatewayResolver { get; set; }
        public int GatewayPort { get; set; } = RouterPortMapper.DefaultServerPort;
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMilliseconds(750);
        public int RetryCount { get; set; } = 1;
        public ushort RequestedExternalPort { get; set; }
        public bool EnableUpnp { get; set; } = true;
        public IPEndPoint UpnpDiscoveryEndpoint { get; set; } = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        public TimeSpan UpnpDiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan UpnpHttpTimeout { get; set; } = TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Optional IPv4 UDP port mapping. A successful reply confirms only that the gateway
    /// accepted the mapping; it does not prove that an Internet peer can reach it.
    /// </summary>
    public sealed class RouterPortMapper : IDisposable, IAsyncDisposable
    {
        public const int DefaultServerPort = 5351;
        private const int PcpVersion = 2;
        private const int PcpMapOpcode = 1;
        private const int PcpMapRequestLength = 60;
        private const int PcpMapResponseMinimumLength = 60;
        private const int PcpMaximumDatagramLength = 1100;
        private const byte UdpProtocolNumber = 17;
        private const byte PcpUnsupportedVersion = 1;
        private const byte PcpUnsupportedOpcode = 4;
        private const byte PcpUnsupportedProtocol = 9;
        private const ushort NatPmpUnsupportedVersion = 1;

        private readonly int internalPort;
        private readonly IPAddress? configuredGateway;
        private readonly Func<IPAddress?> gatewayResolver;
        private readonly int gatewayPort;
        private readonly TimeSpan requestTimeout;
        private readonly int retryCount;
        private readonly ushort requestedExternalPort;
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private byte[] mappingNonce = Array.Empty<byte>();
        private RouterMappingResult? current;
        private bool disposed;
        private readonly RouterPortMapperOptions upnpOptions;
        private UpnpPortMapping? upnp;

        public RouterMappingResult? Current => current;

        public RouterPortMapper(int internalUdpPort, RouterPortMapperOptions? options = null)
        {
            if (internalUdpPort < 1 || internalUdpPort > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(internalUdpPort));
            options = options ?? new RouterPortMapperOptions();
            if (options.GatewayAddress != null && options.GatewayAddress.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("The router mapping gateway must be an IPv4 address.", nameof(options));
            if (options.GatewayPort < 1 || options.GatewayPort > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(options.GatewayPort));
            if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromSeconds(5))
                throw new ArgumentOutOfRangeException(nameof(options.RequestTimeout), "RequestTimeout must be greater than zero and no more than five seconds.");
            if (options.RetryCount < 0 || options.RetryCount > 2) throw new ArgumentOutOfRangeException(nameof(options.RetryCount));
            if (options.UpnpDiscoveryEndpoint == null || options.UpnpDiscoveryEndpoint.AddressFamily != AddressFamily.InterNetwork ||
                options.UpnpDiscoveryEndpoint.Port < 1 || options.UpnpDiscoveryTimeout <= TimeSpan.Zero || options.UpnpDiscoveryTimeout > TimeSpan.FromSeconds(5) ||
                options.UpnpHttpTimeout <= TimeSpan.Zero || options.UpnpHttpTimeout > TimeSpan.FromSeconds(5))
                throw new ArgumentOutOfRangeException(nameof(options), "Invalid UPnP endpoint or bounded timeout.");

            internalPort = internalUdpPort;
            configuredGateway = options.GatewayAddress;
            gatewayResolver = options.GatewayResolver ?? DiscoverDefaultIpv4Gateway;
            gatewayPort = options.GatewayPort;
            requestTimeout = options.RequestTimeout;
            retryCount = options.RetryCount;
            requestedExternalPort = options.RequestedExternalPort;
            upnpOptions = new RouterPortMapperOptions { EnableUpnp = options.EnableUpnp,
                UpnpDiscoveryEndpoint = new IPEndPoint(options.UpnpDiscoveryEndpoint.Address, options.UpnpDiscoveryEndpoint.Port),
                UpnpDiscoveryTimeout = options.UpnpDiscoveryTimeout, UpnpHttpTimeout = options.UpnpHttpTimeout };
        }

        /// <summary>Creates one UDP mapping using PCP, explicit NAT-PMP fallback, or UPnP discovery.</summary>
        public async Task<RouterMappingResult> MapAsync(TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            uint seconds = LifetimeSeconds(lifetime, allowZero: false);
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (current?.HasActiveMapping == true)
                    return Result(RouterMappingStatus.AlreadyMapped, current.Protocol, current.GatewayAddress, current.ExternalAddress,
                        current.ExternalPort, current.Lifetime, null, "A router mapping is already active.");

                IPAddress? gateway = ResolveGateway();
                if (gateway == null)
                    return Result(RouterMappingStatus.NoGateway, RouterMappingProtocol.None, null, null, null, TimeSpan.Zero, null,
                        "No IPv4 default gateway was found.");

                mappingNonce = CreateNonce();
                PcpMapReply pcp = await RequestPcpMapAsync(gateway, seconds, requestedExternalPort, null, cancellationToken).ConfigureAwait(false);
                if (pcp.NatPmpServer)
                {
                    var nat = await RequestNatPmpMapAndCommitAsync(gateway, seconds, requestedExternalPort, RouterMappingStatus.Mapped,
                        cancellationToken).ConfigureAwait(false);
                    return await TryUpnpFallbackAsync(gateway, seconds, nat, cancellationToken).ConfigureAwait(false);
                }
                if (pcp.Result != null) return await TryUpnpFallbackAsync(gateway, seconds, pcp.Result, cancellationToken, pcp.CanTryUpnp).ConfigureAwait(false);
                return CommitPcpMap(pcp, RouterMappingStatus.Mapped, gateway, seconds);
            }
            finally { operation.Release(); }
        }

        /// <summary>Renews the current mapping using the protocol that created its lease.</summary>
        public async Task<RouterMappingResult> RenewAsync(TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            uint seconds = LifetimeSeconds(lifetime, allowZero: false);
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (current?.HasActiveMapping != true)
                    return Result(RouterMappingStatus.NoMapping, RouterMappingProtocol.None, current?.GatewayAddress, null, null,
                        TimeSpan.Zero, null, "There is no active router mapping to renew.");

                IPAddress gateway = current.GatewayAddress!;
                if (current.Protocol == RouterMappingProtocol.Upnp)
                {
                    var renewed = await upnp!.RenewAsync(seconds, cancellationToken).ConfigureAwait(false);
                    if (renewed.HasActiveMapping) current = renewed;
                    else if (renewed.Status == RouterMappingStatus.NoMapping) current = null;
                    return renewed;
                }
                // Keep renewals tied to the protocol that created the mapping. A firmware
                // upgrade during the lease must not create a second PCP identity beside a
                // NAT-PMP mapping.
                if (current.Protocol == RouterMappingProtocol.NatPmp)
                    return await RequestNatPmpMapAndCommitAsync(gateway, seconds, current.ExternalPort ?? requestedExternalPort,
                        RouterMappingStatus.Renewed, cancellationToken).ConfigureAwait(false);

                PcpMapReply pcp = await RequestPcpMapAsync(gateway, seconds, current.ExternalPort ?? requestedExternalPort,
                    current.ExternalAddress, cancellationToken).ConfigureAwait(false);
                if (pcp.NatPmpServer)
                {
                    return await RequestNatPmpMapAndCommitAsync(gateway, seconds, current.ExternalPort ?? requestedExternalPort,
                        RouterMappingStatus.Renewed, cancellationToken).ConfigureAwait(false);
                }
                if (pcp.Result != null) return pcp.Result;
                return CommitPcpMap(pcp, RouterMappingStatus.Renewed, gateway, seconds);
            }
            finally { operation.Release(); }
        }

        /// <summary>Requests deletion of the current mapping. A zero-lifetime reply must confirm deletion.</summary>
        public async Task<RouterMappingResult> DeleteAsync(CancellationToken cancellationToken = default)
        {
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (current?.HasActiveMapping != true)
                    return Result(RouterMappingStatus.NoMapping, RouterMappingProtocol.None, current?.GatewayAddress, null, null,
                        TimeSpan.Zero, null, "There is no active router mapping to delete.");
                return await DeleteCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { operation.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await operation.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) return;
                bool upnpDeleteAttempted = current?.Protocol == RouterMappingProtocol.Upnp && current.HasActiveMapping;
                if (current?.HasActiveMapping == true)
                {
                    try { await DeleteCoreAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch { /* Finite leases expire; legacy permanent UPnP rules still need router cleanup if unreachable. */ }
                }
                if (upnp != null)
                {
                    // An AddPortMapping response may be canceled after the router accepted it.
                    // The provisional nonce-tagged entry must also get best-effort cleanup.
                    if (!upnpDeleteAttempted)
                        try { await upnp.DeleteAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                    upnp.Dispose();
                }
                disposed = true;
            }
            finally { operation.Release(); }
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        /// <summary>Returns the first operational IPv4 gateway reported by the OS.</summary>
        public static IPAddress? DiscoverDefaultIpv4Gateway()
        {
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (GatewayIPAddressInformation entry in adapter.GetIPProperties().GatewayAddresses)
                    {
                        IPAddress address = entry.Address;
                        if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(address) &&
                            !IPAddress.Broadcast.Equals(address) && !IPAddress.Loopback.Equals(address)) return address;
                    }
                }
            }
            catch (NetworkInformationException) { }
            catch (PlatformNotSupportedException) { }
            return null;
        }

        private async Task<RouterMappingResult> DeleteCoreAsync(CancellationToken cancellationToken)
        {
            RouterMappingResult previous = current!;
            IPAddress gateway = previous.GatewayAddress!;
            if (previous.Protocol == RouterMappingProtocol.Upnp)
            {
                var deleted = await upnp!.DeleteAsync(cancellationToken).ConfigureAwait(false);
                if (deleted.Status == RouterMappingStatus.Deleted || deleted.Status == RouterMappingStatus.NoMapping) current = null;
                return deleted;
            }
            if (previous.Protocol == RouterMappingProtocol.NatPmp)
            {
                NatPmpMapReply previousNat = await RequestNatPmpMapAsync(gateway, 0, 0, cancellationToken).ConfigureAwait(false);
                if (previousNat.Result != null) return KeepCurrent(previousNat.Result, previous);
                if (previousNat.Lifetime != 0)
                    return KeepCurrent(Result(RouterMappingStatus.Rejected, RouterMappingProtocol.NatPmp, gateway, previous.ExternalAddress,
                        previous.ExternalPort, TimeSpan.FromSeconds(previousNat.Lifetime), previousNat.ResultCode,
                        "The gateway did not confirm deletion; the mapping remains active."), previous);
                current = null;
                return Result(RouterMappingStatus.Deleted, RouterMappingProtocol.NatPmp, gateway, previous.ExternalAddress,
                    previous.ExternalPort, TimeSpan.Zero, previousNat.ResultCode, "The gateway confirmed mapping deletion.");
            }

            PcpMapReply pcp = await RequestPcpMapAsync(gateway, 0, 0, null, cancellationToken).ConfigureAwait(false);
            if (pcp.NatPmpServer)
            {
                NatPmpMapReply nat = await RequestNatPmpMapAsync(gateway, 0, 0, cancellationToken).ConfigureAwait(false);
                if (nat.Result != null) return KeepCurrent(nat.Result, previous);
                if (nat.Lifetime != 0)
                    return KeepCurrent(Result(RouterMappingStatus.Rejected, RouterMappingProtocol.NatPmp, gateway, previous.ExternalAddress,
                        previous.ExternalPort, TimeSpan.FromSeconds(nat.Lifetime), nat.ResultCode, "The gateway did not confirm deletion; the mapping remains active."), previous);
                current = null;
                return Result(RouterMappingStatus.Deleted, RouterMappingProtocol.NatPmp, gateway, previous.ExternalAddress,
                    previous.ExternalPort, TimeSpan.Zero, nat.ResultCode, "The gateway confirmed mapping deletion.");
            }
            if (pcp.Result != null) return KeepCurrent(pcp.Result, previous);
            if (pcp.Lifetime != 0)
                return KeepCurrent(Result(RouterMappingStatus.Rejected, RouterMappingProtocol.Pcp, gateway, previous.ExternalAddress,
                    previous.ExternalPort, TimeSpan.FromSeconds(pcp.Lifetime), pcp.ResultCode, "The gateway did not confirm deletion; the mapping remains active."), previous);
            current = null;
            return Result(RouterMappingStatus.Deleted, RouterMappingProtocol.Pcp, gateway, previous.ExternalAddress,
                previous.ExternalPort, TimeSpan.Zero, pcp.ResultCode, "The gateway confirmed mapping deletion.");
        }

        private async Task<RouterMappingResult> RequestNatPmpMapAndCommitAsync(IPAddress gateway, uint seconds, ushort suggestedExternalPort,
            RouterMappingStatus successStatus, CancellationToken cancellationToken)
        {
            NatPmpMapReply nat = await RequestNatPmpMapAsync(gateway, seconds, suggestedExternalPort, cancellationToken).ConfigureAwait(false);
            if (nat.Result != null) return nat.Result;
            if (nat.Lifetime > 0 && nat.ExternalPort == 0)
                return Result(RouterMappingStatus.ProtocolError, RouterMappingProtocol.NatPmp, gateway, null, nat.ExternalPort,
                    TimeSpan.FromSeconds(nat.Lifetime), nat.ResultCode, "The gateway returned an invalid external UDP port.");
            if (seconds != 0 && nat.Lifetime == 0)
                return Result(RouterMappingStatus.Rejected, RouterMappingProtocol.NatPmp, gateway, null, nat.ExternalPort, TimeSpan.Zero,
                    nat.ResultCode, "The gateway returned a zero mapping lifetime.");

            // The lease exists as soon as its mapping acknowledgement arrives.
            // Record it before the optional address query so cancellation still
            // leaves disposal enough information to remove the accepted mapping.
            var result = Result(successStatus, RouterMappingProtocol.NatPmp, gateway, current?.ExternalAddress, nat.ExternalPort,
                TimeSpan.FromSeconds(nat.Lifetime), nat.ResultCode, successStatus == RouterMappingStatus.Mapped
                    ? "The gateway accepted the NAT-PMP mapping." : "The gateway renewed the NAT-PMP mapping.");
            current = result;
            IPAddress? externalAddress = await RequestNatPmpExternalAddressAsync(gateway, cancellationToken).ConfigureAwait(false);
            if (externalAddress != null)
                current = result = Result(successStatus, RouterMappingProtocol.NatPmp, gateway, externalAddress, nat.ExternalPort,
                    TimeSpan.FromSeconds(nat.Lifetime), nat.ResultCode, result.Message);
            return result;
        }

        private RouterMappingResult CommitPcpMap(PcpMapReply pcp, RouterMappingStatus successStatus, IPAddress gateway, uint requestedSeconds)
        {
            if (pcp.Lifetime > 0 && pcp.ExternalPort == 0)
                return Result(RouterMappingStatus.ProtocolError, RouterMappingProtocol.Pcp, gateway, pcp.ExternalAddress, pcp.ExternalPort,
                    TimeSpan.FromSeconds(pcp.Lifetime), pcp.ResultCode, "The gateway returned an invalid external UDP port.");
            if (pcp.Lifetime == 0 && requestedSeconds != 0)
                return Result(RouterMappingStatus.Rejected, RouterMappingProtocol.Pcp, gateway, pcp.ExternalAddress, pcp.ExternalPort,
                    TimeSpan.Zero, pcp.ResultCode, "The gateway returned a zero mapping lifetime.");
            var result = Result(successStatus, RouterMappingProtocol.Pcp, gateway, pcp.ExternalAddress, pcp.ExternalPort,
                TimeSpan.FromSeconds(pcp.Lifetime), pcp.ResultCode, successStatus == RouterMappingStatus.Mapped
                    ? "The gateway accepted the PCP mapping." : "The gateway renewed the PCP mapping.");
            current = result;
            return result;
        }

        private async Task<PcpMapReply> RequestPcpMapAsync(IPAddress gateway, uint seconds, ushort externalPort,
            IPAddress? externalAddress, CancellationToken cancellationToken)
        {
            IPAddress? clientAddress;
            try { clientAddress = GetLocalIpv4Address(gateway, gatewayPort); }
            catch (SocketException ex)
            {
                return PcpMapReply.Failed(Result(RouterMappingStatus.TransportError, RouterMappingProtocol.Pcp, gateway, null, null,
                    TimeSpan.Zero, null, "Could not select a local IPv4 address: " + ex.SocketErrorCode));
            }
            if (clientAddress == null)
                return PcpMapReply.Failed(Result(RouterMappingStatus.TransportError, RouterMappingProtocol.Pcp, gateway, null, null,
                    TimeSpan.Zero, null, "Could not select a local IPv4 address for the gateway route."));

            byte[] request = BuildPcpMapRequest(clientAddress, seconds, externalPort, externalAddress);
            byte[]? response;
            try { response = await ExchangeAsync(gateway, request, cancellationToken).ConfigureAwait(false); }
            catch (SocketException ex)
            {
                return PcpMapReply.Failed(Result(RouterMappingStatus.TransportError, RouterMappingProtocol.Pcp, gateway, null, null,
                    TimeSpan.Zero, null, "The PCP request failed: " + ex.SocketErrorCode));
            }
            if (response == null)
                return PcpMapReply.Failed(Result(RouterMappingStatus.TimedOut, RouterMappingProtocol.Pcp, gateway, null, null,
                    TimeSpan.Zero, null, "The gateway did not answer the PCP MAP request."));

            // RFC 6886 §3.5's unsupported-version response is a short NAT-PMP v0
            // response (version 0, opcode 0, result 1). RFC 6887 §9 and Appendix A
            // identify version 0 as the signal to retry this request using NAT-PMP.
            if (IsNatPmpUnsupportedVersion(response)) return PcpMapReply.NatPmpDetected();

            // Version negotiation precedes the common-header length check (RFC
            // 6887 sections 8.3/9). A short unsupported-version reply is not a MAP.
            if (response.Length >= 4 && response.Length < 24 && response[0] != 0 &&
                response[1] == (0x80 | PcpMapOpcode) && response[3] == PcpUnsupportedVersion)
                return PcpMapReply.Failed(Result(RouterMappingStatus.Unsupported, RouterMappingProtocol.Pcp, gateway, null, null,
                    TimeSpan.Zero, PcpUnsupportedVersion, "The gateway does not support this PCP version."));

            if (response.Length < 24 || response.Length > PcpMaximumDatagramLength || (response.Length & 3) != 0 ||
                response[1] != (0x80 | PcpMapOpcode))
                return PcpMapReply.Failed(Result(RouterMappingStatus.ProtocolError, RouterMappingProtocol.Pcp, gateway, null, null,
                    TimeSpan.Zero, null, "The gateway returned a malformed PCP MAP response (" + response.Length + " bytes; header " +
                        BitConverter.ToString(response, 0, Math.Min(4, response.Length)) + ")."), canTryUpnp: true);

            byte resultCode = response[3];
            uint grantedLifetime = ReadUInt32(response, 4);
            if (response[0] != PcpVersion)
            {
                RouterMappingStatus versionStatus = resultCode == PcpUnsupportedVersion ? RouterMappingStatus.Unsupported : RouterMappingStatus.ProtocolError;
                return PcpMapReply.Failed(Result(versionStatus, RouterMappingProtocol.Pcp, gateway, null, null,
                    TimeSpan.FromSeconds(grantedLifetime), resultCode, "The gateway returned an unsupported PCP version."), canTryUpnp: true);
            }

            if (resultCode != 0)
            {
                // PCP error replies can contain only the common header. When the
                // echoed MAP body is present, still verify the transaction identity.
                if (response.Length >= PcpMapResponseMinimumLength && !ResponseMatchesPcpMap(response, mappingNonce, externalPort: null))
                    return PcpMapReply.Failed(Result(RouterMappingStatus.ProtocolError, RouterMappingProtocol.Pcp, gateway, null, null,
                        TimeSpan.Zero, null, "The PCP error response did not match this mapping's nonce or UDP port."));
                RouterMappingStatus status = resultCode == PcpUnsupportedVersion || resultCode == PcpUnsupportedOpcode || resultCode == PcpUnsupportedProtocol
                    ? RouterMappingStatus.Unsupported : RouterMappingStatus.Rejected;
                return PcpMapReply.Failed(Result(status, RouterMappingProtocol.Pcp, gateway, null, null,
                    TimeSpan.FromSeconds(grantedLifetime), resultCode, "The gateway rejected PCP MAP with result code " + resultCode + "."));
            }

            if (response.Length < PcpMapResponseMinimumLength || !ResponseMatchesPcpMap(response, mappingNonce, externalPort: null))
                return PcpMapReply.Failed(Result(RouterMappingStatus.ProtocolError, RouterMappingProtocol.Pcp, gateway, null, null,
                    TimeSpan.Zero, null, "The PCP MAP response did not match this mapping's nonce or UDP port."));

            ushort assignedPort = ReadUInt16(response, 42);
            IPAddress? assignedAddress = DecodePcpExternalIpv4(response, 44);
            return PcpMapReply.Success(grantedLifetime, assignedPort, assignedAddress, resultCode);
        }

        private async Task<NatPmpMapReply> RequestNatPmpMapAsync(IPAddress gateway, uint seconds, ushort suggestedExternalPort,
            CancellationToken cancellationToken)
        {
            byte[] request = new byte[12];
            request[0] = 0;
            request[1] = 1; // NAT-PMP UDP mapping request, RFC 6886 §3.3.
            WriteUInt16(request, 4, (ushort)internalPort);
            WriteUInt16(request, 6, suggestedExternalPort);
            WriteUInt32(request, 8, seconds);

            byte[]? response;
            try { response = await ExchangeAsync(gateway, request, cancellationToken).ConfigureAwait(false); }
            catch (SocketException ex)
            {
                return NatPmpMapReply.Failed(Result(RouterMappingStatus.TransportError, RouterMappingProtocol.NatPmp, gateway, null, null,
                    TimeSpan.Zero, null, "The NAT-PMP request failed: " + ex.SocketErrorCode));
            }
            if (response == null)
                return NatPmpMapReply.Failed(Result(RouterMappingStatus.TimedOut, RouterMappingProtocol.NatPmp, gateway, null, null,
                    TimeSpan.Zero, null, "The gateway did not answer the NAT-PMP mapping request."));
            if (response.Length < 8 || response[0] != 0 || response[1] != 0x81)
                return NatPmpMapReply.Failed(Result(RouterMappingStatus.ProtocolError, RouterMappingProtocol.NatPmp, gateway, null, null,
                    TimeSpan.Zero, null, "The gateway returned a malformed NAT-PMP mapping response."));

            ushort code = ReadUInt16(response, 2);
            if (code != 0)
            {
                if (response.Length != 16 || ReadUInt16(response, 8) != internalPort)
                    return NatPmpMapReply.Failed(Result(RouterMappingStatus.ProtocolError, RouterMappingProtocol.NatPmp, gateway, null, null,
                        TimeSpan.Zero, null, "The NAT-PMP error response did not match the requested internal port."));
                RouterMappingStatus status = code == NatPmpUnsupportedVersion ? RouterMappingStatus.Unsupported : RouterMappingStatus.Rejected;
                return NatPmpMapReply.Failed(Result(status, RouterMappingProtocol.NatPmp, gateway, null, null, TimeSpan.Zero, code,
                    "The gateway rejected the NAT-PMP mapping with result code " + code + "."));
            }
            if (response.Length != 16 || ReadUInt16(response, 8) != internalPort)
                return NatPmpMapReply.Failed(Result(RouterMappingStatus.ProtocolError, RouterMappingProtocol.NatPmp, gateway, null, null,
                    TimeSpan.Zero, null, "The NAT-PMP mapping response did not match the requested internal port."));

            return NatPmpMapReply.Success(ReadUInt32(response, 12), ReadUInt16(response, 10));
        }

        private async Task<IPAddress?> RequestNatPmpExternalAddressAsync(IPAddress gateway, CancellationToken cancellationToken)
        {
            byte[] request = new byte[] { 0, 0 };
            byte[]? response;
            try { response = await ExchangeAsync(gateway, request, cancellationToken).ConfigureAwait(false); }
            catch (SocketException) { return null; }
            if (response == null || response.Length != 12 || response[0] != 0 || response[1] != 0x80 || ReadUInt16(response, 2) != 0)
                return null;
            var address = new IPAddress(new byte[] { response[8], response[9], response[10], response[11] });
            return IPAddress.Any.Equals(address) ? null : address;
        }

        private async Task<byte[]?> ExchangeAsync(IPAddress gateway, byte[] request, CancellationToken cancellationToken)
        {
            var destination = new IPEndPoint(gateway, gatewayPort);
            SocketException? lastSocketError = null;
            for (int attempt = 0; attempt <= retryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int timeoutMilliseconds = (int)Math.Min(requestTimeout.TotalMilliseconds * (attempt == 0 ? 1 : Math.Pow(2, attempt)), 10000);
                using (var udp = new UdpClient(AddressFamily.InterNetwork))
                {
                    try
                    {
                        await udp.SendAsync(request, request.Length, destination).ConfigureAwait(false);
                        Task<UdpReceiveResult> receive = udp.ReceiveAsync();
                        Task delay = Task.Delay(timeoutMilliseconds, cancellationToken);
                        while (true)
                        {
                            Task completed = await Task.WhenAny(receive, delay).ConfigureAwait(false);
                            if (completed != receive)
                            {
                                udp.Close();
                                Observe(receive);
                                cancellationToken.ThrowIfCancellationRequested();
                                break;
                            }

                            UdpReceiveResult packet = await receive.ConfigureAwait(false);
                            if (destination.Equals(packet.RemoteEndPoint)) return packet.Buffer;
                            // Unsolicited packets from any other source are ignored; keep
                            // listening only until this attempt's original deadline.
                            receive = udp.ReceiveAsync();
                        }
                    }
                    catch (SocketException ex) { lastSocketError = ex; }
                    catch (ObjectDisposedException) { }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (lastSocketError != null && lastSocketError.SocketErrorCode != SocketError.TimedOut)
                throw lastSocketError;
            return null;
        }

        private IPAddress? ResolveGateway()
        {
            IPAddress? address = configuredGateway ?? gatewayResolver();
            return address?.AddressFamily == AddressFamily.InterNetwork ? address : null;
        }

        private static IPAddress? GetLocalIpv4Address(IPAddress gateway, int port)
        {
            using (var probe = new UdpClient(AddressFamily.InterNetwork))
            {
                probe.Connect(new IPEndPoint(gateway, port));
                return ((IPEndPoint)probe.Client.LocalEndPoint!).Address;
            }
        }

        private static bool IsNatPmpUnsupportedVersion(byte[] response) => response.Length == 8 && response[0] == 0 &&
            response[1] == 0 && ReadUInt16(response, 2) == NatPmpUnsupportedVersion;

        private bool ResponseMatchesPcpMap(byte[] response, byte[] nonce, ushort? externalPort)
        {
            // Reserved header/MAP fields must be ignored on receipt (RFC 6887
            // sections 7.2/11.1); they are not part of transaction identity.
            if (response.Length < PcpMapResponseMinimumLength || !BytesEqual(response, 24, nonce, 0, 12) ||
                response[36] != UdpProtocolNumber ||
                ReadUInt16(response, 40) != internalPort) return false;
            if (externalPort.HasValue && ReadUInt16(response, 42) != externalPort.Value) return false;
            return true;
        }

        private byte[] BuildPcpMapRequest(IPAddress clientAddress, uint lifetime, ushort externalPort, IPAddress? externalAddress)
        {
            var request = new byte[PcpMapRequestLength];
            request[0] = PcpVersion;
            request[1] = PcpMapOpcode;
            WriteUInt32(request, 4, lifetime);
            byte[] mappedClient = clientAddress.MapToIPv6().GetAddressBytes();
            Array.Copy(mappedClient, 0, request, 8, 16);
            Array.Copy(mappingNonce, 0, request, 24, 12);
            request[36] = UdpProtocolNumber;
            WriteUInt16(request, 40, (ushort)internalPort);
            WriteUInt16(request, 42, lifetime == 0 ? (ushort)0 : externalPort);
            if (lifetime != 0 && externalAddress != null && externalAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] mappedExternal = externalAddress.MapToIPv6().GetAddressBytes();
                Array.Copy(mappedExternal, 0, request, 44, 16);
            }
            return request;
        }

        private static IPAddress? DecodePcpExternalIpv4(byte[] response, int offset)
        {
            var address = new IPAddress(response.Skip(offset).Take(16).ToArray());
            if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
            return address.AddressFamily == AddressFamily.InterNetwork ? address : null;
        }

        private static bool BytesEqual(byte[] left, int leftOffset, byte[] right, int rightOffset, int length)
        {
            if (leftOffset + length > left.Length || rightOffset + length > right.Length) return false;
            for (int index = 0; index < length; index++) if (left[leftOffset + index] != right[rightOffset + index]) return false;
            return true;
        }

        private static byte[] CreateNonce()
        {
            var nonce = new byte[12];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(nonce);
            return nonce;
        }

        private static uint LifetimeSeconds(TimeSpan lifetime, bool allowZero)
        {
            if (lifetime < TimeSpan.Zero || (!allowZero && lifetime == TimeSpan.Zero)) throw new ArgumentOutOfRangeException(nameof(lifetime));
            double seconds = Math.Ceiling(lifetime.TotalSeconds);
            if (seconds > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(lifetime));
            return (uint)seconds;
        }

        private static ushort ReadUInt16(byte[] bytes, int offset) => (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
        private static uint ReadUInt32(byte[] bytes, int offset) => ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
            ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        private static void WriteUInt16(byte[] bytes, int offset, ushort value)
        { bytes[offset] = (byte)(value >> 8); bytes[offset + 1] = (byte)value; }
        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value >> 24); bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8); bytes[offset + 3] = (byte)value;
        }

        private static void Observe(Task task) => task.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        private RouterMappingResult KeepCurrent(RouterMappingResult failure, RouterMappingResult previous)
        {
            current = previous;
            return failure;
        }

        private async Task<RouterMappingResult> TryUpnpFallbackAsync(IPAddress gateway, uint seconds, RouterMappingResult prior, CancellationToken token, bool canTryUpnp = false)
        {
            // An unrecognized PCP header does not rule out a working independent
            // UPnP service. Explicit rejection and mismatched MAP identity still stop.
            if (!upnpOptions.EnableUpnp || (!canTryUpnp && prior.Status != RouterMappingStatus.TimedOut && prior.Status != RouterMappingStatus.Unsupported && prior.Status != RouterMappingStatus.TransportError)) return prior;
            upnp ??= new UpnpPortMapping(internalPort, requestedExternalPort, gateway, upnpOptions);
            var mapped = await upnp.MapAsync(seconds, token).ConfigureAwait(false);
            if (mapped.HasActiveMapping) current = mapped;
            else if (canTryUpnp)
                return new RouterMappingResult(mapped.Status, mapped.Protocol, mapped.GatewayAddress, mapped.ExternalAddress, mapped.ExternalPort,
                    mapped.GrantedLifetime, mapped.ProtocolResultCode, prior.Message + " UPnP fallback: " + mapped.Message, mapped.IsPermanent);
            return mapped;
        }

        private static RouterMappingResult Result(RouterMappingStatus status, RouterMappingProtocol protocol, IPAddress? gateway,
            IPAddress? externalAddress, ushort? externalPort, TimeSpan lifetime, int? code, string message) =>
            new RouterMappingResult(status, protocol, gateway, externalAddress, externalPort, lifetime, code, message);

        private void ThrowIfDisposed()
        { if (disposed) throw new ObjectDisposedException(nameof(RouterPortMapper)); }

        private sealed class PcpMapReply
        {
            public bool NatPmpServer { get; private set; }
            public bool CanTryUpnp { get; private set; }
            public RouterMappingResult? Result { get; private set; }
            public uint Lifetime { get; private set; }
            public ushort ExternalPort { get; private set; }
            public IPAddress? ExternalAddress { get; private set; }
            public byte ResultCode { get; private set; }
            public static PcpMapReply NatPmpDetected() => new PcpMapReply { NatPmpServer = true };
            public static PcpMapReply Failed(RouterMappingResult result, bool canTryUpnp = false) => new PcpMapReply { Result = result, CanTryUpnp = canTryUpnp };
            public static PcpMapReply Success(uint lifetime, ushort externalPort, IPAddress? externalAddress, byte code) =>
                new PcpMapReply { Lifetime = lifetime, ExternalPort = externalPort, ExternalAddress = externalAddress, ResultCode = code };
        }

        private sealed class NatPmpMapReply
        {
            public RouterMappingResult? Result { get; private set; }
            public uint Lifetime { get; private set; }
            public ushort ExternalPort { get; private set; }
            public ushort ResultCode { get; private set; }
            public static NatPmpMapReply Failed(RouterMappingResult result) => new NatPmpMapReply { Result = result };
            public static NatPmpMapReply Success(uint lifetime, ushort externalPort) =>
                new NatPmpMapReply { Lifetime = lifetime, ExternalPort = externalPort, ResultCode = 0 };
        }
    }
}
