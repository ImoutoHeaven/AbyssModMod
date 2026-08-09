#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherContentAcquiredConfirmLeaseTests
{
    [Fact]
    public void Owned_popup_close_can_be_claimed_once_only_by_its_floor_parent_generation()
    {
        var popup = new object();
        var close = new object();
        var lease = new NetherContentAcquiredConfirmLease();

        Assert.True(lease.Register(
            popup,
            close,
            sequence: 7,
            NetherActionKind.SelectFloor,
            ownerGeneration: 4,
            runtimeGeneration: 9
        ));

        NetherContentAcquiredConfirmClaim stale = lease.ClaimOwned(ownerGeneration: 3);
        Assert.Equal(NetherContentAcquiredConfirmClaimKind.CorrelationMismatch, stale.Kind);

        NetherContentAcquiredConfirmClaim claimed = lease.ClaimOwned(ownerGeneration: 4);
        Assert.Equal(NetherContentAcquiredConfirmClaimKind.Claimed, claimed.Kind);
        Assert.Same(close, claimed.Close);
        Assert.Equal(7, claimed.Sequence);
        Assert.Equal(NetherContentAcquiredConfirmClaimKind.None, lease.ClaimOwned(4).Kind);
    }

    [Fact]
    public void Recovered_popup_close_requires_the_same_live_runtime_generation()
    {
        var popup = new object();
        var close = new object();
        var lease = new NetherContentAcquiredConfirmLease();

        Assert.True(lease.Register(
            popup,
            close,
            sequence: 12,
            NetherActionKind.None,
            ownerGeneration: 0,
            runtimeGeneration: 5
        ));

        Assert.Equal(
            NetherContentAcquiredConfirmClaimKind.CorrelationMismatch,
            lease.ClaimRecovered(runtimeGeneration: 4).Kind
        );
        NetherContentAcquiredConfirmClaim claimed = lease.ClaimRecovered(runtimeGeneration: 5);
        Assert.Equal(NetherContentAcquiredConfirmClaimKind.Claimed, claimed.Kind);
        Assert.Same(close, claimed.Close);
        Assert.Equal(12, claimed.Sequence);
        Assert.Equal(NetherContentAcquiredConfirmClaimKind.None, lease.ClaimRecovered(5).Kind);
    }

    [Fact]
    public void Missing_close_fails_closed_and_is_never_retried()
    {
        var lease = new NetherContentAcquiredConfirmLease();

        Assert.True(lease.Register(
            new object(),
            close: null,
            sequence: 2,
            NetherActionKind.None,
            ownerGeneration: 0,
            runtimeGeneration: 1
        ));

        Assert.Equal(
            NetherContentAcquiredConfirmClaimKind.MissingClose,
            lease.ClaimRecovered(runtimeGeneration: 1).Kind
        );
        Assert.Equal(NetherContentAcquiredConfirmClaimKind.None, lease.ClaimRecovered(1).Kind);
    }

    [Fact]
    public void Only_the_exact_popup_close_boundary_invalidates_the_registration()
    {
        var popup = new object();
        var lease = new NetherContentAcquiredConfirmLease();
        Assert.True(lease.Register(
            popup,
            new object(),
            sequence: 3,
            NetherActionKind.SelectFloor,
            ownerGeneration: 2,
            runtimeGeneration: 6
        ));

        Assert.False(lease.InvalidatePopup(new object()));
        Assert.True(lease.InvalidatePopup(popup));
        Assert.Equal(NetherContentAcquiredConfirmClaimKind.None, lease.ClaimOwned(2).Kind);
    }
}
