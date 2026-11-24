using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // The container's native sale runs once on the host. Its shared item tombstones and the
    // economy ledger carry the actual effects to guests; this record routes sale requests.
    internal sealed class SharedMetalSales : IDisposable
    {
        internal const ushort Adapter = 33;
        private const byte Marker = 0xD9;
        private static readonly FieldInfo ContainerField = AccessTools.Field(typeof(Scrapyard_Container_SellMetal_Affiche), "scrapyard_Container_SellMetal");
        private static readonly FieldInfo RadioField = AccessTools.Field(typeof(Scrapyard_Container_SellMetal), "RadioAmount");
        private sealed class Binding
        {
            public ulong Id;
            public Scrapyard_Container_SellMetal Container = null!;
            public Scrapyard_Container_SellMetal_Affiche Sign = null!;
            public readonly List<Scrapyard_Container_SellMetal_Affiche> Signs = new List<Scrapyard_Container_SellMetal_Affiche>();
        }
        private readonly bool host;
        private readonly SharedItems items;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Scrapyard_Container_SellMetal, Binding> byContainer = new Dictionary<Scrapyard_Container_SellMetal, Binding>();
        private readonly PurchaseClaims claims = new PurchaseClaims();
        private readonly HashSet<ulong> claimedItems = new HashSet<ulong>();
        private readonly HashSet<ulong> pendingRemovals = new HashSet<ulong>();
        private readonly Dictionary<(Guid Profile, Guid Operation), (ulong Entity, SaleReply Reply)> results = new Dictionary<(Guid, Guid), (ulong, SaleReply)>();
        private readonly Dictionary<Guid, (ulong Entity, float Deadline)> pending = new Dictionary<Guid, (ulong, float)>();
        private readonly Dictionary<Guid, ulong> timedOut = new Dictionary<Guid, ulong>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool executing, disposed, uncertain;
        public bool NewSalesBlocked { get; set; }
        public bool TransactionsSettled => pending.Count == 0 && timedOut.Count == 0 && pendingRemovals.Count == 0 && !executing && !uncertain;

#if MONMP_DEVELOPMENT
        internal int TestNativeSales { get; private set; }


        internal int TestDuplicateReplays { get; private set; }


        internal int TestRangeRejections { get; private set; }


        internal int TestUnsafeRejections { get; private set; }


        internal SaleReply? TestLastReply { get; private set; }


        private Binding Fixture => bindings.Values.OrderBy(b => b.Id).First();


        internal Scrapyard_Container_SellMetal? TestContainer => bindings.Count == 0 ? null : Fixture.Container;


        internal Scrapyard_Container_SellMetal_Affiche? TestSign => bindings.Count == 0 ? null : Fixture.Sign;


        internal ulong TestEntity => bindings.Count == 0 ? 0 : Fixture.Id;
#endif


        public SharedMetalSales(bool host, SharedItems items, string diagnostics)
        {
            this.host = host; this.items = items;
            var lines = new List<string> { "MONMP_METAL_SALE_REGISTERED", "entity\tsign\tcontainer\tactive" };
            foreach (var sign in Object.FindObjectsOfType<Scrapyard_Container_SellMetal_Affiche>(true)
                .Where(s => s.gameObject.scene.IsValid()).OrderBy(s => RuntimeProbe.CanonicalPath(s.transform), StringComparer.Ordinal))
            {
                var container = ContainerField.GetValue(sign) as Scrapyard_Container_SellMetal;
                if (container == null || !container.gameObject.scene.IsValid())
                    throw new InvalidOperationException("A scrapyard metal-sale sign has no native container.");
                string path = RuntimeProbe.CanonicalPath(container.transform);
                ulong id = StaticId.Hash(path + "/metal-sale-v1");
                if (byContainer.TryGetValue(container, out var existing))
                {
                    existing.Signs.Add(sign);
                    lines.Add(id.ToString("X16") + "\t" + RuntimeProbe.CanonicalPath(sign.transform) + "\t" + path + "\t" + sign.gameObject.activeInHierarchy);
                    continue;
                }
                if (bindings.ContainsKey(id)) throw new InvalidOperationException("Ambiguous native scrapyard sale container: " + path);
                var binding = new Binding { Id = id, Container = container, Sign = sign };
                binding.Signs.Add(sign);
                bindings.Add(id, binding); byContainer.Add(container, binding);
                lines.Add(id.ToString("X16") + "\t" + RuntimeProbe.CanonicalPath(sign.transform) + "\t" + path + "\t" + sign.gameObject.activeInHierarchy);
            }
            if (bindings.Count == 0) throw new InvalidOperationException("No native scrapyard metal-sale sign was found.");

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-metal-sales.txt"), lines.Take(2).Concat(lines.Skip(2).OrderBy(s => s, StringComparer.Ordinal)));
#endif

        }

        public IEnumerable<WorldRecord> InitialRecords()
        {
            foreach (var binding in bindings.Values) yield return new WorldRecord(binding.Id, 1, Adapter, new byte[] { 1 });
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.CommandRequested += Command; channel.ReplyReceived += Reply;
        }

        // True suppresses native execution. The host may execute a checked local sale normally.
        internal bool Intercept(Scrapyard_Container_SellMetal clicked)
        {
            if (disposed || session?.IsRunning != true || executing) return false;
            if (clicked == null || !byContainer.TryGetValue(clicked, out var binding) || channel?.FullySynchronized != true)
            { Report("The metal sale is unavailable until the shared world is ready."); return true; }
            if (NewSalesBlocked)
            { Report("Wait for the multiplayer save to finish before selling metal."); return true; }
            if (host)
            {
                if (!InRange(0, binding) || !SafeContents(binding))
                { Report("Move closer and place shared, loose metal in the container before selling."); return true; }
                ClaimContents(binding);

#if MONMP_DEVELOPMENT
                TestNativeSales++;
#endif

                return false;
            }
            if (pending.Count + timedOut.Count >= 8)
            { Report("Wait for the pending metal sale to finish."); return true; }
            Send(binding.Id, Guid.NewGuid());
            return true;
        }


#if MONMP_DEVELOPMENT
        internal void TestRequest(Guid operation) => Send(TestEntity, operation);
#endif


        private void Send(ulong id, Guid operation)
        {
            if (host || channel == null || operation == Guid.Empty || !bindings.ContainsKey(id)) return;
            if (!pending.ContainsKey(operation) && !timedOut.ContainsKey(operation)) pending.Add(operation, (id, Time.unscaledTime + 10));
            channel.Request(id, new SaleRequest(operation).Encode(), true);
        }

        private bool SafeContents(Binding binding)
        {
            binding.Container.Detection();
            var detected = binding.Container.Metal_Pieces;
            if (detected.Length == 0 || float.IsNaN(binding.Container.Metal_Weight) ||
                float.IsInfinity(binding.Container.Metal_Weight) || binding.Container.Metal_Weight <= 0) return false;
            var ids = new HashSet<ulong>();
            foreach (var item in detected)
            {
                if (item == null || !item.gameObject.activeInHierarchy || !item.CanBeSoldForMetal) return false;
                ulong id = items.EntityOf(item);
                // Native Detection scans colliders. A multi-collider item would otherwise pay twice.
                if (id == 0 || !ids.Add(id) || claimedItems.Contains(id) || !items.Loose(id)) return false;
            }
            return true;
        }

        private void ClaimContents(Binding binding)
        {
            foreach (var item in binding.Container.Metal_Pieces)
            {
                ulong id = items.EntityOf(item);
                claimedItems.Add(id); pendingRemovals.Add(id);
            }
        }

        private bool InRange(uint peer, Binding binding) => binding.Signs.Any(s =>
            s != null && s.gameObject.activeInHierarchy && items.PurchaseInRange(peer, s.transform.position));

        [MonMP.Core.World.WorldCommandAdapters(SharedMetalSales.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var binding) || !SaleRequest.Is(payload) || session == null || channel == null) return;
            var request = SaleRequest.Decode(payload);
            if (!session.Peers.TryGetValue(peer, out var buyer)) return;
            var key = (buyer.ProfileId, request.Operation);
            if (!claims.Claim(key.ProfileId, key.Operation))
            {
                if (results.TryGetValue(key, out var prior))
                {
#if MONMP_DEVELOPMENT
TestDuplicateReplays++;
#endif
 channel.Reply(peer, id, (prior.Entity == id ? prior.Reply :
                    new SaleReply(request.Operation, PurchaseStatus.RetryRejected)).Encode()); }
                else channel.Reply(peer, id, new SaleReply(request.Operation, PurchaseStatus.Capacity).Encode());
                return;
            }
            void Finish(PurchaseStatus status, int count = 0, float weight = 0, int radios = 0)
            {
                var reply = new SaleReply(request.Operation, status, count, weight, radios);
                results[key] = (id, reply);
#if MONMP_DEVELOPMENT
TestLastReply = reply;
#endif

                if (status == PurchaseStatus.Uncertain) uncertain = true;
                if (session.Peers.TryGetValue(peer, out var connected) && connected.ProfileId == key.ProfileId)
                    channel.Reply(peer, id, reply.Encode());
            }
            if (NewSalesBlocked || executing) { Finish(PurchaseStatus.NativeRejected); return; }
            if (!binding.Signs.Any(s => s != null && s.gameObject.activeInHierarchy))
            { Finish(PurchaseStatus.Unavailable); return; }
            if (!InRange(peer, binding))
            {
#if MONMP_DEVELOPMENT
TestRangeRejections++;
#endif
 Finish(PurchaseStatus.TooFar); return; }
            if (!SafeContents(binding))
            {
#if MONMP_DEVELOPMENT
TestUnsafeRejections++;
#endif
 Finish(PurchaseStatus.NativeRejected); return; }
            int count = binding.Container.Metal_Amount;
            float weight = binding.Container.Metal_Weight;
            int radios = (int)RadioField.GetValue(binding.Container);
            int cashBefore = Object.FindObjectOfType<Cash_Manager>().GetCash;
            // Destroy is deferred in Unity. Claim before native execution so another operation
            // pumped this frame cannot sell the same collider again.
            ClaimContents(binding);
            executing = true;
            try
            {
                // The seller's sale popup comes from its reply.
                using (Patches.RemoteActionScope.Enter()) binding.Container.Button_SellMetal();
                int cashAfter = Object.FindObjectOfType<Cash_Manager>().GetCash;
                if (cashAfter == cashBefore + (int)(weight * 2f) * 100)
                {
#if MONMP_DEVELOPMENT
TestNativeSales++;
#endif
 Finish(PurchaseStatus.Placed, count, weight, radios); }
                else Finish(PurchaseStatus.Uncertain);
            }
            catch (Exception ex) { Finish(PurchaseStatus.Uncertain); Debug.LogException(ex); }
            finally { executing = false; }
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (host || !bindings.ContainsKey(id) || !SaleReply.Is(payload)) return;
            var reply = SaleReply.Decode(payload);
            if (pending.TryGetValue(reply.Operation, out var request))
            { if (request.Entity != id) return; pending.Remove(reply.Operation); }
            else
            {
                if (!timedOut.TryGetValue(reply.Operation, out ulong expiredEntity) || expiredEntity != id) return;
                timedOut.Remove(reply.Operation);
            }

#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            if (reply.Status == PurchaseStatus.Uncertain) uncertain = true;
            if (reply.Status == PurchaseStatus.Placed)
            {
                GuestAchievements.Award(AchievementType.SellScrapMetal);
                string radio = reply.Radios > 0 ? "\n<size=80%><color=#FFB0B0><i>" + TEXT.GetText("EVENT_Scrapyard_RadioSold") + "</i></color></size>" : "";
                Singleton<UI_EventMessage>.i.Show(reply.Count + " " + TEXT.GetText("EVENT_Scrapyard_PartsSold") + "\n" + reply.Weight + "KG" + radio,
                    TEXT.GetText("EVENT_Scrapyard_MetalRecyclingTitle"), 8f);
            }
            else Report(reply.Status == PurchaseStatus.TooFar ? "Move closer to the metal-sale sign." :
                reply.Status == PurchaseStatus.Uncertain ? "Metal sale outcome is uncertain. Check shared cash and the container." :
                "The host declined this metal sale. Check that the container holds loose shared metal.");
        }

        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);
        public void Update()
        {
            if (host && pendingRemovals.Count > 0) foreach (ulong id in pendingRemovals.Where(items.Removed).ToArray()) pendingRemovals.Remove(id);
            if (pending.Count > 0) foreach (var p in pending.Where(p => Time.unscaledTime >= p.Value.Deadline).ToArray())
            { pending.Remove(p.Key); timedOut.Add(p.Key, p.Value.Entity); Report("Metal sale confirmation is delayed. Check shared cash and the container."); }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (channel != null) { channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            pending.Clear(); timedOut.Clear(); session = null; channel = null;
        }

        internal readonly struct SaleRequest
        {
            public readonly Guid Operation;
            public SaleRequest(Guid operation) { if (operation == Guid.Empty) throw new InvalidDataException("Invalid sale operation."); Operation = operation; }
            public static bool Is(byte[] payload) => payload != null && payload.Length == 17 && payload[0] == Marker;
            public byte[] Encode() => new[] { Marker }.Concat(Operation.ToByteArray()).ToArray();
            public static SaleRequest Decode(byte[] payload) => Is(payload) ? new SaleRequest(new Guid(payload.Skip(1).Take(16).ToArray())) : throw new InvalidDataException("Invalid sale request.");
        }
        internal readonly struct SaleReply
        {
            public readonly Guid Operation;
            public readonly PurchaseStatus Status;
            public readonly int Count, Radios;
            public readonly float Weight;
            public SaleReply(Guid operation, PurchaseStatus status, int count = 0, float weight = 0, int radios = 0)
            {
                if (operation == Guid.Empty || !Enum.IsDefined(typeof(PurchaseStatus), status) || count < 0 || radios < 0 ||
                    float.IsNaN(weight) || float.IsInfinity(weight) || weight < 0 || (status == PurchaseStatus.Placed ? count == 0 : count != 0 || weight != 0 || radios != 0))
                    throw new InvalidDataException("Invalid sale reply.");
                Operation = operation; Status = status; Count = count; Weight = weight; Radios = radios;
            }
            public static bool Is(byte[] payload) => payload != null && payload.Length == 30 && payload[0] == Marker;
            public byte[] Encode()
            {
                using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write((byte)Status);
                writer.Write(Count); writer.Write(Weight); writer.Write(Radios); return stream.ToArray();
            }
            public static SaleReply Decode(byte[] payload)
            {
                if (!Is(payload)) throw new InvalidDataException("Invalid sale reply size.");
                using var reader = new BinaryReader(new MemoryStream(payload, false)); reader.ReadByte();
                return new SaleReply(new Guid(reader.ReadBytes(16)), (PurchaseStatus)reader.ReadByte(), reader.ReadInt32(), reader.ReadSingle(), reader.ReadInt32());
            }
        }
    }
}
