using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MonMP.Plugin.UI
{
    // Small uGUI builder for the multiplayer screens, dressed in the game's own fonts, rounded
    // sprites and accent colour so they read as part of the native menus.
    internal static class MenuKit
    {
        internal static readonly Color Accent = new Color32(0x73, 0x7B, 0xFA, 0xFF);
        internal static readonly Color Backdrop = new Color32(0x14, 0x19, 0x1E, 0xF5);
        internal static readonly Color Muted = new Color(1, 1, 1, 0.55f);
        internal static readonly Color Error = new Color32(0xFF, 0x8A, 0x80, 0xFF);
        internal static readonly Color Line = new Color32(0xC4, 0xC8, 0xFF, 0xE6);
        internal static readonly Color Good = new Color32(0x8C, 0xE9, 0x9A, 0xFF);

        private static TMP_FontAsset? regular, semibold, bold;
        private static Sprite? filled, outline;
        private static Sprite? generatedFilled, generatedOutline;

        internal static TMP_FontAsset? Regular => regular != null ? regular : regular = Font("OpenSans-Regular SDF");
        internal static TMP_FontAsset? Semibold => semibold != null ? semibold : semibold = Font("OpenSans-Semibold SDF");
        internal static TMP_FontAsset? Bold => bold != null ? bold : bold = Font("OpenSans-Bold SDF");
        private static Sprite? Filled => filled != null ? filled : filled = SpriteNamed("Rounded Filled 128px") ?? (generatedFilled ??= FallbackSprite(false));
        private static Sprite? Outline => outline != null ? outline : outline = SpriteNamed("Rounded Outline 128px - 1x") ?? (generatedOutline ??= FallbackSprite(true));

        // The title scene does not load the pause menu's rounded sprites. A small
        // owned nine-slice avoids turning every outline into a solid rectangle.
        private static Sprite FallbackSprite(bool border)
        {
            const int side = 128; var pixels = new Color32[side * side];
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    var q = new Vector2(Mathf.Abs(x - 63.5f) - 31.5f, Mathf.Abs(y - 63.5f) - 31.5f);
                    float distance = new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0) - 32;
                    float alpha = Mathf.Clamp01(.5f - distance);
                    if (border) alpha -= Mathf.Clamp01(.5f - distance - 8);
                    pixels[y * side + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255));
                }
            var texture = new Texture2D(side, side, TextureFormat.RGBA32, false) { name = "MonMP menu shape", wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels); texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0, 0, side, side), new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect, new Vector4(48, 48, 48, 48));
        }
        internal static void ReleaseOwnedSprites()
        {
            foreach (var sprite in new[] { generatedFilled, generatedOutline })
                if (sprite != null) { UnityEngine.Object.Destroy(sprite.texture); UnityEngine.Object.Destroy(sprite); }
            if (filled == generatedFilled) filled = null; if (outline == generatedOutline) outline = null;
            generatedFilled = generatedOutline = null;
        }

        private static TMP_FontAsset? Font(string name) =>
            Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name == name) ?? TMP_Settings.defaultFontAsset;
        private static Sprite? SpriteNamed(string name) => Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == name);

        // A root overlay canvas, created inactive so every component is wired before it wakes.
        internal static RectTransform Canvas(string name, int order)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(false);
            go.layer = 5;
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.8f;
            go.AddComponent<GraphicRaycaster>();
            return (RectTransform)go.transform;
        }

        internal static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = 5 };
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        internal static RectTransform Stretch(this RectTransform r, float left = 0, float bottom = 0, float right = 0, float top = 0)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.pivot = new Vector2(0.5f, 0.5f);
            r.offsetMin = new Vector2(left, bottom); r.offsetMax = new Vector2(-right, -top);
            return r;
        }

        internal static RectTransform Center(this RectTransform r, float width, float height)
        {
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(width, height); r.anchoredPosition = Vector2.zero;
            return r;
        }

        // Keep fixed-size menu panels inside the scaled canvas on narrower displays.
        // Called after activation, when Unity has established the canvas dimensions.
        internal static void FitWidth(RectTransform panel, float preferredWidth, float margin = 40)
        {
            var canvas = panel.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.transform is not RectTransform canvasRect || canvasRect.rect.width <= 0) return;
            float width = Mathf.Min(preferredWidth, Mathf.Max(200, canvasRect.rect.width - margin * 2));
            if (!Mathf.Approximately(panel.sizeDelta.x, width)) panel.sizeDelta = new Vector2(width, panel.sizeDelta.y);
        }

        internal static T Size<T>(this T c, float width = -1, float height = -1, float flexWidth = -1, float flexHeight = -1) where T : Component
        {
            var e = c.GetComponent<LayoutElement>() ?? c.gameObject.AddComponent<LayoutElement>();
            if (width >= 0) { e.preferredWidth = width; e.minWidth = width; e.flexibleWidth = 0; }
            if (height >= 0) { e.preferredHeight = height; e.minHeight = height; e.flexibleHeight = 0; }
            if (flexWidth >= 0) e.flexibleWidth = flexWidth;
            if (flexHeight >= 0) e.flexibleHeight = flexHeight;
            return c;
        }

        internal static Image Fill(RectTransform r, Color color, bool rounded = true, bool outlined = false, float roundness = 8f)
        {
            var image = r.gameObject.AddComponent<Image>();
            image.color = color;
            var sprite = rounded ? (outlined ? Outline : Filled) : null;
            if (sprite != null) { image.sprite = sprite; image.type = Image.Type.Sliced; image.pixelsPerUnitMultiplier = roundness; }
            return image;
        }

        internal static T Layout<T>(RectTransform r, float spacing, int left = 0, int right = 0, int top = 0, int bottom = 0,
            TextAnchor align = TextAnchor.UpperLeft) where T : HorizontalOrVerticalLayoutGroup
        {
            var layout = r.gameObject.AddComponent<T>();
            layout.spacing = spacing; layout.padding = new RectOffset(left, right, top, bottom); layout.childAlignment = align;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            return layout;
        }

        internal static TextMeshProUGUI Label(Transform parent, string text, float size, TMP_FontAsset? font = null, Color? color = null,
            TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            var r = Rect("Text", parent);
            var label = r.gameObject.AddComponent<TextMeshProUGUI>();
            if ((font ?? Regular) != null) label.font = font ?? Regular;
            label.text = text; label.fontSize = size; label.color = color ?? Color.white; label.alignment = align;
            label.enableWordWrapping = false; label.overflowMode = TextOverflowModes.Ellipsis; label.raycastTarget = false;
            label.richText = false;
            return label;
        }

        // Outline button that fills with the accent on hover (the native menu's look); primary buttons are filled.
        internal static Button Button(Transform parent, string text, UnityAction onClick, bool primary = false, float size = 28)
        {
            var r = Rect("Button " + text, parent);
            var fill = Fill(r, Color.white);
            var button = r.gameObject.AddComponent<Button>();
            button.targetGraphic = fill;
            var colors = button.colors;
            // The game renders in linear space, where faint white fills read far brighter than their alpha.
            colors.normalColor = primary ? Accent : new Color(1, 1, 1, 0.008f);
            colors.highlightedColor = primary ? Color.Lerp(Accent, Color.white, 0.25f) : Accent;
            colors.pressedColor = Color.Lerp(Accent, Color.black, 0.25f);
            colors.selectedColor = colors.normalColor;
            colors.disabledColor = new Color(1, 1, 1, 0.004f);
            colors.colorMultiplier = 1; colors.fadeDuration = 0.08f;
            button.colors = colors;
            var border = Rect("Outline", r).Stretch();
            var line = Fill(border, primary ? Color.clear : Line, outlined: true, roundness: 12);
            line.raycastTarget = false;
            var label = Label(r, text, size, Semibold, align: TextAlignmentOptions.Center);
            label.enableAutoSizing = true; label.fontSizeMin = Mathf.Max(14, size * 0.75f); label.fontSizeMax = size;
            label.rectTransform.Stretch(12, 0, 12, 0);
            button.onClick.AddListener(onClick);
            return button;
        }

        internal static void SetText(this Button button, string text) => button.GetComponentInChildren<TextMeshProUGUI>().text = text;

        // Dim the label with the button so a disabled action reads as unavailable.
        internal static void SetEnabled(this Button button, bool on)
        {
            if (button.interactable == on) return;
            button.interactable = on;
            button.GetComponentInChildren<TextMeshProUGUI>().alpha = on ? 1 : 0.35f;
            var line = button.transform.Find("Outline")?.GetComponent<Image>();
            if (line != null && line.color.a > 0) line.color = on ? Line : new Color(Line.r, Line.g, Line.b, 0.3f);
        }

        internal static TMP_InputField Input(Transform parent, string placeholder, int limit, float size = 26)
        {
            var r = Rect("Input " + placeholder, parent);
            var background = Fill(r, new Color(0, 0, 0, 0.4f));
            var underline = Rect("Underline", r);
            underline.anchorMin = new Vector2(0, 0); underline.anchorMax = new Vector2(1, 0); underline.pivot = new Vector2(0.5f, 0);
            underline.offsetMin = new Vector2(10, 0); underline.offsetMax = new Vector2(-10, 3);
            Fill(underline, new Color(1, 1, 1, 0.35f), rounded: false).raycastTarget = false;
            var area = Rect("Text Area", r).Stretch(16, 4, 16, 4);
            area.gameObject.AddComponent<RectMask2D>();
            var hint = Label(area, placeholder, size, color: new Color(1, 1, 1, 0.3f));
            hint.rectTransform.Stretch(); hint.fontStyle = FontStyles.Italic;
            var text = Label(area, "", size);
            text.rectTransform.Stretch(); text.overflowMode = TextOverflowModes.Overflow;
            var field = r.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.textViewport = area; field.textComponent = text; field.placeholder = hint;
            if (Regular != null) field.fontAsset = Regular;
            field.pointSize = size; field.characterLimit = limit; field.lineType = TMP_InputField.LineType.SingleLine;
            field.customCaretColor = true; field.caretColor = Color.white; field.caretWidth = 2;
            field.selectionColor = new Color(Accent.r, Accent.g, Accent.b, 0.5f);
            field.richText = false;
            var colors = field.colors;
            colors.normalColor = Color.white; colors.highlightedColor = new Color(1.3f, 1.3f, 1.6f, 1); colors.selectedColor = new Color(1.3f, 1.3f, 1.6f, 1);
            colors.colorMultiplier = 1;
            field.colors = colors;
            return field;
        }

        // A labelled check box; returns a setter/getter pair through the toggle.
        internal static Toggle Check(Transform parent, string text, bool value, UnityAction<bool> changed)
        {
            var r = Rect("Check " + text, parent);
            Layout<HorizontalLayoutGroup>(r, 14, align: TextAnchor.MiddleLeft);
            var box = Rect("Box", r).Size(34, 34);
            var boxImage = Fill(box, Line, outlined: true, roundness: 10);
            var tick = Rect("Tick", box).Stretch(7, 7, 7, 7);
            var tickImage = Fill(tick, Accent, roundness: 12);
            var label = Label(r, text, 24);
            label.raycastTarget = true;
            label.Size(flexWidth: 1);
            var toggle = r.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = boxImage; toggle.graphic = tickImage; toggle.isOn = value;
            toggle.onValueChanged.AddListener(changed);
            return toggle;
        }

        internal static ScrollRect Scroll(Transform parent, out RectTransform content)
        {
            var r = Rect("List", parent);
            Fill(r, new Color(0, 0, 0, 0.35f));
            var viewport = Rect("Viewport", r).Stretch(10, 10, 10, 10);
            // A stencil mask: this TMP culls rows against a RectMask2D before their first mesh exists and never re-checks.
            Fill(viewport, Color.white, rounded: false);
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            content = Rect("Content", viewport);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0.5f, 1);
            content.offsetMin = content.offsetMax = Vector2.zero;
            Layout<VerticalLayoutGroup>(content, 6).childForceExpandWidth = true;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = r.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 40;
            return scroll;
        }

        internal static void Clear(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
        }

        // Removes the game's localisation driver from a cloned native control so our text sticks.
        internal static void StripLocalization(GameObject root)
        {
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null && behaviour.GetType().Name.StartsWith("Localize", StringComparison.Ordinal)) UnityEngine.Object.DestroyImmediate(behaviour);
        }
    }
}
