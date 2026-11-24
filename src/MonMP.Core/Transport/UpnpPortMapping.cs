using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace MonMP.Core.Transport
{
    // IGD control stays on the selected IPv4 gateway. No redirects, proxies, DTDs,
    // unbounded XML bodies, or changes to an existing entry owned by another app.
    internal sealed class UpnpPortMapping : IDisposable
    {
        private const int MaxXmlBytes = 65536;
        private readonly int internalPort;
        private readonly ushort requestedPort;
        private readonly IPAddress gateway;
        private readonly IPEndPoint discoveryEndpoint;
        private readonly TimeSpan discoveryTimeout, httpTimeout;
        private readonly HttpClient http;
        private readonly string description = "MonMP-" + Guid.NewGuid().ToString("N");
        private Mapping? mapping;
        private sealed class Service
        {
            internal string Type = "";
            internal Uri Control = null!;
        }
        private sealed class Mapping
        {
            internal Service Service = null!;
            internal IPAddress Client = null!;
            internal ushort Port;
            internal uint Seconds;
            internal bool Permanent;
            internal IPAddress? External;
        }
        private sealed class Entry
        {
            internal string Client = "", Description = "";
            internal int Port;
            internal uint Seconds;
            internal bool Enabled;
        }
        private sealed class Failure : Exception
        {
            internal readonly RouterMappingStatus Status;
            internal readonly int? Code;
            internal Failure(RouterMappingStatus status, string message, int? code = null) : base(message) { Status = status; Code = code; }
        }

        internal UpnpPortMapping(int port, ushort requested, IPAddress gateway, RouterPortMapperOptions options)
        {
            internalPort = port; requestedPort = requested; this.gateway = gateway;
            discoveryEndpoint = options.UpnpDiscoveryEndpoint; discoveryTimeout = options.UpnpDiscoveryTimeout; httpTimeout = options.UpnpHttpTimeout;
            http = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        }

        internal async Task<RouterMappingResult> MapAsync(uint seconds, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var token = deadline.Token;
            try
            {
                if (mapping != null)
                {
                    var cleanup = await DeleteAsync(token).ConfigureAwait(false);
                    if (mapping != null) return cleanup;
                }
                IPAddress local;
                using (var probe = new UdpClient(AddressFamily.InterNetwork))
                {
                    probe.Connect(gateway, discoveryEndpoint.Port);
                    local = ((IPEndPoint)probe.Client.LocalEndPoint!).Address;
                }
                var locations = await DiscoverAsync(local, token).ConfigureAwait(false);
                Failure? last = null;
                foreach (var location in locations)
                {
                    Service[] services;
                    try { services = ReadServices(await RequestAsync(HttpMethod.Get, location, null, null, token).ConfigureAwait(false), location); }
                    catch (Failure ex) { last = ex; continue; }
                    foreach (var service in services)
                    {
                        foreach (ushort port in CandidatePorts())
                        {
                            bool addReturned = false;
                            try
                            {
                                if (await ReadEntryAsync(service, port, token).ConfigureAwait(false) != null) continue;
                                mapping = new Mapping { Service = service, Client = local, Port = port };
                                await AddAsync(seconds, token).ConfigureAwait(false);
                                addReturned = true;
                                return await ConfirmAsync(RouterMappingStatus.Mapped, token).ConfigureAwait(false);
                            }
                            catch (Failure ex)
                            {
                                last = ex;
                                // A definitive SOAP fault did not create an entry. A transport
                                // timeout is ambiguous: keep the provisional tag for cleanup,
                                // rather than opening more ports on another attempt.
                                if (ex.Code.HasValue && !addReturned) mapping = null;
                                else if (mapping != null) return Failed(ex);
                                if (ex.Code == 606) return Failed(ex);
                            }
                        }
                    }
                }
                return last != null ? Failed(last) : Result(RouterMappingStatus.Unsupported, "No usable UPnP Internet Gateway service or free UDP mapping was found.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { return Result(RouterMappingStatus.TimedOut, "UPnP discovery and mapping reached their time limit."); }
            catch (Failure ex) { return Failed(ex); }
            catch (SocketException) { return Result(RouterMappingStatus.TransportError, "The UPnP gateway route is unavailable."); }
        }

        internal async Task<RouterMappingResult> RenewAsync(uint seconds, CancellationToken token)
        {
            if (mapping == null) return Result(RouterMappingStatus.NoMapping, "There is no UPnP mapping to renew.");
            try
            {
                var entry = await ReadEntryAsync(mapping.Service, mapping.Port, token).ConfigureAwait(false);
                if (entry != null && !Owns(entry))
                {
                    mapping = null;
                    return Result(RouterMappingStatus.NoMapping, "The UPnP port now belongs to another rule; it was left unchanged.");
                }
                await AddAsync(seconds, token).ConfigureAwait(false);
                return await ConfirmAsync(RouterMappingStatus.Renewed, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Failure ex) { return Failed(ex); }
        }

        internal async Task<RouterMappingResult> DeleteAsync(CancellationToken token)
        {
            if (mapping == null) return Result(RouterMappingStatus.NoMapping, "There is no UPnP mapping to remove.");
            try
            {
                var prior = mapping;
                var entry = await ReadEntryAsync(prior.Service, prior.Port, token).ConfigureAwait(false);
                if (entry != null && !Owns(entry))
                {
                    mapping = null;
                    return Result(RouterMappingStatus.NoMapping, "The UPnP rule belongs to another owner; it was left unchanged.");
                }
                if (entry != null)
                {
                    await SoapAsync(prior.Service, "DeletePortMapping", PortArguments(prior.Port), token).ConfigureAwait(false);
                    var remaining = await ReadEntryAsync(prior.Service, prior.Port, token).ConfigureAwait(false);
                    if (remaining != null && Owns(remaining)) throw new Failure(RouterMappingStatus.Rejected, "The router did not remove the UPnP mapping.");
                }
                mapping = null;
                return new RouterMappingResult(RouterMappingStatus.Deleted, RouterMappingProtocol.Upnp, gateway, prior.External,
                    prior.Port, TimeSpan.Zero, null, "The gateway confirmed UPnP mapping removal.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Failure ex) { return Failed(ex); }
        }

        private async Task AddAsync(uint seconds, CancellationToken token)
        {
            var m = mapping!;
            XElement[] Arguments(uint duration) => PortArguments(m.Port).Concat(new[] {
                new XElement("NewInternalPort", internalPort), new XElement("NewInternalClient", m.Client.ToString()),
                new XElement("NewEnabled", "1"), new XElement("NewPortMappingDescription", description),
                new XElement("NewLeaseDuration", duration) }).ToArray();
            try { await SoapAsync(m.Service, "AddPortMapping", Arguments(m.Permanent ? 0 : Math.Min(seconds, 604800u)), token).ConfigureAwait(false); }
            catch (Failure ex) when (ex.Code == 725)
            {
                // Older IGD devices may support only a permanent entry. Report that
                // honestly, retain ownership verification, and explicitly remove it on stop.
                m.Permanent = true;
                await SoapAsync(m.Service, "AddPortMapping", Arguments(0), token).ConfigureAwait(false);
            }
        }

        private async Task<RouterMappingResult> ConfirmAsync(RouterMappingStatus status, CancellationToken token)
        {
            var m = mapping!;
            var entry = await ReadEntryAsync(m.Service, m.Port, token).ConfigureAwait(false);
            if (entry == null || !Owns(entry) || !entry.Enabled)
                throw new Failure(RouterMappingStatus.ProtocolError, "The router did not confirm the requested UPnP mapping.");
            m.Seconds = entry.Seconds; m.Permanent = entry.Seconds == 0;
            try
            {
                var external = await SoapAsync(m.Service, "GetExternalIPAddress", Array.Empty<XElement>(), token).ConfigureAwait(false);
                if (IPAddress.TryParse(Value(external, "NewExternalIPAddress"), out var address) && address.AddressFamily == AddressFamily.InterNetwork &&
                    !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.Broadcast)) m.External = address;
            }
            catch (Failure) { /* The verified forwarding rule is useful even when its public address is unavailable. */ }
            return new RouterMappingResult(status, RouterMappingProtocol.Upnp, gateway, m.External, m.Port, TimeSpan.FromSeconds(m.Seconds),
                null, status == RouterMappingStatus.Mapped ? "The gateway accepted the UPnP mapping." : "The gateway renewed the UPnP mapping.", m.Permanent);
        }

        private bool Owns(Entry entry) => mapping != null && entry.Port == internalPort &&
            IPAddress.TryParse(entry.Client, out var address) && address.Equals(mapping.Client) && entry.Description == description;

        private async Task<Entry?> ReadEntryAsync(Service service, ushort port, CancellationToken token)
        {
            XDocument doc;
            try { doc = await SoapAsync(service, "GetSpecificPortMappingEntry", PortArguments(port), token).ConfigureAwait(false); }
            catch (Failure ex) when (ex.Code == 714) { return null; }
            if (!int.TryParse(Value(doc, "NewInternalPort"), NumberStyles.None, CultureInfo.InvariantCulture, out int internalValue) || internalValue < 1 || internalValue > 65535 ||
                !uint.TryParse(Value(doc, "NewLeaseDuration"), NumberStyles.None, CultureInfo.InvariantCulture, out uint lifetime))
                throw new Failure(RouterMappingStatus.ProtocolError, "The router returned an invalid UPnP entry.");
            return new Entry { Client = Value(doc, "NewInternalClient"), Port = internalValue, Description = Value(doc, "NewPortMappingDescription"),
                Seconds = lifetime, Enabled = Value(doc, "NewEnabled") == "1" || Value(doc, "NewEnabled") == "true" };
        }

        private static XElement[] PortArguments(ushort port) => new[] {
            new XElement("NewRemoteHost", ""), new XElement("NewExternalPort", port), new XElement("NewProtocol", "UDP") };
        private IEnumerable<ushort> CandidatePorts()
        {
            yield return requestedPort == 0 ? (ushort)internalPort : requestedPort;
            if (requestedPort != 0 && requestedPort != internalPort) yield return (ushort)internalPort;
            for (int i = 0; i < 3; i++) yield return (ushort)(49152 + (BitConverter.ToUInt16(Guid.NewGuid().ToByteArray(), 0) % 16384));
        }

        private async Task<List<Uri>> DiscoverAsync(IPAddress local, CancellationToken token)
        {
            var found = new List<Uri>();
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            foreach (string target in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANPPPConnection:1" })
            {
                byte[] request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: " + target + "\r\n\r\n");
                await udp.SendAsync(request, request.Length, discoveryEndpoint).ConfigureAwait(false);
            }
            var deadline = Task.Delay(discoveryTimeout, token);
            Task<UdpReceiveResult> receive = udp.ReceiveAsync();
            while (found.Count < 4 && !deadline.IsCompleted)
            {
                if (await Task.WhenAny(receive, deadline).ConfigureAwait(false) != receive)
                {
                    udp.Close();
                    _ = receive.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    token.ThrowIfCancellationRequested(); break;
                }
                var response = await receive.ConfigureAwait(false);
                if (response.RemoteEndPoint.Address.Equals(gateway) && response.Buffer.Length <= 8192)
                {
                    string[] lines = Encoding.ASCII.GetString(response.Buffer).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    if (lines.Length > 0 && lines[0].StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase))
                        foreach (string line in lines.Skip(1))
                            if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line.Substring(9).Trim(), UriKind.Absolute, out var location) &&
                                GatewayUri(location) && !found.Contains(location) && found.Count < 4) found.Add(location);
                }
                if (found.Count >= 4) break;
                receive = udp.ReceiveAsync();
            }
            udp.Close();
            _ = receive.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            token.ThrowIfCancellationRequested();
            return found;
        }

        private Service[] ReadServices(XDocument doc, Uri location)
        {
            var basis = location;
            var baseText = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value;
            if (!string.IsNullOrEmpty(baseText))
            {
                if (!Uri.TryCreate(baseText, UriKind.Absolute, out basis) || !GatewayUri(basis))
                    throw new Failure(RouterMappingStatus.ProtocolError, "The UPnP device advertised a URL outside the selected gateway.");
            }
            var services = new List<Service>();
            foreach (var service in doc.Descendants().Where(e => e.Name.LocalName == "service"))
            {
                string type = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value.Trim() ?? "";
                if (type != "urn:schemas-upnp-org:service:WANIPConnection:1" && type != "urn:schemas-upnp-org:service:WANIPConnection:2" &&
                    type != "urn:schemas-upnp-org:service:WANPPPConnection:1") continue;
                string control = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value.Trim() ?? "";
                if (Uri.TryCreate(basis, control, out var uri) && GatewayUri(uri) && services.Count < 4)
                    services.Add(new Service { Type = type, Control = uri });
            }
            return services.OrderByDescending(s => s.Type.EndsWith(":2", StringComparison.Ordinal)).ToArray();
        }

        private bool GatewayUri(Uri uri) => (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && IPAddress.TryParse(uri.Host, out var ip) && ip.Equals(gateway);

        private Task<XDocument> SoapAsync(Service service, string action, XElement[] arguments, CancellationToken token)
        {
            XNamespace envelope = "http://schemas.xmlsoap.org/soap/envelope/";
            var document = new XDocument(new XElement(envelope + "Envelope", new XAttribute(XNamespace.Xmlns + "s", envelope),
                new XAttribute(envelope + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
                new XElement(envelope + "Body", new XElement(XName.Get(action, service.Type), arguments))));
            return RequestAsync(HttpMethod.Post, service.Control, service.Type + "#" + action, document.ToString(SaveOptions.DisableFormatting), token);
        }

        private async Task<XDocument> RequestAsync(HttpMethod method, Uri uri, string? action, string? body, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(httpTimeout);
            try
            {
                using var request = new HttpRequestMessage(method, uri);
                if (action != null)
                {
                    request.Headers.TryAddWithoutValidation("SOAPAction", "\"" + action + "\"");
                    request.Content = new StringContent(body!, Encoding.UTF8, "text/xml");
                }
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength > MaxXmlBytes) throw new Failure(RouterMappingStatus.ProtocolError, "UPnP response exceeds its size limit.");
                using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var bytes = new MemoryStream();
                byte[] buffer = new byte[4096]; int count;
                while ((count = await source.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (bytes.Length + count > MaxXmlBytes) throw new Failure(RouterMappingStatus.ProtocolError, "UPnP response exceeds its size limit.");
                    bytes.Write(buffer, 0, count);
                }
                bytes.Position = 0;
                using var xml = XmlReader.Create(bytes, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes });
                var doc = XDocument.Load(xml);
                if (int.TryParse(Value(doc, "errorCode"), NumberStyles.None, CultureInfo.InvariantCulture, out int code))
                    throw new Failure(RouterMappingStatus.Rejected, "The UPnP gateway rejected the action (" + code.ToString(CultureInfo.InvariantCulture) + ").", code);
                if (!response.IsSuccessStatusCode) throw new Failure(RouterMappingStatus.ProtocolError, "The UPnP gateway returned an unsuccessful HTTP response.");
                if (action != null)
                {
                    int split = action.IndexOf('#');
                    XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
                    string name = action.Substring(split + 1) + "Response";
                    if (doc.Root?.Name != soap + "Envelope" || doc.Root.Element(soap + "Body")?.Element(XName.Get(name, action.Substring(0, split))) == null)
                        throw new Failure(RouterMappingStatus.ProtocolError, "The UPnP response does not match the requested action.");
                }
                return doc;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new Failure(RouterMappingStatus.TimedOut, "The UPnP HTTP request timed out."); }
            catch (HttpRequestException) { throw new Failure(RouterMappingStatus.TransportError, "The UPnP HTTP request failed."); }
            catch (IOException) { throw new Failure(RouterMappingStatus.TransportError, "The UPnP HTTP connection closed before completion."); }
            catch (XmlException) { throw new Failure(RouterMappingStatus.ProtocolError, "The gateway returned invalid UPnP XML."); }
        }

        private static string Value(XDocument doc, string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
        private RouterMappingResult Failed(Failure ex) => new RouterMappingResult(ex.Status, RouterMappingProtocol.Upnp, gateway, null, mapping?.Port, TimeSpan.Zero, ex.Code, ex.Message);
        private RouterMappingResult Result(RouterMappingStatus status, string message) => new RouterMappingResult(status, RouterMappingProtocol.Upnp, gateway, null, null, TimeSpan.Zero, null, message);
        public void Dispose() => http.Dispose();
    }
}
