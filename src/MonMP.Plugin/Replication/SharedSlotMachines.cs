using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    internal sealed class SharedSlotMachines : IDisposable
    {
        private sealed class ButtonBinding
        {
            public SlotMachine_Button.ButtonType Type;
            public Transform Button = null!;
        }
        private sealed class Binding
        {
            public ulong Id;
            public string Identity = "";
            public SlotMachine Machine = null!;
            public Store_Bar Store = null!;
            public ButtonBinding[] Buttons = Array.Empty<ButtonBinding>();
            public SlotMachineState Original = null!;
            public SlotMachineState Current = null!;
            // Guest presentation of the host's spin.
            public Transform?[] Wheels = new Transform?[3];
            public bool ShownSpinning, ShownWin;
            public Transform? PressedButton;
            public Vector3 ButtonPosition;
            public Quaternion ButtonRotation;
            public float ButtonReleaseAt;
        }
        private const float ReelStep = 25.714285f;
        private static readonly FieldInfo?[] WheelFields =
        {
            AccessTools.Field(typeof(SlotMachine), "SpinningWheel1"), AccessTools.Field(typeof(SlotMachine), "SpinningWheel2"),
            AccessTools.Field(typeof(SlotMachine), "SpinningWheel3")
        };
        private static readonly FieldInfo? SpinClip = AccessTools.Field(typeof(SlotMachine), "Audio_Bar_SlotMachine_Spin");
        private static readonly FieldInfo? WinClip = AccessTools.Field(typeof(SlotMachine), "Audio_Bar_SlotMachine_Win");
        private static readonly FieldInfo? JackpotClip = AccessTools.Field(typeof(SlotMachine), "Audio_Bar_SlotMachine_Jackpot");
        private static readonly FieldInfo? JackpotLight = AccessTools.Field(typeof(SlotMachine), "Transform_TopLight_Jackpot");

        private static readonly FieldInfo StoreBar = AccessTools.Field(typeof(SlotMachine), "StoreBar");
        private static readonly FieldInfo Spinning = AccessTools.Field(typeof(SlotMachine), "Coroutine_Spinning");
        private static readonly FieldInfo ButtonType = AccessTools.Field(typeof(SlotMachine_Button), "Button_Type");
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<SlotMachine, Binding> byMachine = new Dictionary<SlotMachine, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, (float Tokens, float Time)> budgets = new Dictionary<uint, (float, float)>();
        private readonly Dictionary<Guid, ulong> pending = new Dictionary<Guid, ulong>();
        private readonly SlotMachineClaims claims = new SlotMachineClaims();
        private readonly Cash_Manager cash;
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed;
        private float nextCapture;


#if MONMP_DEVELOPMENT
        internal int TestAppliedCommands { get; private set; }


        internal int TestDuplicateCommands { get; private set; }


        internal int TestRejectedCommands { get; private set; }


        internal SlotMachineReply? TestLastReply { get; private set; }


        internal ulong[] TestMachines => bindings.Keys.OrderBy(id => id).ToArray();


        internal SlotMachineState TestState(ulong id) => bindings[id].Current;


        internal SlotMachine TestNative(ulong id) => bindings[id].Machine;


        internal Transform TestButton(ulong id, SlotMachine_Button.ButtonType type) => bindings[id].Buttons.First(button => button.Type == type).Button;


        internal uint TestRevision(ulong id) => channel != null && channel.Records.TryGetValue(id, out var record) ? record.Revision : 0;


        internal int TestClaimCount => claims.Count;


        internal bool TestRestored => !host && bindings.Values.All(binding => Capture(binding, reels: false).Encode().SequenceEqual(binding.Original.Encode()));


        internal bool TestReelsShown(ulong id)
        {
            var binding = bindings[id];
            return Enumerable.Range(0, 3).All(i => binding.Current.ReelAt(i) != SlotMachineState.UnknownReel && ReelOf(binding.Wheels[i]) == binding.Current.ReelAt(i));
        }
#endif

        internal bool TransactionsSettled => pending.Count == 0 &&
            (!host || bindings.Values.All(binding => binding.Machine == null || !(bool)Spinning.GetValue(binding.Machine)!));
        internal bool SettleForSave()
        {
            if (!host || disposed) return true;
            bool complete = true;
            foreach (var binding in bindings.Values)
            {
                if (binding.Machine == null) continue;
                if ((bool)Spinning.GetValue(binding.Machine)!)
                    complete &= NativeSaveProduction.Finish(binding.Machine, "Coroutine_Spin") &&
                        !(bool)Spinning.GetValue(binding.Machine)!;
                binding.Current = Capture(binding);
                if (channel?.Ready == true) channel.Publish(binding.Id, binding.Current.Encode());
            }
            return complete;
        }
        public bool NewActionsBlocked { get; set; }

        internal SharedSlotMachines(bool host, string diagnostics)
        {
            this.host = host;
            cash = Object.FindObjectOfType<Cash_Manager>();
            if (cash == null) throw new InvalidOperationException("Wait until the cash manager initializes before synchronizing slot machines.");
            if (StoreBar?.FieldType != typeof(Store_Bar) || Spinning?.FieldType != typeof(bool) || ButtonType?.FieldType != typeof(SlotMachine_Button.ButtonType))
                throw new InvalidOperationException("Unsupported slot machine field layout.");

            var identities = new IdentityRegistry();
            var lines = new List<string>();
            foreach (var machine in Object.FindObjectsOfType<SlotMachine>(true))
            {
                if (machine == null || !machine.gameObject.scene.IsValid()) continue;
                var store = StoreBar.GetValue(machine) as Store_Bar;
                if (store == null) continue;
                var buttons = machine.GetComponentsInChildren<SlotMachine_Button>(true)
                    .Where(button => button != null && button.gameObject.scene.IsValid())
                    .Select(button => new ButtonBinding { Type = (SlotMachine_Button.ButtonType)ButtonType.GetValue(button)!, Button = button.transform })
                    .ToArray();
                if (buttons.Length == 0) continue;
                string identity = SharedBuildingDoors.StablePath(machine.transform) + "/shared-slot-machine-v1";
                ulong id = identities.Register(identity);
                if (bindings.ContainsKey(id)) throw new InvalidOperationException("Slot machine identity collision: " + identity);
                var binding = new Binding { Id = id, Identity = identity, Machine = machine, Store = store, Buttons = buttons };
                for (int i = 0; i < 3; i++) binding.Wheels[i] = WheelFields[i]?.GetValue(machine) as Transform;
                // Each machine randomizes its reels at Start, so the catalog record leaves them unknown.
                binding.Original = binding.Current = Capture(binding, reels: false);
                bindings.Add(id, binding); byMachine.Add(machine, binding);
                lines.Add(id.ToString("X16") + "\t" + identity + "\tbuttons=" + buttons.Length + "\tbet=" + binding.Current.Bet + "\tcredit=" + binding.Current.Credit);
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-slot-machines.tsv"), new[] { "id\tidentity\tbuttons\tbet\tcredit" }.Concat(lines.OrderBy(s => s)));
#endif

        }

        private static SlotMachineState Capture(Binding binding, bool reels = true)
        {
            int[] data = binding.Machine.Get_SaveData;
            if (data == null || data.Length != 4) throw new InvalidDataException("Native slot machine save data has an unsupported size.");
            return new SlotMachineState(data[0], data[1], data[2], data[3], (bool)Spinning.GetValue(binding.Machine)!,
                reels ? ReelOf(binding.Wheels[0]) : SlotMachineState.UnknownReel,
                reels ? ReelOf(binding.Wheels[1]) : SlotMachineState.UnknownReel,
                reels ? ReelOf(binding.Wheels[2]) : SlotMachineState.UnknownReel);
        }

        // The reel's stop (0-13) when it rests on one; a turning reel sits between stops.
        // Euler x does not round-trip past 90 degrees, so the angle comes from the rotated up axis.
        private static byte ReelOf(Transform? wheel)
        {
            if (wheel == null) return SlotMachineState.UnknownReel;
            Vector3 up = wheel.localRotation * Vector3.up;
            float angle = Mathf.Repeat(Mathf.Atan2(up.z, up.y) * Mathf.Rad2Deg, 360f);
            int stop = Mathf.RoundToInt(angle / ReelStep);
            if (Mathf.Abs(Mathf.DeltaAngle(angle, stop * ReelStep)) > 0.05f) return SlotMachineState.UnknownReel;
            return (byte)(stop % SlotMachineState.ReelStops);
        }

        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, SlotMachineState.Adapter, b.Original.Encode()));

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.ReplyReceived += Reply; channel.CommandRejected += Stale;
        }

        private bool Ready => !disposed && session?.IsRunning == true && channel?.Ready == true;
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) { presence.Remove(peer); budgets.Remove(peer); }

        // Guests suppress the native method completely. Only the host may execute cash and RNG effects.
        internal bool InterceptButton(SlotMachine machine, SlotMachine_Button.ButtonType type)
        {
            if (disposed || session?.IsRunning != true || host) return true;
            if (!byMachine.TryGetValue(machine, out var binding)) return false;
            if (channel?.FullySynchronized != true)
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the shared world to finish loading before using the slot machine.");
                return false;
            }
            if (NewActionsBlocked)
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the multiplayer save to finish before using the slot machine.");
                return false;
            }
            if (!binding.Buttons.Any(button => button.Type == type && button.Button != null && button.Button.gameObject.activeInHierarchy)) return false;
            if (pending.Count >= 16)
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the pending slot machine action to finish.");
                return false;
            }
            var operation = Guid.NewGuid();
            pending[operation] = binding.Id;
            channel!.Request(binding.Id, new SlotMachineCommand(operation, (SlotMachineAction)(byte)type).Encode(), true);
            return false;
        }


#if MONMP_DEVELOPMENT
        internal void TestRequest(ulong id, Guid operation, SlotMachineAction action)
        {
            if (channel?.FullySynchronized != true || session?.IsHost == true) throw new InvalidOperationException("Slot machine test request requires a synchronized guest.");
            if (pending.Count >= 16 && !pending.ContainsKey(operation)) throw new InvalidOperationException("Slot machine test request queue is full.");
            pending[operation] = id;
            channel.Request(id, new SlotMachineCommand(operation, action).Encode(), true);
        }
#endif


        private bool PeerPosition(uint peer, out Vector3 at)
        {
            at = default;
            if (peer == 0 || !presence.TryGetValue(peer, out var observed) || Time.unscaledTime - observed.Time > 2)
                return false;
            at = new Vector3(observed.Pose.X, observed.Pose.Y, observed.Pose.Z);
            if (observed.Pose.State == (byte)PlayerState.Walking) return true;
            // A genuine native chair claim is required; a client cannot invent a seated pose.
            return observed.Pose.State == (byte)PlayerState.InFurnitureChair &&
                MonMPPlugin.Instance?.World?.FurnitureSeats.PeerSeatedNear(peer, at, 1.6f) == true;
        }

        private bool TakeBudget(uint peer)
        {
            float now = Time.unscaledTime;
            var state = budgets.TryGetValue(peer, out var current) ? current : (Tokens: 8f, Time: now);
            state.Tokens = Math.Min(8, state.Tokens + Math.Max(0, now - state.Time) * 2);
            state.Time = now;
            if (state.Tokens < 1) { budgets[peer] = state; return false; }
            state.Tokens -= 1; budgets[peer] = state; return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(SlotMachineState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var binding)) return;
            var command = SlotMachineCommand.Decode(payload);
            if (!session!.Peers.TryGetValue(peer, out var remote)) return;
            var claim = claims.Claim(remote.ProfileId, command.Operation);
            if (claim == SlotMachineClaimResult.Duplicate)
            {

#if MONMP_DEVELOPMENT
                TestDuplicateCommands++;
#endif

                channel!.Reply(peer, id, new SlotMachineReply(command.Operation, SlotMachineReplyStatus.Duplicate).Encode());
                return;
            }
            if (claim == SlotMachineClaimResult.Capacity) { Reply(peer, id, command.Operation, SlotMachineReplyStatus.Capacity); return; }
            if (NewActionsBlocked || !TakeBudget(peer) || !PeerPosition(peer, out var at)) {
#if MONMP_DEVELOPMENT
TestRejectedCommands++;
#endif
 Reply(peer, id, command.Operation, SlotMachineReplyStatus.Rejected); return; }
            var button = binding.Buttons.FirstOrDefault(candidate => (byte)candidate.Type == (byte)command.Action && candidate.Button != null && candidate.Button.gameObject.activeInHierarchy);
            if (button == null || (at - button.Button.position).sqrMagnitude > 20.25f || !CanApply(binding, command.Action))
            {
#if MONMP_DEVELOPMENT
TestRejectedCommands++;
#endif
 Reply(peer, id, command.Operation, SlotMachineReplyStatus.Rejected); return; }

            try
            {
                binding.Machine.ButtonPressed((SlotMachine_Button.ButtonType)(byte)command.Action);
                PressVisual(binding, (SlotMachine_Button.ButtonType)(byte)command.Action);
                binding.Current = Capture(binding);
                channel!.Publish(binding.Id, binding.Current.Encode());

#if MONMP_DEVELOPMENT
                TestAppliedCommands++;
#endif

                Reply(peer, id, command.Operation, SlotMachineReplyStatus.Applied);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                try { binding.Current = Capture(binding); channel!.Publish(binding.Id, binding.Current.Encode()); } catch { }
                Reply(peer, id, command.Operation, SlotMachineReplyStatus.Uncertain);
            }
        }

        private bool CanApply(Binding binding, SlotMachineAction action)
        {
            var state = Capture(binding);
            bool spinning = (bool)Spinning.GetValue(binding.Machine)!;
            switch (action)
            {
                case SlotMachineAction.BetMin:
                case SlotMachineAction.Bet10:
                case SlotMachineAction.Bet100:
                case SlotMachineAction.BetMax:
                    return !spinning;
                case SlotMachineAction.CashIn10: return !spinning && binding.Store.isOpen && cash.isCanAfford(10);
                case SlotMachineAction.CashIn100: return !spinning && binding.Store.isOpen && cash.isCanAfford(100);
                case SlotMachineAction.CashIn1000: return !spinning && binding.Store.isOpen && cash.isCanAfford(1000);
                case SlotMachineAction.CashOut: return !spinning && state.Credit > 0;
                case SlotMachineAction.Spin: return !spinning && binding.Store.isOpen && state.Bet <= state.Credit;
                default: return false;
            }
        }

        private void Reply(uint peer, ulong id, Guid operation, SlotMachineReplyStatus status)
        {
            if (!session!.Peers.ContainsKey(peer)) return;
            channel!.Reply(peer, id, new SlotMachineReply(operation, status).Encode());
        }

        private void Stale(uint peer, ulong id, byte[] payload)
        {
            if (!host || !bindings.ContainsKey(id)) return;
            try { var command = SlotMachineCommand.Decode(payload); Reply(peer, id, command.Operation, SlotMachineReplyStatus.Rejected); }
            catch (InvalidDataException) { }
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (host || !bindings.ContainsKey(id)) return;
            var reply = SlotMachineReply.Decode(payload);
            if (!pending.TryGetValue(reply.Operation, out ulong requestedId) || requestedId != id) return;
            pending.Remove(reply.Operation);

#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            string message = reply.Status switch
            {
                SlotMachineReplyStatus.Applied => "Slot machine action accepted.",
                SlotMachineReplyStatus.Duplicate => "That slot machine action was already handled.",
                SlotMachineReplyStatus.Capacity => "This session cannot accept more slot machine actions.",
                SlotMachineReplyStatus.Uncertain => "Slot machine action outcome is uncertain. Check the machine and your cash before trying again.",
                _ => "The slot machine rejected that action. Check the store hours, range and available cash."
            };
            // A spin shows on the machine itself; only a refusal needs words.
            if (reply.Status != SlotMachineReplyStatus.Applied) MonMPPlugin.Instance?.ReportItemUseUnavailable(message);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(record => record.Adapter == SlotMachineState.Adapter)) Apply(record);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == SlotMachineState.Adapter) Apply(record);
        }

        private void Apply(WorldRecord record)
        {
            if (!bindings.TryGetValue(record.Id, out var binding) || record.Adapter != SlotMachineState.Adapter)
                throw new InvalidDataException("Unknown slot machine record.");
            var state = SlotMachineState.Decode(record.Payload);
            binding.Machine.Get_SaveData = new[] { state.Bet, state.Credit, state.WinnerPaid, state.LossAmount };
            binding.Current = state;
            Present(binding);
        }

        // The host's spin as a guest sees it: reels turn until the host's stops arrive, with the
        // machine's own spin, win and jackpot sounds and light.
        private static void Present(Binding binding)
        {
            var state = binding.Current;
            var machine = binding.Machine;
            if (machine == null) return;
            if (state.Spinning && !binding.ShownSpinning)
            {
                PressVisual(binding, SlotMachine_Button.ButtonType.Spin);
                binding.ShownWin = false;
                Play(machine, SpinClip);
            }
            if (state.Spinning && state.WinnerPaid > 0 && !binding.ShownWin)
            {
                binding.ShownWin = true;
                bool jackpot = state.WinnerPaid >= state.Bet * 100;
                Play(machine, jackpot ? JackpotClip : WinClip);
                if (jackpot && JackpotLight?.GetValue(machine) is Transform light) light.gameObject.SetActive(true);
            }
            if (!state.Spinning && binding.ShownSpinning && JackpotLight?.GetValue(machine) is Transform off) off.gameObject.SetActive(false);
            binding.ShownSpinning = state.Spinning;
            for (int i = 0; i < 3; i++)
            {
                byte stop = state.ReelAt(i);
                if (stop != SlotMachineState.UnknownReel && binding.Wheels[i] != null) binding.Wheels[i]!.localEulerAngles = new Vector3(stop * ReelStep, 0f, 0f);
            }
        }

        private static void Play(SlotMachine machine, FieldInfo? clipField)
        {
            if (clipField?.GetValue(machine) is AudioClip clip && Singleton<SOUND_Manager>.i != null)
                Singleton<SOUND_Manager>.i.PlayOneShotOnTransform(machine.transform, clip);
        }

        private static void ReleaseButtonVisual(Binding binding)
        {
            if (binding.PressedButton != null)
            {
                binding.PressedButton.localPosition = binding.ButtonPosition;
                binding.PressedButton.localRotation = binding.ButtonRotation;
                binding.PressedButton = null;
            }
        }

        // Match native MouseDown/MouseUp presentation without replaying its cash or spin action.
        private static void PressVisual(Binding binding, SlotMachine_Button.ButtonType type)
        {
            ReleaseButtonVisual(binding);
            var button = binding.Buttons.FirstOrDefault(value => value.Type == type)?.Button;
            var native = button == null ? null : button.GetComponent<SlotMachine_Button>();
            if (native == null || button == null) return;
            if (AccessTools.Field(typeof(SlotMachine_Button), "LocalPosition_PressedDown")?.GetValue(native) is not Vector3 position ||
                AccessTools.Field(typeof(SlotMachine_Button), "LocalRotation_PressedDown")?.GetValue(native) is not Vector3 rotation) return;
            binding.PressedButton = button;
            binding.ButtonPosition = AccessTools.Field(typeof(SlotMachine_Button), "LocalPosition_Original")?.GetValue(native) is Vector3 originalPosition
                ? originalPosition : button.localPosition;
            binding.ButtonRotation = AccessTools.Field(typeof(SlotMachine_Button), "LocalRotation_Original")?.GetValue(native) is Vector3 originalRotation
                ? Quaternion.Euler(originalRotation) : button.localRotation;
            binding.ButtonReleaseAt = Time.unscaledTime + 0.35f;
            button.localPosition = position; button.localEulerAngles = rotation;
        }

        public void Update()
        {
            if (!Ready) return;
            foreach (var binding in bindings.Values)
                if (binding.PressedButton != null && Time.unscaledTime >= binding.ButtonReleaseAt) ReleaseButtonVisual(binding);
            if (host)
            {
                if (Time.unscaledTime < nextCapture) return;
                nextCapture = Time.unscaledTime + 0.2f;
                foreach (var binding in bindings.Values)
                {
                    var state = Capture(binding);
                    if (!state.Encode().SequenceEqual(binding.Current.Encode()))
                    { binding.Current = state; channel!.Publish(binding.Id, state.Encode()); }
                }
            }
            else
            {
                foreach (var binding in bindings.Values)
                {
                    // Reels the host still has turning keep turning here.
                    if (binding.Current.Spinning)
                    {
                        for (int i = 0; i < 3; i++)
                            if (binding.Current.ReelAt(i) == SlotMachineState.UnknownReel && binding.Wheels[i] != null)
                                binding.Wheels[i]!.Rotate(Vector3.right * Time.deltaTime * 2000f, Space.Self);
                        if (binding.ShownWin && binding.Current.WinnerPaid >= binding.Current.Bet * 100 && JackpotLight?.GetValue(binding.Machine) is Transform light)
                            light.Rotate(Vector3.up * Time.deltaTime * 500f, Space.Self);
                    }
                    var state = Capture(binding);
                    if (state.Bet != binding.Current.Bet || state.Credit != binding.Current.Credit || state.WinnerPaid != binding.Current.WinnerPaid || state.LossAmount != binding.Current.LossAmount)
                    {
                        binding.Machine.Get_SaveData = new[] { binding.Current.Bet, binding.Current.Credit, binding.Current.WinnerPaid, binding.Current.LossAmount };
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (var binding in bindings.Values) ReleaseButtonVisual(binding);
            if (disposed) return; disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null)
            {
                channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed;
                channel.ReplyReceived -= Reply; channel.CommandRejected -= Stale;
            }
            if (!host)
            {
                foreach (var binding in bindings.Values) if (binding.Machine != null)
                    binding.Machine.Get_SaveData = new[] { binding.Original.Bet, binding.Original.Credit, binding.Original.WinnerPaid, binding.Original.LossAmount };
            }
            presence.Clear(); budgets.Clear(); pending.Clear(); session = null; channel = null;
        }
    }
}
