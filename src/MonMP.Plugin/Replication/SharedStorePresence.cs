using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Guests never run StoreManagers.Tick, so a store's open/closed presentation (staff, signs,
    // parked cars, dealer planters) and the bar's nightly regulars never changed on them. The host
    // publishes which stores are open and which scheduled actors are present; guests apply only the
    // presentation half: the base EnableWhenStoreIsOpen/Close objects, plus the native lifecycle for
    // store types whose open/close is pure presentation.
    internal sealed class SharedStorePresence
    {
        public static readonly ulong Entity = StaticId.Hash("shared-store-presence/v1");
        private static readonly HashSet<Type> NativeLifecycle = new HashSet<Type>
        {
            typeof(Stores), typeof(HardwareStore), typeof(TwentyFourSevenStore), typeof(Store_FarmKiosque),
            typeof(Store_Gilles), typeof(Store_Dealer), typeof(Store_Events), typeof(Store_PizzaSnack), typeof(BrotherHouse), typeof(Store_Sawmill),
            // The speedway events: registration and the races themselves are each player's own, so
            // each guest runs the native open/close (the inscription form, its own championship).
            typeof(Event_DerbyDemolition), typeof(Event_Championship),
            // Roger's parts store swaps its abandoned storefront for the open one only in its own OpenStore (#172).
            typeof(Store_Roger),
        };
        // Events and stores whose lifecycle is replicated by their own systems keep their state alone.
        private static readonly HashSet<Type> Skipped = new HashSet<Type> { typeof(Event_PizzaSnack) };
        // The night meet (Store_MeetManager): its native open raises the cars and drives the meet's
        // radio, which on a guest would turn into radio requests to the host. Guests present it
        // themselves: the cars rise and fall as natively and each guest's own races and bets run
        // natively against them. Raymond's car shows when the host's does.
        private static readonly FieldInfo? MeetOpenAt = AccessTools.Field(typeof(Store_MeetManager), "PositionWhileOpen");
        private static readonly FieldInfo? MeetClosedAt = AccessTools.Field(typeof(Store_MeetManager), "PositionWhileClose");
        private static readonly FieldInfo? MeetRaised = AccessTools.Field(typeof(Store_MeetManager), "RaiseCarDone");
        private static readonly FieldInfo? MeetReady = AccessTools.Field(typeof(Store_MeetManager), "StoreInitialized");
        private static readonly FieldInfo? MeetBets = AccessTools.Field(typeof(Store_MeetManager), "BetIsDoneToday");
        private static readonly FieldInfo? MeetRaymond = AccessTools.Field(typeof(Store_MeetManager), "RaymondCar");
        internal static readonly FieldInfo? FederationBarrel = AccessTools.Field(typeof(Store_FederationSyrup), "Reception_Barrel");
        internal static readonly FieldInfo? FederationGrey = AccessTools.Field(typeof(Store_FederationSyrup), "Reception_GrayedBarrelInfo");
        internal static readonly FieldInfo? FederationBedTank = AccessTools.Field(typeof(Store_FederationSyrup), "Reception_BedTank");
        private byte[]? retry;
        private bool[]? actorPresence;
        private float nextRetry;

#if MONMP_DEVELOPMENT
        internal int TestMeetPresented { get; private set; }
#endif

        private readonly bool host;
        private readonly Stores[] stores;
        private readonly GameObject[] actors;
        private readonly uint layout;
        private readonly Dictionary<Stores, bool> presented = new Dictionary<Stores, bool>();
        private byte[] last = Array.Empty<byte>();
        private float nextCapture;
        private bool applying;
        internal bool Applying => applying;

        public SharedStorePresence(bool host)
        {
            this.host = host;
            stores = UnityEngine.Object.FindObjectsOfType<Stores>(true)
                .Where(s => s.gameObject.scene.IsValid() && !Skipped.Contains(s.GetType()))
                .OrderBy(s => Path(s.transform), StringComparer.Ordinal).ToArray();
            var bar = stores.OfType<Store_Bar>().FirstOrDefault();
            var meet = stores.OfType<Store_MeetManager>().FirstOrDefault();
            actors = (bar == null ? Array.Empty<GameObject>() :
                new[] { "Actor_Roger", "Actor_Eric", "Actor_Chef", "Actor_Jacques", "Actor_Melanie" }
                    .Select(name => (AccessTools.Field(typeof(Store_Bar), name)?.GetValue(bar) as Component)?.gameObject)
                    .Where(go => go != null).Select(go => go!))
                .Concat(meet != null && MeetRaymond?.GetValue(meet) is GameObject raymond && raymond != null ? new[] { raymond } : Array.Empty<GameObject>())
                // Loaded worlds can retain an actor independently of the store's
                // flag. Mirror Francis's actual presence, not a guessed weekday.
                .Concat(stores.OfType<BrotherHouse>().SelectMany(store => store.EnableWhenStoreIsOpen)
                    .Where(go => go != null && go.GetComponent<Actor_Francis>() != null))
                .Distinct().ToArray();
            layout = (uint)StaticId.Hash(string.Join("|", stores.Select(s => Path(s.transform))) + "#" + actors.Length);
            // Native isOpen does not establish completed guest presentation. Cache a store only
            // after Present succeeds, including when the first host state must wait for native Start.
        }

        // Same-name ordinals: a played save reorders scene children relative to the fresh world a
        // menu-joining guest registers from, so raw sibling indices would mismatch the layout.
        private static string Path(Transform t) => SharedBuildingDoors.StablePath(t);

        internal byte[] Capture()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(layout);
            WriteBits(writer, stores.Select(s => s != null && s.isStoreOpen()).ToArray());
            WriteBits(writer, actors.Select(a => a != null && a.activeSelf).ToArray());
            return stream.ToArray();
        }

        private static void WriteBits(BinaryWriter writer, bool[] bits)
        {
            writer.Write((byte)Math.Min(bits.Length, byte.MaxValue));
            for (int i = 0; i < bits.Length && i < 255; i += 8)
            {
                byte b = 0;
                for (int j = 0; j < 8 && i + j < bits.Length; j++) if (bits[i + j]) b |= (byte)(1 << j);
                writer.Write(b);
            }
        }

        private static bool[] ReadBits(BinaryReader reader)
        {
            int count = reader.ReadByte();
            var bits = new bool[count];
            for (int i = 0; i < count; i += 8)
            {
                byte b = reader.ReadByte();
                for (int j = 0; j < 8 && i + j < count; j++) bits[i + j] = (b & (1 << j)) != 0;
            }
            return bits;
        }

        // Host: the latest payload when it changed since the last publish, else null.
        internal byte[]? HostPoll(float now)
        {
            if (!host || now < nextCapture) return null;
            nextCapture = now + 1f;
            var next = Capture();
            if (next.Length > 120 || next.SequenceEqual(last)) return null;
            last = next;
            Debug.Log("MonMP store presence published: open=" + string.Join(",", stores.Where(st => st != null && st.isStoreOpen()).Select(st => st.name)));
            return next;
        }

        internal void Apply(byte[] payload)
        {
            if (host || payload == null || payload.Length < 6) return;
            bool[] open, present;
            using (var reader = new BinaryReader(new MemoryStream(payload, false)))
            {
                if (reader.ReadUInt32() != layout) { Debug.LogWarning("MonMP store presence layout differs from the host; skipped."); return; }
                open = ReadBits(reader); present = ReadBits(reader);
            }
            if (open.Length != stores.Length || present.Length != actors.Length)
            { Debug.LogWarning("MonMP store presence shape differs: stores " + open.Length + "/" + stores.Length + ", actors " + present.Length + "/" + actors.Length); return; }
            actorPresence = present;
            // A newer accepted state supersedes the whole deferred payload, including actors and
            // unrelated stores. Requeue only stores that still cannot present this latest state.
            retry = null;
            var changed = new List<string>();
            applying = true;
            try
            {
                for (int i = 0; i < stores.Length; i++)
                {
                    var store = stores[i];
                    if (store == null || (presented.TryGetValue(store, out bool previous) && previous == open[i])) continue;
                    try
                    {
                        // Not presentable yet (the meet mid-race): left unpresented and retried.
                        if (!Present(store, open[i])) { retry = payload; continue; }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("MonMP store presence failed for " + store.name + ": " + ex.Message);
                        retry = payload;
                        continue;
                    }
                    presented[store] = open[i];
                    changed.Add(store.name + "=" + (open[i] ? "open" : "closed"));
                }
                PresentActors();
            }
            finally { applying = false; }
            if (changed.Count > 0) Debug.Log("MonMP store presence applied: " + string.Join(",", changed));
        }

        private void PresentActors()
        {
            if (actorPresence == null) return;
            bool previous = applying;
            applying = true;
            try
            {
                for (int i = 0; i < actors.Length; i++)
                    if (actors[i] != null && actors[i].activeSelf != actorPresence[i]) actors[i].SetActive(actorPresence[i]);
            }
            finally { applying = previous; }
        }

        // Retry deferred stores and repair late native load/Start activation.
        // Only known actor bits are checked; unchanged stores never rerun their
        // lifecycle, which can clear firewood, race state or other world objects.
        internal void GuestRetry()
        {
            if (host || Time.unscaledTime < nextRetry) return;
            nextRetry = Time.unscaledTime + 1f;
            var payload = retry; retry = null;
            if (payload != null) Apply(payload);
            else PresentActors();
        }

        private bool Present(Stores store, bool open)
        {
            if (store is Store_MeetManager meet) return PresentMeet(meet, open);
            if (NativeLifecycle.Contains(store.GetType()))
            {
                if (open) store.OpenStore(); else store.CloseStore();
                return true;
            }
            Toggle(store, open);
            if (open && store is Store_Bar bar) bar.UpdateRadio();
            // The federation's green circle is switched in its OpenStore, which also spawns the returned empty
            // barrels (host-owned items). The guest's own Start closed it grey on its local friendship.
            if (open && store is Store_FederationSyrup federation)
            {
                (FederationBarrel?.GetValue(federation) as GameObject)?.SetActive(true);
                (FederationGrey?.GetValue(federation) as GameObject)?.SetActive(false);
                if (Singleton<ToDoList_Manager>.i?.isDone(ToDoList_Line.SugarShack_BedTankOutdoorPump) == true)
                    (FederationBedTank?.GetValue(federation) as GameObject)?.SetActive(true);
            }
            return true;
        }

        private static void Toggle(Stores store, bool open)
        {
            store.isOpen = open;
            foreach (var go in store.EnableWhenStoreIsOpen) if (go != null) go.SetActive(open);
            foreach (var go in store.EnableWhenStoreIsClose) if (go != null) go.SetActive(!open);
        }

        private bool PresentMeet(Store_MeetManager meet, bool open)
        {
            // Its Start lowers the cars a few frames in; and like the native close, never mid-race.
            if (MeetReady?.GetValue(meet) is not true || MonMPPlugin.Instance == null) return false;
            var race = meet.Get_RaceManager;
            if (!open && race != null && race.Get_IsRacing) return false;

#if MONMP_DEVELOPMENT
            TestMeetPresented++;
#endif

            meet.isOpen = open;
            MeetRaised?.SetValue(meet, false);
            if (open)
            {
                Toggle(meet, true);
                if (MeetBets?.GetValue(meet) is bool[] bets) Array.Clear(bets, 0, bets.Length);
                race?.Open_Store();
            }
            else race?.Close_Store_Started();
            MonMPPlugin.Instance.StartCoroutine(MoveMeet(meet, open));
            return true;
        }

        private static System.Collections.IEnumerator MoveMeet(Store_MeetManager meet, bool open)
        {
            var from = meet.transform.position;
            var to = (Vector3)((open ? MeetOpenAt : MeetClosedAt)?.GetValue(meet) ?? from);
            for (float timer = 0; timer < 5f; timer += Time.fixedDeltaTime)
            {
                if (meet == null || meet.isOpen != open) yield break;
                meet.transform.position = Vector3.Lerp(from, to, timer / 5f);
                yield return new WaitForFixedUpdate();
            }
            if (meet == null || meet.isOpen != open) yield break;
            meet.transform.position = to;
            var race = meet.Get_RaceManager;
            if (open) { race?.Open_Store_Completed(); MeetRaised?.SetValue(meet, true); }
            else { race?.Close_Store_Completed(); Toggle(meet, false); }
        }
    }
}
