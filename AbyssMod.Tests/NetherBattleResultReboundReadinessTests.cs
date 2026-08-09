#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherBattleResultReboundReadinessTests
{
    [Theory]
    [InlineData((int)NetherSessionStatus.Play, false, true)]
    [InlineData((int)NetherSessionStatus.Battle, false, true)]
    [InlineData((int)NetherSessionStatus.Wait, true, true)]
    [InlineData((int)NetherSessionStatus.Wait, false, false)]
    public void Wait_status_requires_its_modal_registration_before_result_handoff_completes(
        int statusValue,
        bool hasPopup,
        bool expected
    )
    {
        NetherSessionStatus status = (NetherSessionStatus)statusValue;
        Assert.Equal(expected, NetherBattleResultReboundReadiness.IsReady(status, hasPopup));
    }
}
