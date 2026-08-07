#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Bounds a legitimate asynchronous gap between a server status transition and its native UI
/// controller/task registration.  It never manufactures completion: expiry is a binding
/// failure so the coordinator returns control to the player instead of waiting forever.
/// </summary>
internal sealed class NetherNativeWaitGate
{
    private readonly int _maximumMissingPolls;
    private int _missingPolls;

    public NetherNativeWaitGate(int maximumMissingPolls)
    {
        if (maximumMissingPolls < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumMissingPolls));
        _maximumMissingPolls = maximumMissingPolls;
    }

    public NetherNativeActionResult AwaitRegistration(string flow)
    {
        if (string.IsNullOrWhiteSpace(flow))
            throw new ArgumentException("flow is required", nameof(flow));
        _missingPolls++;
        return _missingPolls <= _maximumMissingPolls
            ? NetherNativeActionResult.Started("awaiting-native-" + flow + "-task")
            : NetherNativeActionResult.BindingUnavailable("native-" + flow + "-task-timeout");
    }

    public void ObserveRegistration() => _missingPolls = 0;

    public void Clear() => _missingPolls = 0;
}
