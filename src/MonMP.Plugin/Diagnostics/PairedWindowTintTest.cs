using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Drives the real two-process WorldChannel and native tint methods. It uses
    // direct adapter requests after item pickup; camera ray and physical mouse
    // targeting remain a separate manual acceptance check.
    internal sealed class PairedWindowTintTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedWindowTints tints;
        private readonly SharedItems items;
        private readonly ulong windowId;
        private readonly Vehicle_WindowTint window;
        private readonly float started = Time.unscaledTime;
        private ulong rollId, removerId;
        private Guid appliedOperation;
        private int phase;
        private int grabAttempts;
        private bool requestedGrab;
        private float next;

        internal string Status => "window tint phase=" + phase + "; entity=" + windowId.ToString("X16") +
            "; accepted=" + tints.TestAccepted + "; rejected=" + tints.TestRejected +
            "; pending=" + tints.TestPending + "; roll=" + rollId + "; remover=" + removerId +
            (rollId == 0 ? "" : "; pickup=" + items.TestPickupStatus(rollId));

        internal PairedWindowTintTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            tints = plugin.World!.WindowTints; items = plugin.World.Items;
            var choice = tints.TestWindows.Select(id => (Id: id, Native: tints.TestNative(id)))
                .Where(p => p.Native != null && p.Native.gameObject.activeInHierarchy && !p.Native.Get_IsTint)
                .OrderByDescending(p => p.Native is Vehicle_WindowTint_Second)
                .ThenBy(p => p.Id).FirstOrDefault();
            if (choice.Native == null) throw new InvalidOperationException("No active untinted native window was found.");
            windowId = choice.Id; window = choice.Native;
        }

        internal bool Tick()
        {
            if (Time.unscaledTime - started > 140) throw new InvalidOperationException("Window tint test timed out: " + Status);
            if (Time.unscaledTime < next || plugin.World?.Channel.FullySynchronized != true) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0 && plugin.Session!.Peers.Count >= 2)
            {
                (rollId, removerId) = items.TestSpawnTintTools(window.transform.position + Vector3.up * 0.5f);
                phase = 1;
            }
            else if (phase == 1 && tints.TestRejected > 0 && !window.Get_IsTint) phase = 2;
            else if (phase == 2 && tints.TestAccepted >= 1 && window.Get_IsTint)
            {
                if (items.TestState(rollId).Values.IntValue1 != 1)
                    throw new InvalidOperationException("Host did not spend exactly one tint use.");
                phase = 3;
            }
            else if (phase == 3 && tints.TestAccepted >= 2 && tints.TestDuplicate >= 1 && !window.Get_IsTint) phase = 4;
            else if (phase == 4 && plugin.Session!.Peers.Count == 1)
            {
                if (tints.TestRevision(windowId) < 3) throw new InvalidOperationException("Tint apply/remove revisions were not published.");
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                rollId = items.TestNewestTintTool(false); removerId = items.TestNewestTintTool(true);
                if (rollId == 0 || removerId == 0 || items.TestItem(rollId) == null || items.TestItem(removerId) == null) return false;
                Move(window.transform.position + Vector3.up * 0.75f);
                if (!Same(tints.TestNativeState(windowId), tints.TestState(windowId)))
                    throw new InvalidOperationException("Guest tint material did not match initial host snapshot.");
                // The host validates pickup against the most recently received walking
                // pose. Let the teleported test pose arrive before requesting its lease.
                phase = 1; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 1)
            {
                var state = items.TestState(rollId);
                if (state.Lease.Owner != plugin.Session!.LocalPeerId)
                {
                    if (!requestedGrab || !items.TestPickupPending(rollId))
                    {
                        if (grabAttempts >= 6) throw new InvalidOperationException("Tint roll lease was not granted: " + Status);
                        items.TestGrab(rollId);
                        requestedGrab = true; grabAttempts++;
                        next = Time.unscaledTime + 1.5f;
                    }
                    return false;
                }
                if (window.ApplyTint(new Color(0.1f, 0.2f, 0.3f, 0.4f)) || window.Get_IsTint)
                    throw new InvalidOperationException("Guest changed a tint without host approval.");
                Move(window.transform.position + Vector3.right * 20);
                phase = 2; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 2)
            {
                tints.Request(windowId, rollId, items.TestState(rollId).Lease.Generation, false);
                phase = 3; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 3 && tints.TestPending == 0)
            {
                if (window.Get_IsTint || items.TestState(rollId).Values.IntValue1 != 2)
                    throw new InvalidOperationException("Distant guest tint changed native state or consumed the roll.");
                Move(window.transform.position + Vector3.up * 0.75f);
                phase = 4; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 4)
            {
                appliedOperation = tints.Request(windowId, rollId, items.TestState(rollId).Lease.Generation, false);
                phase = 5; return false;
            }
            if (phase == 5 && tints.TestPending == 0 && window.Get_IsTint && items.TestState(rollId).Values.IntValue1 == 1)
            {
                if (!Same(tints.TestNativeState(windowId), tints.TestState(windowId)))
                    throw new InvalidOperationException("Guest tint material differs from host state.");
                tints.TestReplay(windowId, rollId, items.TestState(rollId).Lease.Generation, false, appliedOperation);
                phase = 6; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 6)
            {
                if (items.TestState(rollId).Values.IntValue1 != 1)
                    throw new InvalidOperationException("A duplicate tint action spent another roll use.");
                items.TestDrop(false);
                phase = 7; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 7)
            {
                items.TestGrab(removerId);
                phase = 8; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 8 && items.TestState(removerId).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                window.RemoveTint();
                if (!window.Get_IsTint) throw new InvalidOperationException("Guest removed tint without host approval.");
                tints.Request(windowId, removerId, items.TestState(removerId).Lease.Generation, true);
                phase = 9; return false;
            }
            if (phase == 9 && tints.TestPending == 0 && !window.Get_IsTint)
            {
                if (!Same(tints.TestNativeState(windowId), tints.TestState(windowId)))
                    throw new InvalidOperationException("Guest native removal differs from host state.");
                plugin.EndPairedTestSession();
                if (!tints.TestRestored) throw new InvalidOperationException("Guest tint did not restore its own baseline.");
                return true;
            }
            return false;
        }

        private void Move(Vector3 location)
        {
            gameplay.PlayerWalking.transform.position = location;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static bool Same(WindowTintState a, WindowTintState b) => a.Encode().SequenceEqual(b.Encode());
    }
}
