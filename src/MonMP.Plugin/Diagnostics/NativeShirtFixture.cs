using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Bounded visual checks of the actual selfView and remotePlayers bodies, rather than a second
    // independently constructed rig. Native first-person rendering and every borrowed asset survive.
    internal sealed class NativeShirtFixture : IDisposable
    {
        private sealed class CapturedAssets
        {
            internal string Name = "";
            internal Mesh Mesh = null!;
            internal Material Material = null!;
        }
        private readonly MonMPPlugin plugin;
        private readonly string directory;
        private readonly Action<string, object> write;
        private readonly Action<Gameplay, string> firstPersonCapture;
        private readonly List<CapturedAssets> captured = new List<CapturedAssets>();
        private readonly Mesh sourceMesh;
        private readonly Material sourceMaterial;
        private readonly Texture sourceMain, sourceNormal;
        private readonly Vector2 sourceScale, sourceOffset;
        private readonly string sourceUvHash;
        private readonly string[] sourceKeywords;
        private readonly float sourceBumpScale;
        private readonly List<(Mesh Mesh, string Name, bool Readable, string Gpu)> borrowedMeshes = new List<(Mesh, string, bool, string)>();
        private string command = "";
        private int phase, frames;
        private float started;
        private PlayerVisualHands? hands;
        private Animator? animator;
        private float speed;
        private UI.FirstPersonEmoteGesture? gesture;
        private object? avatarReport;
        internal bool Running { get; private set; }

        internal NativeShirtFixture(MonMPPlugin plugin, Gameplay gameplay, string directory,
            Action<string, object> write, Action<Gameplay, string> firstPersonCapture)
        {
            this.plugin = plugin; this.directory = directory; this.write = write; this.firstPersonCapture = firstPersonCapture;
            // Native ChangeShirt lazily instances the hands material. Complete that ordinary native
            // setup before snapshotting ownership, so later color selections keep the same instance.
            gameplay.Player.Get_VisualHands.ChangeShirt(gameplay.Player.Get_VisualHands.Get_ShirtColor);
            var renderer = NativeShirt(gameplay);
            sourceMesh = renderer.sharedMesh; sourceMaterial = renderer.sharedMaterial;
            sourceMain = sourceMaterial.mainTexture; sourceNormal = sourceMaterial.GetTexture("_BumpMap");
            sourceScale = sourceMaterial.mainTextureScale; sourceOffset = sourceMaterial.mainTextureOffset;
            sourceKeywords = sourceMaterial.shaderKeywords; sourceBumpScale = sourceMaterial.GetFloat("_BumpScale");
            sourceUvHash = UvHash(renderer);
            foreach (var native in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh != null && (r == renderer || r.name == "Shirt_LongSleeve" || r.name == "Shirt_Plaid"))
                .Where(r => !RuntimeProbe.CanonicalPath(r.transform).Contains("MonMP"))
                .GroupBy(r => r.sharedMesh.GetInstanceID()).Select(g => g.First()))
                borrowedMeshes.Add((native.sharedMesh, native.sharedMesh.name + ":" + native.sharedMesh.GetInstanceID(), native.sharedMesh.isReadable, NativeShirtMeshProbe.BufferHash(native.sharedMesh)));
        }

        internal void Begin(string id)
        {
            if (Running) throw new InvalidOperationException("A native shirt capture is already running.");
            if (plugin.Session?.IsReady != true || plugin.World?.Channel.FullySynchronized != true)
                throw new InvalidOperationException("Native shirt capture requires a ready paired session.");
            command = id; frames = phase = 0; Running = true;
        }

        internal void Tick(Gameplay gameplay)
        {
            if (!Running) return;
            try
            {
                if (++frames < 3) return;
                if (phase == 0)
                {
                    uint other = plugin.Session!.Peers.Keys.First(p => p != plugin.Session.LocalPeerId);
                    var remote = plugin.RemoteAvatarRoot(other) ?? throw new InvalidOperationException("Remote avatar is not ready.");
                    var selfView = (RemotePlayers)AccessTools.Field(typeof(MonMPPlugin), "selfView").GetValue(plugin);
                    uint selfId = plugin.Session.LocalPeerId;
                    var self = selfView.TestRoot(selfId) ?? throw new InvalidOperationException("Self-view avatar is not ready.");
                    string previousView = selfView.TestLocalView(selfId);
                    selfView.SetLocalAvatarView(selfId, LocalAvatarView.Visible);
                    try
                    {
                    var selfShirt = Shirt(self); var remoteShirt = Shirt(remote);
                    var native = NativeShirt(gameplay); var color = native.sharedMaterial.color;
                    Require(SameColor(selfShirt.sharedMaterial.color, color), "Self-view shirt did not retain selected native color.");
                    Require(SameColor(remoteShirt.sharedMaterial.color, color), "Remote shirt did not receive selected native color.");
                    Require(selfShirt.sharedMaterial.mainTexture == sourceMain && remoteShirt.sharedMaterial.mainTexture == sourceMain,
                        "Avatar shirts do not borrow the native first-person plaid texture.");
                    Require(selfShirt.sharedMaterial.GetTexture("_BumpMap") == sourceNormal && remoteShirt.sharedMaterial.GetTexture("_BumpMap") == sourceNormal,
                        "Avatar shirts do not borrow the native first-person normal texture.");
                    Require(selfShirt.sharedMesh != sourceMesh && remoteShirt.sharedMesh != sourceMesh,
                        "Avatar adopted the incompatible first-person mesh.");
                    Require(!borrowedMeshes.Any(b => b.Mesh == selfShirt.sharedMesh || b.Mesh == remoteShirt.sharedMesh),
                        "Avatar did not create a private remapped mesh.");
                    captured.Add(new CapturedAssets { Name = command + "-self", Mesh = selfShirt.sharedMesh, Material = selfShirt.sharedMaterial });
                    captured.Add(new CapturedAssets { Name = command + "-remote", Mesh = remoteShirt.sharedMesh, Material = remoteShirt.sharedMaterial });
                        foreach (AvatarPreviewCapture.View view in Enum.GetValues(typeof(AvatarPreviewCapture.View)))
                        {
                            AvatarPreviewCapture.SaveBitmap(self, Path.Combine(directory, command + "-self-" + view.ToString().ToLowerInvariant() + ".bmp"), view);
                            AvatarPreviewCapture.SaveBitmap(remote, Path.Combine(directory, command + "-remote-" + view.ToString().ToLowerInvariant() + ".bmp"), view);
                        }
                        NativeShirtCloseCapture.Save(remote, remoteShirt, directory, command);
                        var avatars = (System.Collections.IDictionary)AccessTools.Field(typeof(RemotePlayers), "avatars").GetValue(selfView);
                        object selfAvatar = avatars[selfId]!;
                        var selfBody = (StockAvatarBody)AccessTools.Field(selfAvatar.GetType(), "Body").GetValue(selfAvatar);
                        avatarReport = new { source = Describe(native), self = Describe(selfShirt), remote = Describe(remoteShirt), selfViewBefore = previousView, selfSources = selfBody.Sources, remoteSources = plugin.RemoteAvatarBody(other) };
                    }
                    finally { selfView.SetLocalAvatarView(selfId, previousView.Contains("localShadowOnly=True") ? LocalAvatarView.ShadowOnly : LocalAvatarView.Visible); }
                    hands = gameplay.Player.Get_VisualHands;
                    animator = (Animator)AccessTools.Field(typeof(PlayerVisualHands), "animator").GetValue(hands);
                    speed = animator.speed; animator.speed = 0;
                    gesture = new UI.FirstPersonEmoteGesture();
                    var camera = gameplay.PlayerWalking.playerCameraController.cam3DInFrontOfEverything;
                    Require(gesture.Begin(hands, camera, PlayerEmoteKind.Wave), "Native sleeve reference gesture was unavailable.");
                    started = Time.unscaledTime; phase = 1;
                }
                if (phase == 1)
                {
                    gesture!.RestoreBeforeAnimation(); gesture.Apply();
                    if (Time.unscaledTime - started < 0.55f) return;
                    firstPersonCapture(gameplay, command + "-first-person");
                    gesture.Cancel(); gesture = null;
                    Require(SourceUnchanged(gameplay), "Shirt setup mutated a borrowed first-person mesh, material or texture setting.");
                    write(command, new
                    {
                        passed = true, shirt = hands!.Get_ShirtColor,
                        evidence = "Baked previews of actual runtime selfView and remotePlayers bodies plus actual native world/foreground camera sleeve reference.",
                        avatars = avatarReport, nativeSourceUnchanged = true
                    });
                    Stop();
                }
            }
            catch (Exception ex) { write(command, new { failed = true, error = ex.ToString() }); Stop(); }
        }

        internal object AssetStatus(Gameplay gameplay) => new
        {
            nativeSourceUnchanged = SourceUnchanged(gameplay), nativeMeshAlive = sourceMesh != null,
            nativeMaterialAlive = sourceMaterial != null, nativeMainTextureAlive = sourceMain != null, nativeNormalTextureAlive = sourceNormal != null,
            cachedMeshes = StockAvatarShirt.CachedMeshCount, cachedLeases = StockAvatarShirt.CachedLeaseCount,
            borrowedNativeMeshes = borrowedMeshes.Select(b => new { b.Name, alive = b.Mesh != null, readabilityUnchanged = b.Mesh != null && b.Mesh.isReadable == b.Readable, beforeGpuHash = b.Gpu, afterGpuHash = b.Mesh != null ? NativeShirtMeshProbe.BufferHash(b.Mesh) : "destroyed" }).ToArray(),
            assets = captured.Select(c => new { c.Name, meshDestroyed = c.Mesh == null, materialDestroyed = c.Material == null }).ToArray()
        };

        private bool SourceUnchanged(Gameplay gameplay)
        {
            var renderer = NativeShirt(gameplay); var material = renderer.sharedMaterial;
            return renderer.sharedMesh == sourceMesh && material == sourceMaterial && material.mainTexture == sourceMain &&
                material.GetTexture("_BumpMap") == sourceNormal && material.mainTextureScale == sourceScale &&
                material.mainTextureOffset == sourceOffset && material.shaderKeywords.SequenceEqual(sourceKeywords) &&
                material.GetFloat("_BumpScale") == sourceBumpScale && UvHash(renderer) == sourceUvHash;
        }

        private static SkinnedMeshRenderer NativeShirt(Gameplay gameplay) =>
            (SkinnedMeshRenderer)AccessTools.Field(typeof(PlayerVisualHands), "Mesh_Shirt").GetValue(gameplay.Player.Get_VisualHands);
        private static SkinnedMeshRenderer Shirt(Transform root) => root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .First(r => r.name == "Shirt_LongSleeve");
        private static bool SameColor(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a) < 0.0001f;
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static object Describe(SkinnedMeshRenderer renderer)
        {
            var material = renderer.sharedMaterial;
            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked); var uv = baked.uv;
                return new
                {
                    path = RuntimeProbe.CanonicalPath(renderer.transform), mesh = renderer.sharedMesh.name,
                    meshId = renderer.sharedMesh.GetInstanceID(), materialId = material.GetInstanceID(), bones = renderer.bones.Length, vertices = baked.vertexCount,
                    uv = new { min = new[] { uv.Min(v => v.x), uv.Min(v => v.y) }, max = new[] { uv.Max(v => v.x), uv.Max(v => v.y) }, hash = UvHash(renderer) },
                    material = new
                    {
                        material.name, shader = material.shader.name, color = new[] { material.color.r, material.color.g, material.color.b, material.color.a },
                        mainTexture = material.mainTexture.name, mainTextureId = material.mainTexture.GetInstanceID(),
                        normalTexture = material.GetTexture("_BumpMap")?.name, normalTextureId = material.GetTexture("_BumpMap")?.GetInstanceID(),
                        scale = new[] { material.mainTextureScale.x, material.mainTextureScale.y }, offset = new[] { material.mainTextureOffset.x, material.mainTextureOffset.y },
                        bumpScale = material.GetFloat("_BumpScale"), keywords = material.shaderKeywords
                    }
                };
            }
            finally { UnityEngine.Object.Destroy(baked); }
        }
        private static string UvHash(SkinnedMeshRenderer renderer)
        {
            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked);
                ulong hash = 14695981039346656037UL;
                foreach (var uv in baked.uv)
                    foreach (float component in new[] { uv.x, uv.y })
                        foreach (byte value in BitConverter.GetBytes(component)) hash = unchecked((hash ^ value) * 1099511628211UL);
                return hash.ToString("X16");
            }
            finally { UnityEngine.Object.Destroy(baked); }
        }
        private void Stop()
        {
            gesture?.Cancel(); gesture = null;
            if (animator != null) animator.speed = speed;
            animator = null; hands = null; Running = false;
        }
        public void Dispose() => Stop();
    }
}
