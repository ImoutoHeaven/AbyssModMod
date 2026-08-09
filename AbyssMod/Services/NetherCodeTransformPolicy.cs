#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

internal sealed record NetherCodeTransformDecision
{
    public bool CanTransform { get; init; }
    public long RemoveCodeId { get; init; }
    public NetherPauseReason PauseReason { get; init; }
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<long> ProtectedCodeIds { get; init; } = Array.Empty<long>();
}

/// <summary>
/// Chooses the existing code passed to the native target_type=7 conversion flow.  The server
/// selects the new code, so this policy ranks only the authoritative current portfolio and
/// never invents a future code ID or a Rush/Impact semantic absent from master data.
/// </summary>
internal sealed class NetherCodeTransformPolicy
{
    private const long PreferredSafeCodeId = 30024;
    private const long RejectedRiskCodeId = 40024;

    public NetherCodeTransformDecision Decide(IReadOnlyList<NetherCodeState>? codes, int capacity)
    {
        if (codes == null
            || capacity < 1
            || codes.Count is < 1
            || codes.Count > capacity
            || codes.Any(code => code == null || !code.IsKnown || code.CodeId <= 0 || code.Level < 0 || code.Rarity < 0)
            || codes.Select(code => code.CodeId).Distinct().Count() != codes.Count)
        {
            return Pause(NetherPauseReason.UnknownMasterData, "invalid-code-transform-portfolio");
        }

        NetherCodeEffectiveLevels effective = NetherCodePolicy.CalculateEffectiveLevels(codes);
        if (effective.Safe < 0)
            return Pause(NetherPauseReason.UnknownEffect, "code-transform-level-overflow");

        long[] protectedIds = codes
            .Where(code => code.CodeId == PreferredSafeCodeId
                || (effective.Safe >= 5 && code.EffectKind == NetherCodeEffectKind.Safe))
            .Select(code => code.CodeId)
            .OrderBy(id => id)
            .ToArray();

        NetherCodeState? selected = codes
            .Where(code => !protectedIds.Contains(code.CodeId))
            .OrderByDescending(code => code.CodeId == RejectedRiskCodeId || code.EffectKind == NetherCodeEffectKind.Risk)
            .ThenByDescending(code => code.IsResearchOnlyKnown && code.IsResearchOnly
                || code.EffectKind == NetherCodeEffectKind.ResearchOnly)
            .ThenByDescending(code => code.EffectKind == NetherCodeEffectKind.General)
            .ThenBy(code => code.Rarity)
            .ThenBy(code => code.Level)
            .ThenBy(code => code.CodeId)
            .FirstOrDefault();

        return selected == null
            ? new NetherCodeTransformDecision
            {
                PauseReason = NetherPauseReason.NoSafeRoute,
                Detail = "no-removable-code-for-native-transform",
                ProtectedCodeIds = protectedIds,
            }
            : new NetherCodeTransformDecision
            {
                CanTransform = true,
                RemoveCodeId = selected.CodeId,
                ProtectedCodeIds = protectedIds,
                Detail = "remove:" + selected.CodeId,
            };
    }

    private static NetherCodeTransformDecision Pause(NetherPauseReason reason, string detail) => new()
    {
        PauseReason = reason,
        Detail = detail,
    };
}
