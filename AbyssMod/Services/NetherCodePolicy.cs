#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

internal sealed record NetherCodeCandidate(long CodeId, NetherCodeEffectKind EffectKind, int Level)
{
    public bool IsKnown { get; init; } = true;
    public NetherCodeCategory Category { get; init; }
    public int Rarity { get; init; }
    public int PartyCoverage { get; init; }
    public bool IsResearchOnly { get; init; }
}

internal sealed record NetherCodePortfolio
{
    public IReadOnlyList<NetherCodeState> CurrentCodes { get; init; } = Array.Empty<NetherCodeState>();
    public int Capacity { get; init; }
    public int ReloadCount { get; init; }
    public bool IsMasterComplete { get; init; }
    public NetherCombatLane? LockedLane { get; init; }
}

internal readonly record struct NetherCodeEffectiveLevels(int Safe, int Risk, int Rush, int Impact);

internal enum NetherCodeDecisionKind
{
    Select,
    Reload,
    Keep,
    Pause,
}

internal sealed record NetherCodeDecision
{
    public NetherCodeDecisionKind Kind { get; init; }
    public long SelectedCodeId { get; init; }
    public long RemoveCodeId { get; init; }
    public NetherCombatLane LockedLane { get; init; }
    public NetherPauseReason PauseReason { get; init; }
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<long> ProtectedCodeIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> RemovableCodeIds { get; init; } = Array.Empty<long>();
}

internal sealed class NetherCodePolicy
{
    private const long PreferredSafeCodeId = 30024;
    private const long RejectedRiskCodeId = 40024;

    public NetherCodeDecision Decide(
        NetherCodePortfolio portfolio,
        IReadOnlyList<NetherCodeCandidate> candidates,
        NetherAutoClimbSettings settings
    )
    {
        if (portfolio == null)
            throw new ArgumentNullException(nameof(portfolio));
        if (candidates == null)
            throw new ArgumentNullException(nameof(candidates));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (!portfolio.IsMasterComplete || portfolio.Capacity < 1 || portfolio.ReloadCount < 0
            || portfolio.CurrentCodes.Count > portfolio.Capacity
            || portfolio.CurrentCodes.Any(code => !code.IsKnown)
            || candidates.Any(candidate => !candidate.IsKnown))
            return Pause(NetherPauseReason.UnknownMasterData, "incomplete-code-portfolio");

        NetherCodeEffectiveLevels effective = CalculateEffectiveLevels(portfolio.CurrentCodes);
        if (effective.Safe < 0 || effective.Risk < 0 || effective.Rush < 0 || effective.Impact < 0)
            return Pause(NetherPauseReason.UnknownEffect, "code-level-overflow");
        NetherCombatLane lane = ResolveLane(portfolio, candidates, settings.CombatLane);
        IReadOnlyList<long> protectedIds = BuildProtectedIds(portfolio.CurrentCodes, effective);
        IReadOnlyList<NetherCodeState> removable = FindRemovableCodes(portfolio.CurrentCodes, protectedIds, lane);

        List<NetherCodeCandidate> eligible = candidates
            .Where(candidate => !portfolio.CurrentCodes.Any(current => current.CodeId == candidate.CodeId))
            .Where(candidate => candidate.EffectKind != NetherCodeEffectKind.Risk && candidate.CodeId != RejectedRiskCodeId)
            // The native category extension proves that paired categories are mutually
            // exclusive.  A non-full portfolio has no exact replacement parent to resolve that
            // conflict, so leave it for player control; a full portfolio can name the single
            // conflicting code as the verified replacement target below.
            .Where(candidate => portfolio.CurrentCodes.Count == portfolio.Capacity
                || !HasCategoryConflict(candidate, portfolio.CurrentCodes))
            .ToList();
        NetherCodeCandidate? selected = SelectCandidate(eligible, effective, lane);
        if (selected != null)
        {
            long removal = 0;
            if (portfolio.CurrentCodes.Count == portfolio.Capacity)
            {
                IReadOnlyList<NetherCodeState> conflicts = FindCategoryConflicts(selected, portfolio.CurrentCodes);
                if (conflicts.Count > 0)
                {
                    if (conflicts.Count != 1 || protectedIds.Contains(conflicts[0].CodeId))
                        return Keep(lane, protectedIds, "category-conflict-protected", removable);
                    removal = conflicts[0].CodeId;
                }
                else
                {
                    if (removable.Count == 0)
                        return Keep(lane, protectedIds, "all-codes-protected");
                    removal = removable[0].CodeId;
                }
            }
            return new NetherCodeDecision
            {
                Kind = NetherCodeDecisionKind.Select,
                SelectedCodeId = selected.CodeId,
                RemoveCodeId = removal,
                LockedLane = lane,
                ProtectedCodeIds = protectedIds,
                RemovableCodeIds = removable.Select(code => code.CodeId).ToArray(),
            };
        }

        if (effective.Safe < 5 && portfolio.ReloadCount > settings.CodeReloadReserve)
        {
            return new NetherCodeDecision
            {
                Kind = NetherCodeDecisionKind.Reload,
                LockedLane = lane,
                ProtectedCodeIds = protectedIds,
                RemovableCodeIds = removable.Select(code => code.CodeId).ToArray(),
            };
        }

        return Keep(lane, protectedIds, "no-safe-code-candidate", removable);
    }

    public static NetherCodeEffectiveLevels CalculateEffectiveLevels(IReadOnlyList<NetherCodeState> codes)
    {
        if (codes == null)
            throw new ArgumentNullException(nameof(codes));
        try
        {
            int safe = SumLevels(codes, NetherCodeEffectKind.Safe);
            int risk = SumLevels(codes, NetherCodeEffectKind.Risk);
            int rush = SumLevels(codes, NetherCodeEffectKind.Rush);
            int impact = SumLevels(codes, NetherCodeEffectKind.Impact);
            return new NetherCodeEffectiveLevels(
                Math.Max(0, checked(safe - risk)),
                Math.Max(0, checked(risk - safe)),
                Math.Max(0, checked(rush - impact)),
                Math.Max(0, checked(impact - rush))
            );
        }
        catch (OverflowException)
        {
            return new NetherCodeEffectiveLevels(-1, -1, -1, -1);
        }
    }

    private static int SumLevels(IEnumerable<NetherCodeState> codes, NetherCodeEffectKind kind)
    {
        int sum = 0;
        foreach (NetherCodeState code in codes.Where(code => code.EffectKind == kind))
            sum = checked(sum + code.Level);
        return sum;
    }

    private static NetherCombatLane ResolveLane(
        NetherCodePortfolio portfolio,
        IReadOnlyList<NetherCodeCandidate> candidates,
        NetherCombatLane configuredLane
    )
    {
        if (configuredLane != NetherCombatLane.Auto)
            return configuredLane;
        if (portfolio.LockedLane is NetherCombatLane locked && HasCoverage(portfolio.CurrentCodes, locked))
            return locked;

        int rushCoverage = Coverage(portfolio.CurrentCodes, candidates, NetherCombatLane.Rush);
        int impactCoverage = Coverage(portfolio.CurrentCodes, candidates, NetherCombatLane.Impact);
        return rushCoverage >= impactCoverage ? NetherCombatLane.Rush : NetherCombatLane.Impact;
    }

    private static bool HasCoverage(IEnumerable<NetherCodeState> codes, NetherCombatLane lane) =>
        codes.Any(code => ToLane(code.EffectKind) == lane && code.PartyCoverage > 0);

    private static int Coverage(
        IEnumerable<NetherCodeState> current,
        IEnumerable<NetherCodeCandidate> candidates,
        NetherCombatLane lane
    ) => current.Where(code => ToLane(code.EffectKind) == lane).Sum(code => code.PartyCoverage)
        + candidates.Where(code => ToLane(code.EffectKind) == lane).Sum(code => code.PartyCoverage);

    private static NetherCombatLane? ToLane(NetherCodeEffectKind kind) => kind switch
    {
        NetherCodeEffectKind.Rush => NetherCombatLane.Rush,
        NetherCodeEffectKind.Impact => NetherCombatLane.Impact,
        _ => null,
    };

    private static bool HasCategoryConflict(
        NetherCodeCandidate candidate,
        IReadOnlyList<NetherCodeState> current
    ) => current.Any(code => NetherCodeCategorySemantics.IsExclusive(candidate.Category, code.Category));

    private static IReadOnlyList<NetherCodeState> FindCategoryConflicts(
        NetherCodeCandidate candidate,
        IReadOnlyList<NetherCodeState> current
    ) => current
        .Where(code => NetherCodeCategorySemantics.IsExclusive(candidate.Category, code.Category))
        .OrderBy(code => code.CodeId)
        .ToArray();

    private static IReadOnlyList<long> BuildProtectedIds(
        IReadOnlyList<NetherCodeState> codes,
        NetherCodeEffectiveLevels effective
    )
    {
        var protectedIds = new List<long>();
        foreach (NetherCodeState code in codes)
        {
            if (code.CodeId == PreferredSafeCodeId || (effective.Safe >= 5 && code.EffectKind == NetherCodeEffectKind.Safe))
                protectedIds.Add(code.CodeId);
        }
        return protectedIds;
    }

    private static IReadOnlyList<NetherCodeState> FindRemovableCodes(
        IReadOnlyList<NetherCodeState> codes,
        IReadOnlyList<long> protectedIds,
        NetherCombatLane lane
    ) => codes
        .Where(code => !protectedIds.Contains(code.CodeId))
        .OrderByDescending(code => code.EffectKind == NetherCodeEffectKind.Risk || code.CodeId == RejectedRiskCodeId)
        .ThenByDescending(code => code.IsResearchOnly || code.EffectKind == NetherCodeEffectKind.ResearchOnly)
        .ThenByDescending(code => ToLane(code.EffectKind) is NetherCombatLane currentLane && currentLane != lane)
        .ThenByDescending(code => code.PartyCoverage == 0)
        .ThenBy(code => code.Rarity)
        .ThenBy(code => code.PartyCoverage)
        .ThenBy(code => code.CodeId)
        .ToArray();

    private static NetherCodeCandidate? SelectCandidate(
        IEnumerable<NetherCodeCandidate> candidates,
        NetherCodeEffectiveLevels effective,
        NetherCombatLane lane
    ) => candidates
        .OrderByDescending(candidate => candidate.CodeId == PreferredSafeCodeId)
        .ThenByDescending(candidate => candidate.EffectKind == NetherCodeEffectKind.Safe && SafeAfterCandidate(effective, candidate) >= 5)
        .ThenByDescending(candidate => ToLane(candidate.EffectKind) == lane)
        .ThenByDescending(candidate => candidate.PartyCoverage)
        .ThenByDescending(candidate => candidate.Rarity)
        .ThenByDescending(candidate => candidate.Level)
        .ThenBy(candidate => candidate.CodeId)
        .FirstOrDefault();

    private static int SafeAfterCandidate(NetherCodeEffectiveLevels effective, NetherCodeCandidate candidate) =>
        candidate.EffectKind == NetherCodeEffectKind.Safe ? checked(effective.Safe + candidate.Level) : effective.Safe;

    private static NetherCodeDecision Keep(
        NetherCombatLane lane,
        IReadOnlyList<long> protectedIds,
        string detail,
        IReadOnlyList<NetherCodeState>? removable = null
    ) => new()
    {
        Kind = NetherCodeDecisionKind.Keep,
        LockedLane = lane,
        Detail = detail,
        ProtectedCodeIds = protectedIds,
        RemovableCodeIds = removable?.Select(code => code.CodeId).ToArray() ?? Array.Empty<long>(),
    };

    private static NetherCodeDecision Pause(NetherPauseReason reason, string detail) => new()
    {
        Kind = NetherCodeDecisionKind.Pause,
        PauseReason = reason,
        Detail = detail,
    };
}
