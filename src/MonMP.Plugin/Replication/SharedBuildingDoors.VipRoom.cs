using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedBuildingDoors
    {
        private static readonly FieldInfo VipPriceField = AccessTools.Field(typeof(Furniture_DoorHandle_VIPRoom), "UNLOCKPRICE")
            ?? throw new InvalidOperationException("Unsupported native VIP room unlock price.");
        private readonly VipRoomUnlockLedger vipUnlocks = new VipRoomUnlockLedger();
        private readonly Dictionary<Guid, (ulong Entity, float Deadline)> pendingVipUnlocks = new Dictionary<Guid, (ulong, float)>();
        private readonly Dictionary<Guid, (ulong Entity, float Deadline)> delayedVipUnlocks = new Dictionary<Guid, (ulong, float)>();
        private bool executingVipUnlock, uncertainVipUnlock;
        internal bool NewPurchasesBlocked { get; set; }
        // This native purchase is synchronous. An uncertain completed result prevents another
        // purchase, but has no coroutine/output left to settle; saving its observed cash/progress
        // remains safe. A guest still waits while its command lacks any host reply.
        internal bool TransactionsSettled => pendingVipUnlocks.Count == 0 &&
            !vipUnlocks.Executing;
        internal bool IsExecutingGuestVipUnlock => executingVipUnlock;

#if MONMP_DEVELOPMENT
        internal int TestVipNativePurchases { get; private set; }


        internal VipRoomUnlockReply? TestVipLastReply { get; private set; }
#endif


        private static float VipPrice(Furniture_DoorHandle_VIPRoom handle)
        {
            if (VipPriceField.FieldType != typeof(float)) throw new InvalidOperationException("Unsupported native VIP room price field type.");
            float price = (float)VipPriceField.GetValue(handle)!;
            if (float.IsNaN(price) || float.IsInfinity(price) || price < 0 || price > 10_000_000)
                throw new InvalidOperationException("Invalid native VIP room unlock price.");
            return price;
        }

        private void RouteVipUnlock(Binding binding, Furniture_DoorHandle_VIPRoom handle)
        {
            if (channel?.FullySynchronized != true)
            { ReportVip("Wait for the shared world before unlocking the VIP room."); return; }
            if (NewPurchasesBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            { ReportVip("Wait for the multiplayer save to finish before unlocking the VIP room."); return; }
            if (!TransactionsSettled || uncertainVipUnlock || delayedVipUnlocks.Count != 0)
            { ReportVip("Wait for the VIP room purchase confirmation before trying again."); return; }
            int handleIndex = Array.IndexOf(binding.Handles, handle);
            if (handleIndex < 0) return;
            var request = new VipRoomUnlockRequest(Guid.NewGuid(), (byte)handleIndex,
                (bool)OpenField.GetValue(binding.Door)!, VipPrice(handle));
            pendingVipUnlocks.Add(request.Operation, (binding.Id, Time.unscaledTime + 10f));
            channel.Request(binding.Id, request.Encode());
        }

        private void VipRejected(uint peer, ulong entity, byte[] payload)
        { if (VipRoomUnlockRequest.Is(payload)) VipCommand(peer, entity, payload, true); }

        private void VipCommand(uint peer, ulong entity, byte[] payload, bool stale)
        {
            if (!host || session?.IsHost != true || peer == 0 || channel == null ||
                !session.Peers.TryGetValue(peer, out var buyer)) return;
            VipRoomUnlockRequest request;
            try { request = VipRoomUnlockRequest.Decode(payload); }
            catch (System.IO.InvalidDataException error) { Debug.LogWarning("MonMP invalid VIP request: " + error.Message); return; }
            if (!bindings.TryGetValue(entity, out var binding) || binding.Door == null)
            {
                channel.Reply(peer, entity, new VipRoomUnlockReply(request.Operation, VipRoomUnlockStatus.Unavailable).Encode());
                return;
            }
            VipRoomUnlockReply reply;
            try
            {
                reply = vipUnlocks.Execute(buyer.ProfileId, entity, request, () => ExecuteVipUnlock(peer, binding, request, stale));
            }
            catch (Exception ex)
            {
                uncertainVipUnlock = true;
                reply = new VipRoomUnlockReply(request.Operation, VipRoomUnlockStatus.Uncertain);
                Debug.LogException(ex);
            }

#if MONMP_DEVELOPMENT
            TestVipLastReply = reply;
#endif

            if (session.Peers.TryGetValue(peer, out var connected) && connected.ProfileId == buyer.ProfileId)
                channel.Reply(peer, entity, reply.Encode());
            if (binding.Door != null) channel.Publish(binding.Id, Capture(binding.Door).Encode());
        }

        private (VipRoomUnlockStatus Status, int SpentCents) ExecuteVipUnlock(uint peer, Binding binding, VipRoomUnlockRequest request, bool stale)
        {
            if (request.Handle >= binding.Handles.Length || binding.Handles[request.Handle] is not Furniture_DoorHandle_VIPRoom handle || handle == null)
                return (VipRoomUnlockStatus.Unavailable, 0);
            bool inRange = presence.TryGetValue(peer, out var observed) && Time.unscaledTime - observed.Time <= 2f &&
                (handle.transform.position - new Vector3(observed.Pose.X, observed.Pose.Y, observed.Pose.Z)).sqrMagnitude <= CommandRange * CommandRange;
            var cash = Singleton<Cash_Manager>.i;
            var todo = Singleton<ToDoList_Manager>.i;
            float price = VipPrice(handle);
            bool unlockedBefore = todo.isDone(ToDoList_Line.DecorationStoreVIPRoom);
            var before = Capture(binding.Door);
            var decision = VipRoomUnlockPolicy.Validate(request.Price, price,
                NewPurchasesBlocked || uncertainVipUnlock || MonMPPlugin.Instance?.World?.SaveGate.Active == true,
                handle.isActiveAndEnabled && handle.gameObject.activeInHierarchy, unlockedBefore, inRange,
                LockedFor(binding.Door), !stale && request.ObservedOpen == before.Open, cash.isCanAfford(price));
            if (decision != VipRoomUnlockStatus.Unlocked) return (decision, 0);

            int cashBefore = cash.GetCash;
            float spentBefore = GameManager.Get_PlayerStats[4];
            int priceCents = Mathf.RoundToInt(price * 100f);
            int CommittedSpent() => Math.Min(priceCents, Math.Max(0, cashBefore - cash.GetCash));
            executingVipUnlock = true;
            try
            {
                // The host's native body completes progression, spends the current price, and calls
                // the ordinary door toggle. No guest-local purchase or fabricated item lease runs.
                using (Patches.RemoteActionScope.Enter())
                using (SpatialSounds.AtPeer(peer)) handle.MouseDown();
                var after = Capture(binding.Door);
                if (todo.isDone(ToDoList_Line.DecorationStoreVIPRoom) && cash.GetCash == cashBefore - Mathf.RoundToInt(price * 100f) &&
                    after.Open != before.Open && after.Locked == before.Locked)
                {

#if MONMP_DEVELOPMENT
                    TestVipNativePurchases++;
#endif

                    return (VipRoomUnlockStatus.Unlocked, CommittedSpent());
                }
                if (!todo.isDone(ToDoList_Line.DecorationStoreVIPRoom) && cash.GetCash == cashBefore && after.Equals(before))
                    return (VipRoomUnlockStatus.Unavailable, 0);
                uncertainVipUnlock = true;
                return (VipRoomUnlockStatus.Uncertain, CommittedSpent());
            }
            catch (Exception ex)
            {
                uncertainVipUnlock = true;
                Debug.LogException(ex);
                return (VipRoomUnlockStatus.Uncertain, CommittedSpent());
            }
            finally
            {
                // A native UI exception can occur between debit and its counter line.
                // Keep that committed spending in the shared total without removing other actions.
                GameManager.Get_PlayerStats[4] = Math.Max(GameManager.Get_PlayerStats[4], spentBefore + CommittedSpent());
                executingVipUnlock = false;
            }
        }

        private void VipReply(ulong entity, byte[] payload)
        {
            if (host || !Active) return;
            // Route by the requested entity before decoding a shared reply callback. Retain
            // late purchase receipts even when the native door binding has disappeared.
            if (!pendingVipUnlocks.Values.Any(value => value.Entity == entity) &&
                !delayedVipUnlocks.Values.Any(value => value.Entity == entity)) return;
            var reply = VipRoomUnlockReply.Decode(payload);
            bool pending = pendingVipUnlocks.TryGetValue(reply.Operation, out var waiting) && waiting.Entity == entity;
            bool delayed = delayedVipUnlocks.TryGetValue(reply.Operation, out var retained) && retained.Entity == entity;
            if (!pending && !delayed) return;
            pendingVipUnlocks.Remove(reply.Operation); delayedVipUnlocks.Remove(reply.Operation);

#if MONMP_DEVELOPMENT
            TestVipLastReply = reply;
#endif

            // SharedStatistics supplies the committed spending total; receipt replay
            // must not increment that total on this guest a second time.
            switch (reply.Status)
            {
                // Cash/register sound, checklist popup and door animation arrive through their existing
                // replicated records/cues. Repeating them here would play successful feedback twice.
                case VipRoomUnlockStatus.Unlocked:
                    Singleton<Steam_AchievementManager>.i?.AchievementDone_ToDoList(ToDoList_Line.DecorationStoreVIPRoom);
                    return;
                case VipRoomUnlockStatus.AlreadyUnlocked: return;
                case VipRoomUnlockStatus.Funds:
                    Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_NotEnoughCash"));
                    Singleton<SOUND_Manager>.i?.PlaySound_DoorLocked();
                    return;
                case VipRoomUnlockStatus.DoorLocked:
                    Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_DoorLocked"));
                    if (bindings.TryGetValue(entity, out var binding) && binding.Door != null)
                        binding.Door.GetComponent<SOUND_Furniture_Door>()?.Locked();
                    return;
                case VipRoomUnlockStatus.Uncertain: uncertainVipUnlock = true; break;
            }
            ReportVip(reply.Status switch
            {
                VipRoomUnlockStatus.PriceChanged => "The VIP room price changed. Check the handle and try again.",
                VipRoomUnlockStatus.TooFar => "Move closer to the VIP room handle before unlocking it.",
                VipRoomUnlockStatus.Busy => "Wait for the multiplayer save or pending VIP room purchase to finish.",
                VipRoomUnlockStatus.Uncertain => "VIP room purchase outcome is uncertain. Check shared cash and progression before retrying.",
                VipRoomUnlockStatus.Capacity => "This session cannot accept more VIP room purchase requests.",
                VipRoomUnlockStatus.Stale => "The VIP room door changed. Check the handle and try again.",
                _ => "The VIP room unlock is not currently available."
            });
        }

        private static void ReportVip(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);
        private void ExpireVipUnlocks()
        {
            if (host || !Active) return;
            foreach (var pending in pendingVipUnlocks.Where(value => Time.unscaledTime >= value.Value.Deadline).ToArray())
            {
                pendingVipUnlocks.Remove(pending.Key); delayedVipUnlocks[pending.Key] = (pending.Value.Entity, Time.unscaledTime + 120f);
                ReportVip("VIP room confirmation is delayed. Check shared cash and the room before retrying.");
            }
            foreach (var expired in delayedVipUnlocks.Where(value => Time.unscaledTime >= value.Value.Deadline).Select(value => value.Key).ToArray())
                delayedVipUnlocks.Remove(expired);
        }
        private void ResetVipUnlocks()
        { pendingVipUnlocks.Clear(); delayedVipUnlocks.Clear(); }
    }
}
