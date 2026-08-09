#nullable enable

namespace AbyssMod.Services;

/// <summary>
/// Exact current-version seams for the per-battle Nether result page. InitializeViewAsync
/// installs the visible Next button subscription; its generated callback is the native path
/// that returns to FloorSelection. No SceneHistory or server API fallback is permitted.
/// </summary>
internal static class NetherBattleResultNextNativeBinding
{
    public const string ControllerTypeName =
        "Project.BattleResult.NetherQuestBattleResultViewController";

    public static NetherNativeMethodDescriptor InitializeViewDescriptor { get; } = new(
        "InitializeViewAsync",
        new[] { "Il2CppSystem.Threading.CancellationToken" },
        "Cysharp.Threading.Tasks.UniTask"
    ) { IsStatic = false };

    public static NetherCodePopupInteropMethodBinding NextCallbackInterop { get; } = new(
        "_InitializeViewAsync_b__21_1",
        "<InitializeViewAsync>b__21_1",
        new[] { "UniRx.Unit" },
        "System.Void"
    ) { IsStatic = false };
}
