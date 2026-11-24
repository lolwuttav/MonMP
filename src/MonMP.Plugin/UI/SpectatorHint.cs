using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MonMP.Plugin.UI
{
    // The native HUD uses Montserrat labels, lightly shaded rectangles and controller/key badges.
    // Keep our text and badges separate from its live prompts so spectating never takes them over.
    internal sealed class SpectatorHint
    {
        private static readonly Color Shade = new Color(0, 0, 0, 0.3f);
        private RectTransform? root;
        private TextMeshProUGUI? player;
        private string? shownName;
        private bool forced;

        internal void Show(string playerName, bool permadeath = false)
        {
            if (forced != permadeath) { Destroy(); forced = permadeath; }
            EnsureUI();
            if (shownName != playerName)
            {
                player!.text = forced ? playerName : "Spectating " + playerName;
                shownName = playerName;
            }
            if (!root!.gameObject.activeSelf) root.gameObject.SetActive(true);
        }

        internal void Hide()
        { if (root != null && root.gameObject.activeSelf) root.gameObject.SetActive(false); }

        private void EnsureUI()
        {
            if (root != null) return;
            var created = MenuKit.Canvas("MonMP Spectator Hint", 445);
            try
            {
                created.GetComponent<GraphicRaycaster>().enabled = false;
                var group = created.gameObject.AddComponent<CanvasGroup>();
                group.interactable = false; group.blocksRaycasts = false;

                var native = UnityEngine.Object.FindObjectOfType<UI_InteractableText>(true)?.interactableText;
                var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                var hudFont = native != null ? native.font : fonts.FirstOrDefault(f => f.name == "Montserrat-Bold SDF");
                var keyFont = fonts.FirstOrDefault(f => f.name == "OpenSans-ExtraBold SDF") ?? MenuKit.Bold;

                var nameBox = TopPanel(created, "Player", 660, 46, 64);
                player = MenuKit.Label(nameBox, "", 32, hudFont, Color.white, TextAlignmentOptions.Center);
                if (native != null && native.fontSharedMaterial != null) player.fontSharedMaterial = native.fontSharedMaterial;
                player.rectTransform.Stretch(16, 0, 16, 0);
                player.enableAutoSizing = true; player.fontSizeMin = 20; player.fontSizeMax = 32;

                var controls = MenuKit.Rect("Controls", created);
                controls.anchorMin = controls.anchorMax = controls.pivot = new Vector2(0.5f, 1);
                controls.sizeDelta = new Vector2(1012, 56); controls.anchoredPosition = new Vector2(0, -122);
                AddControl(controls, 0, 280, 76, "F6", forced ? "Next (RB)" : "Next player", hudFont, keyFont);
                AddControl(controls, 292, 236, 92, "Mouse", "Orbit", hudFont, keyFont);
                AddControl(controls, 540, 236, 92, "Wheel", "Zoom", hudFont, keyFont);
                AddControl(controls, 788, 224, 76, "Esc", forced ? "Leave (Start)" : "Exit", hudFont, keyFont);

                shownName = null; root = created;
            }
            catch
            {
                UnityEngine.Object.Destroy(created.gameObject); player = null;
                throw;
            }
        }

        private static RectTransform TopPanel(Transform parent, string name, float width, float height, float inset)
        {
            var panel = MenuKit.Rect(name, parent);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 1);
            panel.sizeDelta = new Vector2(width, height); panel.anchoredPosition = new Vector2(0, -inset);
            MenuKit.Fill(panel, Shade, rounded: false).raycastTarget = false;
            return panel;
        }

        private static void AddControl(Transform parent, float x, float width, float keyWidth, string key, string action,
            TMP_FontAsset? hudFont, TMP_FontAsset? keyFont)
        {
            var row = MenuKit.Rect(action, parent);
            row.anchorMin = row.anchorMax = row.pivot = new Vector2(0, 0.5f);
            row.sizeDelta = new Vector2(width, 56); row.anchoredPosition = new Vector2(x, 0);
            MenuKit.Fill(row, Shade, rounded: false).raycastTarget = false;

            var badge = MenuKit.Rect("Key", row);
            badge.anchorMin = badge.anchorMax = badge.pivot = new Vector2(0, 0.5f);
            badge.sizeDelta = new Vector2(keyWidth, 44); badge.anchoredPosition = new Vector2(8, 0);
            var details = GameAssets.i != null ? GameAssets.i.Get_Controller_Button_Details(key, (int)InputDeviceType.Keyboard) : null;
            var background = MenuKit.Fill(badge, Color.white, roundness: 4);
            background.raycastTarget = false;
            if (details?.sprite != null)
            {
                background.sprite = details.sprite;
                background.type = details.sprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple;
                background.preserveAspect = details.Name != "Default";
            }
            string text = details != null && details.Name != "Default" ? details.TextToShow : key;
            var keyLabel = MenuKit.Label(badge, text, 24, keyFont,
                details?.sprite != null ? details.textColor : Color.black, TextAlignmentOptions.Center);
            keyLabel.rectTransform.Stretch(4, 0, 4, 0);
            keyLabel.enableAutoSizing = true; keyLabel.fontSizeMin = 14; keyLabel.fontSizeMax = 24;
            keyLabel.overflowMode = TextOverflowModes.Overflow;

            var label = MenuKit.Label(row, action, 28, hudFont, Color.white, TextAlignmentOptions.Left);
            label.rectTransform.Stretch(keyWidth + 20, 0, 12, 0);
            label.enableAutoSizing = true; label.fontSizeMin = 18; label.fontSizeMax = 28;
            label.overflowMode = TextOverflowModes.Overflow;
        }

        internal void Destroy()
        {
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
            root = null; player = null; shownName = null;
        }
    }
}
