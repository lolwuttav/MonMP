using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.World;
using UnityEngine;
using UnityEngine.UI;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedPortableConsoleCharger
    {
        private readonly PortableConsoleSaveBook consoleSaves = new PortableConsoleSaveBook();
        internal bool ConsoleSaveInProgress => consoleSaves.Active || localConsoleSave != Guid.Empty || completedConsoleSave != Guid.Empty;
        private Guid localConsoleSave;
        private Guid completedConsoleSave;
        private float completedConsoleSaveAt;
        private ulong consoleSaveEntity;
        private int localConsoleTicks;
        private bool consoleApproved, consoleRejected, consoleSaveRequested;
        private ulong ConsoleSaveEntity => consoleSaveEntity != 0 ? consoleSaveEntity :
            consoleSaveEntity = chargersById.Keys.OrderBy(id => id).FirstOrDefault();
        private bool ConsoleSaveAvailable => Ready && !NewActionsBlocked &&
            MonMPPlugin.Instance?.World?.SaveGate.Active != true &&
            MonMPPlugin.Instance?.World?.Clock.FastForwarding != true && !SharedSleep.AdvancingWorldTime &&
            MonMPPlugin.Instance?.World?.SceneFurniture.ComputerUser == ItemLease.Unowned &&
            state.Location == PortableConsoleLocation.Hand && state.BatteryLife > 0;

        internal IEnumerator CoordinateConsoleSave(Game_Default_Menu menu, IEnumerator native)
        {
            var operation = Guid.NewGuid();
            bool started = false, completed = false;
            var needs = Singleton<BasicNeeds_Manager>.i;
            var ticks = Singleton<TickSystem>.i;
            bool previousNeeds = needs.Get_isSaving, previousSaving = ticks.isSaving;
            try
            {
                if (!ConsoleSaveAvailable || localConsoleSave != Guid.Empty || completedConsoleSave != Guid.Empty || ConsoleSaveEntity == 0 ||
                    host && !consoleSaves.Begin(session!.LocalPeerId, operation, Time.unscaledTime))
                { Report("The handheld console is busy or unavailable. Wait and try again."); yield break; }
                localConsoleSave = operation; localConsoleTicks = 0; consoleRejected = consoleSaveRequested = false;
                consoleApproved = host;
                if (!host)
                {
                    SendConsoleSave(PortableConsoleSaveAction.Begin);
                    float deadline = Time.unscaledTime + 8f;
                    while (Running && !consoleApproved && !consoleRejected && Time.unscaledTime < deadline) yield return null;
                    if (!Running || !consoleApproved || consoleRejected)
                    { Report("The host could not start the handheld save. Try again when the shared world is ready."); yield break; }
                }
                started = true;
                while (Running && !consoleRejected && (!host || ConsoleSaveAvailable && consoleSaves.Matches(session!.LocalPeerId, operation)))
                {
                    if (!native.MoveNext()) { completed = true; yield break; }
                    yield return native.Current;
                }
            }
            finally
            {
                (native as IDisposable)?.Dispose();
                if (localConsoleSave == operation)
                {
                    if (host) consoleSaves.End(session?.LocalPeerId ?? 0, operation);
                    else if (Running && !consoleSaveRequested) SendConsoleSave(PortableConsoleSaveAction.Cancel);
                    localConsoleSave = Guid.Empty;
                    if (started && !completed)
                    {
                        if (needs != null) needs.Get_isSaving = previousNeeds;
                        if (ticks != null) ticks.isSaving = previousSaving;
                        if (menu != null && AccessTools.Field(typeof(Game_Default_Menu), "Image_SavingGame").GetValue(menu) is Image image)
                            image.gameObject.SetActive(false);
                        if (hands != null && hands.Get_isGameConsoleOpen && gameplay != null && !gameplay.Get_IsDead && !gameplay.Get_IsSleeping)
                        {
                            hands.GameConsole_Close(false);
                            // Native close enables Player. A different input owner can still
                            // hold it when this save is cancelled, especially during a skip.
                            if (gameplay.Get_isGamePaused || MonMPPlugin.Instance?.Spectating == true ||
                                MonMPPlugin.Instance?.World?.Clock.FastForwarding == true)
                                Singleton<GameAssets>.i.inputs.Player.Disable();
                        }
                    }
                }
            }
        }

        internal bool AllowConsoleSaveTick()
        {
            if (localConsoleSave == Guid.Empty) return true;
            localConsoleTicks++;
            if (host)
            { consoleSaves.Advance(session!.LocalPeerId, localConsoleSave, localConsoleTicks, Time.unscaledTime); return true; }
            SendConsoleSave(PortableConsoleSaveAction.Tick, localConsoleTicks); return false;
        }

        internal bool NativeConsoleSave(House_Location location)
        {
            if (host || !Running || location != House_Location.GameConsole) return false;
            if (localConsoleSave != Guid.Empty && consoleApproved && !consoleRejected && localConsoleTicks >= 120)
            {
                consoleSaveRequested = true; completedConsoleSave = localConsoleSave; completedConsoleSaveAt = Time.unscaledTime;
                SendConsoleSave(PortableConsoleSaveAction.Complete, localConsoleTicks);
            }
            else Report("The handheld save did not finish; no shared save was requested.");
            return true;
        }

        private void SendConsoleSave(PortableConsoleSaveAction action, int count = 0)
        {
            if (channel?.Ready == true && localConsoleSave != Guid.Empty && ConsoleSaveEntity != 0)
                channel.Request(ConsoleSaveEntity, new PortableConsoleSaveRequest(localConsoleSave, action, count).Encode(), true);
        }

        private bool ConsoleSaveReply(byte[] payload)
        {
            if (host || !PortableConsoleSaveRequest.Is(payload)) return false;
            var reply = PortableConsoleSaveRequest.Decode(payload);
            if (reply.Operation != localConsoleSave && reply.Operation != completedConsoleSave) return true;
            if (reply.Operation == completedConsoleSave) completedConsoleSave = Guid.Empty;
            if (reply.Action == PortableConsoleSaveAction.Approved) consoleApproved = true;
            else if (reply.Action == PortableConsoleSaveAction.Rejected)
            { consoleRejected = true; Report("The host refused the handheld save; check the console battery and shared world."); }
            return true;
        }

        private void ConsoleSaveCommand(uint peer, ulong entity, byte[] payload)
        {
            if (!host || !Running || peer == 0 || !session!.Peers.ContainsKey(peer) || entity != ConsoleSaveEntity) return;
            PortableConsoleSaveRequest request;
            try { request = PortableConsoleSaveRequest.Decode(payload); } catch (InvalidDataException) { return; }
            bool match = consoleSaves.Matches(peer, request.Operation);
            if (request.Action == PortableConsoleSaveAction.Cancel) { consoleSaves.End(peer, request.Operation); return; }
            bool poseReady = TryPosition(peer, out _, out bool working) && working && presence.TryGetValue(peer, out var pose) &&
                (pose.Pose.State == PlayerPose.WalkingState || pose.Pose.State == ItemInteractionPosePolicy.ChairState) && !items.PeerHolding(peer);
            bool eligible = ConsoleSaveAvailable && poseReady && (presence[peer].Pose.Hands & PlayerHands.Console) != 0;
            if (request.Action == PortableConsoleSaveAction.Begin)
            {
                bool accepted = eligible && TakeBudget(peer) && consoleSaves.Begin(peer, request.Operation, Time.unscaledTime);
                channel!.Reply(peer, entity, new PortableConsoleSaveRequest(request.Operation,
                    accepted ? PortableConsoleSaveAction.Approved : PortableConsoleSaveAction.Rejected).Encode());
                return;
            }
            if (!match) return;
            // The final close may arrive after the next pose clears the console hand bit.
            if (!ConsoleSaveAvailable || !poseReady)
            {
                consoleSaves.End(peer, request.Operation);
                channel!.Reply(peer, entity, new PortableConsoleSaveRequest(request.Operation, PortableConsoleSaveAction.Rejected).Encode()); return;
            }
            if (request.Action != PortableConsoleSaveAction.Tick && request.Action != PortableConsoleSaveAction.Complete) return;
            int advance = consoleSaves.Advance(peer, request.Operation, request.Ticks, Time.unscaledTime);
            using (new ComputerTimeScope(peer))
                for (int n = 0; n < advance; n++) Singleton<TickSystem>.i.AddOneTickFromSave();
            if (request.Action != PortableConsoleSaveAction.Complete) return;
            if (!consoleSaves.CanComplete(peer, request.Operation, request.Ticks, Time.unscaledTime))
            {
                consoleSaves.End(peer, request.Operation);
                channel!.Reply(peer, entity, new PortableConsoleSaveRequest(request.Operation, PortableConsoleSaveAction.Rejected).Encode()); return;
            }
            consoleSaves.End(peer, request.Operation);
            console.Get_BatteryLife--; CaptureAndPublish();
            MonMPPlugin.Instance?.World?.Saves?.Request(House_Location.GameConsole);
            channel!.Reply(peer, entity, new PortableConsoleSaveRequest(request.Operation, PortableConsoleSaveAction.Approved).Encode());
        }

        private void ExpireConsoleSaveReply()
        {
            if (completedConsoleSave == Guid.Empty || Time.unscaledTime - completedConsoleSaveAt < 8f) return;
            completedConsoleSave = Guid.Empty;
            Report("The handheld save confirmation timed out. Check the host's save before leaving.");
        }
    }
}
