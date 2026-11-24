using System;
using System.Collections.Generic;
using System.IO;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Only an authorized host action opens a frame. Its native ticket retains all
    // accounting and mail behavior, while its personal notice belongs to the actor.
    internal static class NativePoliceFinePresentation
    {
        private sealed class Frame
        {
            internal uint Peer;
            internal Action<uint, byte[]> Reply = null!;
        }

        private sealed class Scope : IDisposable
        {
            private readonly int depth;
            private bool disposed;
            internal Scope(int depth) { this.depth = depth; }
            public void Dispose()
            {
                if (disposed) return; disposed = true;
                if (frames.Count > depth) frames.RemoveRange(depth, frames.Count - depth);
            }
        }

        internal sealed class Capture
        {
            private readonly Frame frame;
            private readonly bool wasStored;
            private readonly IDisposable muted;
            private bool completed;
            internal Capture(uint peer, Action<uint, byte[]> reply, bool wasStored)
            {
                frame = new Frame { Peer = peer, Reply = reply };
                this.wasStored = wasStored;
                muted = SpatialSounds.Mute();
            }
            internal void Finish(Store_PoliceInterceptor store, PoliceTicketData ticket)
            {
                if (completed) return; completed = true;
                try
                {
                    // A later UI failure must not hide a fine that was already committed.
                    if (!wasStored && store.Get_ActiveTickets.Contains(ticket))
                        frame.Reply(frame.Peer, new PoliceFineNotice(Guid.NewGuid(), (PoliceFineKind)ticket.TicketType,
                            ticket.PlayerSpeed, ticket.SpeedLimit, ticket.GetTicketPrice).Encode());
                }
                catch (Exception ex) { Debug.LogWarning("MonMP police fine feedback failed: " + ex.Message); }
                finally { muted.Dispose(); }
            }
        }

        private static readonly List<Frame> frames = new List<Frame>();
        private static readonly HashSet<Guid> displayed = new HashSet<Guid>();
        private static readonly Queue<Guid> displayOrder = new Queue<Guid>();

        internal static IDisposable Begin(uint peer, Action<uint, byte[]> reply)
        {
            if (peer == 0 || reply == null) throw new ArgumentException("A police fine requires its requesting peer and reply route.");
            frames.Add(new Frame { Peer = peer, Reply = reply });
            return new Scope(frames.Count - 1);
        }

        internal static Capture? Before(Store_PoliceInterceptor store, PoliceTicketData ticket)
        {
            if (frames.Count == 0) return null;
            Frame frame = frames[frames.Count - 1];
            return new Capture(frame.Peer, frame.Reply, store.Get_ActiveTickets.Contains(ticket));
        }

        internal static bool Apply(byte[] payload)
        {
            if (!PoliceFineNotice.Is(payload)) return false;
            PoliceFineNotice notice;
            try { notice = PoliceFineNotice.Decode(payload); }
            catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid police fine notice: " + ex.Message); return true; }
            if (!displayed.Add(notice.Id)) return true;
            displayOrder.Enqueue(notice.Id);
            if (displayOrder.Count > 128) displayed.Remove(displayOrder.Dequeue());
            // Match NewTicketCreation's local siren, sender, icon, duration and markup.
            try
            {
                using var local = SpatialSounds.Local();
                var sounds = Singleton<SOUND_Manager>.i;
                // Optional audio/achievement failures must not swallow the ticket banner.
                try { sounds?.PlayOneShot2D(sounds.Police_Siren, _randomPitch: false); }
                catch (Exception ex) { Debug.LogWarning("MonMP police fine audio failed: " + ex.Message); }
                try { Singleton<Steam_AchievementManager>.i?.AchievementDone(AchievementType.FirstPoliceTicket); }
                catch (Exception ex) { Debug.LogWarning("MonMP police fine achievement failed: " + ex.Message); }
                Singleton<UI_EventMessage>.i?.Show(Message(notice), TEXT.GetText("Police_Ticket_SenderName"), sounds?.Police_Siren?.length ?? 4f);
            }
            catch (Exception ex) { Debug.LogWarning("MonMP police fine presentation failed: " + ex.Message); }
            return true;
        }

        internal static string Message(PoliceFineNotice notice)
        {
            var kind = (PoliceTicketData.Ticket_Type)notice.Kind;
            string text = "<mark=#00000088><nobr>";
            text = text + "<b>" + TEXT.Get_PoliceTicketName(kind) + "</b>\t";
            text = kind != PoliceTicketData.Ticket_Type.Speeding ? text + TEXT.Get_PoliceTicketReason(kind) + "\t" :
                text + Mathf.Ceil(notice.PlayerSpeed) + "km/h " + TEXT.Get_PoliceTicketReason(kind) + " " + Mathf.Ceil(notice.SpeedLimit) + "km/h\t";
            text = text + "<u><b>" + notice.Price + "$</b></u>";
            text += "</nobr></mark>\n\n";
            return text + TEXT.GetText("Police_Ticket_Message");
        }
    }
}
