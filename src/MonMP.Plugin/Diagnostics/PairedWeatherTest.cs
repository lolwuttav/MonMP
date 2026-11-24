using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UniStorm;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedWeatherTest
    {
        private readonly MonMPPlugin plugin;
        private readonly UniStormSystem sky = UniStormSystem.Instance;
        private int phase, acknowledgment, events, generations, repairs, samples;
        private float next, until, initialWet;
        public string Status => "weather phase " + phase + "; transition samples=" + samples;
        private static WeatherType Dry(UniStormSystem sky) => sky.AllWeatherTypes.First(w => w.ShaderControl == WeatherType.ShaderControlEnum.None && w.CloudLevel == WeatherType.CloudLevelEnum.Clear);
        private static WeatherType Rain(UniStormSystem sky) => sky.AllWeatherTypes.First(w => w.ShaderControl == WeatherType.ShaderControlEnum.Rain && w.UseLightning == WeatherType.Yes_No.No);
        internal static void Prepare(bool host)
        {
            var sky = UniStormSystem.Instance;
            sky.TimeFlow = sky.RealWorldTime = sky.WeatherGeneration = UniStormSystem.EnableFeature.Disabled;
            sky.CurrentWeatherType = host ? Rain(sky) : Dry(sky); sky.InitializeWeather(false);
            sky.NextWeatherType = host ? Dry(sky) : Rain(sky);
            sky.Temperature = host ? 17 : 8; sky.MoonPhaseIndex = host ? 2 : 1;
            sky.HourToChangeWeather = host ? 19 : 20;
            sky.WeatherForecast.Clear();
            for (int i = 0; i < 24; i++) sky.WeatherForecast.Add(i % 2 == 0 ? sky.CurrentWeatherType : sky.NextWeatherType);
        }
        internal PairedWeatherTest(MonMPPlugin plugin)
        {
            this.plugin = plugin; sky.OnWeatherChangeEvent.AddListener(Changed); sky.OnWeatherGenerationEvent.AddListener(Generated);
            if (plugin.TestOptions.IsHost) plugin.World!.Channel.CommandRequested += Acknowledge;
        }
        private void Changed() => events++;
        private void Generated() => generations++;
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ClockState.Entity && payload.Length == 1) acknowledgment = Math.Max(acknowledgment, payload[0]); }
        private void Signal(byte stage) => plugin.World!.Channel.Request(ClockState.Entity, new[] { stage });
        private void Detach() { sky.OnWeatherChangeEvent.RemoveListener(Changed); sky.OnWeatherGenerationEvent.RemoveListener(Generated); }
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException("Weather test: " + reason); }
        private void Check(SharedWeather weather)
        {
            var native = weather.TestNative; var state = weather.TestState;
            Require(native.Encode().SequenceEqual(state.Encode()), "Native weather samples differ from accepted host state.");
            Require(weather.TestGradientsMatch, "Native weather gradients differ from accepted host samples.");
            Require(events == 0 && generations == 0, "Guest replayed world weather events.");
        }
        internal bool Tick()
        {
            if (Time.unscaledTime < next) return false;
            if (!plugin.TestOptions.IsHost && phase == 5) { plugin.BeginPairedTestSession(); phase = 6; return false; }
            var world = plugin.World; if (world?.Channel.FullySynchronized != true) return false;
            var weather = world.Weather; var state = weather.TestState;
            if (plugin.TestOptions.IsHost)
            {
                if (phase == 0 && acknowledgment >= 1)
                {
                    sky.TransitionSpeed = 1; sky.ChangeWeather(Dry(sky)); sky.Temperature = 21;
                    Require(events == 1, "Host native transition did not emit its weather event."); phase = 1; until = Time.unscaledTime + 14;
                }
                else if (phase == 1 && Time.unscaledTime > until)
                { sky.Temperature = 22; phase = 2; }
                else if (phase == 2 && acknowledgment >= 2) phase = 3;
                else if (phase == 3 && acknowledgment >= 3)
                { sky.Temperature = 23; phase = 4; next = Time.unscaledTime + 0.5f; }
                else if (phase == 4) { Detach(); world.Channel.CommandRequested -= Acknowledge; return true; }
                return false;
            }
            if (phase == 0)
            {
                Require(state.Temperature == 17 && state.Moon == 2 && state.ChangeHour == 19 && state.Forecast.Count == 24, "Late-join host forecast/calendar fields missing.");
                Check(weather); initialWet = state.Scalars[7];
                sky.ChangeWeather(Dry(sky)); UniStormManager.Instance.ChangeWeatherInstantly(Dry(sky));
                UniStormManager.Instance.RandomWeather(); UniStormManager.Instance.RegenerateForecast();
                sky.GenerateWeather(); sky.m_UniStormLightningSystem.GenerateLightning(); Check(weather);
                Signal(1); phase = 1;
            }
            else if (phase == 1)
            {
                if (state.Temperature == 17) { Signal(1); next = Time.unscaledTime + 0.3f; return false; }
                Check(weather); samples++;
                if (state.Temperature != 22) return false;
                Require(samples > 3 && state.Scalars[7] < initialWet, "Native transition did not produce multiple replicated wetness samples.");
                sky.CurrentFogAmount = 0.37f; sky.CloudBaseColor.SetKeys(new[] { new GradientColorKey(Color.magenta, 0), new GradientColorKey(Color.magenta, 1) }, sky.CloudBaseColor.alphaKeys);
                repairs = world.Channel.RepairsApplied;
                Require(world.Channel.RequestResync(SnapshotScope.Section(WeatherState.Adapter), "Weather visual repair"), "Repair request failed."); phase = 2;
            }
            else if (phase == 2 && world.Channel.RepairsApplied > repairs)
            { Check(weather); Signal(2); phase = 3; next = Time.unscaledTime + 0.5f; }
            else if (phase == 3)
            {
                Detach(); plugin.EndPairedTestSession();
                Require(weather.TestRestored, "Disconnect did not restore captured offline weather.");
                sky.ChangeWeather(Rain(sky)); Require(sky.CurrentWeatherType == Rain(sky), "Offline native weather remained blocked.");
                phase = 5; next = Time.unscaledTime + 1;
            }
            else if (phase == 6)
            {
                Require(state.Temperature == 22, "Reconnect lost host weather."); Check(weather); Signal(3); phase = 7; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 7) { if (state.Temperature == 23) return true; Signal(3); next = Time.unscaledTime + 0.3f; }
            return false;
        }
    }
}

