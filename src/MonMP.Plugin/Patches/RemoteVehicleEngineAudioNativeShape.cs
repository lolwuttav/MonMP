using System;
using System.Reflection;

namespace MonMP.Plugin.Patches
{
    // SoundManager.Update is part of the game assembly rather than MonMP. Keep the
    // remote-only bypass disabled unless the small set of native members this
    // presentation path uses still has the expected shape.
    internal static class RemoteVehicleEngineAudioNativeShape
    {
        private const BindingFlags PublicInstanceDeclared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        internal static bool IsSupported(Type soundManagerType, Type engineRunningType, Type vehicleComponentType,
            Type soundComponentType, Type vehicleControllerType, Type vehicleType, Type multiplayerInstanceType,
            Type engineComponentType, Type audioSourceType, Type audioClipType, Type audioMixerGroupType, Type audioMixerType)
        {
            if (soundManagerType == null || engineRunningType == null || vehicleComponentType == null ||
                soundComponentType == null || vehicleControllerType == null || vehicleType == null ||
                multiplayerInstanceType == null || engineComponentType == null || audioSourceType == null ||
                audioClipType == null || audioMixerGroupType == null || audioMixerType == null)
                return false;

            if (!vehicleComponentType.IsAssignableFrom(soundManagerType) ||
                !soundComponentType.IsAssignableFrom(engineRunningType) || !vehicleType.IsAssignableFrom(vehicleControllerType) ||
                !multiplayerInstanceType.IsEnum || !Array.Exists(Enum.GetNames(multiplayerInstanceType), name => name == "Remote"))
                return false;

            FieldInfo? runningField = soundManagerType.GetField("engineRunningComponent", PublicInstanceDeclared);
            if (runningField == null || runningField.FieldType != engineRunningType)
                return false;

            PropertyInfo? controllerProperty = vehicleComponentType.GetProperty("VehicleController", PublicInstanceDeclared);
            if (!IsPublicInstanceProperty(controllerProperty, vehicleControllerType, requireSetter: false))
                return false;
            PropertyInfo? activeProperty = vehicleComponentType.GetProperty("Active", PublicInstanceDeclared);
            if (!IsPublicInstanceProperty(activeProperty, typeof(bool), requireSetter: false))
                return false;
            PropertyInfo? isOnProperty = vehicleComponentType.GetProperty("IsOn", PublicInstanceDeclared);
            PropertyInfo? isEnabledProperty = vehicleComponentType.GetProperty("IsEnabled", PublicInstanceDeclared);
            PropertyInfo? initializedProperty = vehicleComponentType.GetProperty("Initialized", PublicInstanceDeclared);
            PropertyInfo? lodIndexProperty = vehicleComponentType.GetProperty("LodIndex", PublicInstanceDeclared);
            if (!IsPublicInstanceProperty(isOnProperty, typeof(bool), requireSetter: true) ||
                !IsPublicInstanceProperty(isEnabledProperty, typeof(bool), requireSetter: false) ||
                !IsPublicInstanceProperty(initializedProperty, typeof(bool), requireSetter: false) ||
                !IsPublicInstanceProperty(lodIndexProperty, typeof(int), requireSetter: true))
                return false;

            PropertyInfo? vehicleModeProperty = vehicleType.GetProperty("VehicleMultiplayerInstanceType", PublicInstanceDeclared);
            if (!IsPublicInstanceProperty(vehicleModeProperty, multiplayerInstanceType, requireSetter: false))
                return false;

            PropertyInfo? throttleProperty = engineComponentType.GetProperty("ThrottlePosition", PublicInstanceDeclared);
            if (!IsPublicInstanceProperty(throttleProperty, typeof(float), requireSetter: true))
                return false;

            if (!HasPublicInstanceField(soundComponentType, "audioMixerGroup", audioMixerGroupType) ||
                !HasPublicInstanceField(engineComponentType, "throttlePosition", typeof(float)))
                return false;

            PropertyInfo? sourceProperty = soundComponentType.GetProperty("Source", PublicInstanceDeclared);
            PropertyInfo? clipProperty = soundComponentType.GetProperty("Clip", PublicInstanceDeclared);
            PropertyInfo? mixerProperty = audioMixerGroupType.GetProperty("audioMixer", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? sourceClipProperty = audioSourceType.GetProperty("clip", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? sourceEnabledProperty = audioSourceType.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo? setDefaults = engineRunningType.GetMethod("SetDefaults", PublicInstanceDeclared, null,
                new[] { vehicleControllerType }, null);
            if (!IsPublicInstanceProperty(sourceProperty, audioSourceType, requireSetter: false) ||
                !IsPublicInstanceProperty(clipProperty, audioClipType, requireSetter: true) ||
                !IsPublicInstanceProperty(mixerProperty, audioMixerType, requireSetter: false) ||
                !IsPublicInstanceProperty(sourceClipProperty, audioClipType, requireSetter: true) ||
                !IsPublicInstanceProperty(sourceEnabledProperty, typeof(bool), requireSetter: true) ||
                setDefaults == null || setDefaults.ReturnType != typeof(void) || setDefaults.IsStatic || setDefaults.IsGenericMethod)
                return false;

            if (!IsPublicVoidMethod(soundComponentType, "Enable") || !IsPublicVoidMethod(soundComponentType, "Disable"))
                return false;

            FieldInfo? inputField = vehicleControllerType.GetField("input", PublicInstanceDeclared);
            FieldInfo? stateField = inputField?.FieldType.GetField("states", PublicInstanceDeclared);
            if (inputField == null || stateField == null ||
                !HasPublicInstanceField(stateField.FieldType, "throttle", typeof(float)))
                return false;

            FieldInfo? powertrainField = vehicleControllerType.GetField("powertrain", PublicInstanceDeclared);
            FieldInfo? engineField = powertrainField?.FieldType.GetField("engine", PublicInstanceDeclared);
            if (powertrainField == null || engineField == null || engineField.FieldType != engineComponentType)
                return false;

            return IsPublicVoidUpdate(soundManagerType) && IsPublicVoidUpdate(engineRunningType);
        }

        private static bool HasPublicInstanceField(Type type, string name, Type fieldType)
        {
            FieldInfo? field = type.GetField(name, PublicInstanceDeclared);
            return field != null && field.FieldType == fieldType;
        }

        private static bool IsPublicVoidUpdate(Type type)
        {
            MethodInfo[] updates = Array.FindAll(type.GetMethods(PublicInstanceDeclared), method => method.Name == "Update");
            if (updates.Length != 1)
                return false;
            MethodInfo? update = type.GetMethod("Update", PublicInstanceDeclared, null, Type.EmptyTypes, null);
            return update != null && update.DeclaringType == type && update.ReturnType == typeof(void) && !update.IsGenericMethod;
        }

        private static bool IsPublicVoidMethod(Type type, string name)
        {
            MethodInfo? method = type.GetMethod(name, PublicInstanceDeclared, null, Type.EmptyTypes, null);
            return method != null && method.ReturnType == typeof(void) && !method.IsStatic && !method.IsGenericMethod;
        }

        private static bool IsPublicInstanceProperty(PropertyInfo? property, Type valueType, bool requireSetter)
        {
            if (property == null || property.PropertyType != valueType || property.GetIndexParameters().Length != 0)
                return false;
            MethodInfo? getter = property.GetGetMethod();
            MethodInfo? setter = property.GetSetMethod();
            return getter != null && !getter.IsStatic && (!requireSetter || setter != null && !setter.IsStatic);
        }
    }
}
