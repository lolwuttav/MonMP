using System;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class ConsumptionPresentation
    {
        private enum Cue : byte { Food = 1, Drink, SyrupCan, SyrupBottleFood, SyrupBottleDrink, Wine, SpoiledFood }

        internal static void LocalOpen(Type type)
        {
            var sound = Singleton<SOUND_Manager>.i;
            if (sound == null) return;
            if (type == typeof(Item_MapleSyrupCan)) sound.PlaySound_OpenCan();
            else if (type == typeof(Item_MapleSyrup)) sound.PlaySound_UnscrewCap();
        }

        // Match the reviewed native use effects, including coffee and wine whose
        // sound is chosen by their override rather than their prefab category.
        private static byte Variant(Type type, InteractableItemType? kind, ItemValues values, bool hardMode)
        {
            if (type == typeof(Item_WeedPreRollPack)) return 0; // Native inventory refill is silent.
            if (type == typeof(Item_MapleSyrupCan)) return (byte)Cue.SyrupCan;
            if (type == typeof(Item_MapleWineBottle)) return (byte)Cue.Wine;
            if (type == typeof(Item_TomHoustonCup) || type == typeof(Item_HomemadeCoffeeMug)) return (byte)Cue.Drink;
            if (type == typeof(Item_Poutine)) return (byte)(hardMode && values.Value1 <= 0 ? Cue.SpoiledFood : Cue.Food);
            if (type == typeof(Item_MapleSyrup))
                return kind == InteractableItemType.Food ? (byte)Cue.SyrupBottleFood :
                    kind == InteractableItemType.Drink ? (byte)Cue.SyrupBottleDrink : (byte)0;
            return kind == InteractableItemType.Food ? (byte)Cue.Food :
                kind == InteractableItemType.Drink ? (byte)Cue.Drink : (byte)0;
        }

        internal static void Announce(ulong item, Type type, GameObject asset, ItemState state, Vector3 at, uint actor, bool hardMode)
        {
            byte variant = Variant(type, asset.GetComponent<InteractableItems>()?.interactableItemType, state.Values, hardMode);
            if (variant == 0) return;
            // PersonalUse already played on the consumer. Only a completed, accepted
            // effect creates a cue; a rejected request and a replayed receipt stay silent.
            PresentationReplays.Events?.HostAnnounce(PresentationKind.Consumption, variant, item, at, 0, actor, actor != 0);
        }

        internal static bool Play(PresentationEvent e)
        {
            var sound = Singleton<SOUND_Manager>.i;
            if (sound == null) return false;
            var cue = (Cue)e.Variant;
            AudioClip? clip;
            switch (cue)
            {
                case Cue.Food:
                case Cue.SyrupBottleFood:
                case Cue.SpoiledFood: clip = sound.BasicNeeds_Food; break;
                case Cue.Drink:
                case Cue.SyrupCan:
                case Cue.SyrupBottleDrink: clip = sound.BasicNeeds_Drink; break;
                case Cue.Wine: clip = sound.BasicNeeds_Wine; break;
                default: return false;
            }
            if (clip == null) return false;
            using (SpatialSounds.At(new Vector3(e.X, e.Y, e.Z), e.Actor))
            {
                if (cue == Cue.SyrupCan) sound.PlaySound_OpenCan();
                else if (cue == Cue.SyrupBottleFood || cue == Cue.SyrupBottleDrink) sound.PlaySound_UnscrewCap();
                else if (cue == Cue.SpoiledFood && sound.Player_Ewwww != null) sound.PlayOneShot2D(sound.Player_Ewwww, _randomPitch: true);
                sound.PlayOneShot2D(clip, _randomPitch: true);
            }
            return true;
        }
    }
}
