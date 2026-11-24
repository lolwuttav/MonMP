using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MonMP.Core.Transport;

namespace MonMP.Plugin
{
    // The router transaction never blocks the Unity frame or the gameplay listener.
    internal sealed class RouterMappingHost
    {
        private readonly Random renewalJitter = new Random();
        private RouterPortMapper? mapper;
        private CancellationTokenSource? cancellation;
        private Task<RouterMappingResult>? operation;
        private Task? cleanup;
        private DateTime renewAtUtc;
        private int renewalFailures;
        private int pendingPort;

        internal string Status { get; private set; } = "";

        internal void Start(int udpPort)
        {
            if (udpPort < 1 || udpPort > 65535) throw new ArgumentOutOfRangeException(nameof(udpPort));
            Stop();
            pendingPort = udpPort;
            Status = "Requesting a UDP router mapping; hosting continues while the router responds.";
            StartPending();
        }

        internal void Tick(bool hosting)
        {
            if (!hosting)
            {
                if (mapper != null || pendingPort != 0) Stop();
                return;
            }

            if (cleanup?.IsCompleted == true)
            {
                // Cleanup observes its own failures; finite leases also expire without a shutdown packet.
                cleanup = null;
            }
            StartPending();
            if (operation?.IsCompleted == true)
            {
                var finished = operation;
                operation = null;
                try { Report(finished.GetAwaiter().GetResult()); }
                catch (OperationCanceledException) { Status = "Router mapping canceled."; }
                catch (Exception ex) { Status = "Router mapping failed: " + ex.Message; RetryPriorLease(); }
            }
            if (mapper != null && operation == null && renewAtUtc != default && DateTime.UtcNow >= renewAtUtc)
            {
                renewAtUtc = default;
                try
                {
                    operation = mapper.RenewAsync(TimeSpan.FromHours(2), cancellation!.Token);
                    Status = "Renewing the UDP router mapping.";
                }
                catch (Exception ex) { Status = "Router renewal failed: " + ex.Message; }
            }
        }

        internal void Stop()
        {
            pendingPort = 0;
            renewAtUtc = default;
            renewalFailures = 0;
            Status = "";
            var oldMapper = mapper;
            var oldCancellation = cancellation;
            var oldOperation = operation;
            mapper = null;
            cancellation = null;
            operation = null;
            oldCancellation?.Cancel();
            if (oldMapper != null)
                cleanup = CleanupAsync(cleanup, oldMapper, oldOperation, oldCancellation);
            else oldCancellation?.Dispose();
        }

        private void StartPending()
        {
            if (pendingPort == 0 || mapper != null || cleanup != null) return;
            int udpPort = pendingPort;
            pendingPort = 0;
            try
            {
                mapper = new RouterPortMapper(udpPort);
                cancellation = new CancellationTokenSource();
                operation = mapper.MapAsync(TimeSpan.FromHours(2), cancellation.Token);
            }
            catch (Exception ex)
            {
                Status = "Router mapping unavailable: " + ex.Message;
                var failedMapper = mapper;
                mapper = null;
                cancellation?.Dispose();
                cancellation = null;
                if (failedMapper != null) cleanup = CleanupAsync(cleanup, failedMapper, null, null);
            }
        }

        private void Report(RouterMappingResult result)
        {
            if (result.Status != RouterMappingStatus.Mapped && result.Status != RouterMappingStatus.Renewed)
            {
                Status = "Router mapping unavailable: " + result.Message;
                RetryPriorLease();
                return;
            }

            renewalFailures = 0;

            string protocol = result.Protocol == RouterMappingProtocol.Pcp ? "PCP" : result.Protocol == RouterMappingProtocol.Upnp ? "UPnP" : "NAT-PMP";
            string endpoint = result.ExternalAddress != null && result.ExternalPort.HasValue
                ? result.ExternalAddress + ":" + result.ExternalPort.Value.ToString(CultureInfo.InvariantCulture)
                : "external address unavailable";
            Status = protocol + " mapped gameplay UDP to " + endpoint + ". External reachability is unverified.";
            if (result.IsPermanent) Status += " This router requires a permanent rule; MonMP requests removal when hosting stops.";
            if (result.ExternalAddress != null && ExternalAddressScope.IsPrivateOrSharedIPv4(result.ExternalAddress))
                Status += " Router reports a private/shared address; upstream NAT is likely.";

            double seconds = result.IsPermanent ? 3600 : result.Lifetime.TotalSeconds;
            if (seconds <= 0) { renewAtUtc = default; return; }
            double fraction = result.Protocol == RouterMappingProtocol.Pcp
                ? 0.5 + renewalJitter.NextDouble() * 0.125
                : 0.5;
            renewAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(0.1, seconds * fraction));
        }

        private void RetryPriorLease()
        {
            renewAtUtc = default;
            var priorLease = mapper?.Current;
            if (priorLease?.HasActiveMapping != true || (!priorLease.IsPermanent && (priorLease.ExpiresAtUtc == null || priorLease.ExpiresAtUtc.Value <= DateTime.UtcNow))) return;
            double remaining = priorLease.IsPermanent ? 300 : (priorLease.ExpiresAtUtc!.Value - DateTime.UtcNow).TotalSeconds;
            if (remaining <= 2) return;
            renewalFailures = Math.Min(renewalFailures + 1, 6);
            double backoff = Math.Min(120, 15 * Math.Pow(2, renewalFailures - 1));
            renewAtUtc = DateTime.UtcNow.AddSeconds(Math.Min(backoff, remaining / 2));
            Status += priorLease.IsPermanent ? " Retrying the UPnP mapping check." : " The previous lease is still active; retrying renewal before it expires.";
        }

        private static async Task CleanupAsync(Task? priorCleanup, RouterPortMapper oldMapper,
            Task<RouterMappingResult>? oldOperation, CancellationTokenSource? oldCancellation)
        {
            try
            {
                if (priorCleanup != null) await priorCleanup.ConfigureAwait(false);
                if (oldOperation != null)
                {
                    try { await oldOperation.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch { }
                }
                await oldMapper.DisposeAsync().ConfigureAwait(false);
            }
            catch { /* Finite leases expire; a legacy permanent rule may need manual router cleanup. */ }
            finally { oldCancellation?.Dispose(); }
        }
    }
}
