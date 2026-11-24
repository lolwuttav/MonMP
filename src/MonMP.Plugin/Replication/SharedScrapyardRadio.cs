using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // The native save already stores one removed bit per scrapyard radio slot. The host keeps
    // those bits canonical; guests only mirror them and may request a host-created shared item.
    internal sealed class SharedScrapyardRadio : IDisposable
    {
        private const float InteractionRange = 4.5f;
        private const int MaxPending = 16;
        private const int MaxReplayResults = 4096;

        private static readonly FieldInfo SlotField = AccessTools.Field(typeof(Scrapyard_Radio), "Slot");
        private static readonly FieldInfo ScrapyardField = AccessTools.Field(typeof(Scrapyard_Radio), "Scrapyard");
        private sealed class OriginalRadio
        {
            public Scrapyard_Radio Radio = null!;
            public bool Active;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
        }

        private readonly bool host;
        private readonly Gameplay gameplay;
        private readonly Scrapyard scrapyard;
        private readonly Scrapyard_RadioSlot[] slots;
        private readonly SharedItems items;
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, (int Slot, float SentAt)> pending = new Dictionary<uint, (int, float)>();
        private readonly Dictionary<(uint Peer, uint Nonce), bool> replayResults = new Dictionary<(uint, uint), bool>();
        private readonly Queue<(uint Peer, uint Nonce)> replayOrder = new Queue<(uint, uint)>();
        private readonly List<OriginalRadio> originalRadios = new List<OriginalRadio>();
        private readonly HashSet<Scrapyard_Radio> createdPresentation = new HashSet<Scrapyard_Radio>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private ScrapyardRadioState state, original;
        private uint nextNonce;
        private float nextCapture;
        private bool disposed, applying, hasAuthoritativeState;


#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal ScrapyardRadioState TestState => state;


        internal Scrapyard TestScrapyard => scrapyard;


        internal Scrapyard_RadioSlot[] TestSlots => slots;


        internal bool TestRestored => !host && Capture().Equals(original) &&
            originalRadios.All(entry => entry.Radio == null ||
                (entry.Radio.gameObject.activeSelf == entry.Active &&
                 entry.Radio.transform.localPosition == entry.LocalPosition &&
                 Quaternion.Angle(entry.Radio.transform.localRotation, entry.LocalRotation) < 0.01f)) &&
            createdPresentation.All(radio => radio == null || !radio.gameObject.activeSelf);
#endif


        internal SharedScrapyardRadio(Gameplay gameplay, bool host, SharedItems items)
        {
            this.gameplay = gameplay;
            this.host = host;
            this.items = items;
            var scrapyards = Object.FindObjectsOfType<Scrapyard>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid()).ToArray();
            if (scrapyards.Length != 1) throw new InvalidOperationException("Expected exactly one native scrapyard in the active world; found " + scrapyards.Length + ".");
            scrapyard = scrapyards[0];
            slots = scrapyard.Get_RadioSlots;
            if (slots == null || slots.Length < 1 || slots.Length > ScrapyardRadioState.MaxSlots || slots.Any(slot => slot == null || slot.Location == null))
            {
                string detail = slots == null ? "null" : string.Join(";", slots.Select((slot, index) => slot == null
                    ? index + ":null"
                    : index + ":removed=" + slot.isRemoved + ",location=" + (slot.Location == null ? "null" : slot.Location.name)));
                throw new InvalidOperationException("Native scrapyard radio slots do not match the supported bounded layout (count=" +
                    (slots == null ? "null" : slots.Length.ToString()) + ", slots=" + detail + ").");
            }
            if (SlotField?.FieldType != typeof(Scrapyard_RadioSlot) || ScrapyardField?.FieldType != typeof(Scrapyard))
                throw new InvalidOperationException("Unsupported native Scrapyard_Radio field layout.");
            state = original = Capture();
            foreach (var radio in FindRadios())
            {
                originalRadios.Add(new OriginalRadio
                {
                    Radio = radio,
                    Active = radio.gameObject.activeSelf,
                    LocalPosition = radio.transform.localPosition,
                    LocalRotation = radio.transform.localRotation
                });
            }
        }

        internal IEnumerable<WorldRecord> InitialRecords() => new[]
        {
            new WorldRecord(ScrapyardRadioState.Entity, 1, ScrapyardRadioState.Adapter, state.Encode())
        };

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session;
            this.channel = channel;
            session.PoseReceived += OnPose;
            session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        private bool Running => !disposed && session?.IsRunning == true;
        private bool Ready => Running && channel?.FullySynchronized == true;
        private bool IsGuest => Running && !host;

        // Called by the Harmony prefix for both host and guest. The native path is suppressed
        // online so it cannot create an unleased Part_Radio or mutate only one save copy.
        internal bool InterceptPickup(Scrapyard_Radio radio)
        {
            if (applying) return false;
            if (!Running) return true;
            if (radio == null || !TrySlot(radio, out int slot)) return false;
            if (!Ready)
            {
                if (!host) MonMP.Plugin.MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the shared world to finish loading before taking a scrapyard radio.");
                return false;
            }
            if (state.IsRemoved(slot) || !radio.gameObject.activeInHierarchy) return false;
            if (host && gameplay.Player.IsHoldingAnObject) return false;
            if (!host && gameplay.Player.IsHoldingAnObject)
            {
                MonMP.Plugin.MonMPPlugin.Instance?.ReportItemUseUnavailable("Put down the item in your hand before taking a scrapyard radio.");
                return false;
            }
            if (!host && pending.Count >= MaxPending)
            {
                MonMP.Plugin.MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the pending scrapyard radio request to finish.");
                return false;
            }
            uint nonce = NextNonce();
            if (!host) pending[nonce] = (slot, Time.unscaledTime);
            channel!.Request(ScrapyardRadioState.Entity, new ScrapyardRadioCommand(nonce, slot).Encode(), useLease: true);
            return false;
        }

        // Guest simulation is not allowed to consume Unity's local RNG or spawn a radio
        // independently. The guest instead keeps the last host-authored presentation.
        internal bool SuppressGuestNewDay(Scrapyard candidate, int day)
        {
            if (!IsGuest || candidate != scrapyard) return false;
            SyncPresentation();
            return true;
        }

        internal bool SuppressGuestSpawn(Scrapyard candidate)
        {
            if (!IsGuest || candidate != scrapyard) return false;
            SyncPresentation();
            return true;
        }

        internal bool SuppressGuestLoad(Scrapyard candidate)
        {
            if (!IsGuest || candidate != scrapyard) return false;
            SyncPresentation();
            return true;
        }

        internal void NativeNewDayCompleted(Scrapyard candidate)
        {
            if (!host || !Running || candidate != scrapyard) return;
            CaptureAndPublish();
            SyncPresentation();
        }

        // The host keeps the native pickup path so it retains the normal sound and grab
        // animation. Capture its save-backed removed bit immediately after native code runs
        // instead of waiting for the periodic reconciliation tick.
        internal void NativePickupCompleted(Scrapyard_Radio radio)
        {
            if (!host || !Running || !TrySlot(radio, out _)) return;
            CaptureAndPublish();
            SyncPresentation();
        }

        internal void NativeSpawnCompleted(Scrapyard candidate)
        {
            if (candidate != scrapyard || !Running) return;
            if (host) CaptureAndPublish();
            SyncPresentation();
        }

        internal void NativeLoadCompleted(Scrapyard candidate)
        {
            if (candidate != scrapyard || !Running) return;
            if (host) CaptureAndPublish();
            SyncPresentation();
        }

        private uint NextNonce()
        {
            if (++nextNonce == 0) ++nextNonce;
            return nextNonce;
        }

        private ScrapyardRadioState Capture() => new ScrapyardRadioState(slots.Select(slot => slot.isRemoved));

        private void CaptureAndPublish()
        {
            if (!host || channel?.Ready != true) return;
            var captured = ScrapyardRadioPolicy.AdoptNativeSlotState(host, state, Capture());
            if (captured.Equals(state)) return;
            state = captured;
            channel.Publish(ScrapyardRadioState.Entity, state.Encode());
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

        private void OnLeft(uint peer)
        {
            presence.Remove(peer);
            foreach (var key in replayResults.Keys.Where(key => key.Peer == peer).ToArray()) replayResults.Remove(key);
            if (pending.Count != 0 && session != null && peer == session.LocalPeerId) pending.Clear();
        }

        private bool PeerAtSlot(uint peer, int slot)
        {
            if (peer == 0)
            {
                return gameplay != null && gameplay.Get_PlayerState == PlayerState.Walking &&
                    Vector3.Distance(gameplay.PlayerWalking.transform.position, slots[slot].Location.position) <= InteractionRange;
            }
            if (!presence.TryGetValue(peer, out var observed) || Time.unscaledTime - observed.Time > 2f ||
                observed.Pose.State != (byte)PlayerState.Walking) return false;
            var position = new Vector3(observed.Pose.X, observed.Pose.Y, observed.Pose.Z);
            return Vector3.Distance(position, slots[slot].Location.position) <= InteractionRange;
        }

        private bool TrySlot(Scrapyard_Radio radio, out int index)
        {
            index = -1;
            if (radio == null || radio.gameObject == null || radio.gameObject.scene != scrapyard.gameObject.scene ||
                !ReferenceEquals(ScrapyardField.GetValue(radio), scrapyard)) return false;
            var slot = SlotField.GetValue(radio) as Scrapyard_RadioSlot;
            if (slot == null) return false;
            for (int i = 0; i < slots.Length; i++)
                if (ReferenceEquals(slots[i], slot)) { index = i; return true; }
            return false;
        }

        [MonMP.Core.World.WorldCommandAdapters(ScrapyardRadioState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || session?.IsHost != true || entity != ScrapyardRadioState.Entity || !ScrapyardRadioCommand.Is(payload)) return;
            ScrapyardRadioCommand request;
            try { request = ScrapyardRadioCommand.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (request.Slot >= slots.Length) { SendReply(peer, request.Nonce, false); return; }
            var key = (peer, request.Nonce);
            if (replayResults.TryGetValue(key, out bool prior)) { SendReply(peer, request.Nonce, prior); return; }

            bool accepted = false;
            int slotIndex = request.Slot;
            try
            {
                if (peer != 0 && (!session.Peers.ContainsKey(peer) || items.PeerHolding(peer)))
                    accepted = false;
                else if (PeerAtSlot(peer, slotIndex) && !slots[slotIndex].isRemoved &&
                    FindRadios(slotIndex).Any(radio => radio.gameObject.activeInHierarchy) &&
                    (peer != 0 || !gameplay.Player.IsHoldingAnObject))
                {
                    var radios = FindRadios(slotIndex).Where(radio => radio.gameObject.activeInHierarchy).ToArray();
                    var at = radios[0].transform;
                    var asset = Singleton<GameAssets>.i == null ? null : Singleton<GameAssets>.i.Part_Radio;
                    ulong itemId = asset == null ? 0 : items.HostSpawnLeasedItem(asset, at.position, at.rotation, peer);
                    if (itemId != 0)
                    {
                        slots[slotIndex].isRemoved = true;
                        CaptureAndPublish();
                        SyncPresentation();
                        accepted = true;

#if MONMP_DEVELOPMENT
                        TestAccepted++;
#endif

                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                // The replay entry below prevents a retried request from creating a second part
                // if a failure happened after native item registration.
                accepted = slots[slotIndex].isRemoved;
            }
            if (!accepted)
#if MONMP_DEVELOPMENT
TestRejected++;
#else
{}
#endif

            Remember(key, accepted);
            SendReply(peer, request.Nonce, accepted);
        }

        private void Remember((uint Peer, uint Nonce) key, bool accepted)
        {
            replayResults[key] = accepted;
            replayOrder.Enqueue(key);
            while (replayOrder.Count > MaxReplayResults)
            {
                var oldest = replayOrder.Dequeue();
                replayResults.Remove(oldest);
            }
        }

        private void SendReply(uint peer, uint nonce, bool accepted)
        {
            if (channel?.Ready == true && session?.Peers.ContainsKey(peer) == true || peer == 0 && channel?.Ready == true)
                channel!.Reply(peer, ScrapyardRadioState.Entity, new ScrapyardRadioReply(nonce, accepted).Encode());
        }

        private void Rejected(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != ScrapyardRadioState.Entity || !ScrapyardRadioCommand.Is(payload)) return;
            try { SendReply(peer, ScrapyardRadioCommand.Decode(payload).Nonce, false); }
            catch (InvalidDataException) { }
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != ScrapyardRadioState.Entity) return;
            ScrapyardRadioReply reply;
            try { reply = ScrapyardRadioReply.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (!pending.Remove(reply.Nonce)) return;
            if (!reply.Accepted)
                MonMP.Plugin.MonMPPlugin.Instance?.ReportItemUseUnavailable("The host rejected the scrapyard radio request. Check range, your hands and the slot state.");
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (!records.TryGetValue(ScrapyardRadioState.Entity, out var record))
            {
                if (!host && !hasAuthoritativeState)
                    throw new InvalidDataException("Initial scrapyard radio state is missing from the host snapshot.");
                return;
            }
            if (record.Adapter != ScrapyardRadioState.Adapter) throw new InvalidDataException("Scrapyard radio snapshot has the wrong adapter.");
            Apply(ScrapyardRadioState.Decode(record.Payload));
        }

        private void Changed(WorldRecord record)
        {
            if (record.Id != ScrapyardRadioState.Entity || record.Adapter != ScrapyardRadioState.Adapter) return;
            Apply(ScrapyardRadioState.Decode(record.Payload));
        }

        private void Apply(ScrapyardRadioState next)
        {
            if (next.Count != slots.Length) throw new InvalidDataException("Host and local scrapyard radio slot counts differ.");
            state = next;
            if (!host) hasAuthoritativeState = true;
            applying = true;
            try
            {
                if (ScrapyardRadioPolicy.AllowSharedSlotMutation(host))
                    for (int i = 0; i < slots.Length; i++) slots[i].isRemoved = state.IsRemoved(i);
                SyncPresentation();
            }
            finally { applying = false; }
        }

        private Scrapyard_Radio[] FindRadios() => Object.FindObjectsOfType<Scrapyard_Radio>(true)
            .Where(radio => radio != null && radio.gameObject.scene == scrapyard.gameObject.scene &&
                ReferenceEquals(ScrapyardField.GetValue(radio), scrapyard)).ToArray();

        private Scrapyard_Radio[] FindRadios(int slot) => FindRadios()
            .Where(radio => ReferenceEquals(SlotField.GetValue(radio), slots[slot])).ToArray();

        private void SyncPresentation()
        {
            // LoadAndUnlock and NewDay callbacks can run before the initial world snapshot.
            // Do not briefly display radios based on the guest's unrelated native save.
            if (IsGuest && !hasAuthoritativeState) return;
            for (int i = 0; i < slots.Length; i++)
            {
                var radios = FindRadios(i);
                if (state.IsRemoved(i))
                {
                    foreach (var radio in radios) if (radio.gameObject.activeSelf) radio.gameObject.SetActive(false);
                    continue;
                }
                var chosen = radios.FirstOrDefault(radio => radio.gameObject.activeSelf) ?? radios.FirstOrDefault();
                if (chosen == null)
                {
                    var asset = Singleton<GameAssets>.i == null ? null : Singleton<GameAssets>.i.Scrapyard_Radio;
                    if (asset == null) continue;
                    var go = Object.Instantiate(asset, slots[i].Location);
                    chosen = go.GetComponent<Scrapyard_Radio>();
                    if (chosen == null) { Object.Destroy(go); continue; }
                    chosen.transform.localPosition = Vector3.zero;
                    chosen.transform.localRotation = Quaternion.identity;
                    chosen.Initialized(slots[i], scrapyard);
                    createdPresentation.Add(chosen);
                }
                chosen.gameObject.SetActive(true);
                for (int n = 0; n < radios.Length; n++)
                    if (radios[n] != chosen) radios[n].gameObject.SetActive(false);
            }
        }

        public void Update()
        {
            if (!Running) return;
            if (!host)
            {
                if (pending.Count > 0) foreach (var request in pending.Where(pair => Time.unscaledTime - pair.Value.SentAt > 8f).Select(pair => pair.Key).ToArray())
                {
                    pending.Remove(request);
                    MonMP.Plugin.MonMPPlugin.Instance?.ReportItemUseUnavailable("The scrapyard radio request timed out; check the slot before trying again.");
                }
            }
            if (host && Time.unscaledTime >= nextCapture)
            {
                nextCapture = Time.unscaledTime + 0.5f;
                CaptureAndPublish();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot;
                channel.Changed -= Changed;
                channel.CommandRequested -= Command;
                channel.CommandRejected -= Rejected;
                channel.ReplyReceived -= Reply;
            }
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (!host)
            {
                applying = true;
                try
                {
                    foreach (var entry in originalRadios)
                    {
                        if (entry.Radio == null) continue;
                        entry.Radio.transform.localPosition = entry.LocalPosition;
                        entry.Radio.transform.localRotation = entry.LocalRotation;
                        entry.Radio.gameObject.SetActive(entry.Active);
                    }
                    foreach (var radio in createdPresentation)
                        if (radio != null) { radio.gameObject.SetActive(false); Object.Destroy(radio.gameObject); }
                }
                finally { applying = false; }
            }
            pending.Clear(); replayResults.Clear(); replayOrder.Clear(); presence.Clear();
            session = null; channel = null;
        }
    }
}
