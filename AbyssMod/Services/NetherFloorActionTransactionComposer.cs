#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// Folds owned modal actions into their original SelectFloor parent.  Native Nether flows can
/// have multiple modal children under one parent (notably Event -> Code Offer), therefore a
/// composition is append-only and the one eventual GET validates every child together.
/// </summary>
internal static class NetherFloorActionTransactionComposer
{
    private enum CodeChangeExpectation
    {
        None,
        Valid,
        Invalid,
    }

    /// <summary>
    /// Compatibility overload for isolated policy tests and single-stage callers.  Runtime
    /// code must pass both the immutable native owner and current settlement copy below.
    /// </summary>
    public static bool TryCompose(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup,
        NetherPlannedAction child,
        out NetherPlannedAction composed
    ) => TryCompose(parent, parent, popup, child, out composed);

    /// <summary>
    /// Appends exactly one owned popup stage.  <paramref name="ownerParent"/> remains the
    /// native task owner; <paramref name="settlement"/> is the current immutable evidence
    /// copy.  The two must identify the same floor and pre-status, but only settlement can
    /// carry prior stages.
    /// </summary>
    public static bool TryCompose(
        NetherPlannedAction ownerParent,
        NetherPlannedAction settlement,
        NetherRuntimePopupContext popup,
        NetherPlannedAction child,
        out NetherPlannedAction composed
    )
    {
        composed = default;
        if (!IsValidParent(ownerParent)
            || !HasSameParentIdentity(ownerParent, settlement)
            || popup == null)
        {
            return false;
        }

        NetherSessionStatus terminal = GetTerminal(popup.Kind, child);
        if (terminal == NetherSessionStatus.Unknown
            || !CanUseTerminal(settlement.ExpectedAfterStatus, terminal))
        {
            return false;
        }

        if (!TryGetStages(settlement, out List<NetherFloorPopupStage> stages)
            || !TryCreateStage(popup, child, terminal, out NetherFloorPopupStage? stage)
            || stage == null
            || !CanAppend(ownerParent, stages, stage))
        {
            return false;
        }

        stages.Add(stage);
        IReadOnlyList<NetherEffect> retainedEffects = stages
            .Where(value => value.ActionKind == NetherActionKind.SelectEventOption)
            .SelectMany(value => value.ExpectedEffects)
            .ToArray();

        composed = settlement with
        {
            ExpectedAfterStatus = terminal,
            OwnedPopupKind = stage.PopupKind,
            OwnedPopupActionKind = stage.ActionKind,
            OwnedPopupStages = stages.ToArray(),
            // These are retained for existing telemetry and legacy tests.  The ordered stage
            // sequence above is authoritative for reconciliation.
            OptionNumber = stage.OptionNumber,
            ExpectedEffects = retainedEffects,
            ContentId = stage.ContentId,
            ContentAmount = stage.ContentAmount,
            GoldCost = stage.GoldCost,
            CodeId = stage.CodeId,
            ReplaceCodeId = stage.ReplaceCodeId,
        };
        return true;
    }

    /// <summary>
    /// A native SelectFloor parent is not allowed to reach reconciliation while a stage it
    /// itself made necessary is absent.  In particular the Event result's code-change effect
    /// is only settled after a matching CodeOffer selection; issuing GET earlier would turn a
    /// visually closed but unfinished chain into a misleading server result.
    /// </summary>
    public static bool IsCompleteForParentTerminal(NetherPlannedAction settlement)
    {
        if (!IsValidParent(settlement)
            || settlement.ExpectedAfterStatus == NetherSessionStatus.Unknown)
        {
            return false;
        }

        IReadOnlyList<NetherFloorPopupStage> stages = settlement.OwnedPopupStages
            ?? Array.Empty<NetherFloorPopupStage>();
        if (stages.Count == 0)
            return settlement.OwnedPopupKind == NetherRuntimePopupKind.None;

        NetherFloorPopupStage? previous = null;
        foreach (NetherFloorPopupStage stage in stages)
        {
            if (!IsWellFormedStage(stage)
                || stage.ExpectedAfterStatus != settlement.ExpectedAfterStatus)
            {
                return false;
            }
            if (previous != null
                && (previous.OwnerGeneration <= 0
                    || previous.Sequence <= 0
                    || stage.OwnerGeneration != previous.OwnerGeneration
                    || !HasMonotonicStageIdentity(previous, stage)))
            {
                return false;
            }
            previous = stage;
        }

        // RerollAsync is an intermediate mutation of the same live CodeOffer, never a parent
        // settlement.  The owner may reach its terminal only after the bridge has advanced the
        // popup epoch and the controller has appended one exact terminal code selection.  Keep
        // and close have no independently verified native terminal binding in this flow, so
        // they deliberately remain incomplete/fail-closed instead of being guessed as success.
        if (!HasTerminalSelectionAfterEveryReload(stages))
            return false;

        CodeChangeExpectation codeChange = GetExpectedCodeChange(stages, out long expectedCodeId);
        if (codeChange == CodeChangeExpectation.Invalid)
            return false;
        if (codeChange == CodeChangeExpectation.Valid)
        {
            NetherFloorPopupStage[] matchingSelections = stages
                .Where(stage => stage.PopupKind == NetherRuntimePopupKind.CodeOffer
                    && stage.ActionKind == NetherActionKind.SelectCode
                    && stage.CodeId == expectedCodeId)
                .ToArray();
            return matchingSelections.Length == 1
                && ReferenceEquals(stages[^1], matchingSelections[0]);
        }
        return true;
    }

    private static bool HasTerminalSelectionAfterEveryReload(
        IReadOnlyList<NetherFloorPopupStage> stages
    )
    {
        for (int index = 0; index < stages.Count; index++)
        {
            NetherFloorPopupStage reload = stages[index];
            if (reload.PopupKind != NetherRuntimePopupKind.CodeOffer
                || reload.ActionKind != NetherActionKind.ReloadCode)
            {
                continue;
            }

            if (index + 1 >= stages.Count)
                return false;

            NetherFloorPopupStage terminal = stages[index + 1];
            if (terminal.PopupKind != NetherRuntimePopupKind.CodeOffer
                || terminal.ActionKind != NetherActionKind.SelectCode
                || terminal.OwnerGeneration != reload.OwnerGeneration
                || terminal.Sequence != reload.Sequence
                || terminal.DecisionEpoch <= reload.DecisionEpoch)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsValidParent(NetherPlannedAction parent) =>
        parent.Kind == NetherActionKind.SelectFloor
        && parent.FloorId > 0
        && parent.FloorLevel >= 1
        && parent.FloorIndex >= 0
        && parent.ExpectedBeforeStatus == NetherSessionStatus.Play;

    private static bool HasSameParentIdentity(NetherPlannedAction owner, NetherPlannedAction settlement) =>
        settlement.Kind == NetherActionKind.SelectFloor
        && settlement.FloorId == owner.FloorId
        && settlement.FloorLevel == owner.FloorLevel
        && settlement.FloorIndex == owner.FloorIndex
        && settlement.ExpectedBeforeStatus == owner.ExpectedBeforeStatus;

    private static NetherSessionStatus GetTerminal(NetherRuntimePopupKind popupKind, NetherPlannedAction child) =>
        popupKind switch
        {
            NetherRuntimePopupKind.Event or NetherRuntimePopupKind.Recovery or NetherRuntimePopupKind.Treasure
                when child.Kind == NetherActionKind.SelectEventOption && IsExactEventAction(child) =>
                    child.ExpectedEffects.Any(effect => effect.Kind == NetherEffectKind.Battle)
                        ? NetherSessionStatus.Battle
                        : NetherSessionStatus.Play,
            NetherRuntimePopupKind.Shop when child.Kind == NetherActionKind.LeaveShop => NetherSessionStatus.Play,
            NetherRuntimePopupKind.Shop when child.Kind == NetherActionKind.BuyShopItem && IsExactShopBuy(child) =>
                NetherSessionStatus.Play,
            NetherRuntimePopupKind.CodeOffer when child.Kind == NetherActionKind.SelectCode && child.CodeId > 0 =>
                NetherSessionStatus.Play,
            NetherRuntimePopupKind.CodeOffer when child.Kind == NetherActionKind.ReloadCode =>
                NetherSessionStatus.Play,
            _ => NetherSessionStatus.Unknown,
        };

    private static bool CanUseTerminal(NetherSessionStatus existing, NetherSessionStatus next) =>
        existing is NetherSessionStatus.Wait or NetherSessionStatus.Unknown
        || existing == next;

    private static bool TryGetStages(NetherPlannedAction settlement, out List<NetherFloorPopupStage> stages)
    {
        stages = settlement.OwnedPopupStages?.ToList() ?? new List<NetherFloorPopupStage>();
        if (stages.Count != 0)
            return stages.All(IsWellFormedStage);

        // Older state-machine characterization fixtures may supply only scalar legacy fields.
        // Convert those to one stage before appending rather than silently dropping evidence.
        if (settlement.OwnedPopupKind == NetherRuntimePopupKind.None
            && settlement.OwnedPopupActionKind == NetherActionKind.None)
        {
            return true;
        }
        if (settlement.OwnedPopupKind == NetherRuntimePopupKind.None
            || settlement.OwnedPopupActionKind == NetherActionKind.None)
        {
            return false;
        }

        stages.Add(new NetherFloorPopupStage(
            settlement.OwnedPopupKind,
            settlement.OwnedPopupActionKind,
            OwnerGeneration: 0,
            Sequence: 0,
            settlement.ExpectedAfterStatus,
            settlement.OptionNumber,
            settlement.ExpectedEffects?.ToArray() ?? Array.Empty<NetherEffect>(),
            settlement.ContentId,
            settlement.ContentAmount,
            settlement.GoldCost,
            settlement.CodeId,
            settlement.ReplaceCodeId
        ));
        return IsWellFormedStage(stages[0]);
    }

    private static bool TryCreateStage(
        NetherRuntimePopupContext popup,
        NetherPlannedAction child,
        NetherSessionStatus terminal,
        out NetherFloorPopupStage? stage
    )
    {
        stage = null;
        if (popup.OwnerAction != NetherActionKind.None
            && popup.OwnerAction != NetherActionKind.SelectFloor)
        {
            return false;
        }
        if (popup.OwnerGeneration < 0 || popup.Sequence < 0)
            return false;

        stage = new NetherFloorPopupStage(
            popup.Kind,
            child.Kind,
            popup.OwnerGeneration,
            popup.Sequence,
            terminal,
            child.OptionNumber,
            child.ExpectedEffects?.ToArray() ?? Array.Empty<NetherEffect>(),
            child.ContentId,
            child.ContentAmount,
            child.GoldCost,
            child.CodeId,
            child.ReplaceCodeId,
            popup.DecisionEpoch
        );
        return IsWellFormedStage(stage);
    }

    private static bool IsWellFormedStage(NetherFloorPopupStage? stage)
    {
        if (stage == null || stage.PopupKind == NetherRuntimePopupKind.None
            || stage.ActionKind == NetherActionKind.None
            || stage.ExpectedAfterStatus == NetherSessionStatus.Unknown)
        {
            return false;
        }

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
        return GetTerminal(stage.PopupKind, child) == stage.ExpectedAfterStatus;
    }

    private static bool CanAppend(
        NetherPlannedAction ownerParent,
        IReadOnlyList<NetherFloorPopupStage> stages,
        NetherFloorPopupStage next
    )
    {
        if (stages.Count == 0)
            return true;

        NetherFloorPopupStage previous = stages[^1];
        // A multi-stage production flow has a stamped owner tuple.  Zero is tolerated only
        // for one isolated legacy-stage fixture; it cannot safely grow into a live sequence.
        if (previous.OwnerGeneration <= 0 || previous.Sequence <= 0
            || next.OwnerGeneration <= 0 || next.Sequence <= 0
            || next.OwnerGeneration != previous.OwnerGeneration
            || !HasMonotonicStageIdentity(previous, next)
            || next.PopupKind == previous.PopupKind && next.ActionKind == previous.ActionKind)
        {
            return false;
        }

        if (next.ExpectedAfterStatus != previous.ExpectedAfterStatus)
            return false;

        CodeChangeExpectation codeChange = GetExpectedCodeChange(stages, out long expectedCodeId);
        if (codeChange == CodeChangeExpectation.Invalid)
            return false;
        // The only proven multi-popup floor chain is Event's code-change handoff.  Do not
        // generalize it to arbitrary modal sequences: a code effect requires a CodeOffer,
        // and that offer may only select the exact replacement (or first reroll, whose later
        // same-popup epoch is completed by the code-flow seam).  This makes duplicate or
        // conflicting effect keys fail closed instead of being silently summed.
        if (codeChange == CodeChangeExpectation.Valid)
        {
            if (next.PopupKind != NetherRuntimePopupKind.CodeOffer
                || next.ActionKind is not (NetherActionKind.SelectCode or NetherActionKind.ReloadCode))
            {
                return false;
            }
            return next.ActionKind != NetherActionKind.SelectCode || next.CodeId == expectedCodeId;
        }

        // A Reroll may later be followed by the terminal choice on the same live CodeOffer.
        // It is the only other stage edge with a verified owner; all unrelated second popups
        // are rejected until they have their own exact native parent contract.
        return previous.PopupKind == NetherRuntimePopupKind.CodeOffer
            && previous.ActionKind == NetherActionKind.ReloadCode
            && next.PopupKind == NetherRuntimePopupKind.CodeOffer
            && next.ActionKind == NetherActionKind.SelectCode
            && ownerParent.Kind == NetherActionKind.SelectFloor;
    }

    private static bool HasMonotonicStageIdentity(
        NetherFloorPopupStage previous,
        NetherFloorPopupStage next
    )
    {
        if (next.Sequence > previous.Sequence)
            return next.DecisionEpoch == 0;

        // RerollAsync preserves the same CodeOffer instance/registration.  Only the bridge's
        // post-task authoritative refresh can advance an epoch on that exact owner tuple.
        return next.Sequence == previous.Sequence
            && previous.PopupKind == NetherRuntimePopupKind.CodeOffer
            && next.PopupKind == NetherRuntimePopupKind.CodeOffer
            && next.DecisionEpoch > previous.DecisionEpoch;
    }

    private static CodeChangeExpectation GetExpectedCodeChange(
        IEnumerable<NetherFloorPopupStage> stages,
        out long codeId
    )
    {
        codeId = 0;
        foreach (NetherEffect effect in stages
                     .Where(stage => stage.ActionKind == NetherActionKind.SelectEventOption)
                     .SelectMany(stage => stage.ExpectedEffects))
        {
            if (effect.Kind == NetherEffectKind.AbyssCodeChanged)
            {
                if (effect.ReplacementCodeId <= 0 || codeId != 0)
                    return CodeChangeExpectation.Invalid;
                codeId = effect.ReplacementCodeId;
            }
        }
        return codeId > 0 ? CodeChangeExpectation.Valid : CodeChangeExpectation.None;
    }

    private static bool IsExactEventAction(NetherPlannedAction action) =>
        action.OptionNumber > 0
        && action.ExpectedEffects != null
        && action.ExpectedEffects.Count > 0
        && !HasDuplicateEffectKeys(action.ExpectedEffects)
        && action.ExpectedEffects.All(effect => effect != null
            && effect.Known
            && effect.ContentKnown
            && effect.Kind != NetherEffectKind.Unknown
            && effect.Amount >= 0
            && (effect.Kind != NetherEffectKind.AbyssCodeChanged || effect.ReplacementCodeId > 0)
            && (effect.Kind != NetherEffectKind.Item || effect.ContentId > 0));

    private static bool HasDuplicateEffectKeys(IReadOnlyList<NetherEffect> effects)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (NetherEffect effect in effects)
        {
            if (effect == null)
                return true;
            string key = effect.Kind switch
            {
                NetherEffectKind.Item => "item:" + effect.ContentId,
                NetherEffectKind.AbyssCodeChanged => "code-change",
                _ => "effect:" + (int)effect.Kind,
            };
            if (!keys.Add(key))
                return true;
        }
        return false;
    }

    private static bool IsExactShopBuy(NetherPlannedAction action) =>
        action.ContentId > 0 && action.ContentAmount > 0 && action.GoldCost >= 0;
}
