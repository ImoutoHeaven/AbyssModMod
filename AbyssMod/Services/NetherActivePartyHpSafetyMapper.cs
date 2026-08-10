#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

/// <summary>
/// The authoritative HP surface carried by one live <c>NetherPartyCharacterModel</c>.  The
/// native model updates <see cref="HpRatio"/> from the server's <c>current_hp_ratio</c>; it is
/// intentionally not reconstructed from a guessed current/max pair.
/// </summary>
internal readonly record struct NetherActiveBattleMemberHp(
    long CharacterId,
    double HpRatio,
    bool IsAlive
);

/// <summary>
/// The lowest authoritative party HP fraction, or an explicit unknown result.  A nullable
/// value prevents callers from silently treating an unavailable health contract as full health.
/// </summary>
internal readonly record struct NetherActivePartyHpSafety(
    bool IsKnown,
    int? MinimumHpPermille,
    string Detail
);

/// <summary>
/// Converts every live party character's authoritative HP ratio to the strict lowest permille
/// used by the battle route gate.  Any incomplete, duplicate, non-finite, or out-of-range
/// observation is unsafe.  A non-alive member is conservatively zero after its ratio has still
/// been validated, so a stale contradictory model cannot conceal malformed raw input.
/// </summary>
internal sealed class NetherActivePartyHpSafetyMapper
{
    public NetherActivePartyHpSafety Map(IReadOnlyList<NetherActiveBattleMemberHp>? members)
    {
        if (members == null || members.Count == 0)
            return Unknown("empty-active-party-character-models");

        var characterIds = new HashSet<long>();
        int minimumPermille = 1000;

        foreach (NetherActiveBattleMemberHp member in members)
        {
            if (member.CharacterId <= 0)
                return Unknown("invalid-nether-party-character-id");
            if (!characterIds.Add(member.CharacterId))
                return Unknown("duplicate-nether-party-character-id:" + member.CharacterId);
            if (double.IsNaN(member.HpRatio) || double.IsInfinity(member.HpRatio))
                return Unknown("non-finite-nether-party-hp-ratio:" + member.CharacterId);
            if (member.HpRatio is < 0d or > 1d)
                return Unknown("out-of-range-nether-party-hp-ratio:" + member.CharacterId);

            try
            {
                // Floor conversion is intentional: the server ratio is permille-shaped and a
                // fractional presentation value must never round a member up over the safety
                // threshold.
                int permille = checked((int)Math.Floor(checked(member.HpRatio * 1000d)));
                if (permille is < 0 or > 1000)
                    return Unknown("invalid-nether-party-hp-permille:" + member.CharacterId);
                minimumPermille = Math.Min(minimumPermille, member.IsAlive ? permille : 0);
            }
            catch (OverflowException)
            {
                return Unknown("nether-party-hp-permille-overflow:" + member.CharacterId);
            }
        }

        return new NetherActivePartyHpSafety(true, minimumPermille, string.Empty);
    }

    internal static NetherActivePartyHpSafety Unknown(string detail) => new(
        IsKnown: false,
        MinimumHpPermille: null,
        Detail: detail
    );
}
