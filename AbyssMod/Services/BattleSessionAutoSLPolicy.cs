using System;

namespace AbyssMod.Services;

public static class BattleSessionAutoSLPolicy
{
    public static float ClampCooldown(float seconds) => Math.Max(0f, seconds);

    public static bool ShouldRetry(BattleDropProbeReport report) =>
        report.Error.Length == 0 && report.RareDropCount == 0;
}
