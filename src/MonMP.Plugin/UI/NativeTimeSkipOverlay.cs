using System;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MonMP.Plugin.UI
{
    // Borrow the authored native overlay. Its SpecialMenu keeps the real text
    // reference and host Started/Completed callbacks keep their native effects.
    internal sealed class NativeTimeSkipOverlay : IDisposable
    {
        private static readonly System.Reflection.FieldInfo Window = AccessTools.Field(typeof(SpecialMenu), "Window_FastFowardingTime");
        private static readonly System.Reflection.FieldInfo DateText = AccessTools.Field(typeof(SpecialMenu), "TEXT_FastFowardingTime");
        private SpecialMenu? owner;
        private GameObject? window;
        private RectTransform? rectangle;
        private UI_ClockCalendar? calendar;
        private Transform? parent;
        private Transform? nativeCanvas;
        private int sibling;
        private bool originalActive, host;
        private Vector2 anchorMin, anchorMax, pivot, size, position;
        private Vector3 localPosition, localScale;
        private Quaternion localRotation;

        internal void Show(bool isHost, bool visible)
        {
            if (window == null)
            {
                foreach (var candidate in UnityEngine.Object.FindObjectsOfType<SpecialMenu>(true))
                {
                    var authored = Window.GetValue(candidate) as GameObject;
                    if (authored == null) continue;
                    var canvas = authored.GetComponentInParent<Canvas>(true);
                    if (canvas == null || !canvas.gameObject.activeInHierarchy) continue;
                    owner = candidate; window = authored; nativeCanvas = canvas.transform; break;
                }
                if (window == null) return;
                host = isHost;
                originalActive = window.activeSelf;
                var transform = window.transform;
                parent = transform.parent; sibling = transform.GetSiblingIndex();
                localPosition = transform.localPosition; localRotation = transform.localRotation; localScale = transform.localScale;
                rectangle = transform as RectTransform;
                if (rectangle != null)
                {
                    anchorMin = rectangle.anchorMin; anchorMax = rectangle.anchorMax; pivot = rectangle.pivot;
                    size = rectangle.sizeDelta; position = rectangle.anchoredPosition;
                }
                transform.SetParent(nativeCanvas, false);
                transform.SetAsLastSibling();
            }
            window.SetActive(visible);
            if (calendar == null) calendar = UnityEngine.Object.FindObjectOfType<UI_ClockCalendar>();
            var text = owner != null ? DateText.GetValue(owner) as TextMeshProUGUI : null;
            if (calendar != null && text != null) text.text = calendar.Get_DateText();
        }

        internal void End()
        {
            if (window != null)
            {
                var transform = window.transform;
                transform.SetParent(parent, false);
                transform.SetSiblingIndex(sibling);
                transform.localPosition = localPosition; transform.localRotation = localRotation; transform.localScale = localScale;
                if (rectangle != null)
                {
                    rectangle.anchorMin = anchorMin; rectangle.anchorMax = anchorMax; rectangle.pivot = pivot;
                    rectangle.sizeDelta = size; rectangle.anchoredPosition = position;
                }
                // Host native Started can precede capture. Once the shared skip
                // ends, its window must stay hidden rather than restore that true.
                window.SetActive(!host && originalActive);
            }
            owner = null; window = null; rectangle = null; parent = null; nativeCanvas = null; calendar = null;
        }
        public void Dispose()
        {
            End();
        }
    }
}
