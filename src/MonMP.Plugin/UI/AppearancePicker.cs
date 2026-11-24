using System;
using System.Globalization;
using MonMP.Core.Protocol;
using MonMP.Plugin.Replication;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MonMP.Plugin.UI
{
    internal sealed class AppearancePicker : IDisposable
    {
        private readonly MonMPPlugin plugin;
        private readonly RectTransform root, box, viewport, portraitFrame;
        private readonly RawImage[] layers = new RawImage[4];
        private readonly TextMeshProUGUI[] names = new TextMeshProUGUI[4];
        private readonly TMP_InputField hex;
        private readonly Image swatch;
        private readonly ShirtColorPicker colorPicker;
        private readonly TextMeshProUGUI textureLabel;
        private readonly TextMeshProUGUI unavailable, view;
        private readonly Button apply;
        private readonly AppearancePortrait portrait = new AppearancePortrait();
        private PlayerAppearance selected;
        private bool back, refreshing, closing;
        private int backClosedFrame = -1;
        internal bool Open => root != null && root.gameObject.activeSelf;
        internal AppearancePicker(MonMPPlugin plugin, Transform parent)
        {
            this.plugin = plugin;
            root = MenuKit.Rect("Character selection", parent).Stretch();
            MenuKit.Fill(root, new Color(0, 0, 0, .82f), rounded: false);
            box = MenuKit.Rect("Character window", root).Center(1120, 850);
            MenuKit.Fill(box, new Color32(0x1E, 0x24, 0x2B, 0xFF), roundness: 4);
            MenuKit.Layout<VerticalLayoutGroup>(box, 16, 34, 34, 26, 26).childForceExpandWidth = true;
            MenuKit.Label(box, "APPEARANCE", 38, MenuKit.Bold, align: TextAlignmentOptions.Center).Size(height: 56);
            var columns = MenuKit.Rect("Parts and preview", box).Size(flexHeight: 1);
            MenuKit.Layout<HorizontalLayoutGroup>(columns, 30).childForceExpandHeight = true;
            var controls = MenuKit.Scroll(columns, out var list).Size(flexWidth: 1);
            string[] labels = { "Hair / hat", "Torso", "Legs", "Shoes" };
            for (int i = 0; i < 4; i++)
            {
                int part = i;
                MenuKit.Label(list, labels[i], 24, MenuKit.Semibold).Size(height: 30);
                var row = MenuKit.Rect(labels[i] + " choice", list).Size(height: 48);
                MenuKit.Layout<HorizontalLayoutGroup>(row, 8).childForceExpandHeight = true;
                MenuKit.Button(row, "<", () => Cycle(part, -1), size: 26).Size(48);
                names[i] = MenuKit.Label(row, "", 24, align: TextAlignmentOptions.Center).Size(flexWidth: 1);
                MenuKit.Button(row, ">", () => Cycle(part, 1), size: 26).Size(48);
            }
            var textureChoice = MenuKit.Button(list, "", () => { selected = selected.WithTexture(!selected.NativeTexture); Refresh(); }, size: 22).Size(height: 44);
            textureLabel = textureChoice.GetComponentInChildren<TextMeshProUGUI>();
            MenuKit.Label(list, "Shirt colour", 24, MenuKit.Semibold).Size(height: 34);
            var hexRow = MenuKit.Rect("RGB hex", list).Size(height: 48);
            MenuKit.Layout<HorizontalLayoutGroup>(hexRow, 10).childForceExpandHeight = true;
            swatch = MenuKit.Fill(MenuKit.Rect("Colour", hexRow).Size(48), Color.white);
            MenuKit.Button(hexRow, "Pick colour", OpenColorPicker, size: 22).Size(155);
            hex = MenuKit.Input(hexRow, "RRGGBB", 6, 24).Size(flexWidth: 1);
            hex.onEndEdit.AddListener(value => {
                if (!Open || closing || refreshing) return;
                if (!refreshing && value.Length == 6 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color))
                    selected = new PlayerAppearance(selected.Head, selected.Torso, selected.Legs, selected.Shoes, true, color, selected.NativeTexture);
                Refresh();
            });
            MenuKit.Button(list, "Use closet colour", () => { selected = selected.WithoutColor(); Refresh(); }, size: 22).Size(height: 44);
            MenuKit.Label(list, "A closet selection replaces your custom colour.", 20, color: MenuKit.Muted).Size(height: 52);
            var right = MenuKit.Rect("Preview column", columns).Size(400);
            MenuKit.Layout<VerticalLayoutGroup>(right, 10).childForceExpandWidth = true;
            viewport = MenuKit.Rect("Preview", right).Size(flexHeight: 1);
            MenuKit.Fill(viewport, new Color32(0x1F, 0x1F, 0x1F, 0xFF));
            portraitFrame = MenuKit.Rect("Portrait", viewport).Center(304, 480);
            string[] layerNames = { "Shoes", "Legs", "Skin and hair", "Shirt" };
            for (int i = 0; i < layers.Length; i++)
            {
                layers[i] = MenuKit.Rect(layerNames[i], portraitFrame).Stretch().gameObject.AddComponent<RawImage>(); layers[i].raycastTarget = false;
            }
            unavailable = MenuKit.Label(viewport, "Preview unavailable", 22, color: MenuKit.Muted, align: TextAlignmentOptions.Center);
            unavailable.rectTransform.Stretch();
            var turn = MenuKit.Rect("Turn character", right).Size(height: 48);
            MenuKit.Layout<HorizontalLayoutGroup>(turn, 12).childForceExpandHeight = true;
            MenuKit.Button(turn, "<", Turn, size: 28).Size(70);
            view = MenuKit.Label(turn, "Front", 22, color: MenuKit.Muted, align: TextAlignmentOptions.Center).Size(flexWidth: 1);
            MenuKit.Button(turn, ">", Turn, size: 28).Size(70);
            var buttons = MenuKit.Rect("Character actions", box).Size(height: 56);
            MenuKit.Layout<HorizontalLayoutGroup>(buttons, 16).childForceExpandHeight = true;
            apply = MenuKit.Button(buttons, "Apply", () => { plugin.MenuSetAppearance(selected); Close(); }, primary: true).Size(flexWidth: 1);
            MenuKit.Button(buttons, "Cancel", Close).Size(flexWidth: 1);
            colorPicker = new ShirtColorPicker(root, PickedColor);
            root.gameObject.SetActive(false);
        }
        private byte Part(int slot) => slot == 0 ? selected.Head : slot == 1 ? selected.Torso : slot == 2 ? selected.Legs : selected.Shoes;
        private void Cycle(int slot, int delta)
        {
            byte id = AvatarPartChoices.Cycle(slot, Part(slot), delta);
            selected = new PlayerAppearance(slot == 0 ? id : selected.Head, slot == 1 ? id : selected.Torso,
                slot == 2 ? id : selected.Legs, slot == 3 ? id : selected.Shoes, selected.CustomColor, selected.Rgb, selected.NativeTexture); Refresh();
        }
        private Color Tint => selected.CustomColor ? RemotePlayers.AppearanceColor(selected, 0) : plugin.MenuNativeShirtTint;
        private uint CurrentRgb { get { var c = (Color32)Tint; return (uint)(c.r << 16 | c.g << 8 | c.b); } }
        private void OpenColorPicker()
        {
            var before = selected;
            colorPicker.Show(Tint, () => { selected = before; Refresh(); });
        }
        private void PickedColor(Color color)
        {
            if (closing || !Open) return;
            var c = (Color32)color;
            selected = new PlayerAppearance(selected.Head, selected.Torso, selected.Legs, selected.Shoes, true,
                (uint)(c.r << 16 | c.g << 8 | c.b), selected.NativeTexture); Refresh();
        }
        internal void Show()
        {
            backClosedFrame = -1;
            selected = plugin.MenuAppearance; back = false; root.SetAsLastSibling(); root.gameObject.SetActive(true);
            Refresh(); Canvas.ForceUpdateCanvases(); Tick();
        }
        private void Turn() { back = !back; Refresh(); }
        private void Refresh()
        {
            if (closing || !Open) return;
            refreshing = true;
            try
            {
                bool available = portrait.Select(selected, back);
                for (int i = 0; i < layers.Length; i++) { layers[i].texture = portrait.Layers[i]; layers[i].gameObject.SetActive(available); }
                unavailable.gameObject.SetActive(!available); layers[3].color = Tint; swatch.color = Tint;
                for (int i = 0; i < 4; i++) names[i].text = AvatarPartChoices.Label(i, Part(i));
                uint c = CurrentRgb;
                textureLabel.text = "Shirt texture: " + (selected.NativeTexture ? "Original" : "Plaid");
                hex.SetTextWithoutNotify(c.ToString("X6", CultureInfo.InvariantCulture)); view.text = back ? "Back" : "Front";
                apply.SetEnabled(selected != plugin.MenuAppearance && !plugin.LocalDisconnectPending && !plugin.MenuConnecting);
            }
            finally { refreshing = false; }
        }
        internal void Tick()
        {
            if (!Open) return;
            if (plugin.LocalDisconnectPending || plugin.MenuConnecting) { Close(); return; }
            MenuKit.FitWidth(box, 1120);
            var canvas = root.GetComponentInParent<Canvas>().transform as RectTransform;
            float height = canvas != null ? Mathf.Min(850, canvas.rect.height - 60) : 850;
            if (height > 200 && !Mathf.Approximately(box.sizeDelta.y, height)) box.sizeDelta = new Vector2(box.sizeDelta.x, height);
            float h = Mathf.Max(1, Mathf.Min(viewport.rect.height - 20, (viewport.rect.width - 20) * 480f / 304f));
            var size = new Vector2(h * 304f / 480f, h); if (portraitFrame.sizeDelta != size) portraitFrame.sizeDelta = size;
            colorPicker.Tick();
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true) CloseForBack();
        }
        internal bool CloseForBack()
        {
            if (backClosedFrame == Time.frameCount) return true;
            if (colorPicker.Open) { backClosedFrame = Time.frameCount; colorPicker.Close(true); return true; }
            if (Open) { backClosedFrame = Time.frameCount; Close(); return true; }
            return backClosedFrame == Time.frameCount;
        }
        internal void Close()
        {
            closing = true;
            try
            {
                colorPicker.Close(false);
                foreach (var layer in layers) if (layer != null) layer.texture = null;
                portrait.Dispose(); if (root != null) root.gameObject.SetActive(false);
            }
            finally { closing = false; }
        }
        public void Dispose() { Close(); colorPicker.Dispose(); if (root != null) UnityEngine.Object.Destroy(root.gameObject); }
#if MONMP_DEVELOPMENT
        internal void TestSelect(byte model) { selected = PlayerAppearance.Whole(model); Refresh(); }
        internal void TestParts(PlayerAppearance value) { selected = value; Refresh(); }
        internal void TestRgb(string value) { hex.text = value; hex.onEndEdit.Invoke(value); }
        internal void TestOpenColor() => OpenColorPicker();
        internal void TestHSV(float h, float s, float v) => colorPicker.TestHSV(h, s, v);
        internal void TestColorDrag(float h, float s, float v) => colorPicker.TestDrag(h, s, v);
        internal void TestCloseColor(bool cancel) => colorPicker.Close(cancel);
        internal bool TestColorOpen => colorPicker.Open;
        internal void TestTexture() { selected = selected.WithTexture(!selected.NativeTexture); Refresh(); }
        internal void TestTurn() => Turn();
        internal bool TestReady => portrait.Layers[0] != null;
        internal RectTransform TestRoot => root;
        internal void TestApply() => apply.onClick.Invoke();
        internal void TestBack() => CloseForBack();
        internal Color TestTint => layers[3].color;
#endif
    }
}
