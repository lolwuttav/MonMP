using System;
using System.IO;
using MonMP.Core.Protocol;
using UnityEngine;

namespace MonMP.Plugin.UI
{
    // Four small, non-readable layers for the current view. Upper skin/hair and
    // fabric have mutual native depth occlusion baked into their alpha channels.
    // No preview camera, world, avatar animator, scene search or recurring render
    // work is required.
    internal sealed class AppearancePortrait : IDisposable
    {
        internal readonly Texture2D?[] Layers = new Texture2D?[4];
        private PlayerAppearance appearance;
        private bool back, loaded;
        internal bool Select(PlayerAppearance selected, bool rear)
        {
            if (loaded && appearance.SameGeometry(selected) && appearance.NativeTexture == selected.NativeTexture && back == rear) return true;
            Dispose(); appearance = selected; back = rear;
            try
            {
                selected.Validate();
                byte[] ids = { selected.Shoes, selected.Legs };
                string[] parts = { "shoes", "legs" };
                for (int i = 0; i < ids.Length; i++)
                    Layers[i] = Load(PlayerModelPolicy.Key(ids[i]) + (rear ? "-back-" : "-front-") + parts[i] + ".png");
                Layers[2] = Load(AvatarPartChoices.UpperPortrait(selected.Torso, selected.Head, rear, "skin"));
                Layers[3] = Load(AvatarPartChoices.UpperPortrait(selected.Torso, selected.Head, rear, selected.NativeTexture ? "nativefabric" : "fabric"));
                loaded = true; return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("MonMP character preview unavailable: " + ex.Message); Dispose(); return false;
            }
        }
        private static Texture2D Load(string file)
        {
            using var stream = typeof(AppearancePortrait).Assembly.GetManifestResourceStream("MonMP.Appearance." + file)
                ?? throw new InvalidDataException("Missing character portrait.");
            if (stream.Length <= 0 || stream.Length > 512 * 1024) throw new InvalidDataException("Invalid character portrait size.");
            using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "MonMP character preview", wrapMode = TextureWrapMode.Clamp };
            try
            {
                if (!ImageConversion.LoadImage(texture, bytes.ToArray(), markNonReadable: true) || texture.width != 304 || texture.height != 480)
                    throw new InvalidDataException("Invalid character portrait dimensions.");
                return texture;
            }
            catch { UnityEngine.Object.Destroy(texture); throw; }
        }
        public void Dispose()
        {
            for (int i = 0; i < Layers.Length; i++) { if (Layers[i] != null) UnityEngine.Object.Destroy(Layers[i]); Layers[i] = null; }
            loaded = false;
        }
    }
}
