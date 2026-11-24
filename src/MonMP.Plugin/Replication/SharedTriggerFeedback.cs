using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Trigger inputs still mutate only on the host. These small, host-authenticated replies
    // carry their native refusal/progress UI to the player who holds the input item.
    // A loose input goes to the nearest present player; no gameplay ownership changes here.
    internal sealed class SharedTriggerFeedback : IDisposable
    {
        private enum Kind : byte { CrimperPower = 1, CrimperSyrup, CrimperLids, CrimperRemoveCan, CompostFull, CompostClosed, DealerCount, DealerFull }
        private static readonly FieldInfo CrimperShack = Required(typeof(SugarShack_CrimpingMachine), "sugarShack");
        private static readonly FieldInfo CrimperWorking = Required(typeof(SugarShack_CrimpingMachine), "isWorking");
        private static readonly FieldInfo CrimperLastCan = Required(typeof(SugarShack_CrimpingMachine), "LastCastSpawned");
        private static readonly FieldInfo CrimperPlateau = Required(typeof(SugarShack_CrimpingMachine), "UpDownPlateau");
        private static readonly FieldInfo CompostLid = Required(typeof(Bunker_CompostMachine), "CompostLid");
        private static readonly FieldInfo DealerHuge = Required(typeof(Store_Dealer_WeedReception), "isHugePack");
        private static readonly FieldInfo DealerAdded = Required(typeof(Store_Dealer_WeedReception), "LastTimeBigPackAdded");
        private static FieldInfo Required(Type type, string name) => AccessTools.Field(type, name) ?? throw new InvalidOperationException("Missing trigger feedback field: " + type.Name + "." + name);
        private readonly bool host;
        private readonly SharedItems items;
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<(ulong, Kind), float> recent = new Dictionary<(ulong, Kind), float>();
        private NetworkSession? session;
        private WorldChannel? channel;

        public SharedTriggerFeedback(bool host, SharedItems items) { this.host = host; this.items = items; }
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += Pose; session.PeerLeft += Left; channel.ReplyReceived += Reply;
        }
        private void Pose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void Left(uint peer) => presence.Remove(peer);

        // True means an unchanged remote input was refused and its native host popup is replaced.
        internal bool Crimper(SugarShack_CrimpingMachine machine, Collider other)
        {
            var item = other == null ? null : other.GetComponent<Item_MapleSyrupCan_Empty>();
            if (!host || item == null || (bool)CrimperWorking.GetValue(machine)) return false;
            var shack = CrimperShack.GetValue(machine) as SugarShack;
            if (shack == null) return false;
            Kind? kind = null;
            if (!shack.Get_HomeGenerator.Get_isEngineRunning) kind = Kind.CrimperPower;
            else if (shack.Get_SyrupLevel < 1) kind = Kind.CrimperSyrup;
            else if (shack.Get_CrimpingMachine_LidsAmount < 1) kind = Kind.CrimperLids;
            else if (CrimperLastCan.GetValue(machine) is Item_MapleSyrupCan can && can != null &&
                can.transform.parent == (CrimperPlateau.GetValue(machine) as Transform)) kind = Kind.CrimperRemoveCan;
            return kind.HasValue && Report(item, machine.transform.position, kind.Value);
        }

        internal bool Compost(Bunker_CompostMachine machine, Collider other)
        {
            var item = other == null ? null : other.GetComponent<InteractableItems>();
            if (!host || item == null) return false;
            Kind? kind = null;
            if (machine.Get_Compost >= machine.Capacity) kind = Kind.CompostFull;
            else if ((item is Tool_Seeds seeds && seeds.Get_SeedsType == SeedsType.Potato || item is Tool_HarvestBasket ||
                item is Garden_TreeCuttings || item.CanBeComposted) &&
                CompostLid.GetValue(machine) is Furniture_Door lid && lid != null && !lid.Get_IsOpen) kind = Kind.CompostClosed;
            return kind.HasValue && Report(item, machine.transform.position, kind.Value);
        }

        internal bool Dealer(Store_Dealer_WeedReception reception, InteractableItems item)
        {
            if (!host || !(item is Item_WeedPreRollBigPack) || !(bool)DealerHuge.GetValue(reception) ||
                (float)DealerAdded.GetValue(reception) + 1f >= Time.timeSinceLevelLoad) return false;
            int count = 0;
            foreach (var collider in Physics.OverlapSphere(reception.transform.position, 1.65f))
                if (collider.GetComponent<Item_WeedPreRollBigPack>() != null) count++;
            if (count < 27) return Report(item, reception.transform.position, Kind.DealerCount, (byte)count);
            return reception.storeDealer != null && reception.storeDealer.isHugePaletteFull &&
                Report(item, reception.transform.position, Kind.DealerFull);
        }

        private bool Report(InteractableItems item, Vector3 at, Kind kind, byte count = 0)
        {
            if (session?.IsRunning != true || channel?.Ready != true || !items.Handles(item)) return false;
            ulong id = items.EntityOf(item);
            var owner = items.LeaseOwnerOf(item);
            if (!host || !owner.HasValue || owner == 0) return false;
            uint recipient = owner.Value;
            if (recipient == ItemLease.Unowned)
            {
                recipient = 0;
                var player = Singleton<Gameplay>.i?.Player;
                float closest = player == null ? 25f : Mathf.Min(25f, (player.transform.position - at).sqrMagnitude);
                foreach (var candidate in presence)
                {
                    if (!Fresh(candidate.Key, at, out float distance) || distance >= closest) continue;
                    closest = distance; recipient = candidate.Key;
                }
            }
            if (recipient == 0 || !Fresh(recipient, at, out _)) return false;
            if (Take(id, kind)) channel.Reply(recipient, id, new byte[] { 0xFA, 0xC7, (byte)kind, count });
            return true;
        }

        private bool Fresh(uint peer, Vector3 at, out float distance)
        {
            distance = float.PositiveInfinity;
            if (session?.Peers.ContainsKey(peer) != true || !presence.TryGetValue(peer, out var seen) ||
                Time.unscaledTime - seen.Time < 0f || Time.unscaledTime - seen.Time > 2f ||
                seen.Pose.Spectating || seen.Pose.Rest != PlayerRest.None) return false;
            distance = (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - at).sqrMagnitude;
            return distance <= 25f;
        }

        private bool Take(ulong id, Kind kind)
        {
            float now = Time.unscaledTime;
            var key = (id, kind);
            if (recent.TryGetValue(key, out float last) && now >= last && now - last < 1f) return false;
            if (recent.Count >= 256) recent.Clear();
            recent[key] = now; return true;
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (host || channel == null || payload.Length != 4 || payload[0] != 0xFA || payload[1] != 0xC7 ||
                !channel.Records.TryGetValue(id, out var record) || record.Adapter != ItemState.Adapter) return;
            var kind = (Kind)payload[2];
            if (payload[2] < (byte)Kind.CrimperPower || payload[2] > (byte)Kind.DealerFull ||
                (kind == Kind.DealerCount ? payload[3] >= 27 : payload[3] != 0)) return;
            if (Take(id, kind)) Show(kind, payload[3]);
        }
        private static void Show(Kind kind, byte count)
        {
            if (kind == Kind.DealerCount)
            { Singleton<UI_EventMessage>.i?.Show(count + " / 27", TEXT.GetText("Dealers_Name"), 3f, UI_EventMessage.EventIconType.Talk); return; }
            string key = kind switch
            {
                Kind.CrimperPower => "ERROR_NoElectricity",
                Kind.CrimperSyrup => "ERROR_SugarShack_CrimpingMachine_NoSyrup",
                Kind.CrimperLids => "ERROR_SugarShack_CrimpingMachine_NoLids",
                Kind.CrimperRemoveCan => "ERROR_SugarShack_CrimpingMachine_RemoveCan",
                Kind.CompostFull => "BUNKER_CompostMachine_CompostFull",
                Kind.CompostClosed => "BUNKER_CompostMachine_TheLidIsClose",
                _ => "ERROR_Dealer_PalletIsFull"
            };
            Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText(key));
        }
        public void Dispose()
        {
            if (session != null) { session.PoseReceived -= Pose; session.PeerLeft -= Left; }
            if (channel != null) channel.ReplyReceived -= Reply;
            session = null; channel = null; presence.Clear(); recent.Clear();
        }
    }
}
