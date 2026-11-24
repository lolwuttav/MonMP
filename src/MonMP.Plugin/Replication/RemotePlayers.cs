using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.UI;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed class RemotePlayers : IDisposable
    {
#if MONMP_PERFORMANCE_CAPTURE
        internal void AppendCaptureAvatarRoots(List<GameObject> roots)
        {
            foreach (var avatar in avatars.Values)
            {
                if (avatar.Mannequin != null) roots.Add(avatar.Mannequin);
                if (avatar.Body?.CaptureRenderingRoot != null) roots.Add(avatar.Body.CaptureRenderingRoot);
                avatar.Props?.AppendCaptureRenderingRoots(roots);
            }
        }
#endif
        private sealed class Avatar
        {
            public GameObject Root = null!;
            public Transform Torso = null!;
            public Transform TorsoPivot = null!;
            public Transform Pelvis = null!;
            public Transform Head = null!;
            public Transform Neck = null!;
            public Transform HeldAnchor = null!;
            public Transform LeftArm = null!;
            public Transform LeftElbow = null!;
            public Transform RightArm = null!;
            public Transform RightElbow = null!;
            public Transform LeftHip = null!;
            public Transform LeftKnee = null!;
            public Transform RightHip = null!;
            public Transform RightKnee = null!;
            public Material Material = null!;
            public Material ShirtMaterial = null!;
            public Light Flashlight = null!;
            public byte Shirt;
            public byte Model, BodyModel = byte.MaxValue;
            public PlayerAppearance Appearance, BodyAppearance;
            public float ModelChangedAt, NextModelBuild;
#if MONMP_DEVELOPMENT
            public int TestModelBuilds;
#endif
            public string Name = "";
            public Vector3 From, To, LastRootPosition;
            public readonly RemoteGroundMotion GroundMotion = new RemoteGroundMotion();
            public bool GroundPrediction;
            // Map markers retain the last rendered native feet position during spectating. The
            // spectator head follows the camera, while this remains the parked world anchor.
            public Vector3 MapPosition;
            // Riding a shared carrier: From/To in its space, placed on this client's own copy (#249).
            public Transform? Carrier, LastCarrier;
            public Vector3 LastCarrierLocal;
            public Vector3 FromLocal, ToLocal;
            public Transform? PresentationSeat;
            public Vector3 PresentationSeatPosition;
            public Quaternion PresentationSeatRotation;
            public int PresentationSeatFrame = -1;
            public Quaternion FromRotation, ToRotation;
            public float ReceivedAt, FromPitch, Pitch;
            public float FromCrouch, ToCrouch, CrouchAmount;
            public float GaitPhase, WalkAmount;
            public byte State;
            public bool HasPose;
            public PlayerPose LastPose;
            // The stock NPC body once the scene provides one; the primitive mannequin until then.
            public GameObject Mannequin = null!;
            public AvatarShadowCasting? MannequinShadowCasting;
            public AvatarHeadOnlyPresentation? HeadOnlyPresentation;
            public StockAvatarBody? Body;
            // Native feedback belongs to the local player in every camera mode. Visibility may
            // change between the ordinary and shadow passes without changing that ownership.
            public bool IsLocalSelf;
            public bool LocalShadowOnly;
            public bool Spectating;
            public Vector3 SpectatorHeadFrom, SpectatorHeadTo, SpectatorHead;
            public float NextBodyAttempt;
            public string BodyStatus = "mannequin (pending)";
            // Emotes: Sorry's held hand, the one-shot sequence and the gesture/voice timers.
            public AudioSource Voice = null!;
            public bool Sorry;
            public float SorryAmount;
            public PlayerEmoteSequenceGate EmoteSequence;
            public float SlurAt = -100f, SpitAt = -100f, SpitDropAt = -1f;
            public PlayerEmoteKind Gesture;
            public float GestureAt = -100f;
            private string voiceText = "";
            public string VoiceText
            {
                get => voiceText;
                set
                {
                    if (voiceText == value) return;
                    voiceText = value;
                    QuotedVoiceText = value.Length == 0 ? "" : "\"" + value + "\"";
                }
            }
            public string QuotedVoiceText { get; private set; } = "";
            public string EmoteLog = "";
            public bool IsChatCaption;
            public float VoiceUntil;
            // Standing still, the body keeps its facing while the head follows the camera.
            public float BodyYaw, LastCameraYaw;
            public bool HasCameraYaw;
            // Held hand states (smoothed 0..1), resting, and the vomit/poke/pet gesture timers.
            public PlayerHands Hands;
            public bool IndependentAim;
            public PlayerRest Rest;
            public Vector3 SleepAnchor;
            public bool SleepAnchorKnown;
            public Transform? SleepAnchorFrame;
            public Vector3 SleepAnchorLocal;
            // Drunk (0..1 target and smoothed) and hungover: the idle sway and slump.
            public float DrunkTarget, DrunkAmount, HangoverAmount;
            public bool Hungover;
            public bool FlashlightOn;
            public float MapAmount, ConsoleAmount, FlashAmount, JointAmount, SmokeAmount;
            public float VomitAt = -100f, VomitSeconds = 3f, InteractAt = -100f, PetAt = -100f;
            public RemoteHandProps? Props;
            // Native footsteps from a source at the feet; the smoke inhale has its own source.
            public AudioSource Feet = null!, Smoke = null!;
            public float StepDistance;
            // Far away the body is posed a few times a second instead of every frame.
            public float FarElapsed;
            // The interpolated camera look, kept apart from the root, which a seat, chair or bed turns.
            public Quaternion CameraRotation = Quaternion.identity;
            // A furniture chair faces the way the native seat turned the camera on sitting down.
            public float ChairYaw;
            // Off the ground (0..1) while walking.
            public float AirAmount;
            // The bed a sleeper lies in: along it towards the foot end, curled up on a back seat.
            public float BedYaw, NextBedCheck;
            public bool HasBed, BedCurl;
            public Furniture_Bed? Bed;
            public float BedWidth;
            public bool BedFloor;
            public float BedSideways;
            // A native bar stool's cushion top, found when the player sits down.
            public Vector3? StoolSurface; public bool StoolChecked, OnCreeper;
            // The held tool's own particles and sound while its user works it.
            public InteractableItems? ToolItem;
            public ParticleSystem[] ToolParticles = Array.Empty<ParticleSystem>();
            public bool ToolSoundStarted;
            public AudioSource? ToolSpraySound;
            public RemoteHeldVisual? HeldVisual;
            public RemoteEquippedPose? EquippedPose;
        }

        private sealed class SpitDrop
        {
            public GameObject Drop = null!;
            public uint Owner;
            public Vector3 Velocity;
            public float Until;
            public bool Landed;
        }

        private readonly List<SpitDrop> spitDrops = new List<SpitDrop>();
        private static readonly System.Reflection.FieldInfo? OneShotSourceField = AccessTools.Field(typeof(SOUND_General), "OneShotSource");
        private SOUND_Manager? sounds;
        private GameAssets? assets;

#if MONMP_DEVELOPMENT
        internal int TestSpitDropsSpawned { get; private set; }


        internal int TestSpitDropsLanded { get; private set; }
#endif


        private readonly Dictionary<uint, Avatar> avatars = new Dictionary<uint, Avatar>();
        private GUIStyle? nameStyle, captionStyle;
        private const float InterpolationSeconds = 0.1f;
        // A native walk is about 1.15 m/s: at that speed the stride is a full one (it was 2.5, so a
        // walker blended a 45% step into the standing pose and slid).
        private const float WalkSpeedForFullGait = 1.1f;
        private const float BodyRetrySeconds = 5f;
        // Head bone of a seated body: just below the seated player's camera (1.6 m above the walker root).
        private const float SeatedHeadHeight = 1.52f;
        private static readonly Vector3 MouthOffset = new Vector3(0, -0.08f, 0.12f);
        // Native timings: SpitDown spawns the drop 1.85 s after the spit sound; voice text shows 2 s.
        private const float SpitDropDelay = 1.85f, SpitDropLife = 5f, VoiceTextSeconds = 2f;
        private const float StandingBodySlack = RemoteBodyFacing.StandingSlack;
        // Beyond these distances the feet are not placed on the ground, footsteps are not played and
        // the body is posed at five frames a second.
        private const float FeetDistance = 45f, FootstepDistance = 30f, ThrottleDistance = 120f, ThrottleSeconds = 0.2f;
        private const float FootstepGain = 1.2f;
        private static readonly Vector3 PushHands = new Vector3(0, 0.85f, 0.35f);
        private CMF.AudioControl? walkerAudio;
        private int footstepMask = -1;
        private static Wheelbarrow[] barrows = Array.Empty<Wheelbarrow>();
        private static Furniture_Bed[] beds = Array.Empty<Furniture_Bed>();
        private static float nextBedScan;
        private static readonly Dictionary<Furniture_Bed, (Vector3 Axis, Vector3 Center, float Width)> bedShapes = new Dictionary<Furniture_Bed, (Vector3, Vector3, float)>();
        private static float nextBarrowScan;
        private static readonly Dictionary<Wheelbarrow, (float Reach, float Half)> barrowShapes = new Dictionary<Wheelbarrow, (float, float)>();

#if MONMP_DEVELOPMENT
        internal int TestFootsteps { get; private set; }
#endif


        public int VisibleCount
        {
            get { int count = 0; foreach (var avatar in avatars.Values) if (avatar.HasPose && avatar.Root.activeInHierarchy) count++; return count; }
        }


#if MONMP_DEVELOPMENT
        internal bool TestPoseNear(uint id, Vector3 position, float radius) => avatars.TryGetValue(id, out var avatar) &&
            avatar.HasPose && avatar.Root.activeInHierarchy && Time.unscaledTime - avatar.ReceivedAt <= 2f &&
            Vector3.Distance(avatar.Root.transform.position, position) <= radius;
#endif


        public void Add(PeerInfo peer)
        {
            Remove(peer.Id);
            var root = new GameObject("MonMP Remote " + peer.Id);
            UnityEngine.Object.DontDestroyOnLoad(root);
            var shader = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
            if (shader == null) { UnityEngine.Object.Destroy(root); throw new InvalidOperationException("No avatar shader available."); }
            var material = new Material(shader)
            {
                color = new Color(((peer.ColorRgb >> 16) & 255) / 255f, ((peer.ColorRgb >> 8) & 255) / 255f, (peer.ColorRgb & 255) / 255f)
            };
            var shirtMaterial = new Material(shader) { color = ShirtColor(0) };
            var mannequin = new GameObject("Mannequin");
            mannequin.transform.SetParent(root.transform, false);

            try
            {
                var pelvis = Part(mannequin.transform, "Pelvis", PrimitiveType.Capsule,
                    new Vector3(0, 0.86f, 0), new Vector3(0.34f, 0.17f, 0.25f), material);
                var torsoPivot = Pivot(mannequin.transform, "TorsoPivot", new Vector3(0, 1f, 0));
                var torso = Part(torsoPivot, "Torso", PrimitiveType.Capsule,
                    new Vector3(0, 0.2f, 0), new Vector3(0.46f, 0.37f, 0.29f), shirtMaterial);
                Part(mannequin.transform, "Neck", PrimitiveType.Capsule,
                    new Vector3(0, 1.52f, 0), new Vector3(0.11f, 0.12f, 0.11f), material);
                var head = Pivot(mannequin.transform, "Head", new Vector3(0, 1.68f, 0));
                Part(head, "HeadMesh", PrimitiveType.Sphere, Vector3.zero,
                    new Vector3(0.36f, 0.4f, 0.34f), material);

                var leftArm = Pivot(torsoPivot, "LeftArm", new Vector3(-0.3f, 0.39f, 0));
                Part(leftArm, "LeftUpperArm", PrimitiveType.Capsule,
                    new Vector3(0, -0.19f, 0.015f), new Vector3(0.19f, 0.2f, 0.19f), material);
                var leftElbow = Pivot(leftArm, "LeftElbow", new Vector3(0, -0.38f, 0.02f));
                Part(leftElbow, "LeftForearm", PrimitiveType.Capsule,
                    new Vector3(0, -0.17f, 0.015f), new Vector3(0.17f, 0.18f, 0.17f), material);

                var rightArm = Pivot(torsoPivot, "RightArm", new Vector3(0.3f, 0.39f, 0));
                Part(rightArm, "RightUpperArm", PrimitiveType.Capsule,
                    new Vector3(0, -0.19f, 0.015f), new Vector3(0.19f, 0.2f, 0.19f), material);
                var rightElbow = Pivot(rightArm, "RightElbow", new Vector3(0, -0.38f, 0.02f));
                Part(rightElbow, "RightForearm", PrimitiveType.Capsule,
                    new Vector3(0, -0.17f, 0.015f), new Vector3(0.17f, 0.18f, 0.17f), material);
                var rightHand = Pivot(rightElbow, "RightHand", new Vector3(0, -0.34f, 0));
                Part(rightHand, "RightHandMesh", PrimitiveType.Sphere, Vector3.zero,
                    new Vector3(0.13f, 0.16f, 0.12f), material);

                var leftHip = Pivot(mannequin.transform, "LeftHip", new Vector3(-0.13f, 0.82f, 0));
                Part(leftHip, "LeftThigh", PrimitiveType.Capsule,
                    new Vector3(0, -0.23f, 0), new Vector3(0.23f, 0.24f, 0.23f), material);
                var leftKnee = Pivot(leftHip, "LeftKnee", new Vector3(0, -0.46f, 0));
                Part(leftKnee, "LeftShin", PrimitiveType.Capsule,
                    new Vector3(0, -0.21f, 0), new Vector3(0.18f, 0.22f, 0.18f), material);

                var rightHip = Pivot(mannequin.transform, "RightHip", new Vector3(0.13f, 0.82f, 0));
                Part(rightHip, "RightThigh", PrimitiveType.Capsule,
                    new Vector3(0, -0.23f, 0), new Vector3(0.23f, 0.24f, 0.23f), material);
                var rightKnee = Pivot(rightHip, "RightKnee", new Vector3(0, -0.46f, 0));
                Part(rightKnee, "RightShin", PrimitiveType.Capsule,
                    new Vector3(0, -0.21f, 0), new Vector3(0.18f, 0.22f, 0.18f), material);

                var anchor = new GameObject("HeldItemAnchor").transform;
                anchor.SetParent(root.transform, false);
                anchor.localPosition = new Vector3(0.35f, 1.1f, 0.5f);

                // The player's flashlight: a head-mounted spot that follows the look pitch.
                var flashlight = new GameObject("Flashlight").AddComponent<Light>();
                flashlight.transform.SetParent(root.transform, false);
                flashlight.type = LightType.Spot; flashlight.range = 30f; flashlight.spotAngle = 60f;
                flashlight.intensity = 1.6f; flashlight.color = new Color(1f, 0.96f, 0.85f);
                flashlight.shadows = LightShadows.None; flashlight.enabled = false;

                var voice = new GameObject("Voice").AddComponent<AudioSource>();
                voice.transform.SetParent(root.transform, false);
                voice.transform.localPosition = new Vector3(0, 1.6f, 0);
                voice.playOnAwake = false; voice.spatialBlend = 1f; voice.dopplerLevel = 0f;
                voice.rolloffMode = AudioRolloffMode.Linear; voice.minDistance = 2f; voice.maxDistance = 35f;
                var feet = new GameObject("Feet").AddComponent<AudioSource>();
                feet.transform.SetParent(root.transform, false);
                feet.playOnAwake = false; feet.spatialBlend = 1f; feet.dopplerLevel = 0f;
                feet.rolloffMode = AudioRolloffMode.Linear; feet.minDistance = 1.5f; feet.maxDistance = 25f;
                var smoke = new GameObject("Smoke").AddComponent<AudioSource>();
                smoke.transform.SetParent(voice.transform, false);
                smoke.playOnAwake = false; smoke.spatialBlend = 1f; smoke.dopplerLevel = 0f; smoke.loop = false;
                smoke.rolloffMode = AudioRolloffMode.Linear; smoke.minDistance = 1.5f; smoke.maxDistance = 20f;

                root.SetActive(false);
                var avatar = new Avatar
                {
                    Root = root, Torso = torso, TorsoPivot = torsoPivot, Pelvis = pelvis, Head = head,
                    Neck = mannequin.transform.Find("Neck")!, HeldAnchor = anchor, Mannequin = mannequin,
                    LeftArm = leftArm, LeftElbow = leftElbow, RightArm = rightArm, RightElbow = rightElbow,
                    LeftHip = leftHip, LeftKnee = leftKnee, RightHip = rightHip, RightKnee = rightKnee,
                    Material = material, ShirtMaterial = shirtMaterial, Flashlight = flashlight, Voice = voice, Feet = feet, Smoke = smoke, Shirt = 0, Name = peer.DisplayName
                };
                avatar.HeadOnlyPresentation = CreateHeadOnlyPresentation(avatar);
                avatars.Add(peer.Id, avatar);
            }
            catch
            {
                UnityEngine.Object.Destroy(root);
                UnityEngine.Object.Destroy(material);
                UnityEngine.Object.Destroy(shirtMaterial);
                throw;
            }
        }

        internal static Color AppearanceColor(PlayerAppearance appearance, byte shirt) => appearance.CustomColor
            ? new Color32((byte)(appearance.Rgb >> 16), (byte)(appearance.Rgb >> 8), (byte)appearance.Rgb, 255) : ShirtColor(shirt);
        internal static Color ShirtColor(byte shirt)
        {
            switch (shirt)
            {
                case 1: return new Color(0.09803923f, 0.4170849f, 0.8862745f); // Blue
                case 2: return new Color(0.09803923f, 0.8862745f, 0.4617344f); // Green
                case 3: return new Color(0.6320754f, 0.6320754f, 0.6320754f); // Gray
                case 4: return new Color(1f, 0.5571218f, 0.08176094f); // Orange
                case 5: return new Color(1f, 0.5345911f, 0.9706057f); // Pink
                case 6: return new Color(0.9756485f, 0.9811321f, 0.4319449f); // Yellow
                case 7: return new Color(0.47f, 0.31f, 0.66f); // Purple
                case 8: return Color.white;
                default: return new Color(0.8862745f, 0.09803923f, 0.2392157f); // Red
            }
        }

        private static Transform Pivot(Transform parent, string name, Vector3 position)
        {
            var pivot = new GameObject(name).transform;
            pivot.SetParent(parent, false);
            pivot.localPosition = position;
            return pivot;
        }

        private static AvatarHeadOnlyPresentation CreateHeadOnlyPresentation(Avatar avatar)
        {
            var renderers = avatar.Root.GetComponentsInChildren<Renderer>(true);
            var headRenderers = new bool[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;
                var transform = renderer.transform;
                headRenderers[i] = IsUnder(transform, avatar.Head) ||
                    avatar.Body?.IsHeadRenderer(renderer) == true ||
                    HasHeadSurfaceName(transform, avatar.Root.transform);
            }
            return new AvatarHeadOnlyPresentation(renderers, headRenderers);
        }

        private static bool IsUnder(Transform transform, Transform? parent) =>
            parent != null && (transform == parent || transform.IsChildOf(parent));

        private static bool HasHeadSurfaceName(Transform transform, Transform root)
        {
            for (var current = transform; current != null && current != root; current = current.parent)
                if (current.name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    current.name.IndexOf("hair", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static void RefreshHeadOnlyPresentation(Avatar avatar)
        {
            avatar.HeadOnlyPresentation?.Restore();
            avatar.HeadOnlyPresentation = CreateHeadOnlyPresentation(avatar);
            if (avatar.Spectating) avatar.HeadOnlyPresentation.SetHeadOnly(true);
        }

        private static void SetMannequinFallback(Avatar avatar) =>
            avatar.Mannequin.SetActive(avatar.Body == null || avatar.Spectating && !avatar.Body.HasSpectatorHeadMesh);

        private static Vector3 HeadPosition(Avatar avatar) => avatar.Spectating && avatar.Body?.HasSpectatorHeadMesh != true
            ? avatar.Head.position : avatar.Body != null ? avatar.Body.Head.position : avatar.Head.position;

        private static Transform Part(Transform parent, string name, PrimitiveType primitive, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(primitive);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            // Disable immediately; Destroy is deferred until the end of the frame.
            var collider = part.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                UnityEngine.Object.Destroy(collider);
            }
            part.GetComponent<Renderer>().sharedMaterial = material;
            return part.transform;
        }

        // The avatar of a peer with a fresh pose, for native AI that follows a player (Leo).
        internal Transform? AvatarRoot(uint id) => avatars.TryGetValue(id, out var avatar) && avatar.HasPose &&
            avatar.Root.activeInHierarchy && Time.unscaledTime - avatar.ReceivedAt <= 5f ? avatar.Root.transform : null;

        public void Apply(uint peer, PlayerPose pose) => Apply(peer, pose, null);

        public void ApplyTimed(uint peer, PlayerPose pose, double sampledAt) => Apply(peer, pose, sampledAt);

        private void Apply(uint peer, PlayerPose pose, double? sampledAt)
        {
            if (!avatars.TryGetValue(peer, out var avatar)) return;
            var destination = new Vector3(pose.X, pose.Y, pose.Z);
            bool firstPose = !avatar.HasPose;
            if (firstPose) avatar.MapPosition = SpectatorPresentationPolicy.MapAnchor(destination, avatar.MapPosition, pose.Spectating, false);
            var carrier = pose.Carrier != 0 ? MonMPPlugin.Instance?.Items?.Carriers?.Resolve(pose.Carrier) : null;
            if (carrier != null) destination = carrier.TransformPoint(new Vector3(pose.CarrierX, pose.CarrierY, pose.CarrierZ));
            var spectatorHeadDestination = new Vector3(pose.SpectatorHeadX, pose.SpectatorHeadY, pose.SpectatorHeadZ);
            var rotation = Quaternion.Euler(0, pose.Yaw, 0);
            bool predictGround = !pose.Spectating && pose.Rest == PlayerRest.None &&
                (pose.State == (byte)PlayerState.Walking || pose.State == (byte)PlayerState.InWheelbarrow);
            bool snap = SetMotionTarget(avatar, destination, carrier, new Vector3(pose.CarrierX, pose.CarrierY, pose.CarrierZ), predictGround, sampledAt);
            avatar.SpectatorHeadFrom = snap ? spectatorHeadDestination : avatar.SpectatorHead;
            avatar.SpectatorHeadTo = spectatorHeadDestination;
            avatar.FromRotation = snap ? rotation : avatar.CameraRotation;
            // Native chairs turn the camera to the seat on sitting down; the body keeps that facing.
            if (pose.State == (byte)PlayerState.InFurnitureChair && (snap || avatar.State != pose.State)) avatar.ChairYaw = pose.Yaw;
            avatar.FromPitch = snap ? pose.Pitch : Mathf.Lerp(avatar.FromPitch, avatar.Pitch, Mathf.Clamp01((Time.unscaledTime - avatar.ReceivedAt) / InterpolationSeconds));
            avatar.To = destination; avatar.ToRotation = rotation; avatar.Pitch = pose.Pitch; avatar.State = pose.State;
            if (!avatar.Appearance.SameGeometry(pose.Appearance)) avatar.ModelChangedAt = Time.unscaledTime;
            bool tintChanged = avatar.Appearance.CustomColor != pose.Appearance.CustomColor || avatar.Appearance.Rgb != pose.Appearance.Rgb;
            if (avatar.Appearance.NativeTexture != pose.Appearance.NativeTexture) avatar.Body?.SetNativeShirtTexture(pose.Appearance.NativeTexture);
            avatar.Model = pose.Model; avatar.Appearance = pose.Appearance;
            if (avatar.Shirt != pose.Shirt || tintChanged)
            {
                avatar.Shirt = pose.Shirt;
                avatar.ShirtMaterial.color = AppearanceColor(pose.Appearance, pose.Shirt);
                if (avatar.Body != null) avatar.Body.ShirtMaterial.color = AppearanceColor(pose.Appearance, pose.Shirt);
            }
            avatar.FlashlightOn = pose.Flashlight;
            bool wasSmoking = (avatar.Hands & PlayerHands.Smoking) != 0, smoking = (pose.Hands & PlayerHands.Smoking) != 0;
            if (!pose.Spectating && !avatar.IsLocalSelf && smoking != wasSmoking && avatar.HasPose) SmokeSound(avatar, smoking);
            avatar.Hands = pose.Hands;
            avatar.IndependentAim = pose.IndependentAim;
            avatar.Rest = pose.Rest;
            avatar.DrunkTarget = pose.Drunk / (float)PlayerPose.MaxDrunk; avatar.Hungover = pose.Hungover;
            bool crouched = pose.State == (byte)PlayerState.Walking && pose.Crouched;
            avatar.FromCrouch = snap ? (crouched ? 1f : 0f) : avatar.CrouchAmount;
            avatar.ToCrouch = crouched ? 1f : 0f;
            if (snap) { avatar.LastRootPosition = destination; avatar.WalkAmount = 0; }
            avatar.ReceivedAt = Time.unscaledTime;
            avatar.LastPose = pose;
            avatar.HasPose = true;
            avatar.Root.SetActive(true);

            if (avatar.Spectating != pose.Spectating)
            {
                avatar.Spectating = pose.Spectating;
                if (avatar.Spectating)
                {
                    avatar.Body?.SetRacingHelmet(false);
                    avatar.Body?.SetPresentationScale(1f);
                    avatar.Props?.HideZoom();
                    avatar.Body?.EnsureSpectatorHead();
                    avatar.Body?.BeginSpectatorHeadOnly();
                    SetMannequinFallback(avatar);
                    RefreshHeadOnlyPresentation(avatar);
                    SuppressSpectatorFeedback(peer, avatar);
                }
                else
                {
                    avatar.Body?.RestoreSpectatorHead();
                    avatar.HeadOnlyPresentation?.Restore();
                    avatar.Head.localPosition = new Vector3(0, Mathf.Lerp(1.68f, 1.05f, avatar.CrouchAmount), 0);
                    SetMannequinFallback(avatar);
                }
            }
            avatar.Flashlight.enabled = pose.Flashlight && pose.Rest == PlayerRest.None && !avatar.IsLocalSelf && !avatar.Spectating;

            avatar.Sorry = pose.Sorry && pose.State != (byte)PlayerState.InCreeper && pose.Rest == PlayerRest.None;
            // The first pose only sets the sequence baseline, so joining mid-emote does not replay it.
            if (avatar.EmoteSequence.Accept(pose.EmoteSequence))
            {
                if (PlayerEmotePolicy.IsCosmeticGesture(pose.Emote) &&
                    !PlayerEmotePolicy.CanPlayCosmeticGesture(pose.Emote, RemoteGesturePresentationPolicy.IsAvailable((PlayerState)pose.State), pose.Rest, pose.Sorry))
                    avatar.Gesture = PlayerEmoteKind.None;
                else if (!avatar.Spectating) PlayEmote(avatar, pose.Emote, pose.EmoteVariant, avatar.IsLocalSelf);
            }
        }

        internal void SetLocalAvatarView(uint id, LocalAvatarView view)
        {
            if (!avatars.TryGetValue(id, out var avatar) || view == LocalAvatarView.Disabled) return;
            // Set identity before the same-visibility early return: a newly created visible self
            // avatar already has LocalShadowOnly=false, but still must never replay native cues.
            avatar.IsLocalSelf = true;
            bool shadowOnly = view == LocalAvatarView.ShadowOnly;
            if (avatar.LocalShadowOnly == shadowOnly) return;
            avatar.LocalShadowOnly = shadowOnly;
            avatar.MannequinShadowCasting ??= new AvatarShadowCasting(avatar.Mannequin.GetComponentsInChildren<Renderer>(true));
            avatar.MannequinShadowCasting?.SetShadowOnly(shadowOnly);
            avatar.Body?.SetShadowOnly(shadowOnly);
            avatar.Flashlight.enabled = false;
            if (shadowOnly)
            {
                avatar.SpitDropAt = -1f;
                StopTool(avatar);
                avatar.HeldVisual?.Reset();
                avatar.Smoke.Stop();
                avatar.Feet.Stop();
                avatar.Voice.Stop();
                foreach (var drop in spitDrops)
                    if (drop.Drop != null) UnityEngine.Object.Destroy(drop.Drop);
                spitDrops.Clear();
            }
        }

        private void SuppressSpectatorFeedback(uint peer, Avatar avatar)
        {
            avatar.SpitDropAt = -1f;
            avatar.SpitAt = avatar.SlurAt = -100f;
            avatar.VomitAt = avatar.InteractAt = avatar.PetAt = -100f;
            avatar.Sorry = false; avatar.SorryAmount = 0f; avatar.Gesture = PlayerEmoteKind.None;
            avatar.MapAmount = avatar.ConsoleAmount = avatar.FlashAmount = avatar.JointAmount = avatar.SmokeAmount = 0f;
            avatar.VoiceText = ""; avatar.VoiceUntil = 0f; avatar.IsChatCaption = false;
            StopTool(avatar);
            avatar.HeldVisual?.Reset();
            avatar.Flashlight.enabled = false;
            avatar.Smoke.Stop(); avatar.Feet.Stop(); avatar.Voice.Stop();
            foreach (var particles in avatar.Root.GetComponentsInChildren<ParticleSystem>(true))
                if (particles != null) particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            for (int i = spitDrops.Count - 1; i >= 0; i--)
            {
                var drop = spitDrops[i];
                if (drop.Owner != peer) continue;
                if (drop.Drop != null) UnityEngine.Object.Destroy(drop.Drop);
                spitDrops.RemoveAt(i);
            }
        }

        private static bool SetMotionTarget(Avatar avatar, Vector3 destination, Transform? carrier, Vector3 local, bool predictGround, double? sampledAt)
        {
            bool sameCarrier = avatar.HasPose && carrier != null && avatar.Carrier == carrier;
            // A new carrier establishes a new coordinate frame. Start at the accepted local
            // spot instead of interpolating old world feet outside a moving carrier.
            bool snap = !avatar.HasPose || carrier != null && !sameCarrier ||
                (sameCarrier ? (local - avatar.ToLocal).sqrMagnitude : (destination - avatar.To).sqrMagnitude) > 100;
            avatar.From = snap ? destination : avatar.Root.transform.position;
            if (carrier != null)
            {
                // The carrier may already have moved since the avatar's last frame. Retain the
                // rider's interpolated local position instead of converting that stale world root.
                avatar.FromLocal = sameCarrier && !snap
                    ? Vector3.Lerp(avatar.FromLocal, avatar.ToLocal, Mathf.Clamp01((Time.unscaledTime - avatar.ReceivedAt) / InterpolationSeconds))
                    : carrier.InverseTransformPoint(avatar.From);
                avatar.ToLocal = local;
                avatar.GroundMotion.Clear();
            }
            else
            {
                bool resetGround = snap || avatar.Carrier != null || !predictGround || !avatar.GroundPrediction;
                if (sampledAt.HasValue) avatar.GroundMotion.AcceptTimed(destination, Time.unscaledTime, sampledAt.Value, resetGround);
                else avatar.GroundMotion.Accept(destination, Time.unscaledTime, resetGround);
            }
            avatar.GroundPrediction = predictGround;
            avatar.Carrier = carrier;
            return snap;
        }

        private static bool CanUseMovementGait(Transform? carrier, Vector3 movement)
        {
            // Ground motion resets its speed at teleports. Legitimate fast walking can
            // cross a metre in one rendered frame; only carrier gait uses that guard.
            return carrier == null || movement.sqrMagnitude < 1f;
        }

        private SOUND_Manager? Sounds() => sounds != null ? sounds : sounds = UnityEngine.Object.FindObjectOfType<SOUND_Manager>();

        internal void PointAt(uint peer)
        {
            if (avatars.TryGetValue(peer, out var avatar)) { avatar.Gesture = PlayerEmoteKind.Point; avatar.GestureAt = Time.unscaledTime; }
        }

        private void PlayEmote(Avatar avatar, PlayerEmoteKind kind, byte variant, bool suppressFeedback)
        {
            if (kind == PlayerEmoteKind.None) return;
            avatar.EmoteLog = (avatar.EmoteLog.Length > 200 ? "" : avatar.EmoteLog) + kind + ";";
            var manager = Sounds();
            float now = Time.unscaledTime;
            avatar.Gesture = PlayerEmoteKind.None;
            switch (kind)
            {
                case PlayerEmoteKind.Oups:
                    if (!suppressFeedback) PlayVoice(avatar, manager, manager?.PlayerVoice_Oups, variant, "PlayerVoice_Sorry");
                    break;
                case PlayerEmoteKind.Sacre:
                    if (!suppressFeedback) PlayVoice(avatar, manager, manager?.PlayerVoice_Sacre, variant, "PlayerVoice_FrenchSlur");
                    avatar.SlurAt = now;
                    break;
                case PlayerEmoteKind.Spit:
                    if (!suppressFeedback)
                    {
                        RouteToGameMixer(avatar.Voice, manager);
                        // SOUND_General.Play_OneShot uses 0.2 for the native player's spit.
                        if (manager?.Player_Spit != null) avatar.Voice.PlayOneShot(manager.Player_Spit, 0.2f);
                    }
                    avatar.SpitAt = now; avatar.SpitDropAt = suppressFeedback ? -1f : now + SpitDropDelay;
                    break;
                case PlayerEmoteKind.Vomit:
                    // Native: the vomit sound, and the particles for the sound's length less half a second.
                    var clip = manager?.Player_Vomit;
                    if (!suppressFeedback)
                    {
                        RouteToGameMixer(avatar.Voice, manager);
                        // SOUND_Manager.PlayOneShot2D applies its native 0.2 gain.
                        if (clip != null) avatar.Voice.PlayOneShot(clip, 0.2f);
                    }
                    avatar.VomitAt = now; avatar.VomitSeconds = clip != null ? Mathf.Max(1f, clip.length - 0.5f) : 3f;
                    if (!suppressFeedback) avatar.Props?.PlayVomit(avatar.Body, avatar.VomitSeconds);
                    break;
                case PlayerEmoteKind.Interact: avatar.InteractAt = now; break;
                case PlayerEmoteKind.PetLeo: avatar.PetAt = now; break;
                case PlayerEmoteKind.Wave:
                case PlayerEmoteKind.Point:
                case PlayerEmoteKind.ThumbsUp:
                    avatar.Gesture = kind;
                    avatar.GestureAt = now;
                    break;
            }
        }

        // #170: a linear 1.5-25 m falloff still played a remote step at 60% ten metres away, so another
        // player's steps sounded as if they were beside you. Use the falloff curve native NPC voices and
        // engines use (GameAssets.AudioSourceCustomCurve), which fades quickly with distance.
        private static void UseGameFalloff(AudioSource source)
        {
            if (source.rolloffMode == AudioRolloffMode.Custom) return;
            try
            {
                var curve = Singleton<GameAssets>.i?.AudioSourceCustomCurve;
                if (curve == null || curve.length == 0) return;
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
                source.rolloffMode = AudioRolloffMode.Custom;
            }
            catch { }
        }

        private static void RouteToGameMixer(AudioSource source, SOUND_Manager? manager)
        {
            UseGameFalloff(source);
            if (source.outputAudioMixerGroup == null && manager != null && OneShotSourceField?.GetValue(manager.GetComponent<SOUND_General>()) is AudioSource native)
                source.outputAudioMixerGroup = native.outputAudioMixerGroup;
        }

        // Native: the inhale plays while the smoke key is held and the exhale when it is let go.
        private void SmokeSound(Avatar avatar, bool inhaling)
        {
            var manager = Sounds();
            if (manager == null) return;
            RouteToGameMixer(avatar.Smoke, manager);
            RouteToGameMixer(avatar.Voice, manager);
            if (inhaling)
            {
                avatar.Smoke.clip = manager.General_SmokeInhale;
                avatar.Smoke.volume = 0.9f;
                if (avatar.Smoke.clip != null) avatar.Smoke.Play();
            }
            else
            {
                avatar.Smoke.Stop();
                if (manager.General_SmokeExhale != null) avatar.Voice.PlayOneShot(manager.General_SmokeExhale, 0.2f);
            }
        }

        private CMF.AudioControl? WalkerAudio() => walkerAudio != null ? walkerAudio : walkerAudio = UnityEngine.Object.FindObjectOfType<CMF.AudioControl>();

        // The local walker's own footstep clips and cadence, picked by the terrain layer underfoot.
        private void Footsteps(Avatar avatar, Vector3 position, float speed, float deltaTime)
        {
            var audio = WalkerAudio();
            if (audio == null || audio.footStepClips == null || audio.footStepClips.Length == 0) return;
            avatar.StepDistance += speed * deltaTime;
            if (avatar.StepDistance < Mathf.Clamp(audio.footstepDistance, 0.4f, 3f)) return;
            avatar.StepDistance = 0;
            if (footstepMask == -1)
                footstepMask = LayerMask.GetMask("Ground", "IgnoreByPlayerInteractable", "Default", "VehiclePlayerCantGoThrough", "MainVehicleTrigger");
            if (!Physics.Raycast(position + Vector3.up * 0.3f, Vector3.down, 0.6f, StockAvatarBody.GroundMask, QueryTriggerInteraction.Ignore)) return;
            int index = UnityEngine.Random.Range(0, audio.footStepClips.Length);
            var clip = audio.footStepClips[index];
            switch (TerrainLayer(position))
            {
                case 1: case 5: clip = Pick(audio.footStepGrassClips, index) ?? clip; break;
                case 0: case 2: clip = Pick(audio.footStepGravelClips, index) ?? clip; break;
            }
            if (clip == null) return;
            if (avatar.Feet.outputAudioMixerGroup == null && audio.audioSource != null) avatar.Feet.outputAudioMixerGroup = audio.audioSource.outputAudioMixerGroup;
            UseGameFalloff(avatar.Feet);
            float volume = audio.audioClipVolume * (1f + UnityEngine.Random.Range(-audio.relativeRandomizedVolumeRange, audio.relativeRandomizedVolumeRange));
            avatar.Feet.PlayOneShot(clip, volume * FootstepGain);

#if MONMP_DEVELOPMENT
            TestFootsteps++;
#endif

        }

        private static AudioClip? Pick(AudioClip[]? clips, int index) => clips != null && clips.Length > 0 ? clips[index % clips.Length] : null;

        // Native AudioControl.GetmainTexture: the strongest splat layer of the terrain underfoot.
        private int TerrainLayer(Vector3 position)
        {
            if (!Physics.Raycast(position + Vector3.up * 4f, Vector3.down, out var hit, 70f, footstepMask)) return -1;
            var terrain = hit.collider.GetComponent<Terrain>();
            if (terrain == null || terrain.terrainData == null) return -1;
            var data = terrain.terrainData;
            var origin = terrain.transform.position;
            int x = (int)((position.x - origin.x) / data.size.x * data.alphamapWidth);
            int y = (int)((position.z - origin.z) / data.size.z * data.alphamapHeight);
            if (x < 0 || y < 0 || x >= data.alphamapWidth || y >= data.alphamapHeight) return -1;
            var mix = data.GetAlphamaps(x, y, 1, 1);
            int best = 0; float strongest = 0;
            for (int i = 0; i < mix.GetLength(2); i++) if (mix[0, 0, i] > strongest) { strongest = mix[0, 0, i]; best = i; }
            return best;
        }

        // The pushed wheelbarrow near the avatar: native pushing turns it to look at the pusher's
        // hands, so the grips sit on its forward axis where it passes the hands, either side of it.
        private static bool TryBarrowGrips(Vector3 position, Quaternion rotation, out Vector3 left, out Vector3 right)
        {
            left = right = default;
            if (Time.unscaledTime >= nextBarrowScan) { barrows = UnityEngine.Object.FindObjectsOfType<Wheelbarrow>(); nextBarrowScan = Time.unscaledTime + 5f; }
            Wheelbarrow? best = null;
            float bestDistance = 3f;
            foreach (var barrow in barrows)
            {
                if (barrow == null) continue;
                float distance = Vector3.Distance(barrow.transform.position, position);
                if (distance < bestDistance) { best = barrow; bestDistance = distance; }
            }
            if (best == null) return false;
            var t = best.transform;
            if (!barrowShapes.TryGetValue(best, out var shape))
            {
                // The far end of the handles and half the handle spread, from the meshes in barrow space.
                float reach = 1.2f, half = 0.27f;
                var ends = new List<Vector3>();
                foreach (var filter in best.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null) continue;
                    var b = filter.sharedMesh.bounds;
                    for (int i = 0; i < 8; i++)
                        ends.Add(t.InverseTransformPoint(filter.transform.TransformPoint(b.center + Vector3.Scale(b.extents,
                            new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)))));
                }
                if (ends.Count > 0)
                {
                    reach = ends.Max(e => e.z);
                    half = Mathf.Clamp(ends.Where(e => e.z > reach - 0.25f).Select(e => Mathf.Abs(e.x)).DefaultIfEmpty(0.27f).Max() - 0.03f, 0.18f, 0.35f);
                }
                barrowShapes[best] = shape = (reach, half);
            }
            var hands = position + rotation * PushHands;
            float along = Mathf.Clamp(Vector3.Dot(hands - t.position, t.forward), 0.1f, Mathf.Max(0.2f, shape.Reach - 0.06f));
            var center = t.position + t.forward * along;
            var across = Vector3.ProjectOnPlane(rotation * Vector3.right, t.forward);
            across = across.sqrMagnitude < 1e-4f ? t.right : across.normalized;
            left = center - across * shape.Half; right = center + across * shape.Half;
            return true;
        }

        private static void PlayVoice(Avatar avatar, SOUND_Manager? manager, PlacerVoiceDetails[]? voices, byte variant, string textKey)
        {
            if (manager == null || voices == null || voices.Length == 0) return;
            var clip = voices[variant % voices.Length]?.VoiceClip;
            // Route through the game's own mixer group so the volume settings apply.
            RouteToGameMixer(avatar.Voice, manager);
            if (clip != null) avatar.Voice.PlayOneShot(clip, 0.8f);
            avatar.IsChatCaption = false;
            try { avatar.VoiceText = TEXT.GetText(textKey); } catch { avatar.VoiceText = ""; }
            avatar.VoiceUntil = Time.unscaledTime + VoiceTextSeconds;
        }

        // A presentation copy of the native spit drop: no script, collider or rigidbody, so only the
        // spitter's own drop waters plants or reaches NPCs.
        private void SpawnSpit(uint owner, Vector3 mouth, Quaternion look)
        {
            if (assets == null) assets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            var prefab = assets != null ? assets.Player_SpitDrop : null;
            if (prefab == null) return;
            var drop = UnityEngine.Object.Instantiate(prefab, mouth, look);
            float speed = 5f;
            foreach (var body in drop.GetComponentsInChildren<Rigidbody>(true)) if (body.mass > 0.0001f) speed = Mathf.Clamp(5f / body.mass, 1f, 20f);
            foreach (var behaviour in drop.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(behaviour);
            foreach (var component in drop.GetComponentsInChildren<Component>(true))
                if (component is Rigidbody || component is AudioSource) UnityEngine.Object.DestroyImmediate(component);
            foreach (var collider in drop.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            spitDrops.Add(new SpitDrop { Drop = drop, Owner = owner, Velocity = look * Vector3.forward * speed, Until = Time.unscaledTime + SpitDropLife });

#if MONMP_DEVELOPMENT
            TestSpitDropsSpawned++;
#endif

        }

        private void UpdateSpitDrops(float deltaTime)
        {
            float now = Time.unscaledTime;
            for (int i = spitDrops.Count - 1; i >= 0; i--)
            {
                var d = spitDrops[i];
                if (d.Drop == null || now > d.Until)
                {
                    if (d.Drop != null) UnityEngine.Object.Destroy(d.Drop);
                    spitDrops.RemoveAt(i);
                    continue;
                }
                if (d.Landed) continue;
                var from = d.Drop.transform.position;
                d.Velocity += Physics.gravity * deltaTime;
                var to = from + d.Velocity * deltaTime;
                if (Physics.Linecast(from, to, out var hit, StockAvatarBody.GroundMask, QueryTriggerInteraction.Ignore))
                {
                    d.Drop.transform.position = hit.point;
                    d.Landed = true; d.Until = now + 0.1f;
                    foreach (var renderer in d.Drop.GetComponentsInChildren<Renderer>()) renderer.enabled = false;
                    var land = Sounds()?.Player_Spit_Land;
                    if (land != null) AudioSource.PlayClipAtPoint(land, hit.point, 0.15f);

#if MONMP_DEVELOPMENT
                    TestSpitDropsLanded++;
#endif

                }
                else d.Drop.transform.position = to;
            }
        }

        // Hand grips at nine and three o'clock on the rim, turned with the wheel (up to where the
        // hands would cross).
        private static (Vector3 Left, Vector3 Right) WheelGrips(Transform wheel, float angle, Vector3 vehicleRight)
            => VehicleSeatPosePresentation.AutomaticWheelGrips(wheel, angle, vehicleRight);

        public void Update()
        {
            // Both the remote roster and the independent self-view are updated in menus
            // and between sessions too. Avoid Camera.main's native lookup when empty.
            if (avatars.Count == 0 && spitDrops.Count == 0) return;
            float frameTime = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            float now = Time.unscaledTime;
            var viewer = Camera.main;
            var viewerPosition = viewer != null ? viewer.transform.position : Vector3.zero;
            var plugin = MonMPPlugin.Instance;
            var items = plugin?.Items;
            var world = plugin?.World;
            var calibrations = plugin?.HeldGrips;
            // Held-item queries share SharedItems' lazy frame snapshot. Eagerly
            // rebuilding here made the remote roster and self-view scan it twice.
            foreach (var pair in avatars)
            {
                var avatar = pair.Value;
                if (!avatar.HasPose) continue;
                float deltaTime = frameTime;
                float t = Mathf.Clamp01((now - avatar.ReceivedAt) / InterpolationSeconds);
                avatar.SpectatorHead = Vector3.Lerp(avatar.SpectatorHeadFrom, avatar.SpectatorHeadTo, t);
                if (avatar.Spectating)
                {
                    // A spectator has no hands, feet, vehicle seat, held tool, or body animation
                    // to solve. Keep only the floating head, skipping world seat/bed searches,
                    // ground probes, IK, props and full-body animation for this avatar.
                    var spectatorPosition = avatar.Carrier != null
                        ? avatar.Carrier.TransformPoint(Vector3.Lerp(avatar.FromLocal, avatar.ToLocal, t))
                        : Vector3.Lerp(avatar.From, avatar.To, t);
                    var spectatorRotation = Quaternion.Slerp(avatar.FromRotation, avatar.ToRotation, t);
                    avatar.CameraRotation = spectatorRotation;
                    avatar.Root.transform.SetPositionAndRotation(spectatorPosition, spectatorRotation);
                    avatar.LastRootPosition = spectatorPosition;
                    avatar.LastCarrier = avatar.Carrier;
                    if (avatar.Carrier != null) avatar.LastCarrierLocal = avatar.Carrier.InverseTransformPoint(spectatorPosition);

                    float spectatorYaw = spectatorRotation.eulerAngles.y;
                    float spectatorCameraTurn = avatar.HasCameraYaw ? Mathf.DeltaAngle(avatar.LastCameraYaw, spectatorYaw) : 0f;
                    avatar.LastCameraYaw = spectatorYaw; avatar.HasCameraYaw = true;
                    bool spectatorStanding = avatar.State == (byte)PlayerState.Walking && avatar.Rest == PlayerRest.None;
                    avatar.BodyYaw = spectatorStanding
                        ? Mathf.MoveTowards(Mathf.Clamp(avatar.BodyYaw - spectatorCameraTurn, -StandingBodySlack, StandingBodySlack),
                            0f, deltaTime * (avatar.WalkAmount > 0.05f ? 360f : RemoteBodyFacing.IdleRecoveryDegrees))
                        : 0f;
                    float spectatorHeadYaw = spectatorStanding ? -avatar.BodyYaw : 0f;
                    float spectatorPitch = Mathf.Lerp(avatar.FromPitch, avatar.Pitch, t);
                    if ((avatar.Body == null || !avatar.BodyAppearance.SameGeometry(avatar.Appearance)) && now >= avatar.NextBodyAttempt) TryAttachBody(avatar);
                    if (avatar.Body != null)
                        avatar.Body.PresentSpectatorHead(avatar.SpectatorHead, avatar.BodyYaw, spectatorHeadYaw, spectatorPitch);
                    if (avatar.Body?.HasSpectatorHeadMesh != true)
                    {
                        avatar.Head.position = avatar.SpectatorHead;
                        avatar.Head.rotation = Quaternion.Euler(spectatorPitch, spectatorYaw + spectatorHeadYaw, 0f);
                    }
                    continue;
                }
                var seat = world?.Vehicles.SeatFor(pair.Key) ?? world?.SchoolBusPassengers.SeatFor(pair.Key) ?? world?.MartinCar?.SeatFor(pair.Key);
                if (seat == null && world != null && world.BoatMotion.TryGetSeatFrame(pair.Key, out var boatSeat)) seat = boatSeat;
                var extraKind = SharedExtraSeats.SeatKind.Cab;
                Transform? extraSeat = seat == null ? world?.ExtraSeats.SeatFor(pair.Key, out extraKind) : null;
                if (extraSeat != null) seat = extraSeat;
                avatar.PresentationSeat = seat;
                avatar.PresentationSeatFrame = Time.frameCount;
                if (seat != null)
                {
                    avatar.PresentationSeatPosition = seat.position;
                    avatar.PresentationSeatRotation = seat.rotation;
                }
                bool resting = avatar.Rest != PlayerRest.None && seat == null;
                var position = seat != null ? seat.position :
                    avatar.Carrier != null ? avatar.Carrier.TransformPoint(Vector3.Lerp(avatar.FromLocal, avatar.ToLocal, t)) : avatar.GroundPrediction ? avatar.GroundMotion.Position(now) : Vector3.Lerp(avatar.From, avatar.To, t);
                var cameraRotation = Quaternion.Slerp(avatar.FromRotation, avatar.ToRotation, t);
                avatar.CameraRotation = cameraRotation;
                float cameraYaw = cameraRotation.eulerAngles.y;
                bool chair = seat == null && !resting && avatar.State == (byte)PlayerState.InFurnitureChair;
                bool sleeping = resting && avatar.Rest == PlayerRest.Sleeping;
                if (sleeping)
                {
                    // Native sleep moves the camera every frame. A remote body rests at its
                    // initial bed position instead of following those camera shakes.
                    if (!avatar.SleepAnchorKnown) { avatar.SleepAnchor = avatar.To; avatar.SleepAnchorKnown = true; avatar.SleepAnchorFrame = null; }
                    if (avatar.SleepAnchorFrame != null) avatar.SleepAnchor = avatar.SleepAnchorFrame.TransformPoint(avatar.SleepAnchorLocal);
                    position = avatar.SleepAnchor;
                }
                else { avatar.SleepAnchorKnown = false; avatar.SleepAnchorFrame = null; }
                if (sleeping && now >= avatar.NextBedCheck)
                {
                    avatar.NextBedCheck = now + 1f;
                    var bed = world?.Sleep.BedOf(pair.Key) ?? NearestBed(position);
                    // Decide which way the sleeper lies once per bed. Re-deciding every second from the
                    // bobbing eyes flipped the body end for end over the head: a jump of meters (#115).
                    if (!avatar.HasBed || bed != avatar.Bed)
                    {
                        avatar.Bed = bed;
                        if (bed?.SleepPosition != null) position = SleepingEyes(bed.SleepPosition);
                        avatar.HasBed = TryBed(bed, position, cameraYaw, out avatar.BedYaw, out avatar.BedCurl, out avatar.BedWidth);
                        if (bed != null)
                        {
                            avatar.SleepAnchorFrame = bed.transform;
                            if (bed.SleepPosition != null)
                                avatar.SleepAnchor = SleepingEyes(bed.SleepPosition);
                            avatar.SleepAnchorLocal = bed.transform.InverseTransformPoint(avatar.SleepAnchor);
                            position = avatar.SleepAnchor;
                        }
                    }
                }
                else if (!sleeping) { avatar.HasBed = false; avatar.NextBedCheck = 0; avatar.BedSideways = 0; }
                var rotation = seat != null ? seat.rotation
                    : chair ? Quaternion.Euler(0, avatar.ChairYaw, 0)
                    : sleeping && avatar.HasBed ? Quaternion.Euler(0, avatar.BedYaw, 0)
                    : cameraRotation;
                // On a mechanic creeper: the head rests on its pillow and the body lies along the board (#209).
                Vector3 creeperPillow = default;
                Quaternion creeperFeet = default;
                bool creeperRider = seat == null && !resting && avatar.State == (byte)PlayerState.InCreeper &&
                    world != null && world.Creepers.TryRiderFrame(pair.Key, out creeperPillow, out creeperFeet);
                if (creeperRider) { position = creeperPillow; rotation = creeperFeet; }
                avatar.BedFloor = false;
                if (sleeping && avatar.HasBed && avatar.Bed != null && world != null)
                {
                    var occupancy = world.Sleep.BedOccupancy(pair.Key, avatar.Bed);
                    var layout = MonMP.Core.World.SleeperLayout.For(occupancy.Slot, occupancy.Count, avatar.BedWidth, avatar.BedCurl);
                    // Ease between places, so a sleeper joining or leaving the bed doesn't make the others jump.
                    avatar.BedSideways = Mathf.MoveTowards(avatar.BedSideways, layout.Sideways, deltaTime * 1.5f);
                    position += rotation * Vector3.right * avatar.BedSideways;
                    avatar.BedFloor = layout.OnFloor;
                    if (layout.OnFloor && Physics.Raycast(position + Vector3.up, Vector3.down, out var floor, 8f, StockAvatarBody.GroundMask, QueryTriggerInteraction.Ignore))
                        position.y = floor.point.y + 0.22f;
                }
                VehicleSeatPoseCatalog.Entry? calibratedSeat = null;
                VehicleSeatPoseOverrides.Pose? seatPose = null;
                if (seat != null && plugin?.SeatPoses != null)
                {
                    calibratedSeat = VehicleSeatPoseCatalog.Resolve(world, pair.Key, seat, avatar.State == (byte)PlayerState.InVehicle_Driver, extraKind);
                    if (calibratedSeat != null && plugin.SeatPoses.TryGetRuntime(calibratedSeat.VehicleKey, calibratedSeat.SeatKey, calibratedSeat.Shape, out var savedSeat))
                    {
                        seatPose = savedSeat;
                        VehicleSeatPosePresentation.BodyPlacement(calibratedSeat, savedSeat, out position, out rotation);
                    }
                }
                avatar.Root.transform.SetPositionAndRotation(position, rotation);
                avatar.MapPosition = SpectatorPresentationPolicy.MapAnchor(position, avatar.MapPosition, spectating: false, hasLastRenderedRoot: true);

                float speed = 0;
                var movement = position - avatar.LastRootPosition;
                avatar.LastRootPosition = position;
                // A rider's gait is its step over the carrier, not the carrier's own travel (#249).
                if (avatar.Carrier != null)
                {
                    var local = avatar.Carrier.InverseTransformPoint(position);
                    movement = avatar.LastCarrier == avatar.Carrier ? avatar.Carrier.TransformVector(local - avatar.LastCarrierLocal) : Vector3.zero;
                    avatar.LastCarrierLocal = local;
                }
                avatar.LastCarrier = avatar.Carrier;
                bool pushing = avatar.State == (byte)PlayerState.InWheelbarrow && seat == null && !resting;
                if ((avatar.State == (byte)PlayerState.Walking || pushing) && seat == null && !resting && CanUseMovementGait(avatar.Carrier, movement))
                {
                    speed = avatar.Carrier == null ? avatar.GroundMotion.Speed(now) : new Vector2(movement.x, movement.z).magnitude / deltaTime;
                }
                float distance = viewer != null ? Vector3.Distance(viewerPosition, position) : 0f;
                if (!avatar.IsLocalSelf && !avatar.Spectating && avatar.State == (byte)PlayerState.Walking && !resting && seat == null && distance < FootstepDistance && speed > 0.05f)
                    Footsteps(avatar, position, speed, deltaTime);
                float targetWalk = Mathf.Clamp01(Mathf.Max(0, speed - 0.08f) / WalkSpeedForFullGait);
                avatar.WalkAmount = Mathf.MoveTowards(avatar.WalkAmount, targetWalk, deltaTime * 5f);
                if (avatar.WalkAmount > 0.001f)
                    avatar.GaitPhase = Mathf.Repeat(avatar.GaitPhase + deltaTime * Mathf.Lerp(2f, 10f, avatar.WalkAmount), Mathf.PI * 2f);

                avatar.CrouchAmount = Mathf.Lerp(avatar.FromCrouch, avatar.ToCrouch, t);
                avatar.SorryAmount = Mathf.MoveTowards(avatar.SorryAmount, avatar.Sorry ? 1f : 0f, deltaTime * 6f);
                float handRate = deltaTime * 5f;
                avatar.MapAmount = Mathf.MoveTowards(avatar.MapAmount, (avatar.Hands & PlayerHands.Map) != 0 && !resting ? 1f : 0f, handRate);
                avatar.ConsoleAmount = Mathf.MoveTowards(avatar.ConsoleAmount, (avatar.Hands & PlayerHands.Console) != 0 && !resting ? 1f : 0f, handRate);
                avatar.FlashAmount = Mathf.MoveTowards(avatar.FlashAmount, avatar.FlashlightOn && !resting ? 1f : 0f, handRate);
                avatar.JointAmount = Mathf.MoveTowards(avatar.JointAmount, (avatar.Hands & PlayerHands.Joint) != 0 && !resting ? 1f : 0f, handRate);
                avatar.SmokeAmount = Mathf.MoveTowards(avatar.SmokeAmount, (avatar.Hands & PlayerHands.Smoking) != 0 && !resting ? 1f : 0f, deltaTime * 3f);
                avatar.DrunkAmount = Mathf.MoveTowards(avatar.DrunkAmount, avatar.DrunkTarget, deltaTime * 0.5f);
                avatar.HangoverAmount = Mathf.MoveTowards(avatar.HangoverAmount, avatar.Hungover ? 1f : 0f, deltaTime * 0.5f);
                float pitch = Mathf.Lerp(avatar.FromPitch, avatar.Pitch, t);

                // Standing still, the body keeps its facing until the head is 45 degrees off it, then
                // slowly squares up; walking lines it up at once. Seated, only the head turns.
                bool standing = seat == null && avatar.State == (byte)PlayerState.Walking && !resting;
                bool gestureStateAvailable = RemoteGesturePresentationPolicy.CanPresent(
                    (PlayerState)avatar.State, standing, seat != null || chair);
                if (!gestureStateAvailable || avatar.Gesture != PlayerEmoteKind.None &&
                    now - avatar.GestureAt >= StockAvatarBody.GestureDuration(avatar.Gesture))
                    avatar.Gesture = PlayerEmoteKind.None;
                float cameraTurn = avatar.HasCameraYaw ? Mathf.DeltaAngle(avatar.LastCameraYaw, cameraYaw) : 0;
                avatar.LastCameraYaw = cameraYaw; avatar.HasCameraYaw = true;
                var heldForFacing = items?.HeldItemForPresentation(pair.Key);
                bool furnitureHeld = heldForFacing is Furniture_Decoration || heldForFacing is Garden_OutdoorPlanter;
                avatar.BodyYaw = RemoteBodyFacing.Offset(avatar.BodyYaw, cameraTurn, standing, avatar.WalkAmount > 0.05f, furnitureHeld, deltaTime);
                float headYaw = standing ? -avatar.BodyYaw : seat != null || chair ?
                    StockGesturePoseMath.SeatedHeadYaw(rotation.eulerAngles.y, cameraYaw) : 0;
                // Jumping or falling: nothing under the feet (only looked for near enough to see).
                bool airborne = standing && distance < FeetDistance &&
                    !Physics.Raycast(position + Vector3.up * 0.15f, Vector3.down, 0.5f, StockAvatarBody.GroundMask, QueryTriggerInteraction.Ignore);
                avatar.AirAmount = Mathf.MoveTowards(avatar.AirAmount, airborne ? 1f : 0f, deltaTime * (airborne ? 4f : 8f));
                ToolEffects(avatar, pair.Key, !avatar.IsLocalSelf && !avatar.Spectating && (avatar.Hands & PlayerHands.Tool) != 0 &&
                    now - avatar.ReceivedAt <= 2f && !resting && distance < ThrottleDistance);

                if ((avatar.Body == null || !avatar.BodyAppearance.SameGeometry(avatar.Appearance)) && now >= avatar.NextBodyAttempt) TryAttachBody(avatar);
                bool posed = true;
                if (avatar.Body != null && distance > ThrottleDistance)
                {
                    avatar.FarElapsed += deltaTime;
                    posed = avatar.FarElapsed >= ThrottleSeconds;
                    if (posed) { deltaTime = avatar.FarElapsed; avatar.FarElapsed = 0; }
                }
                Vector3? held = null;
                Quaternion? heldRotation = null;
                Vector3? heldLeft = null;
                Quaternion? heldLeftRotation = null;
                float heldWidth = 0;
                Transform? heldView = null;
                int heldViewKind = 0; // ordinary copy, impact gun, scene air, fuel nozzle
                var heldLook = Quaternion.Euler(StockGesturePoseMath.ClampAimPitch(pitch), cameraYaw, 0);
                var heldBodyRotation = rotation * Quaternion.Euler(0, avatar.BodyYaw, 0);
                var impactEyes = HeldEyes(position, rotation, avatar.State, resting, creeperRider, avatar.CrouchAmount);
                if (posed)
                {
                    bool chairHolding = !resting && avatar.State == (byte)PlayerState.InFurnitureChair;
                    bool canPresentHeld = standing || chairHolding || creeperRider;
                    // Native carry floats at its shared world pose. Only equipped tools
                    // use calibrated grips, copied models and the rigid reach correction.
                    bool equipped = (avatar.Hands & PlayerHands.Equipped) != 0;
                    if (equipped && (standing || chairHolding) && items != null &&
                        items.TryGetHeldItemPresentation(pair.Key, rotation * Quaternion.Euler(0, avatar.BodyYaw, 0) * Vector3.right, position + Vector3.up * 1.35f, out var heldPosition, out heldWidth, out heldRotation, out heldLeft, out heldLeftRotation))
                        held = heldPosition;
                    // On a creeper the tool is held up over the chest, where the rider works under the car (#209).
                    else if (equipped && creeperRider && items != null &&
                        items.TryGetHeldItemPresentation(pair.Key, rotation * Vector3.right, null, out var lyingHeld, out heldWidth, out heldRotation, out heldLeft, out heldLeftRotation))
                        held = lyingHeld;
                    // VR items already carry the owner's wrist pose. A desktop
                    // equipment copy here would move the tool back to the face.
                    if (equipped && !avatar.IndependentAim && !avatar.LocalShadowOnly && !avatar.Spectating && canPresentHeld && items != null && calibrations != null &&
                        now - avatar.ReceivedAt <= 2f &&
                        items.TryGetRemoteHeldEquipment(pair.Key, out var equipment, out var equipmentShape))
                    {
                        avatar.HeldVisual ??= new RemoteHeldVisual(calibrations);
                        Vector3? equipPosition = null; Quaternion? equipRotation = null;
                        if (plugin?.Gameplay != null)
                        {
                            avatar.EquippedPose ??= new RemoteEquippedPose();
                            if (avatar.EquippedPose.TryGet(equipment, plugin.Gameplay, impactEyes, heldLook, out var nativePosition, out var nativeRotation))
                            { equipPosition = nativePosition; equipRotation = nativeRotation; }
                        }
                        if (avatar.HeldVisual.TryPresent(equipment, equipmentShape,
                            heldBodyRotation, out _, out _, equipPosition, equipRotation, avatar.Root.transform) &&
                            avatar.HeldVisual.PresentedTransform is Transform equipmentView)
                        {
                            heldView = equipmentView;
                            if (calibrations.TryGetRuntime(equipment, equipmentShape, out var localPalm, out var localPalmRotation, out var width,
                                out var localLeft, out var localLeftRotation))
                            {
                                held = equipmentView.TransformPoint(localPalm);
                                heldRotation = equipmentView.rotation * localPalmRotation; heldWidth = width;
                                heldLeft = localLeft.HasValue ? equipmentView.TransformPoint(localLeft.Value) : (Vector3?)null;
                                heldLeftRotation = localLeftRotation.HasValue ? equipmentView.rotation * localLeftRotation.Value : (Quaternion?)null;
                            }
                            else if (held.HasValue)
                            {
                                held = equipmentView.TransformPoint(equipment.transform.InverseTransformPoint(held.Value));
                                if (heldLeft.HasValue) heldLeft = equipmentView.TransformPoint(equipment.transform.InverseTransformPoint(heldLeft.Value));
                            }
                        }
                    }
                    else if (!avatar.LocalShadowOnly) avatar.HeldVisual?.Reset();
                    Vector3 impactPalm = default; Quaternion impactTurn = default;
                    Vector3? impactLeft = null; Quaternion? impactLeftTurn = null; float impactWidth = 0;
                    // Native creeper entry anchors the walker at the pillow and lowers its
                    // standing camera by 1.32 m. Pass eyes, not a walking-height head frame.
                    bool impactPresented = canPresentHeld && now - avatar.ReceivedAt <= 2f &&
                        world != null && world.Bolts.TryPresentImpactGun(pair.Key, impactEyes, heldLook,
                            out impactPalm, out impactTurn, out impactLeft, out impactLeftTurn, out impactWidth,
                            rotation * Quaternion.Euler(0, avatar.BodyYaw, 0));
                    if (impactPresented)
                    {
                        held = impactPalm; heldRotation = impactTurn; heldWidth = impactWidth;
                        heldLeft = impactLeft; heldLeftRotation = impactLeftTurn;
                        avatar.HeldVisual?.Reset();
                        heldView = world!.Bolts.PresentedTransform(pair.Key); heldViewKind = 1;
                    }
                    else world?.Bolts.ResetImpactPresentation(pair.Key);
                    if (!impactPresented && canPresentHeld && now - avatar.ReceivedAt <= 2f &&
                        world != null && world.SceneAirEquipment.TryPresent(pair.Key, position, heldLook, avatar.CrouchAmount,
                            out var airPalm, out var airTurn, out var airLeft, out var airLeftTurn, out var airWidth, impactEyes,
                            rotation * Quaternion.Euler(0, avatar.BodyYaw, 0)))
                    {
                        held = airPalm; heldRotation = airTurn; heldWidth = airWidth;
                        heldLeft = airLeft; heldLeftRotation = airLeftTurn;
                        avatar.HeldVisual?.Reset();
                        heldView = world.SceneAirEquipment.PresentedTransform(pair.Key); heldViewKind = 2;
                    }
                    else world?.SceneAirEquipment.ResetPresentation(pair.Key);
                    if (world?.FuelNozzles.TryPalm(pair.Key, out var nozzlePalm, out var nozzleTurn,
                        out var nozzleLeft, out var nozzleLeftTurn, out var nozzleWidth, heldBodyRotation, impactEyes, heldLook, plugin?.Gameplay) == true)
                    {
                        held = nozzlePalm; heldRotation = nozzleTurn; heldWidth = nozzleWidth; heldLeft = nozzleLeft; heldLeftRotation = nozzleLeftTurn;
                        avatar.HeldVisual?.Reset(); heldView = world.FuelNozzles.PresentedTransform(pair.Key); heldViewKind = 3;
                    }
                    if (heldView != null && heldView.parent != avatar.Root.transform) heldView.SetParent(avatar.Root.transform, true);
                }
                // A wheel gesture is a short-lived cosmetic hand pose. Cancel it as soon as a
                // higher-priority native hold/gesture becomes active; it must not reappear when
                // the map, tool, Sorry pose or held goal later clears.
                bool nativeGesture = now - avatar.InteractAt >= 0 && now - avatar.InteractAt < StockAvatarBody.InteractSeconds ||
                    now - avatar.PetAt >= 0 && now - avatar.PetAt < StockAvatarBody.PetSeconds;
                const PlayerHands zoomBlockedHands = PlayerHands.Map | PlayerHands.Console | PlayerHands.Joint |
                    PlayerHands.Smoking | PlayerHands.Tool | PlayerHands.Equipped;
                bool zoom = (avatar.Hands & PlayerHands.Zoom) != 0 && (avatar.Hands & zoomBlockedHands) == 0 &&
                    !resting && !avatar.Spectating && !held.HasValue && items?.HeldItemOf(pair.Key) == null &&
                    !avatar.FlashlightOn && avatar.SorryAmount < 0.01f && !nativeGesture &&
                    now - avatar.VomitAt >= avatar.VomitSeconds &&
                    avatar.State != (byte)PlayerState.InCreeper && avatar.State != (byte)PlayerState.InWheelbarrow &&
                    now - avatar.ReceivedAt <= 2f;
                if (!PlayerEmotePolicy.CanContinueCosmeticGesture(avatar.Gesture, gestureStateAvailable, avatar.Rest, avatar.Hands,
                    avatar.Sorry, avatar.MapAmount > 0.001f, avatar.ConsoleAmount > 0.001f,
                    held.HasValue, nativeGesture))
                    avatar.Gesture = PlayerEmoteKind.None;
                if (avatar.Body != null && posed)
                {
                    var seatedVehicle = seat != null ? world?.Vehicles.VehicleForPeer(pair.Key) ?? seat.GetComponentInParent<NWH.VehiclePhysics2.VehicleController>() : null;
                    bool missile = seatedVehicle != null && seatedVehicle.vehicle_type == Vehicle_Type.LeMissile;
                    avatar.Body.SetPresentationScale(missile ? StockAvatarBody.MissileScale : 1f);
                    avatar.Body.SetRacingHelmet(!avatar.Spectating &&
                        (missile || world?.SpeedwayEvents?.WearsRacingHelmet(pair.Key) == true));
                    var mode = resting ? (sleeping ? (avatar.HasBed && avatar.BedCurl && !avatar.BedFloor ? StockAvatarBody.Mode.Curl : StockAvatarBody.Mode.Lie) : StockAvatarBody.Mode.Down) : BodyMode(avatar.State);
                    var overlay = new StockAvatarBody.Overlay
                    {
                        Pitch = pitch, HeadYaw = headYaw, BodyYaw = avatar.BodyYaw, Sorry = avatar.SorryAmount,
                        SlurTime = now - avatar.SlurAt, SpitTime = now - avatar.SpitAt,
                        Gesture = gestureStateAvailable ? avatar.Gesture : PlayerEmoteKind.None,
                        GestureTime = avatar.Gesture == PlayerEmoteKind.None ? -1f : now - avatar.GestureAt,
                        PointTarget = MonMPPlugin.Instance?.Pings?.PointTarget(pair.Key),
                        Map = avatar.MapAmount, Console = avatar.ConsoleAmount, Flashlight = avatar.FlashAmount, Joint = avatar.JointAmount, Smoking = avatar.SmokeAmount,
                        VomitTime = now - avatar.VomitAt, VomitSeconds = avatar.VomitSeconds, InteractTime = now - avatar.InteractAt, PetTime = now - avatar.PetAt,
                        Drunk = avatar.DrunkAmount, Hangover = avatar.HangoverAmount, Clock = now + pair.Key * 2.7f,
                        Air = avatar.AirAmount, Zoom = zoom ? 1f : 0f
                    };
                    if (extraSeat != null) mode = extraKind == SharedExtraSeats.SeatKind.Ground ? StockAvatarBody.Mode.Ground : StockAvatarBody.Mode.Chair;
                    // Getting on a creeper lies straight down on it: easing down from standing swung the
                    // body up out of the floor on the board's pivot (#209).
                    if (creeperRider && !avatar.OnCreeper) avatar.Body.SetLieBlend(1f);
                    avatar.OnCreeper = creeperRider;
                    if (!chair) avatar.StoolChecked = false;
                    else if (!avatar.StoolChecked) { avatar.StoolChecked = true; avatar.StoolSurface = StoolSurface(avatar.To); }
                    avatar.Body.Present(mode, avatar.WalkAmount, speed, avatar.CrouchAmount, overlay, deltaTime);
                    // Bed floors and stools: the hips on the surface; other seats (cab, ATV rider: the
                    // driver seat's frame) by the head height.
                    if (extraSeat != null && (extraKind == SharedExtraSeats.SeatKind.Ground || extraKind == SharedExtraSeats.SeatKind.Stool))
                        avatar.Body.AlignHips(position);
                    else if (chair && avatar.StoolSurface.HasValue)
                        avatar.Body.AlignHips(avatar.StoolSurface.Value);
                    else if (mode == StockAvatarBody.Mode.Sit || mode == StockAvatarBody.Mode.Chair || mode == StockAvatarBody.Mode.Drive)
                        avatar.Body.AlignSeatHead(position + rotation * Vector3.up * SeatedHeadHeight);
                    // Asleep, the pose position is the sleeper's eyes.
                    else if ((mode == StockAvatarBody.Mode.Lie || mode == StockAvatarBody.Mode.Curl) && sleeping)
                        avatar.Body.AlignHead(position - Vector3.up * 0.06f);
                    else if (mode == StockAvatarBody.Mode.Lie && creeperRider)
                        avatar.Body.AlignHead(creeperPillow + Vector3.up * 0.14f);
                    var goals = new StockAvatarBody.Goals
                    {
                        Held = held, HeldWidth = heldWidth, HeldRotation = heldRotation, HeldLeft = heldLeft, HeldLeftRotation = heldLeftRotation,
                        ConstrainHeld = heldView != null,
                        Feet = mode == StockAvatarBody.Mode.Stand && distance < FeetDistance && avatar.AirAmount < 0.1f
                    };
                    if (mode == StockAvatarBody.Mode.Drive && world != null &&
                        (world.Vehicles.TryGetSteeringWheel(pair.Key, out var wheel, out var wheelAngle, out var vehicleRight) ||
                         world.BoatMotion.TryGetSteeringWheel(pair.Key, out wheel, out wheelAngle, out vehicleRight)))
                    {
                        var grips = WheelGrips(wheel, wheelAngle, vehicleRight);
                        goals.GripLeft = grips.Left; goals.GripRight = grips.Right; goals.GripCenter = (grips.Left + grips.Right) * 0.5f;
                    }
                    else if (pushing && TryBarrowGrips(position, rotation, out var barrowLeft, out var barrowRight))
                    {
                        goals.GripLeft = barrowLeft; goals.GripRight = barrowRight; goals.Push = true;
                    }
                    if (seatPose != null && calibratedSeat != null && seatPose.CustomHands)
                    {
                        VehicleSeatPosePresentation.HandFrame(calibratedSeat, out var handPosition, out var handRotation);
                        VehicleSeatPosePresentation.ApplyHands(seatPose, handPosition, handRotation, ref goals, mode == StockAvatarBody.Mode.Drive);
                    }
                    if (seatPose != null && calibratedSeat != null)
                        VehicleSeatPosePresentation.ApplyFeet(seatPose, calibratedSeat.Anchor.position, calibratedSeat.Anchor.rotation, ref goals);
                    avatar.Props?.PrepareMapCalibration(ref goals, plugin?.HeldGrips,
                        avatar.MapAmount > .5f && avatar.ConsoleAmount <= .5f);
                    avatar.Props?.PrepareFlashlightCalibration(ref goals, plugin?.HeldGrips, avatar.FlashAmount > .5f);
                    avatar.Body.Solve(goals, deltaTime, out var heldTranslation);
                    if (heldView != null)
                    {
                        if (heldViewKind == 0) avatar.HeldVisual?.MoveWithHands(heldTranslation);
                        else if (heldViewKind == 1) world?.Bolts.TranslatePresentation(pair.Key, heldTranslation);
                        else if (heldViewKind == 2) world?.SceneAirEquipment.TranslatePresentation(pair.Key, heldTranslation);
                        else world?.FuelNozzles.TranslatePresentation(pair.Key, heldTranslation);
                    }
                    var smokeVehicle = seat != null ? world?.Vehicles.VehicleForPeer(pair.Key) ??
                        world?.SchoolBusPassengers.VehicleForPeer(pair.Key) ?? world?.MartinCar?.VehicleForPeer(pair.Key) : null;
                    avatar.Props?.Update(avatar.Body, avatar.MapAmount, avatar.ConsoleAmount, avatar.FlashAmount, avatar.JointAmount, avatar.SmokeAmount,
                        !avatar.LocalShadowOnly && !avatar.Spectating && !resting, smokeVehicle, deltaTime, zoom, suppressFeedback: avatar.IsLocalSelf);
                }
                else
                {
                    Animate(avatar);
                    if (held.HasValue) AimRightHandAt(avatar, held.Value);
                    else if (avatar.SorryAmount > 0.5f) AimRightHandAt(avatar, avatar.Head.position + rotation * new Vector3(0.3f, 0.1f, 0.1f));
                    avatar.Head.localRotation = Quaternion.Euler(pitch, headYaw, 0);
                }
                if (!posed || avatar.Body == null) avatar.HeldVisual?.MoveWithHands(Vector3.zero);
                RemoteAvatarLightPresentation.Present(avatar.Flashlight, avatar.Body, avatar.Head, avatar.Props, avatar.FlashAmount, pitch, cameraYaw);
                AvatarTransformWrites.LocalPosition(avatar.HeldAnchor, new Vector3(0.35f, Mathf.Lerp(1.1f, 0.75f, avatar.CrouchAmount), 0.5f));
                if (!avatar.IsLocalSelf && !avatar.Spectating && avatar.SpitDropAt >= 0 && now >= avatar.SpitDropAt)
                {
                    avatar.SpitDropAt = -1;
                    var headPosition = avatar.Body != null ? avatar.Body.Head.position : avatar.Head.position;
                    var look = Quaternion.Euler(pitch, cameraYaw, 0);
                    SpawnSpit(pair.Key, headPosition + look * MouthOffset, look);
                }
            }
            UpdateSpitDrops(frameTime);
        }

        internal void LateTick()
        {
            // Native movement and cinematic routes may advance after Update. Carry the solved
            // rider by that final rigid seat delta without resampling network head aim or emotes.
            foreach (var avatar in avatars.Values)
            {
                var seat = avatar.PresentationSeat;
                if (seat == null || avatar.Spectating || avatar.PresentationSeatFrame != Time.frameCount) continue;
                MovingSeatPresentation.Follow(avatar.PresentationSeatPosition, avatar.PresentationSeatRotation, seat, avatar.Root.transform);
                avatar.LastRootPosition = avatar.Root.transform.position;
                avatar.MapPosition = avatar.LastRootPosition;
                avatar.PresentationSeatFrame = -1;
            }
        }

        private static Vector3 HeldEyes(Vector3 position, Quaternion rotation, byte state, bool resting, bool creeper, float crouch) =>
            !resting && state == (byte)PlayerState.InFurnitureChair ? position + rotation * Vector3.up * SeatedHeadHeight :
            position + Vector3.up * (creeper ? 1.6f - 1.32f : Mathf.Lerp(1.6f, 1.05f, crouch));

        // The held tool's native effects; #150 replays the chainsaw's cut loop on its shared item.
        private static void ToolEffects(Avatar avatar, uint peer, bool active)
        {
            var item = active || avatar.ToolItem != null ? MonMPPlugin.Instance?.Items?.HeldItemForPresentation(peer) : null;
            if (avatar.ToolItem != null && (!active || item != avatar.ToolItem)) StopTool(avatar);
            if (!active || item == null) return;
            if (NativeToolOwnsEffects(item)) { if (avatar.ToolItem != null) StopTool(avatar); return; }
            if (avatar.ToolItem != item)
            {
                avatar.ToolItem = item;
                avatar.ToolParticles = item.GetComponentsInChildren<ParticleSystem>(true).Where(p => p != null && !p.isEmitting).ToArray();
                foreach (var particles in avatar.ToolParticles) particles.Play(true);
            }
            // Newly activated item clones have not necessarily run their native SOUND.Start yet.
            // Retry the sound after its sources exist, without restarting particles or native Start.
            if (avatar.ToolSoundStarted || !ToolSoundReady(item)) return;
            if (item is Tool_SprayPaintCan)
            {
                var sound = Singleton<SOUND_Manager>.i;
                var assets = Singleton<GameAssets>.i;
                if (sound == null || sound.Tool_TirePumpAirOut == null || assets == null) return;
                var spray = item.gameObject.AddComponent<AudioSource>();
                avatar.ToolSpraySound = spray;
                spray.playOnAwake = false; spray.loop = true; spray.spatialBlend = 1;
                spray.clip = sound.Tool_TirePumpAirOut; spray.volume = 0.1f; spray.pitch = 1.1f;
                spray.dopplerLevel = 0; spray.maxDistance = 30; spray.outputAudioMixerGroup = assets.AUDIO_EffectMixer;
                if (assets.AudioSourceCustomCurve != null)
                { spray.SetCustomCurve(AudioSourceCurveType.CustomRolloff, assets.AudioSourceCustomCurve); spray.rolloffMode = AudioRolloffMode.Custom; }
                else spray.rolloffMode = AudioRolloffMode.Linear;
                spray.Play();
            }
            if (item is Tool_PesticideSolution)
            {
                var sound = Singleton<SOUND_Manager>.i;
                if (sound == null || sound.Tool_BottleSpray == null) return;
                using (SpatialSounds.At(item.transform.position, peer))
                    sound.PlayOneShot2D(sound.Tool_BottleSpray, _randomPitch: true);
            }
            var can = item.GetComponent<SOUND_Tool_WateringCan>();
            if (can != null) can.StartPoor();
            var torch = item.GetComponent<SOUND_Tool_MapTorch>();
            if (torch != null) torch.StartFlame();
            var chainsaw = item.GetComponent<SOUND_Tool_Chainsaw>();
            if (chainsaw != null) chainsaw.CutStart();
            avatar.ToolSoundStarted = true;
        }

        private static bool ToolSoundReady(InteractableItems item, bool stopping = false)
        {
            var can = item.GetComponent<SOUND_Tool_WateringCan>();
            if (can != null && !(AccessTools.Field(typeof(SOUND_Tool_WateringCan), "WaterPoor")?.GetValue(can) is AudioSource water && water != null)) return false;
            var torch = item.GetComponent<SOUND_Tool_MapTorch>();
            if (torch != null && ((!stopping && !(AccessTools.Field(typeof(SOUND_Tool_MapTorch), "MapTorch_TurnOn")?.GetValue(torch) is AudioSource ignition && ignition != null)) ||
                !(AccessTools.Field(typeof(SOUND_Tool_MapTorch), "MapTorch_Loop")?.GetValue(torch) is AudioSource flame && flame != null))) return false;
            var chainsaw = item.GetComponent<SOUND_Tool_Chainsaw>();
            return chainsaw == null || AccessTools.Field(typeof(SOUND_Tool_Chainsaw), "ChainsawCut")?.GetValue(chainsaw) is AudioSource cut && cut != null;
        }

        private static void StopTool(Avatar avatar)
        {
            var item = avatar.ToolItem;
            avatar.ToolItem = null;
            var particlesToStop = avatar.ToolParticles;
            avatar.ToolParticles = Array.Empty<ParticleSystem>();
            bool soundStarted = avatar.ToolSoundStarted;
            avatar.ToolSoundStarted = false;
            StopSpraySound(avatar);
            // A shared item can have been taken/equipped locally since this avatar last used it.
            // Its native coroutine now owns these same particles and sources (#339); forget our
            // stale cache without stopping a pour/flame/cut that the native tool already started.
            if (item != null && NativeToolOwnsEffects(item)) return;
            foreach (var particles in particlesToStop) if (particles != null) particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (item == null || !soundStarted || !ToolSoundReady(item, stopping: true)) return;
            var can = item.GetComponent<SOUND_Tool_WateringCan>();
            if (can != null) can.StopPoor();
            var torch = item.GetComponent<SOUND_Tool_MapTorch>();
            if (torch != null) torch.StopFlame();
            var chainsaw = item.GetComponent<SOUND_Tool_Chainsaw>();
            if (chainsaw != null) chainsaw.CutStop();
        }

        private static void StopSpraySound(Avatar avatar)
        {
            var spray = avatar.ToolSpraySound; avatar.ToolSpraySound = null;
            if (spray == null) return;
            spray.Stop(); UnityEngine.Object.Destroy(spray);
        }

        private static bool NativeToolOwnsEffects(InteractableItems item)
        {
            var gameplay = MonMPPlugin.Instance?.Gameplay;
            return gameplay != null && gameplay.Player != null && gameplay.Player.Get_ActiveTool == item;
        }

        // Called while the paired watering fixture's real native pour is running. Seed the old
        // observer cache only; the native item, coroutine, particles and source stay unchanged.

#if MONMP_DEVELOPMENT
        internal static void TestLocalToolCleanup(Tool_WateringCan item)
        {
            var sound = item.GetComponent<SOUND_Tool_WateringCan>();
            var source = sound == null ? null : AccessTools.Field(typeof(SOUND_Tool_WateringCan), "WaterPoor").GetValue(sound) as AudioSource;
            var particles = item.GetComponentsInChildren<ParticleSystem>(true).Where(p => p != null && p.isEmitting).ToArray();
            if (!NativeToolOwnsEffects(item) || source == null || !source.isPlaying || particles.Length == 0)
                throw new InvalidOperationException("Tool cleanup #339 requires a native locally equipped watering can with an active pour and source.");
            var stale = new Avatar { ToolItem = item, ToolParticles = particles };
            StopTool(stale);
            if (!source.isPlaying || particles.Any(p => !p.isEmitting) || stale.ToolItem != null || stale.ToolParticles.Length != 0)
                throw new InvalidOperationException("Stale remote cleanup stopped a native local pour or retained the old item cache (#339).");
            Debug.Log("MonMP tool cleanup #339: stale remote cache cleared while native local WaterTap/WaterPoor stayed active.");
        }
#endif


        // Native bar stools set the walker on the floor at standing eye height; a sitter there is
        // placed with the hips on the stool's seat pan instead of by head height.
        private static Furniture_Chair[] chairs = Array.Empty<Furniture_Chair>();
        private static float nextChairScan;
        private static readonly System.Reflection.FieldInfo? ChairSeatLocation = HarmonyLib.AccessTools.Field(typeof(Furniture_Chair), "SeatLocation");
        internal static Vector3? StoolSurface(Vector3 seatPosition)
        {
            if (Time.unscaledTime >= nextChairScan) { chairs = UnityEngine.Object.FindObjectsOfType<Furniture_Chair>(); nextChairScan = Time.unscaledTime + 10f; }
            foreach (var chair in chairs)
            {
                if (chair == null || !(ChairSeatLocation?.GetValue(chair) is Transform location) || Vector3.Distance(location.position, seatPosition) > 0.3f) continue;
                // The stool is the nearest "Stool" ancestor of the seat location or the chair: the
                // bar's own chairs keep both a level or two below it.
                var stool = StoolOf(location) ?? StoolOf(chair.transform);
                if (stool == null) return null;
                return SharedExtraSeats.StoolSurface(stool);
            }
            return null;
        }

        private static Transform? StoolOf(Transform? t)
        {
            for (int i = 0; t != null && i < 4; i++, t = t.parent)
                if (t.name.IndexOf("Stool", StringComparison.OrdinalIgnoreCase) >= 0) return t;
            return null;
        }

        // The bed a sleeper lies in, near their eyes: the root turns along the bed's long axis
        // towards the foot end, whichever way the native sleep position left the camera. Beds in
        // vehicles (the back seat) curl the sleeper up.
        internal static Furniture_Bed? NearestBed(Vector3 eyes)
        {
            if (Time.unscaledTime >= nextBedScan) { beds = UnityEngine.Object.FindObjectsOfType<Furniture_Bed>(); nextBedScan = Time.unscaledTime + 10f; }
            Furniture_Bed? best = null;
            float bestDistance = 2.5f;
            foreach (var bed in beds)
            {
                if (bed == null) continue;
                float distance = Vector3.Distance(bed.SleepPosition != null ? SleepingEyes(bed.SleepPosition) : bed.transform.position, eyes);
                if (distance < bestDistance) { best = bed; bestDistance = distance; }
            }
            return best;
        }
        internal static Vector3 SleepingEyes(Transform sleepPosition) => SleepingBedPose.Eyes(sleepPosition);
        private static bool TryBed(Furniture_Bed? best, Vector3 eyes, float cameraYaw, out float yaw, out bool curl, out float width)
        {
            yaw = cameraYaw; curl = false; width = 0.8f;
            if (best == null) return false;
            var t = best.transform;
            if (!bedShapes.TryGetValue(best, out var shape))
            {
                // The longest level side of the biggest box on the bed; the renderers' box otherwise.
                Vector3 axis = Vector3.zero, center = t.position;
                float volume = 0;
                foreach (var box in best.GetComponentsInChildren<BoxCollider>(true))
                {
                    var size = Vector3.Scale(box.size, box.transform.lossyScale);
                    float v = Mathf.Abs(size.x * size.y * size.z);
                    if (v <= volume) continue;
                    volume = v;
                    center = box.transform.TransformPoint(box.center);
                    float longest = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        var side = Vector3.ProjectOnPlane(box.transform.rotation * (i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward) * Mathf.Abs(size[i]), Vector3.up);
                        if (side.magnitude > longest) { longest = side.magnitude; axis = side.normalized; }
                    }
                    var across = Vector3.Cross(Vector3.up, axis);
                    width = Mathf.Abs(Vector3.Dot(box.transform.right, across) * size.x) +
                        Mathf.Abs(Vector3.Dot(box.transform.up, across) * size.y) + Mathf.Abs(Vector3.Dot(box.transform.forward, across) * size.z);
                }
                if (axis == Vector3.zero)
                {
                    var renderers = best.GetComponentsInChildren<Renderer>().Where(r => !(r is ParticleSystemRenderer)).ToArray();
                    if (renderers.Length > 0)
                    {
                        var bounds = renderers[0].bounds;
                        foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                        center = bounds.center;
                        axis = bounds.size.x >= bounds.size.z ? Vector3.right : Vector3.forward;
                        width = Mathf.Min(bounds.size.x, bounds.size.z);
                    }
                }
                shape = (t.InverseTransformDirection(axis), t.InverseTransformPoint(center), Mathf.Clamp(width, 0.6f, 2.2f));
                bedShapes[best] = shape;
            }
            curl = best is Furniture_Bed_BackSeat;
            width = shape.Width;
            var along = Vector3.ProjectOnPlane(t.TransformDirection(shape.Axis), Vector3.up);
            if (along.sqrMagnitude < 1e-4f) return false;
            along.Normalize();
            // The eyes are at the head end, so the foot end is across the middle from them.
            float toMiddle = Vector3.Dot(t.TransformPoint(shape.Center) - eyes, along);
            // Near the middle the eyes can't tell the ends apart; the sender's yaw already points to the feet.
            if (Mathf.Abs(toMiddle) < 0.35f) toMiddle = Vector3.Dot(Quaternion.Euler(0, cameraYaw, 0) * Vector3.forward, along);
            // Native sleep sets the walker to SleepPosition's rotation, which lays it down: its up runs from
            // the feet to the head. That is the bed's own answer; the box middle can sit off a headboard or
            // frame collider and put the body through the wall (rt-12 report).
            if (best.SleepPosition != null)
            {
                var feet = -Vector3.ProjectOnPlane(best.SleepPosition.up, Vector3.up);
                if (feet.sqrMagnitude > 0.25f) toMiddle = Vector3.Dot(feet, along);
            }
            if (toMiddle < 0) along = -along;
            yaw = Quaternion.LookRotation(along).eulerAngles.y;
            return true;
        }

        private static StockAvatarBody.Mode BodyMode(byte state)
        {
            switch ((PlayerState)state)
            {
                case PlayerState.InVehicle_Driver: return StockAvatarBody.Mode.Drive;
                case PlayerState.InVehicle_Passenger: return StockAvatarBody.Mode.Sit;
                case PlayerState.InFurnitureChair: return StockAvatarBody.Mode.Chair;
                case PlayerState.InCreeper: return StockAvatarBody.Mode.Lie;
                default: return StockAvatarBody.Mode.Stand;
            }
        }

        private static void TryAttachBody(Avatar avatar)
        {
            if (avatar.Body != null)
            {
                // Coalesce rapid selections and cap remote reconstruction work. The
                // most recent authenticated pose remains the desired appearance.
                if (Time.unscaledTime < avatar.NextModelBuild || Time.unscaledTime - avatar.ModelChangedAt < .15f) return;
            }
            avatar.NextBodyAttempt = Time.unscaledTime + BodyRetrySeconds;
            // Peers can join while a menu-joining guest is still loading; wait for the gameplay scene.
            if (UnityEngine.Object.FindObjectOfType<Gameplay>() == null) { avatar.BodyStatus = "mannequin (no gameplay scene)"; return; }
            var model = PlayerModelPolicy.Key(avatar.Model);
            var body = StockAvatarBody.TryCreateAppearance(avatar.Root.transform, AppearanceColor(avatar.Appearance, avatar.Shirt), avatar.Appearance, out var reason);
            if (body == null)
            {
                avatar.BodyStatus = (avatar.Body != null ? "previous body" : "mannequin") + " (" + reason + ")";
                Debug.LogWarning("MonMP remote avatar kept its previous presentation: " + reason); return;
            }
            avatar.BodyStatus = reason;
            Debug.Log("MonMP remote avatar body: " + reason + (body.Walks ? "" : " (no walk)"));
            body.SetShadowOnly(avatar.LocalShadowOnly);
            // Build first: failure keeps the visible body, and shared cloth/skin
            // geometry stays acquired across a successful replacement.
            avatar.Body?.Destroy();
            avatar.Body = body;
            avatar.BodyModel = avatar.Model; avatar.BodyAppearance = avatar.Appearance; avatar.NextModelBuild = Time.unscaledTime + 1f;
#if MONMP_DEVELOPMENT
            avatar.TestModelBuilds++;
#endif
            avatar.NextBodyAttempt = Time.unscaledTime;
            if (avatar.Spectating)
            {
                body.EnsureSpectatorHead();
                body.BeginSpectatorHeadOnly();
            }
            SetMannequinFallback(avatar);
            try { avatar.Props ??= RemoteHandProps.Create(avatar.Root.transform); }
            catch (Exception ex) { Debug.LogWarning("MonMP remote avatar hand props unavailable: " + ex.Message); }
            RefreshHeadOnlyPresentation(avatar);
            // The beam matches the local player's own flashlight.
            var native = avatar.Props?.Flashlight?.SourceLight;
            if (native != null)
            {
                avatar.Flashlight.type = native.type; avatar.Flashlight.color = native.color;
                avatar.Flashlight.range = Mathf.Max(native.range, 10f); avatar.Flashlight.spotAngle = native.spotAngle;
                avatar.Flashlight.intensity = Mathf.Max(native.intensity, 1f); avatar.Flashlight.cookie = native.cookie;
            }
        }

        private static void Animate(Avatar avatar)
        {
            float crouch = avatar.CrouchAmount;
            float walk = avatar.WalkAmount;
            float phase = avatar.GaitPhase;
            float swing = Mathf.Sin(phase) * walk;
            float oppositeSwing = -swing;
            bool driving = avatar.State == (byte)PlayerState.InVehicle_Driver;

            avatar.Pelvis.localPosition = new Vector3(0, Mathf.Lerp(0.86f, 0.78f, crouch), 0);
            avatar.TorsoPivot.localPosition = new Vector3(0, Mathf.Lerp(1f, 0.86f, crouch), 0);
            avatar.TorsoPivot.localRotation = Quaternion.Euler(-10f * crouch, 0, 0);
            avatar.Torso.localRotation = Quaternion.identity;
            avatar.Head.localPosition = new Vector3(0, Mathf.Lerp(1.68f, 1.05f, crouch), 0);
            avatar.Neck.localPosition = new Vector3(0, Mathf.Lerp(1.52f, 0.97f, crouch), 0);

            float hipBend = -32f * crouch;
            float kneeBend = 58f * crouch;
            avatar.LeftHip.localRotation = Quaternion.Euler(hipBend + swing * 30f, 0, 0);
            avatar.RightHip.localRotation = Quaternion.Euler(hipBend + oppositeSwing * 30f, 0, 0);
            avatar.LeftKnee.localRotation = Quaternion.Euler(kneeBend + Mathf.Max(0, -swing) * 24f, 0, 0);
            avatar.RightKnee.localRotation = Quaternion.Euler(kneeBend + Mathf.Max(0, -oppositeSwing) * 24f, 0, 0);

            float armBase = driving ? -52f : -10f * crouch;
            float elbowBase = driving ? -38f : 8f * crouch;
            avatar.LeftArm.localRotation = Quaternion.Euler(armBase - oppositeSwing * 25f, 0, -2f);
            avatar.RightArm.localRotation = Quaternion.Euler(armBase - swing * 25f, 0, 2f);
            avatar.LeftElbow.localRotation = Quaternion.Euler(elbowBase, 0, 0);
            avatar.RightElbow.localRotation = Quaternion.Euler(elbowBase, 0, 0);
        }

        private static void AimRightHandAt(Avatar avatar, Vector3 targetWorld)
        {
            const float upperLength = 0.38f;
            const float forearmLength = 0.34f;
            var shoulder = avatar.RightArm.localPosition;
            var offset = avatar.TorsoPivot.InverseTransformPoint(targetWorld) - shoulder;
            float rawDistance = offset.magnitude;
            // A stale or divergent remote item should not pull the arm away from the mannequin.
            if (rawDistance < 0.02f || rawDistance > 1.5f) return;

            var direction = offset / rawDistance;
            float distance = Mathf.Clamp(rawDistance, Mathf.Abs(upperLength - forearmLength) + 0.001f,
                upperLength + forearmLength - 0.001f);
            float along = (upperLength * upperLength - forearmLength * forearmLength + distance * distance) / (2f * distance);
            float bend = Mathf.Sqrt(Mathf.Max(0, upperLength * upperLength - along * along));
            var pole = Vector3.forward - direction * Vector3.Dot(Vector3.forward, direction);
            if (pole.sqrMagnitude < 0.001f) pole = Vector3.right - direction * Vector3.Dot(Vector3.right, direction);
            pole.Normalize();

            var elbowVector = direction * along + pole * bend;
            var upperRotation = Quaternion.FromToRotation(Vector3.down, elbowVector.normalized);
            var forearmDirection = Quaternion.Inverse(upperRotation) * (direction * distance - elbowVector);
            avatar.RightArm.localRotation = upperRotation;
            avatar.RightElbow.localRotation = Quaternion.FromToRotation(Vector3.down, forearmDirection.normalized);
        }

        internal void Caption(uint peer, string text)
        {
            if (!avatars.TryGetValue(peer, out var avatar)) return;
            avatar.IsChatCaption = true;
            avatar.VoiceText = text; avatar.VoiceUntil = Time.unscaledTime + ChatMessage.CaptionSeconds;
        }

        public void DrawNames(Camera? camera)
        {
            if (camera == null) return;
            // GUI.skin is only available during OnGUI; initialize here and reuse on later passes.
            var style = nameStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, richText = false, fontSize = 17, fontStyle = FontStyle.Bold };
            foreach (var avatar in avatars.Values)
            {
                if (!avatar.HasPose) continue;
                var rootPosition = avatar.Root.transform.position;
                var headPosition = HeadPosition(avatar);
                float ordinaryTagHeight = avatar.Rest != PlayerRest.None ? 0.9f : Mathf.Lerp(2.1f, 1.5f, avatar.CrouchAmount);
                var tagPosition = SpectatorPresentationPolicy.NameTagPosition(rootPosition, headPosition, avatar.Spectating, ordinaryTagHeight);
                float distance = Vector3.Distance(camera.transform.position,
                    SpectatorPresentationPolicy.NameTagDistanceOrigin(rootPosition, headPosition, avatar.Spectating));
                var point = MonMPPlugin.Instance?.ProjectPresentation(camera, tagPosition) ?? camera.WorldToScreenPoint(tagPosition);
                if (distance > 150 || point.z <= 0) continue;
                var nameRect = new Rect(point.x - 150, Screen.height - point.y - 18, 300, 36);
                Color labelColor = GUI.color;
                GUI.color = new Color(0, 0, 0, labelColor.a * 0.9f);
                GUI.Label(new Rect(nameRect.x + 1, nameRect.y + 1, nameRect.width, nameRect.height), avatar.Name, style);
                GUI.color = labelColor;
                GUI.Label(nameRect, avatar.Name, style);
                if (avatar.VoiceText.Length > 0 && Time.unscaledTime < avatar.VoiceUntil)
                {
                    if (avatar.IsChatCaption)
                        GUI.Label(new Rect(point.x - 210, Screen.height - point.y + 5, 420, 70), avatar.VoiceText,
                            captionStyle ??= new GUIStyle(style) { wordWrap = true });
                    else
                        // Keep native voice lines ("Oups!", "Sacre!") quoted in their original rectangle.
                        GUI.Label(new Rect(point.x - 150, Screen.height - point.y + 5, 300, 30), avatar.QuotedVoiceText, style);
                }
                else if (avatar.Rest == PlayerRest.Sleeping)
                    GUI.Label(new Rect(point.x - 150, Screen.height - point.y + 5, 300, 30), "Zzz", style);
            }
        }

        // Each posed avatar's name and its head (the stock body's where it has one, seated or not).
        internal IEnumerable<(string Name, Vector3 Head)> NameTags()
        {
            foreach (var avatar in avatars.Values)
                if (avatar.HasPose) yield return (avatar.Name, HeadPosition(avatar));
        }

        // Spectator cameras may follow only an active ordinary peer. Spectating peers keep their
        // feet/root anchored while their head follows the camera, so they are not valid targets.
        internal Vector3? HeadPosition(uint peer) => avatars.TryGetValue(peer, out var avatar) &&
            avatar.HasPose && !avatar.Spectating && avatar.Rest != PlayerRest.Down && avatar.Root.activeInHierarchy &&
            Time.unscaledTime - avatar.ReceivedAt <= 2f ? HeadPosition(avatar) : (Vector3?)null;

        // Spectator cameras use HeadPosition(uint), which deliberately rejects spectators as
        // targets. Maps use this separate feet anchor, held at its last ordinary rendered value.
        internal Vector3? MapPosition(uint peer) => avatars.TryGetValue(peer, out var avatar) &&
            avatar.HasPose && avatar.Root.activeInHierarchy && PlayerMarkerFreshness.TryGetOpacity(Time.unscaledTime, avatar.ReceivedAt, out _)
                ? avatar.MapPosition : (Vector3?)null;


#if MONMP_DEVELOPMENT
        internal Vector3 TestHeadPosition(uint peer) => avatars.TryGetValue(peer, out var avatar) ? HeadPosition(avatar) : Vector3.zero;


        internal PlayerPose? TestRawPose(uint peer) => avatars.TryGetValue(peer, out var avatar) && avatar.HasPose
            ? avatar.LastPose : (PlayerPose?)null;


        internal bool TestIsSpectating(uint peer) => avatars.TryGetValue(peer, out var avatar) && avatar.Spectating;
#endif


        // Stable compact snapshot for the guarded native visual probe. `root` remains the feet
        // anchor; `head` is the independently posed visible head while spectating.

#if MONMP_DEVELOPMENT
        internal string TestSpectatorVisual(uint peer)
        {
            if (!avatars.TryGetValue(peer, out var avatar)) return "missing";
            var root = avatar.Root.transform.position;
            var head = HeadPosition(avatar);
            var renderers = avatar.Root.GetComponentsInChildren<Renderer>(true);
            int enabled = 0, headEnabled = 0, headRenderers = 0;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                bool selected = avatar.HeadOnlyPresentation?.IsHeadRenderer(renderer) == true;
                if (selected) headRenderers++;
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                enabled++;
                if (selected) headEnabled++;
            }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "spectating={0};root={1:R},{2:R},{3:R};head={4:R},{5:R},{6:R};body={7};mannequin={8};enabledRenderers={9};headRenderers={10};headEnabled={11};headTriangles={12}",
                avatar.Spectating, root.x, root.y, root.z, head.x, head.y, head.z,
                avatar.Body != null, avatar.Mannequin.activeInHierarchy, enabled, headRenderers, headEnabled,
                avatar.Body != null ? avatar.Body.SpectatorHeadTriangleCount : 0);
        }
#endif


        // Shadow-state snapshot for the guarded first-person/third-person native probe.

#if MONMP_DEVELOPMENT
        internal string TestLocalView(uint peer)
        {
            if (!avatars.TryGetValue(peer, out var avatar)) return "missing";
            int off = 0, on = 0, twoSided = 0, shadowsOnly = 0, enabled = 0;
            foreach (var renderer in avatar.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (renderer.enabled && renderer.gameObject.activeInHierarchy) enabled++;
                switch (renderer.shadowCastingMode)
                {
                    case UnityEngine.Rendering.ShadowCastingMode.On: on++; break;
                    case UnityEngine.Rendering.ShadowCastingMode.TwoSided: twoSided++; break;
                    case UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly: shadowsOnly++; break;
                    default: off++; break;
                }
            }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "localShadowOnly={0};bodyShadowOnly={1};mannequinShadowOnly={2};enabledRenderers={3};shadowOff={4};shadowOn={5};shadowTwoSided={6};shadowOnly={7}",
                avatar.LocalShadowOnly, avatar.Body?.IsShadowOnly ?? false, avatar.MannequinShadowCasting?.IsShadowOnly ?? false,
                enabled, off, on, twoSided, shadowsOnly);
        }
#endif


        public void Remove(uint id)
        {
            if (!avatars.TryGetValue(id, out var avatar)) return;
            StopTool(avatar);
            avatar.HeldVisual?.Dispose();
            avatar.Props?.Destroy();
            avatar.HeadOnlyPresentation?.Restore();
            avatar.Body?.RestoreSpectatorHead();
            avatar.Body?.Destroy();
            avatar.MannequinShadowCasting?.Restore();
            avatar.Root.SetActive(false);
            UnityEngine.Object.Destroy(avatar.Root);
            UnityEngine.Object.Destroy(avatar.Material);
            UnityEngine.Object.Destroy(avatar.ShirtMaterial);
            avatars.Remove(id);
        }

        // A seated player's pose is the seat location itself, so a chair is taken when any remote
        // player in a chair state sits within the radius of it.
        internal bool SeatTaken(Vector3 seat, float radius)
        {
            foreach (var avatar in avatars.Values)
                if (avatar.HasPose && avatar.State == (byte)PlayerState.InFurnitureChair && Vector3.Distance(avatar.To, seat) <= radius) return true;
            return false;
        }

#if MONMP_DEVELOPMENT
        internal float TestHeadHeight(uint id) => avatars.TryGetValue(id, out var avatar) ? (avatar.Body != null ? avatar.Body.HeadHeight : avatar.Head.localPosition.y) : 0f;


        internal string TestBody(uint id) => avatars.TryGetValue(id, out var avatar) ? avatar.BodyStatus : "none";


        internal bool TestStockBody(uint id) => avatars.TryGetValue(id, out var avatar) && avatar.Body != null;
        internal int TestBodyModel(uint id) => avatars.TryGetValue(id, out var avatar) && avatar.Body != null ? avatar.BodyModel : -1;
        internal int TestModelBuilds(uint id) => avatars.TryGetValue(id, out var avatar) ? avatar.TestModelBuilds : 0;
        internal bool TestModelFabric(uint id) => avatars.TryGetValue(id, out var avatar) && avatar.Body?.FabricReady == true && avatar.Body.TestSleeveMaskApplied;
        internal bool TestNativeTexture(uint id) => avatars.TryGetValue(id, out var avatar) && avatar.Body?.TestNativeShirtTexture == true;
        internal int TestModelPropIdentity(uint id) => avatars.TryGetValue(id, out var avatar) && avatar.Props?.Map?.Object != null ? avatar.Props.Map.Object.GetInstanceID() : 0;


        internal string TestEmotes(uint id) => avatars.TryGetValue(id, out var avatar) ? avatar.EmoteLog : "none";


        internal float TestSorry(uint id) => avatars.TryGetValue(id, out var avatar) ? avatar.SorryAmount : 0f;
#endif

        // The raised Sorry hand: the right hand level with the head or above.

#if MONMP_DEVELOPMENT
        internal bool TestHandRaised(uint id) =>
            avatars.TryGetValue(id, out var avatar) && avatar.Body != null && avatar.Body.RightHand.position.y > avatar.Body.Head.position.y - 0.2f;
#endif

        internal bool ToolActive(uint id) => avatars.TryGetValue(id, out var avatar) && avatar.HasPose &&
            (avatar.Hands & PlayerHands.Tool) != 0 && Time.unscaledTime - avatar.ReceivedAt <= 2f;

#if MONMP_DEVELOPMENT
        internal string TestHandProps(uint id) => avatars.TryGetValue(id, out var avatar) ? avatar.Props?.Status ?? "none" : "none";


        internal PlayerHands TestHands(uint id) => avatars.TryGetValue(id, out var avatar) ? avatar.Hands : PlayerHands.None;


        internal bool TestFlashlightInHand(uint id) => avatars.TryGetValue(id, out var avatar) && avatar.FlashAmount > 0.5f && avatar.Props?.Flashlight?.Object.activeSelf == true;
#endif

        // Resting: the stock body lies with its head under a metre above the ground it rests on.

#if MONMP_DEVELOPMENT
        internal PlayerRest TestRest(uint id) => avatars.TryGetValue(id, out var avatar) ? avatar.Rest : PlayerRest.None;


        internal bool TestLying(uint id) =>
            avatars.TryGetValue(id, out var avatar) && avatar.Body != null && avatar.Rest != PlayerRest.None &&
            Mathf.Abs(avatar.Body.Head.position.y - avatar.Body.LeftFoot.position.y) < 0.6f;


        internal string TestVoiceText(uint id) => avatars.TryGetValue(id, out var avatar) && Time.unscaledTime < avatar.VoiceUntil ? avatar.VoiceText : "";
#endif


        // Renders the stock body in each presentation (the next Update restores the live pose).

#if MONMP_DEVELOPMENT
        internal string TestPreviewPoses(uint id, string directory)
        {
            if (!avatars.TryGetValue(id, out var avatar) || avatar.Body == null) return "no stock body";
            var body = avatar.Body;
            var props = avatar.Props;
            var root = avatar.Root.transform;
            Diagnostics.HeldItemPresentationProbe.WriteNativeAnchors(directory);
            var written = new List<string>();
            var side = Diagnostics.AvatarPreviewCapture.View.Side;
            var back = Diagnostics.AvatarPreviewCapture.View.Back;
            void Shot(string name, StockAvatarBody.Mode mode, float walk, float crouch, StockAvatarBody.Overlay overlay, StockAvatarBody.Goals goals,
                Diagnostics.AvatarPreviewCapture.View view = Diagnostics.AvatarPreviewCapture.View.Front, float lie = -1)
            {
                bool lying = mode == StockAvatarBody.Mode.Lie || mode == StockAvatarBody.Mode.Down || mode == StockAvatarBody.Mode.Curl;
                body.SetLieBlend(lie >= 0 ? lie : lying ? 1f : 0f);
                body.Present(mode, walk, 1.4f, crouch, overlay, lie >= 0 ? 0.1f : 0.3f);
                body.Solve(goals, 1f);
                props?.Update(body, overlay.Map, overlay.Console, overlay.Flashlight, overlay.Joint, overlay.Smoking, true);
                Diagnostics.AvatarPreviewCapture.SaveBitmap(root, System.IO.Path.Combine(directory, "avatar-pose-" + name + ".bmp"), view);
                // Hidden between shots, so leaving the smoking shot does not play an exhale.
                props?.Update(body, 0, 0, 0, 0, 0, false);
                written.Add(name);
            }
            StockAvatarBody.Overlay Look(float pitch) => StockAvatarBody.Overlay.Look(pitch);
            var stand = StockAvatarBody.Mode.Stand;
            var none = new StockAvatarBody.Goals();
            Shot("stand", stand, 0, 0, Look(0), none);
            Shot("walk", stand, 1, 0, Look(0), none);
            Shot("crouch", stand, 0, 1, Look(0), none);
            Shot("look-down", stand, 0, 0, Look(45), none);
            var lookLeft = Look(0); lookLeft.HeadYaw = -60;
            Shot("look-left", stand, 0, 0, lookLeft, none);
            Shot("hold", stand, 0, 0, Look(20), new StockAvatarBody.Goals { Held = root.position + root.rotation * new Vector3(0.3f, 1.15f, 0.55f) });
            Shot("carry-wide", stand, 0, 0, Look(20), new StockAvatarBody.Goals { Held = root.position + root.rotation * new Vector3(0, 1.0f, 0.45f), HeldWidth = 0.6f }, side);
            var sorry = Look(0); sorry.Sorry = 1;
            Shot("sorry", stand, 0, 0, sorry, none);
            var spit = Look(0); spit.SpitTime = 1.6f;
            Shot("spit-windup", stand, 0, 0, spit, none, side);
            var map = Look(25); map.Map = 1;
            Shot("map", stand, 0, 0, map, none, back);
            var console = Look(35); console.Console = 1;
            Shot("console", stand, 0, 0, console, none, side);
            var flashlight = Look(10); flashlight.Flashlight = 1;
            Shot("flashlight", stand, 0, 0, flashlight, none, side);
            var joint = Look(0); joint.Joint = 1;
            Shot("joint", stand, 0, 0, joint, none, side);
            var smoking = Look(0); smoking.Joint = 1; smoking.Smoking = 1;
            Shot("smoking", stand, 0, 0, smoking, none, side);
            var vomit = Look(0); vomit.VomitTime = 1f; vomit.VomitSeconds = 3f;
            Shot("vomit", stand, 0, 0, vomit, none, side);
            var interact = Look(10); interact.InteractTime = StockAvatarBody.InteractSeconds * 0.5f;
            Shot("interact", stand, 0, 0, interact, none, side);
            var pet = Look(30); pet.PetTime = StockAvatarBody.PetSeconds * 0.5f;
            Shot("pet", stand, 0, 0, pet, none, side);
            Shot("push", stand, 0.6f, 0, Look(10), new StockAvatarBody.Goals
            {
                GripLeft = root.position + root.rotation * new Vector3(-0.27f, 0.8f, 0.45f),
                GripRight = root.position + root.rotation * new Vector3(0.27f, 0.8f, 0.45f), Push = true
            }, side);
            Shot("sit", StockAvatarBody.Mode.Sit, 0, 0, Look(0), none);
            Shot("chair", StockAvatarBody.Mode.Chair, 0, 0, Look(0), none);
            Shot("chair-side", StockAvatarBody.Mode.Chair, 0, 0, Look(0), none, side);
            Shot("ground", StockAvatarBody.Mode.Ground, 0, 0, Look(0), none);
            Shot("ground-side", StockAvatarBody.Mode.Ground, 0, 0, Look(0), none, side);
            Shot("sleep", StockAvatarBody.Mode.Lie, 0, 0, Look(0), none, side);
            Shot("curl", StockAvatarBody.Mode.Curl, 0, 0, Look(0), none, side);
            Shot("curl-top", StockAvatarBody.Mode.Curl, 0, 0, Look(0), none);
            Shot("down", StockAvatarBody.Mode.Down, 0, 0, Look(0), none, side);
            Shot("falling", StockAvatarBody.Mode.Down, 0, 0, Look(0), none, side, 0.45f);
            var jump = Look(0); jump.Air = 1;
            Shot("jump", stand, 0.5f, 0, jump, none, side);
            Shot("crouch-walk", stand, 1, 1, Look(0), none, side);
            body.SetLieBlend(0);

            // Driving with both hands on a stand-in wheel turned 30 degrees.
            body.Present(StockAvatarBody.Mode.Drive, 0, 0, 0, Look(0), 0.3f);
            var wheel = new GameObject("PreviewWheel").transform;
            wheel.SetParent(root, false);
            wheel.position = body.Head.position + root.rotation * new Vector3(0, -0.5f, 0.42f);
            wheel.rotation = root.rotation * Quaternion.Euler(-25f, 0, 0);
            var rim = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.DestroyImmediate(rim.GetComponent<Collider>());
            rim.transform.SetParent(wheel, false);
            rim.transform.localRotation = Quaternion.Euler(90f, 0, 0);
            rim.transform.localScale = new Vector3(0.36f, 0.01f, 0.36f);
            var grips = WheelGrips(wheel, 30f, root.right);
            Shot("drive", StockAvatarBody.Mode.Drive, 0, 0, Look(0), new StockAvatarBody.Goals { GripLeft = grips.Left, GripRight = grips.Right });
            VehicleSeatPosePresentation.ForgetWheel(wheel);
            UnityEngine.Object.DestroyImmediate(wheel.gameObject);

            // The left foot up on a 15 cm step: that knee bends, the other leg stays on the ground.
            body.Present(stand, 0, 0, 0, Look(0), 0.3f);
            var leftFoot = root.InverseTransformPoint(body.LeftFoot.position);
            var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
            step.transform.SetParent(root, false);
            step.transform.localPosition = new Vector3(leftFoot.x - 0.1f, 0.075f, 0);
            step.transform.localScale = new Vector3(0.3f, 0.15f, 0.7f);
            Physics.SyncTransforms();
            Shot("step", stand, 0, 0, Look(0), new StockAvatarBody.Goals { Feet = true });
            UnityEngine.Object.DestroyImmediate(step);
            body.ResetFeet();
            props?.Update(body, 0, 0, 0, 0, 0, false);
            if (props != null) System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "avatar-hand-props.txt"), props.Describe() + "\nsources: " + body.Sources + "\n");
            return string.Join(",", written);
        }


        internal int TestShirt(uint id) => avatars.TryGetValue(id, out var avatar) ? avatar.Shirt : -1;


        internal Vector3? TestHips(uint id) => avatars.TryGetValue(id, out var avatar) && avatar.Body != null ? avatar.Body.HipsPosition : (Vector3?)null;
#endif

        internal Vector3? Position(uint id) =>
            avatars.TryGetValue(id, out var avatar) && avatar.HasPose && avatar.Root != null ? avatar.Root.transform.position : (Vector3?)null;
        internal Vector3? Nearest(Vector3 from)
        {
            Vector3? best = null; float bestDistance = float.MaxValue;
            foreach (var avatar in avatars.Values)
            {
                if (!avatar.HasPose || avatar.Root == null) continue;
                float distance = (avatar.Root.transform.position - from).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = avatar.Root.transform.position; }
            }
            return best;
        }

#if MONMP_DEVELOPMENT
        internal Transform? TestRoot(uint id) => avatars.TryGetValue(id, out var avatar) ? avatar.Root.transform : null;
#endif


        public void Dispose()
        {
            var failures = new List<Exception>();
            void Attempt(string action, Action run)
            {
                try { run(); }
                catch (Exception ex) { failures.Add(new InvalidOperationException("Remote players cleanup: " + action, ex)); }
            }
            try
            {
                foreach (var drop in spitDrops)
                    if (drop.Drop != null) Attempt("spit drop", () => UnityEngine.Object.Destroy(drop.Drop));
                foreach (var pair in avatars)
                {
                    var avatar = pair.Value;
                    var item = avatar.ToolItem;
                    var particlesToStop = avatar.ToolParticles;
                    avatar.ToolItem = null;
                    avatar.ToolParticles = Array.Empty<ParticleSystem>();
                    Attempt("spray sound " + pair.Key, () => StopSpraySound(avatar));
                    Attempt("tool " + pair.Key, () =>
                    {
                        if (item != null && NativeToolOwnsEffects(item)) return;
                        foreach (var particles in particlesToStop)
                            if (particles != null) Attempt("tool particles " + pair.Key,
                                () => particles.Stop(true, ParticleSystemStopBehavior.StopEmitting));
                        if (item == null) return;
                        Attempt("watering sound " + pair.Key, () => item.GetComponent<SOUND_Tool_WateringCan>()?.StopPoor());
                        Attempt("torch sound " + pair.Key, () => item.GetComponent<SOUND_Tool_MapTorch>()?.StopFlame());
                        Attempt("chainsaw sound " + pair.Key, () => item.GetComponent<SOUND_Tool_Chainsaw>()?.CutStop());
                    });
                    Attempt("held visual " + pair.Key, () => avatar.HeldVisual?.Dispose());
                    Attempt("props " + pair.Key, () => avatar.Props?.Destroy());
                    Attempt("head-only renderer state " + pair.Key, () => avatar.HeadOnlyPresentation?.Restore());
                    Attempt("spectator body offset " + pair.Key, () => avatar.Body?.RestoreSpectatorHead());
                    Attempt("body " + pair.Key, () => avatar.Body?.Destroy());
                    Attempt("mannequin shadow modes " + pair.Key, () => avatar.MannequinShadowCasting?.Restore());
                    Attempt("deactivate root " + pair.Key, () => avatar.Root.SetActive(false));
                    Attempt("root " + pair.Key, () => UnityEngine.Object.Destroy(avatar.Root));
                    Attempt("material " + pair.Key, () => UnityEngine.Object.Destroy(avatar.Material));
                    Attempt("shirt material " + pair.Key, () => UnityEngine.Object.Destroy(avatar.ShirtMaterial));
                }
            }
            finally
            {
                spitDrops.Clear(); VehicleSeatPosePresentation.Clear(); barrowShapes.Clear();
                barrows = Array.Empty<Wheelbarrow>(); bedShapes.Clear();
                beds = Array.Empty<Furniture_Bed>(); avatars.Clear();
            }
            if (failures.Count != 0) throw new AggregateException("Remote players cleanup encountered failures.", failures);
        }
    }
}
