using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UniStorm;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.Replication
{
    internal sealed class SharedWeather : IDisposable
    {
        private static readonly string[] gradientNames = { "CloudBaseColor", "CloudLightColor", "FogColor", "FogLightColor", "AmbientSkyLightColor", "AmbientEquatorLightColor", "AmbientGroundLightColor", "SunColor" };
        private static readonly string[] switcherNames = { "CloudColorKeySwitcher", "CloudLightColorKeySwitcher", "FogColorKeySwitcher", "FogLightColorKeySwitcher", "AmbientSkyLightColorKeySwitcher", "AmbientEquatorLightColorKeySwitcher", "AmbientGroundLightColorKeySwitcher", "SunLightColorKeySwitcher" };
        private static readonly FieldInfo cloudMaterial = AccessTools.Field(typeof(UniStormSystem), "m_CloudDomeMaterial");
        private static readonly FieldInfo generated = AccessTools.Field(typeof(UniStormSystem), "m_WeatherGenerated");
        private static readonly MethodInfo moonPhase = AccessTools.Method(typeof(UniStormSystem), "CalculateMoonPhase");
        private readonly UniStormSystem sky;
        private readonly Scene originalScene;
        private readonly bool host;
        private readonly Dictionary<ushort, WeatherType> types;
        private readonly Dictionary<WeatherType, ushort> typeIds;
        private readonly Dictionary<ulong, FieldInfo> gradients;
        private readonly ParticleSystem[] particles;
        private readonly AudioSource[] sounds;
        private readonly WorldRecord[] descriptors;
        private readonly WeatherState original;
        private readonly Dictionary<ulong, WeatherGradient> originalGradients, currentGradients;
        private readonly bool originalGenerated;
        private WeatherState current = null!;
        private readonly ushort[] forecastScratch = new ushort[24];
        private readonly float[] scalarScratch = new float[WeatherState.ScalarCount];
        private readonly float[] particleScratch, soundScratch;
        private readonly Dictionary<ulong, CapturePublication> publications = new Dictionary<ulong, CapturePublication>();
        private WorldChannel? channel;
        private NetworkSession? session;
        private bool applying, disposed, initialized;
        private float nextCapture;
        // Lightning is a local random event in UniStorm. The host counts its strikes; each new count
        // replays one strike (with the host's flash pattern) around the guest's own player.
        private static readonly ulong LightningEntity = StaticId.Hash("shared-weather-lightning/v1");
        private uint lightningCount;
        private bool lightningBaseline;
        private static byte[] EncodeLightning(uint count, byte pattern) { var b = new byte[5]; BitConverter.GetBytes(count).CopyTo(b, 0); b[4] = pattern; return b; }
        public bool BlocksLocalChanges => !host && session != null && !disposed && !applying;

#if MONMP_DEVELOPMENT
        internal WeatherState TestState => current;


        internal WeatherState TestNative => Capture();


        internal bool TestGradientsMatch => currentGradients.All(p => CaptureGradient(p.Key).Encode().SequenceEqual(p.Value.Encode()));


        internal bool TestRestored => !host && Capture().Encode().SequenceEqual(original.Encode()) &&
            originalGradients.All(p => CaptureGradient(p.Key).Encode().SequenceEqual(p.Value.Encode()));
#endif

        private Material Clouds => (Material)cloudMaterial.GetValue(sky);
        public SharedWeather(bool host, string diagnostics)
        {
            this.host = host; sky = UnityEngine.Object.FindObjectOfType<UniStormSystem>();
            if (sky == null || !sky.UniStormInitialized) throw new InvalidOperationException("Weather has not initialized.");
            originalScene = sky.gameObject.scene;
            var assets = sky.AllWeatherTypes.Where(w => w != null).Distinct().OrderBy(w => w.name, StringComparer.Ordinal).ToArray();
            if (assets.Length == 0 || assets.Length > 64 || assets.Select(w => w.name).Distinct(StringComparer.Ordinal).Count() != assets.Length)
                throw new InvalidOperationException("Ambiguous or oversized local weather catalog.");
            types = assets.Select((w, i) => (w, id: (ushort)(i + 1))).ToDictionary(p => p.id, p => p.w);
            typeIds = types.ToDictionary(p => p.Value, p => p.Key);
            particles = sky.WeatherEffectsList.Concat(sky.AdditionalWeatherEffectsList).Distinct().OrderBy(p => p.name, StringComparer.Ordinal).ToArray();
            sounds = sky.WeatherSoundsList.Distinct().OrderBy(s => s.name, StringComparer.Ordinal).ToArray();
            particleScratch = new float[particles.Length]; soundScratch = new float[sounds.Length];
            if (particles.Any(p => p == null) || sounds.Any(s => s == null) || particles.Select(p => p.name).Distinct().Count() != particles.Length ||
                sounds.Select(s => s.name).Distinct().Count() != sounds.Length || sky.MoonPhaseList.Count == 0 || sky.MoonPhaseList.Count > 32)
                throw new InvalidOperationException("Unsupported weather presentation layout.");
            gradients = gradientNames.ToDictionary(n => StaticId.Hash("shared-weather-gradient/v1/" + n), n => AccessTools.Field(typeof(UniStormSystem), n));
            string layout = string.Join("/", particles.Select(p => "particle:" + p.name).Concat(sounds.Select(s => "sound:" + s.name))
                .Concat(sky.MoonPhaseList.Select(m => "moon:" + m.MoonPhaseTexture.name + ":" + m.MoonPhaseIntensity.ToString("R", CultureInfo.InvariantCulture))));
            descriptors = types.Select(p => new WorldRecord(StaticId.Hash("weather-preset/v1/" + p.Key + "/" + p.Value.name + "/" +
                (int)p.Value.ShaderControl + "/" + (int)p.Value.CloudLevel + "/" + (int)p.Value.PrecipitationWeatherType + "/" + layout), 1, 3, Array.Empty<byte>())).ToArray();
            original = current = Capture(); originalGenerated = (bool)generated.GetValue(sky);
            originalGradients = gradients.Keys.ToDictionary(id => id, CaptureGradient);
            currentGradients = new Dictionary<ulong, WeatherGradient>(originalGradients);
            _ = original.Encode();

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "weather-catalog.tsv"), new[] { "id\tasset\tdisplay\tprecipitation\tshader" }
                .Concat(types.Select(p => p.Key + "\t" + p.Value.name + "\t" + p.Value.WeatherTypeName + "\t" + p.Value.PrecipitationWeatherType + "\t" + p.Value.ShaderControl)));


            File.WriteAllText(Path.Combine(diagnostics, "shared-weather.txt"), "MONMP_WEATHER_REGISTERED\nPresets=" + types.Count + "\nParticles=" + particles.Length + "\nSounds=" + sounds.Length + "\nGradients=" + gradients.Count + "\nCatalog=" + WorldRecord.Digest(descriptors, true));
#endif

        }
        private ushort Id(WeatherType? weather) => weather == null ? (ushort)0 : typeIds.TryGetValue(weather, out var id)
            ? id : throw new InvalidOperationException("Weather type is outside the local catalog.");
        public IEnumerable<WorldRecord> InitialRecords() => descriptors.Concat(new[] { new WorldRecord(WeatherState.Entity, 1, WeatherState.Adapter, original.Encode()) })
            .Concat(originalGradients.Select(p => new WorldRecord(p.Key, 1, WeatherState.Adapter, p.Value.Encode())))
            .Append(new WorldRecord(LightningEntity, 1, WeatherState.Adapter, EncodeLightning(0, 0)));
        public void Attach(NetworkSession session, WorldChannel channel)
        { this.session = session; this.channel = channel; channel.SnapshotApplied += Snapshot; channel.Changed += Changed; }
        // Native UniStorm leaves transient out-of-range values: the next weather-change hour is Hour-1,
        // so -1 at midnight, and the moon phase counts one past its list until the next phase update.
        // A cheat day-advance hit both and disabled weather sync for the session (#236).
        private static byte Wrap(int value, int count) => (byte)(count <= 0 ? 0 : ((value % count) + count) % count);
        private IReadOnlyList<ushort> Forecast()
        {
            if (sky.WeatherForecast.Count != forecastScratch.Length) return Array.Empty<ushort>();
            for (int i = 0; i < forecastScratch.Length; i++)
            {
                var weather = sky.WeatherForecast[i];
                if (weather == null || !typeIds.TryGetValue(weather, out forecastScratch[i])) return Array.Empty<ushort>();
            }
            return forecastScratch;
        }
        private WeatherState Capture()
        {
            var clouds = Clouds;
            scalarScratch[0] = clouds.GetFloat("_uCloudsCoverage"); scalarScratch[1] = clouds.GetFloat("_uCloudsBottom"); scalarScratch[2] = clouds.GetFloat("_uCloudsHeight");
            scalarScratch[3] = sky.CurrentFogAmount; scalarScratch[4] = sky.UniStormWindZone.windMain;
            scalarScratch[5] = sky.SunIntensity; scalarScratch[6] = sky.MoonIntensity;
            scalarScratch[7] = Shader.GetGlobalFloat("_WetnessStrength"); scalarScratch[8] = Shader.GetGlobalFloat("_SnowStrength");
            scalarScratch[9] = sky.SunAttenuationMultipler; scalarScratch[10] = sky.m_CurrentCloudHeight;
            scalarScratch[11] = sky.CurrentWindIntensity; scalarScratch[12] = RenderSettings.fogDensity; scalarScratch[13] = sky.FogLightFalloff;
            for (int i = 0; i < particles.Length; i++) particleScratch[i] = particles[i].emission.rateOverTime.constant;
            for (int i = 0; i < sounds.Length; i++) soundScratch[i] = sounds[i].volume;
            return WeatherCapture.Reuse(current, Id(sky.CurrentWeatherType), Id(sky.NextWeatherType), Mathf.Clamp(sky.Temperature, -150, 200),
                Wrap(sky.MoonPhaseIndex, sky.MoonPhaseList.Count > 0 ? Math.Min(sky.MoonPhaseList.Count, 32) : 32),
                Wrap(sky.HourToChangeWeather, 24), (byte)sky.WeatherGenerationMethod, sky.WeatherGeneration == UniStormSystem.EnableFeature.Enabled,
                Forecast(), scalarScratch, particleScratch, soundScratch);
        }
        private WeatherGradient CaptureGradient(ulong id)
        {
            var g = (Gradient)gradients[id].GetValue(sky);
            var previous = currentGradients != null && currentGradients.TryGetValue(id, out var existing) ? existing : null;
            return WeatherCapture.ReuseGradient(previous, g.colorKeys, g.alphaKeys);
        }
        private void Validate(WeatherState state)
        {
            if (!types.ContainsKey(state.Current) || (state.Next != 0 && !types.ContainsKey(state.Next)) || state.Forecast.Any(id => !types.ContainsKey(id)) ||
                state.Moon >= sky.MoonPhaseList.Count || state.Particles.Count != particles.Length || state.Sounds.Count != sounds.Length)
                throw new InvalidDataException("Weather state does not match the local catalog.");
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            WeatherState? state = null; var colors = new Dictionary<ulong, WeatherGradient>();
            foreach (var record in records.Values.Where(r => r.Adapter == WeatherState.Adapter))
            {
                if (record.Id == WeatherState.Entity) { state = WeatherState.Decode(record.Payload); Validate(state); }
                else if (record.Id == LightningEntity) { Lightning(record.Payload, false); }
                else if (gradients.ContainsKey(record.Id)) colors.Add(record.Id, WeatherGradient.Decode(record.Payload));
                else throw new InvalidDataException("Unknown weather record.");
            }
            foreach (var p in colors) currentGradients[p.Key] = p.Value;
            if (state != null) { current = state; Apply(true); }
            else foreach (var p in colors) ApplyGradient(p.Key, p.Value);
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != WeatherState.Adapter) return;
            if (record.Id == WeatherState.Entity) { var state = WeatherState.Decode(record.Payload); Validate(state); current = state; Apply(false); }
            else if (record.Id == LightningEntity) Lightning(record.Payload, true);
            else if (gradients.ContainsKey(record.Id)) { var value = WeatherGradient.Decode(record.Payload); currentGradients[record.Id] = value; ApplyGradient(record.Id, value); }
            else throw new InvalidDataException("Unknown weather record.");
        }
        private void Lightning(byte[] payload, bool strike)
        {
            if (payload.Length != 5) throw new InvalidDataException("Invalid lightning record.");
            uint count = BitConverter.ToUInt32(payload, 0);
            bool fresh = lightningBaseline && count != lightningCount;
            lightningCount = count; lightningBaseline = true;
            var system = sky.m_UniStormLightningSystem;
            if (!strike || !fresh || system == null) return;
            applying = true;
            try
            {
                if (payload[4] < sky.LightningFlashPatterns.Count) system.LightningCurve = sky.LightningFlashPatterns[payload[4]];
                system.GenerateLightning();
            }
            catch (Exception ex) { Debug.LogWarning("MonMP lightning replay failed: " + ex.Message); }
            finally { applying = false; }
        }
        internal void HostLightning(UniStorm.Utility.LightningSystem system)
        {
            if (!host || disposed || session?.IsRunning != true || channel?.Ready != true || system != sky.m_UniStormLightningSystem) return;
            int pattern = sky.LightningFlashPatterns.IndexOf(system.LightningCurve);
            channel.Publish(LightningEntity, EncodeLightning(++lightningCount, (byte)Mathf.Clamp(pattern, 0, 255)));
        }
        private void StopWeatherCoroutines()
        {
            foreach (var field in typeof(UniStormSystem).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(f => f.FieldType == typeof(Coroutine)))
            { if (field.GetValue(sky) is Coroutine routine) sky.StopCoroutine(routine); field.SetValue(sky, null); }
        }
        private void Apply(bool force, bool restore = false)
        {
            applying = true;
            try
            {
                bool changed = !initialized || sky.CurrentWeatherType != types[current.Current] || force;
                sky.CurrentWeatherType = types[current.Current]; sky.NextWeatherType = current.Next == 0 ? null : types[current.Next];
                sky.Temperature = current.Temperature; sky.HourToChangeWeather = current.ChangeHour;
                sky.WeatherGenerationMethod = (UniStormSystem.WeatherGenerationMethodEnum)current.GenerationMethod;
                sky.WeatherForecast.Clear(); sky.WeatherForecast.AddRange(current.Forecast.Select(id => types[id]));
                sky.WeatherGeneration = restore && current.GenerationEnabled ? UniStormSystem.EnableFeature.Enabled : UniStormSystem.EnableFeature.Disabled;
                generated.SetValue(sky, restore && originalGenerated);
                if (changed) { StopWeatherCoroutines(); sky.InitializeWeather(false); initialized = true; }
                if (sky.MoonPhaseIndex != current.Moon || changed) { sky.MoonPhaseIndex = current.Moon; moonPhase.Invoke(sky, null); }
                var v = current.Scalars;
                Clouds.SetFloat("_uCloudsCoverage", v[0]); Clouds.SetFloat("_uCloudsBottom", v[1]); Clouds.SetFloat("_uCloudsHeight", v[2]);
                sky.CurrentFogAmount = v[3]; sky.UniStormWindZone.windMain = v[4]; sky.SunIntensity = v[5]; sky.MoonIntensity = v[6];
                Shader.SetGlobalFloat("_WetnessStrength", v[7]); Shader.SetGlobalFloat("_SnowStrength", v[8]); sky.SunAttenuationMultipler = v[9];
                sky.m_CurrentCloudHeight = v[10]; sky.CurrentWindIntensity = v[11]; RenderSettings.fogDensity = v[12]; sky.FogLightFalloff = v[13];
                for (int i = 0; i < particles.Length; i++) { var emission = particles[i].emission; emission.rateOverTime = new ParticleSystem.MinMaxCurve(current.Particles[i]); }
                for (int i = 0; i < sounds.Length; i++) { sounds[i].volume = current.Sounds[i]; if (current.Sounds[i] > 0 && !sounds[i].isPlaying) sounds[i].Play(); }
                if (changed) foreach (var p in currentGradients) ApplyGradient(p.Key, p.Value);
                UnityEngine.Object.FindObjectOfType<UI_ClockCalendar>()?.Tick(sky.GetCurrentTick, UnityEngine.Object.FindObjectOfType<TickSystem>().Get_DayOfTheWeek);
            }
            finally { applying = false; }
        }
        private void ApplyGradient(ulong id, WeatherGradient state)
        {
            var colors = new GradientColorKey[state.Colors.Count / 5]; var alphas = new GradientAlphaKey[state.Alphas.Count / 2];
            for (int i = 0; i < colors.Length; i++) { int n = i * 5; colors[i] = new GradientColorKey(new Color(state.Colors[n + 1], state.Colors[n + 2], state.Colors[n + 3], state.Colors[n + 4]), state.Colors[n]); }
            for (int i = 0; i < alphas.Length; i++) alphas[i] = new GradientAlphaKey(state.Alphas[i * 2 + 1], state.Alphas[i * 2]);
            ((Gradient)gradients[id].GetValue(sky)).SetKeys(colors, alphas);
            AccessTools.Field(typeof(UniStormSystem), switcherNames[Array.IndexOf(gradientNames, gradients[id].Name)]).SetValue(sky, colors);
        }
        public void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            if (host && Time.unscaledTime >= nextCapture)
            {
                nextCapture = Time.unscaledTime + 0.25f; current = Capture();
                var publication = Publication(WeatherState.Entity);
                channel!.Records.TryGetValue(WeatherState.Entity, out var record);
                if (!publication.CanSkip(current, record))
                {
                    var payload = current.Encode(); channel.Publish(WeatherState.Entity, payload);
                    publication.Published(current, channel.Records.TryGetValue(WeatherState.Entity, out var published) ? published : null, payload);
                }
                foreach (ulong id in gradients.Keys)
                {
                    var gradient = CaptureGradient(id); currentGradients[id] = gradient;
                    publication = Publication(id); channel.Records.TryGetValue(id, out record);
                    if (publication.CanSkip(gradient, record)) continue;
                    var payload = gradient.Encode(); channel.Publish(id, payload);
                    publication.Published(gradient, channel.Records.TryGetValue(id, out var published) ? published : null, payload);
                }
            }
        }
        private CapturePublication Publication(ulong id)
        {
            if (!publications.TryGetValue(id, out var publication)) publications.Add(id, publication = new CapturePublication());
            return publication;
        }
        public void BeforeNativeUpdate()
        {
            if (!BlocksLocalChanges) return;
            sky.WeatherGeneration = UniStormSystem.EnableFeature.Disabled;
            if (initialized && (sky.CurrentWeatherType != types[current.Current] || sky.Temperature != current.Temperature)) Apply(false);
        }
        // #377: Unity can destroy weather children before this adapter's OnDestroy cleanup.
        // InitializeWeather dereferences both native particle lists; the sky surviving alone
        // does not make native restoration safe. Ordinary live disconnect still restores all.
        private bool CanRestoreOriginalScene()
        {
            if (sky == null || !originalScene.IsValid() || !originalScene.isLoaded || sky.gameObject.scene != originalScene ||
                !sky.UniStormInitialized || Clouds == null || sky.UniStormWindZone == null ||
                particles.Any(value => value == null) || sounds.Any(value => value == null) ||
                sky.WeatherEffectsList == null || sky.WeatherEffectsList.Any(value => value == null) ||
                sky.AdditionalWeatherEffectsList == null || sky.AdditionalWeatherEffectsList.Any(value => value == null) ||
                sky.WeatherSoundsList == null || sky.WeatherSoundsList.Any(value => value == null) ||
                !types.TryGetValue(original.Current, out var weather) || weather == null ||
                original.Next != 0 && (!types.TryGetValue(original.Next, out var next) || next == null) ||
                original.Forecast.Any(id => !types.TryGetValue(id, out var forecast) || forecast == null)) return false;
            // Native InitializeWeather's fog branch and CalculateMoonPhase also use scene
            // components/materials, before our own emission/audio and clock restoration.
            if (sky.FogType == UniStormSystem.FogTypeEnum.UnistormFog &&
                (sky.m_UniStormAtmosphericFog == null || sky.SunObjectMaterial == null)) return false;
            if (!(AccessTools.Field(typeof(UniStormSystem), "m_MoonRenderer").GetValue(sky) is UnityEngine.Object moonRenderer) || moonRenderer == null ||
                !(AccessTools.Field(typeof(UniStormSystem), "m_MoonPhaseMaterial").GetValue(sky) is UnityEngine.Object moonMaterial) || moonMaterial == null ||
                sky.MoonPhaseList == null || original.Moon >= sky.MoonPhaseList.Count ||
                sky.MoonPhaseList[original.Moon].MoonPhaseTexture == null) return false;
            if (weather.UseWeatherEffect == WeatherType.Yes_No.Yes &&
                (weather.WeatherEffect == null || !sky.WeatherEffectsList.Any(value => value.name == weather.WeatherEffect.name + " (UniStorm)"))) return false;
            if (weather.UseAdditionalWeatherEffect == WeatherType.Yes_No.Yes &&
                (weather.AdditionalWeatherEffect == null || !sky.AdditionalWeatherEffectsList.Any(value => value.name == weather.AdditionalWeatherEffect.name + " (UniStorm)"))) return false;
            return UnityEngine.Object.FindObjectOfType<UI_ClockCalendar>() == null || UnityEngine.Object.FindObjectOfType<TickSystem>() != null;
        }
        public void Dispose()
        {
            if (disposed) return;
            try
            {
                if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
                if (!host && CanRestoreOriginalScene())
                { current = original; foreach (var p in originalGradients) currentGradients[p.Key] = p.Value; Apply(true, true); }
            }
            finally { disposed = true; applying = false; initialized = false; session = null; channel = null; }
        }
    }
}
