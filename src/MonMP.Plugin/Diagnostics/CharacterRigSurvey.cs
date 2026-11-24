using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Inventories the game's rigged characters (NPC actors, the first-person player arms and every
    // loaded animator controller, avatar and clip) to choose a stock body for remote players.
    // Humanoid avatars can share clips between rigs and expose IK/look-at; generic ones cannot.
    internal static class CharacterRigSurvey
    {
        private static readonly HumanBodyBones[] KeyBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head, HumanBodyBones.LeftUpperArm,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightHand, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightFoot
        };

        internal static void Write(string directory)
        {
            var text = new StringBuilder();
            var previews = new List<(string Name, Transform Root)>();
            var previewed = new HashSet<string>();

            text.AppendLine("## Scene animators");
            foreach (var animator in UnityEngine.Object.FindObjectsOfType<Animator>(true).OrderBy(a => Path(a.transform)))
            {
                var actor = animator.GetComponentInParent<Actor>();
                var hands = animator.GetComponentInParent<PlayerVisualHands>();
                var root = actor != null ? actor.transform : animator.transform;
                var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (skinned.Length == 0 && actor == null && hands == null) continue;
                var controller = animator.runtimeAnimatorController;
                var avatar = animator.avatar;
                text.Append("animator ").Append(Path(animator.transform))
                    .Append(" | owner=").Append(actor != null ? actor.GetType().Name + "(" + actor.actor_Type + ")" : hands != null ? "PlayerVisualHands" : "-")
                    .Append(" | active=").Append(animator.gameObject.activeInHierarchy).Append(" enabled=").Append(animator.enabled)
                    .Append(" | controller=").Append(controller != null ? controller.name : "none")
                    .Append(" | avatar=").Append(avatar != null ? avatar.name + " human=" + avatar.isHuman + " valid=" + avatar.isValid : "none")
                    .Append(" | rootMotion=").Append(animator.applyRootMotion).Append(" cull=").Append(animator.cullingMode)
                    .AppendLine();
                if (animator.isActiveAndEnabled && controller != null)
                {
                    try { text.Append("  params: ").AppendLine(string.Join(", ", animator.parameters.Select(p => p.name + ":" + p.type))); }
                    catch (Exception ex) { text.Append("  params: unavailable ").AppendLine(ex.Message); }
                }
                if (controller != null)
                    text.Append("  clips: ").AppendLine(string.Join(", ", controller.animationClips.Where(c => c != null).Select(c => c.name + "(" + c.length.ToString("0.00") + "s" + (c.humanMotion ? ",human" : "") + (c.isLooping ? ",loop" : "") + ")").Distinct()));
                if (avatar != null && avatar.isHuman)
                    text.Append("  bones: ").AppendLine(string.Join(", ", KeyBones.Select(b => b + "=" + (animator.GetBoneTransform(b)?.name ?? "missing"))));
                foreach (var mesh in skinned)
                    text.Append("  mesh ").Append(Path(mesh.transform)).Append(" | shared=").Append(mesh.sharedMesh != null ? mesh.sharedMesh.name + " v" + mesh.sharedMesh.vertexCount : "none")
                        .Append(" | bones=").Append(mesh.bones.Length).Append(" rootBone=").Append(mesh.rootBone != null ? mesh.rootBone.name : "none")
                        .Append(" | materials=").Append(string.Join("/", mesh.sharedMaterials.Select(m => m == null ? "null" : m.name + "[" + (m.shader != null ? m.shader.name : "?") + (m.HasProperty("_Color") ? "," + m.color : "") + "]")))
                        .Append(" | height=").Append(mesh.bounds.size.y.ToString("0.00")).AppendLine();
                string key = (controller != null ? controller.name : "none") + "/" + string.Join("+", skinned.Select(s => s.sharedMesh != null ? s.sharedMesh.name : "?"));
                if (actor != null && root.gameObject.activeInHierarchy && skinned.Length > 0 && previewed.Add(key))
                    previews.Add((actor.GetType().Name + "-" + root.name, root));
            }

            // Most NPCs are posed skeletons without an animator: their bone rotations are the only
            // stock sitting/driving/standing poses.
            text.AppendLine().AppendLine("## Skinned character roots");
            foreach (var armature in UnityEngine.Object.FindObjectsOfType<Transform>(true).Where(t => t.name == "Armature" && t.Find("Hips") != null).OrderBy(t => Path(t)))
            {
                var root = armature.parent;
                if (root == null) continue;
                text.Append("character ").Append(Path(root)).Append(" | active=").Append(root.gameObject.activeInHierarchy)
                    .Append(" | components=").Append(string.Join(",", root.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name)))
                    .Append(" | children=").Append(string.Join(",", root.Cast<Transform>().Select(c => c.name + (c.gameObject.activeSelf ? "" : "(off)"))))
                    .Append(" | colliders=").Append(root.GetComponentsInChildren<Collider>(true).Length)
                    .Append(" | scripts=").Append(string.Join(",", root.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null).Select(m => m.GetType().Name).Distinct()))
                    .AppendLine();
                if (root.gameObject.activeInHierarchy && previewed.Add("pose/" + root.name) && previews.Count < 16)
                    previews.Add(("pose-" + root.name, root));
            }

            text.AppendLine().AppendLine("## Loaded animator controllers");
            foreach (var controller in Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>().OrderBy(c => c.name))
                text.Append("controller ").Append(controller.name).Append(" (").Append(controller.GetType().Name).Append("): ")
                    .AppendLine(string.Join(", ", controller.animationClips.Where(c => c != null).Select(c => c.name).Distinct()));

            text.AppendLine().AppendLine("## Loaded avatars");
            foreach (var avatar in Resources.FindObjectsOfTypeAll<Avatar>().OrderBy(a => a.name))
                text.Append("avatar ").Append(avatar.name).Append(" human=").Append(avatar.isHuman).Append(" valid=").Append(avatar.isValid).AppendLine();

            text.AppendLine().AppendLine("## Loaded locomotion-like clips");
            foreach (var clip in Resources.FindObjectsOfTypeAll<AnimationClip>().Where(c => IsLocomotion(c.name)).OrderBy(c => c.name))
                text.Append("clip ").Append(clip.name).Append(" ").Append(clip.length.ToString("0.00")).Append("s human=").Append(clip.humanMotion)
                    .Append(" loop=").Append(clip.isLooping).Append(" legacy=").Append(clip.legacy).AppendLine();

            text.AppendLine().AppendLine("## Previews");
            foreach (var (name, root) in previews.Take(16))
            {
                string file = "rig-preview-" + Sanitize(name) + ".bmp";
                try { AvatarPreviewCapture.SaveBitmap(root, System.IO.Path.Combine(directory, file)); text.Append("preview ").AppendLine(file); }
                catch (Exception ex) { text.Append("preview ").Append(file).Append(" failed: ").AppendLine(ex.Message); }
            }
            File.WriteAllText(System.IO.Path.Combine(directory, "character-rigs.txt"), text.ToString());
        }

        private static bool IsLocomotion(string name)
        {
            string n = name.ToLowerInvariant();
            return new[] { "walk", "run", "idle", "sit", "crouch", "drive", "driving", "jump", "strafe", "turn", "sprint", "fall", "land", "stand", "talk", "wave" }.Any(n.Contains);
        }
        private static string Sanitize(string name) => new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
