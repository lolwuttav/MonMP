using System;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        private static readonly System.Reflection.FieldInfo PickupToolLocations = AccessTools.Field(typeof(PlayerInteractable), "toolLocations");
        private static readonly System.Reflection.FieldInfo PickupSpannerGold = AccessTools.Field(typeof(Tool_AdjustableSpanner), "Is_HugeGoldEdition");
        private Binding? pickupPreview;
        private RemoteHeldVisual? pickupVisual;
        private uint nextPickupNonce;
        private int outstandingPickups;
        private readonly System.Collections.Generic.Dictionary<uint, uint> pickupNonceByPeer = new System.Collections.Generic.Dictionary<uint, uint>();
        private bool NewPickupNonce(uint peer, uint nonce)
        {
            if (pickupNonceByPeer.TryGetValue(peer, out uint prior) && unchecked((int)(nonce - prior)) <= 0) return false;
            pickupNonceByPeer[peer] = nonce; return true;
        }

        private bool BeginPickup(Binding binding, GrabMode mode)
        {
            if (host)
            {
                binding.PendingGrab = mode; binding.PendingSince = Time.unscaledTime; NoteGrabPose(binding);
                return Request(binding, ItemAction.Acquire);
            }
            uint ignored = binding.IgnoredPickupGeneration;
            uint prior = ignored == 1 ? uint.MaxValue : ignored - 1;
            if (!Ready || TransfersBlocked || pickupPreview != null || binding.Pickup != null || outstandingPickups >= 16 ||
                binding.Item == null || !binding.State.Exists || binding.Absorbed || binding.Releasing ||
                binding.State.Lease.Owner != ItemLease.Unowned ||
                ignored != 0 && (binding.State.Lease.Generation == ignored || binding.State.Lease.Generation == prior) ||
                gameplay.Player.IsHoldingAnObject ||
                !CanHandleItems(gameplay.Get_PlayerState))
            { return false; }
            uint nonce = ++nextPickupNonce;
            binding.IgnoredPickupGeneration = 0;
            if (nonce == 0) nonce = ++nextPickupNonce;
            binding.Pickup = new ItemPickupClaim(session!.LocalPeerId, nonce, binding.State.Lease.Generation, Time.unscaledTime);
            binding.PickupPresentation = mode == GrabMode.Hand ? Integration.LocalInteraction.CapturePickup(binding.Item.gameObject.GetInstanceID()) : default;
            outstandingPickups++; pickupPreview = binding;
            binding.PendingGrab = mode; binding.PendingSince = Time.unscaledTime;
            NoteGrabPose(binding);
            try
            {
                channel!.Request(binding.Id, new ItemPickupRequest(nonce, binding.Pickup.Generation, ItemPickupAction.Acquire).Encode(), true);
                PresentPickup();
                return true;
            }
            catch
            {
                // The enqueue may have succeeded before a later presentation failed.
                // Retain the ticket as cancelled so any late grant gets released.
                CancelPickup(binding); throw;
            }
        }
        private void HidePickupPreview(Binding binding)
        {
            if (pickupPreview != binding) return;
            pickupPreview = null;
            ResetPickupVisual();
        }
        private void ResetPickupVisual()
        {
            try { pickupVisual?.Reset(); }
            catch (Exception ex) { Debug.LogWarning("MonMP pickup preview cleanup: " + ex.Message); }
        }
        private void EndPickup(Binding binding, bool clearMode)
        {
            HidePickupPreview(binding);
            if (clearMode) binding.PickupPresentation = default;
            if (binding.Pickup != null) { binding.Pickup = null; outstandingPickups--; }
            if (clearMode)
            { binding.PendingGrab = GrabMode.None; binding.PendingTool = null; binding.PendingUse = false; binding.GrabLocalPosition = null; }
        }
        private void CancelPickup(Binding binding)
        {
            var claim = binding.Pickup;
            if (claim == null) return;
            claim.Cancelled = true;
            binding.IgnoredPickupGeneration = claim.GrantedGeneration;
            HidePickupPreview(binding);
            binding.PendingGrab = GrabMode.None; binding.PendingTool = null; binding.PendingUse = false;
            binding.GrabLocalPosition = null;
            SendPickupCancellation(binding, Time.unscaledTime);
        }
        private void SendPickupCancellation(Binding binding, float now)
        {
            var claim = binding.Pickup;
            if (claim == null || !claim.Cancelled || !Ready || now < claim.NextCancel) return;
            claim.NextCancel = now + ItemPickupClaim.RetrySeconds;
            channel!.Request(binding.Id, new ItemPickupRequest(claim.Nonce, claim.Generation, ItemPickupAction.Cancel).Encode(), true);
        }
        private bool PickupReply(ulong id, byte[] payload)
        {
            if (!ItemPickupReply.Is(payload)) return false;
            var reply = ItemPickupReply.Decode(payload);
            if (!bindings.TryGetValue(id, out var binding) || binding.Pickup is not { } claim || !claim.Matches(reply)) return true;
            if (reply.Outcome == ItemPickupOutcome.Granted) return true; // The canonical state still grants the real hold.
            if (claim.Cancelled && reply.Outcome == ItemPickupOutcome.Refused) return true;
            bool refused = !claim.Cancelled && reply.Outcome == ItemPickupOutcome.Refused;
            if (claim.Cancelled) binding.IgnoredPickupGeneration = claim.GrantedGeneration;
            if (reply.Outcome == ItemPickupOutcome.Unclaimed) binding.IgnoredPickupGeneration = 0;
            EndPickup(binding, true);
            if (refused) MonMPPlugin.Instance?.ReportItemUseUnavailable("That item could not be picked up.");
            return true;
        }
        // Called before applying a record, including tombstones. Cosmetic state
        // never becomes ownership and never masks another player's winning lease.
        private bool ObservePickup(Binding binding, ItemState state)
        {
            if (host) return false;
            var claim = binding.Pickup;
            if (claim != null)
            {
                if (claim.Owns(state.Lease) && state.Exists)
                {
                    if (claim.Cancelled)
                    { binding.Releasing = true; SendPickupCancellation(binding, Time.unscaledTime); return true; }
                    EndPickup(binding, false);
                }
                else if (claim.Lost(state))
                {
                    bool lostRace = !claim.Cancelled && state.Exists && state.Lease.Owner != ItemLease.Unowned && state.Lease.Owner != session!.LocalPeerId;
                    EndPickup(binding, true);
                    if (lostRace) MonMPPlugin.Instance?.ReportItemUseUnavailable("Another player picked up that item.");
                }
            }
            bool cancelledGrant = state.Exists && state.Lease.Owner == session!.LocalPeerId &&
                binding.IgnoredPickupGeneration != 0 && state.Lease.Generation == binding.IgnoredPickupGeneration;
            if (cancelledGrant) binding.Releasing = true;
            else if (binding.IgnoredPickupGeneration != 0 && state.Lease.Owner == ItemLease.Unowned &&
                state.Lease.Generation == ItemPickupRequest.GrantedGeneration(binding.IgnoredPickupGeneration)) binding.Releasing = false;
            return cancelledGrant;
        }
        private void TickPickup(Binding binding, float now)
        {
            var claim = binding.Pickup;
            if (claim == null) return;
            if (!claim.Cancelled && (now < claim.StartedAt || now - claim.StartedAt >= ItemPickupClaim.Timeout)) CancelPickup(binding);
            else if (claim.Cancelled) SendPickupCancellation(binding, now);
        }
        private void PresentPickup()
        {
            var binding = pickupPreview;
            if (binding?.Pickup == null) return;
            if (!Ready || TransfersBlocked || binding.Item == null || !binding.Item.gameObject.activeInHierarchy ||
                !CanHandleItems(gameplay.Get_PlayerState))
            { CancelPickup(binding); return; }
            int intent = binding.PickupPresentation.Intent;
            if (intent < 0 || (intent == 0 && gameplay.Player.IsHoldingAnObject)) { CancelPickup(binding); return; }
            if (intent > 0) return; // Keep the real object at its authoritative position until the grant.
            var view = gameplay.Player.transform;
            if (!binding.Item.canGrab()) { ResetPickupVisual(); return; }
            var position = view.TransformPoint(binding.GrabLocalPosition ?? view.InverseTransformPoint(binding.Item.transform.position));
            var rotation = view.rotation * binding.GrabLocalRotation;
            var scale = binding.Item.transform.lossyScale;
            bool equipped = binding.PendingGrab == GrabMode.Equip;
            if (equipped && binding.Item is InteractableTools tool)
            {
                Transform? location = null;
                if (tool.toolsType == ToolsType.HoistPuller) location = gameplay.Player.Tool_HoistPuller_Location;
                else if (PickupToolLocations.GetValue(gameplay.Player) is InteractableToolLocation[] locations)
                {
                    int seen = 0;
                    foreach (var candidate in locations)
                    {
                        if (candidate.toolsType != tool.toolsType || candidate.Location == null) continue;
                        if (tool.toolsType == ToolsType.AdjustableSpanner && tool.IntValue1_Max == 1 && seen++ == 0) continue;
                        location = candidate.Location; break;
                    }
                }
                if (location != null)
                { position = view.TransformPoint(location.localPosition); rotation = view.rotation * location.localRotation; }
                float factor = tool is Tool_Chainsaw || tool is Tool_DirtShovel || tool is Tool_MapTorch ? .3f :
                    tool is Tool_HarvestBasket ? .5f : tool is Tool_TubingRoll ? .18f :
                    tool is Tool_AdjustableSpanner && PickupSpannerGold.GetValue(tool) is false ? .6f : 1f;
                if (factor != 1f) scale = view.lossyScale * factor;
            }
            pickupVisual ??= new RemoteHeldVisual(MonMPPlugin.Instance!.HeldGrips);
            pickupVisual.TryPresentLocal(binding.Item, position, rotation, scale, view, equipped);
        }
        internal bool PendingPickupInput(string method)
        {
            var binding = pickupPreview;
            if (!GuestRunning || binding?.Pickup == null || binding.Pickup.Cancelled) return false;
            // A VR grip owns this ticket. Clicking with the other hand still
            // operates its equipped tool and cannot edit this pending purchase.
            if (binding.Item != null && binding.PickupPresentation.Intent > 0) return false;
            // Menu clicks must not fall through to the still-canonical world item.
            if (Cursor.lockState != CursorLockMode.Locked) return true;
            if (method == "MouseLeftClickDown" || method == "MouseRightClickDown")
            {
                if (binding.PendingGrab == GrabMode.Hand) CancelPickup(binding);
            }
            else if (method == "UseKeyDown")
            {
                if (binding.Item is InteractableTools tool && tool.canEquip())
                { binding.PendingGrab = binding.PendingGrab == GrabMode.Equip ? GrabMode.Hand : GrabMode.Equip; PresentPickup(); }
                else binding.PendingUse = true;
            }
            return true;
        }
        private void PickupCommand(uint peer, ulong id, ItemPickupRequest request)
        {
            ItemPickupOutcome outcome = ItemPickupOutcome.Refused;
            try
            {
                if (!bindings.TryGetValue(id, out var binding)) return;
                var prior = binding.Pickup;
                if (request.Action == ItemPickupAction.Cancel)
                {
                    // A cancellation can also fence a request not admitted yet.
                    // Older cancels still release their exact active ticket below.
                    NewPickupNonce(peer, request.Nonce);
                    if (prior != null && prior.Matches(peer, request))
                    {
                        prior.Cancelled = true;
                        if (prior.Owns(leases[id]))
                            Command(peer, id, new ItemCommand(ItemAction.Drop, prior.GrantedGeneration, WorldPose(binding.State)).Encode());
                        outcome = prior.Owns(leases[id]) ? ItemPickupOutcome.Refused : ItemPickupOutcome.Cancelled;
                    }
                    else outcome = ItemPickupOutcome.Unclaimed;
                    return;
                }
                if (prior != null && prior.Matches(peer, request))
                { outcome = !prior.Cancelled && prior.Owns(leases[id]) ? ItemPickupOutcome.Granted : ItemPickupOutcome.Refused; return; }
                if (!NewPickupNonce(peer, request.Nonce)) return;
                var before = leases[id];
                if (before.Owner != ItemLease.Unowned || before.Generation != request.Generation) return;
                // Preserve every native/lease/range/save guard in the existing command.
                Command(peer, id, new ItemCommand(ItemAction.Acquire, request.Generation, WorldPose(binding.State)).Encode());
                var claim = new ItemPickupClaim(peer, request.Nonce, request.Generation, Time.unscaledTime);
                if (!claim.Owns(leases[id])) return;
                binding.Pickup = claim; outcome = ItemPickupOutcome.Granted;
            }
            finally { channel!.Reply(peer, id, new ItemPickupReply(request.Nonce, request.Generation, outcome).Encode()); }
        }
        private void ClearPickupPreview()
        {
            pickupPreview = null;
            ResetPickupVisual(); pickupVisual = null; outstandingPickups = 0;
            foreach (var binding in bindings.Values) { binding.Pickup = null; binding.PickupPresentation = default; binding.IgnoredPickupGeneration = 0; }
            pickupNonceByPeer.Clear();
        }
    }
}
