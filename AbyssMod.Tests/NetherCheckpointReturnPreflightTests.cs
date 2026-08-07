#nullable enable

using System.Collections.Generic;
using System.Linq;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCheckpointReturnPreflightTests
{
    [Fact]
    public void LockRewardZero_ReturnsNoReturnWithoutInspectingCandidates()
    {
        NetherCheckpointReturnPreflightDecision decision = Decide(
            lockReward: 0,
            Item(100) with { HasMasterData = false, HasContentData = false, HasRarityData = false }
        );

        Assert.Equal(NetherCheckpointReturnPreflightKind.NoReturn, decision.Kind);
        Assert.Empty(decision.WholeEntrySelection);
        Assert.Equal(string.Empty, decision.ExpectedPristineHash);
    }

    [Fact]
    public void PositiveLockReward_ProducesWholeEntrySelectionAndPristineHash()
    {
        NetherCheckpointReturnPreflightDecision decision = Decide(
            lockReward: 2,
            Item(11, amount: 7, contentType: 1, rarity: 5),
            Item(12, amount: 3, contentType: 91, rarity: 1),
            Item(13, amount: 1, contentType: 2, rarity: 4)
        );

        Assert.Equal(NetherCheckpointReturnPreflightKind.Ready, decision.Kind);
        Assert.Equal(2, decision.SelectionLimit);
        Assert.Equal(new long[] { 12, 11 }, decision.WholeEntrySelection.Select(item => item.ItemId));
        Assert.Equal(new int[] { 3, 7 }, decision.WholeEntrySelection.Select(item => item.Amount));
        Assert.NotEmpty(decision.ExpectedPristineHash);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void PositiveLockReward_UnknownAuthoritativeDescriptorPauses(
        bool hasMaster,
        bool hasContent,
        bool hasRarity
    )
    {
        NetherCheckpointReturnPreflightDecision decision = Decide(
            lockReward: 1,
            Item(11) with
            {
                HasMasterData = hasMaster,
                HasContentData = hasContent,
                HasRarityData = hasRarity,
            }
        );

        Assert.Equal(NetherCheckpointReturnPreflightKind.Pause, decision.Kind);
        Assert.Equal(NetherPauseReason.UnknownMasterData, decision.PauseReason);
        Assert.Empty(decision.WholeEntrySelection);
    }

    [Fact]
    public void PositiveLockReward_OverEntryLimitPausesBeforeAnySelection()
    {
        NetherCheckpointReturnPreflightDecision decision = Decide(
            lockReward: 2,
            Item(11, amount: 9)
        );

        Assert.Equal(NetherCheckpointReturnPreflightKind.Pause, decision.Kind);
        Assert.Equal(NetherPauseReason.UnknownMasterData, decision.PauseReason);
        Assert.Empty(decision.WholeEntrySelection);
    }

    [Fact]
    public void Selection_AlwaysKeepsWholeEntryAmount()
    {
        NetherCheckpointReturnPreflightDecision decision = Decide(
            lockReward: 1,
            Item(11, amount: 99, contentType: 1, rarity: 2),
            Item(12, amount: 1, contentType: 1, rarity: 1)
        );

        NetherCheckpointReturnPreflightItem selected = Assert.Single(decision.WholeEntrySelection);
        Assert.Equal(11, selected.ItemId);
        Assert.Equal(99, selected.Amount);
    }

    [Fact]
    public void PreserveConfigurationWinsBeforeTypeAndRarityRanking()
    {
        NetherCheckpointReturnPreflightDecision decision = Decide(
            lockReward: 1,
            preserveIds: new HashSet<long> { 33 },
            Item(11, amount: 1, contentType: 91, rarity: 5),
            Item(33, amount: 1, contentType: 1, rarity: 1)
        );

        NetherCheckpointReturnPreflightItem selected = Assert.Single(decision.WholeEntrySelection);
        Assert.Equal(33, selected.ItemId);
    }

    [Fact]
    public void PristineHash_IsDeterministicAcrossSourceEnumerationOrder()
    {
        NetherCheckpointReturnPreflightItem[] ordered =
        [
            Item(11, amount: 2, contentType: 3, rarity: 4),
            Item(12, amount: 7, contentType: 91, rarity: 1),
        ];

        NetherCheckpointReturnPreflightDecision first = Decide(2, ordered);
        NetherCheckpointReturnPreflightDecision second = Decide(2, ordered.Reverse().ToArray());

        Assert.Equal(NetherCheckpointReturnPreflightKind.Ready, first.Kind);
        Assert.Equal(NetherCheckpointReturnPreflightKind.Ready, second.Kind);
        Assert.Equal(first.ExpectedPristineHash, second.ExpectedPristineHash);
        Assert.Equal(first.WholeEntrySelection, second.WholeEntrySelection);
    }

    [Fact]
    public void UnknownPreflight_DoesNotAuthorizeNativeContinueParent()
    {
        var preflight = new NetherCheckpointReturnPreflight();
        NetherCheckpointReturnPreflightDecision decision = preflight.Evaluate(
            lockReward: 1,
            new[] { Item(11) with { HasMasterData = false } },
            new HashSet<long>()
        );

        Assert.False(preflight.CanStartNativeContinueParent(decision));
        Assert.False(preflight.CanConfirmReturnPopup(decision));
    }

    [Fact]
    public void MatchingFreshPopup_AuthorizesConfirmOnlyAfterHashLimitAndWholeEntriesMatch()
    {
        var preflight = new NetherCheckpointReturnPreflight();
        NetherCheckpointReturnPreflightItem[] items =
        [
            Item(11, amount: 7, contentType: 91, rarity: 3),
            Item(12, amount: 1, contentType: 2, rarity: 5),
        ];
        NetherCheckpointReturnPreflightDecision planned = preflight.Evaluate(1, items, new HashSet<long>());

        NetherCheckpointReturnPreflightDecision verified = preflight.VerifyFreshPopup(
            planned,
            popupSelectionLimit: 1,
            freshItems: items.Reverse().ToArray(),
            preserveItemIds: new HashSet<long>()
        );

        Assert.True(preflight.CanStartNativeContinueParent(planned));
        Assert.True(preflight.CanConfirmReturnPopup(verified));
        Assert.Equal(planned.ExpectedPristineHash, verified.ExpectedPristineHash);
        Assert.Equal(planned.WholeEntrySelection, verified.WholeEntrySelection);
    }

    [Fact]
    public void MismatchedFreshPopup_DoesNotAuthorizeConfirm()
    {
        var preflight = new NetherCheckpointReturnPreflight();
        NetherCheckpointReturnPreflightDecision planned = preflight.Evaluate(
            1,
            new[] { Item(11, amount: 2, contentType: 1, rarity: 3) },
            new HashSet<long>()
        );

        NetherCheckpointReturnPreflightDecision wrongLimit = preflight.VerifyFreshPopup(
            planned,
            popupSelectionLimit: 2,
            freshItems: new[] { Item(11, amount: 2, contentType: 1, rarity: 3) },
            preserveItemIds: new HashSet<long>()
        );
        NetherCheckpointReturnPreflightDecision wrongEntries = preflight.VerifyFreshPopup(
            planned,
            popupSelectionLimit: 1,
            freshItems: new[] { Item(11, amount: 1, contentType: 1, rarity: 3) },
            preserveItemIds: new HashSet<long>()
        );

        Assert.Equal(NetherCheckpointReturnPreflightKind.Pause, wrongLimit.Kind);
        Assert.Equal(NetherCheckpointReturnPreflightKind.Pause, wrongEntries.Kind);
        Assert.False(preflight.CanConfirmReturnPopup(wrongLimit));
        Assert.False(preflight.CanConfirmReturnPopup(wrongEntries));
    }

    [Fact]
    public void NoReturn_AuthorizesParentButNeverReturnConfirm()
    {
        var preflight = new NetherCheckpointReturnPreflight();
        NetherCheckpointReturnPreflightDecision decision = preflight.Evaluate(
            0,
            new[] { Item(11) with { HasMasterData = false } },
            new HashSet<long>()
        );

        Assert.True(preflight.CanStartNativeContinueParent(decision));
        Assert.False(preflight.CanConfirmReturnPopup(decision));
    }

    private static NetherCheckpointReturnPreflightDecision Decide(
        int lockReward,
        params NetherCheckpointReturnPreflightItem[] items
    ) => Decide(lockReward, new HashSet<long>(), items);

    private static NetherCheckpointReturnPreflightDecision Decide(
        int lockReward,
        IReadOnlySet<long> preserveIds,
        params NetherCheckpointReturnPreflightItem[] items
    ) => new NetherCheckpointReturnPreflight().Evaluate(lockReward, items, preserveIds);

    private static NetherCheckpointReturnPreflightItem Item(
        long itemId,
        int amount = 1,
        int contentType = 1,
        int rarity = 1
    ) => new(itemId, amount)
    {
        ContentType = contentType,
        MasterRarity = rarity,
    };
}
