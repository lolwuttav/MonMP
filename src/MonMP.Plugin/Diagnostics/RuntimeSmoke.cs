using System;
using System.Linq;
using System.Net;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.Transport;

namespace MonMP.Plugin.Diagnostics
{
    // Opt-in command-line startup check. Never loads a save or touches game/player state.
    internal sealed class RuntimeSmoke : IDisposable
    {
        private readonly NetworkSession host;
        private readonly NetworkSession client;
        private readonly LanDiscovery advertiser, browser;
        private bool sent;
        private bool posePassed;
        public bool Passed => posePassed && browser.Hosts.Any(h => h.Session.Compatible && h.Session.Port == host.Port);
        public RuntimeSmoke()
        {
            host = new NetworkSession(new SessionOptions { DisplayName = "SmokeHost", Scene = "MonMP.RuntimeSmoke", EnableIPv6 = false });
            client = new NetworkSession(new SessionOptions { DisplayName = "SmokeClient", Scene = "MonMP.RuntimeSmoke", EnableIPv6 = false });
            host.PoseReceived += (id, pose) => posePassed = id == client.LocalPeerId && pose.X == 42 && host.Peers.Count == 2;
            advertiser = new LanDiscovery(() => new LanSession(host.Epoch, "SmokeHost", host.Port, host.Peers.Count, false));
            browser = new LanDiscovery();
            try { host.Host(0); client.Join("127.0.0.1", host.Port); browser.Search(new IPEndPoint(IPAddress.Loopback, advertiser.LocalPort)); }
            catch { Dispose(); throw; }
        }
        public void Tick()
        {
            host.Tick(); client.Tick();
            advertiser.Poll(); browser.Poll();
            if (client.IsReady && !sent) { client.SendPose(new PlayerPose(42, 1, 2, 30, 10)); sent = true; }
        }
        public void Dispose() { browser.Dispose(); advertiser.Dispose(); client.Dispose(); host.Dispose(); }
    }
}
