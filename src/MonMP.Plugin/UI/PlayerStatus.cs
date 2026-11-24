using MonMP.Core.Session;
using TMPro;
using UnityEngine;

namespace MonMP.Plugin.UI
{
    // Player-facing feedback remains visible with diagnostics disabled, using the native menu kit.
    internal sealed class PlayerStatus
    {
        private RectTransform? root, syncBox, sleepBox;
        private TextMeshProUGUI? sync, sleep;

        internal void Tick(MonMPPlugin plugin)
        {
            bool visible = !MonMPPlugin.CleanFrame && plugin.Session?.IsRunning == true;
            if (root == null && visible)
            {
                root = MenuKit.Canvas("MonMP Status", 450);
                syncBox = Panel(root, "Synchronizing", 1, -60, out sync);
                sleepBox = Panel(root, "Sleep", 0, 80, out sleep);
            }
            if (root == null) return;
            SetActive(root, visible);
            if (!visible) return;
            bool loading = plugin.Session!.State == SessionState.Connected && plugin.World?.Channel.FullySynchronized != true;
            SetActive(syncBox!, loading);
            if (loading)
            {
                string status = "Synchronizing world\n" + (plugin.World?.SynchronizationStatus ?? "Waiting for the host...");
                FitMessage(syncBox!, sync!, status);
            }
            string message = plugin.World?.Sleep.LocalStatus ?? "";
            SetActive(sleepBox!, message.Length > 0);
            FitMessage(sleepBox!, sleep!, message);
        }

        private static void FitMessage(RectTransform box, TextMeshProUGUI text, string message)
        {
            float previousWidth = box.sizeDelta.x;
            MenuKit.FitWidth(box, 740, 20);
            if (text.text == message && Mathf.Approximately(previousWidth, box.sizeDelta.x)) return;
            text.text = message;
            // Both loading details and remapped sleep controls may wrap. Anchor the growing
            // panels to their screen edge so the additional lines stay inside the canvas.
            box.sizeDelta = new Vector2(box.sizeDelta.x, Mathf.Max(80, text.GetPreferredValues(message, box.sizeDelta.x - 40, 0).y + 24));
        }

        private static void SetActive(RectTransform target, bool active)
        {
            if (target.gameObject.activeSelf != active) target.gameObject.SetActive(active);
        }

        private static RectTransform Panel(Transform parent, string name, float anchor, float offset, out TextMeshProUGUI text)
        {
            var box = MenuKit.Rect(name, parent);
            box.anchorMin = box.anchorMax = new Vector2(0.5f, anchor);
            box.pivot = new Vector2(0.5f, anchor);
            box.sizeDelta = new Vector2(740, 80); box.anchoredPosition = new Vector2(0, offset);
            MenuKit.Fill(box, MenuKit.Backdrop).raycastTarget = false;
            text = MenuKit.Label(box, "", 24, MenuKit.Semibold, align: TextAlignmentOptions.Center);
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            text.rectTransform.Stretch(20, 10, 20, 10);
            return box;
        }

        internal void Destroy() { if (root != null) Object.Destroy(root.gameObject); }
    }
}
