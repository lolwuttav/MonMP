using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedVehiclePartsTest
    {
        private readonly MonMPPlugin plugin;
        private readonly WorldChannel fixtureChannel;
        private readonly PairedPartRemovalFixture removal;
        private const byte InputMarker = 0x70, InputDomain = 0x49;
        private InteractableParts? initialHostPart, initialGuestPart;
        private ulong initialInput, announcedSocket;
        private ushort announcedPrefab;
        private int initialPickupPhase, initialPickupAttempts;
        private float nextInputAnnouncement;
        private bool initialInputSpawned;
        private int phase;
        private float next;
        private ulong socket;
        private ushort prefab;
        private InteractableParts? removed;
        private ulong held;
        private Vector3 firstHeldPosition;
        private float observedUntil;
        private bool moved;
        private string partName = "";
        private int looseBaseline = -1;
        private float phaseAt;
        private bool sawRemoved;
        private InteractableParts? hostInstalled, guestInstalled;
        private ulong replica;
        private InteractableItems? hostDropped;
        private Action<IReadOnlyDictionary<ulong, WorldRecord>>? snapshotFault;
        private int repairsBeforeFault;
        private long handlerFailuresBeforeFault;
        private readonly List<(ulong Id, Vehicle_PartSocket Socket, VehiclePartState State)> repairFamily =
            new List<(ulong, Vehicle_PartSocket, VehiclePartState)>();
        private readonly Dictionary<BoltHoleDetails, BoltHoleState> repairBolts = new Dictionary<BoltHoleDetails, BoltHoleState>();
        private readonly Dictionary<BoltHoleDetails, BoltHoleState> originalPartialBolts = new Dictionary<BoltHoleDetails, BoltHoleState>();
        private Vehicle_PartSocket? originalPartialSocket;
        private ulong originalPartialId;
        private static int Loose(string name) => Replication.SharedVehicleParts.TestLooseParts(name);
        private string DescribeLoose(Replication.SharedVehicleParts parts)
        {
            var anchor = parts.TestSocketPosition(socket);
            return string.Join("; ", UnityEngine.Object.FindObjectsOfType<InteractableParts>()
                .Where(p => p.PrefabName == partName && !p.isPlaced)
                .Select(p =>
                {
                    ulong id = plugin.Items!.TestBoundId(p);
                    return "id " + id.ToString("X") + (id == 0 ? "" : " lease " + plugin.Items.TestState(id).Lease.Owner + "/" + plugin.Items.TestState(id).Lease.Generation) +
                        " " + Vector3.Distance(p.transform.position, anchor).ToString("F2") + " m from socket, parent " + (p.transform.parent == null ? "none" : p.transform.parent.name) +
                        ", collider " + (p.itemCollider != null && p.itemCollider.enabled) + ", temp " + p.TempPlaced;
                }));
        }
        internal string Status => "parts phase=" + phase + "; count=" + plugin.World?.VehicleParts.Count +
            "; initialPickup=" + initialPickupPhase + "/" + initialPickupAttempts + "; input=" + initialInput.ToString("X") +
            "; inputLease=" + InitialInputLease() + "; held=" + held.ToString("X") + "; local=" + LocalHeldName() + "; adopting=" + plugin.Items?.TestAdopting;
        private int loggedPhase = -1;
        private float discoverUntil;
        private string LocalHeldName()
        {
            var item = plugin.Gameplay?.Player?.Get_HoldingItem;
            return item == null ? "" : item.name; // Unity null: a destroyed held item must not throw here
        }
        internal PairedVehiclePartsTest(MonMPPlugin plugin)
        {
            if (!Testing.LocalTestIsolation.Installed || !plugin.TestOptions.Enabled)
                throw new InvalidOperationException("The parts input fixture requires an isolated local test copy.");
            this.plugin = plugin; fixtureChannel = plugin.World!.Channel; removal = new PairedPartRemovalFixture(plugin);
            if (!plugin.TestOptions.IsHost) fixtureChannel.ReplyReceived += InitialInputReply;
        }
        private string InitialInputLease() => initialInput != 0 && fixtureChannel.Records.TryGetValue(initialInput, out var record) && record.Adapter == ItemState.Adapter
            ? ItemState.Decode(record.Payload).Lease.Owner + "/" + ItemState.Decode(record.Payload).Lease.Generation : "pending";
        private void InitialInputReply(ulong entity, byte[] payload)
        {
            if (phase != 0 || entity != ClockState.Entity || payload.Length != 20 || payload[0] != InputMarker || payload[1] != InputDomain) return;
            announcedSocket = BitConverter.ToUInt64(payload, 2); announcedPrefab = BitConverter.ToUInt16(payload, 10);
            initialInput = BitConverter.ToUInt64(payload, 12);
        }
        private void AnnounceInitialInput(uint peer)
        {
            if (Time.unscaledTime < nextInputAnnouncement || !fixtureChannel.TestPeerSynchronized(peer)) return;
            var payload = new byte[20]; payload[0] = InputMarker; payload[1] = InputDomain;
            Array.Copy(BitConverter.GetBytes(socket), 0, payload, 2, 8);
            Array.Copy(BitConverter.GetBytes(prefab), 0, payload, 10, 2);
            Array.Copy(BitConverter.GetBytes(initialInput), 0, payload, 12, 8);
            fixtureChannel.Reply(peer, ClockState.Entity, payload); nextInputAnnouncement = Time.unscaledTime + 0.5f;
        }
        internal bool Tick()
        {
            var world = plugin.World;
            if (plugin.Session?.IsRunning != true || world == null || !ReferenceEquals(world.Channel, fixtureChannel))
            {
                fixtureChannel.ReplyReceived -= InitialInputReply; removal.Dispose();
                throw new InvalidOperationException("Parts fixture session ended or was replaced before completion: " + Status);
            }
            if (phase != loggedPhase) { loggedPhase = phase; Debug.Log("MonMP parts test phase " + phase + " holding " + LocalHeldName()); }
            var parts = world.VehicleParts;
            if (!parts.Ready) return false;
            if (parts.Count == 0) throw new InvalidOperationException("No Konig part sockets found; is the master scene loaded?");
            if (phase == 0 && socket == 0)
            {
                (socket, prefab) = parts.TestAnyEmptySocketWithPrefab(boltedBodyPart: true);
                Debug.Log("MonMP parts fixture socket " + parts.TestSocketType(socket) + " prefab " + prefab);
                if (!plugin.TestOptions.IsHost)
                {
                    PartsTuneBaselineFixture.CheckCapturedAndReplaced(plugin);
                    var bolts = world.Bolts;
                    foreach (var id in bolts.TestFirstHoles(bolts.Count))
                    {
                        var state = bolts.TestOriginal(id);
                        var hole = bolts.TestHole(id);
                        if (!state.Installed || state.Percent == 0 || state.Percent == 100) continue;
                        var bound = parts.PaintSockets.FirstOrDefault(p => p.Socket == hole.SockedBoltedByThisBolt);
                        if (bound.Socket == null || !parts.TestOriginal(bound.Id).Installed) continue;
                        originalPartialSocket = bound.Socket; originalPartialId = bound.Id;
                        foreach (var siblingId in bolts.TestFirstHoles(bolts.Count))
                            if (bolts.TestHole(siblingId).SockedBoltedByThisBolt == bound.Socket)
                                originalPartialBolts[bolts.TestHole(siblingId)] = bolts.TestOriginal(siblingId);
                        break;
                    }
                }
            }
            return plugin.TestOptions.IsHost ? Host(parts) : Guest(parts);
        }
        private bool Host(Replication.SharedVehicleParts parts)
        {
            if (phase == 0)
            {
                Require(!parts.TestNative(socket).Installed, "Fixture requires an empty socket.");
                if (plugin.Session!.Peers.Count != 2) return false;
                var items = plugin.Items!;
                if (!initialInputSpawned)
                {
                    initialHostPart = items.TestSpawn(parts.TestSocketPosition(socket) + Vector3.back * 1.5f + Vector3.up * 0.35f,
                        items.TestPrefab(parts.TestPrefabName(prefab))) as InteractableParts;
                    Require(initialHostPart != null, "The host fixture did not spawn the requested loose native part.");
                    initialInputSpawned = true;
                    items.TestDiscoverSoon(); return false;
                }
                Require(initialHostPart != null, "The original host-created loose input disappeared before its guest handoff.");
                initialInput = items.TestBoundId(initialHostPart!);
                if (initialInput == 0) { items.TestDiscoverSoon(); return false; }
                uint peer = plugin.Session.Peers.Keys.Single(value => value != 0);
                AnnounceInitialInput(peer);
                if (items.TestState(initialInput).Lease.Owner != peer) return false;
                Debug.Log("MONMP_PARTS_INPUT_HOST: socket=" + socket.ToString("X") + "; item=" + initialInput.ToString("X") +
                    "; prefab=" + prefab + "; guest lease=" + items.TestState(initialInput).Lease.Generation);
                phase = 1;
            }
            else if (phase == 1 && parts.TestNative(socket).Installed)
            {
                Require(parts.TestNative(socket).Prefab == prefab, "Host did not apply the guest's installed part prefab.");
                phase = 2;
            }
            else if (phase == 2 && parts.TestNative(socket).FullyTightened)
            {
                Require(parts.TestInstalledBoltObjects(socket), "Host restored bolt flags without native bolt objects.");
                phase = 3;
            }
            else if (phase == 3)
            {
                removal.HostClear(parts, socket);
                if (parts.TestNative(socket).Installed) return false;
                uint peer = plugin.Session!.Peers.Keys.Single(p => p != 0);
                held = plugin.Items!.TestHeldPart(peer);
                if (held == 0) return false;
                firstHeldPosition = plugin.Items.TestItem(held)!.transform.position;
                partName = plugin.Items.TestItem(held)!.PrefabName;
                looseBaseline = Loose(partName) - 1; // the guest's held part is loose until it is installed again
                observedUntil = Time.unscaledTime + 4;
                phase = 4;
            }
            else if (phase == 4)
            {
                var item = plugin.Items!.TestItem(held);
                Require(item != null && item.rb != null && item.rb.isKinematic, "Guest-removed held part became dynamic on the host after native Start.");
                Require(item!.itemCollider.isTrigger, "Remote held part retained a solid collider.");
                moved |= Vector3.Distance(firstHeldPosition, item.transform.position) > 0.5f;
                if (Time.unscaledTime >= observedUntil) { Require(moved, "Host held part did not follow the guest hand movement."); phase = 5; }
            }
            // The guest installs the held (shared, loose) part natively, as a player does (#132).
            else if (phase == 5 && parts.TestNative(socket).Installed) { phase = 6; phaseAt = Time.unscaledTime; }
            else if (phase == 6 && Time.unscaledTime - phaseAt > 5)
            {
                Require(parts.TestNative(socket).Installed && parts.TestInstalledPart(socket) != null, "Host lost the part the guest installed.");
                Require(Loose(partName) == looseBaseline, "Host has a loose copy of the part the guest installed: " + Loose(partName) + " loose, baseline " + looseBaseline + ": " + DescribeLoose(parts) + ".");
                Require(plugin.Items!.TestItem(held) == null, "The host's twin of the guest-installed part is still a shared loose item.");
                parts.TestRemoveIntoHand(socket);
                Require(!parts.TestNative(socket).Installed, "Host native remove did not apply immediately.");
                phase = 7; phaseAt = Time.unscaledTime;
            }
            else if (phase == 7 && Time.unscaledTime - phaseAt > 1)
            {
                parts.TestAimHeld(socket);
                phase = 70; phaseAt = Time.unscaledTime;
            }
            else if (phase == 70 && Time.unscaledTime - phaseAt > 1.5f)
            {
                hostInstalled = parts.TestInstallHeld(socket);
                Require(parts.TestNative(socket).Installed, "Host native install of its held part did not apply immediately.");
                phase = 8; phaseAt = Time.unscaledTime;
            }
            else if (phase == 8 && Time.unscaledTime - phaseAt > 2)
            {
                Require(parts.TestInstalledPart(socket) == hostInstalled && hostInstalled != null, "Host's installed part was replaced or destroyed.");
                Require(Loose(partName) == looseBaseline, "Installing a held part as host left a loose duplicate: " + Loose(partName) + " loose, baseline " + looseBaseline + ": " + DescribeLoose(parts) + ".");
                phase = 85; phaseAt = Time.unscaledTime;
            }
            else if (phase == 85 && Time.unscaledTime - phaseAt > 3)
            {
                var game = plugin.Gameplay!;
                game.PlayerWalking.transform.position = parts.TestSocketPosition(socket) + Vector3.back * 1.2f;
                game.Get_Player_Rigidbody.position = game.PlayerWalking.transform.position;
                game.Get_Player_Rigidbody.velocity = Vector3.zero;
                // Take it off and drop it for the guest to pick up: the guest then holds a copy of a host item.
                parts.TestRemoveIntoHand(socket);
                phase = 9; phaseAt = Time.unscaledTime;
            }
            else if (phase == 9 && Time.unscaledTime - phaseAt > 1)
            {
                hostDropped = plugin.Gameplay!.Player.Get_HoldingItem;
                plugin.Gameplay.Player.UnGrabHoldingItem();
                phase = 10; phaseAt = Time.unscaledTime;
            }
            else if (phase == 10 && plugin.Gameplay!.Player.Get_HoldingItem != null)
            {
                if (Time.unscaledTime - phaseAt < 3) return false;
                ulong id = hostDropped == null ? 0 : plugin.Items!.TestBoundId(hostDropped);
                throw new InvalidOperationException("Vehicle parts test: host could not drop the part it took off: bound " + id.ToString("X") +
                    (id == 0 ? "" : " lease " + plugin.Items!.TestState(id).Lease.Owner + "/" + plugin.Items.TestState(id).Lease.Generation) +
                    " holding " + LocalHeldName() + " state " + plugin.Gameplay.Get_PlayerState +
                    " distance " + (hostDropped == null ? -1 : Vector3.Distance(hostDropped.transform.position, plugin.Gameplay.PlayerWalking.transform.position)).ToString("F1"));
            }
            else if (phase == 10 && parts.TestNative(socket).Installed) { phase = 11; phaseAt = Time.unscaledTime; }
            else if (phase == 11 && Time.unscaledTime - phaseAt > 1.5f)
            {
                Require(parts.TestNative(socket).Installed && parts.TestInstalledPart(socket) != null, "Host lost the host part the guest picked up and installed.");
                Require(Loose(partName) == looseBaseline, "Host kept a loose copy of the part the guest picked up and installed: " + Loose(partName) + " loose, baseline " + looseBaseline + ": " + DescribeLoose(parts) + ".");
                phase = 12;
            }
            else if (phase == 12 && plugin.Session!.Peers.Count == 1) { removal.Dispose(); plugin.EndPairedTestSession(); return true; }
            return false;
        }
        private bool Guest(Replication.SharedVehicleParts parts)
        {
            // Scan for new local items every frame while the host's copy is removed and shrinks away:
            // the once-a-second scan otherwise hits that 0.3 s window only by chance (#132).
            if (Time.unscaledTime < discoverUntil) plugin.Items!.TestDiscoverSoon();
            if (next > 0 && UnityEngine.Time.unscaledTime < next) return false;
            if (phase == 0)
            {
                try
                {
                    if (!PrepareInitialGuestInput(parts)) return false;
                    var installed = parts.TestInstallHeld(socket);
                    Require(installed == initialGuestPart && parts.TestNative(socket).Installed,
                        "Local native install did not apply immediately after the shared input lease and native aim settled.");
                    fixtureChannel.ReplyReceived -= InitialInputReply;
                    Debug.Log("MONMP_PARTS_INPUT_GUEST: native held install; socket=" + socket.ToString("X") +
                        "; item=" + initialInput.ToString("X") + "; pickup attempts=" + initialPickupAttempts);
                    phase = 1; next = UnityEngine.Time.unscaledTime + 1.5f;
                }
                catch { fixtureChannel.ReplyReceived -= InitialInputReply; throw; }
            }
            else if (phase == 1 && parts.TestRecord(socket).Installed)
            {
                Require(parts.TestRecord(socket).Prefab == prefab, "Host echo lost the installed prefab.");
                parts.TestTighten(socket);
                Require(parts.TestNative(socket).FullyTightened, "Local native tighten did not apply immediately.");
                phase = 2; next = UnityEngine.Time.unscaledTime + 1.5f;
            }
            else if (phase == 2 && parts.TestRecord(socket).FullyTightened)
            {
                Require(parts.TestInstalledBoltObjects(socket), "Guest restored bolt flags without native bolt objects.");
                removal.Request(socket); phase = 20;
            }
            else if (phase == 20 && removal.GuestReady(parts, socket))
            {
                removed = parts.TestRemoveIntoHand(socket);
                Require(!parts.TestNative(socket).Installed, "Local native remove did not apply immediately.");
                phase = 3; next = UnityEngine.Time.unscaledTime + 1.5f;
            }
            else if (phase == 3 && !parts.TestRecord(socket).Installed)
            {
                held = plugin.Items!.TestBoundId(removed!);
                partName = removed!.PrefabName;
                if (held == 0 || plugin.Items.TestState(held).Lease.Owner != plugin.Session!.LocalPeerId) return false;
                Require(plugin.Gameplay!.Player.Get_HoldingItem == removed, "Removal did not retain the part in the guest's native hand.");
                phase = 4; next = Time.unscaledTime + 1;
            }
            else if (phase == 4)
            {
                var game = plugin.Gameplay!;
                game.PlayerWalking.transform.position += Vector3.right;
                game.Get_Player_Rigidbody.position = game.PlayerWalking.transform.position;
                game.Get_Player_Rigidbody.velocity = Vector3.zero;
                phase = 5; next = Time.unscaledTime + 5;
            }
            else if (phase == 5)
            {
                Require(plugin.Items!.TestLocalHeld(held), "Guest lost the removed part during the hold.");
                looseBaseline = Loose(partName) - 1;
                parts.TestAimHeld(socket);
                phase = 50; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 50)
            {
                var installed = parts.TestInstallHeld(socket);
                Require(parts.TestNative(socket).Installed && parts.TestInstalledPart(socket) == installed, "Guest native install of its held part did not apply immediately.");
                phase = 6; next = Time.unscaledTime + 2f;
            }
            else if (phase == 6)
            {
                Require(parts.TestRecord(socket).Installed, "Host did not echo the guest's install of its held part.");
                Require(parts.TestNative(socket).Installed && parts.TestInstalledPart(socket) != null, "The part the guest installed disappeared from its socket.");
                Require(Loose(partName) == looseBaseline, "Guest has a loose copy of the part it installed: " + Loose(partName) + " loose, baseline " + looseBaseline + ": " + DescribeLoose(parts) + ".");
                phase = 7;
            }
            else if (phase == 7)
            {
                sawRemoved |= !parts.TestRecord(socket).Installed;
                if (sawRemoved) discoverUntil = Time.unscaledTime + 2f;
                if (!sawRemoved || !parts.TestRecord(socket).Installed) return false;
                phase = 8; next = Time.unscaledTime + 2.5f;
            }
            else if (phase == 8)
            {
                Require(parts.TestNative(socket).Installed && parts.TestInstalledPart(socket) != null, "Guest did not show the part the host installed.");
                Require(Loose(partName) == looseBaseline, "Guest has a loose copy of the part the host installed: " + Loose(partName) + " loose, baseline " + looseBaseline + ": " + DescribeLoose(parts) + ".");
                phase = 9;
            }
            else if (phase == 9)
            {
                // The host takes it off and drops it; pick up the guest's copy of that host item.
                if (parts.TestRecord(socket).Installed) return false;
                replica = plugin.Items!.TestLoosePart(partName);
                if (replica == 0) return false;
                var item = plugin.Items.TestItem(replica)!;
                var game = plugin.Gameplay!;
                game.PlayerWalking.transform.position = item.transform.position + Vector3.right * 0.8f;
                game.Get_Player_Rigidbody.position = game.PlayerWalking.transform.position;
                game.Get_Player_Rigidbody.velocity = Vector3.zero;
                next = Time.unscaledTime + 1.5f; phase = 10;
            }
            else if (phase == 10)
            {
                if (plugin.Gameplay!.Player.Get_HoldingItem == null) { plugin.Items!.TestGrab(replica); next = Time.unscaledTime + 0.5f; return false; }
                Require(plugin.Items!.TestState(replica).Lease.Owner == plugin.Session!.LocalPeerId, "Guest holds the host's part without its lease.");
                parts.TestAimHeld(socket);
                phase = 11; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 11)
            {
                guestInstalled = parts.TestInstallHeld(socket);
                phase = 12; next = Time.unscaledTime + 5f; // the host checks it at 1.5 s after its echo
            }
            else if (phase == 12)
            {
                Require(parts.TestRecord(socket).Installed, "Host did not echo the guest's install of the host's part.");
                Require(parts.TestInstalledPart(socket) == guestInstalled && guestInstalled != null, "The host part the guest installed disappeared from its socket.");
                Require(Loose(partName) == looseBaseline, "Guest has a loose copy of the host part it installed: " + Loose(partName) + " loose, baseline " + looseBaseline + ": " + DescribeLoose(parts) + ".");
                parts.TestRemoveIntoHand(socket);
                phase = 13; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 13)
            {
                Require(plugin.Gameplay!.Player.Get_HoldingItem == guestInstalled, "Guest could not take the installed host part off again.");
                plugin.Items!.TestDrop(false);
                phase = 14; next = Time.unscaledTime + 3f;
            }
            else if (phase == 14)
            {
                var dropped = guestInstalled;
                if (dropped == null) throw new InvalidOperationException("Vehicle parts test: the part the guest dropped vanished.");
                Require(dropped != null && dropped.gameObject.activeInHierarchy, "The part the guest dropped vanished.");
                Require(plugin.Items!.TestBoundId(dropped!) != 0, "The part the guest took off and dropped is not a shared item again.");
                Require(dropped!.itemCollider != null && dropped.itemCollider.enabled && !dropped.itemCollider.isTrigger, "The part the guest dropped has no solid collider.");
                Require(dropped.transform.position.y > parts.TestSocketPosition(socket).y - 3f, "The part the guest dropped fell through the world.");
                var channel = plugin.World!.Channel;
                repairsBeforeFault = channel.RepairsApplied;
                handlerFailuresBeforeFault = channel.HandlerFailures;
                snapshotFault = _ => throw new ArgumentException("Controlled paired Parts snapshot failure.");
                channel.SnapshotApplied += snapshotFault;
                Require(channel.RequestResync(SnapshotScope.ForEntity(VehiclePartState.Adapter, socket)), "Could not request the scoped Parts snapshot.");
                phase = 15; phaseAt = Time.unscaledTime;
            }
            else if (phase == 15)
            {
                var channel = plugin.World!.Channel;
                if (channel.RepairsApplied <= repairsBeforeFault || channel.HandlerFailures <= handlerFailuresBeforeFault)
                {
                    if (Time.unscaledTime - phaseAt > 8f) throw new InvalidOperationException("Vehicle parts test: controlled scoped-snapshot handler failure was not isolated.");
                    return false;
                }
                channel.SnapshotApplied -= snapshotFault!;
                Require(plugin.Session!.IsRunning, "A local scoped Parts snapshot failure disconnected the guest.");
                Require(parts.TestNative(socket).Equals(parts.TestRecord(socket)), "Scoped Parts snapshot did not leave the installed part synchronized.");
                var engine = parts.PaintSockets.FirstOrDefault(p => p.Socket.car_PartsType == Car_PartsType.Engine &&
                    p.Socket.vehicleAttached.Get_Vehicle.vehicle_type == Vehicle_Type.MainVehicle && p.Socket.isInstalled &&
                    p.Socket.partInstalled != null && parts.TestRecord(p.Id).Installed);
                if (engine.Socket == null)
                    Debug.Log("MonMP parts regression SKIP scoped engine repair: no installed accepted main Konig engine.");
                else
                {
                    var manager = engine.Socket.vehicleAttached;
                    var family = new[] { engine.Socket, manager.Intake, manager.ExhaustManifold, manager.Transmission };
                    foreach (var member in family)
                    {
                        var bound = parts.PaintSockets.FirstOrDefault(p => p.Socket == member);
                        if (bound.Socket == null || !member.isInstalled || member.partInstalled == null || !parts.TestRecord(bound.Id).Installed)
                        { repairFamily.Clear(); break; }
                        repairFamily.Add((bound.Id, member, parts.TestRecord(bound.Id)));
                    }
                    if (repairFamily.Count != family.Length)
                        Debug.Log("MonMP parts regression SKIP scoped engine repair: accepted intake/manifold/transmission prerequisites absent.");
                    else
                    {
                        foreach (var id in plugin.World.Bolts.TestFirstHoles(plugin.World.Bolts.Count))
                        {
                            var hole = plugin.World.Bolts.TestHole(id);
                            if (family.Contains(hole.SockedBoltedByThisBolt)) repairBolts[hole] = plugin.World.Bolts.TestRecord(id);
                        }
                        if (!parts.TestReplaceWithNativeAlternate(engine.Id))
                        {
                            Debug.Log("MonMP parts regression SKIP scoped engine replacement: no genuine compatible alternate engine prefab.");
                            return FinishDisconnectRegression(parts);
                        }
                        repairsBeforeFault = channel.RepairsApplied;
                        Require(channel.RequestResync(SnapshotScope.ForEntity(VehiclePartState.Adapter, engine.Id)), "Could not request scoped engine regression repair.");
                        phase = 16; phaseAt = Time.unscaledTime;
                        return false;
                    }
                }
                return FinishDisconnectRegression(parts);
            }
            else if (phase == 16)
            {
                if (plugin.World!.Channel.RepairsApplied <= repairsBeforeFault)
                {
                    Require(Time.unscaledTime - phaseAt < 8f, "Scoped engine regression repair timed out.");
                    return false;
                }
                foreach (var member in repairFamily)
                {
                    Require(parts.TestNative(member.Id).Equals(member.State), "Scoped engine repair lost accepted " + member.Socket.car_PartsType + ".");
                    Require(parts.TestInstalledBoltObjects(member.Id), "Scoped engine repair restored flags without bolt hardware.");
                }
                foreach (var pair in repairBolts) Require(HoleState(pair.Key).Equals(pair.Value), "Scoped engine repair lost accepted per-hole tightness.");
                Debug.Log("MonMP parts regression PASS scoped engine replacement and accepted children/bolt restoration.");
                return FinishDisconnectRegression(parts);
            }
            return false;
        }
        private bool PrepareInitialGuestInput(Replication.SharedVehicleParts parts)
        {
            var world = plugin.World!; var items = plugin.Items!; var game = plugin.Gameplay!;
            if (!world.PersonalProfileReady || world.ReconnectPositions?.Settled != true || game.Get_PlayerState != PlayerState.Walking) return false;
            Require(!parts.TestRecord(socket).Installed, "Late join missed the empty socket baseline.");
            if (initialInput == 0) return false;
            Require(announcedSocket == socket && announcedPrefab == prefab, "The host announced a different native input/socket fixture.");
            if (!fixtureChannel.Records.TryGetValue(initialInput, out var record) || record.Adapter != ItemState.Adapter) return false;
            initialGuestPart ??= items.ItemOf(initialInput) as InteractableParts;
            var inputPart = initialGuestPart;
            if (inputPart == null) return false;
            Require(items.TestBoundId(inputPart) == initialInput && inputPart.PrefabName == parts.TestPrefabName(prefab),
                "The announced host input did not bind to the expected native part.");
            if (initialPickupPhase == 0)
            {
                Require(game.Player.Get_HoldingItem == null, "The initial part pickup requires empty native hands.");
                MoveInitialApproach(inputPart.transform.position); initialPickupPhase = 1;
                next = Time.unscaledTime + 1; return false;
            }
            if (initialPickupPhase == 1)
            {
                if (Vector3.Distance(game.PlayerWalking.transform.position, inputPart.transform.position) > 3.5f)
                { MoveInitialApproach(inputPart.transform.position); next = Time.unscaledTime + 1; return false; }
                if (items.TestState(initialInput).Lease.Owner != plugin.Session!.LocalPeerId || !items.TestLocalHeld(initialInput))
                {
                    if (!items.TestPickupPending(initialInput))
                    {
                        Require(initialPickupAttempts++ < 6, "The host-created input did not receive its guest pickup lease.");
                        HarmonyLib.AccessTools.Field(typeof(PlayerInteractable), "currentInteractableItem").SetValue(game.Player, inputPart);
                        HarmonyLib.AccessTools.Method(typeof(PlayerInteractable), "GrabInteractable").Invoke(game.Player, null);
                    }
                    next = Time.unscaledTime + 0.5f; return false;
                }
                Require(game.Player.Get_HoldingItem == inputPart && items.TryGetLocalPartInstall(inputPart, out var input, out _) && input == initialInput,
                    "The granted shared part was not the native locally held installation input.");
                MoveInitialApproach(parts.TestSocketPosition(socket)); initialPickupPhase = 2;
                next = Time.unscaledTime + 1; return false;
            }
            Require(items.TestLocalHeld(initialInput) && items.TryGetLocalPartInstall(inputPart, out var leased, out _) && leased == initialInput,
                "The shared input lease was lost before native installation.");
            if (Vector3.Distance(game.PlayerWalking.transform.position, parts.TestSocketPosition(socket)) > 3.5f)
            { MoveInitialApproach(parts.TestSocketPosition(socket)); next = Time.unscaledTime + 1; return false; }
            if (initialPickupPhase == 2)
            {
                parts.TestAimHeld(socket); initialPickupPhase = 3; next = Time.unscaledTime + 1; return false;
            }
            return true;
        }
        private void MoveInitialApproach(Vector3 target)
        {
            var game = plugin.Gameplay!; var position = target + Vector3.back * 1.2f;
            game.PlayerWalking.transform.position = position; game.Get_Player_Rigidbody.position = position;
            game.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private bool FinishDisconnectRegression(Replication.SharedVehicleParts parts)
        {
            bool canExercise = originalPartialSocket != null && originalPartialSocket.isInstalled && originalPartialSocket.partInstalled != null &&
                parts.TestReplaceWithNativeAlternate(originalPartialId);
            if (!canExercise) Debug.Log("MonMP parts regression SKIP partial-bolt disconnect: no actual partially bolted baseline with a genuine compatible alternate prefab.");
            removal.Dispose();
            plugin.EndPairedTestSession();
            PartsTuneBaselineFixture.CheckRestored();
            if (canExercise)
            {
                Require(parts.TestNative(originalPartialId).Equals(parts.TestOriginal(originalPartialId)), "Disconnect did not restore original partial-bolt part.");
                Require(parts.TestInstalledBoltObjects(originalPartialId), "Disconnect restored bolt flags without hardware.");
                foreach (var pair in originalPartialBolts) Require(HoleState(pair.Key).Equals(pair.Value), "Part reinstall erased original partial bolt tightness during disconnect.");
                Debug.Log("MonMP parts regression PASS actual partial-bolt baseline restoration after part replacement/disconnect.");
            }
            return true;
        }
        private static BoltHoleState HoleState(BoltHoleDetails hole) => new BoltHoleState(hole.isBoltInstalled,
            hole.isBoltInstalled && hole.BoltedPercent > 0 ? (byte)Mathf.Max(1, Mathf.RoundToInt(Mathf.Clamp01(hole.BoltedPercent) * 100)) : (byte)0);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Vehicle parts test: " + message); }
    }

    // Runs before SharedWorld construction. Original scene parts and hardware are retained,
    // while native socket operations install owned resource clones for the isolated test.
    internal static class PartsTuneBaselineFixture
    {
        private sealed class SavedSocket
        {
            internal Vehicle_PartSocket Socket = null!;
            internal InteractableParts Part = null!;
            internal Vector3 Position, Scale;
            internal Quaternion Rotation;
            internal readonly List<(BoltHoleDetails Hole, bool Installed, float Percent, Hardware_Bolt? Bolt)> Holes =
                new List<(BoltHoleDetails, bool, float, Hardware_Bolt?)>();
        }
        private static readonly List<SavedSocket> saved = new List<SavedSocket>();
        private static GameObject? storage;
        private static NWH.VehiclePhysics2.VehicleController? vehicle;
        private static Part_ECU? stagedEcu;
        private static VehicleTuneState expected;
        private static string stagedPrefab = "";
        private static bool prepared, guestReady, checkedReplacement;

        internal static void Prepare(bool host)
        {
            if (prepared) return;
            prepared = true;
            vehicle = UnityEngine.Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>()
                .FirstOrDefault(v => v.vehicle_type == Vehicle_Type.MainVehicle);
            var manager = vehicle?.vehicle_PartsManager;
            if (manager?.ECU == null || !manager.ECU.isInstalled || !(manager.ECU.partInstalled is Part_ECU))
            { Debug.Log("MonMP tune regression SKIP: no genuine installed main Konig ECU before world construction."); return; }
            if (host)
            {
                if (manager.NitrousBottle?.isInstalled == true && manager.NitrousBottle.partInstalled != null)
                    SaveAndRemove(manager.NitrousBottle);
                Debug.Log("MonMP tune regression host staged native bottle absence before world construction.");
                return;
            }
            if (manager.NitrousBottle == null || !manager.NitrousBottle.isInstalled || manager.NitrousBottle.partInstalled == null)
            { Debug.Log("MonMP tune regression SKIP: genuine guest bottle prerequisite absent."); return; }
            var alternate = Resources.LoadAll<GameObject>("").FirstOrDefault(asset =>
                asset.GetComponent<Part_ECU>() is Part_ECU ecu && ecu.PrefabName != manager.ECU.partInstalled.PrefabName &&
                (ecu.CompatibleVehiclesType & ModifiableVehicles.Konig) != 0 && Resources.Load<GameObject>(ecu.PrefabName) == asset);
            var bottleAsset = Resources.Load<GameObject>(manager.NitrousBottle.partInstalled.PrefabName);
            if (alternate == null || bottleAsset == null)
            { Debug.Log("MonMP tune regression SKIP: no alternate canonical compatible ECU/bottle resource."); return; }
            var ecuBackup = SaveAndRemove(manager.ECU);
            stagedEcu = InstallClone(ecuBackup, alternate) as Part_ECU;
            if (stagedEcu == null) throw new InvalidOperationException("Tune regression alternate ECU clone has wrong native type.");
            stagedEcu.IntValue1 = 1051013199; stagedEcu.IntValue2 = 1232515000;
            stagedEcu.RefreshCurve();
            var bottle = InstallClone(SaveAndRemove(manager.NitrousBottle), bottleAsset);
            bottle.Value1 = 37.5f;
            expected = new VehicleTuneState(true, stagedEcu.IntValue1, stagedEcu.IntValue2, true, bottle.Value1);
            stagedPrefab = stagedEcu.PrefabName; guestReady = true;
            Debug.Log("MonMP tune regression guest staged native alternate ECU and valid nondefault tune/bottle charge before world construction.");
        }
        private static SavedSocket SaveAndRemove(Vehicle_PartSocket socket)
        {
            var part = socket.partInstalled;
            var backup = new SavedSocket { Socket = socket, Part = part, Position = part.transform.localPosition,
                Rotation = part.transform.localRotation, Scale = part.transform.localScale };
            foreach (var hole in socket.boltHolesDetails)
                backup.Holes.Add((hole, hole.isBoltInstalled, hole.BoltedPercent, hole.generated_BoltHole?.hardware_Bolt));
            saved.Add(backup);
            storage ??= new GameObject("MonMP owned tune fixture originals");
            storage.SetActive(false);
            socket.RemovePart();
            part.transform.SetParent(storage.transform, true);
            foreach (var hole in backup.Holes)
            {
                if (hole.Installed && hole.Bolt != null) hole.Bolt.transform.SetParent(storage.transform, true);
                hole.Hole.isBoltInstalled = false; hole.Hole.BoltedPercent = 0;
            }
            return backup;
        }
        private static InteractableParts InstallClone(SavedSocket backup, GameObject asset)
        {
            var clone = UnityEngine.Object.Instantiate(asset).GetComponent<InteractableParts>();
            clone.Get_isJustSpawned = true;
            backup.Socket.InstallPart(clone);
            foreach (var hole in backup.Holes)
                if (hole.Installed)
                {
                    var bolt = UnityEngine.Object.Instantiate(Singleton<GameAssets>.i.Hardware_Bolt(hole.Hole.boltSize), hole.Hole.HoleLocation).GetComponent<Hardware_Bolt>();
                    hole.Hole.Attach(bolt, hole.Percent);
                }
            return clone;
        }
        internal static void CheckCapturedAndReplaced(MonMPPlugin plugin)
        {
            if (!guestReady || vehicle == null) return;
            if (!plugin.World!.VehicleTune.TestOriginal(vehicle).Equals(expected))
                throw new InvalidOperationException("Tune regression original was captured after guest ECU/bottle replacement.");
            var manager = vehicle.vehicle_PartsManager;
            if (!manager.ECU.isInstalled || manager.ECU.partInstalled == null || manager.ECU.partInstalled.PrefabName == stagedPrefab)
            { Debug.Log("MonMP tune regression SKIP replacement assertion: host ECU absent or identical to staged alternate prefab."); guestReady = false; return; }
            if (manager.NitrousBottle.isInstalled)
                throw new InvalidOperationException("Tune regression did not receive the host's mismatched bottle absence.");
            checkedReplacement = true;
            Debug.Log("MonMP tune regression observed genuine guest ECU replacement and mismatched bottle presence; pre-world baseline intact.");
        }
        internal static void CheckRestored()
        {
            if (!guestReady || !checkedReplacement || vehicle == null) return;
            var manager = vehicle.vehicle_PartsManager;
            var ecu = manager.ECU.partInstalled as Part_ECU;
            var bottle = manager.NitrousBottle.partInstalled;
            if (!manager.ECU.isInstalled || ecu == null || ecu.PrefabName != stagedPrefab ||
                ecu.IntValue1 != expected.Ecu1 || ecu.IntValue2 != expected.Ecu2 ||
                !manager.NitrousBottle.isInstalled || bottle == null || bottle.Value1 != expected.Charge)
                throw new InvalidOperationException("Tune regression actual disconnect lost original ECU tune or bottle charge after part replacement.");
            Debug.Log("MonMP tune regression PASS exact pre-world ECU tune and bottle charge restored after actual disconnect.");
        }
        internal static void Cleanup()
        {
            foreach (var backup in saved.AsEnumerable().Reverse())
            {
                var current = backup.Socket.partInstalled;
                if (backup.Socket.isInstalled && current != null)
                {
                    backup.Socket.RemovePart();
                    if (current != backup.Part) { current.gameObject.SetActive(false); UnityEngine.Object.Destroy(current.gameObject); }
                }
                foreach (var hole in backup.Holes) hole.Hole.DetachAndDestroy();
                backup.Part.gameObject.SetActive(true);
                backup.Socket.InstallPart(backup.Part);
                backup.Part.transform.localPosition = backup.Position; backup.Part.transform.localRotation = backup.Rotation; backup.Part.transform.localScale = backup.Scale;
                foreach (var hole in backup.Holes)
                {
                    if (hole.Installed && hole.Bolt != null)
                    { hole.Bolt.gameObject.SetActive(true); hole.Hole.Attach(hole.Bolt, hole.Percent); }
                    else
                    {
                        hole.Hole.isBoltInstalled = hole.Installed; hole.Hole.BoltedPercent = hole.Percent;
                        hole.Hole.generated_BoltHole.hardware_Bolt = hole.Bolt!;
                        hole.Hole.generated_BoltHole.boltGameObject = hole.Bolt == null ? null! : hole.Bolt.gameObject;
                    }
                }
            }
            saved.Clear();
            if (storage != null) UnityEngine.Object.Destroy(storage);
            storage = null; vehicle = null; stagedEcu = null; prepared = guestReady = checkedReplacement = false;
        }
    }
}
