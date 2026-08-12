using System;
using System.IO;
using AbyssMod.Services;

namespace AbyssMod;

/// <summary>
/// Polls the BepInEx cfg from Unity's main thread and reloads it after a
/// changed file stamp remains stable across two polls.
/// </summary>
internal static class ConfigAutoReload
{
    private const float PollIntervalSeconds = 0.25f;
    private const float FailureRetrySeconds = 1.0f;

    private static readonly ConfigAutoReloadState State = new();
    private static float _nextPollTime;
    private static string _lastReadError = string.Empty;

    public static void Update(float unscaledTime)
    {
        if (unscaledTime < _nextPollTime)
            return;

        _nextPollTime = unscaledTime + PollIntervalSeconds;
        if (!TryReadCurrentStamp(out ConfigFileStamp stamp, out string error))
        {
            State.Observe(fileExists: false, default);
            LogReadErrorOnce(error);
            return;
        }

        _lastReadError = string.Empty;
        if (State.Observe(fileExists: true, stamp) != ConfigReloadDecision.Reload)
            return;

        try
        {
            Plugin.ConfigFile.Reload();
            Patches.EnhancePatch.ReloadNovelLive2DScale();
            AcknowledgeCurrent();
            Logger.Info($"[Config] Auto-reloaded: {Plugin.ConfigFile.ConfigFilePath}");
        }
        catch (Exception ex)
        {
            _nextPollTime = unscaledTime + FailureRetrySeconds;
            Logger.Warn(
                $"[Config] Auto-reload failed; retrying: {ex.GetType().Name}: {ex.Message}"
            );
        }
    }

    public static void AcknowledgeCurrent()
    {
        if (TryReadCurrentStamp(out ConfigFileStamp stamp, out _))
            State.Acknowledge(stamp);
    }

    private static bool TryReadCurrentStamp(
        out ConfigFileStamp stamp,
        out string error
    )
    {
        try
        {
            var file = new FileInfo(Plugin.ConfigFile.ConfigFilePath);
            if (!file.Exists)
            {
                stamp = default;
                error = string.Empty;
                return false;
            }

            stamp = new ConfigFileStamp(file.LastWriteTimeUtc.Ticks, file.Length);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            stamp = default;
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static void LogReadErrorOnce(string error)
    {
        if (error.Length == 0 || string.Equals(error, _lastReadError, StringComparison.Ordinal))
            return;

        _lastReadError = error;
        Logger.Warn($"[Config] Auto-reload file check failed: {error}");
    }
}
