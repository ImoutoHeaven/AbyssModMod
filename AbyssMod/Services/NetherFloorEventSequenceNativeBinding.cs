#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Exact current-version task seam for an interactive floor.  Unlike
/// OnFloorClickedEventAsync, this task remains pending while the native popup is open and
/// completes only after its event sequence has settled.
/// </summary>
internal static class NetherFloorEventSequenceNativeBinding
{
    public const string ControllerTypeName =
        "Project.Nether.FloorSelection.SubViewController";

    public static NetherNativeMethodDescriptor SequenceDescriptor { get; } = new(
        "ExecuteCurrentFloorEventSequenceAsync",
        Array.Empty<string>(),
        "Cysharp.Threading.Tasks.UniTask"
    ) { IsStatic = false };
}
