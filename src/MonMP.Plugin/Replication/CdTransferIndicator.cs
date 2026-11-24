using System;
using TMPro;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed class CdTransferIndicator : IDisposable
    {
        private TextMeshProUGUI? title;
        private string original = "", rendered = "";
        private Vector2 originalAnchorMax, originalOffsetMax;
        private bool expanded;
        internal void Bind(TextMeshProUGUI songCount)
        {
            // Inspected native CD Player panel. Never search the scene or match
            // translated text; a changed native layout simply omits the indicator.
            var next = songCount.transform.parent?.Find("TitleBackground/Title")?.GetComponent<TextMeshProUGUI>();
            if (next == title) return;
            Dispose(); title = next;
            if (title != null) original = rendered = title.text;
        }
        internal void Show(string? rates)
        {
            if (title == null) return;
            if (rates != null && !expanded)
            {
                // Native Title is only 196 px wide inside a 320 px header.
                // Use that spare width; native font autosizing/localization stays intact.
                var rect = title.rectTransform;
                originalAnchorMax = rect.anchorMax; originalOffsetMax = rect.offsetMax;
                rect.anchorMax = new Vector2(1, rect.anchorMax.y);
                rect.offsetMax = new Vector2(-6, rect.offsetMax.y);
                expanded = true;
            }
            else if (rates == null) RestoreWidth();
            // Keep native localization or another UI owner's latest heading.
            if (title.text != rendered) original = title.text;
            rendered = rates == null ? original : original + " <size=55%>" + rates + "</size>";
            if (title.text != rendered) title.text = rendered;
        }
        public void Dispose()
        {
            if (title != null && title.text == rendered) title.text = original;
            RestoreWidth();
            title = null; original = rendered = "";
        }
        private void RestoreWidth()
        {
            if (expanded && title != null)
            {
                title.rectTransform.anchorMax = originalAnchorMax;
                title.rectTransform.offsetMax = originalOffsetMax;
            }
            expanded = false;
        }
    }
}
