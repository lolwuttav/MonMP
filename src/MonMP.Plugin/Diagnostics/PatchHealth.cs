using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using GleyTrafficSystem;
using NWH;
using NWH.DWP2.ShipController;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Modules.Trailer;
using NWH.WheelController3D;
using UnityEngine;
using UnityEngine.Events;
using UniStorm;
using Leo;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Diagnostics
{
    internal static class PatchHealth
    {
        internal static IEnumerable<MethodInfo> Required()
        {
            yield return Method(typeof(PlayerVisualHands), nameof(PlayerVisualHands.ChangeShirt), typeof(int));
            yield return Method(typeof(Furniture_GardeRobe), nameof(Furniture_GardeRobe.MouseDown));
            foreach (var target in Patches.ItemDiscoveryLifecyclePatch.Targets()) yield return target;
            foreach (var target in Patches.ItemDiscoveryBoltRemovalPatch.Targets()) yield return target;
            foreach (var target in Patches.NativeSourcePatches.Targets()) yield return target;
            yield return Method(typeof(InteractableParts), nameof(InteractableParts.UpdatePartPlacement), typeof(bool));
            yield return Method(typeof(Hardware_Bolt), nameof(Hardware_Bolt.RemovedFromTray));
            yield return Method(typeof(Home_Bunker), "Button_FrenchFriesPress");
            yield return Method(typeof(Home_Bunker), "Button_BigPackMachine");
            yield return Method(typeof(Home_Bunker), "Button_HugePackMachine", typeof(bool));
            yield return Method(typeof(Home_Bunker), "ScanForThisPlant", typeof(int), typeof(SeedsType));
            yield return Method(typeof(Garden_DirtPatch), "OnDestroy");
            yield return Method(typeof(Wheelbarrow), "FreezeContent", typeof(bool));
            yield return Method(typeof(WheelController), "HitUpdate");
            yield return Method(typeof(Item_Poutine), "Perished");
            yield return Method(typeof(ItemForSale_BarDrink), "MouseHover");
            yield return Method(typeof(PlayerInteractable), "CheckIfHoldingItemToCancel", typeof(InteractableItems));
            yield return Method(typeof(UI_GenericWindow_MeetsManager), "ClaimReward", typeof(int));
            yield return Method(typeof(RaceManager), "AICompletedTheRace");
            yield return Method(typeof(RaceManager), "StopRace");
            Field(typeof(RaceManager), "CurrentRace_Timer", typeof(float));
            Field(typeof(UI_GenericWindow_MeetsManager), "listBuilt", typeof(bool));
            Field(typeof(UI_GenericWindow_MeetsManager), "Window_Rewards", typeof(GameObject));
            foreach (var target in NativeSaveProduction.Factories()) yield return target;
            foreach (var target in NativeSaveProduction.Steps()) yield return target;
            foreach (var target in Patches.SharedProductionOutputPatch.Targets()) yield return (MethodInfo)target;
            yield return Method(typeof(UniStormSystem), "Coroutine_ThisAmountOfTick", typeof(int), typeof(SpecialMenu), typeof(int));
            yield return Method(typeof(Vehicle_DriverSeat), "MouseDown");
            yield return Method(typeof(PlayerInteractable), "KeepHoldingItemAboveFloor");
            yield return Method(typeof(Tool_FishBucket), "MouseDown");
            yield return Method(typeof(Tool_FishBucket), "OnCollisionEnter", typeof(Collision));
            yield return Method(typeof(Tool_FishBucket), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(EntreposageGarage_Door), "DoorMoving", typeof(bool));
            yield return Method(typeof(Konig_Seat_Button), "MouseDown");
            Field(typeof(Konig_Seat_Button), "konig_Seat", typeof(Konig_Seat));
            Field(typeof(VehicleController), "activeLODIndex", typeof(int));
            yield return Method(typeof(BasicNeeds_Manager), "Fill_Everything");
            yield return Method(typeof(Gameplay), nameof(Gameplay.Tick), typeof(int));
            yield return Method(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.Start));
            yield return Method(typeof(Tree_Growing), nameof(Tree_Growing.Start));
            yield return Method(typeof(TreeTrunk), nameof(TreeTrunk.Start));
            yield return Method(typeof(TreeBase), "Awake");
            yield return Method(typeof(SugarShack_Tubing), "set_GetTarget", typeof(TubingConnectionPoint));
            foreach (var target in Patches.BackgroundInteractionCoroutines.Targets())
            {
                // Targets resolves each exact native factory signature (including the
                // bottle/can arguments) and validates its generated bool MoveNext().
                yield return target;
            }
            foreach (var target in Patches.SceneFurniturePatches.Targets().Concat(Patches.PoliceSpotPatches.Targets()))
                yield return Method(target.DeclaringType!, target.Name, target.GetParameters().Select(p => p.ParameterType).ToArray());
            Field(typeof(Furniture_GardeRobe), "playerVisualHands", typeof(PlayerVisualHands));
            Field(typeof(Game_Default_Menu), "Image_SavingGame", typeof(UnityEngine.UI.Image));
            Field(typeof(PlayerVisualHands), "isGameConsoleOpen", typeof(bool));
            Field(typeof(Furniture_GardeRobe), "Player_ShirtColor", typeof(Player_ShirtColor));
            Field(typeof(Furniture_Computer), "_State", typeof(Computer_State));
            foreach (string field in new[] { "Mat_Screen", "Mat_Light_Computer", "Mat_Light_Monitor" }) Field(typeof(Furniture_Computer), field, typeof(Material));
            Field(typeof(Furniture_Phone), "Apareille", typeof(GameObject));
            Field(typeof(BendOnCollision), "alreadyBend", typeof(bool));
            Field(typeof(BendOnCollision), "OriginalLocalEuler", typeof(Vector3));
            Field(typeof(BendOnCollision), "Solidity", typeof(float));
            Field(typeof(BendOnCollision), "XAxisOnly", typeof(bool));
            yield return Method(typeof(BendOnCollision), nameof(BendOnCollision.VehicleCollision), typeof(Collision));
            Field(typeof(Police_SpotDetails), "GO_Spot", typeof(GameObject));
            Field(typeof(Police_SpotDetails), "FlashingLights", typeof(Light));
            Field(typeof(Police_SpotDetails), "PoliceCar", typeof(Store_PoliceInterceptor_Car));
            Field(typeof(Police_SpotDetails), "Warning_Color", typeof(bool));
            Field(typeof(Police_SpotDetails), "GiveOnlyWarningOnly", typeof(bool));
            Field(typeof(Police_SpotDetails), "RealSpotToEnable", typeof(Police_SpotDetails));
            Field(typeof(Police_SpotDetails), "DistanceToChangeForRealSpot", typeof(float));
            Field(typeof(Police_SpotDetails), "WaitPlayerFarEnough", typeof(bool));
            Field(typeof(Kaligaz_FuelGun), "tubeController", typeof(TubeController));
            Field(typeof(Kaligaz_FuelGun), "IsDockFuelGun", typeof(bool));
            Field(typeof(TubeController), "TargetEnd", typeof(Transform));
            Field(typeof(Furniture_Radio), "vc", typeof(VehicleController));
            _ = Method(typeof(Furniture_Computer), "UpdateScreen", typeof(int));
            yield return Method(typeof(GameManager), "set_SETTING_Cheats", typeof(bool));
            yield return Method(typeof(EscapeMenu_GeneralV2), "Cheats_Activate");
            yield return Method(typeof(EscapeMenu_GeneralV2), "Update_CheatFriendshipWindow");
            foreach (var method in Patches.NativeCheatActionPatch.Targets()) yield return (MethodInfo)method;
            yield return Method(typeof(EscapeMenuV2), "ResumeGame", typeof(bool));
            yield return Method(typeof(Store_PoliceInterceptor), "NewTicketCreation", typeof(PoliceTicketData), typeof(Police_SpotDetails));
            yield return Method(typeof(Store_PoliceInterceptor), nameof(Store_PoliceInterceptor.PlayerDetected), typeof(Police_SpotDetails), typeof(bool));
            yield return Method(typeof(Police_SpotDetails), "FixedUpdated_WaitPlayerFarEnough");
            yield return Method(typeof(NWH.VehiclePhysics2.Effects.SurfaceParticleSystem), "Update");
            Field(typeof(NWH.VehiclePhysics2.Effects.SurfaceParticleSystem), "_vc", typeof(VehicleController));
            Field(typeof(NWH.VehiclePhysics2.Effects.SurfaceParticleSystem), "surfacePreset", typeof(NWH.VehiclePhysics2.GroundDetection.SurfacePreset));
            Field(typeof(NWH.VehiclePhysics2.Effects.SurfaceParticleManager), "particleSystems", typeof(List<NWH.VehiclePhysics2.Effects.SurfaceParticleSystem>));
#if MONMP_DEVELOPMENT
            // Native recovery probes are installed by the development assembly's
            // global Harmony owner, so verify them just like its gameplay hooks.
            yield return Method(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.Fill_Sleep), typeof(bool), typeof(bool), typeof(bool));
            yield return Method(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.SkipNight), typeof(int));
            foreach (var method in Patches.InteractionAuditPatch.Targets()) yield return method;
#endif
            foreach (var method in Patches.SharedActorRouteAuthorityPatch.Targets()) yield return (MethodInfo)method;
            foreach (var method in Patches.SharedSchoolBusAuthorityPatch.Targets()) yield return (MethodInfo)method;
            foreach (var method in Patches.SharedSchoolBusStatePatch.Targets()) yield return (MethodInfo)method;
            yield return Method(typeof(AI_SchoolBus), "CheckIfTeleportationNeeded", typeof(int));
            foreach (var method in Patches.SharedMartinCarAuthorityPatch.Targets()) yield return (MethodInfo)method;
            foreach (var method in Patches.SharedMartinCarCoroutinePatch.Targets()) yield return (MethodInfo)method;
            foreach (var method in Patches.SharedMartinCarStatePatch.Targets()) yield return (MethodInfo)method;
            foreach (var method in Patches.SharedMartinCarSmokePatch.Targets()) yield return (MethodInfo)method;
            foreach (var method in Patches.SharedMartinCarPhonePatch.Targets()) yield return (MethodInfo)method;
            foreach (var method in Patches.SharedWoodSplitterControlPatch.Targets()) yield return (MethodInfo)method;
            foreach (var method in Patches.GuestNpcMoodAndFriendshipGuardPatch.Targets()) yield return (MethodInfo)method;
            yield return Method(typeof(Actor), "MouseDown");
            yield return Method(typeof(Actor_Roger), nameof(Actor_Roger.Greetings), typeof(bool));
            foreach (var method in Patches.SharedNpcGuestDialogGreetingPatch.Targets().Concat(Patches.SharedNpcGuestDialogChoicePatch.Targets()))
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            Field(typeof(UI_GenericWindow_Dialogs), "CurrentActor", typeof(Actor));
            Field(typeof(UI_GenericWindow_Dialogs), "ActorGreetings", typeof(TMPro.TextMeshProUGUI));
            _ = Method(typeof(UI_GenericWindow_Dialogs), "UpdateButtons", typeof(string));
            yield return Method(typeof(Actor), "TalkOverHead", typeof(string));
            yield return Method(typeof(Actor), "OnTriggerEnter", typeof(Collider));
            _ = Method(typeof(Actor_Police), "canGiveMapleSyrupCan", typeof(Item_MapleSyrupCan));
            foreach (var method in Patches.SharedActiveToolMousePatch.Targets()) yield return method;
            foreach (var method in Patches.PendingItemPickupInputPatch.Targets()) yield return method;
            yield return Method(typeof(EscapeMenu_GeneralV2), "LoadCD");
            yield return Method(typeof(EscapeMenu_GeneralV2), "UpdatePage");
            yield return Method(typeof(SOUND_Manager), "BuildPlayerMusicList");
            foreach (var method in Patches.SalePresentationPatch.TargetMethods()) yield return (MethodInfo)method;
            yield return Method(typeof(Gameplay), "PauseGame");
            yield return Method(typeof(MainMenu_Manager), "CloseOpenWindows");
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
            // The isolated CVO world-load gate is installed by the global Harmony owner.
            yield return Method(typeof(MainMenu_Manager), nameof(MainMenu_Manager.StartGame), typeof(bool));
#endif
            yield return Method(typeof(SOUND_General), "PlayerVoice", typeof(AudioClip));
            yield return Method(typeof(SOUND_Manager), "PlaySound_Player_Spit");
            yield return Method(typeof(SOUND_Base), "PlayOneShot", typeof(AudioSource), typeof(AudioClip), typeof(float), typeof(float));
            yield return Method(typeof(SOUND_Base), "PlayOneShot", typeof(AudioSource));
            yield return Method(typeof(SOUND_Base), "PlayAudioSource", typeof(AudioSource), typeof(bool));
            yield return Method(typeof(SOUND_Base), "PauseAudioSource", typeof(AudioSource));
            yield return Method(typeof(SOUND_Base), "StopAudioSource", typeof(AudioSource));
            yield return Method(typeof(SOUND_Manager), "PlayOneShot2D", typeof(AudioClip), typeof(bool), typeof(float), typeof(float));
            yield return Method(typeof(DrunkEffect), "Vomit");
            yield return Method(typeof(PlayerVisualHands), "Interaction");
            yield return Method(typeof(PlayerVisualHands), "InteractionPetLeo");
            yield return Method(typeof(PlayerVisualHands), "SorryDown");
            yield return Method(typeof(PlayerVisualHands), "SorryUp");
            yield return Method(typeof(BasicNeeds_Manager), "Tick", typeof(bool), typeof(bool));
            yield return Method(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.Pet_Leo));
            yield return Method(typeof(Interactable), "CanUse");
            yield return Method(typeof(Gameplay), "EnterExitChair", typeof(Transform));
            yield return Method(typeof(Gameplay), "ResumeGame");
            yield return Method(typeof(SaveSystem), "Save", typeof(House_Location));
            yield return Method(typeof(SaveSystem), "Load");
            yield return Method(typeof(SaveSystem), "LoadAfterEverythingMoved");
            yield return Method(typeof(SaveSystem), "GetPlayerFromSave");
            yield return Method(typeof(SaveSystem), "LoadPlayer", typeof(PlayerData));
            yield return Method(typeof(EscapeMenuV2), "Button_QuitGameConfirmation");
            yield return Method(typeof(SaveSystem), "HardMode_Permadeath");
            yield return Method(typeof(Gameplay), nameof(Gameplay.HardMode_LoadSave));
            yield return Method(typeof(SaveSystem), "DeleteSaveName", typeof(string));
            yield return Method(typeof(GlobalClassManager), "get_HasASaveForThisGame");
            yield return Method(typeof(GlobalClassManager), "CheckIfItemNeedToBeDeleted", typeof(GameObject), typeof(float));
            yield return Method(typeof(GlobalClassManager), "Awake");
            yield return Method(typeof(UI_BlackScreenWaitForSaveSystem), "FadeBlackScreen");
            yield return Method(typeof(Furniture_SwitchButton), "MouseDown");
            yield return Method(typeof(Furniture_Light), "TurnOn"); yield return Method(typeof(Furniture_Light), "TurnOff");
            yield return Method(typeof(PlayerInteractable), "GrabInteractable");
            yield return Method(typeof(PlayerInteractable), "UnGrabInteractable", typeof(bool));
            yield return Method(typeof(PlayerInteractable), "UseKeyDown");
            yield return Method(typeof(Item_Poutine), "Tick", typeof(int));
            yield return Method(typeof(Item_Poutine), "set_Get_IsRefrigerated", typeof(bool));
            yield return Method(typeof(UniStormSystem), "Update");
            foreach (string name in new[] { "AddOneSecond", "CalculateTimeSlider", "CalculateDays", "HourlyUpdate" })
                yield return Method(typeof(UniStormSystem), name);
            yield return Method(typeof(UniStormSystem), "AddOneHour", typeof(SpecialMenu));
            yield return Method(typeof(UniStormSystem), "AddOneDay", typeof(SpecialMenu));
            yield return Method(typeof(TickSystem), "Tick", typeof(int)); yield return Method(typeof(TickSystem), "DayChanged");
            yield return Method(typeof(Gameplay), "Sleep_Start", typeof(Furniture_Bed), typeof(bool));
            yield return Method(typeof(Gameplay), "Sleeping", typeof(Furniture_Bed), typeof(bool));
            yield return Method(typeof(Gameplay), "Death", typeof(string), typeof(bool));
            yield return Method(typeof(Gameplay), "PermaDeath", typeof(string));
            Field(typeof(Gameplay), "isDead", typeof(bool));
            yield return Method(typeof(BasicNeeds_Manager), "Fill_Death");
            yield return Method(typeof(BasicNeeds_Manager), "Fill_PassOut");
            foreach (var target in Patches.SharedWeatherChangePatch.Targets())
                yield return Method(target.DeclaringType!, target.Name, target.GetParameters().Select(p => p.ParameterType).ToArray());
            foreach (var target in Patches.SharedEconomyMutationPatch.Targets())
                yield return Method(target.DeclaringType!, target.Name, target.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Cash_Manager), "TryToBuy", typeof(ItemForSale_Display), typeof(int), typeof(Actor), typeof(bool));
            yield return Method(typeof(ItemForSale_Display_DogProduct), "MouseDown");
            yield return Method(typeof(ItemForSale_Display_ToDoListToUnlock), "MouseDown");
            yield return Method(typeof(Leo_Manager), "Tick", typeof(int));
            yield return Method(typeof(Leo_Manager), "MouseDown");
            yield return Method(typeof(TamedSM), nameof(TamedSM.Pet));
            yield return Method(typeof(Leo_Manager), "Use");
            yield return Method(typeof(Leo_Manager), "MouseHover");
            yield return Method(typeof(Leo_Manager), nameof(Leo_Manager.Update_NameToAddAfter));
            yield return Method(typeof(EscapeMenu_Friend_Slot), nameof(EscapeMenu_Friend_Slot.UpdateSlot));
            Field(typeof(EscapeMenu_Friend_Slot), "actor_Type", typeof(Actor_Type));
            Field(typeof(EscapeMenu_Friend_Slot), "TIP_PettedToday", typeof(GameObject));
            Field(typeof(EscapeMenu_Friend_Slot), "TIP_NotPettedToday", typeof(GameObject));
            yield return Method(typeof(Leo_Manager), "TreatGiven", typeof(Tool_DogTreats));
            yield return Method(typeof(Leo_Manager), "CarBed", typeof(CarBed));
            yield return Method(typeof(Leo_Manager), "PlayerIsSleeping", typeof(Furniture_Bed));
            yield return Method(typeof(Leo_Manager), "LeoTamed");
            yield return Method(typeof(Leo.RoamingPlayer), "GoToRandomDestination");
            yield return Method(typeof(Leo.HidingSpotState), "Tick", typeof(int));
            yield return Method(typeof(Leo.SeakingToPlayerState), "Tick", typeof(int));
            yield return Method(typeof(Leo.SeakingToPlayerState), "UpdateLogic");
            yield return Method(typeof(Tool_DogTreats), "MouseDown");
            yield return Method(typeof(Tool_DryFoodPack), "MouseDown");
            yield return Method(typeof(Tool_FertilizerPack), "MouseDown");
            yield return Method(typeof(Tool_PesticideSolution), "MouseDown");
            yield return Method(typeof(Home_Bunker), "AddFertilizer", typeof(Tool_FertilizerPack));
            yield return Method(typeof(Home_Bunker), "WaterPlants");
            yield return Method(typeof(Home_Bunker), "Manually_WaterPlants");
            yield return Method(typeof(Tool_WateringCan), "MouseUp");
            foreach (var method in Patches.SharedLeoWateringCanPatch.Targets()) yield return method;
            yield return Method(typeof(StateMachine), "Update");
            yield return Method(typeof(StateMachine), "LateUpdate");
            yield return Method(typeof(Cash_Manager), "isCanAfford", typeof(float));
            yield return Method(typeof(SlotMachine), "ButtonPressed", typeof(SlotMachine_Button.ButtonType));
            yield return Method(typeof(SlotMachine), nameof(SlotMachine.ResetMachine));
            yield return Method(typeof(CMF.AdvancedWalkerController), nameof(CMF.AdvancedWalkerController.ExitChair));
            yield return Method(typeof(ToDoList_Manager), nameof(ToDoList_Manager.PavedRoadsBought), typeof(ToDoList_Line));
            yield return Method(typeof(PavedRoads), nameof(PavedRoads.PavedRoadBought), typeof(ToDoList_Line));
            yield return Method(typeof(PlayerInteractable), "get_IsHoldingAnObject");
            yield return Method(typeof(ItemForSale_Display), "MouseHover");
            yield return Method(typeof(ToDoList_Manager), "ToDoListCompleted", typeof(ToDoList_Line));
            yield return Method(typeof(ToDoList_Manager), "CheckLineFromSaveSystem", typeof(string));
            yield return Method(typeof(ToDoList_LineDetails), "CheckLine");
            yield return Method(typeof(Gameplay), "EnterExitVehicle", typeof(Vehicle), typeof(Transform), typeof(bool));
            foreach (var type in new[] { typeof(Part_Seat_Forward), typeof(Part_Seat_Height) })
            { yield return Method(type, "MouseDown"); yield return Method(type, "MouseRightDown"); }
            yield return Method(typeof(Gameplay), "ExitVehicle");
            yield return Method(typeof(WheelController), "OnPrePhysicsSubstep", typeof(float), typeof(float));
            yield return Method(typeof(WheelController), "OnPhysicsSubstep", typeof(float), typeof(float), typeof(int));
            yield return Method(typeof(WheelController), "OnPostPhysicsSubstep", typeof(float), typeof(float));
            yield return Method(typeof(Vehicle_Door), "ToggleDoor"); yield return Method(typeof(Vehicle_Door), "FixedUpdate");
            yield return Method(typeof(Vehicle_Hood), "Release"); yield return Method(typeof(Vehicle_Hood), "Open");
            yield return Method(typeof(Vehicle_Hood), "Close"); yield return Method(typeof(Vehicle_Hood), "FixedUpdate");
            yield return Method(typeof(Vehicle_Trunk), "Release"); yield return Method(typeof(Vehicle_Trunk), "FixedUpdate");
            yield return Method(typeof(Tool_AdjustableSpanner), "GrabBolt", typeof(Hardware_Bolt)); yield return Method(typeof(Tool_Air_Impact), "GrabBolt", typeof(Hardware_Bolt));
            foreach (var method in Patches.SharedPendingToolBoltInstallPatch.TargetMethods())
                yield return Method(method.DeclaringType!, method.Name);
            yield return Method(typeof(InteractablePacks), "Use"); yield return Method(typeof(InteractablePacks), "GetOnePackedItem");
            yield return Method(typeof(InteractableItems), "Awake");
            yield return Method(typeof(Item_WeedPreRollBigPack), "Use"); yield return Method(typeof(Item_WeedPreRollHugePack), "Use");
            yield return Method(typeof(Part_FuelTankCap), "UpdateVisual");
            yield return Method(typeof(Tool_MagneticTray), "AddBolt", typeof(Hardware_Bolt));
            yield return Method(typeof(Tool_MagneticTray), "MouseRightDown");
            yield return Method(typeof(Vehicle_Chainsaw_Holder), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Item_Fish), nameof(Item_Fish.DropFromFishingRod));
            yield return Method(typeof(Tool_FishBucket), "Add_Fish", typeof(Item_Fish), typeof(bool));
            yield return Method(typeof(Tool_FishBucket), "MouseRightDown");
            yield return Method(typeof(Store_FarmKiosque), "Receive", typeof(Item_Fish));
            yield return Method(typeof(Store_FarmKiosque), "Receive", typeof(Tool_FishBucket));
            yield return Method(typeof(Vehicle_Button_WindowSwitch), "MouseDown");
            yield return Method(typeof(Vehicle_Button_LightDomeOptions), "ChangeLightMode");
            yield return Method(typeof(Vehicle_Button_WindowSwitch), "MovingWindow", typeof(float));
            foreach (var type in new[] { typeof(Vehicle_IgnitionSwitch), typeof(Vehicle_HighLowShifter), typeof(Vehicle_Button_ParkingBrake), typeof(Vehicle_Button_Shifter), typeof(Vehicle_Button_LightSwitch), typeof(Vehicle_Button_Highbeam), typeof(Vehicle_Button_HazardLights), typeof(Vehicle_Button_ExtraLights) })
                yield return Method(type, "MouseDown");
            yield return Method(typeof(Vehicle_Button_LightSwitch), "MouseRightDown");
            yield return Method(typeof(Vehicle_Plate), "MouseRightDown");
            yield return Method(typeof(Vehicle_Button_Shifter), "MouseRightDown");
            yield return Method(typeof(Vehicle_Button_ParkingBrake), "MouseRightDown"); yield return Method(typeof(Vehicle_Button_ParkingBrake), "Use");
            yield return Method(typeof(Vehicle), "set_Get_IgnitionPosition", typeof(int));
            yield return Method(typeof(Vehicle), nameof(Vehicle.FixedUpdate));
            yield return Method(typeof(AdvancedShipController), "SetInputToPlayer", typeof(bool));
            yield return Method(typeof(AdvancedShipController), "Update");
            yield return Method(typeof(Engine), "Update");
            yield return Method(typeof(Part_FuelTank), "set_Get_FuelLevel", typeof(float));
            yield return Method(typeof(Part_Autometer_RPM), "Knob_ChangeShiftLightRPM", typeof(int));
            yield return Method(typeof(Part_Autometer_Speed), "Change_UnitOfSpeed");
            yield return Method(typeof(Part_Engine_ExhaustManifold_Turbo), "Knob_ChangeWestgateSound", typeof(int));
            yield return Method(typeof(Part_Suspension_Hub_Stake), "Grab");
            yield return Method(typeof(Part_Spoiler), "Grab");
            yield return Method(typeof(Boat_Anchor), "MouseDown"); yield return Method(typeof(Boat_Anchor), "MouseRightDown");
            yield return Method(typeof(Boat_Anchor_FishingBoat), "MouseDown");
            yield return Method(typeof(Anchor), "Drop"); yield return Method(typeof(Anchor), "Weigh");
            yield return Method(typeof(Vehicle_IgnitionSwitch_FishingBoat), "MouseDown"); yield return Method(typeof(Vehicle_IgnitionSwitch), "MouseUp");
            yield return Method(typeof(Boat_SmallCruiser_FuelCap), "MouseDown"); yield return Method(typeof(Boat_SmallCruiser_FuelCap), "MouseRightDown");
            yield return Method(typeof(Boat_SmallCruiser_FuelCap), "onLoadSaveSystem");
            yield return Method(typeof(NWH.VehiclePhysics2.Sound.SoundManager), "Update");
            yield return Method(typeof(NWH.VehiclePhysics2.Sound.SoundComponents.CrashComponent), "PlayCollisionSound");
            Field(typeof(NWH.VehiclePhysics2.Sound.SoundComponents.CrashComponent), "collisionData", typeof(Collision));
            yield return Method(typeof(NWH.VehiclePhysics2.Effects.DamageHandler), "HandleCollision", typeof(Collision));
            yield return Method(typeof(Gameplay), "VehicleCollision", typeof(Collision));
            yield return Method(typeof(NWH.VehiclePhysics.NWH2_RES), "Update");
            yield return Method(typeof(TrailerHitchModule), "AttachTrailer", typeof(VehicleController));
            yield return Method(typeof(TrailerHitchModule), "DetachTrailer", typeof(VehicleController));
            yield return Method(typeof(Trailer_Coupler), "MouseDown");
            yield return Method(typeof(Vehicle_PaintablePanel), "Paint", typeof(Vector3), typeof(float), typeof(float), typeof(bool));
            yield return Method(typeof(Tool_SprayPaintCan), "MouseDown");
            yield return Method(typeof(Tool_SprayPaintCan), "MouseUp");
            var sprayPreview = AccessTools.EnumeratorMoveNext(Method(typeof(Tool_SprayPaintCan), "CheckActivePannel")) ??
                throw new InvalidOperationException("Unsupported native spray preview coroutine.");
            yield return Method(sprayPreview.DeclaringType!, "MoveNext");
            yield return Method(typeof(InteractableTools), "get_isCanInteractWhileHolding");
            yield return Method(typeof(InteractableItems), "FreezeInContainer", typeof(Transform));
            yield return Method(typeof(Tool_DentRepairKit), "MouseDown");
            yield return Method(typeof(Vehicle_PaintablePanel), "RepairRust");
            yield return Method(typeof(Tool_RustRepairKit), "MouseDown");
            Field(typeof(Tool_RustSolution), "LastTimeUsed", typeof(float));
            Field(typeof(Tool_RustSolution), "PuishParticle", typeof(ParticleSystem));
            yield return Method(typeof(Tool_RustSolution), "MouseDown");
            yield return Method(typeof(Vehicle_PaintablePanel), "RepairDent", typeof(bool));
            yield return Method(typeof(Vehicle_PaintablePanel), "ForceRust");
            yield return Method(typeof(Tool_WindowTint), "MouseDown");
            yield return Method(typeof(Vehicle_WindowTint), "Start");
            yield return Method(typeof(Tool_WindowTintRemover), "MouseDown");
            yield return Method(typeof(Vehicle_WindowTint), "ApplyTint", typeof(Color), typeof(bool));
            yield return Method(typeof(Vehicle_WindowTint), "RemoveTint");
            yield return Method(typeof(Vehicle_WindowTint), "LoadSaveSystem", typeof(WindowTintData));
            foreach (var method in Patches.BuildingDoorHandlePatch.Targets().Concat(Patches.BuildingDoorMutationPatch.Targets()))
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Furniture_DoorHandle_VIPRoom), nameof(Furniture_DoorHandle_VIPRoom.MouseHover));
            yield return Method(typeof(ToDoList_Manager), "Checked");
            _ = Method(typeof(Steam_AchievementManager), nameof(Steam_AchievementManager.AchievementDone_ToDoList), typeof(ToDoList_Line));
            Field(typeof(Furniture_DoorHandle_VIPRoom), "UNLOCKPRICE", typeof(float));
            foreach (var method in Patches.ScrapyardGateMutationPatch.Targets())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Scrapyard), "NewDay", typeof(int));
            yield return Method(typeof(Scrapyard), "SpawnRadios");
            yield return Method(typeof(Scrapyard), "LoadAndUnlock", typeof(bool[]));
            foreach (var method in Patches.BunkerBathtubButtonPatch.Targets())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Bunker_Bathtub), nameof(Bunker_Bathtub.DetectorTriggered));
            yield return Method(typeof(Scrapyard_Radio), "MouseRightDown");
            yield return Method(typeof(Furniture_Radio), "Button_Power", typeof(bool), typeof(bool));
            yield return Method(typeof(Furniture_Radio), "Button_Volume", typeof(float));
            yield return Method(typeof(Furniture_Radio), "Button_Presets", typeof(int));
            yield return Method(typeof(Furniture_Radio), "Button_FMCD");
            yield return Method(typeof(Furniture_Radio), "Button_SeekNext");
            yield return Method(typeof(Furniture_Radio), "Button_SeekPrev");
            yield return Method(typeof(Furniture_Radio), nameof(Furniture_Radio.UpdateScreen));
            Field(typeof(Furniture_Radio), "ScreenText", typeof(TMPro.TextMeshPro));
            Field(typeof(Furniture_Radio), "TimerScreenGoBackNormal", typeof(float));
            yield return Method(typeof(SOUND_Manager), "Tick", typeof(int));
            yield return Method(typeof(SOUND_Manager), "PlayStationOnThisRadio", typeof(SOUND_Radio));
            yield return Method(typeof(Store_Bar), "UpdateRadio");
            Field(typeof(Store_Bar), "MusicPlaying", typeof(AudioSource));
            yield return Method(typeof(SOUND_Radio), "ButtonPressed");
            yield return Method(typeof(SOUND_Manager), "LoadMusic", typeof(string), typeof(AudioType));
            foreach (var method in Patches.DealerMachinePurchasePatch.Targets())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(ItemForSale_Dealer), nameof(ItemForSale_Dealer.MouseDown));
            yield return Method(typeof(ItemForSale_Dealer), nameof(ItemForSale_Dealer.MouseHover));
            Field(typeof(ItemForSale_Dealer), "AllPlanters", typeof(GameObject));
            Field(typeof(ItemForSale_Dealer), "isOutdoorPlanter", typeof(bool));
            Field(typeof(Vehicle_Trailer_Dolly), "Switch_Ramp", typeof(Furniture_SwitchButton)); Field(typeof(Vehicle_Trailer_Dolly), "Switch_AttachVehicle", typeof(Furniture_SwitchButton));
            Field(typeof(Vehicle_Trailer_Dolly), "RampOpenLocation", typeof(Transform)); Field(typeof(Vehicle_Trailer_Dolly), "Ramp_OriginalPosition", typeof(Vector3));
            Field(typeof(Vehicle_Trailer_Dolly), "Ramp_OriginalRotation", typeof(Quaternion)); Field(typeof(Vehicle_Trailer_Dolly), "Ramp_Timer", typeof(float));
            Field(typeof(Vehicle_Trailer_Dolly), "Ramp_Total", typeof(float)); Field(typeof(Vehicle_Trailer_Dolly), "Coroutine_Ramp", typeof(bool));
            Field(typeof(Vehicle_Trailer_Dolly), "WheelAttach_Left", typeof(Transform)); Field(typeof(Vehicle_Trailer_Dolly), "WheelAttach_Right", typeof(Transform));
            Field(typeof(Vehicle_Trailer_Dolly), "WheelsDetection", typeof(BoxCollider));
            yield return Method(typeof(EntreposageGarage_PancarteForSale), nameof(EntreposageGarage_PancarteForSale.MouseDown));
            yield return Method(typeof(EntreposageGarage), nameof(EntreposageGarage.BuyThisGarage));
            yield return Method(typeof(EntreposageGarage), nameof(EntreposageGarage.ToggleDoor), typeof(int));
            yield return Method(typeof(EntreposageGarage), nameof(EntreposageGarage.UnloadThisCar), typeof(int), typeof(NWH.Vehicle));
            yield return Method(typeof(EntreposageGarage), nameof(EntreposageGarage.LoadThisCar), typeof(int), typeof(NWH.Vehicle));
            yield return Method(typeof(EntreposageGarage_Door), "Door_IsOKToClose");
            yield return AccessTools.PropertyGetter(typeof(EntreposageGarage_Door), nameof(EntreposageGarage_Door.isPlayerInBay)) ?? throw new InvalidOperationException("Critical storage garage bay getter missing.");
            Field(typeof(EntreposageGarage_Door), "EntreposageGarage", typeof(EntreposageGarage));
            Field(typeof(EntreposageGarage_Door), "DoorID", typeof(int));
            Field(typeof(EntreposageGarage_Door), "Animator_Door", typeof(Animator));
            Field(typeof(EntreposageGarage_Door), "BayDetector_Collider", typeof(BoxCollider));
            yield return Method(typeof(Pack_MapleSyrupCrate), "AddToPack", typeof(Collider));
            yield return Method(typeof(UI_GenericWindow_MeetsManager), "Bet_Done", typeof(int));
            yield return Method(typeof(RaceManager), nameof(RaceManager.PlayerCompletedTheRace));
            yield return Method(typeof(Store_MeetManager), nameof(Store_MeetManager.CloseStore));
            yield return Method(typeof(PlayerInteractable), "CheckForInteractable");
            yield return Method(typeof(PlayerInteractable), "ResetCurrentInteractable", typeof(Interactable));
            yield return Method(typeof(NitrousRefillStation), "BottleEntered", typeof(Part_NitrousBottle));
            yield return Method(typeof(SOUND_NitrousRefillStation), nameof(SOUND_NitrousRefillStation.StartFilling));
            yield return Method(typeof(SOUND_NitrousRefillStation), nameof(SOUND_NitrousRefillStation.StopFilling));
            yield return Method(typeof(Vehicle_Nitrous_Controls), "Button_Arm_On");
            yield return Method(typeof(Vehicle_Nitrous_Controls), "Button_Arm_Off");
            yield return Method(typeof(Vehicle_Nitrous_Controls), "Power_Button_Toggle");
            yield return Method(typeof(Vehicle_Underglow), "Button_OnOff");
            yield return Method(typeof(Vehicle_Underglow), "Update");
            yield return Method(typeof(Vehicle_Button_Shifter), nameof(Vehicle_Button_Shifter.CanUse));
            yield return Method(typeof(SOUND_Vehicle_Switch), nameof(SOUND_Vehicle_Switch.On));
            yield return Method(typeof(SOUND_Vehicle_Switch), nameof(SOUND_Vehicle_Switch.Off));
            yield return Method(typeof(SOUND_Vehicle_ParkingBrake), nameof(SOUND_Vehicle_ParkingBrake.On));
            yield return Method(typeof(SOUND_Vehicle_ParkingBrake), nameof(SOUND_Vehicle_ParkingBrake.Off));
            yield return Method(typeof(Vehicle_Button_ExtraLights), "UpdateVisualSound");
            yield return Method(typeof(Vehicle_Button_LightDomeOptions), nameof(Vehicle_Button_LightDomeOptions.ChangeLightMode));
            yield return Method(typeof(SOUND_Vehicle_Door), nameof(SOUND_Vehicle_Door.Open));
            yield return Method(typeof(SOUND_Vehicle_Door), nameof(SOUND_Vehicle_Door.Close));
            yield return Method(typeof(Vehicle_Underglow), "Button_Mode");
            yield return Method(typeof(Vehicle_Underglow), "Button_CLR");
            yield return Method(typeof(Vehicle_Underglow), "Button_Cycle");
            yield return Method(typeof(Vehicle_Underglow), "Button_Force");
            yield return Method(typeof(Store_PizzaSnack_FriesReception), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Store_Dealer_WeedReception), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Store_Bar_MapleWineReception), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Store_FederationSyrup_Reception), "OnTriggerEnter", typeof(Collider));
            Field(typeof(Store_FederationSyrup), "Reception_Barrel", typeof(GameObject)); Field(typeof(Store_FederationSyrup), "Reception_GrayedBarrelInfo", typeof(GameObject)); Field(typeof(Store_FederationSyrup), "Reception_BedTank", typeof(GameObject));
            yield return Method(typeof(Store_IslandHouses_Reception), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Store_FarmKiosque_ReceptionFish), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Furniture_Phone), "RingingCompleted");
            Field(typeof(Furniture_Phone), "NumberComposed", typeof(string));
            Field(typeof(Store_KaliGas), "PriceSuperFuel", typeof(TMPro.TextMeshPro));
            Field(typeof(Kaligaz_FuelGun), "FuelAdded", typeof(float));
            _ = Method(typeof(Kaligaz_FuelGun), "ChangePriceText", typeof(float));
            yield return Method(typeof(Furniture_Phone), nameof(Furniture_Phone.ButtonPressed), typeof(string));
            yield return Method(typeof(Store_PostOffice), nameof(Store_PostOffice.ShipThisLetterToPlayer), typeof(Envelope_Type), typeof(bool));
            yield return Method(typeof(SugarShack_PumpSyrupOutside), nameof(SugarShack_PumpSyrupOutside.AddSyrup), typeof(int));
            yield return Method(typeof(Store_Bar), nameof(Store_Bar.Receive), typeof(Pack_MapleSyrupCrate));
            yield return Method(typeof(Store_FederationSyrup), nameof(Store_FederationSyrup.Receive), typeof(Trailer_MapleSyrupTank));
            foreach (var method in Patches.NewspaperCardboxPatch.Targets())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Tool_DogTreats), nameof(Tool_DogTreats.MouseDown));
            yield return Method(typeof(ItemForSale_Kegs), nameof(ItemForSale_Kegs.MouseDown));
            yield return Method(typeof(ItemForSale_Display_CarLift), nameof(ItemForSale_Display_CarLift.MouseDown));
            yield return Method(typeof(Item_CoffeePercolator), nameof(Item_CoffeePercolator.MouseDown));
            yield return Method(typeof(Item_CoffeePercolator), nameof(Item_CoffeePercolator.Use));
            yield return Method(typeof(Item_CoffeePercolator), nameof(Item_CoffeePercolator.Start));
            yield return Method(typeof(Item_CoffeePercolator), "BrewingCoffee");
            Field(typeof(Item_CoffeePercolator), "ElectricCoffeePercolator", typeof(Item_ElectricCoffeePercolator));
            Field(typeof(Item_CoffeePercolator), "Coroutine_BrewingCoffee", typeof(bool));
            Field(typeof(Item_CoffeePercolator), "VFX_Smoke", typeof(ParticleSystem));
            Field(typeof(Item_CoffeePercolator), "Percolator_Cap", typeof(Transform));
            Field(typeof(Item_CoffeePercolator), "Visual_CoffeeBeans", typeof(Transform));
            foreach (var method in Patches.SharedMapTorchCoroutinePatch.Targets())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Tool_MapTorch), nameof(Tool_MapTorch.MouseDown));
            foreach (var method in Patches.TireChangerAuthorityPatches.Targets())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            foreach (var method in Patches.PortableConsoleChargerPatchTargets.RequiredTargets())
                yield return (MethodInfo)method;
            yield return Method(typeof(PostOffice_ReceptionBox), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Tool_Fence), nameof(Tool_Fence.MouseDown));
            yield return Method(typeof(Tool_Fence), nameof(Tool_Fence.MouseRightDown));
            yield return Method(typeof(Tool_Fence), nameof(Tool_Fence.MouseRightUp));
            yield return Method(typeof(Tool_Fence), nameof(Tool_Fence.Use));
            yield return Method(typeof(UI_Crosshair), nameof(UI_Crosshair.RemoveAPart), typeof(float));
            foreach (var method in Patches.SharedFenceRemovalCoroutinePatch.Targets())
                yield return method;
            foreach (var method in Patches.FirewoodAuthorityPatches.Targets())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            foreach (var method in Patches.SharedBoltToolInputPatch.Targets())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(BoltHoleDetails), nameof(BoltHoleDetails.Attach), typeof(Hardware_Bolt), typeof(float));
            yield return Method(typeof(BoltHoleDetails), nameof(BoltHoleDetails.Unscrewed));
            foreach (var method in Patches.SharedBoltToolCoroutinePatch.Targets())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Tool_AdjustableSpanner), "CheckForPickingABolt", typeof(bool));
            yield return Method(typeof(Tool_Air_Impact), "CheckForPickingABolt", typeof(bool));
            var airImpactSpanner = Method(typeof(Tool_Air_Impact), "Spanner");
            var airImpactMoveNext = AccessTools.EnumeratorMoveNext(airImpactSpanner);
            if (airImpactMoveNext == null) throw new InvalidOperationException("Unsupported native air-impact coroutine: Tool_Air_Impact.Spanner");
            yield return airImpactMoveNext;
            yield return Method(typeof(Vehicle_PartSocket), nameof(Vehicle_PartSocket.Detach));
            foreach (var method in Patches.SharedCoiloverValuesPatch.TargetMethods())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Part_ControlArm_Tuned), nameof(Part_ControlArm_Tuned.Knob_Camber), typeof(int));
            foreach (var method in Patches.SharedRearSeatValuesPatch.TargetMethods())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Tool_HoistPuller_Handle), nameof(Tool_HoistPuller_Handle.MouseDown));
            yield return Method(typeof(Tool_HoistPuller_Handle), nameof(Tool_HoistPuller_Handle.MouseRightDown));
            yield return Method(typeof(Vehicle_HoistPullerBox), nameof(Vehicle_HoistPullerBox.MouseDown));
            yield return Method(typeof(Tool_HoistPuller), nameof(Tool_HoistPuller.MouseRightDown));
            yield return Method(typeof(Tool_HoistPuller), nameof(Tool_HoistPuller.Crink));
            yield return Method(typeof(Tool_HoistPuller), "FixedUpdate");
            yield return Method(typeof(PlayerInteractable), nameof(PlayerInteractable.Grab_HoistPuller), typeof(Tool_HoistPuller));
            yield return Method(typeof(PlayerInteractable), "CheckForHoist");
            yield return Method(typeof(PlayerInteractable), "UseKeyDown");
            yield return Method(typeof(PlayerInteractable), "GrabInteractable");
            yield return Method(typeof(UI_GenericWindow_Gilles), "Button_FindThisObject");
            yield return Method(typeof(UI_GenericWindowManager), nameof(UI_GenericWindowManager.OpenGilles));
            yield return Method(typeof(Store_Gilles), "Tick", typeof(int));
            yield return Method(typeof(Tool_Seeds), nameof(Tool_Seeds.MouseDown));
            yield return Method(typeof(Tool_HarvestBasket), nameof(Tool_HarvestBasket.MouseDown));
            yield return Method(typeof(Tool_HarvestBasket), "SpawnAddSeeds", typeof(Vector3));
            yield return Method(typeof(Store_FederationSyrup), nameof(Store_FederationSyrup.Receive), typeof(Item_Barrel120));
            yield return Method(typeof(Store_IslandHouses), nameof(Store_IslandHouses.Receive), typeof(Item_Barrel120));
            yield return Method(typeof(ItemForSale_BarDrink), nameof(ItemForSale_BarDrink.MouseDown));
            yield return Method(typeof(Store_Bar), nameof(Store_Bar.OpenStore));
            yield return Method(typeof(Store_Bar), nameof(Store_Bar.CloseStore));
            yield return Method(typeof(Actor_Carole), nameof(Actor_Carole.PlayerNewWineBottle), typeof(bool));
            yield return Method(typeof(Store_Dealer_BunkerSellSign), nameof(Store_Dealer_BunkerSellSign.MouseDown));
            yield return Method(typeof(Store_Dealer), nameof(Store_Dealer.TryToBuyBunker));
            yield return Method(typeof(Home), nameof(Home.BunkerBought));
            yield return Method(typeof(Home), "NewDay", typeof(int));
            yield return Method(typeof(Home), nameof(Home.UpdateBunkerConstruction));
            yield return Method(typeof(Workbench_PaintMixing), nameof(Workbench_PaintMixing.TryToBuy), typeof(int));
            yield return Method(typeof(Workbench_PaintMixing), "KaliGas_Open");
            yield return Method(typeof(RacecarBuild), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Speedway_RacecarToDo), nameof(Speedway_RacecarToDo.MouseDown));
            foreach (var method in Patches.SharedWoodenKegPatchTargets.RequiredTargets())
                yield return (MethodInfo)method;
            foreach (var method in SharedTowingService.RequiredTargets())
                yield return (MethodInfo)method;
            yield return Method(typeof(PlayerInteractable), "GrabInteractable");
            yield return Method(typeof(Event_PizzaSnack), nameof(Event_PizzaSnack.StartEvent));
            yield return Method(typeof(Event_PizzaSnack), nameof(Event_PizzaSnack.PlayerStepIn));
            yield return Method(typeof(Event_PizzaSnack), nameof(Event_PizzaSnack.PizzaDelivered));
            yield return Method(typeof(Event_PizzaSnack), "OpenStore");
            yield return Method(typeof(Event_PizzaSnack), "CloseStore");
            yield return Method(typeof(Event_PizzaSnack), "OpenTakeOutDoor");
            yield return Method(typeof(Event_PizzaSnack), "CloseTakeOutDoor");
            yield return Method(typeof(Event_PizzaSnack), "EventCompleted");
            yield return Method(typeof(Event_PizzaSnack_DropOffCube), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(VehicleForSale_Display), nameof(VehicleForSale_Display.MouseDown));
            yield return Method(typeof(VehicleForSale_Display), nameof(VehicleForSale_Display.MouseHover));
            foreach (var method in Patches.SharedRearToePatch.Targets()) yield return (MethodInfo)method;
            yield return Method(typeof(NWH.VehiclePhysics2.VehicleGUI.DashGUIController), "Update");
            yield return Method(typeof(Cash_Manager), nameof(Cash_Manager.TryToBuy_Vehicle), typeof(VehicleForSale_Display));
            foreach (var method in new[] {
                Method(typeof(Tool_TirePump), nameof(Tool_TirePump.MouseDown)),
                Method(typeof(Tool_TirePump), nameof(Tool_TirePump.MouseUp)),
                Method(typeof(Tool_TirePump), nameof(Tool_TirePump.MouseRightDown)),
                Method(typeof(Tool_TirePump), nameof(Tool_TirePump.MouseRightUp)),
                Method(typeof(Tool_Air_TirePump), nameof(Tool_Air_TirePump.MouseDown)),
                Method(typeof(Tool_Air_TirePump), nameof(Tool_Air_TirePump.MouseUp)),
                Method(typeof(Tool_Air_TirePump), nameof(Tool_Air_TirePump.MouseRightDown)),
                Method(typeof(Tool_Air_TirePump), nameof(Tool_Air_TirePump.MouseRightUp)),
                Method(typeof(Part_Driveline_Wheel), "set_Value1", typeof(float)),
                Method(typeof(Part_Driveline_Wheel), nameof(Part_Driveline_Wheel.PopTheTire)),
                Method(typeof(AirCompressor), nameof(AirCompressor.UsingAir)),
                Method(typeof(AirCompressor), "StartMotor"),
                Method(typeof(InteractableTools), "canEquip")
            }) yield return method;
            yield return Method(typeof(StoreManagers), "Tick", typeof(int), typeof(int));
            yield return Method(typeof(InteractableItems), "StartFire");
            yield return Method(typeof(Tool_Envelope), nameof(Tool_Envelope.StartTheFire));
            yield return Method(typeof(Tool_Envelope), nameof(Tool_Envelope.MouseDown));
            yield return Method(typeof(Tool_Envelope), nameof(Tool_Envelope.Start));
            yield return Method(typeof(Envelope_Police_Ticket), nameof(Envelope_Police_Ticket.Equiped));
            yield return Method(typeof(Furniture_Phone), nameof(Furniture_Phone.PutApareilleBack));
            yield return Method(typeof(InteractableTools), nameof(InteractableTools.UnEquiped));
            // This private mesh refresh is reflected directly, rather than Harmony-patched.
            Method(typeof(Tool_WateringCan), "UpdateVisual");
            yield return Method(typeof(Newspaper_Crumpled), nameof(Newspaper_Crumpled.StartTheFire));
            yield return Method(typeof(Vehicle_PartSocket), "InstallPart", typeof(InteractableParts));
            yield return Method(typeof(Vehicle_PartSocket), "RemovePart");
            yield return Method(typeof(PlayerInteractable), "RemovePart_Timer", typeof(Vehicle_PartSocket));
            yield return Method(typeof(Vehicle_PartSocket), "CheckIfCanToRemoved");
            Field(typeof(PlayerInteractable), "ui_Crosshair", typeof(UI_Crosshair));
            _ = Method(typeof(UI_Crosshair), "RemoveAPart", typeof(float));
            yield return Method(typeof(Vehicle_PartsManager), "VehiclePart_FullyTighten", typeof(Vehicle_PartSocket));
            yield return Method(typeof(Vehicle_PartsManager), "CheckPartSolidity", typeof(Vehicle_PartSocket));
            foreach (var method in Patches.SharedSteeringRackValuesPatch.TargetMethods())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            foreach (var method in Patches.SharedPendingPartValuesPatch.TargetMethods())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Part_FuelTank), "set_Get_FuelLevel", typeof(float));
            foreach (var type in new[] { typeof(Tool_FuelCanister), typeof(Kaligaz_FuelGun) })
            {
                var coroutine = AccessTools.Method(type, "LookingForContainerHole");
                var step = coroutine?.ReturnType == typeof(System.Collections.IEnumerator) ? AccessTools.EnumeratorMoveNext(coroutine) : null;
                if (step == null || step.ReturnType != typeof(bool) || AccessTools.Field(step.DeclaringType, "<>4__this")?.FieldType != type)
                    throw new InvalidOperationException("Unsupported native pour coroutine: " + type.FullName);
                yield return step;
            }
            foreach (var method in Patches.GaragePressPatch.Targets().Concat(Patches.GarageHostOnlyPatch.Targets()))
                yield return Method(method.DeclaringType!, method.Name);
            foreach (var method in Patches.FoodPrepPatchTargets.Targets())
                yield return Method(method.DeclaringType!, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Furniture_Fryer), nameof(Furniture_Fryer.PressPotato));
            yield return Method(typeof(SOUND_Furniture_Fryer), nameof(SOUND_Furniture_Fryer.Close));
            yield return Method(typeof(SOUND_Furniture_Fryer), nameof(SOUND_Furniture_Fryer.Open));
            yield return Method(typeof(SOUND_Furniture_Fryer), nameof(SOUND_Furniture_Fryer.BuildPoutine));
            yield return Method(typeof(HomeGenerator), "Tick", typeof(int));
            yield return Method(typeof(HomeGenerator), "StartEngine");
            yield return Method(typeof(Store_PostOffice), "TryingToSendThisLetter", typeof(Tool_Envelope));
            yield return Method(typeof(Store_PostOffice), "MoveEnveloppe", typeof(GameObject), typeof(Envelope_Type));
            yield return Method(typeof(Cottage), "BoughtAnimation");
            yield return AccessTools.PropertyGetter(typeof(HomeGenerator), "Get_IsHydroRunning") ??
                throw new InvalidOperationException("Unsupported native property getter: HomeGenerator.Get_IsHydroRunning");
            yield return AccessTools.PropertyGetter(typeof(HomeGenerator), "Get_isEngineRunning") ??
                throw new InvalidOperationException("Unsupported native property getter: HomeGenerator.Get_isEngineRunning");
            yield return Method(typeof(TreeBase), "Cut", typeof(Vector3)); yield return Method(typeof(TreeTrunk), "CutAppart");
            yield return Method(typeof(Store_Sawmill_ReceptionWoodLogs), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(WoodSplitter), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Item_FireWood), nameof(Item_FireWood.Start));
            yield return Method(typeof(Furniture_CarLift), "LiftButtonPressed", typeof(bool)); yield return Method(typeof(Furniture_CarLift), "LiftButtonReleased"); yield return Method(typeof(Furniture_CarLift), "MoveArms", typeof(int), typeof(int)); yield return Method(typeof(Furniture_CarLift), "ForceUnfreeze");
            yield return Method(typeof(Furniture_CarLift), nameof(Furniture_CarLift.FreezeUnfreezeVehicleOnLift), typeof(bool));
            Field(typeof(Furniture_CarLift), "Cache_VehicleFrozen", typeof(NWH.Vehicle));
            yield return Method(typeof(SugarShack), "Tick", typeof(int)); yield return Method(typeof(SugarShack), "DayChanged"); yield return Method(typeof(SugarShack), "WoodBurningInEvap", typeof(float));
            yield return Method(typeof(ItemForSale_SugarShack), "MouseDown");
            yield return Method(typeof(ItemForSale_SugarShack), "MouseHover");
            yield return Method(typeof(SOUND_SugarShack), "PressSyrup");
            yield return Method(typeof(SugarShack), "ToggleValve_TankToEvap"); yield return Method(typeof(SugarShack), "ToggleValve_SyrupToBaril");
            foreach (string name in new[] { "ElectricAutoHeater_TurnOn", "ElectricAutoHeater_TurnOff", "OutsidePump_TurnOn", "OutsidePump_TurnOff" }) yield return Method(typeof(SugarShack), name);
            yield return Method(typeof(SugarShack_CrimpingMachine), "OnTriggerEnter", typeof(Collider)); yield return Method(typeof(SugarShack_ReceptionWoodLogs), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(BuggyBuild), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Cottage_SellSign), "MouseDown"); yield return Method(typeof(Cottage_SellSign), "Update_NameToAddAfter");
            foreach (var target in Patches.SmallCruiserSalePatch.Targets().Concat(Patches.FishingBoatSalePatch.Targets())
                .Concat(Patches.BoatSalePriceQuotePatch.Targets()))
                yield return Method(target.DeclaringType!, target.Name, target.GetParameters().Select(p => p.ParameterType).ToArray());
            yield return Method(typeof(Bunker_CompostMachine), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Bunker_CompostMachine), "Button_MakeFertilizer");
            yield return Method(typeof(Bunker_CompostMachine), "HourChanged");
            yield return Method(typeof(CoffeeSeedAutoSpawn), "MouseDown"); yield return Method(typeof(CoffeeSeedAutoSpawn), "NewDay", typeof(int));
            yield return Method(typeof(Furniture_CannabisGrinder), "MouseDown");
            yield return Method(typeof(Furniture_CannabisPackager), "MouseDown");
            yield return Method(typeof(Item_CoffeePercolator), "MouseDown");
            yield return Method(typeof(Wheelbarrow), "HandleClick"); yield return Method(typeof(Wheelbarrow), "ResetLocation"); yield return Method(typeof(Wheelbarrow), "Update");
            yield return Method(typeof(MechanicCreeper), "MouseDown");
            yield return AccessTools.Method(typeof(SugarShack_SlabBuild), "BuildSugarShack") ?? throw new InvalidOperationException("Unsupported native method: SugarShack_SlabBuild.BuildSugarShack");
            yield return Method(typeof(UI_EventMessage), "Show", typeof(string), typeof(string), typeof(float), typeof(UI_EventMessage.EventIconType));
            yield return Method(typeof(UI_EventMessage), "Show", typeof(Color), typeof(string), typeof(string), typeof(float), typeof(UI_EventMessage.EventIconType));
            yield return Method(typeof(UI_ErrorMessage), "Show", typeof(string), typeof(float));
            // Presentation relays (static scan 2026-09-28).
            yield return Method(typeof(SOUND_Tool_DirtShovel), "Shovel");
            yield return Method(typeof(Actor), "ActorVoice_Play", typeof(AudioClip));
            yield return Method(typeof(SOUND_ActorVoices), "CommentingOnProduct", typeof(Actor));
            yield return Method(typeof(SOUND_General), "Spent");
            yield return Method(typeof(SOUND_General), "Received");
            yield return Method(typeof(Steam_AchievementManager), "AchievementDone", typeof(AchievementType));
            yield return Method(typeof(SOUND_WoodSplitter), "PullStart");
            yield return Method(typeof(SOUND_Manager), "Play_General_LeatherCrafting");
            yield return Method(typeof(SOUND_Manager), "Play_General_TapePacking");
            yield return Method(typeof(Tool_FishingRod), nameof(Tool_FishingRod.Equiped));
            yield return Method(typeof(Tool_FishingRod), nameof(Tool_FishingRod.Start));
            yield return Method(typeof(Tool_FishingRod), nameof(Tool_FishingRod.UnEquiped));
            yield return Method(typeof(AnimalRunAcrossTheRoad), "PlayerEnteredZone");
            foreach (string name in new[] { "PlaySound_BoltScrewing", "PlaySound_BoltScrewingOff", "PlaySound_BoltUnScrewing", "PlaySound_BoltUnScrewingOff",
                "PlaySound_BoltScrewingInstalled", "PlaySound_BoltScrewingUnInstalled", "PlaySound_ImpactUnScrew" })
                yield return Method(typeof(SOUND_Manager), name);
            // Speedway events (SharedSpeedwayEvents).
            foreach (string name in new[] { "PlayerEntered_Drag", "PlayerEntered_Oval", "PlayerEntered_Track", "PlayerCross_DragFinishLine", "PlayerCross_OvalCheckpoint", "PlayerCross_TrackCheckpoint" })
                yield return Method(typeof(Speedway), name);
            yield return Method(typeof(Event_Championship), "InscriptionFormClicked"); yield return Method(typeof(Event_Championship), "StartDetector_PlayerIn");
            yield return Method(typeof(Event_Championship), "Player_Checkpoint"); yield return Method(typeof(Event_Championship), "Championship_NextEvent", typeof(bool));
            yield return Method(typeof(Event_DerbyDemolition), "InscriptionFormClicked"); yield return Method(typeof(Event_DerbyDemolition), "StartLine_PlayerEntered");
            yield return Method(typeof(Event_DerbyDemolition), "Tick", typeof(int)); yield return Method(typeof(Event_DerbyDemolition), "RoundCompleted", typeof(bool), typeof(bool));
            yield return Method(typeof(Event_DerbyDemolition), "CloseStore");
            foreach (string name in new[] { "PlaySound_RaceCountdown", "PlaySound_RaceStart", "PlaySound_Race_Checkpoint", "PlaySound_Race_Finish", "PlaySound_Race_Whistle", "PlaySound_Race_CarDead", "PlaySound_BuggyBuildCompleted" })
                yield return Method(typeof(SOUND_Manager), name);
            yield return Method(typeof(UI_EventMessage), "Hide");
            yield return Method(typeof(NWH.VehiclePhysics2.Effects.DamageHandler), "Enqueue", typeof(Collision), typeof(float));
            yield return Method(typeof(NWH.VehiclePhysics2.Effects.DamageHandler), "Repair");
            foreach (string name in new[] { "Drag_StartLine", "Drag_FinishLine", "Oval_StartLine", "Track_StartLine" }) Field(typeof(Speedway), name, typeof(GameObject));
            Field(typeof(Speedway), "Track_Checkpoints", typeof(GameObject[])); Field(typeof(Speedway), "Oval_Checkpoints", typeof(GameObject[]));
            foreach (string name in new[] { "dragTree", "trackTree", "ovalTree" }) Field(typeof(Speedway), name, typeof(DragTree));
            foreach (string name in new[] { "IsRacing", "PlayerInDragStart", "PlayerInOvalStart", "PlayerInTrackStart" }) Field(typeof(Speedway), name, typeof(bool));
            Field(typeof(Speedway), "DerbyWon", typeof(int)); Field(typeof(Speedway), "ChampionshipWon", typeof(int));
            Field(typeof(Speedway), "speedway_BestTimes", typeof(Speedway_BestTimes)); Field(typeof(Speedway), "Championship_BestTimes", typeof(Speedway_Championship_BestTimes));
            Field(typeof(Speedway), "speedway_RacecarToDo", typeof(Speedway_RacecarToDo));
            Field(typeof(DragTree), "cacheStage", typeof(int)); Field(typeof(PlayerDetector), "Variables", typeof(GameObject[]));
            Field(typeof(Canvas_3D_TrafficLight), "TrafficLight", typeof(GameObject)); Field(typeof(Canvas_3D_TrafficLight), "CurrentStatus", typeof(Canvas_3D_TrafficLight.TrafficLightStatus));
            Field(typeof(Event_Championship), "EventRunning", typeof(bool)); Field(typeof(Event_Championship), "isRacing", typeof(bool));
            Field(typeof(Event_Championship), "InscriptionBooth_Form", typeof(Furniture_SwitchButton));
            Field(typeof(Event_Championship), "dragTree", typeof(DragTree)); Field(typeof(Event_Championship), "trackTree", typeof(DragTree));
            Field(typeof(Event_Championship), "Canvas_TrafficLight", typeof(Canvas_3D_TrafficLight)); Field(typeof(Event_Championship), "UI_Event_Championship", typeof(UI_Event_Championship));
            Field(typeof(Event_Championship), "CurrentRace_Lap", typeof(int)); Field(typeof(Event_Championship), "Championship_Stage", typeof(int));
            Field(typeof(Event_Championship), "CurrentRace_Timer_Lap", typeof(float)); Field(typeof(Event_Championship), "CurrentRace_Timer_LastLap", typeof(float));
            Field(typeof(Event_Championship), "Championship_TrophySpawnLocation", typeof(Transform));
            foreach (string name in new[] { "PlayerDetector_StartingLine", "Checkpoints_Rally", "Checkpoints_Drag", "Checkpoints_Race" }) Field(typeof(Event_Championship), name, typeof(PlayerDetector[]));
            foreach (string name in new[] { "AI_Championship_Rally1", "AI_Championship_Rally2", "AI_Championship_Drag1", "AI_Championship_Drag2", "AI_Championship_Race1", "AI_Championship_Race2" })
                Field(typeof(Event_Championship), name, typeof(AI_RallyCar));
            Field(typeof(AI_RallyCar), "vc", typeof(NWH.VehiclePhysics2.VehicleController));
            Field(typeof(Event_DerbyDemolition), "EventRunning", typeof(bool)); Field(typeof(Event_DerbyDemolition), "PlayerIsRegistered", typeof(bool));
            Field(typeof(Event_DerbyDemolition), "AI_Vehicles", typeof(Event_DerbyDemolition_Vehicle[])); Field(typeof(Event_DerbyDemolition), "playerCar", typeof(Event_DerbyDemolition_Vehicle));
            foreach (string name in new[] { "EntranceBlock", "PlayerDetector", "Actors_Spectator", "DerbyCars" }) Field(typeof(Event_DerbyDemolition), name, typeof(GameObject));
            Field(typeof(Event_DerbyDemolition), "InscriptionBooth_Form", typeof(Furniture_SwitchButton)); Field(typeof(Event_DerbyDemolition), "ui_Event_DerbyDemolition", typeof(UI_Event_DerbyDemolition));
            Field(typeof(Event_DerbyDemolition), "Derby_TrophySpawnLocation", typeof(Transform));
            Field(typeof(Event_DerbyDemolition), "Round_Current", typeof(int)); Field(typeof(Event_DerbyDemolition), "Round_Total", typeof(int));
            Field(typeof(Event_DerbyDemolition), "Coroutine_RaisingCars", typeof(bool)); Field(typeof(Event_DerbyDemolition), "Coroutine_LoweringCars", typeof(bool));
            Field(typeof(Event_DerbyDemolition), "PositionWhileOpen", typeof(Vector3)); Field(typeof(Event_DerbyDemolition), "PositionWhileClose", typeof(Vector3));
            Field(typeof(Event_DerbyDemolition_Vehicle), "Slider_HealthBar", typeof(UnityEngine.UI.Slider)); Field(typeof(Event_DerbyDemolition_Vehicle), "Image_HealthBar", typeof(UnityEngine.UI.Image));
            Field(typeof(Event_DerbyDemolition_Vehicle), "VFX_Steam", typeof(ParticleSystem));
            Field(typeof(Event_DerbyDemolition_Vehicle), "Health", typeof(float)); Field(typeof(Event_DerbyDemolition_Vehicle), "MaxHealth", typeof(float));
            Field(typeof(UI_Event_DerbyDemolition), "DerbyDemolitionUI", typeof(GameObject));
            Field(typeof(NWH.VehiclePhysics2.Effects.DamageHandler), "_deformableMeshFilters", typeof(System.Collections.Generic.List<MeshFilter>));
            Field(typeof(PetNeeds), "NeedType", typeof(int)); Field(typeof(Furniture_Fryer), "LastTimeAddedCurds", typeof(float));
            Field(typeof(CoffeeSeedAutoSpawn), "SeedsSpawn_Location", typeof(Transform)); Field(typeof(Gameplay), "playerState", typeof(PlayerState));
            Field(typeof(Bunker_CompostMachine), "MachineWorking", typeof(bool));
            Field(typeof(Actor_Police), "Timer_LastTimeSmokedOn", typeof(float)); Field(typeof(Actor_Police), "Police_SpotDetails", typeof(Police_SpotDetails));
            Field(typeof(SOUND_ActorVoices), "ActorVoices", typeof(Actor_VoiceLines[])); Field(typeof(Tool_FertilizerPack), "VFX_FertilizerPack", typeof(GameObject));
            Field(typeof(AnimalRunAcrossTheRoad), "Coroutine_AnimalRunning", typeof(bool)); Field(typeof(AnimalRunAcrossTheRoad), "LastTimeItRun", typeof(float));
            Field(typeof(AnimalRunAcrossTheRoad), "mesh", typeof(SkinnedMeshRenderer)); Field(typeof(AnimalRunAcrossTheRoad), "AnimalFurs", typeof(Material[]));
            _ = AccessTools.Method(typeof(Event_DerbyDemolition), "LowerCars") ?? throw new InvalidOperationException("Unsupported native method: Event_DerbyDemolition.LowerCars");
            _ = AccessTools.Method(typeof(AnimalRunAcrossTheRoad), "AnimalRunning") ?? throw new InvalidOperationException("Unsupported native method: AnimalRunAcrossTheRoad.AnimalRunning");
            yield return Method(typeof(DisableWhenPlayerTooFar), "Tick", typeof(int));
            yield return Method(typeof(UI_GenericWindowManager), "OpenDialogs", typeof(Actor), typeof(bool));
            yield return Method(typeof(Furniture_Chair), "MouseDown"); yield return Method(typeof(Furniture_Decoration_Seat), "MouseDown");
            yield return Method(typeof(Furniture_Chair), nameof(Furniture_Chair.Start));
            yield return Method(typeof(Furniture_Decoration_Seat), nameof(Furniture_Decoration_Seat.Start));
            yield return Method(typeof(Scrapyard_Container_SellMetal), "Button_SellMetal");
            yield return Method(typeof(TrashRecycle), "ShowMessage", typeof(InteractableItems));
            yield return Method(typeof(TrashRecycle), "EmptyTrash");
            // Reflection-only native camera cache contract; these are not Harmony targets.
            SharedTraffic.VerifyCameraCacheLayout();
            Field(typeof(TrafficManager), "drivingAI", typeof(DrivingAI));
            Field(typeof(DrivingAI), "driveActions", typeof(DriveActions[]));
            Field(typeof(GleyTrafficSystem.VehicleComponent), "maxSpeed", typeof(int));
            Field(typeof(GleyTrafficSystem.VehicleComponent), "isInTrigger", typeof(bool));
            Field(typeof(GleyTrafficSystem.VehicleComponent), "obstacleList", typeof(System.Collections.Generic.List<Collider>));
            Field(typeof(GleyTrafficSystem.VehicleComponent), "lightsOn", typeof(bool));
            Field(typeof(GleyUrbanAssets.VisibilityScript), "readyToRemove", typeof(bool));
            Field(typeof(GleyUrbanAssets.VisibilityScript), "neverBeenVisible", typeof(bool));
            Field(typeof(VehicleLightsComponent), "currentTime", typeof(float));
            Field(typeof(VehicleLightsComponent), "updateLights", typeof(bool));
            Field(typeof(VehicleLightsComponent), "leftBlink", typeof(bool));
            Field(typeof(VehicleLightsComponent), "rightBlink", typeof(bool));

            Field(typeof(TrafficManager), "activeCameraPositions", SharedTraffic.CameraCacheArrayType);
            Field(typeof(GleyUrbanAssets.GridManager), "activeCameraPositions", SharedTraffic.CameraCacheArrayType);
            Field(typeof(TrafficManager), "activeSquaresLevel", typeof(int));
            yield return Method(typeof(TrafficManager), "Update"); yield return Method(typeof(TrafficManager), "FixedUpdate");
            yield return Method(typeof(GleyTrafficSystem.VehicleComponent), "OnTriggerExit", typeof(Collider));
            yield return Method(typeof(GleyTrafficSystem.VehicleComponent), "OnTriggerEnter", typeof(Collider));
            yield return Method(typeof(Tool_TubingPost), "MouseDown"); yield return Method(typeof(Tool_TubingChalumeau), "MouseDown"); yield return Method(typeof(SugarShack_TubingPost), "MouseRightDown");
            yield return Method(typeof(Tool_TubingRoll), "TubeConnected"); yield return Method(typeof(SugarShack_Tubing), "DeleteThisTube");
            yield return Method(typeof(Tool_TubingRoll), nameof(Tool_TubingRoll.Equiped));
            yield return Method(typeof(Tool_TubingRoll), nameof(Tool_TubingRoll.UnEquiped));
            yield return Method(typeof(SugarShack_TubingPost), nameof(SugarShack_TubingPost.Start));
            yield return Method(typeof(Tool_PruningLoppers), nameof(Tool_PruningLoppers.Equiped));
            yield return Method(typeof(Tool_PruningLoppers), nameof(Tool_PruningLoppers.UnEquiped));
            yield return Method(typeof(TreeBase), nameof(TreeBase.Start));
            yield return Method(typeof(Garden_TreeCuttings), nameof(Garden_TreeCuttings.Start));
            yield return Method(typeof(TreeBase), "NewDayEvent", typeof(int)); yield return Method(typeof(Tree_Growing), "NewDay", typeof(int)); yield return Method(typeof(TreeBase), "GrabCuttings");
            yield return Method(typeof(Tree_Growing), "Fertilize"); yield return Method(typeof(Tree_Growing), "Cut"); yield return Method(typeof(Garden_TreeCuttings), "MouseDown"); yield return Method(typeof(Tool_PruningLoppers), "SpawnAddTreeCutting", typeof(TreeBase));
            yield return Method(typeof(Garden_DirtPatch), "Tick", typeof(int)); yield return Method(typeof(Garden_DirtPatch), "DayChanged");
            yield return Method(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.MouseHover));
            yield return Method(typeof(Garden_DirtPatch), "Plant", typeof(SeedsType)); yield return Method(typeof(Garden_DirtPatch), "Water", typeof(bool));
            yield return Method(typeof(Garden_DirtPatch), "Fertilize"); yield return Method(typeof(Garden_DirtPatch), "Desinfect"); yield return Method(typeof(Garden_DirtPatch), "Harvest");
            yield return Method(typeof(Garden_DirtPatch), "RemovePlantFromShovel"); yield return Method(typeof(Garden_DirtPatch), "RemoveWithAShovel"); yield return Method(typeof(Tool_DirtShovel), "MouseDown"); yield return Method(typeof(Tool_DirtShovel), "MouseRightDown"); Field(typeof(Tool_DirtShovel), "mainCamera", typeof(Camera));
        }
        private static MethodInfo Method(Type type, string name, params Type[] args)
        {
            var method = AccessTools.Method(type, name, args);
            Type result = (type == typeof(Vehicle_Button_WindowSwitch) && name == "MovingWindow") || (type == typeof(Gameplay) && (name == "Sleeping" || name == "Death" || name == "PermaDeath")) ||
                (type == typeof(Tool_Air_Impact) && name == "Spanner") || (type == typeof(Store_PostOffice) && name == "MoveEnveloppe") || (type == typeof(Cottage) && name == "BoughtAnimation") || (type == typeof(PlayerInteractable) && name == "CheckForHoist") ||
                (type == typeof(Furniture_Fryer) && name == "BuildPoutine") || (type == typeof(NitrousRefillStation) && name == "RefillBottle") ||
                (type == typeof(Item_CoffeePercolator) && name == "BrewingCoffee") ||
                (type == typeof(PlayerInteractable) && name == "RemovePart_Timer") ||
                (type == typeof(UniStormSystem) && name == "Coroutine_ThisAmountOfTick") ||
                (type == typeof(EntreposageGarage_Door) && name == "DoorMoving") ||
                ((type == typeof(Tool_AdjustableSpanner) || type == typeof(Tool_Air_Impact)) && name == "Spanner") ||
                (type == typeof(Tool_SprayPaintCan) && name == "CheckActivePannel") ||
                (type == typeof(SOUND_Manager) && name == "LoadMusic") ||
                (type == typeof(EscapeMenu_GeneralV2) && name == "LoadCD") ? typeof(System.Collections.IEnumerator) :
                (type == typeof(PlayerInteractable) && name == "get_IsHoldingAnObject") ||
                (type == typeof(Vehicle_PartSocket) && name == "CheckIfCanToRemoved") ||
                (type == typeof(InteractableTools) && (name == "canEquip" || name == "get_isCanInteractWhileHolding")) ||
                (type == typeof(Actor_Police) && name == "canGiveMapleSyrupCan") ||
                (type == typeof(Vehicle_Button_Shifter) && name == nameof(Vehicle_Button_Shifter.CanUse)) ||
                (type == typeof(Interactable) && name == nameof(Interactable.CanUse)) ? typeof(bool) :
                type.IsNested && name == "MoveNext" && typeof(System.Collections.IEnumerator).IsAssignableFrom(type) ? typeof(bool) :
                type == typeof(Home_Bunker) && name == "ScanForThisPlant" ? typeof(bool) :
                type == typeof(Home_Bunker) && name == "AddFertilizer" ? typeof(float) :
                (type == typeof(Furniture_Phone) && name == "RingingCompleted") ||
                (type == typeof(SOUND_ActorVoices) && name == nameof(SOUND_ActorVoices.CommentingOnProduct)) ? typeof(int) :
                (type == typeof(InteractablePacks) || type == typeof(Item_WeedPreRollBigPack) || type == typeof(Item_WeedPreRollHugePack)) && name == "Use" ? typeof(bool) : type == typeof(InteractablePacks) && name == "GetOnePackedItem" ? typeof(GameObject) :
                type == typeof(Tool_FishBucket) && name == "Add_Fish" ? typeof(bool) : type == typeof(NWH.VehiclePhysics2.Effects.DamageHandler) && name == "Enqueue" ? typeof(bool) :
                type == typeof(Cash_Manager) && name == "TryToBuy" ? typeof(InteractableItems) :
                type == typeof(Cash_Manager) && name == "isCanAfford" ? typeof(bool) : (type == typeof(Item_CoffeePercolator) || type == typeof(Tool_Fence)) && name == "Use" ? typeof(bool) : type == typeof(Vehicle_WindowTint) && name == "ApplyTint" ? typeof(bool) : type == typeof(Vehicle_PaintablePanel) && (name == "RepairRust" || name == "RepairDent" || name == "ForceRust") ? typeof(bool) :
                typeof(Actor).IsAssignableFrom(type) && (name == nameof(Actor.Greetings) || name == nameof(Actor.PlayerClickedOnDialog)) ? typeof(string) :
                (type == typeof(Cottage_SellSign) || type == typeof(SmallCruiser_SellSign) || type == typeof(BoatFishing_SellSign) || type == typeof(Leo_Manager)) && name == "Update_NameToAddAfter" ? typeof(string) : type == typeof(SaveSystem) && name == "GetPlayerFromSave" ? typeof(PlayerData) :
                type == typeof(SOUND_Manager) && name == "PlayOneShot2D" ? typeof(AudioSource) :
                type == typeof(AI_SchoolBus) && name == "CheckIfTeleportationNeeded" ? typeof(AI_SchoolBus_Checkpoint) :
                (type == typeof(Vehicle_Button_ParkingBrake) && name == "Use") || (type == typeof(Leo_Manager) && name == "Use") || (type == typeof(GlobalClassManager) && name == "get_HasASaveForThisGame") ||
                (type == typeof(SaveSystem) && name == "DeleteSaveName") || (type == typeof(Garden_DirtPatch) && (name == "Fertilize" || name == "Harvest")) || (type == typeof(Tree_Growing) && name == "Fertilize") || (type == typeof(SugarShack) && name.StartsWith("ToggleValve_")) || (type == typeof(EntreposageGarage_Door) && name == "Door_IsOKToClose") ? typeof(bool) : typeof(void);
            if (method == null || method.ReturnType != result ||
                !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(args) ||
                method.IsStatic != ((type == typeof(SaveSystem) && name != "Load") ||
                    (type == typeof(GameManager) && name == "set_SETTING_Cheats")))
                throw new InvalidOperationException("Unsupported native method signature: " + type.FullName + "." + name);
            return method;
        }
        internal static void ValidateFields()
        {
            Field(typeof(WheelController), "_totalForceThisUpdate", typeof(Vector3));
            Field(typeof(WheelController), "_chassisTorque", typeof(float));
            Field(typeof(WheelController), "_hasBeenEnabledThisFrame", typeof(bool));
            Field(typeof(WheelController), "_hasHitARigidbody", typeof(bool));
            Field(typeof(PlayerInteractable), "gameplay", typeof(Gameplay));
            Field(typeof(PlayerInteractable), "HoldingItem", typeof(InteractableItems));
            Field(typeof(PlayerInteractable), "PlayerHeight", typeof(float));
            // Native time-display helpers are layout dependencies, not Harmony targets.
            Field(typeof(UniStormSystem), "coroutine_ThisAmountOfTick", typeof(bool));
            Field(typeof(SpecialMenu), "Window_FastFowardingTime", typeof(GameObject));
            Field(typeof(SpecialMenu), "TEXT_FastFowardingTime", typeof(TMPro.TextMeshProUGUI));
            _ = Method(typeof(SpecialMenu), "FastForwardingTime_UpdateTime");
            Field(typeof(EscapeMenu_GeneralV2), "CheatWindow", typeof(RectTransform));
            Field(typeof(EscapeMenu_GeneralV2), "CDPlayer_SongLoaded", typeof(TMPro.TextMeshProUGUI));
            Field(typeof(EscapeMenu_GeneralV2), "FriendshipWindow", typeof(RectTransform));
            Field(typeof(EscapeMenu_GeneralV2), "Button_Cheats", typeof(UnityEngine.UI.Button));
            Field(typeof(SpecialMenu), "Toggle_GodSpeed", typeof(UnityEngine.UI.Toggle));
            Field(typeof(SpecialMenu), "SpawnObjects", typeof(GameObject[]));
            Field(typeof(SpecialMenu), "VehicleIDtoType", typeof(List<Vehicle_Type>));
            Field(typeof(SpecialMenu), "Dropdown_Vehicles", typeof(UnityEngine.UI.Dropdown));
            Field(typeof(SpecialMenu), "Dropdown_SpawnObjects", typeof(UnityEngine.UI.Dropdown));
            Field(typeof(SpecialMenu), "Dropdown_Weather", typeof(UnityEngine.UI.Dropdown));
            Field(typeof(SpecialMenu), "Dropdown_Gravity", typeof(UnityEngine.UI.Dropdown));
            Field(typeof(UI_GameCreditVersion), "gameVersion", typeof(TMPro.TextMeshProUGUI));
            Field(typeof(TEXT), "DIALOG_UNIQUE_MOTHER", typeof(string[]), isStatic: true);
            Field(typeof(NWH.VehiclePhysics2.VehicleGUI.DashGUIController), "_update", typeof(bool));
            Replication.SharedSchoolBus.ValidateNativeLayout();
            Replication.SharedMartinCar.ValidateNativeLayout();
            // These are native dependencies called or observed by replication, not Harmony targets.
            // Validate their signatures without requiring a patch owner on the methods.
            _ = Method(typeof(Fishing_Trash), "DropFromFishingRod");
            _ = Method(typeof(Part_Autometer_RPM), "Update_Knob_Text");
            _ = Method(typeof(Vehicle), "set_Velocity", typeof(Vector3));
            _ = Method(typeof(Vehicle), "set_LocalVelocity", typeof(Vector3));
            _ = Method(typeof(Vehicle), "set_LocalForwardVelocity", typeof(float));
            _ = Method(typeof(Vehicle), "set_VelocityMagnitude", typeof(float));
            Field(typeof(SOUND_Manager), "CDPlayer", typeof(List<AudioClip>));
            Field(typeof(SOUND_Manager), "RadioPlaying", typeof(List<SOUND_Radio>));
            Field(typeof(SOUND_Radio), "CurrentCDSong", typeof(int));
            Field(typeof(SOUND_Radio), "CurrentCDTime", typeof(float));
            Field(typeof(SOUND_Radio), "FMCD", typeof(int));
            Field(typeof(Part_Autometer_RPM), "RPM_ShiftLight", typeof(int));
            Field(typeof(Part_Autometer_Speed), "isInMPH", typeof(bool));
            Field(typeof(Part_Engine_ExhaustManifold_Turbo), "westgateIndex", typeof(int));
            Field(typeof(Part_Engine_ExhaustManifold_Turbo), "clips", typeof(AudioClip[]));
            Field(typeof(Part_Suspension_Hub_Stake), "PlayerGrabThatItemOneTime", typeof(int));
            Field(typeof(Part_Spoiler), "PlayerGrabThatItemOneTime", typeof(int));
            Field(typeof(TrashRecycle), "isRecycle", typeof(bool));
            Field(typeof(AI_Actor_FollowRoute), "actor", typeof(Actor));
            Field(typeof(AI_Actor_FollowRoute), "RoutePoints", typeof(List<Transform>));
            Field(typeof(AI_Actor_FollowRoute), "routes", typeof(Transform[]));
            Field(typeof(AI_Actor_FollowRoute), "targetWayPoint", typeof(Transform));
            Field(typeof(TrashRecycle), "isBoth", typeof(bool));
            Field(typeof(TrashRecycle), "DetectTrashCollider", typeof(BoxCollider));
            Field(typeof(TrashRecycle), "DetechTrashCollider2", typeof(BoxCollider));
            Method(typeof(Tool_MagneticTray), "OnCollisionEnter", typeof(Collision));
            Method(typeof(Tool_MagneticTray), "OnTriggerEnter", typeof(Collider));
            foreach (string name in new[] { "_hazardLightsOn", "_leftBlinkersOn", "_rightBlinkersOn" }) Field(typeof(NWH.VehiclePhysics2.Effects.LightsMananger), name, typeof(bool));
            foreach (string name in new[] { "_leftBlinkerTurnOnTime", "_rightBlinkerTurnOnTime" }) Field(typeof(NWH.VehiclePhysics2.Effects.LightsMananger), name, typeof(float));
            Field(typeof(Vehicle_Button_WindowSwitch), "CurrentTarget", typeof(float));
            Field(typeof(Tool_HoistPuller), "_isGroundPlaced", typeof(bool));
            foreach (string name in new[] { "MinRange", "MaxRange", "MinForce", "MaxForce" })
                Field(typeof(Tool_HoistPuller), name, typeof(float));
            Field(typeof(Tool_HoistPuller), "ForceMode", typeof(ForceMode));
            Field(typeof(Tool_HoistPuller_Handle), "boxCollider", typeof(Collider));
            Field(typeof(NWH.VehiclePhysics2.Powertrain.TransmissionComponent), "_currentGearIndex", typeof(int));
            Field(typeof(GameManager), "WeedJointAmount", typeof(int), isStatic: true);
            Field(typeof(Vehicle_Button_ExtraLights), "vehicleController", typeof(VehicleController));
            Field(typeof(Vehicle_Button_ExtraLights), "RotationOn", typeof(Vector3));
            Field(typeof(Vehicle_Button_ExtraLights), "RotationOff", typeof(Vector3));
            Field(typeof(Vehicle_HighLowShifter), "CurrentPosition", typeof(int));
            Field(typeof(Vehicle_HighLowShifter), "Clip_2x4", typeof(AudioClip));
            Field(typeof(Vehicle_HighLowShifter), "Clip_4x4", typeof(AudioClip));
            Field(typeof(Vehicle_Button_LightDomeOptions), "vc", typeof(VehicleController));
            Field(typeof(SOUND_Vehicle_ElectricWindow), "ElectricWindow", typeof(AudioSource));
            Field(typeof(Vehicle_Button_WindowSwitch), "Window", typeof(Transform));
            Field(typeof(Vehicle_Button_WindowSwitch), "LeverMeshToRotate", typeof(Transform));
            Field(typeof(EscapeMenuV2), "QuitButtonClicked", typeof(int));
            Field(typeof(ItemForSale_Display), "price", typeof(float));
            Field(typeof(ItemForSale_Display_DogProduct), "NeedLeoTamedToPurschase", typeof(bool));
            Field(typeof(ToDoList_Manager), "ToDoListsPerType", typeof(List<ToDoList_Object>));
            Field(typeof(ToDoList_LineDetails), "value", typeof(bool));
            Field(typeof(ItemForSale_Display_ToDoListToUnlock), "toDoList_Line", typeof(ToDoList_Line));
            Field(typeof(Cash_Manager), "cash", typeof(int)); Field(typeof(Cash_Manager), "CashPerSunday", typeof(float[]));
            Field(typeof(SlotMachine), "StoreBar", typeof(Store_Bar)); Field(typeof(SlotMachine), "Coroutine_Spinning", typeof(bool));
            Field(typeof(SlotMachine_Button), "Button_Type", typeof(SlotMachine_Button.ButtonType));
            Field(typeof(UI_ShowCash), "CoroutineRunning", typeof(bool)); Field(typeof(UI_ShowCash), "Cache_cashAdded", typeof(float));
            Field(typeof(PlayerInteractable), "HoldingItem", typeof(InteractableItems));
            Field(typeof(Tool_WindowTint), "tintColor", typeof(Color));
            Field(typeof(Pack_MapleSyrupCrate), "IsCrateFull", typeof(bool));
            Field(typeof(Pack_MapleSyrupCrate), "IsCrateFull_Type", typeof(int));
            Field(typeof(Vehicle_WindowTint), "isTint", typeof(bool));
            Field(typeof(PlayerInteractable), "currentInteractableItem", typeof(InteractableItems));
            Field(typeof(PlayerInteractable), "currentInteractable", typeof(Interactable));
            Field(typeof(PlayerInteractable), "activeTool", typeof(InteractableTools));
            Field(typeof(PlayerInteractable), "activeToolType", typeof(ToolsType));
            Field(typeof(PlayerInteractable), "isMouseLeftClickDown", typeof(bool));
            Field(typeof(PlayerInteractable), "isMouseRightClickDown", typeof(bool));
            Field(typeof(Tool_FishBucket), "FishesInBucket", typeof(List<Item_Fish>));
            Field(typeof(Tool_FishBucket), "Bucket_Bottom", typeof(Transform));
            Field(typeof(BasicNeeds_Manager), "EnergyDrinkInBlood", typeof(int));
            Field(typeof(Furniture_Light), "SwitchButton", typeof(Furniture_SwitchButton));
            Field(typeof(Furniture_Decoration), "CanBeFreeze", typeof(bool));
            Field(typeof(BuggyBuild), "PackInstalled", typeof(bool[])); Field(typeof(BuggyBuild), "vc", typeof(VehicleController));
            Field(typeof(BuggyBuild), "Seat", typeof(GameObject)); Field(typeof(BuggyBuild), "ToEnableWhenCompleted", typeof(GameObject[]));
            Field(typeof(BuggyBuild), "ToDisableWhenCompleted", typeof(GameObject[]));
            Field(typeof(BuggyBuild.BuggyBuild_PacksDetails), "ToEnableOnceInstalled", typeof(GameObject[]));
            Field(typeof(BuggyBuild.BuggyBuild_PacksDetails), "ToDisableOnceInstalled", typeof(GameObject[]));
            Field(typeof(RacecarBuild), "PackInstalled", typeof(bool[])); Field(typeof(RacecarBuild), "vc", typeof(VehicleController));
            Field(typeof(RacecarBuild), "PacksDetails", typeof(RacecarBuild.RaceCar_PacksDetails[]));
            Field(typeof(RacecarBuild), "ToEnableWhenCompleted", typeof(GameObject[]));
            Field(typeof(RacecarBuild), "ToDisableWhenCompleted", typeof(GameObject[]));
            Field(typeof(RacecarBuild.RaceCar_PacksDetails), "ToEnableOnceInstalled", typeof(GameObject[]));
            Field(typeof(RacecarBuild.RaceCar_PacksDetails), "ToDisableOnceInstalled", typeof(GameObject[]));
            Field(typeof(Cottage_SellSign), "Cottage", typeof(Cottage));
            Field(typeof(Cottage), "CottageGate", typeof(Transform)); Field(typeof(Cottage), "SellSign", typeof(Transform));
            _ = Method(typeof(Cottage), "TryToBuy");
            Field(typeof(BoatFishing_SellSign), "FishNeeded", typeof(BoatFishing_SellSign.Fish_Needed[]));
            Field(typeof(BoatFishing_SellSign), "ToDisableWhenBought", typeof(GameObject[]));
            Field(typeof(Boat_SmallCruiser_FuelCap), "fuelCap", typeof(Part_FuelTankCap));
            _ = Method(typeof(SmallCruiser_SellSign), "MouseDown"); _ = Method(typeof(SmallCruiser_SellSign), "Update_NameToAddAfter");
            _ = Method(typeof(BoatFishing_SellSign), "MouseDown"); _ = Method(typeof(BoatFishing_SellSign), "Update_NameToAddAfter");
            foreach (string name in Replication.SharedSugarShack.BoolFields) Field(typeof(SugarShack), name, typeof(bool));
            foreach (string name in Replication.SharedSugarShack.ObjectFields) Field(typeof(SugarShack), name, typeof(GameObject));
            Field(typeof(SugarShack), "Valve_WaterTank", typeof(SugarShack_Valve_WaterTank)); Field(typeof(SugarShack), "sugarShack_CrimpingMachine", typeof(SugarShack_CrimpingMachine));
            Field(typeof(SugarShack), "PumpSyrupOutside", typeof(SugarShack_PumpSyrupOutside)); Field(typeof(SugarShack), "HeatDistortion_Particles", typeof(ParticleSystem[]));
            Field(typeof(SugarShack), "ElectricAutoHeater_Switch", typeof(Furniture_SwitchButton)); Field(typeof(SugarShack), "OutsidePump_Switch", typeof(Furniture_SwitchButton));
            Field(typeof(SugarShack), "SOUND_SugarShack", typeof(SOUND_SugarShack)); Field(typeof(SugarShack), "WaterTempInEvap", typeof(float)); Field(typeof(SugarShack), "AmountOfTreeConnected", typeof(int));
            foreach (string name in Replication.SugarShackPresentation.AudioFields) Field(typeof(SOUND_SugarShack), name, typeof(AudioSource));
            Field(typeof(SugarShack), "Gauge_WaterTemp", typeof(NWH.VehiclePhysics2.VehicleGUI.AnalogGauge)); Field(typeof(SugarShack), "Gauge_FirePower", typeof(NWH.VehiclePhysics2.VehicleGUI.AnalogGauge));
            Field(typeof(SugarShack_Valve_SyrupToBaril), "SugarShack", typeof(SugarShack));
            Field(typeof(ItemForSale_SugarShack), "price", typeof(float)); Field(typeof(ItemForSale_SugarShack), "store", typeof(Store_FederationSyrup));
            foreach (string name in new[] { "isCrimpingMachine", "isReverseOsmosis", "isWaterTank", "IsWaterTank50k", "isElectricAutoHeater", "isSiroptier", "isBedTankOutdoorPump" })
                Field(typeof(ItemForSale_SugarShack), name, typeof(bool));
            Field(typeof(Tool_TubingPost), "isTempPlaced", typeof(bool)); Field(typeof(Tool_TubingChalumeau), "isTempPlaced", typeof(bool)); Field(typeof(Tool_TubingChalumeau), "CorrectSpotTobePlaced", typeof(bool));
            Field(typeof(SugarShack_TubingPost), "TreeConnectedTo", typeof(TreeBase)); _ = Method(typeof(SugarShack_Tubing), "LoadConnect", typeof(SugarShack_TubingPost), typeof(SugarShack_TubingPost), typeof(int), typeof(int));
            Field(typeof(Tool_TubingRoll), "current_TubingPost_Connection", typeof(SugarShack_TubingPost_Connection));
            Field(typeof(TrailerHitchModule), "_configurableJoint", typeof(ConfigurableJoint));
            Field(typeof(TrailerHitchModule), "_trailerModules", typeof(List<TrailerModule>));
            Field(typeof(TrailerHitchModule), "isHitchInstalled", typeof(bool));
            Field(typeof(TreeBase), "CuttingSphere_Ready", typeof(GameObject));
            Field(typeof(Garden_TreeCuttings), "CuttingSphere", typeof(GameObject));
            _ = Method(typeof(SugarShack), "ElectricHeater_UpdateLight"); _ = Method(typeof(SugarShack), "OutsidePump_UpdateLight"); _ = Method(typeof(SugarShack), "Tick_GlowLight");
            _ = Method(typeof(SugarShack), "ElectricAutoHeater_Update", typeof(bool), typeof(bool)); _ = Method(typeof(SugarShack_Valve_SyrupToBaril), "updateVisual", typeof(bool));
            Field(typeof(Furniture_CarLift), "Collider_LiftVehicle", typeof(Transform)); Field(typeof(Furniture_CarLift), "LiftVehicle_LowerPosition", typeof(Transform));
            Field(typeof(Furniture_CarLift), "LiftVehicle_HighPosition", typeof(Transform)); Field(typeof(Furniture_CarLift), "Colliders_Button", typeof(Collider[]));
            Field(typeof(Furniture_CarLift), "MovingLift_Direction", typeof(float)); _ = Method(typeof(Furniture_CarLift), "ChangeFreezeMode", typeof(bool));
            Field(typeof(Furniture_Decoration), "ToDisableWhenGrab", typeof(GameObject[]));
            Field(typeof(Furniture_Decoration), "CustomCenterOfMass", typeof(Transform));
            Field(typeof(Item_Poutine), "PoutineGuts", typeof(MeshRenderer));
            _ = Method(typeof(Vehicle_Chainsaw_Holder), "Chainsaw_Attach", typeof(bool));
            Field(typeof(UniStormSystem), "m_LastHour", typeof(int));
            Field(typeof(TickSystem), "totalTickSinceGameStart", typeof(int));
            Field(typeof(TickSystem), "DayOfTheWeek", typeof(int)); Field(typeof(TickSystem), "NumberOfDays", typeof(int));
            Field(typeof(UniStormSystem), "m_CloudDomeMaterial", typeof(Material));
            Field(typeof(UniStormSystem), "m_WeatherGenerated", typeof(bool));
            foreach (string name in new[] { "CloudColorKeySwitcher", "CloudLightColorKeySwitcher", "FogColorKeySwitcher", "FogLightColorKeySwitcher", "AmbientSkyLightColorKeySwitcher", "AmbientEquatorLightColorKeySwitcher", "AmbientGroundLightColorKeySwitcher", "SunLightColorKeySwitcher" })
                Field(typeof(UniStormSystem), name, typeof(GradientColorKey[]));
            Field(typeof(Vehicle_Door), "isOpen", typeof(bool));
            Field(typeof(Vehicle_Door), "originalLocalPosition", typeof(Vector3)); Field(typeof(Vehicle_Door), "originalLocalEuler", typeof(Vector3));
            Field(typeof(Vehicle_Door), "DoorOpened", typeof(UnityEvent)); Field(typeof(Vehicle_Door), "DoorClosed", typeof(UnityEvent));
            Field(typeof(Vehicle_Hood), "isOpen", typeof(bool)); Field(typeof(Vehicle_Hood), "isReleased", typeof(bool));
            Field(typeof(Vehicle_Hood), "originalLocalPosition", typeof(Vector3)); Field(typeof(Vehicle_Hood), "originalLocalEuler", typeof(Vector3));
            Field(typeof(Vehicle_Hood), "Collider_CloseHoodButton", typeof(GameObject));
            Field(typeof(Vehicle_ExteriorHoodRelease), "isToCloseTheHood", typeof(bool));
            Field(typeof(Vehicle_Trunk), "isOpen", typeof(bool));
            Field(typeof(Vehicle_Trunk), "originalLocalPosition", typeof(Vector3)); Field(typeof(Vehicle_Trunk), "originalLocalEuler", typeof(Vector3));
            Field(typeof(PlayerInteractable), "Current_GeneratedBoltHole", typeof(Generated_BoltHole)); Field(typeof(PlayerInteractable), "currentGeneratedPart", typeof(Generated_Part));
            Field(typeof(Furniture_SwitchButton), "isOnClickOnly", typeof(bool));
            Field(typeof(HomeGenerator), "KillSwitchButton", typeof(Furniture_SwitchButton)); Field(typeof(HomeGenerator), "isKillSwitchOff", typeof(bool));
            Field(typeof(HomeGenerator), "store_Hydro", typeof(Store_HydroSaintClin));
            Field(typeof(HomeGenerator), "isEngineRunning", typeof(bool)); _ = Method(typeof(HomeGenerator), "EngineStartingUp"); _ = Method(typeof(HomeGenerator), "EngineShutingDown");
            Field(typeof(Furniture_Fryer), "FromageCurdsInMachine", typeof(int)); Field(typeof(Furniture_Fryer), "Coroutine_BuildingRunning", typeof(bool));
            Field(typeof(Furniture_Fryer), "animator", typeof(Animator)); Field(typeof(Furniture_Fryer), "vapeur", typeof(ParticleSystem));
            Field(typeof(Furniture_Fryer), "SOUND_Furniture_Fryer", typeof(SOUND_Furniture_Fryer));
            Field(typeof(Furniture_Fryer), "FryingBasket", typeof(GameObject));
            Field(typeof(NitrousRefillStation), "NitrousPressureGauge", typeof(NWH.VehiclePhysics2.VehicleGUI.AnalogGauge));
            Field(typeof(NitrousRefillStation), "SOUND_NitrousRefillStation", typeof(SOUND_NitrousRefillStation));
            Field(typeof(NitrousRefillStation), "isWorking", typeof(bool));
            Field(typeof(SugarShack_CrimpingMachine), "sugarShack", typeof(SugarShack));
            Field(typeof(SugarShack_CrimpingMachine), "isWorking", typeof(bool));
            Field(typeof(SugarShack_CrimpingMachine), "LastCastSpawned", typeof(Item_MapleSyrupCan));
            Field(typeof(SugarShack_CrimpingMachine), "UpDownPlateau", typeof(Transform));
            Field(typeof(Bunker_CompostMachine), "CompostLid", typeof(Furniture_Door));
            Field(typeof(Bunker_CompostMachine), "Compost", typeof(float));
            Field(typeof(Bunker_CompostMachine), "Fertilizer", typeof(float));
            Field(typeof(Home_Bunker), "BunkerCompostMachine", typeof(Bunker_CompostMachine));
            _ = Method(typeof(Bunker_CompostMachine), "UpdateVisual");
            Field(typeof(Store_Dealer_WeedReception), "isHugePack", typeof(bool));
            Field(typeof(Store_Dealer_WeedReception), "LastTimeBigPackAdded", typeof(float));
            Field(typeof(SOUND_NitrousRefillStation), "NitrousFill", typeof(AudioSource));
            Field(typeof(SOUND_NitrousRefillStation), "OneShotAudio", typeof(AudioSource));
            Field(typeof(SOUND_Furniture_Fryer), "Press", typeof(AudioSource));
            Field(typeof(SOUND_Furniture_Fryer), "BoilingOil", typeof(AudioSource));
            Field(typeof(SOUND_Furniture_Fryer), "OneShot", typeof(AudioSource));
            Field(typeof(HomeGenerator), "EngineRunningCoroutine", typeof(bool));
            Field(typeof(HomeGenerator), "FuelTank", typeof(Part_FuelTank));
            Field(typeof(HomeGenerator), "store_Hydro", typeof(Store_HydroSaintClin));
            Field(typeof(HomeGenerator), "LightsConnected", typeof(Furniture_Light[]));
            Field(typeof(Tool_FishingRod), "Tube_FishingLine", typeof(TubeRenderer)); Field(typeof(Tool_FishingRod), "Transform_LineAttachment", typeof(Transform));
            Field(typeof(Tool_FishingRod), "VFX_FishingSplash", typeof(ParticleSystem)); Field(typeof(Tool_FishingRod), "FishingRod_Folded", typeof(GameObject));
            Field(typeof(Tool_FishingRod), "FishingRod_Unfolded", typeof(GameObject));
            Field(typeof(Tool_FishingRod), "FishCaugh", typeof(GameObject));
            Field(typeof(Tool_FishingRod), "Fish_Location", typeof(Transform));
            Field(typeof(GameAssets), "Fishing_Trash", typeof(GameObject[]));
            Field(typeof(Vehicle_Underglow), "Switch_Button_Mode", typeof(Furniture_SwitchButton));
            Field(typeof(Vehicle_Underglow), "Switch_Button_Cycle", typeof(Furniture_SwitchButton));
            Field(typeof(Vehicle_Underglow), "Switch_Button_Intensity", typeof(Furniture_SwitchButton));
            Field(typeof(Vehicle_Underglow), "Switch_Button_Color", typeof(Furniture_SwitchButton));
            Field(typeof(Vehicle_Underglow), "_ignitionOn", typeof(bool));
            Field(typeof(Vehicle_Underglow), "LightSource", typeof(Light));
            Field(typeof(Vehicle_Underglow), "FeetLight_L", typeof(Light));
            Field(typeof(Vehicle_Underglow), "FeetLight_R", typeof(Light));
            Field(typeof(Vehicle_Underglow), "Underglow_Bloom", typeof(MeshRenderer));
            Field(typeof(SOUND_Tool_WateringCan), "WaterPoor", typeof(AudioSource));
            Field(typeof(SOUND_Tool_MapTorch), "MapTorch_TurnOn", typeof(AudioSource));
            Field(typeof(SOUND_Tool_MapTorch), "MapTorch_Loop", typeof(AudioSource));
            Field(typeof(SOUND_Tool_Chainsaw), "ChainsawCut", typeof(AudioSource));
            Field(typeof(PlayerInteractable), "toolLocations", typeof(InteractableToolLocation[]));
            Field(typeof(Tool_Air_TirePump), "tubeController", typeof(TubeController));
            Field(typeof(Tool_Air_TirePump), "TubeRolled", typeof(GameObject));
            Field(typeof(Tool_Air_TirePump), "TubeEnd", typeof(Transform));
            Field(typeof(Tool_Air_Impact), "airCompressor", typeof(AirCompressor));
            Field(typeof(Tool_Air_Impact), "tubeController", typeof(TubeController));
            Field(typeof(Tool_Air_Impact), "TubeRolled", typeof(GameObject));
            Field(typeof(Tool_Air_Impact), "TubeEnd", typeof(Transform));
            Field(typeof(Tool_Air_Impact), "mainCamera", typeof(Camera));
            Field(typeof(Tool_Air_Impact), "isMouseLeftClickDown", typeof(bool));
            Field(typeof(Tool_Air_Impact), "isMouseRightClickDown", typeof(bool));
            Field(typeof(TubeController), "CustomMaterial", typeof(Material));
            foreach (var pumpType in new[] { typeof(Tool_TirePump), typeof(Tool_Air_TirePump) })
            {
                Field(pumpType, "isMouseLeftClickDown", typeof(bool));
                Field(pumpType, "isMouseRightClickDown", typeof(bool));
            }
            Field(typeof(AirCompressor), "isMotorRunning", typeof(bool));
            Field(typeof(AirCompressor), "EngineFlywheel", typeof(Transform));
            Field(typeof(SOUND_AirCompressor), "AirCompressorMotor", typeof(AudioSource));
            var compressorStart = Method(typeof(AirCompressor), "StartMotor");
            if (!compressorStart.IsPrivate || compressorStart.IsStatic || compressorStart.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported native AirCompressor.StartMotor() signature.");
            var toolCanEquip = Method(typeof(InteractableTools), "canEquip");
            if (toolCanEquip.IsStatic || toolCanEquip.ReturnType != typeof(bool))
                throw new InvalidOperationException("Unsupported native InteractableTools.canEquip() signature.");
            var playerUnequip = Method(typeof(PlayerInteractable), "UnEquipTool");
            if (playerUnequip.IsStatic || playerUnequip.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported native PlayerInteractable.UnEquipTool() signature.");
            Field(typeof(PlayerInteractable), "visualHands", typeof(PlayerVisualHands));
            Field(typeof(PlayerVisualHands), "animator", typeof(Animator));
            Field(typeof(PlayerVisualHands), "inputs", typeof(PlayerInputActions));
            Field(typeof(PlayerVisualHands), "Map", typeof(GameObject));
            foreach (string name in new[] { "SorryDown", "SorryUp", "SpitDown", "Map_Toggle" })
            {
                var nativeEmote = Method(typeof(PlayerVisualHands), name);
                if (!nativeEmote.IsPrivate || nativeEmote.IsStatic || nativeEmote.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported native PlayerVisualHands." + name + "() signature.");
            }
            Field(typeof(StoreManagers), "stores", typeof(List<Stores>));
            Field(typeof(Store_TravelingVan), "PositionWhileOpen", typeof(Vector3));
            foreach (string name in new[] { "Van", "RallyShocks", "RallyShocksLocked", "DragTires", "DragTiresLocked" })
                Field(typeof(Store_TravelingVan), name, typeof(GameObject));
            Field(typeof(NWH.VehiclePhysics2.Modules.Coolant.CoolantModule), "Temp_EngineBlock", typeof(float));
            Field(typeof(NWH.VehiclePhysics2.Modules.Coolant.CoolantModule), "Temp_Radiator", typeof(float));
            _ = Method(typeof(Furniture_Fryer), "UpdateVisualBasket");
            Field(typeof(Store_HydroSaintClin), "DaySinceBuildStarted", typeof(int)); Field(typeof(Store_HydroSaintClin), "HydroCut", typeof(bool));
            Field(typeof(Store_HydroSaintClin), "BillToPay", typeof(float)); Field(typeof(Store_HydroSaintClin), "currentComsuption", typeof(float));
            Field(typeof(UI_UseKeyText), "GO_Universal", typeof(UnityEngine.GameObject));
            Field(typeof(Store_PostOffice), "ReadyToSendAnotherEnvelope", typeof(bool));
            Field(typeof(Store_PostOffice), "LocationMailBoxPlayer", typeof(Transform));
            Field(typeof(Envelope_Police_Ticket), "Text_TicketsList", typeof(TMPro.TextMeshPro));
            Field(typeof(Envelope_Police_Ticket), "BillToPay_Price", typeof(TMPro.TextMeshPro));
            Field(typeof(Tool_AdjustableSpanner), "CurrentHolding_Bolt", typeof(Hardware_Bolt)); Field(typeof(Tool_Air_Impact), "CurrentHolding_Bolt", typeof(Hardware_Bolt));
            _ = Method(typeof(Gameplay), "Force_ExitVehicle");
            _ = Method(typeof(PlayerInteractable), "UnEquipTool"); _ = Method(typeof(InteractablePacks), "UpdatePack");
            _ = Method(typeof(PlayerInteractable), "EquipTool", typeof(InteractableTools));
            Field(typeof(SOUND_General), "AdjustableSpanner_Screw", typeof(AudioSource));
            Field(typeof(SOUND_General), "AdjustableSpanner_UnScrew", typeof(AudioSource));
            _ = Method(typeof(Vehicle_Button_ParkingBrake), nameof(Vehicle_Button_ParkingBrake.VisualValue), typeof(float));
            Field(typeof(Vehicle_Button_ParkingBrake), "isYAxis", typeof(bool)); Field(typeof(Vehicle_Button_ParkingBrake), "maxAgle", typeof(float));
            Field(typeof(Part_FuelTankCap), "Screwed", typeof(float)); Field(typeof(Part_FuelTankCap), "CoroutineRunning", typeof(bool));
            Field(typeof(Part_FuelTankCap), "FuelTankHole", typeof(GameObject)); Field(typeof(Part_FuelTankCap), "OriginalParent", typeof(Transform)); Field(typeof(Part_FuelDoor), "isOpen", typeof(bool));
            _ = Method(typeof(Part_FuelTankCap), "UpdateVisual"); _ = Method(typeof(Part_FuelDoor), "UpdateVisual");
            Field(typeof(Tool_FuelCanister), "FuelTank", typeof(Part_FuelTank)); Field(typeof(Tool_FuelCanister), "CanisterFuelCap", typeof(Part_FuelTankCap));
            Field(typeof(Store_HydroSaintClin), "DaySinceBuildStarted", typeof(int)); Field(typeof(Store_HydroSaintClin), "ConstructionStages", typeof(Store_HydroSaintClin.Hydro_ConstructionStage[])); Field(typeof(Store_HydroSaintClin), "BuildCompleted", typeof(bool));
            Field(typeof(Vehicle_Nitrous_Controls), "power", typeof(int)); Field(typeof(Vehicle_Nitrous_Controls), "Power_Switch", typeof(Furniture_SwitchButton));
            Field(typeof(Vehicle_Shifter), "Knob", typeof(Transform)); Field(typeof(Vehicle_Shifter), "shifter_Positions", typeof(Vehicle_Shifter_Position[]));
            Field(typeof(BasicNeeds_Manager), "Last_SyrupWarning", typeof(float)); Field(typeof(BasicNeeds_Manager), "Last_Warning", typeof(float));
            _ = Method(typeof(BasicNeeds_Manager), "UpdateUI");
            foreach (string warning in new[] { "WarningImage_MapleSyrup", "WarningImage_Poutine", "WarningImage_Energy", "WarningImage_Anxiety" })
                Field(typeof(UI_BasicNeeds), warning, typeof(UnityEngine.UI.Image));
            Field(typeof(Vehicle_Underglow), "vehicle", typeof(NWH.Vehicle));
            _ = Method(typeof(Vehicle_PartsManager), "Update_ECU");
            Field(typeof(Store_TravelingVan), "SprayPaintAvailable", typeof(GameObject[])); Field(typeof(Store_TravelingVan), "BoltForSale", typeof(ItemForSale_Display)); Field(typeof(Store_TravelingVan), "BoltForSale2", typeof(ItemForSale_Display)); Field(typeof(Store_TravelingVan), "BoltForSale3", typeof(ItemForSale_Display));
            Field(typeof(Store_TravelingVan), "BoltsInPackForSale", typeof(MeshRenderer[])); Field(typeof(Store_TravelingVan), "BoltsInPackForSale2", typeof(MeshRenderer[])); Field(typeof(Store_TravelingVan), "BoltsInPackForSale3", typeof(MeshRenderer[])); _ = Method(typeof(Vehicle_Underglow), "UpdateLights"); _ = Method(typeof(Vehicle_Underglow), "Vehicle_IgnitionOn"); _ = Method(typeof(Vehicle_Underglow), "Vehicle_IgnitionOff");
            Field(typeof(WoodSplitter), "Ram", typeof(Transform)); Field(typeof(WoodSplitter), "Handle", typeof(Transform));
            Field(typeof(WoodSplitter), "RamMaxPosition", typeof(Transform)); Field(typeof(WoodSplitter), "RamOriginalPosition", typeof(Vector3));
            Field(typeof(Speedway), "DaySinceBuildStarted", typeof(int)); _ = Method(typeof(Speedway), "UpdateSpeedwayConstruction");
            _ = Method(typeof(Store_Roger), nameof(Store_Roger.SaveSystemJustLoaded));
            Field(typeof(GleyTrafficSystem.VehicleComponent), "engineSound", typeof(GleyTrafficSystem.EngineSoundComponent)); Field(typeof(GleyTrafficSystem.VehicleComponent), "maxSpeed", typeof(int)); Field(typeof(Kaligaz_FuelGun), "fuelStorageTank", typeof(Part_FuelTank));
            Field(typeof(Kaligaz_FuelGun), "IsDockFuelGun", typeof(bool)); Field(typeof(Kaligaz_FuelGun), "isOrdinaire", typeof(bool));
            Field(typeof(TreeBase), "Leaves", typeof(GameObject)); Field(typeof(TreeBase), "HasCuttings", typeof(bool));
            Field(typeof(Tree_Growing), "tree_Type", typeof(Tree_Type)); Field(typeof(Tree_Growing), "isFertilized", typeof(bool)); Field(typeof(Tree_Growing), "TickSincePlanted", typeof(int));
            Field(typeof(Tree_Growing), "FinalSize", typeof(Vector3)); Field(typeof(Garden_TreeCuttings), "SpaceOccupedWhenPlanting", typeof(MeshRenderer)); Field(typeof(Tool_PruningLoppers), "CutterMouthLocation", typeof(Transform)); _ = Method(typeof(Tree_Growing), "UpdateVisual"); _ = Method(typeof(TreeTrunk), "Start");
            Field(typeof(Garden_DirtPatch), "seedsType", typeof(SeedsType)); Field(typeof(Garden_DirtPatch), "WateredTick", typeof(int)); Field(typeof(Garden_DirtPatch), "AmountToHarvest", typeof(int));
            foreach (string name in new[] { "isInfested", "isFertilized", "AlreadyFullyGrown", "ReadyToHarvest", "HARDMODE_IsPlantDead" }) Field(typeof(Garden_DirtPatch), name, typeof(bool));
            Field(typeof(Garden_DirtPatch), "isUpright", typeof(bool));
            Field(typeof(Garden_DirtPatch), "GrowPercent", typeof(float)); Field(typeof(Garden_DirtPatch), "VisualPlant", typeof(Garden_PlantReadyToHarvest));
            Field(typeof(Garden_DirtPatch), "GameObject_Infestation", typeof(GameObject)); Field(typeof(Garden_DirtPatch), "mesh", typeof(MeshRenderer));
            _ = Method(typeof(Garden_DirtPatch), "UpdateVisualPlant"); _ = Method(typeof(Garden_DirtPatch), "UpdateDirt"); _ = Method(typeof(Garden_DirtPatch), "PlantDie");
            Field(typeof(HomeGarage), "airCompressor", typeof(AirCompressor));
            Field(typeof(HomeGarage), "DaysSinceBuilt", typeof(int)); Field(typeof(HomeGarage), "animator", typeof(Animator));
            foreach (string name in new[] { "Stage1", "Stage2", "FinalGarage" }) Field(typeof(HomeGarage), name, typeof(GameObject));
            Field(typeof(Speedway_Garage), "DoorLocation", typeof(float)); Field(typeof(Speedway_Garage), "TargetLocation", typeof(float));
            foreach (string name in new[] { "GarageDoor", "GarageDoor_OpenLocation", "GarageDoor_ClosedLocation" }) Field(typeof(Speedway_Garage), name, typeof(Transform));
            Field(typeof(AirCompressor), "SOUND_AirCompressor", typeof(SOUND_AirCompressor));
            Field(typeof(AirCompressor), "NeedElectricity", typeof(bool));
            Field(typeof(Vehicle_PartsManager), "carPartSockets", typeof(System.Collections.Generic.List<Vehicle_PartSocket>));
            foreach (string name in new[] { "OlTruck_Axle_Front", "OlTruck_Axle_Rear", "OlTruck_Driveshaft_Front",
                "OlTruck_Driveshaft_Rear", "OlTruck_TransfertCase_FrontConnection", "OlTruck_TransfertCase_RearConnection" })
                Field(typeof(Vehicle_PartsManager), name, typeof(Transform));
            foreach (string name in new[] { "Tires1", "Tires2", "Tires3", "Tires4", "Tires5", "Tires6" })
                Field(typeof(Part_Driveline_Wheel), name, typeof(GameObject));
            Field(typeof(BoltHoleDetails), "isBoltInstalled", typeof(bool)); Field(typeof(BoltHoleDetails), "BoltedPercent", typeof(float));
            _ = Method(typeof(BoltHoleDetails), nameof(BoltHoleDetails.Detach));
            Field(typeof(BoltHoleDetails), "generated_BoltHole", typeof(Generated_BoltHole));
            Field(typeof(Generated_BoltHole), "hardware_Bolt", typeof(Hardware_Bolt));
            foreach (string name in new[] { "isOpen", "isLocked", "isHorizontal", "isCottage", "isInitialized" }) Field(typeof(Furniture_Door), name, typeof(bool));
            Field(typeof(Furniture_Door), "OriginalYRotation", typeof(float)); Field(typeof(Furniture_Door), "OriginalZRotation", typeof(float));
            Field(typeof(Furniture_DoorHandle), "furniture_Door", typeof(Furniture_Door));
            Field(typeof(Tool_MagneticTray), "BoltsInTray", typeof(Hardware_Bolt[]));
            Field(typeof(Tool_MagneticTray), "BoltLocation0", typeof(Transform));
            Field(typeof(Hardware_Bolt), "isBoltInMagneticTray", typeof(Tool_MagneticTray));
            Field(typeof(Hardware_Bolt), "LastTimePickFromMagneticTray", typeof(float));
            Field(typeof(Vehicle_Chainsaw_Holder), "chainsaw", typeof(Tool_Chainsaw));
            Field(typeof(Vehicle_Chainsaw_Holder), "Chainsaw_HoldLocation", typeof(Transform));
            Field(typeof(Vehicle_Chainsaw_Holder), "saveLoaded", typeof(bool));
            Field(typeof(Tool_Chainsaw), "LastTimePickFromHolder", typeof(float));
            Field(typeof(Tool_AdjustableSpanner), "Is_HugeGoldEdition", typeof(bool));
            Field(typeof(BrotherHouse), "GarageStartedPalette", typeof(GameObject));
            Field(typeof(HardwareStore), "Item_DirtShovel", typeof(ItemForSale_Display)); Field(typeof(HardwareStore), "Item_WateringCan", typeof(ItemForSale_Display));
            Field(typeof(HardwareStore), "Trailer_WoodSplitter", typeof(VehicleForSale_Display)); Field(typeof(TwentyFourSevenStore), "Vehicle_OlTruck", typeof(VehicleForSale_Display)); Field(typeof(TwentyFourSevenStore), "Vehicle_SmollATV", typeof(VehicleForSale_Display));
            _ = Replication.SalePresentation.HealthFields();
            foreach (var slide in Replication.SalePresentation.HealthTargets())
                if (((MethodInfo)slide).ReturnType != typeof(System.Collections.IEnumerator)) throw new InvalidOperationException("Unsupported native method: " + slide.DeclaringType!.Name + "." + slide.Name);
            Field(typeof(Scrapyard), "Collider_Locked", typeof(GameObject)); Field(typeof(Scrapyard), "Collider_Normal", typeof(GameObject));
            Field(typeof(Scrapyard), "DustOnBuy", typeof(GameObject)); Field(typeof(Scrapyard), "DoorHandlePin", typeof(GameObject));
            Field(typeof(Scrapyard), "FenceDoorLeft", typeof(Transform)); Field(typeof(Scrapyard), "FenceDoorRight", typeof(Transform));
            foreach (string name in new[] { "Left_OriginalRotation", "Right_OriginalRotation", "Left_EndRotation", "Right_EndRotation" }) Field(typeof(Scrapyard), name, typeof(Quaternion));
            Field(typeof(Store_PoliceInterceptor), "ActiveTickets", typeof(List<PoliceTicketData>));
            if (AccessTools.Method(typeof(Furniture_Door), "Start", Type.EmptyTypes)?.ReturnType != typeof(void)) throw new InvalidOperationException("Unsupported native method: Furniture_Door.Start");
            foreach (string name in new[] { "OpenAnimation", "OpenHorizontalAnimation" })
                if (AccessTools.Method(typeof(Furniture_Door), name, Type.EmptyTypes)?.ReturnType != typeof(System.Collections.IEnumerator)) throw new InvalidOperationException("Unsupported native method: Furniture_Door." + name);
            // SharedDollies calls these by reflection.
            if (AccessTools.Method(typeof(Vehicle_Trailer_Dolly), "Detect", Type.EmptyTypes)?.ReturnType != typeof(void)) throw new InvalidOperationException("Unsupported native method: Vehicle_Trailer_Dolly.Detect");
            if (AccessTools.Method(typeof(Vehicle_Trailer_Dolly), "OpenCloseRamp", Type.EmptyTypes)?.ReturnType != typeof(System.Collections.IEnumerator)) throw new InvalidOperationException("Unsupported native method: Vehicle_Trailer_Dolly.OpenCloseRamp");
        }
        private static void Field(Type owner, string name, Type expected, bool isStatic = false)
        {
            var field = AccessTools.Field(owner, name);
            if (field == null || field.IsStatic != isStatic || field.FieldType != expected) throw new InvalidOperationException("Unsupported native field: " + owner.FullName + "." + name);
        }
        internal static void VerifyInstalled(string? directory)
        {
            var required = Required().Cast<MethodBase>().Distinct().ToArray();
            foreach (var target in required)
                if (Harmony.GetPatchInfo(target)?.Owners.Contains(MonMPPlugin.PluginId) != true)
                    throw new InvalidOperationException("Critical Harmony patch missing: " + target.DeclaringType?.FullName + "." + target.Name);
            var actual = Harmony.GetAllPatchedMethods().Where(m =>
            {
                var info = Harmony.GetPatchInfo(m);
                return info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                    .Any(p => p.owner == MonMPPlugin.PluginId);
            }).ToArray();
            var missing = actual.FirstOrDefault(m => !required.Contains(m));
            if (missing != null) throw new InvalidOperationException("A gameplay patch has no explicit health check: " + missing.DeclaringType?.FullName + "." + missing.Name);
#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(directory!, "patch-health.txt"), new[] { "MONMP_PATCH_HEALTH_PASS", "Verified gameplay targets: " + required.Length }
                .Concat(required.Select(m => m.DeclaringType!.FullName + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName)) + ")")));
#endif
        }
#if MONMP_DEVELOPMENT
        internal static void VerifyManualPatchInstalled(Harmony patch, MethodBase target)
        {
            // Only the isolated fixture patches this generated native coroutine step. Resolve it
            // from the exact private factory, then check its bool MoveNext() signature explicitly.
            if (patch.Id == "MonMP.LocalTest.FryerCook" && target.Name == "MoveNext")
            {
                var factory = Method(typeof(Furniture_Fryer), "BuildPoutine");
                var expected = AccessTools.EnumeratorMoveNext(factory);
                if (expected == null || !expected.Equals(target) ||
                    !Method(expected.DeclaringType!, "MoveNext").Equals(target))
                    throw new InvalidOperationException("Unsupported native fryer cook coroutine signature.");
            }
            if (patch.Id == "MonMP.LocalTest.NitrousRefill" && target.Name == "MoveNext")
            {
                var factory = Method(typeof(NitrousRefillStation), "RefillBottle", typeof(Part_NitrousBottle));
                var expected = AccessTools.EnumeratorMoveNext(factory);
                if (expected == null || !expected.Equals(target) ||
                    !Method(expected.DeclaringType!, "MoveNext").Equals(target))
                    throw new InvalidOperationException("Unsupported native nitrous refill coroutine signature.");
            }
            if (patch.Id == "MonMP.LocalTest.NitrousRefill" && target.Name == nameof(Cash_Manager.Event_Spent) &&
                !Method(typeof(Cash_Manager), nameof(Cash_Manager.Event_Spent), typeof(float), typeof(bool)).Equals(target))
                throw new InvalidOperationException("Unsupported native nitrous refill wallet signature.");
            if (patch.Id == "MonMP.LocalTest.NitrousRefill" && target.Name == "OnTriggerEnter" &&
                !Method(typeof(NitrousRefillStation), "OnTriggerEnter", typeof(Collider)).Equals(target))
                throw new InvalidOperationException("Unsupported native nitrous refill trigger signature.");
            if (patch.Id == "MonMP.LocalTest.TravelingVan")
            {
                var method = AccessTools.Method(typeof(Store_TravelingVan), target.Name, Type.EmptyTypes);
                if ((target.Name != "RaiseVan" && target.Name != "LowerVan") || method == null || method.IsStatic ||
                    method.ReturnType != typeof(System.Collections.IEnumerator) || !method.Equals(target))
                    throw new InvalidOperationException("Unsupported native traveling van motion signature.");
            }
            if (patch.Id == "MonMP.LocalTest.PurchasedTruckDoors")
            {
                if (target.DeclaringType == typeof(SharedVehicleDoors) && target.Name == "Apply")
                {
                    var binding = typeof(SharedVehicleDoors).GetNestedType("Binding", BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("Purchased truck diagnostic binding missing.");
                    if (!Method(typeof(SharedVehicleDoors), "Apply", binding, typeof(MonMP.Core.World.VehicleDoorState), typeof(bool)).Equals(target) ||
                        !(target is MethodInfo apply) || apply.ReturnType != typeof(void))
                        throw new InvalidOperationException("Unsupported purchased truck diagnostic apply signature.");
                    Field(binding, "Door", typeof(Vehicle_Door)); Field(binding, "State", typeof(MonMP.Core.World.VehicleDoorState));
                }
                else
                {
                    bool valid = target.DeclaringType == typeof(Vehicle_Door) && target.Name == "Start" ||
                        target.DeclaringType == typeof(SOUND_Vehicle_Door) && (target.Name == "Open" || target.Name == "Close") ||
                        target.DeclaringType == typeof(SOUND_Vehicle_Hood) && (target.Name == "Release" || target.Name == "Open" || target.Name == "Close");
                    if (!valid || !Method(target.DeclaringType!, target.Name).Equals(target))
                        throw new InvalidOperationException("Unsupported native purchased truck door/hood signature.");
                }
            }
            if (Harmony.GetPatchInfo(target)?.Owners.Contains(patch.Id) != true)
                throw new InvalidOperationException("Local test Harmony patch missing: " + target.DeclaringType?.FullName + "." + target.Name);
        }

#endif
    }
}
