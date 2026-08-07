#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCodeReloadEpochCoordinatorTests
{
    [Fact]
    public void Completed_reroll_requires_changed_authoritative_offer_and_exactly_one_reload_before_epoch_advances()
    {
        var coordinator = new NetherCodeReloadEpochCoordinator();
        var owner = new NetherCodeReloadEpochOwner(NetherActionKind.SelectFloor, 3, 7);

        Assert.True(coordinator.Begin(owner, reloadCount: 3, Candidates(100, 200)));
        Assert.Equal(
            NetherNativeActionResultKind.Started,
            coordinator.Pump(
                () => NetherNativeActionResult.Completed("reroll-task-terminal"),
                () => Refresh(owner, reloadCount: 2, Candidates(300, 400))
            ).Kind
        );
        Assert.Equal(NetherCodeReloadEpochStage.AwaitingRefresh, coordinator.Stage);

        Assert.Equal(
            NetherNativeActionResultKind.Completed,
            coordinator.Pump(
                () => throw new Xunit.Sdk.XunitException("must-not-repoll-reroll"),
                () => Refresh(owner, reloadCount: 2, Candidates(300, 400))
            ).Kind
        );
        Assert.Equal(NetherCodeReloadEpochStage.Ready, coordinator.Stage);
        Assert.Equal(1, coordinator.DecisionEpoch);
        Assert.True(coordinator.IsOwner(owner));
    }

    [Theory]
    [InlineData(3, 100, 200)] // unchanged candidates
    [InlineData(3, 300, 400)] // reload count did not move
    public void Changed_offer_requires_exact_decrement_and_never_retries_after_a_fault(
        int afterReloadCount,
        long firstOffer,
        long secondOffer
    )
    {
        var coordinator = new NetherCodeReloadEpochCoordinator();
        var owner = new NetherCodeReloadEpochOwner(NetherActionKind.SelectFloor, 3, 7);
        Assert.True(coordinator.Begin(owner, reloadCount: 3, Candidates(100, 200)));

        coordinator.Pump(
            () => NetherNativeActionResult.Completed("reroll-task-terminal"),
            () => Refresh(owner, afterReloadCount, Candidates(firstOffer, secondOffer))
        );
        NetherNativeActionResult fault = coordinator.Pump(
            () => throw new Xunit.Sdk.XunitException("must-not-repoll-reroll"),
            () => Refresh(owner, afterReloadCount, Candidates(firstOffer, secondOffer))
        );

        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, fault.Kind);
        Assert.Equal(NetherCodeReloadEpochStage.Faulted, coordinator.Stage);
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            coordinator.Pump(
                () => throw new Xunit.Sdk.XunitException("must-not-retry"),
                () => throw new Xunit.Sdk.XunitException("must-not-recature")
            ).Kind
        );
    }

    [Fact]
    public void Wrong_live_owner_or_reroll_task_fault_fails_closed_without_a_decision_epoch()
    {
        var owner = new NetherCodeReloadEpochOwner(NetherActionKind.SelectFloor, 3, 7);
        var coordinator = new NetherCodeReloadEpochCoordinator();
        Assert.True(coordinator.Begin(owner, reloadCount: 2, Candidates(100)));
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            coordinator.Pump(
                () => NetherNativeActionResult.UnknownOutcome("native-reroll-fault"),
                () => Refresh(owner, 1, Candidates(200))
            ).Kind
        );
        Assert.Equal(0, coordinator.DecisionEpoch);

        coordinator.Reset();
        Assert.True(coordinator.Begin(owner, reloadCount: 2, Candidates(100)));
        coordinator.Pump(
            () => NetherNativeActionResult.Completed("reroll-task-terminal"),
            () => Refresh(owner, 1, Candidates(200))
        );
        NetherNativeActionResult wrongOwner = coordinator.Pump(
            () => throw new Xunit.Sdk.XunitException("must-not-repoll-reroll"),
            () => Refresh(owner with { Sequence = 8 }, 1, Candidates(200))
        );
        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, wrongOwner.Kind);
        Assert.Equal(0, coordinator.DecisionEpoch);
    }

    [Fact]
    public void Pending_reroll_is_bounded_and_never_advances_or_retries_after_timeout()
    {
        var coordinator = new NetherCodeReloadEpochCoordinator(maximumPendingPumps: 1);
        var owner = new NetherCodeReloadEpochOwner(NetherActionKind.SelectFloor, 3, 7);
        Assert.True(coordinator.Begin(owner, reloadCount: 2, Candidates(100)));

        Assert.Equal(
            NetherNativeActionResultKind.Started,
            coordinator.Pump(
                () => NetherNativeActionResult.Started("reroll-still-pending"),
                () => throw new Xunit.Sdk.XunitException("must-not-read-before-terminal")
            ).Kind
        );
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            coordinator.Pump(
                () => NetherNativeActionResult.Started("reroll-timeout"),
                () => throw new Xunit.Sdk.XunitException("must-not-read-after-timeout")
            ).Kind
        );
        Assert.Equal(NetherCodeReloadEpochStage.Faulted, coordinator.Stage);
        Assert.Equal(0, coordinator.DecisionEpoch);
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            coordinator.Pump(
                () => throw new Xunit.Sdk.XunitException("must-not-retry-after-timeout"),
                () => throw new Xunit.Sdk.XunitException("must-not-recature-after-timeout")
            ).Kind
        );
    }

    [Fact]
    public void Second_same_owner_epoch_keeps_the_first_epoch_when_fault_or_stale_refresh_occurs()
    {
        var owner = new NetherCodeReloadEpochOwner(NetherActionKind.SelectFloor, 3, 7);
        var coordinator = new NetherCodeReloadEpochCoordinator();
        Assert.True(coordinator.Begin(owner, reloadCount: 3, Candidates(100)));
        coordinator.Pump(
            () => NetherNativeActionResult.Completed("first-reroll-terminal"),
            () => Refresh(owner, 2, Candidates(200))
        );
        Assert.Equal(
            NetherNativeActionResultKind.Completed,
            coordinator.Pump(
                () => throw new Xunit.Sdk.XunitException("must-not-repoll-first-reroll"),
                () => Refresh(owner, 2, Candidates(200))
            ).Kind
        );
        Assert.Equal(1, coordinator.DecisionEpoch);

        Assert.True(coordinator.Begin(owner, reloadCount: 2, Candidates(200)));
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            coordinator.Pump(
                () => NetherNativeActionResult.UnknownOutcome("second-reroll-fault"),
                () => Refresh(owner, 1, Candidates(300))
            ).Kind
        );
        Assert.Equal(NetherCodeReloadEpochStage.Faulted, coordinator.Stage);
        Assert.Equal(1, coordinator.DecisionEpoch);

        coordinator.Reset();
        Assert.True(coordinator.Begin(owner, reloadCount: 3, Candidates(100)));
        coordinator.Pump(
            () => NetherNativeActionResult.Completed("first-reroll-terminal"),
            () => Refresh(owner, 2, Candidates(200))
        );
        coordinator.Pump(
            () => throw new Xunit.Sdk.XunitException("must-not-repoll-first-reroll"),
            () => Refresh(owner, 2, Candidates(200))
        );
        Assert.True(coordinator.Begin(owner, reloadCount: 2, Candidates(200)));
        coordinator.Pump(
            () => NetherNativeActionResult.Completed("second-reroll-terminal"),
            () => Refresh(owner, 1, Candidates(300))
        );
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            coordinator.Pump(
                () => throw new Xunit.Sdk.XunitException("must-not-repoll-second-reroll"),
                () => Refresh(owner with { Sequence = 8 }, 1, Candidates(300))
            ).Kind
        );
        Assert.Equal(NetherCodeReloadEpochStage.Faulted, coordinator.Stage);
        Assert.Equal(1, coordinator.DecisionEpoch);
    }

    private static NetherRuntimeCodeCandidatesResult Candidates(params long[] ids) => new(
        ids.Select(id => new NetherCodeCandidate(id, NetherCodeEffectKind.Safe, 1)).ToArray(),
        IsMasterComplete: true,
        Detail: string.Empty
    );

    private static NetherCodeReloadEpochRefresh Refresh(
        NetherCodeReloadEpochOwner owner,
        int reloadCount,
        NetherRuntimeCodeCandidatesResult candidates
    ) => new(owner, reloadCount, candidates);
}
