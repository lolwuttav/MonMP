#if MONMP_DEVELOPMENT
using System;
using System.IO;
using System.Linq;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.UI
{
    internal sealed partial class HoldPositionEditor
    {
        // Called only on the isolated fixture's editor/store, never the live editor.
        internal object ProbeMap(string directory)
        {
            var choice = choices.Single(c => c.HandMap);
            Select(choice);
            if (draft == null || handPropPreview == null || body == null) throw new InvalidOperationException("Map catalog preview did not initialize: " + message);
            draft.ItemX = .03f; draft.ItemY = -.02f; draft.ItemPitch = 8;
            var expected = draft.Copy(); Save();
            var reloaded = new HeldGripOverrides(overrides.Path);
            if (!reloaded.TryGetNamedRuntime(RemoteMapCalibration.Key, choice.Entry.Definition.ShapeHash, out var saved) ||
                saved.ItemX != expected.ItemX || saved.ItemY != expected.ItemY || saved.ItemPitch != expected.ItemPitch || !saved.TwoHanded)
                throw new InvalidOperationException("Map calibration did not survive save/reload: " + message);
            RenderPreview();
            var previewPosition = itemCopy!.transform.position; var previewRotation = itemCopy.transform.rotation;
            var look = StockAvatarBody.Overlay.Look(pitch); look.Map = 1;
            body.Present(StockAvatarBody.Mode.Stand, 0, 0, crouch, look, 1);
            body.Solve(new StockAvatarBody.Goals { MapProp = handPropPreview, MapCalibration = saved }, 1);
            float positionError = Vector3.Distance(previewPosition, itemCopy.transform.position);
            float rotationError = Quaternion.Angle(previewRotation, itemCopy.transform.rotation);
            float rightError = Vector3.Distance(body.RightPalm, itemCopy.transform.TransformPoint(saved.Position));
            float leftError = Vector3.Distance(body.LeftPalm, itemCopy.transform.TransformPoint(saved.LeftPosition));
            if (positionError > .002f || rotationError > .1f || rightError > .012f || leftError > .012f)
                throw new InvalidOperationException($"Map runtime/preview mismatch: position={positionError}, rotation={rotationError}, palms={rightError}/{leftError}");
            string shape = RemoteMapCalibration.ShapeOf(itemCopy);
            var overlay = new GameObject(RemoteMapCalibration.OverlayName); overlay.transform.SetParent(itemCopy.transform, false);
            overlay.AddComponent<MeshFilter>().sharedMesh = itemCopy.GetComponentInChildren<MeshFilter>().sharedMesh;
            if (RemoteMapCalibration.ShapeOf(itemCopy) != shape) throw new InvalidOperationException("Player-marker overlays changed map identity.");
            RemoteMapCalibration.RemoveOverlay(itemCopy);
            if (itemCopy.GetComponentsInChildren<MonoBehaviour>(true).Length != 0 || itemCopy.GetComponentsInChildren<Collider>(true).Length != 0)
                throw new InvalidOperationException("Map preview contains gameplay components.");
            Tick();
            return new { selected = choice.Label, persisted = true, saved.TwoHanded, positionError, rotationError, rightError, leftError,
                store = overrides.Path, nativeMapUnmodified = true };
        }
        internal object ProbeFlashlight()
        {
            var choice = choices.Single(c => c.HandFlashlight); Select(choice);
            if (draft == null || handPropPreview == null || body == null) throw new InvalidOperationException("Flashlight preview did not initialize: " + message);
            draft.ItemX = .02f; draft.ItemY = -.025f; draft.ItemPitch = 12;
            Save(); var reloaded = new HeldGripOverrides(overrides.Path);
            if (!reloaded.TryGetNamedRuntime(RemoteFlashlightCalibration.Key, choice.Entry.Definition.ShapeHash, out var saved) ||
                saved.ItemX != .02f || saved.ItemY != -.025f || saved.ItemPitch != 12 || saved.TwoHanded)
                throw new InvalidOperationException("Flashlight calibration did not persist: " + message);
            float frameDrift = 0, palmError = 0;
            foreach (float duck in new[] { 0f, 1f }) foreach (float lookPitch in new[] { -25f, 0f, 30f })
            {
                crouch = duck; pitch = lookPitch; RenderPreview();
                var position = itemCopy!.transform.position; var rotation = itemCopy.transform.rotation;
                for (int i = 0; i < 3; i++)
                {
                    var look = StockAvatarBody.Overlay.Look(pitch); look.Flashlight = 1;
                    body.Present(StockAvatarBody.Mode.Stand, 0, 0, crouch, look, 1);
                    body.Solve(new StockAvatarBody.Goals { FlashlightProp = handPropPreview, FlashlightCalibration = saved }, 1);
                    frameDrift = Mathf.Max(frameDrift, Vector3.Distance(position, itemCopy.transform.position));
                    if (Quaternion.Angle(rotation, itemCopy.transform.rotation) > .1f) throw new InvalidOperationException("Flashlight preview/runtime rotation mismatch.");
                    palmError = Mathf.Max(palmError, Vector3.Distance(body.LeftPalm, itemCopy.transform.TransformPoint(saved.Position)));
                }
            }
            if (frameDrift > .002f || palmError > .012f) throw new InvalidOperationException($"Flashlight frame/grip mismatch: {frameDrift}/{palmError}");
            if (itemCopy!.GetComponentsInChildren<MonoBehaviour>(true).Length != 0 || itemCopy.GetComponentsInChildren<Light>(true).Length != 0)
                throw new InvalidOperationException("Flashlight preview contains native scripts or lights.");
            crouch = pitch = 0; Tick();
            return new { selected = choice.Label, persisted = true, nativeHand = "left", frameDrift, palmError, store = overrides.Path };
        }
        internal void CaptureMapProbe(string path)
        {
            Tick();
            if (texture == null) throw new InvalidOperationException("Map preview texture unavailable.");
            var old = RenderTexture.active; var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            try { RenderTexture.active = texture; image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); image.Apply(); File.WriteAllBytes(path, image.EncodeToPNG()); }
            finally { RenderTexture.active = old; UnityEngine.Object.Destroy(image); }
        }
    }
}
#endif
