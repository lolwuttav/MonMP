using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedMeetRace
    {
        private readonly MeetProgressState originalProgress = CaptureProgress();
        private MeetProgressState? sharedProgress;
        private byte[] publishedProgress = Array.Empty<byte>();
        private float nextProgressCapture;
        private readonly Actor_Anthony? anthony = UnityEngine.Object.FindObjectsOfType<Actor_Anthony>(true)
            .FirstOrDefault(actor => actor.gameObject.scene.IsValid());
        private (int Reward, uint Operation, float At)? pendingReward;
        private readonly Dictionary<uint, (uint Operation, byte[] Reply)> rewardReplies = new Dictionary<uint, (uint, byte[])>();

        private static MeetProgressState CaptureProgress()
        {
            var streaks = GameManager.Get_RaceWonInARow;
            return new MeetProgressState(GameManager.Get_Meet_Race_BestTimes, streaks[0], streaks[1], GameManager.Get_Meet_Rewards_Claimed);
        }

        private static void ApplyProgress(MeetProgressState progress)
        {
            // Native PlayerData/SaveSystem already persist these exact arrays. Keeping the
            // host's arrays canonical also preserves vanilla saves and Gilles' recovery list.
            GameManager.Get_Meet_Race_BestTimes = progress.BestTimes.ToArray();
            GameManager.Get_RaceWonInARow = new[] { progress.Streak, progress.BestStreak };
            GameManager.Get_Meet_Rewards_Claimed = progress.Claimed.ToArray();
        }

        private void SetProgress(MeetProgressState progress)
        {
            ApplyProgress(progress);
            PublishProgress();
        }

        private void PublishProgress()
        {
            if (!host || channel?.Ready != true) return;
            sharedProgress = CaptureProgress();
            var bytes = sharedProgress.Encode();
            if (bytes.SequenceEqual(publishedProgress)) return;
            try { channel.Publish(MeetProgressState.Entity, bytes); }
            catch (Exception ex) { Debug.LogWarning("MonMP meet progression publication will retry: " + ex.Message); return; }
            publishedProgress = bytes;
            RefreshProgressWindow();
        }

        private void ReceiveProgress(byte[] payload)
        {
            try { sharedProgress = MeetProgressState.Decode(payload); }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException) { return; }
            ApplyProgress(sharedProgress);
            RefreshProgressWindow();
        }

        private void UpdateProgress()
        {
            if (Time.unscaledTime < nextProgressCapture) return;
            nextProgressCapture = Time.unscaledTime + 0.25f;
            if (host) PublishProgress();
            // The native winner still runs locally for their achievement and result UI.
            // Its array writes never become another shared result or entitlement.
            else if (sharedProgress != null) ApplyProgress(sharedProgress);
        }

        private static void RefreshProgressWindow()
        {
            try
            {
                var window = UnityEngine.Object.FindObjectOfType<UI_GenericWindow_MeetsManager>();
                if (window == null || !window.isActiveAndEnabled || AccessTools.Field(typeof(UI_GenericWindow_MeetsManager), "listBuilt")?.GetValue(window) is not true) return;
                var rewards = AccessTools.Field(typeof(UI_GenericWindow_MeetsManager), "Window_Rewards")?.GetValue(window) as GameObject;
                AccessTools.Method(typeof(UI_GenericWindow_MeetsManager), rewards?.activeSelf == true ? "Open_Rewards" : "Open_Race")?.Invoke(window, null);
            }
            catch (Exception ex) { Debug.LogWarning("MonMP meet progress window refresh failed: " + ex.Message); }
        }

        // Both host and guest claims use one host transaction. In particular, the guest
        // never executes native Instantiate, so generic item adoption cannot duplicate it.
        internal bool AllowReward(UI_GenericWindow_MeetsManager window, int reward)
        {
            if (!Attached) return true;
            if (WindowField?.GetValue(window) is UI_GenericWindowManager manager) manager.Button_CloseGenericWindow();
            if (reward < 0 || reward >= 3 || channel?.FullySynchronized != true || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return false;
            if (Singleton<Gameplay>.i?.Player.IsHoldingAnObject == true)
            {
                Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_AlreadyHaveAItemInHand"));
                return false;
            }
            if (pendingReward.HasValue && Time.unscaledTime - pendingReward.Value.At < 5f) return false;
            uint operation = ++nextOperation;
            if (operation == 0) operation = ++nextOperation;
            pendingReward = (reward, operation, Time.unscaledTime);
            var request = new byte[6]; request[0] = Reward; request[1] = (byte)reward;
            BitConverter.GetBytes(operation).CopyTo(request, 2);
            channel!.Request(Entity, request, useLease: true);
            return false;
        }

        private void ClaimReward(uint peer, byte[] request)
        {
            uint operation = BitConverter.ToUInt32(request, 2);
            int reward = request[1];
            if (operation == 0 || reward >= 3) return;
            if (rewardReplies.TryGetValue(peer, out var previous) && operation <= previous.Operation)
            {
                if (operation == previous.Operation) channel!.Reply(peer, Entity, previous.Reply);
                return;
            }
            byte status = 0; ulong item = 0;
            var progress = CaptureProgress();
            var items = MonMPPlugin.Instance?.World?.Items;
            if (progress.Claimed[reward]) status = 2;
            else if (progress.Eligible(reward) && meet?.isOpen == true && anthony != null && anthony.isActiveAndEnabled &&
                MonMPPlugin.Instance?.World?.SaveGate.Active != true && items != null && items.HostCanReceiveItem(peer) &&
                items.NearPeer(peer, anthony.transform.position, 4.5f) && items.TryGetPeerHandPose(peer, out var position, out var rotation))
            {
                var assets = Singleton<GameAssets>.i;
                var prefab = reward == 0 ? assets?.Part_Spoiler : reward == 1 ? assets?.Part_Muffler5 : assets?.Part_LipSpoiler;
                if (prefab != null)
                {
                    // Reserve the durable entitlement before spawning. The item API either
                    // returns a committed identity or rolls an uncommitted spawn back.
                    var claimed = progress.Claim(reward);
                    ApplyProgress(claimed);
                    try { item = items.HostSpawnLeasedItem(prefab, position, rotation, peer); }
                    catch (Exception ex) { Debug.LogWarning("MonMP meet reward spawn failed: " + ex.Message); }
                    if (item == 0) ApplyProgress(progress);
                    else
                    {
                        status = 1;
                        try { Singleton<ToDoList_Manager>.i.Racing_ClaimReward(reward); }
                        catch (Exception ex) { Debug.LogWarning("MonMP meet reward ToDo failed: " + ex.Message); }
                    }
                    PublishProgress();
                }
            }
            var reply = new byte[15]; reply[0] = Reward; reply[1] = (byte)reward; reply[2] = status;
            BitConverter.GetBytes(operation).CopyTo(reply, 3); BitConverter.GetBytes(item).CopyTo(reply, 7);
            rewardReplies[peer] = (operation, reply);
            channel!.Reply(peer, Entity, reply);
        }

        private void RewardReply(byte[] payload)
        {
            if (payload.Length != 15 || !pendingReward.HasValue || payload[1] != pendingReward.Value.Reward ||
                BitConverter.ToUInt32(payload, 3) != pendingReward.Value.Operation || payload[2] > 2) return;
            pendingReward = null;
            if (payload[2] == 1)
            {
                using (SpatialSounds.Local()) Singleton<SOUND_Manager>.i?.PlaySound_BuggyBuildCompleted();
            }
            else MonMPPlugin.Instance?.ReportItemUseUnavailable(payload[2] == 2 ? "This shared meet reward has already been claimed." : "This meet reward is unavailable. Check eligibility, empty your hands and return to Anthony.");
        }
    }
}
