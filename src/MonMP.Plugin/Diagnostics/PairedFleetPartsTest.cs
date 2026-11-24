using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Fleet: part sockets and bolt holes beyond the Konig. For each active shared vehicle with a
    // catalogued part for an empty socket, the guest installs, tightens and removes it natively and
    // the host follows. Physical bolt insertion and spanner input belong to the Bolts suites.
    internal sealed class PairedFleetPartsTest
    {
        private const int Mark = 60;
        private readonly MonMPPlugin plugin;
        private readonly WorldChannel channel;
        private readonly PairedPartRemovalFixture removal;
        private readonly List<(Vehicle_Type Type, ulong Socket, ushort Prefab)> targets = new List<(Vehicle_Type, ulong, ushort)>();
        private readonly List<string> skipped = new List<string>();
        private int phase, index;
        private float next, deadline, nextOffer;
        private InteractableItems? fixturePart;
        private ulong sharedPart, offeredSocket, offeredPart;
        private ushort offeredPrefab;
        private readonly ulong hole = 0;
        private string counts = "";
        internal string Evidence => "Fleet: guest picked up host-spawned shared parts with authoritative leases, installed them through the native held-part path, tightened and removed them on active native vehicles " +
            string.Join(",", targets.Select(target => target.Type)) + "; skipped=" + string.Join(",", skipped) +
            ". The host cleared fixture bolts after the guest observed full tightening; dormant shell installation and physical spanner/bolt insertion were not exercised.";
        internal string Status => "fleet phase=" + phase + " target=" + index + "/" + targets.Count + "; " + counts +
            (index < targets.Count ? "; current=" + targets[index].Type + " " + targets[index].Socket.ToString("X") : "") +
            "; skipped=" + string.Join(",", skipped) + "; sharedPart=" + sharedPart.ToString("X") + "; offer=" + offeredSocket.ToString("X") + PartStatus + "; hole=" + hole.ToString("X") + HoleStatus;
        private string PartStatus
        {
            get
            {
                var state = sharedPart == 0 ? null : plugin.Items?.StateOf(sharedPart);
                return state == null ? "" : "; item=" + state.Exists + "/" + state.Lease.Owner + "/" + state.Lease.Generation;
            }
        }
        private string HoleStatus
        {
            get
            {
                if (hole == 0 || plugin.World == null) return "";
                try { var n = plugin.World.Bolts.TestNative(hole); var r = plugin.World.Bolts.TestRecord(hole); return " native=" + n.Installed + "/" + n.Percent + " record=" + r.Installed + "/" + r.Percent; }
                catch (Exception ex) { return " holeStatus=" + ex.GetType().Name; }
            }
        }
        internal PairedFleetPartsTest(MonMPPlugin plugin)
        {
            this.plugin = plugin; channel = plugin.World!.Channel; removal = new PairedPartRemovalFixture(plugin);
            if (!plugin.TestOptions.IsHost) channel.ReplyReceived += OnOffer;
        }
        internal bool Tick()
        {
            var world = plugin.World;
            if (plugin.Session?.IsRunning != true || world == null || !ReferenceEquals(world.Channel, channel))
            {
                channel.ReplyReceived -= OnOffer; removal.Dispose();
                throw new InvalidOperationException("Fleet fixture session ended or was replaced before completion: " + Status);
            }
            var parts = world.VehicleParts; var bolts = world.Bolts;
            if (!parts.Ready || !bolts.Ready || !channel.FullySynchronized) return false;
            // The deadline starts once both peers are in; a slow guest load used to eat the host's budget.
            if (plugin.Session!.Peers.Count < 2 && phase == 0) return false;
            if (deadline == 0) { deadline = Time.unscaledTime + 150; next = Time.unscaledTime + 3; }
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Fleet test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            if (phase == 0)
            {
                if (plugin.TestOptions.IsHost && plugin.Session!.Peers.Count < 2) return false;
                counts = string.Join(" ", Replication.SharedVehicleParts.Fleet.Select(t => t + "=" + parts.TestCount(t) + "/" + bolts.TestCount(t)));
                // The scrapyard Konig is a Konig: its sockets are the Parts suite's (#196).
                foreach (var type in Replication.SharedVehicleParts.Fleet.Where(t => t != Vehicle_Type.MainVehicle && t != Vehicle_Type.ScrapyardKonig))
                {
                    var native = Singleton<GlobalClassManager>.i.GetVehicles(type);
                    if (native == null || !native.gameObject.activeInHierarchy)
                    { skipped.Add(type + (native == null ? "(missing)" : "(dormant)")); continue; }
                    if (parts.TestCount(type) == 0) { skipped.Add(type + "(no sockets)"); continue; }
                    try { var (socket, prefab) = parts.TestAnyEmptySocketWithPrefab(type); targets.Add((type, socket, prefab)); }
                    catch (InvalidOperationException) { skipped.Add(type + "(no empty catalogued socket)"); }
                }
                Require(targets.Count > 0, "No active shared vehicle beyond the Konig has an empty socket with a catalogued part: " + counts + "; skipped=" + string.Join(",", skipped));
                Debug.Log("MONMP_FLEET_TARGETS: active=" + string.Join(",", targets.Select(target => target.Type)) + "; skipped=" + string.Join(",", skipped));
                // Bolt mounts are host-validated (a shared bolt item, a nearby peer); the Bolts suites cover
                // them. This suite's unshared native attach from across the map is rejected by design (#196).
                phase = 1;
                return false;
            }
            return plugin.TestOptions.IsHost ? Host(parts, bolts) : Guest(parts, bolts);
        }
        private bool Host(Replication.SharedVehicleParts parts, Replication.SharedVehicleBolts bolts)
        {
            if (phase == 1)
            {
                if (index >= targets.Count) { phase = 4; return false; }
                var t = targets[index];
                var items = plugin.Items!;
                if (fixturePart == null && sharedPart == 0)
                {
                    Require(!parts.TestNative(t.Socket).Installed, "Host fixture requires an empty " + t.Type + " socket.");
                    fixturePart = items.TestSpawn(parts.TestSocketPosition(t.Socket) + Vector3.back * 1.4f + Vector3.up * 0.7f,
                        items.TestPrefab(parts.TestPrefabName(t.Prefab)));
                    fixturePart.isSpawnFromTheSaveLoadSystem = true; fixturePart.CreateBasics();
                    if (fixturePart.rb != null) fixturePart.rb.isKinematic = true;
                    items.TestDiscoverSoon(); next = Time.unscaledTime + 0.2f; return false;
                }
                if (sharedPart == 0)
                {
                    sharedPart = items.TestBoundId(fixturePart!);
                    if (sharedPart == 0) { items.TestDiscoverSoon(); return false; }
                }
                if (Time.unscaledTime >= nextOffer)
                {
                    Offer(t.Socket, t.Prefab, sharedPart); nextOffer = Time.unscaledTime + 0.5f;
                }
                if (parts.TestNative(t.Socket).Installed)
                {
                    Require(parts.TestNative(t.Socket).Prefab == t.Prefab, "Host applied the wrong prefab on the " + t.Type + ".");
                    Require(items.TestItem(sharedPart) == null, "Host kept the installed " + t.Type + " part as a shared loose item.");
                    phase = 2;
                }
            }
            else if (phase == 2 && parts.TestNative(targets[index].Socket).FullyTightened) phase = 3;
            else if (phase == 3)
            {
                removal.HostClear(parts, targets[index].Socket);
                if (!parts.TestNative(targets[index].Socket).Installed)
                { index++; fixturePart = null; sharedPart = 0; nextOffer = 0; phase = 1; }
            }
            else if (phase == 4 && (hole == 0 || (bolts.TestNative(hole).Installed && bolts.TestNative(hole).Percent == Mark))) phase = 5;
            else if (phase == 5 && plugin.Session!.Peers.Count == 1) { channel.ReplyReceived -= OnOffer; removal.Dispose(); return true; }
            return false;
        }
        private bool Guest(Replication.SharedVehicleParts parts, Replication.SharedVehicleBolts bolts)
        {
            var items = plugin.Items!;
            if (phase == 1)
            {
                if (index >= targets.Count) { phase = 4; return false; }
                var t = targets[index];
                if (plugin.World?.PersonalProfileReady != true || plugin.World.ReconnectPositions?.Settled != true) return false;
                Require(!parts.TestRecord(t.Socket).Installed, "The " + t.Type + " socket record is not empty.");
                if (offeredSocket != t.Socket || offeredPrefab != t.Prefab || offeredPart == 0 ||
                    items.ItemOf(offeredPart) is not InteractableParts part) return false;
                sharedPart = offeredPart;
                Require(items.TestState(sharedPart).Exists && part.PrefabName == parts.TestPrefabName(t.Prefab),
                    "The host offered the wrong shared part for the " + t.Type + ".");
                Move(part.transform.position + Vector3.right * 0.8f);
                phase = 10; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 10)
            {
                var game = plugin.Gameplay!;
                AccessTools.Field(typeof(PlayerInteractable), "currentInteractableItem").SetValue(game.Player, items.TestItem(sharedPart));
                AccessTools.Method(typeof(PlayerInteractable), "GrabInteractable").Invoke(game.Player, null);
                phase = 11; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 11)
            {
                if (!items.TestLocalHeld(sharedPart) || items.TestState(sharedPart).Lease.Owner != plugin.Session!.LocalPeerId) return false;
                Require(items.TestItem(sharedPart) is InteractableParts part && items.TryGetLocalPartInstall(part, out ulong id, out _) && id == sharedPart,
                    "Guest held the fleet part without its shared install lease.");
                Move(parts.TestSocketPosition(targets[index].Socket) + Vector3.back * 1.2f);
                parts.TestAimHeld(targets[index].Socket);
                phase = 12; next = Time.unscaledTime + 1f;
            }
            else if (phase == 12)
            {
                var t = targets[index];
                Require(items.TestLocalHeld(sharedPart) && items.TestState(sharedPart).Lease.Owner == plugin.Session!.LocalPeerId,
                    "Guest lost its shared fleet part before native installation.");
                Require(items.TestItem(sharedPart) is InteractableParts part &&
                    items.TryGetLocalPartInstall(part, out ulong id, out _) && id == sharedPart,
                    "Shared fleet part installation is no longer admitted.");
                var installed = parts.TestInstallHeld(t.Socket);
                Require(parts.TestNative(t.Socket).Installed && parts.TestInstalledPart(t.Socket) == installed,
                    "The local held-part install on the " + t.Type + " did not apply.");
                phase = 2; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 2 && parts.TestRecord(targets[index].Socket).Installed)
            {
                Require(parts.TestRecord(targets[index].Socket).Prefab == targets[index].Prefab,
                    "Host echo lost the installed fleet part prefab.");
                parts.TestTighten(targets[index].Socket);
                phase = 3; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 3 && parts.TestRecord(targets[index].Socket).FullyTightened)
            { removal.Request(targets[index].Socket); phase = 30; }
            else if (phase == 30 && removal.GuestReady(parts, targets[index].Socket))
            {
                parts.TestRemove(targets[index].Socket);
                phase = 35; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 35 && !parts.TestRecord(targets[index].Socket).Installed) { index++; phase = 1; }
            else if (phase == 4)
            {
                if (hole != 0)
                {
                    if (bolts.TestNative(hole).Installed) bolts.TestUnscrew(hole);
                    bolts.TestInstall(hole); bolts.TestTighten(hole, Mark);
                }
                phase = 5; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 5 && (hole == 0 || (bolts.TestRecord(hole).Installed && bolts.TestRecord(hole).Percent == Mark)))
            {
                channel.ReplyReceived -= OnOffer;
                removal.Dispose();
                plugin.EndPairedTestSession();
                return true;
            }
            return false;
        }
        private void Offer(ulong socket, ushort prefab, ulong item)
        {
            uint peer = plugin.Session!.Peers.Keys.Single(value => value != 0);
            if (!channel.TestPeerSynchronized(peer)) return;
            var payload = new byte[20]; payload[0] = 0x66; payload[1] = 0x49;
            Array.Copy(BitConverter.GetBytes(socket), 0, payload, 2, 8);
            Array.Copy(BitConverter.GetBytes(prefab), 0, payload, 10, 2);
            Array.Copy(BitConverter.GetBytes(item), 0, payload, 12, 8);
            channel.Reply(peer, ClockState.Entity, payload);
        }
        private void OnOffer(ulong entity, byte[] payload)
        {
            if (entity != ClockState.Entity || payload.Length != 20 || payload[0] != 0x66 || payload[1] != 0x49) return;
            offeredSocket = BitConverter.ToUInt64(payload, 2); offeredPrefab = BitConverter.ToUInt16(payload, 10);
            offeredPart = BitConverter.ToUInt64(payload, 12);
        }
        private void Move(Vector3 location)
        {
            var game = plugin.Gameplay!;
            game.PlayerWalking.transform.position = location; game.Get_Player_Rigidbody.position = location; game.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Fleet test: " + reason); }
    }
}
