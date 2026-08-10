#nullable enable

namespace AbyssMod.Services;

/// <summary>
/// Separates the bridge's internal monotonically increasing registration counter from the
/// Continue coordinator's observable liveness contract.  A retained counter is not evidence
/// that the old FloorSelection controller still exists: absence must be represented as zero so
/// the bounded rebind gate can wait for the next exact owner registration.
/// </summary>
internal static class NetherRuntimeGenerationVisibility
{
    public static long ForLiveFloorSelection(object? liveFloorSelection, long monotonicGeneration) =>
        liveFloorSelection == null ? 0 : monotonicGeneration;
}
