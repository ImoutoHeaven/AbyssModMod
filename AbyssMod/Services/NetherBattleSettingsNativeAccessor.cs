#nullable enable

using System;
using System.Linq;
using System.Reflection;

namespace AbyssMod.Services;

/// <summary>
/// Exact packaged-game binding for exploration/Nether battle settings.  The game exposes this
/// surface through Project.Ingame.IIngameUserSettings and applies it to BottomRightView via
/// ApplyUserSettings; no property-name fallback or guessed component lookup is used.
/// </summary>
internal sealed class NetherBattleSettingsNativeAccessor : INetherBattleSettingsNative
{
    private const string SettingsInterfaceTypeName = "Project.Ingame.IIngameUserSettings";
    private const string SpeedTypeName = "Project.GameSpeedType";
    private readonly object _settings;
    private readonly MethodInfo _getAuto;
    private readonly MethodInfo _setAuto;
    private readonly MethodInfo _getSpeed;
    private readonly MethodInfo _setSpeed;
    private readonly Type _speedType;

    private NetherBattleSettingsNativeAccessor(
        object settings,
        MethodInfo getAuto,
        MethodInfo setAuto,
        MethodInfo getSpeed,
        MethodInfo setSpeed,
        Type speedType
    )
    {
        _settings = settings;
        _getAuto = getAuto;
        _setAuto = setAuto;
        _getSpeed = getSpeed;
        _setSpeed = setSpeed;
        _speedType = speedType;
    }

    public static bool TryCreate(object settings, out NetherBattleSettingsNativeAccessor? accessor, out string error)
    {
        accessor = null;
        error = string.Empty;
        if (settings == null)
        {
            error = "missing-native-ingame-user-settings";
            return false;
        }

        Type concrete = settings.GetType();
        bool implementsExpectedInterface = concrete.GetInterfaces().Any(type =>
            string.Equals(type.FullName, SettingsInterfaceTypeName, StringComparison.Ordinal)
        );
        if (!implementsExpectedInterface)
        {
            error = "unexpected-native-settings-interface:" + concrete.FullName;
            return false;
        }

        if (!TryResolveExact(concrete, "get_IsAuto", Type.EmptyTypes, typeof(bool), out MethodInfo? getAuto, out error)
            || !TryResolveExact(concrete, "set_IsAuto", new[] { typeof(bool) }, typeof(void), out MethodInfo? setAuto, out error)
            || !TryResolveSpeedMethods(concrete, out MethodInfo? getSpeed, out MethodInfo? setSpeed, out Type? speedType, out error))
        {
            return false;
        }

        accessor = new NetherBattleSettingsNativeAccessor(settings, getAuto!, setAuto!, getSpeed!, setSpeed!, speedType!);
        return true;
    }

    public bool TryRead(out bool autoEnabled, out int speed, out string error)
    {
        autoEnabled = false;
        speed = 0;
        error = string.Empty;
        try
        {
            object? rawAuto = _getAuto.Invoke(_settings, Array.Empty<object>());
            object? rawSpeed = _getSpeed.Invoke(_settings, Array.Empty<object>());
            if (rawAuto == null || rawSpeed == null)
            {
                error = "native-settings-read-null";
                return false;
            }
            autoEnabled = Convert.ToBoolean(rawAuto);
            speed = Convert.ToInt32(rawSpeed);
            if (speed is < 0 or > 3)
            {
                error = "native-settings-speed-out-of-range:" + speed;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ":" + ex.Message;
            return false;
        }
    }

    public bool TryForceAutoAndHighestSpeed(out string error) => TryWrite(true, 3, out error);

    public bool TryWrite(bool autoEnabled, int speed, out string error)
    {
        error = string.Empty;
        if (speed is < 0 or > 3)
        {
            error = "invalid-native-settings-speed:" + speed;
            return false;
        }
        try
        {
            _setAuto.Invoke(_settings, new object[] { autoEnabled });
            object speedValue = Enum.ToObject(_speedType, speed);
            _setSpeed.Invoke(_settings, new[] { speedValue });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ":" + ex.Message;
            return false;
        }
    }

    private static bool TryResolveSpeedMethods(
        Type concrete,
        out MethodInfo? getSpeed,
        out MethodInfo? setSpeed,
        out Type? speedType,
        out string error
    )
    {
        getSpeed = null;
        setSpeed = null;
        speedType = null;
        error = string.Empty;
        MethodInfo[] getters = concrete.GetMethods(Flags)
            .Where(method => method.Name == "get_Speed" && method.GetParameters().Length == 0
                && string.Equals(method.ReturnType.FullName, SpeedTypeName, StringComparison.Ordinal))
            .ToArray();
        if (getters.Length != 1)
        {
            error = "native-settings-get-speed-not-exact:" + getters.Length;
            return false;
        }
        Type resolvedSpeedType = getters[0].ReturnType;
        MethodInfo[] setters = concrete.GetMethods(Flags)
            .Where(method => method.Name == "set_Speed" && method.ReturnType == typeof(void)
                && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == resolvedSpeedType)
            .ToArray();
        if (setters.Length != 1)
        {
            error = "native-settings-set-speed-not-exact:" + setters.Length;
            return false;
        }
        speedType = resolvedSpeedType;
        getSpeed = getters[0];
        setSpeed = setters[0];
        return true;
    }

    private static bool TryResolveExact(
        Type concrete,
        string name,
        Type[] parameters,
        Type returnType,
        out MethodInfo? result,
        out string error
    )
    {
        MethodInfo[] matches = concrete.GetMethods(Flags)
            .Where(method => method.Name == name && method.ReturnType == returnType
                && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters))
            .ToArray();
        result = matches.Length == 1 ? matches[0] : null;
        error = result == null ? "native-settings-" + name + "-not-exact:" + matches.Length : string.Empty;
        return result != null;
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
}

/// <summary>Owns the exact BottomRightView registration lifetime for the settings accessor.</summary>
internal static class NetherBattleSettingsNativeRegistry
{
    private static readonly object Gate = new();
    private static object? _owner;
    private static NetherBattleSettingsNativeAccessor? _accessor;

    public static void Register(object owner, object settings)
    {
        string error = string.Empty;
        if (owner == null || !NetherBattleSettingsNativeAccessor.TryCreate(settings, out NetherBattleSettingsNativeAccessor? next, out error))
        {
            Logger.Error("[F12][NetherClimb] native battle settings accessor unavailable: " + error);
            return;
        }

        lock (Gate)
        {
            if (_accessor != null)
                NetherBattleSettingsLease.UnregisterNativeAccessor(_accessor);
            _owner = owner;
            _accessor = next;
            NetherBattleSettingsLease.RegisterNativeAccessor(next!);
        }
        // Recovery belongs to the controller lifecycle, not accessor registration itself.  The
        // callback runs after the exact native object is stored and can therefore defer/retry
        // with the persisted lease phase as its authority.
        NetherAutoClimbController.OnBattleSettingsAccessorRegistered();
    }

    public static void Unregister(object owner)
    {
        if (owner == null)
            return;
        bool unregistered = false;
        lock (Gate)
        {
            if (!ReferenceEquals(_owner, owner) || _accessor == null)
                return;
            NetherBattleSettingsLease.UnregisterNativeAccessor(_accessor);
            _accessor = null;
            _owner = null;
            unregistered = true;
        }
        if (unregistered)
            NetherAutoClimbController.OnBattleSettingsAccessorUnregistered();
    }
}
