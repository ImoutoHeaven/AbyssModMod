using System.Diagnostics;
using System.Threading;

namespace AbyssMod.Services;

internal static class NetherResumeAutoSLGate
{
    private const int MaxAgeSeconds = 600;
    private static long _armedAtTimestamp;

    public static void Arm()
    {
        Volatile.Write(ref _armedAtTimestamp, Stopwatch.GetTimestamp());
    }

    public static void Disarm()
    {
        Interlocked.Exchange(ref _armedAtTimestamp, 0);
    }

    public static bool TryConsume()
    {
        long armedAt = Interlocked.Exchange(ref _armedAtTimestamp, 0);
        if (armedAt == 0)
            return false;

        long elapsed = Stopwatch.GetTimestamp() - armedAt;
        return elapsed >= 0 && elapsed <= (long)MaxAgeSeconds * Stopwatch.Frequency;
    }
}
