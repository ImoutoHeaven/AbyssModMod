#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// Interprets a completed read-only Nether refresh after a native controller invocation.
/// It deliberately recognizes only action-specific server-owned postconditions.  Anything
/// else is ambiguous, so the controller pauses instead of replaying a non-idempotent action.
/// </summary>
internal static class NetherActionReconcilePolicy
{
    public static NetherActionOutcome Evaluate(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (before == null)
            throw new ArgumentNullException(nameof(before));
        if (after == null)
            throw new ArgumentNullException(nameof(after));

        return action.Kind switch
        {
            NetherActionKind.SelectFloor => EvaluateFloor(action, before, after),
            NetherActionKind.SelectEventOption => EvaluateEvent(action, before, after),
            NetherActionKind.BuyShopItem => EvaluateShopBuy(action, before, after),
            NetherActionKind.LeaveShop => UnchangedOrAmbiguous(before, after),
            NetherActionKind.SelectCode => EvaluateCodeSelect(action, before, after),
            NetherActionKind.ReloadCode => EvaluateCodeReload(before, after),
            NetherActionKind.KeepCode => EvaluateCodeKeep(before, after),
            NetherActionKind.TransformCode => EvaluateCodeTransform(action, before, after),
            NetherActionKind.Continue => EvaluateContinue(action, before, after),
            NetherActionKind.BattleSettlement => EvaluateBattleSettlement(action, before, after),
            NetherActionKind.FinishAtCheckpoint => after.Status == NetherSessionStatus.Clear
                || after.Status == NetherSessionStatus.Lose
                    ? NetherActionOutcome.Applied
                    : UnchangedOrAmbiguous(before, after),
            NetherActionKind.SelectReturnItems => after.LockReward < before.LockReward
                || AcquiredItemsChanged(before, after)
                    ? NetherActionOutcome.Applied
                    : UnchangedOrAmbiguous(before, after),
            _ => NetherActionOutcome.Ambiguous,
        };
    }

    private static NetherActionOutcome EvaluateFloor(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (action.FloorId <= 0
            || action.ExpectedBeforeStatus == NetherSessionStatus.Unknown
            || action.ExpectedAfterStatus == NetherSessionStatus.Unknown
            || before.Status != action.ExpectedBeforeStatus)
        {
            return NetherActionOutcome.Ambiguous;
        }

        if (after.CurrentFloorId != action.FloorId || after.Status != action.ExpectedAfterStatus)
            return UnchangedOrAmbiguous(before, after);

        // A direct combat parent has no owned modal.  Once a popup was observed, the one
        // SelectFloor parent is instead a composed transaction: accepting the floor/status
        // alone would turn a wrong option, cost, or resource result into a false Applied.
        // Multiple popup stages belong to the same parent (Event -> Code Offer), so every
        // stage must prove its exact effect from the one final GET snapshot.
        IReadOnlyList<NetherFloorPopupStage> stages = action.OwnedPopupStages
            ?? Array.Empty<NetherFloorPopupStage>();
        if (stages.Count != 0)
        {
            int reloadStageCount = 0;
            bool hasCodeTerminal = false;
            foreach (NetherFloorPopupStage stage in stages)
            {
                if (stage == null || stage.ExpectedAfterStatus != action.ExpectedAfterStatus)
                    return NetherActionOutcome.Ambiguous;

                if (stage.PopupKind == NetherRuntimePopupKind.CodeOffer
                    && stage.ActionKind == NetherActionKind.ReloadCode)
                {
                    try
                    {
                        reloadStageCount = checked(reloadStageCount + 1);
                    }
                    catch (OverflowException)
                    {
                        return NetherActionOutcome.Ambiguous;
                    }
                }

                if (stage.PopupKind == NetherRuntimePopupKind.CodeOffer
                    && (stage.ActionKind == NetherActionKind.SelectCode
                        || stage.ActionKind == NetherActionKind.KeepCode))
                {
                    hasCodeTerminal = true;
                }
            }

            // Every retained Reload was individually proven by the live epoch coordinator,
            // but the only authority snapshot is the final GET.  Verify their aggregate
            // resource delta once; applying before-1 to each stage would reject a valid
            // [Reload, Reload, Select] chain against its one final snapshot.  A terminal
            // Select/Keep must prove the same arithmetic even when it retained zero Reload
            // stages: otherwise a direct terminal could accept an unrelated ticket decrement.
            if (hasCodeTerminal)
            {
                try
                {
                    if (before.CodeReloadCount < reloadStageCount
                        || after.CodeReloadCount != checked(before.CodeReloadCount - reloadStageCount))
                    {
                        return UnchangedOrAmbiguous(before, after);
                    }
                }
                catch (OverflowException)
                {
                    return NetherActionOutcome.Ambiguous;
                }
            }

            NetherActionOutcome codeStages = EvaluateOwnedCodeStages(stages, before, after);
            if (codeStages != NetherActionOutcome.Applied)
                return codeStages;

            foreach (NetherFloorPopupStage stage in stages)
            {
                if (stage.PopupKind is NetherRuntimePopupKind.CodeOffer or NetherRuntimePopupKind.CodeTransform)
                {
                    continue;
                }
                NetherActionOutcome outcome = EvaluateOwnedFloorStage(stage, before, after);
                if (outcome != NetherActionOutcome.Applied)
                    return outcome;
            }
            return NetherActionOutcome.Applied;
        }

        if (action.OwnedPopupKind == NetherRuntimePopupKind.None)
            return NetherActionOutcome.Applied;

        return action.OwnedPopupKind switch
        {
            NetherRuntimePopupKind.Event or NetherRuntimePopupKind.Recovery or NetherRuntimePopupKind.Treasure
                when action.OwnedPopupActionKind == NetherActionKind.SelectEventOption =>
                    EvaluateEventEffects(action, before, after),
            NetherRuntimePopupKind.Shop when action.OwnedPopupActionKind == NetherActionKind.LeaveShop =>
                NetherActionOutcome.Applied,
            NetherRuntimePopupKind.Shop when action.OwnedPopupActionKind == NetherActionKind.BuyShopItem =>
                EvaluateShopBuy(action, before, after),
            NetherRuntimePopupKind.CodeOffer when action.OwnedPopupActionKind == NetherActionKind.SelectCode =>
                EvaluateCodeSelect(action, before, after),
            NetherRuntimePopupKind.CodeOffer when action.OwnedPopupActionKind == NetherActionKind.ReloadCode =>
                EvaluateCodeReload(before, after),
            NetherRuntimePopupKind.CodeOffer when action.OwnedPopupActionKind == NetherActionKind.KeepCode =>
                EvaluateCodeKeep(before, after),
            _ => NetherActionOutcome.Ambiguous,
        };
    }

    private static NetherActionOutcome EvaluateOwnedFloorStage(
        NetherFloorPopupStage stage,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        NetherPlannedAction child = new(stage.ActionKind)
        {
            OptionNumber = stage.OptionNumber,
            ExpectedEffects = stage.ExpectedEffects,
            ContentId = stage.ContentId,
            ContentAmount = stage.ContentAmount,
            GoldCost = stage.GoldCost,
            CodeId = stage.CodeId,
            ReplaceCodeId = stage.ReplaceCodeId,
        };
        return stage.PopupKind switch
        {
            NetherRuntimePopupKind.Event or NetherRuntimePopupKind.Recovery or NetherRuntimePopupKind.Treasure
                when stage.ActionKind == NetherActionKind.SelectEventOption =>
                    EvaluateEventEffects(child, before, after),
            NetherRuntimePopupKind.Shop when stage.ActionKind == NetherActionKind.LeaveShop =>
                NetherActionOutcome.Applied,
            NetherRuntimePopupKind.Shop when stage.ActionKind == NetherActionKind.BuyShopItem =>
                EvaluateShopBuy(child, before, after),
            NetherRuntimePopupKind.CodeOffer when stage.ActionKind == NetherActionKind.SelectCode =>
                EvaluateCodeSelectPortfolio(child, before, after),
            NetherRuntimePopupKind.CodeOffer when stage.ActionKind == NetherActionKind.ReloadCode =>
                EvaluateCodeReload(before, after),
            // Reload consumption is aggregated by the parent transaction.  Keep itself proves
            // only that the original portfolio survived that cancel sequence unchanged.
            NetherRuntimePopupKind.CodeOffer when stage.ActionKind == NetherActionKind.KeepCode =>
                EvaluateCodeKeepPortfolio(before, after),
            NetherRuntimePopupKind.CodeTransform when stage.ActionKind == NetherActionKind.TransformCode =>
                EvaluateCodeTransform(child, before, after),
            _ => NetherActionOutcome.Ambiguous,
        };
    }

    private static NetherActionOutcome EvaluateEvent(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        return EvaluateEventEffects(action, before, after);
    }

    private static NetherActionOutcome EvaluateEventEffects(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (action.OptionNumber <= 0
            || action.ExpectedEffects == null
            || action.ExpectedEffects.Count == 0
            || action.ExpectedEffects.Any(effect => effect == null
                || !effect.Known
                || !effect.ContentKnown
                || effect.Kind == NetherEffectKind.Unknown
                || effect.Amount < 0))
        {
            return NetherActionOutcome.Ambiguous;
        }

        try
        {
            int erosionDelta = action.ExpectedEffects.Sum(effect => effect.Kind switch
            {
                NetherEffectKind.Erosion => effect.Amount,
                NetherEffectKind.ErosionHeal => -effect.Amount,
                _ => 0,
            });
            int goldDelta = action.ExpectedEffects.Sum(effect => effect.Kind switch
            {
                NetherEffectKind.NetherGoldUsed => -effect.Amount,
                NetherEffectKind.NetherGoldGain => effect.Amount,
                _ => 0,
            });
            int keyDelta = action.ExpectedEffects.Sum(effect => effect.Kind switch
            {
                NetherEffectKind.TreasureKeyUsed => -effect.Amount,
                NetherEffectKind.TreasureKeyGain => effect.Amount,
                _ => 0,
            });
            int hpDelta = action.ExpectedEffects.Sum(effect => effect.Kind switch
            {
                NetherEffectKind.Heal => effect.Amount,
                NetherEffectKind.Damage => -effect.Amount,
                _ => 0,
            });
            bool resourcesMatch = after.ErosionPoint == checked(before.ErosionPoint + erosionDelta)
                && after.NetherGold == checked(before.NetherGold + goldDelta)
                && after.TreasureKeyCount == checked(before.TreasureKeyCount + keyDelta);
            if (!resourcesMatch)
                return UnchangedOrAmbiguous(before, after);

            if (hpDelta != 0 && !HasExactHpDelta(before, after, hpDelta))
                return UnchangedOrAmbiguous(before, after);

            foreach (NetherEffect effect in action.ExpectedEffects)
            {
                switch (effect.Kind)
                {
                    case NetherEffectKind.Item:
                        if (effect.ContentId <= 0
                            || GetAcquiredItemAmount(after, effect.ContentId)
                                != checked(GetAcquiredItemAmount(before, effect.ContentId) + effect.Amount))
                        {
                            return UnchangedOrAmbiguous(before, after);
                        }
                        break;
                    // These are native flow triggers, not direct resource/code IDs.  Their
                    // separate owned CodeTransform/CodeOffer stages prove the mutation.
                    case NetherEffectKind.AbyssCodeTransform:
                    case NetherEffectKind.AbyssCodeOffer:
                        break;
                    case NetherEffectKind.Battle:
                        if (after.Status != NetherSessionStatus.Battle)
                            return UnchangedOrAmbiguous(before, after);
                        break;
                }
            }

            return NetherActionOutcome.Applied;
        }
        catch (OverflowException)
        {
            return NetherActionOutcome.Ambiguous;
        }
    }

    private static NetherActionOutcome EvaluateShopBuy(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (action.ContentId <= 0 || action.ContentAmount <= 0 || action.GoldCost < 0)
            return NetherActionOutcome.Ambiguous;

        int itemDelta = GetAcquiredItemAmount(after, action.ContentId) - GetAcquiredItemAmount(before, action.ContentId);
        return itemDelta == action.ContentAmount
            && after.NetherGold == before.NetherGold - action.GoldCost
                ? NetherActionOutcome.Applied
                : UnchangedOrAmbiguous(before, after);
    }

    private static NetherActionOutcome EvaluateCodeSelect(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (after.CodeReloadCount != before.CodeReloadCount)
            return UnchangedOrAmbiguous(before, after);

        return EvaluateCodeSelectPortfolio(action, before, after);
    }

    private static NetherActionOutcome EvaluateCodeSelectPortfolio(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (action.CodeId <= 0 || ContainsCode(before, action.CodeId))
            return NetherActionOutcome.Ambiguous;
        if (action.ReplaceCodeId > 0 && !ContainsCode(before, action.ReplaceCodeId))
            return NetherActionOutcome.Ambiguous;

        bool selected = ContainsCode(after, action.CodeId);
        bool replacementRemoved = action.ReplaceCodeId <= 0 || !ContainsCode(after, action.ReplaceCodeId);
        return selected && replacementRemoved
            ? NetherActionOutcome.Applied
            : UnchangedOrAmbiguous(before, after);
    }

    private static NetherActionOutcome EvaluateCodeReload(NetherSnapshot before, NetherSnapshot after) =>
        after.CodeReloadCount == before.CodeReloadCount - 1
            ? NetherActionOutcome.Applied
            : UnchangedOrAmbiguous(before, after);

    private static NetherActionOutcome EvaluateCodeKeep(NetherSnapshot before, NetherSnapshot after) =>
        after.CodeReloadCount == before.CodeReloadCount
            && HasUnchangedCodePortfolio(before, after)
                ? NetherActionOutcome.Applied
                : UnchangedOrAmbiguous(before, after);

    private static NetherActionOutcome EvaluateCodeKeepPortfolio(NetherSnapshot before, NetherSnapshot after) =>
        HasUnchangedCodePortfolio(before, after)
            ? NetherActionOutcome.Applied
            : UnchangedOrAmbiguous(before, after);

    private static NetherActionOutcome EvaluateContinue(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (action.TicketCost <= 0
            || action.ExpectedMapId <= 0
            || action.ExpectedFloorId <= 0
            || action.ExpectedSegmentFloorLevel <= 0
            || before.TicketCount < action.TicketCost)
        {
            return NetherActionOutcome.Ambiguous;
        }

        return after.TicketCount == before.TicketCount - action.TicketCost
            && after.MapId == action.ExpectedMapId
            && after.CurrentFloorId == action.ExpectedFloorId
            && after.FloorLevel == action.ExpectedSegmentFloorLevel
                ? NetherActionOutcome.Applied
                : UnchangedOrAmbiguous(before, after);
    }

    private static NetherActionOutcome EvaluateOwnedCodeStages(
        IReadOnlyList<NetherFloorPopupStage> stages,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        NetherFloorPopupStage[] transforms = stages
            .Where(stage => stage.PopupKind == NetherRuntimePopupKind.CodeTransform
                && stage.ActionKind == NetherActionKind.TransformCode)
            .ToArray();
        NetherFloorPopupStage[] terminals = stages
            .Where(stage => stage.PopupKind == NetherRuntimePopupKind.CodeOffer
                && stage.ActionKind is NetherActionKind.SelectCode or NetherActionKind.KeepCode)
            .ToArray();
        bool hasAnyCodeStage = stages.Any(stage => stage.PopupKind is NetherRuntimePopupKind.CodeOffer
            or NetherRuntimePopupKind.CodeTransform);
        if (!hasAnyCodeStage)
            return NetherActionOutcome.Applied;
        if (transforms.Length > 1 || terminals.Length > 1)
            return NetherActionOutcome.Ambiguous;

        if (transforms.Length == 1)
        {
            NetherPlannedAction transform = new(NetherActionKind.TransformCode)
            {
                ReplaceCodeId = transforms[0].ReplaceCodeId,
            };
            if (terminals.Length == 0 || terminals[0].ActionKind == NetherActionKind.KeepCode)
                return EvaluateCodeTransform(transform, before, after);

            NetherFloorPopupStage selected = terminals[0];
            return EvaluateCodeTransformThenSelect(
                transform.ReplaceCodeId,
                selected.CodeId,
                selected.ReplaceCodeId,
                before,
                after
            );
        }

        if (terminals.Length == 1)
        {
            NetherFloorPopupStage terminal = terminals[0];
            NetherPlannedAction action = new(terminal.ActionKind)
            {
                CodeId = terminal.CodeId,
                ReplaceCodeId = terminal.ReplaceCodeId,
            };
            return terminal.ActionKind == NetherActionKind.SelectCode
                ? EvaluateCodeSelectPortfolio(action, before, after)
                : EvaluateCodeKeepPortfolio(before, after);
        }

        // A reload-only CodeOffer is not a terminal settlement.
        return NetherActionOutcome.Ambiguous;
    }

    private static NetherActionOutcome EvaluateCodeTransform(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        if (action.ReplaceCodeId <= 0)
            return NetherActionOutcome.Ambiguous;
        HashSet<long>? beforeIds = TryGetCodeIds(before);
        HashSet<long>? afterIds = TryGetCodeIds(after);
        if (beforeIds == null || afterIds == null
            || !beforeIds.Contains(action.ReplaceCodeId)
            || afterIds.Contains(action.ReplaceCodeId)
            || beforeIds.Count != afterIds.Count
            || string.Equals(before.CodeHash, after.CodeHash, StringComparison.Ordinal))
        {
            return UnchangedOrAmbiguous(before, after);
        }

        if (beforeIds.Where(id => id != action.ReplaceCodeId).Any(id => !afterIds.Contains(id))
            || afterIds.Count(id => !beforeIds.Contains(id)) != 1)
        {
            return UnchangedOrAmbiguous(before, after);
        }
        return NetherActionOutcome.Applied;
    }

    private static NetherActionOutcome EvaluateCodeTransformThenSelect(
        long transformedCodeId,
        long selectedCodeId,
        long selectedReplacementId,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        HashSet<long>? beforeIds = TryGetCodeIds(before);
        HashSet<long>? afterIds = TryGetCodeIds(after);
        if (beforeIds == null || afterIds == null
            || transformedCodeId <= 0
            || selectedCodeId <= 0
            || !beforeIds.Contains(transformedCodeId)
            || beforeIds.Contains(selectedCodeId)
            || afterIds.Contains(transformedCodeId)
            || !afterIds.Contains(selectedCodeId)
            || selectedReplacementId == selectedCodeId
            || string.Equals(before.CodeHash, after.CodeHash, StringComparison.Ordinal))
        {
            return UnchangedOrAmbiguous(before, after);
        }

        var requiredSurvivors = new HashSet<long>(beforeIds);
        requiredSurvivors.Remove(transformedCodeId);
        if (selectedReplacementId > 0 && beforeIds.Contains(selectedReplacementId))
            requiredSurvivors.Remove(selectedReplacementId);
        if (requiredSurvivors.Any(id => !afterIds.Contains(id))
            || selectedReplacementId > 0 && afterIds.Contains(selectedReplacementId))
        {
            return UnchangedOrAmbiguous(before, after);
        }

        int expectedCount = selectedReplacementId > 0 ? beforeIds.Count : checked(beforeIds.Count + 1);
        int expectedNewIds = selectedReplacementId > 0 && !beforeIds.Contains(selectedReplacementId) ? 1 : 2;
        return afterIds.Count == expectedCount
            && afterIds.Count(id => !beforeIds.Contains(id)) == expectedNewIds
                ? NetherActionOutcome.Applied
                : UnchangedOrAmbiguous(before, after);
    }

    private static HashSet<long>? TryGetCodeIds(NetherSnapshot snapshot)
    {
        if (snapshot.Codes == null
            || snapshot.Codes.Any(code => code == null || code.CodeId <= 0))
        {
            return null;
        }
        var ids = new HashSet<long>(snapshot.Codes.Select(code => code.CodeId));
        return ids.Count == snapshot.Codes.Count ? ids : null;
    }

    private static NetherActionOutcome EvaluateBattleSettlement(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after
    )
    {
        NetherBattleSettlementContract? contract = action.BattleSettlement;
        if (contract == null
            || contract.EntryStatus != NetherSessionStatus.Battle
            || contract.ExpectedStatus == NetherSessionStatus.Unknown
            || contract.EntryMapId <= 0
            || contract.EntryFloorId <= 0
            || contract.ExpectedMapId <= 0
            || contract.ExpectedFloorId <= 0
            || before.Status != contract.EntryStatus
            || before.MapId != contract.EntryMapId
            || before.CurrentFloorId != contract.EntryFloorId)
        {
            return NetherActionOutcome.Ambiguous;
        }

        return after.Status == contract.ExpectedStatus
            && after.MapId == contract.ExpectedMapId
            && after.CurrentFloorId == contract.ExpectedFloorId
                ? NetherActionOutcome.Applied
                : UnchangedOrAmbiguous(before, after);
    }

    private static NetherActionOutcome UnchangedOrAmbiguous(NetherSnapshot before, NetherSnapshot after) =>
        IsAuthoritativelyUnchanged(before, after)
            ? NetherActionOutcome.NotApplied
            : NetherActionOutcome.Ambiguous;

    private static bool IsAuthoritativelyUnchanged(NetherSnapshot before, NetherSnapshot after) =>
        before.Fingerprint == after.Fingerprint
        && string.Equals(CreateItemIdentity(before), CreateItemIdentity(after), StringComparison.Ordinal)
        && string.Equals(CreateCodeIdentity(before), CreateCodeIdentity(after), StringComparison.Ordinal);

    private static bool ContainsCode(NetherSnapshot snapshot, long codeId) =>
        snapshot.Codes.Any(code => code.CodeId == codeId);

    private static int GetAcquiredItemAmount(NetherSnapshot snapshot, long contentId) =>
        snapshot.AcquiredItems.Where(item => item.ItemId == contentId).Sum(item => item.Amount);

    private static bool AcquiredItemsChanged(NetherSnapshot before, NetherSnapshot after) =>
        !string.Equals(CreateItemIdentity(before), CreateItemIdentity(after), StringComparison.Ordinal);

    private static bool HasExactHpDelta(NetherSnapshot before, NetherSnapshot after, int expectedDelta)
    {
        if (before.Characters == null || after.Characters == null
            || before.Characters.Count == 0
            || before.Characters.Count != after.Characters.Count)
        {
            return false;
        }
        try
        {
            var afterByCharacterId = new Dictionary<long, NetherCharacterState>();
            foreach (NetherCharacterState character in after.Characters)
            {
                if (!afterByCharacterId.TryAdd(character.CharacterId, character))
                    return false;
            }

            foreach (NetherCharacterState character in before.Characters)
            {
                if (!afterByCharacterId.TryGetValue(character.CharacterId, out NetherCharacterState observed)
                    || observed.IsActive != character.IsActive)
                {
                    return false;
                }

                int expectedHp = character.IsActive
                    ? checked(character.HpPermille + expectedDelta)
                    : character.HpPermille;
                if (observed.HpPermille != expectedHp)
                    return false;
            }

            return !string.Equals(before.CharacterHpHash, after.CharacterHpHash, StringComparison.Ordinal);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string CreateItemIdentity(NetherSnapshot snapshot) => string.Join(
        ";",
        snapshot.AcquiredItems
    );

    private static string CreateCodeIdentity(NetherSnapshot snapshot) => string.Join(
        ";",
        snapshot.Codes
            .Select(code => code.CodeId
                + ":" + code.Level
                + ":" + (int)code.EffectKind
                + ":" + code.IsKnown
                + ":" + (int)code.Category
                + ":" + code.Rarity
                + ":" + code.PartyCoverageKnown
                + ":" + code.PartyCoverage
                + ":" + code.IsResearchOnlyKnown
                + ":" + code.IsResearchOnly)
            .OrderBy(identity => identity, StringComparer.Ordinal)
    );

    private static bool HasUnchangedCodePortfolio(NetherSnapshot before, NetherSnapshot after) =>
        !string.IsNullOrWhiteSpace(before.CodeHash)
        && string.Equals(before.CodeHash, after.CodeHash, StringComparison.Ordinal)
        && string.Equals(CreateCodeIdentity(before), CreateCodeIdentity(after), StringComparison.Ordinal);
}
