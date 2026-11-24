using UnityEngine;
using UnityEngine.UI;

namespace MonMP.Plugin.UI
{
    internal sealed class ErrorCopyButton
    {
        private readonly MonMPPlugin plugin;
        private readonly RectTransform row;
        private readonly Button button;
        private float copiedUntil;
        internal ErrorCopyButton(MonMPPlugin plugin, Transform parent)
        {
            this.plugin = plugin;
            row = MenuKit.Rect("Disconnect errors", parent).Size(height: 42);
            MenuKit.Layout<HorizontalLayoutGroup>(row, 0, align: TextAnchor.MiddleCenter).childForceExpandHeight = true;
            button = MenuKit.Button(row, "Copy Errors", Copy, size: 22).Size(240);
            row.gameObject.SetActive(false);
        }
        private void Copy()
        {
            try { GUIUtility.systemCopyBuffer = plugin.MenuErrorReport; button.SetText("Copied"); }
            catch { button.SetText("Copy failed"); }
            copiedUntil = Time.unscaledTime + 3;
        }
        internal void Tick()
        {
            bool visible = plugin.MenuHasErrorReport &&
                !plugin.MenuConnecting && !plugin.LocalDisconnectPending;
            if (row.gameObject.activeSelf != visible) row.gameObject.SetActive(visible);
            if (copiedUntil > 0 && Time.unscaledTime >= copiedUntil) { button.SetText("Copy Errors"); copiedUntil = 0; }
        }
    }
}
