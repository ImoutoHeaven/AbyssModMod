#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherBattleResultCodeCoordinatorTests
{
    [Fact]
    public void Live_result_code_offer_is_selected_before_next_can_continue()
    {
        var driver = new Driver
        {
            Snapshot = Snapshot(),
            Candidates = Candidates(30024),
            Popup = ResultPopup(),
        };
        var flow = new NetherBattleResultCodeCoordinator(maximumPopupPolls: 2);

        NetherBattleResultCodeStep invoked = flow.Pump(driver, Settings(), null, allowInvoke: true);

        Assert.Equal(NetherBattleResultCodeStepKind.AwaitingNative, invoked.Kind);
        Assert.Equal(NetherActionKind.SelectCode, driver.InvokedActions.Single().Kind);
        Assert.Equal(30024, driver.InvokedActions.Single().CodeId);

        driver.NativeSteps.Enqueue(NetherBattleResultCodeNativeStep.Pending("code-confirm-pending"));
        driver.NativeSteps.Enqueue(NetherBattleResultCodeNativeStep.Completed("code-confirm-terminal"));
        Assert.Equal(
            NetherBattleResultCodeStepKind.AwaitingNative,
            flow.Pump(driver, Settings(), null, allowInvoke: true).Kind
        );
        Assert.Equal(
            NetherBattleResultCodeStepKind.Completed,
            flow.Pump(driver, Settings(), null, allowInvoke: true).Kind
        );
        Assert.Single(driver.InvokedActions);
    }

    [Fact]
    public void Authoritative_candidates_block_next_until_result_popup_registers()
    {
        var driver = new Driver
        {
            Snapshot = Snapshot(),
            Candidates = Candidates(30024),
            Popup = null,
        };
        var flow = new NetherBattleResultCodeCoordinator(maximumPopupPolls: 2);

        Assert.Equal(
            NetherBattleResultCodeStepKind.AwaitingPopup,
            flow.Pump(driver, Settings(), null, allowInvoke: true).Kind
        );
        Assert.Empty(driver.InvokedActions);

        driver.Popup = ResultPopup();
        Assert.Equal(
            NetherBattleResultCodeStepKind.AwaitingNative,
            flow.Pump(driver, Settings(), null, allowInvoke: true).Kind
        );
        Assert.Single(driver.InvokedActions);
    }

    [Fact]
    public void Reload_ready_redecides_same_result_popup_then_selects_once()
    {
        var driver = new Driver
        {
            Snapshot = Snapshot() with { CodeReloadCount = 2 },
            Candidates = Candidates(40024),
            Popup = ResultPopup(),
        };
        var flow = new NetherBattleResultCodeCoordinator(maximumPopupPolls: 2);

        Assert.Equal(
            NetherBattleResultCodeStepKind.AwaitingNative,
            flow.Pump(driver, Settings(), null, allowInvoke: true).Kind
        );
        Assert.Equal(NetherActionKind.ReloadCode, driver.InvokedActions.Single().Kind);

        driver.NativeSteps.Enqueue(NetherBattleResultCodeNativeStep.ReloadReady("fresh-offer"));
        Assert.Equal(
            NetherBattleResultCodeStepKind.ReloadReady,
            flow.Pump(driver, Settings(), null, allowInvoke: true).Kind
        );

        driver.Snapshot = driver.Snapshot with { CodeReloadCount = 1 };
        driver.Candidates = Candidates(30024);
        driver.Popup = ResultPopup() with { DecisionEpoch = 1 };
        Assert.Equal(
            NetherBattleResultCodeStepKind.AwaitingNative,
            flow.Pump(driver, Settings(), null, allowInvoke: true).Kind
        );
        Assert.Equal(
            new[] { NetherActionKind.ReloadCode, NetherActionKind.SelectCode },
            driver.InvokedActions.Select(action => action.Kind)
        );
    }

    [Fact]
    public void F12_off_before_result_code_decision_performs_no_mutation()
    {
        var driver = new Driver
        {
            Snapshot = Snapshot(),
            Candidates = Candidates(30024),
            Popup = ResultPopup(),
        };
        var flow = new NetherBattleResultCodeCoordinator(maximumPopupPolls: 2);

        NetherBattleResultCodeStep step = flow.Pump(driver, Settings(), null, allowInvoke: false);

        Assert.Equal(NetherBattleResultCodeStepKind.CanceledBeforeInvoke, step.Kind);
        Assert.Empty(driver.InvokedActions);
    }

    [Fact]
    public void No_authoritative_offer_allows_result_next_without_popup_guessing()
    {
        var driver = new Driver
        {
            Snapshot = Snapshot(),
            Candidates = new NetherRuntimeCodeCandidatesResult(
                Array.Empty<NetherCodeCandidate>(),
                IsMasterComplete: true,
                Detail: string.Empty
            ),
        };
        var flow = new NetherBattleResultCodeCoordinator(maximumPopupPolls: 2);

        NetherBattleResultCodeStep step = flow.Pump(driver, Settings(), null, allowInvoke: true);

        Assert.Equal(NetherBattleResultCodeStepKind.Completed, step.Kind);
        Assert.Empty(driver.InvokedActions);
    }

    private static NetherRuntimePopupContext ResultPopup() => new()
    {
        Kind = NetherRuntimePopupKind.CodeOffer,
        OwnerAction = NetherActionKind.BattleSettlement,
        OwnerGeneration = 9,
        Sequence = 12,
        DecisionEpoch = 0,
    };

    private static NetherSnapshot Snapshot() => new()
    {
        Status = NetherSessionStatus.Play,
        NetherId = 1,
        MapId = 1,
        CurrentFloorId = 27,
        CurrentNodeId = 38654705666,
        FloorLevel = 8,
        FloorIndex = 1,
        CodeReloadCount = 1,
        CodeCapacity = 28,
        Characters = new[] { new NetherCharacterState(1001, 900) },
        Codes = Array.Empty<NetherCodeState>(),
        Floors = Array.Empty<NetherFloorNode>(),
        CharacterHpHash = "1001:900:1",
        CodeHash = string.Empty,
        MapHash = "map",
    };

    private static NetherRuntimeCodeCandidatesResult Candidates(long codeId) => new(
        new[]
        {
            codeId == 30024
                ? NetherCodeRuntimeSemanticMapper.MapCandidate(
                    codeId,
                    (int)NetherCodeCategory.ErosionResistance,
                    effectType: 1,
                    level: 1,
                    rarity: 1
                )
                : NetherCodeRuntimeSemanticMapper.MapCandidate(
                    codeId,
                    (int)NetherCodeCategory.ErosionEnhancement,
                    effectType: 1,
                    level: 1,
                    rarity: 1
                ),
        },
        IsMasterComplete: true,
        Detail: string.Empty
    );

    private static NetherAutoClimbSettings Settings() => new()
    {
        CombatLane = NetherCombatLane.Auto,
        CodeReloadReserve = 1,
    };

    private sealed class Driver : INetherBattleResultCodeDriver
    {
        public NetherSnapshot Snapshot { get; set; } = Snapshot();
        public NetherRuntimeCodeCandidatesResult Candidates { get; set; } = Candidates(30024);
        public NetherRuntimePopupContext? Popup { get; set; }
        public List<NetherPlannedAction> InvokedActions { get; } = new();
        public Queue<NetherBattleResultCodeNativeStep> NativeSteps { get; } = new();

        public NetherRuntimeSnapshotResult TryCaptureBattleResultCodeSnapshot() =>
            NetherRuntimeSnapshotResult.Success(Snapshot);

        public NetherRuntimeCodeCandidatesResult TryGetCodeCandidates() => Candidates;

        public NetherRuntimePopupResult TryGetBattleResultCodePopup() => Popup == null
            ? NetherRuntimePopupResult.Failure("popup-not-yet-registered")
            : NetherRuntimePopupResult.Success(Popup);

        public NetherNativeActionResult InvokeBattleResultCode(
            NetherRuntimePopupContext popup,
            NetherPlannedAction action
        )
        {
            InvokedActions.Add(action);
            return NetherNativeActionResult.Started("result-code-invoked");
        }

        public NetherBattleResultCodeNativeStep PollBattleResultCodeNative() =>
            NativeSteps.Count == 0
                ? NetherBattleResultCodeNativeStep.Pending("result-code-pending")
                : NativeSteps.Dequeue();
    }
}
