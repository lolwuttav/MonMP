using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MonMP.Plugin.UI
{
    // Small native-UI HSV modal. Three one-dimensional gradients are created only
    // while open; dragging changes UI colours, never regenerates a texture.
    internal sealed class ShirtColorPicker : IDisposable
    {
        private readonly RectTransform root, box, square, hue, cursor, hueCursor;
        private readonly RawImage white, black, spectrum;
        private readonly Image baseHue, sample;
        private readonly TextMeshProUGUI code;
        private readonly ColorPickSurface squareInput, hueInput;
        private readonly Action<Color> changed;
        private Texture2D? saturationTexture, valueTexture, hueTexture;
        private Action? cancelled;
        private GameObject? previousSelection;
        private float h, s, v;
        private Vector2 lastSquare, lastHue;
        private bool disposed;
        internal bool Open => root != null && root.gameObject.activeSelf;
        internal ShirtColorPicker(Transform parent, Action<Color> changed)
        {
            this.changed = changed;
            root = MenuKit.Rect("Shirt colour picker", parent).Stretch();
            MenuKit.Fill(root, new Color(0, 0, 0, .6f), rounded: false);
            box = MenuKit.Rect("Colour window", root).Center(500, 470);
            MenuKit.Fill(box, new Color32(0x1E, 0x24, 0x2B, 0xFF), roundness: 4);
            MenuKit.Layout<VerticalLayoutGroup>(box, 14, 24, 24, 22, 22).childForceExpandWidth = true;
            MenuKit.Label(box, "SHIRT COLOUR", 30, MenuKit.Bold, align: TextAlignmentOptions.Center).Size(height: 40);
            var row = MenuKit.Rect("Colour controls", box).Size(flexHeight: 1);
            MenuKit.Layout<HorizontalLayoutGroup>(row, 18).childForceExpandHeight = true;
            square = MenuKit.Rect("Saturation and brightness", row).Size(flexWidth: 1);
            baseHue = MenuKit.Fill(square, Color.red, rounded: false);
            white = MenuKit.Rect("Saturation gradient", square).Stretch().gameObject.AddComponent<RawImage>(); white.raycastTarget = false;
            black = MenuKit.Rect("Value gradient", square).Stretch().gameObject.AddComponent<RawImage>(); black.raycastTarget = false;
            cursor = MenuKit.Rect("Colour cursor", square).Center(14, 14);
            MenuKit.Fill(cursor, Color.white, outlined: true, roundness: 2).raycastTarget = false;
            cursor.anchorMin = cursor.anchorMax = Vector2.zero;
            squareInput = square.gameObject.AddComponent<ColorPickSurface>(); squareInput.targetGraphic = baseHue; squareInput.transition = Selectable.Transition.None;
            squareInput.Picked = p => { s = p.x; v = p.y; Update(true); };
            hue = MenuKit.Rect("Hue", row).Size(32);
            spectrum = hue.gameObject.AddComponent<RawImage>();
            hueCursor = MenuKit.Rect("Hue cursor", hue).Center(40, 4);
            MenuKit.Fill(hueCursor, Color.white, rounded: false).raycastTarget = false;
            hueCursor.anchorMin = hueCursor.anchorMax = Vector2.zero;
            hueInput = hue.gameObject.AddComponent<ColorPickSurface>(); hueInput.targetGraphic = spectrum; hueInput.transition = Selectable.Transition.None; hueInput.VerticalOnly = true;
            hueInput.Picked = p => { h = p.y; Update(true); };
            var result = MenuKit.Rect("Colour sample", box).Size(height: 40);
            MenuKit.Layout<HorizontalLayoutGroup>(result, 16).childForceExpandHeight = true;
            sample = MenuKit.Fill(MenuKit.Rect("Sample", result).Size(64), Color.white);
            code = MenuKit.Label(result, "", 24, align: TextAlignmentOptions.Center).Size(flexWidth: 1);
            var actions = MenuKit.Rect("Colour actions", box).Size(height: 48);
            MenuKit.Layout<HorizontalLayoutGroup>(actions, 14).childForceExpandHeight = true;
            var done = MenuKit.Button(actions, "Done", () => Close(false), primary: true).Size(flexWidth: 1);
            var cancel = MenuKit.Button(actions, "Cancel", () => Close(true)).Size(flexWidth: 1);
            squareInput.Accepted = () => hueInput.Select(); hueInput.Accepted = () => done.Select();
            done.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnLeft = cancel, selectOnRight = cancel, selectOnUp = hueInput };
            cancel.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnLeft = done, selectOnRight = done, selectOnUp = squareInput };
            root.gameObject.SetActive(false);
        }
        internal void Show(Color color, Action cancel)
        {
            if (disposed || root == null || Open) return;
            previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            cancelled = cancel; Color.RGBToHSV(color, out h, out s, out v);
            try
            {
            saturationTexture = Gradient(256, 1, i => new Color(1, 1, 1, 1 - i / 255f));
            valueTexture = Gradient(1, 256, i => new Color(0, 0, 0, 1 - i / 255f));
            hueTexture = Gradient(1, 256, i => Color.HSVToRGB(i / 255f, 1, 1));
            white.texture = saturationTexture; black.texture = valueTexture; spectrum.texture = hueTexture;
            root.SetAsLastSibling(); root.gameObject.SetActive(true); Canvas.ForceUpdateCanvases(); Update(false);
            squareInput.Select();
            }
            catch { Close(false); throw; }
        }
        private static Texture2D Gradient(int width, int height, Func<int, Color> pixel)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) {
                name = "MonMP colour picker gradient", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            try
            {
                var colors = new Color[width * height]; for (int i = 0; i < colors.Length; i++) colors[i] = pixel(i);
                texture.SetPixels(colors); texture.Apply(false, true); return texture;
            }
            catch { UnityEngine.Object.Destroy(texture); throw; }
        }
        private void Update(bool notify)
        {
            var color = Color.HSVToRGB(h, s, v); var bytes = (Color32)color;
            baseHue.color = Color.HSVToRGB(h, 1, 1); sample.color = color;
            code.text = "#" + ColorUtility.ToHtmlStringRGB(color);
            squareInput.Value = new Vector2(s, v); hueInput.Value = new Vector2(.5f, h);
            PlaceCursors();
            if (notify) changed(bytes);
        }
        private void PlaceCursors()
        {
            cursor.anchoredPosition = new Vector2(square.rect.width * s, square.rect.height * v);
            hueCursor.anchoredPosition = new Vector2(hue.rect.width * .5f, hue.rect.height * h);
            lastSquare = square.rect.size; lastHue = hue.rect.size;
        }
        internal void Tick()
        {
            if (!Open) return;
            MenuKit.FitWidth(box, 500);
            if (lastSquare != square.rect.size || lastHue != hue.rect.size) PlaceCursors();
        }
        internal void Close(bool cancel)
        {
            bool wasOpen = Open; if (root != null) root.gameObject.SetActive(false);
            if (white != null) white.texture = null;
            if (black != null) black.texture = null;
            if (spectrum != null) spectrum.texture = null;
            if (saturationTexture != null) UnityEngine.Object.Destroy(saturationTexture);
            if (valueTexture != null) UnityEngine.Object.Destroy(valueTexture);
            if (hueTexture != null) UnityEngine.Object.Destroy(hueTexture);
            saturationTexture = valueTexture = hueTexture = null;
            var restore = cancelled; cancelled = null;
            if (wasOpen && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(previousSelection != null && previousSelection.activeInHierarchy ? previousSelection : null);
            previousSelection = null;
            if (cancel && wasOpen) restore?.Invoke();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Close(false); if (root != null) UnityEngine.Object.Destroy(root.gameObject);
        }
#if MONMP_DEVELOPMENT
        internal void TestHSV(float hueValue, float saturation, float value) { h = hueValue; s = saturation; v = value; Update(true); }
        internal void TestDrag(float hueValue, float saturation, float value)
        {
            void Drag(RectTransform rect, ColorPickSurface surface, Vector2 point)
            {
                var local = new Vector2(Mathf.Lerp(rect.rect.xMin, rect.rect.xMax, point.x), Mathf.Lerp(rect.rect.yMin, rect.rect.yMax, point.y));
                var e = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left,
                    position = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(local)) };
                surface.OnPointerDown(e); surface.OnDrag(e); surface.OnPointerUp(e);
            }
            Drag(hue, hueInput, new Vector2(.5f, hueValue)); Drag(square, squareInput, new Vector2(saturation, value));
        }
#endif
    }

    internal sealed class ColorPickSurface : Selectable, IDragHandler, ISubmitHandler
    {
        internal Action<Vector2>? Picked;
        internal Action? Accepted;
        internal Vector2 Value;
        internal bool VerticalOnly;
        public override void OnPointerDown(PointerEventData e)
        { base.OnPointerDown(e); if (e.button == PointerEventData.InputButton.Left) Pick(e); }
        public void OnDrag(PointerEventData e) { if (e.button == PointerEventData.InputButton.Left) Pick(e); }
        private void Pick(PointerEventData e)
        {
            if (!IsActive() || !IsInteractable() || !RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)transform, e.position, e.pressEventCamera, out var p)) return;
            var rect = ((RectTransform)transform).rect;
            Value = new Vector2(Mathf.InverseLerp(rect.xMin, rect.xMax, p.x), Mathf.InverseLerp(rect.yMin, rect.yMax, p.y));
            Picked?.Invoke(Value);
        }
        public override void OnMove(AxisEventData e)
        {
            if (!IsActive() || !IsInteractable()) return;
            Value = VerticalOnly ? new Vector2(.5f, Mathf.Clamp01(Value.y + (e.moveVector.y + e.moveVector.x) / 100f)) :
                new Vector2(Mathf.Clamp01(Value.x + e.moveVector.x / 100f), Mathf.Clamp01(Value.y + e.moveVector.y / 100f));
            Picked?.Invoke(Value); e.Use();
        }
        public void OnSubmit(BaseEventData e) { if (IsActive() && IsInteractable()) { Accepted?.Invoke(); e.Use(); } }
    }
}
