using System;
using System.Collections.Generic;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Gilles' lost-item search: the item he is looking for lived only in the requesting peer's Store_Gilles,
    // so a guest's request moved nothing but its own replica at 18:00 (which the host then put back), and
    // the host never knew a search was running. The host owns the search: guests send the shared item they
    // picked, the host starts the native search, reclaims the item from a guest's hand when Gilles returns
    // it, and publishes its new position. Every peer sees whether a search is running.
    internal sealed class SharedGilles : IDisposable
    {
        public const ushort Adapter = 105;
        private const int ReturnTick = 1080;
        private static readonly ulong Id = StaticId.Hash("gilles-search-v1");
        private readonly bool host;
        private readonly SharedItems items;
        private readonly Store_Gilles? store;
        private NetworkSession? session;
        private WorldChannel? channel;
        private ulong returning;
        private float nextCapture;

        public SharedGilles(bool host, SharedItems items)
        {
            this.host = host; this.items = items;
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<Store_Gilles>(true))
                if (candidate.gameObject.scene.IsValid()) { store = candidate; break; }
        }

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            if (store != null) yield return new WorldRecord(Id, 1, Adapter, new byte[1]);
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            if (store == null) return;
            this.session = session; this.channel = channel;
            channel.CommandRequested += Command;
        }

        private bool Running => session?.IsRunning == true && channel?.Ready == true;
        private bool Waiting => host ? store?.Get_ItemOnWait != null
            : channel != null && channel.Records.TryGetValue(Id, out var record) && record.Payload.Length == 1 && record.Payload[0] == 1;

        // Guest: only a correlated host-authorized native talk/choice opens this
        // local search window. Its confirmation goes to the host.
        internal bool GuestOpenWindow()
        {
            if (host || store == null || !Running) return false;
            if (!Waiting) UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>()?.OpenGilles();
            return true;
        }

        // Guest: true when the search request was sent to the host instead of running on the local replica.
        internal bool Intercept(UI_GenericWindow_Gilles window, InteractableItems? selected)
        {
            if (host || store == null || !Running || selected == null) return false;
            ulong item = items.EntityOf(selected);
            if (item == 0) return false;
            if (!Waiting)
            {
                if (!store.isOpen) Singleton<UI_ErrorMessage>.i?.Show(TEXT.DIALOG_Gilles_Closed);
                else
                {
                    channel!.Request(Id, BitConverter.GetBytes(item));
                    Singleton<UI_EventMessage>.i?.Show(TEXT.DIALOG_Gilles_StartSearch, "Gilles", 8f, UI_EventMessage.EventIconType.Talk);
                }
            }
            window.Button_Cancel();
            return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedGilles.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || id != Id || store == null || payload.Length != 8) return;
            if (store.Get_ItemOnWait != null || !store.isOpen) return;
            var item = items.ItemOf(BitConverter.ToUInt64(payload, 0));
            if (item == null) return;
            store.Get_ItemOnWait = item;
            Publish();
        }

        // Host, before Gilles' 18:00 tick: a guest may be holding the item he returns; the native tick only
        // cancels the host's own hold.
        internal void BeforeTick(int tick)
        {
            returning = 0;
            if (!host || store == null || tick != ReturnTick || !(store.Get_ItemOnWait is { } item) || !Running) return;
            if (items.HeldByRemotePlayer(item)) items.HostReclaim(item);
            returning = items.EntityOf(item);
        }

        internal void AfterTick()
        {
            if (returning == 0) return;
            items.PublishHostItem(returning);
            returning = 0;
            Publish();
        }

        private void Publish()
        {
            if (Running) channel!.Publish(Id, new[] { (byte)(store?.Get_ItemOnWait != null ? 1 : 0) });
        }

        public void Update()
        {
            if (!host || store == null || !Running || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 1f;
            Publish();
        }

        public void Dispose()
        {
            if (channel != null) channel.CommandRequested -= Command;
            session = null; channel = null;
        }
    }
}
