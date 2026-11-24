using System;
using System.Collections.Generic;
using MonMP.Core.Input;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MonMP.Plugin.UI
{
    // One retained marker per player. Click-time physics only; the ordinary presentation loop
    // projects existing targets and refreshes distance text five times per second.
    internal sealed class SocialPings : IDisposable
    {
        private sealed class Marker
        {
            internal RectTransform Root = null!;
            internal TextMeshProUGUI Label = null!;
            internal string Name = "";
            internal Vector3 Position;
            internal float Expires, PointUntil, TextAt;
        }
        private readonly MonMPPlugin plugin;
        private readonly NetworkSession session;
        private readonly Func<EmoteWheelSlot, bool> point;
        private readonly Dictionary<uint, Marker> markers = new Dictionary<uint, Marker>();
        private readonly List<uint> expired = new List<uint>(16);
        private RaycastHit[] hits = new RaycastHit[32];
        private RectTransform? root;
        private float nextClick;
        private bool disposed;
        internal SocialPings(MonMPPlugin plugin, NetworkSession session, Func<EmoteWheelSlot, bool> point)
        {
            this.plugin = plugin; this.session = session; this.point = point;
            session.PingReceived += Receive; session.PeerLeft += Remove;
        }
        internal void Tick()
        {
            if (disposed || Mouse.current?.middleButton.wasPressedThisFrame != true || Time.unscaledTime < nextClick) return;
            var game = plugin.Gameplay;
            if (!session.IsReady || plugin.World == null || !plugin.World.Channel.FullySynchronized || plugin.World.SaveGate.Active ||
                plugin.SocialUiBlocked || plugin.Spectating || game?.Player?.mainCamera == null || game.Get_isGamePaused ||
                game.Get_IsDead || game.Get_IsSleeping || !EmoteGameplayPolicy.IsWheelDisplayState(game.Get_PlayerState) ||
                game.Player.Get_VisualHands.Get_IsMapOpen || game.Player.Get_VisualHands.Get_isGameConsoleOpen ||
                UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>()?.Get_isOpen == true) return;
            var camera = game.Player.mainCamera;
            int count;
            do
            {
                count = Physics.RaycastNonAlloc(camera.transform.position, camera.transform.forward, hits, SocialPing.MaxDistance,
                    ~LayerMask.GetMask("Ignore Raycast", "Player", "UI"), QueryTriggerInteraction.Ignore);
                if (count < hits.Length || hits.Length >= 256) break;
                Array.Resize(ref hits, hits.Length * 2);
            } while (true);
            float nearest = float.PositiveInfinity; Vector3 target = default;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.collider == null || hit.collider.transform.IsChildOf(game.Player.transform) ||
                    game.PlayerWalking != null && hit.collider.transform.IsChildOf(game.PlayerWalking.transform) || hit.distance >= nearest) continue;
                nearest = hit.distance; target = hit.point;
            }
            if (float.IsPositiveInfinity(nearest)) return;
            bool canPoint = game.Player.Get_HoldingItem == null && game.Player.Get_ActiveTool == null;
            if (!session.SendPing(target.x, target.y, target.z, canPoint)) return;
            nextClick = Time.unscaledTime + 1;
            plugin.Activity.RecordAction();
            if (canPoint) point(EmoteWheelSlot.Point);
        }
        private void Receive(SocialPing ping)
        {
            if (disposed || !session.Peers.TryGetValue(ping.PeerId, out var peer)) return;
            if (root == null)
            {
                root = MenuKit.Canvas("MonMP Pings", 440);
                root.GetComponent<GraphicRaycaster>().enabled = false;
                var group = root.gameObject.AddComponent<CanvasGroup>(); group.interactable = group.blocksRaycasts = false;
                root.gameObject.SetActive(true);
            }
            if (!markers.TryGetValue(ping.PeerId, out var marker))
            {
                marker = new Marker { Root = MenuKit.Rect("Ping", root).Center(280, 66), Name = peer.DisplayName };
                var diamond = MenuKit.Rect("Target", marker.Root);
                diamond.anchorMin = diamond.anchorMax = diamond.pivot = new Vector2(.5f, 1);
                diamond.sizeDelta = new Vector2(16, 16); diamond.anchoredPosition = Vector2.zero; diamond.localRotation = Quaternion.Euler(0, 0, 45);
                var color = new Color(((peer.ColorRgb >> 16) & 255) / 255f, ((peer.ColorRgb >> 8) & 255) / 255f, (peer.ColorRgb & 255) / 255f);
                MenuKit.Fill(diamond, color, rounded: false).raycastTarget = false;
                var caption = MenuKit.Rect("Caption", marker.Root).Stretch(0, 0, 0, 28);
                MenuKit.Fill(caption, new Color(0, 0, 0, .35f), rounded: false).raycastTarget = false;
                marker.Label = MenuKit.Label(caption, "", 22, MenuKit.Semibold, color, TextAlignmentOptions.Center);
                marker.Label.rectTransform.Stretch(8, 0, 8, 0); marker.Label.enableAutoSizing = true;
                marker.Label.fontSizeMin = 16; marker.Label.fontSizeMax = 22;
                markers.Add(ping.PeerId, marker);
            }
            marker.Position = new Vector3(ping.X, ping.Y, ping.Z);
            marker.Expires = Time.unscaledTime + SocialPing.Seconds;
            marker.PointUntil = ping.Point ? Time.unscaledTime + 1.8f : 0;
            marker.TextAt = 0;
            if (ping.Point) plugin.PointRemotePlayer(ping.PeerId);
        }
        internal Vector3? PointTarget(uint peer) => markers.TryGetValue(peer, out var marker) && Time.unscaledTime < marker.PointUntil ? marker.Position : (Vector3?)null;
        internal void LateTick()
        {
            if (disposed || root == null) return;
            var camera = plugin.Gameplay?.Player?.mainCamera;
            bool show = camera != null && !plugin.SocialUiBlocked && !MonMPPlugin.CleanFrame && plugin.Gameplay?.Get_isGamePaused != true;
            root.gameObject.SetActive(show); expired.Clear();
            foreach (var pair in markers)
            {
                var marker = pair.Value;
                if (Time.unscaledTime >= marker.Expires) { expired.Add(pair.Key); continue; }
                if (!show) continue;
                var screen = plugin.ProjectPresentation(camera!, marker.Position);
                if (screen.z < 0) { screen.x = Screen.width - screen.x; screen.y = Screen.height - screen.y; }
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, null, out var local);
                var bounds = root.rect;
                local.x = Mathf.Clamp(local.x, bounds.xMin + 150, bounds.xMax - 150);
                local.y = Mathf.Clamp(local.y, bounds.yMin + 45, bounds.yMax - 120);
                marker.Root.anchoredPosition = local;
                marker.Label.alpha = Mathf.Clamp01((marker.Expires - Time.unscaledTime) / .75f);
                if (Time.unscaledTime >= marker.TextAt)
                {
                    marker.TextAt = Time.unscaledTime + .2f;
                    marker.Label.text = marker.Name + "  ·  " + Vector3.Distance(camera!.transform.position, marker.Position).ToString("0") + " m";
                }
            }
            foreach (uint peer in expired) Remove(peer);
        }
        private void Remove(uint peer)
        {
            if (!markers.TryGetValue(peer, out var marker)) return;
            UnityEngine.Object.Destroy(marker.Root.gameObject); markers.Remove(peer);
        }
        public void Dispose()
        {
            if (disposed) return;
            session.PingReceived -= Receive; session.PeerLeft -= Remove;
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
            markers.Clear(); root = null; disposed = true;
        }
    }
}
