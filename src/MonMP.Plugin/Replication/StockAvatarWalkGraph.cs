using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace MonMP.Plugin.Replication
{
    internal static class StockAvatarWalkGraph
    {
        internal static PlayableGraph Create(Animator animator, AnimationClip clip, out AnimationClipPlayable playable)
        {
            // Actor.ChangePose(NoAnimation) disables native NPC animators. A cloned decoration
            // can inherit that state even though its bones still accept procedural emotes.
            animator.enabled = true;
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("MonMP remote walk");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                playable = AnimationClipPlayable.Create(graph, clip);
                AnimationPlayableOutput.Create(graph, "walk", animator).SetSourcePlayable(playable);
                return graph;
            }
            catch
            {
                graph.Destroy();
                throw;
            }
        }

        internal static void Evaluate(PlayableGraph graph, AnimationClipPlayable playable, float time, Transform body, Transform? armature = null)
        {
            // The clip owns the skeleton only. Keep placement, body yaw, lying offsets and
            // height calibration under RemotePlayers/StockAvatarBody even if it has root keys.
            var position = body.localPosition;
            var rotation = body.localRotation;
            var scale = body.localScale;
            // Actor_Walking binds both translation and rotation on Armature itself. It is
            // outside the posed bone list: restoring only StockBody leaves this inner rig
            // free to lag/turn independently of the network root and its nameplate.
            var rigPosition = armature != null ? armature.localPosition : Vector3.zero;
            var rigRotation = armature != null ? armature.localRotation : Quaternion.identity;
            var rigScale = armature != null ? armature.localScale : Vector3.one;
            try
            {
                playable.SetTime(time);
                graph.Evaluate(0f);
            }
            finally
            {
                AvatarTransformWrites.LocalPosition(body, position);
                AvatarTransformWrites.LocalRotation(body, rotation);
                AvatarTransformWrites.LocalScale(body, scale);
                if (armature != null)
                {
                    AvatarTransformWrites.LocalPosition(armature, rigPosition);
                    AvatarTransformWrites.LocalRotation(armature, rigRotation);
                    AvatarTransformWrites.LocalScale(armature, rigScale);
                }
            }
        }
    }
}
