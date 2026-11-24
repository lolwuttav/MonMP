using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;

namespace MonMP.Core.Session
{
    public sealed partial class NetworkSession
    {
        public SessionCheatState Cheats { get; private set; } = new SessionCheatState(false);
        private readonly Dictionary<uint, (PlayerPose Pose, double At)> latestSocialPoses = new Dictionary<uint, (PlayerPose, double)>();
        private ChatRateLimit localPingRate = new ChatRateLimit(), localCheatRate = new ChatRateLimit();
        public event Action<SessionCheatState>? CheatsChanged;
        public event Action<uint, bool>? CheatPermissionChanged;
        public event Action<uint, CheatActionRequest>? CheatActionReceived;
        public event Action<CheatFeedback>? CheatFeedbackReceived;
        public event Action<SocialPing>? PingReceived;
        internal IEnumerable<KeyValuePair<Guid, string>> ProfileIdentityHints => profileNames.ToArray();

        public void SetCheatsEnabled(bool enabled) => SetCheats(new SessionCheatState(enabled, Cheats.GravityY, Cheats.Allowed));
        public void SetCheatGravity(float y) => SetCheats(new SessionCheatState(Cheats.Enabled, y, Cheats.Allowed));
        public void SetCheatPermission(uint peer, bool allowed)
        {
            if (!IsHost || peer == 0 || !peers.ContainsKey(peer)) throw new InvalidOperationException("Only the host can grant a connected guest permission.");
            var grants = new HashSet<uint>(Cheats.Allowed);
            if (allowed) grants.Add(peer); else grants.Remove(peer);
            SetCheats(new SessionCheatState(Cheats.Enabled, Cheats.GravityY, grants));
            // Explicit grants/revocations are preferences. Departure cleanup is only
            // a connection change and must not erase the remembered permission.
            CheatPermissionChanged?.Invoke(peer, allowed);
        }
        private void SetCheats(SessionCheatState state)
        {
            if (!IsHost) throw new InvalidOperationException("Only the host can change session cheats.");
            state = PreserveEnabledCheats(state);
            if (state.Enabled == Cheats.Enabled && state.GravityY == Cheats.GravityY && state.Allowed.SequenceEqual(Cheats.Allowed)) return;
            Cheats = state; CheatsChanged?.Invoke(state);
            Broadcast(Wire.Encode(MessageKind.CheatState, Epoch, state.Write), true, worldOnly: true);
        }
        // Vanilla activation is permanent for the loaded game. Keep the same one-way
        // policy until this network session ends, while allowing grants to be revoked.
        private SessionCheatState PreserveEnabledCheats(SessionCheatState state) =>
            Cheats.Enabled && !state.Enabled ? new SessionCheatState(true, state.GravityY, state.Allowed) : state;
        private void SendCheatState(int connection) => transport!.Send(connection, Wire.Encode(MessageKind.CheatState, Epoch, Cheats.Write), true);
        public bool TryGetSocialPose(uint peer, out PlayerPose pose)
        {
            pose = default;
            if (!latestSocialPoses.TryGetValue(peer, out var sample) || clock.Elapsed.TotalSeconds - sample.At > 2) return false;
            pose = sample.Pose; return true;
        }
        public bool RequestCheatAction(CheatActionRequest action)
        {
            if (!IsReady || !Cheats.Permitted(LocalPeerId) || !localCheatRate.Take(clock.Elapsed.TotalSeconds)) return false;
            if (IsHost) CheatActionReceived?.Invoke(0, action);
            else if (!QueueSocial(Wire.Encode(MessageKind.CheatAction, Epoch, action.Write))) return false;
            return true;
        }
        public bool SendCheatFeedback(uint peer, CheatFeedback feedback)
        {
            if (!IsHost) throw new InvalidOperationException("Only the host can send cheat feedback.");
            if (peer == 0) { CheatFeedbackReceived?.Invoke(feedback); return true; }
            int connection = WorldConnectionFor(peer);
            if (connection < 0) return false;
            transport!.Send(connection, Wire.Encode(MessageKind.CheatFeedback, Epoch, feedback.Write), true);
            return true;
        }
        public bool SendPing(float x, float y, float z, bool point)
        {
            if (!IsReady || !TryGetSocialPose(LocalPeerId, out var pose) || !localPingRate.Take(clock.Elapsed.TotalSeconds)) return false;
            var ping = new SocialPing(LocalPeerId, x, y, z, point);
            if (!PingInRange(ping, pose)) return false;
            if (IsHost) { PingReceived?.Invoke(ping); Broadcast(Wire.Encode(MessageKind.SocialPing, Epoch, ping.Write), true, worldOnly: true); }
            else if (!QueueSocial(Wire.Encode(MessageKind.SocialPing, Epoch, ping.Write))) return false;
            return true;
        }
        private bool QueueSocial(byte[] bytes)
        {
            if (guestReliable.Count >= GuestQueueLimit) return false;
            guestReliable.Enqueue(bytes); FlushGuestQueue(); return true;
        }
        private static bool PingInRange(SocialPing ping, PlayerPose pose)
        {
            double x = ping.X - pose.X, y = ping.Y - pose.Y, z = ping.Z - pose.Z;
            return !pose.Spectating && pose.Rest == PlayerRest.None && x*x + y*y + z*z <= (SocialPing.MaxDistance + 5) * (SocialPing.MaxDistance + 5);
        }
        private void ReceiveSocial(Member member, Packet packet, double now)
        {
            if (!IsReady || IsHost && !member.WorldReady) throw new InvalidDataException("Social control before scene registration.");
            switch (packet.Kind)
            {
                case MessageKind.CheatState:
                    if (IsHost) throw new InvalidDataException("Guest cannot set session cheat state.");
                    var state = SessionCheatState.Read(packet.Reader); packet.Complete();
                    if (state.Allowed.Any(id => !peers.ContainsKey(id))) throw new InvalidDataException("Unknown cheat permission peer.");
                    Cheats = PreserveEnabledCheats(state); CheatsChanged?.Invoke(Cheats); break;
                case MessageKind.CheatAction:
                    if (!IsHost) throw new InvalidDataException("Only the host receives shared cheat actions.");
                    var action = CheatActionRequest.Read(packet.Reader); packet.Complete();
                    // An in-flight action after revocation is inert, rather than disconnecting an ordinary guest.
                    if (!Cheats.Permitted(member.PeerId) || !member.CheatRate.Take(now) || !TryGetSocialPose(member.PeerId, out var actor) || actor.Spectating || actor.Rest != PlayerRest.None) return;
                    CheatActionReceived?.Invoke(member.PeerId, action); break;
                case MessageKind.CheatFeedback:
                    if (IsHost) throw new InvalidDataException("Guest cannot send cheat feedback.");
                    var feedback = CheatFeedback.Read(packet.Reader); packet.Complete();
                    CheatFeedbackReceived?.Invoke(feedback); break;
                case MessageKind.SocialPing:
                    var ping = SocialPing.Read(packet.Reader); packet.Complete();
                    if (!peers.ContainsKey(ping.PeerId) || IsHost && ping.PeerId != member.PeerId) throw new InvalidDataException("Ping identity spoofing.");
                    if (IsHost && (!member.PingRate.Take(now) || !TryGetSocialPose(member.PeerId, out var source) || !PingInRange(ping, source))) return;
                    PingReceived?.Invoke(ping);
                    if (IsHost) Broadcast(Wire.Encode(MessageKind.SocialPing, Epoch, ping.Write), true, worldOnly: true);
                    break;
            }
        }
        private void ResetSocial()
        {
            Cheats = new SessionCheatState(false); latestSocialPoses.Clear();
            localPingRate = new ChatRateLimit(); localCheatRate = new ChatRateLimit();
        }
    }
}
