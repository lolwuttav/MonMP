using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedPaintPanelsTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedPaintPanels shared;
        private readonly ulong panelId;
        private readonly bool dynamicPanel;
        private readonly Vehicle_PaintablePanel panel;
        private readonly float started = Time.unscaledTime;
        private PaintPanelState? baseline;
        private float next;
        private int phase;

        internal string Status => "paint panel phase=" + phase + "; scope=" + (dynamicPanel ? "item/socket" : "static") +
            "; entity=" + panelId.ToString("X16") +
            "; revision=" + shared.TestRevision(panelId) + "; accepted=" + shared.TestAccepted +
            "; rejected=" + shared.TestRejected + "; pending=" + shared.TestPending +
            "; reconnectSettled=" + (plugin.World?.ReconnectPositions?.Settled == true);

        internal PairedPaintPanelsTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin;
            this.gameplay = gameplay;
            shared = plugin.World!.PaintPanels;
            // Prefer a socket/item-owned native panel when one is installed and
            // paintable; the static panel remains a fallback on sparse saves.
            var candidate = shared.TestPanels.Select(id => (Id: id, Panel: shared.TestNative(id)))
                .Where(entry => entry.Panel != null && entry.Panel.gameObject.activeInHierarchy && entry.Panel.PannelAssociatedToSave == null)
                .Where(entry => HasMaterial(entry.Panel))
                .OrderByDescending(entry => shared.TestDynamicPanels.Contains(entry.Id))
                .ThenBy(entry => entry.Id).FirstOrDefault();
            if (candidate.Panel == null) throw new InvalidOperationException("No active, unassociated native paintable panel is available for the paired fixture.");
            panelId = candidate.Id;
            panel = candidate.Panel;
            dynamicPanel = shared.TestDynamicPanels.Contains(panelId);
        }

        internal bool Tick()
        {
            if (Time.unscaledTime - started > 120) throw new InvalidOperationException("Paint panel test timed out: " + Status);
            // Teardown clears World; completion must still verify the cached adapter's restoration.
            if (!plugin.TestOptions.IsHost && phase == 3 && plugin.World == null) return Guest();
            if (Time.unscaledTime < next || plugin.World?.Channel.FullySynchronized != true) return false;
            if (plugin.TestOptions.IsHost) return Host();
            return Guest();
        }

        private bool Host()
        {
            if (phase == 0 && plugin.Session!.Peers.Count >= 2)
            {
                baseline = shared.TestState(panelId);
                phase = 1;
            }
            else if (phase == 1 && shared.TestAccepted > 0 && baseline != null && !Same(shared.TestState(panelId), baseline))
                phase = 2;
            else if (phase == 2 && plugin.Session!.Peers.Count == 1)
            {
                if (shared.TestRevision(panelId) < 2) throw new InvalidOperationException("Guest paint did not publish a new native panel revision.");
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                // Reconnect placement can arrive after profile/world activation.
                if (plugin.World?.ReconnectPositions?.Settled != true) return false;
                baseline = shared.TestState(panelId);
                if (!Same(shared.TestNativeState(panelId), baseline))
                    throw new InvalidOperationException("Guest did not apply the host's native paint panel snapshot.");
                Move(panel.transform.position + Vector3.up * 0.75f);
                phase = 1;
                next = Time.unscaledTime + 0.8f;
                return false;
            }
            if (phase == 1)
            {
                float targetRed = baseline!.Red < 0.5f ? 1f : 0f;
                float smoothDelta = baseline.Smoothness < 0.85f ? 0.02f : -0.02f;
                float metalDelta = baseline.Metalness < 0.85f ? 0.02f : -0.02f;
                panel.Paint(new Vector3(targetRed, baseline.Green, baseline.Blue), smoothDelta, metalDelta, true);
                phase = 2;
                next = Time.unscaledTime + 1.2f;
                return false;
            }
            if (phase == 2 && shared.TestPending == 0 && !Same(shared.TestState(panelId), baseline!))
            {
                if (!Same(shared.TestNativeState(panelId), shared.TestState(panelId)))
                    throw new InvalidOperationException("Guest native material differs from the host-authoritative paint state.");
                plugin.EndPairedTestSession();
                phase = 3;
            }
            if (phase == 3 && plugin.World == null)
            {
                if (!shared.TestRestored) throw new InvalidOperationException("Guest paint state was not restored on disconnect.");
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

        private static bool HasMaterial(Vehicle_PaintablePanel value)
        {
            try { return value.Get_Material != null; }
            catch { return false; }
        }

        private static bool Same(PaintPanelState a, PaintPanelState b) => a.Encode().SequenceEqual(b.Encode());
    }
}
