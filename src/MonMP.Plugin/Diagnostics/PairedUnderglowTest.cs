using System;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Text;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;
using UnityEngine.Events;

namespace MonMP.Plugin.Diagnostics
{
    // Fixture-only kit on a shared ATV. Native Start/Update run normally on the
    // host; the real two-process WorldChannel carries its material/light samples.
    // Physical kit purchase, installation and aimed mouse clicks remain manual.
    internal sealed class PairedUnderglowTest : IDisposable
    {
        private static readonly MethodInfo NativeUpdate = AccessTools.Method(typeof(Vehicle_Underglow), "Update");
        private static readonly MethodInfo IgnitionOn = AccessTools.Method(typeof(Vehicle_Underglow), "Vehicle_IgnitionOn");
        private static readonly MethodInfo IgnitionOff = AccessTools.Method(typeof(Vehicle_Underglow), "Vehicle_IgnitionOff");
        private readonly MonMPPlugin plugin;
        private readonly GameObject fixture;
        private readonly Vehicle_Underglow kit;
        private readonly NWH.Vehicle vehicle;
        private readonly Material material;
        private readonly Material materialOracle;
        private readonly float started = Time.unscaledTime;
        private SharedUnderglow? shared;
        private UnderglowState baseline, previous;
        private int phase, acknowledgment, samples;
        private uint revision;
        private bool sawColor, sawIntensity, disposed;
        private float next;
        private ulong recordId;
        internal string Status => "underglow phase=" + phase + "; ack=" + acknowledgment +
            "; samples=" + samples + "; RGB changed=" + sawColor + "; pulse changed=" + sawIntensity;

        internal PairedUnderglowTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin;
            var vehicles = plugin.Vehicles!;
            vehicles.TestType = Vehicle_Type.SmollATV;
            vehicle = vehicles.TestVehicle;
            if (vehicle.GetComponentsInChildren<Vehicle_Underglow>(true).Length != 0)
                throw new InvalidOperationException("Underglow fixture requires an ATV without an installed kit.");
            recordId = SharedUnderglow.RecordId(vehicles.TestId);
            fixture = new GameObject("MonMPUnderglowTestFixture"); fixture.SetActive(false);
            fixture.transform.SetParent(vehicle.transform, false);
            kit = fixture.AddComponent<Vehicle_Underglow>();
            Set("vehicle", vehicle);
            Set("LightSource", MakeLight("under")); Set("FeetLight_L", MakeLight("feet-left")); Set("FeetLight_R", MakeLight("feet-right"));
            var bloomObject = new GameObject("bloom"); bloomObject.transform.SetParent(fixture.transform, false);
            var renderer = bloomObject.AddComponent<MeshRenderer>();
            var shader = Shader.Find("Standard") ?? throw new InvalidOperationException("Underglow fixture Standard shader unavailable.");
            material = new Material(shader); renderer.sharedMaterial = material;
            materialOracle = new Material(shader); materialOracle.EnableKeyword("_EMISSION");
            Set("Underglow_Bloom", renderer);
            Set("colorList", new[] { new Color(0.7f, 0.24f, 0.11f, 1), new Color(0.1f, 0.6f, 0.37f, 1) });
            foreach (string field in new[] { "Switch_Button_Mode", "Switch_Button_Cycle", "Switch_Button_Intensity", "Switch_Button_Color" })
            {
                var buttonObject = new GameObject(field); buttonObject.transform.SetParent(fixture.transform, false);
                var button = buttonObject.AddComponent<Furniture_SwitchButton>(); button.enabled = false; Set(field, button);
            }
            kit.LoadSettingFromSave(new[] { 0, 0, 0, 10, 0 });
            fixture.SetActive(true);
            if (plugin.TestOptions.IsHost) plugin.World!.Channel.CommandRequested += Acknowledge;
            next = Time.unscaledTime + 0.25f; // allow the actual native Start to run
        }

        private Light MakeLight(string name)
        {
            var go = new GameObject(name); go.transform.SetParent(fixture.transform, false);
            var light = go.AddComponent<Light>(); light.color = plugin.TestOptions.IsHost ? Color.red : Color.blue;
            light.intensity = plugin.TestOptions.IsHost ? 2 : 3; light.enabled = false; return light;
        }
        private void Set(string name, object value) => AccessTools.Field(typeof(Vehicle_Underglow), name).SetValue(kit, value);
        private void Acknowledge(uint peer, ulong id, byte[] data)
        { if (peer != 0 && id == recordId && data.Length == 2 && data[0] == 0xF0) acknowledgment = Math.Max(acknowledgment, data[1]); }
        private void Signal(byte stage) => plugin.World!.Channel.Request(recordId, new byte[] { 0xF0, stage }, useLease: true);
        private void Configure(int mode, int cycle = 0, int intensity = 12, bool on = true)
        {
            kit.LoadSettingFromSave(new[] { on ? 1 : 0, mode, cycle, intensity, 1 });
            (on ? IgnitionOn : IgnitionOff).Invoke(kit, null);
            // Invoke the real native animation method too, rather than synthesizing
            // the expected hue/intensity in the adapter or this test.
            if (on) NativeUpdate.Invoke(kit, null);
            shared!.LateUpdate();
        }

        internal bool Tick()
        {
            if (Time.unscaledTime - started > 120) throw new InvalidOperationException("Underglow test timed out: " + Status);
            if (Time.unscaledTime < next) return false;
            if (!plugin.TestOptions.IsHost && phase == 8)
            { plugin.BeginPairedTestSession(); shared = null; phase = 9; next = Time.unscaledTime + 0.5f; return false; }
            if (plugin.World?.Channel.FullySynchronized != true) return false;
            if (shared == null)
            {
                shared = plugin.World.Underglow; shared.TestScan();
                if (!shared.TestBound(kit)) throw new InvalidOperationException("Fixture kit did not bind to the shared ATV.");
            }
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0 && acknowledgment >= 1) { Configure(3); phase = 1; }
            else if (phase == 1 && acknowledgment >= 2) { Configure(4, 1); phase = 2; }
            else if (phase == 2 && acknowledgment >= 3) { Configure(5, 2); phase = 3; }
            else if (phase == 3 && acknowledgment >= 4) { Configure(5, 2, on: false); phase = 4; }
            else if (phase == 4 && acknowledgment >= 5)
            {
                fixture.SetActive(false);
                Configure(5, 2, intensity: 14, on: false);
                Require(shared!.TestNative(kit).Ready, "Inactive native kit sources were not sampled.");
                phase = 5;
            }
            else if (phase == 5 && acknowledgment >= 6) { fixture.SetActive(true); Configure(5, 0); phase = 6; }
            else if (phase == 6 && acknowledgment >= 8) { Dispose(); return true; }
            return false;
        }

        private bool Guest()
        {
            var state = shared!.TestState(kit);
            if (phase == 0)
            {
                // Only this fixture's baseline is changed. Capture it after native
                // Start so disconnect restoration can be compared exactly.
                kit.LoadSettingFromSave(new[] { 0, 0, 0, 10, 0 }); IgnitionOff.Invoke(kit, null);
                foreach (var light in fixture.GetComponentsInChildren<Light>(true)) { light.color = Color.blue; light.intensity = 3; }
                material.color = Color.blue; material.SetColor("_EmissionColor", Color.blue);
                shared.TestCaptureBaseline(kit); baseline = shared.TestNative(kit);
                shared.LateUpdate(); Signal(1); phase = 1; ResetSamples(); return false;
            }
            if (phase == 1 && state.Mode == 3 && state.Ignition && Observe(state, false))
            { Signal(2); phase = 2; ResetSamples(); }
            else if (phase == 2 && state.Mode == 4 && state.Cycle == 1 && Observe(state, true))
            {
                Require(state.UnderEnabled && !state.LeftEnabled && !state.RightEnabled && state.BloomActive, "Under-only zone differs from the native host.");
                Signal(3); phase = 3; ResetSamples();
            }
            else if (phase == 3 && state.Mode == 5 && state.Cycle == 2 && Observe(state, true))
            {
                Require(!state.UnderEnabled && state.LeftEnabled && state.RightEnabled && !state.BloomActive, "Feet-only zone differs from the native host.");
                Signal(4); phase = 4;
            }
            else if (phase == 4 && !state.Ignition && state.Intensity == 12)
            { Check(state); Require(!state.UnderEnabled && !state.LeftEnabled && !state.RightEnabled && !state.BloomActive, "Ignition off left native sources enabled."); Signal(5); phase = 5; }
            else if (phase == 5 && !state.Ignition && state.Intensity == 14)
            { Check(state); Require(state.Ready, "Inactive host kit did not retain source readiness."); Signal(6); phase = 6; ResetSamples(); }
            else if (phase == 6 && state.Ignition && state.Cycle == 0 && Observe(state, true))
            {
                Signal(7); plugin.EndPairedTestSession();
                Require(shared.ShouldRunNativeUpdate(kit), "Offline native underglow Update remained suppressed.");
                var restored = shared.TestNative(kit);
                var nativeBaseline = ExpectedNativePresentation(baseline);
                if (!Same(restored, nativeBaseline)) Require(false, "Guest did not restore its settings, ignition, light and native material baseline. " + Difference(nativeBaseline, restored));
                phase = 8; next = Time.unscaledTime + 1; return false;
            }
            else if (phase == 9 && state.Ignition && state.Mode == 5 && state.Cycle == 0)
            {
                Check(state); ResetSamples(); phase = 10;
            }
            else if (phase == 10 && Observe(state, true))
            { Signal(8); Dispose(); return true; }
            return false;
        }

        private bool Observe(UnderglowState state, bool pulse)
        {
            Check(state);
            uint current = shared!.TestRevision(kit);
            if (current == revision) return false;
            if (samples > 0)
            {
                sawColor |= state.Bloom.Red != previous.Bloom.Red || state.Bloom.Green != previous.Bloom.Green || state.Bloom.Blue != previous.Bloom.Blue;
                sawIntensity |= state.Under.Intensity != previous.Under.Intensity;
            }
            previous = state; revision = current; samples++;
            if (samples < 12) return false;
            Require(sawColor && (!pulse || sawIntensity), "Actual host native RGB/pulse did not advance across samples.");
            return true;
        }
        private void Check(UnderglowState state)
        {
            shared!.LateUpdate();
            Require(!shared.ShouldRunNativeUpdate(kit), "Ready guest native animation was not suppressed.");
            var cached = shared.TestCachedState(kit);
            if (!Same(cached, state)) Require(false, "Guest cached sample differs from the exact channel state. " + Difference(state, cached));
            var expectedNative = ExpectedNativePresentation(state);
            var actual = shared.TestNative(kit);
            if (!Same(actual, expectedNative)) Require(false, "Guest lights/material differ from the host sample after native material setters. " +
                "native: " + Difference(expectedNative, actual) + "; raw material setter effect: " + Difference(state, expectedNative));
            var beforeNativeUpdate = actual;
            NativeUpdate.Invoke(kit, null);
            actual = shared.TestNative(kit);
            if (!Same(actual, beforeNativeUpdate)) Require(false, "Guest native Update advanced its own RGB/pulse phase. " + Difference(beforeNativeUpdate, actual));
        }
        private UnderglowState ExpectedNativePresentation(UnderglowState state)
        {
            // Paired run 20260929-171137-492 showed only material RGB changing
            // by 1-4 ULP through Unity setters/getters; wire/cache and Lights were
            // exact. Use a separate material of the same shader as the native
            // setter oracle. No numeric epsilon applies to any field, and the
            // tested renderer is never used to construct its expected result.
            materialOracle.color = new Color(state.Bloom.Red, state.Bloom.Green, state.Bloom.Blue, state.Bloom.Alpha);
            materialOracle.SetColor("_EmissionColor", new Color(state.Emission.Red, state.Emission.Green, state.Emission.Blue, state.Emission.Alpha));
            var bloom = materialOracle.color; var emission = materialOracle.GetColor("_EmissionColor");
            return new UnderglowState(state.Active, state.Mode, state.Cycle, state.Intensity, state.ColorIndex,
                state.Ready, state.Ignition, state.UnderEnabled, state.LeftEnabled, state.RightEnabled, state.BloomActive,
                state.Under, state.Left, state.Right, new UnderglowColor(bloom.r, bloom.g, bloom.b, bloom.a),
                new UnderglowColor(emission.r, emission.g, emission.b, emission.a));
        }
        // Allocate detailed diagnostics only on failure.
        private static string Difference(UnderglowState expected, UnderglowState actual)
        {
            var a = expected.Encode(); var b = actual.Encode(); var text = new StringBuilder();
            string[] headers = { "marker", "version", "active", "mode", "cycle", "setting-intensity", "color-index", "flags" };
            for (int at = 0; at < 8; at++)
                if (a[at] != b[at]) text.Append(headers[at]).Append(" expected=").Append(a[at].ToString("X2"))
                    .Append(" actual=").Append(b[at].ToString("X2")).Append("; ");
            string[] groups = { "under", "left", "right", "bloom", "emission" };
            string[] channels = { "r", "g", "b", "a", "intensity" };
            int offset = 8;
            for (int group = 0; group < groups.Length; group++)
                for (int component = 0; component < (group < 3 ? 5 : 4); component++, offset += 4)
                {
                    int abits = BitConverter.ToInt32(a, offset), bbits = BitConverter.ToInt32(b, offset);
                    if (abits == bbits) continue;
                    text.Append(groups[group]).Append('.').Append(channels[component]).Append(" expected=")
                        .Append(BitConverter.ToSingle(a, offset).ToString("R", CultureInfo.InvariantCulture))
                        .Append(" [").Append(abits.ToString("X8")).Append("] actual=")
                        .Append(BitConverter.ToSingle(b, offset).ToString("R", CultureInfo.InvariantCulture))
                        .Append(" [").Append(bbits.ToString("X8")).Append("]; ");
                }
            return text.Length == 0 ? "identical" : text.ToString();
        }
        private void ResetSamples() { samples = 0; revision = 0; sawColor = false; sawIntensity = false; }
        private static bool Same(UnderglowState a, UnderglowState b) => a.Encode().SequenceEqual(b.Encode());
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Underglow test: " + message); }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (plugin.TestOptions.IsHost && plugin.World != null) plugin.World.Channel.CommandRequested -= Acknowledge;
            fixture.SetActive(false);
            vehicle.ignitionOn.RemoveListener((UnityAction)Delegate.CreateDelegate(typeof(UnityAction), kit, IgnitionOn));
            vehicle.ignitionOff.RemoveListener((UnityAction)Delegate.CreateDelegate(typeof(UnityAction), kit, IgnitionOff));
            var renderer = (MeshRenderer)AccessTools.Field(typeof(Vehicle_Underglow), "Underglow_Bloom").GetValue(kit);
            var instance = renderer.material;
            if (instance != material) UnityEngine.Object.Destroy(instance);
            UnityEngine.Object.Destroy(fixture); UnityEngine.Object.Destroy(material); UnityEngine.Object.Destroy(materialOracle);
        }
    }
}
