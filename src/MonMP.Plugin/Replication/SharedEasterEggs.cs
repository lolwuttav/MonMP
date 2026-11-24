using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Easter eggs (#291). Natively, grabbing an egg adds one to the Easter basket's count, shows the
    // Santa Goat progress message and destroys the egg. On a guest that would change only its copies,
    // so a guest's grab asks the host instead. The host checks the egg is loose and within reach,
    // counts it on the shared basket and removes the egg (both reach everyone through SharedItems).
    // The finder hears and sees the native progress feedback.
    internal sealed class SharedEasterEggs : IDisposable
    {
        public const ushort Adapter = 136;
        internal static readonly ulong Entity = StaticId.Hash("easter-eggs/v1");
        private const byte Pickup = 1;
        private const float Reach = 4f;

        private readonly SharedItems items;
        private readonly Dictionary<ulong, float> pending = new Dictionary<ulong, float>();
        private readonly ConditionalWeakTable<Item_Event_EasterEgg, object> consumed = new ConditionalWeakTable<Item_Event_EasterEgg, object>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed;


#if MONMP_DEVELOPMENT
        internal int TestPickups { get; private set; }
#endif


        internal SharedEasterEggs(SharedItems items) { this.items = items; }

        internal static IEnumerable<WorldRecord> InitialRecords() => new[] { new WorldRecord(Entity, 1, Adapter, new byte[] { 1 }) };

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            world.CommandRequested += Command; world.ReplyReceived += Reply;
        }

        private bool host => session?.IsHost == true;
        private bool Attached => !disposed && channel?.Ready == true && session?.IsRunning == true;
        internal bool AlreadyConsumed(Item_Event_EasterEgg egg) => consumed.TryGetValue(egg, out _);

        // Shared grabs are consumed synchronously before native deferred destruction. A host
        // uses the same path locally so its grab cannot race a guest in this update.
        internal bool RequestPickup(Item_Event_EasterEgg egg)
        {
            if (!Attached) return false;
            ulong id = items.EntityOf(egg);
            if (id == 0) return false;
            if (host)
            {
                if (!items.FreeOrOwnedBy(id, 0) || !PickupEgg(id, out int count))
                    MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the shared Easter egg pickup to become available.");
                else if (count > 0) Feedback(count);
                return true;
            }
            if (pending.TryGetValue(id, out float at) && Time.unscaledTime - at < 2f) return true;
            pending[id] = Time.unscaledTime;
            var payload = new byte[9];
            payload[0] = Pickup; BitConverter.GetBytes(id).CopyTo(payload, 1);
            channel!.Request(Entity, payload, useLease: true);
            return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedEasterEggs.Adapter)]

        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != Entity || peer == 0 || payload.Length != 9 || payload[0] != Pickup) return;
            ulong id = BitConverter.ToUInt64(payload, 1);
            int count = -1;
            bool accepted = items.ItemOf(id) is Item_Event_EasterEgg && items.HostItemInRange(peer, id, Reach) &&
                items.FreeOrOwnedBy(id, peer) && PickupEgg(id, out count);
            if (accepted)
            {
                Debug.Log("MonMP Easter egg " + id.ToString("X16") + " found by peer " + peer + " (basket " + count + "/6)");
            }
            var reply = new byte[14];
            reply[0] = Pickup; BitConverter.GetBytes(id).CopyTo(reply, 1); reply[9] = (byte)(accepted ? 1 : 0);
            BitConverter.GetBytes(count).CopyTo(reply, 10);
            channel!.Reply(peer, Entity, reply);
        }

        private bool PickupEgg(ulong id, out int count)
        {
            count = -1;
            var basket = UnityEngine.Object.FindObjectOfType<Item_Event_EasterBasket>();
            if (items.ItemOf(id) is not Item_Event_EasterEgg egg || !items.HostConsumeEasterEgg(id)) return false;
            consumed.GetValue(egg, _ => new object());
            if (basket != null)
            {
                basket.IntValue1++;
                count = basket.IntValue1;
                ulong basketId = items.EntityOf(basket);
                if (basketId != 0) items.HostPublishItem(basketId);
            }

#if MONMP_DEVELOPMENT
            TestPickups++;
#endif

            return true;
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != Entity || payload.Length != 14 || payload[0] != Pickup) return;
            pending.Remove(BitConverter.ToUInt64(payload, 1));
            if (payload[9] != 1) return;
            int count = BitConverter.ToInt32(payload, 10);
            if (count > 0) Feedback(count);
        }

        // As Item_Event_EasterEgg.WaitOneFrame.
        private static void Feedback(int count)
        {
            string text = count switch
            {
                2 => "Oh! :3",
                3 => "Mid-point! :D",
                4 => "You good! :)",
                5 => "Almost there! ^^",
                6 => "Well done\nDang, you really are a PRO GAMER!",
                _ => "Adorable! =)"
            };
            if (count == 6) Singleton<SOUND_Manager>.i.PlaySound_BuggyBuildCompleted();
            Singleton<SOUND_Manager>.i.Play_Event_HappyPopSuccess();
            Singleton<UI_EventMessage>.i.Show(new Color(1f, 0.51f, 0.75f), "[" + count + "/6] " + text, "Santa Goat", 9f, UI_EventMessage.EventIconType.Heart);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null) { channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            session = null; channel = null;
        }
    }
}
