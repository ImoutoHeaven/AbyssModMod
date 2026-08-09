#nullable enable

namespace AbyssMod.Services;

/// <summary>
/// Defines when the FloorSelection owner created by a battle-result Next transition is ready
/// to return to ordinary planning. A Wait snapshot is modal by definition; completing before
/// its popup is registered would make the next update falsely diagnose an unsupported popup.
/// </summary>
internal static class NetherBattleResultReboundReadiness
{
    public static bool IsReady(NetherSessionStatus status, bool hasActivePopup) =>
        status != NetherSessionStatus.Wait || hasActivePopup;
}
