namespace AbyssMod.Services;

internal readonly record struct ConfigFileStamp(long LastWriteTimeUtcTicks, long Length);

internal enum ConfigReloadDecision
{
    NoChange = 0,
    AwaitingStableChange = 1,
    Reload = 2,
}

/// <summary>
/// Requires the same changed file stamp to be observed twice before reloading.
/// This avoids reading a cfg while an editor is still writing or replacing it.
/// </summary>
internal sealed class ConfigAutoReloadState
{
    private bool _initialized;
    private ConfigFileStamp _acceptedStamp;
    private bool _hasPendingStamp;
    private ConfigFileStamp _pendingStamp;

    public ConfigReloadDecision Observe(bool fileExists, ConfigFileStamp stamp)
    {
        if (!fileExists)
        {
            _hasPendingStamp = false;
            return ConfigReloadDecision.NoChange;
        }

        if (!_initialized)
        {
            Acknowledge(stamp);
            return ConfigReloadDecision.NoChange;
        }

        if (stamp == _acceptedStamp)
        {
            _hasPendingStamp = false;
            return ConfigReloadDecision.NoChange;
        }

        if (!_hasPendingStamp || stamp != _pendingStamp)
        {
            _pendingStamp = stamp;
            _hasPendingStamp = true;
            return ConfigReloadDecision.AwaitingStableChange;
        }

        return ConfigReloadDecision.Reload;
    }

    public void Acknowledge(ConfigFileStamp stamp)
    {
        _initialized = true;
        _acceptedStamp = stamp;
        _hasPendingStamp = false;
    }
}
