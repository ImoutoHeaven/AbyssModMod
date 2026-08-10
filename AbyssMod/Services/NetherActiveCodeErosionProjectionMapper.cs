#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// Raw ownership fields copied from a live <c>NetherCodeData</c>.  Amount is retained in the
/// fingerprint even though the current erosion master mapping consumes the master parameter,
/// so a server-side ownership change can never reuse a stale projection identity.
/// </summary>
internal readonly record struct NetherPossessionCodeErosionInput(long CodeId, long Amount)
{
    public bool HasRequiredFields { get; init; } = true;
}

/// <summary>
/// All raw effect fields from one <c>MNetherCodes</c> master row.  Parameter two and three are
/// deliberately retained rather than discarded: a non-zero value on a 6–9 erosion effect is
/// not currently representable by the one-amount erosion policy and therefore fails closed.
/// </summary>
internal readonly record struct NetherCodeErosionMasterInput(
    long CodeId,
    int EffectType,
    long EffectParameter1,
    long EffectParameter2,
    long EffectParameter3
)
{
    public bool HasRequiredFields { get; init; } = true;
}

/// <summary>
/// A sorted, authoritative code/master join retained with the projection so diagnostics and
/// future policy versions can distinguish a code-ID-only match from a complete parameter match.
/// </summary>
internal readonly record struct NetherActiveCodeErosionEntry(
    long CodeId,
    long PossessionAmount,
    int EffectType,
    long EffectParameter1,
    long EffectParameter2,
    long EffectParameter3
);

/// <summary>
/// Result of mapping the current possession portfolio to erosion modifiers.  The projection is
/// usable only when every active code/master relationship is unambiguous and understood.
/// </summary>
internal sealed record NetherActiveCodeErosionProjection
{
    public bool ErosionProjectionKnown { get; init; }
    public IReadOnlyList<long> SortedCodeIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<NetherActiveCodeErosionEntry> Entries { get; init; } =
        Array.Empty<NetherActiveCodeErosionEntry>();
    public IReadOnlyList<NetherCodeEffect> ErosionEffects { get; init; } = Array.Empty<NetherCodeEffect>();
    public string CodeHash { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Builds a fail-closed erosion projection from live possession code models and exact master
/// rows.  Effect types 1/2 are confirmed non-erosion inputs: they stay in the fingerprint but
/// produce no modifier.  Types 6–9 map directly to the existing <see cref="NetherCodeEffect"/>
/// model.  No code ID, including 30024 or 40024, has a special erosion meaning here.
/// </summary>
internal sealed class NetherActiveCodeErosionProjectionMapper
{
    public NetherActiveCodeErosionProjection Map(
        IReadOnlyList<NetherPossessionCodeErosionInput>? possessions,
        IReadOnlyList<NetherCodeErosionMasterInput>? masters
    )
    {
        if (possessions == null)
            return Unknown("missing-possession-nether-codes");
        if (possessions.Count == 0)
            return Known(
                Array.Empty<NetherActiveCodeErosionEntry>(),
                Array.Empty<NetherCodeEffect>()
            );
        if (masters == null)
            return Unknown("missing-m-nether-codes");

        var possessionById = new Dictionary<long, NetherPossessionCodeErosionInput>();
        foreach (NetherPossessionCodeErosionInput possession in possessions)
        {
            if (!possession.HasRequiredFields || possession.CodeId <= 0 || possession.Amount < 0)
                return Unknown("invalid-possession-nether-code");
            if (!possessionById.TryAdd(possession.CodeId, possession))
                return Unknown("duplicate-possession-nether-code:" + possession.CodeId);
        }

        var mastersByActiveCodeId = new Dictionary<long, List<NetherCodeErosionMasterInput>>();
        foreach (NetherCodeErosionMasterInput master in masters)
        {
            if (!possessionById.ContainsKey(master.CodeId))
                continue;
            if (!mastersByActiveCodeId.TryGetValue(master.CodeId, out List<NetherCodeErosionMasterInput>? matches))
            {
                matches = new List<NetherCodeErosionMasterInput>();
                mastersByActiveCodeId.Add(master.CodeId, matches);
            }
            matches.Add(master);
        }

        var entries = new List<NetherActiveCodeErosionEntry>(possessionById.Count);
        var effects = new List<NetherCodeEffect>();
        foreach (NetherPossessionCodeErosionInput possession in possessionById.Values.OrderBy(code => code.CodeId))
        {
            if (!mastersByActiveCodeId.TryGetValue(possession.CodeId, out List<NetherCodeErosionMasterInput>? matches)
                || matches.Count == 0)
            {
                return Unknown("missing-m-nether-code:" + possession.CodeId);
            }
            if (matches.Count != 1)
                return Unknown("duplicate-m-nether-code:" + possession.CodeId);

            NetherCodeErosionMasterInput master = matches[0];
            if (!master.HasRequiredFields || master.CodeId != possession.CodeId)
                return Unknown("invalid-m-nether-code:" + possession.CodeId);

            entries.Add(new NetherActiveCodeErosionEntry(
                possession.CodeId,
                possession.Amount,
                master.EffectType,
                master.EffectParameter1,
                master.EffectParameter2,
                master.EffectParameter3
            ));

            switch (master.EffectType)
            {
                // Confirmed ordinary/party effects: their raw values remain in the entry/hash,
                // but they do not alter battle erosion.
                case 1:
                case 2:
                    break;
                case 6:
                case 7:
                case 8:
                case 9:
                    if (!TryMapErosionEffect(master, out NetherCodeEffect effect, out string error))
                        return Unknown(error + ":" + possession.CodeId);
                    effects.Add(effect);
                    break;
                default:
                    return Unknown("unknown-nether-code-effect-type:" + master.EffectType);
            }
        }

        return Known(entries, effects);
    }

    private static bool TryMapErosionEffect(
        NetherCodeErosionMasterInput master,
        out NetherCodeEffect effect,
        out string error
    )
    {
        effect = default;
        error = string.Empty;
        if (master.EffectParameter1 is <= 0 or > int.MaxValue)
        {
            error = "invalid-nether-code-effect-parameter-1";
            return false;
        }
        // Parameters two and three cannot be projected by NetherErosionPolicy's single amount
        // contract.  Treating them as zero would change a native effect, so only the explicitly
        // unparameterized shape is currently safe.
        if (master.EffectParameter2 != 0 || master.EffectParameter3 != 0)
        {
            error = "unprojectable-nether-code-effect-parameter-2-or-3";
            return false;
        }

        NetherCodeEffectKind kind = master.EffectType switch
        {
            6 => NetherCodeEffectKind.ErosionAdditionUp,
            7 => NetherCodeEffectKind.ErosionAdditionDown,
            8 => NetherCodeEffectKind.ErosionRateUp,
            9 => NetherCodeEffectKind.ErosionRateDown,
            _ => NetherCodeEffectKind.Unknown,
        };
        if (kind == NetherCodeEffectKind.Unknown)
        {
            error = "unknown-nether-code-effect-type";
            return false;
        }

        effect = new NetherCodeEffect(
            master.CodeId,
            kind,
            checked((int)master.EffectParameter1)
        )
        {
            IsKnown = true,
            OrderKnown = true,
        };
        return true;
    }

    private static NetherActiveCodeErosionProjection Known(
        IReadOnlyList<NetherActiveCodeErosionEntry> entries,
        IReadOnlyList<NetherCodeEffect> effects
    ) => new()
    {
        ErosionProjectionKnown = true,
        SortedCodeIds = entries.Select(entry => entry.CodeId).ToArray(),
        Entries = entries,
        ErosionEffects = effects,
        CodeHash = CreateCodeHash(entries),
        Detail = string.Empty,
    };

    internal static NetherActiveCodeErosionProjection Unknown(string detail) => new()
    {
        ErosionProjectionKnown = false,
        SortedCodeIds = Array.Empty<long>(),
        Entries = Array.Empty<NetherActiveCodeErosionEntry>(),
        ErosionEffects = Array.Empty<NetherCodeEffect>(),
        CodeHash = "nether-codes:unknown",
        Detail = detail,
    };

    private static string CreateCodeHash(IReadOnlyList<NetherActiveCodeErosionEntry> entries)
    {
        if (entries.Count == 0)
            return "nether-codes:none";
        return string.Join(
            ";",
            entries.Select(entry => string.Join(
                ":",
                entry.CodeId.ToString(CultureInfo.InvariantCulture),
                entry.PossessionAmount.ToString(CultureInfo.InvariantCulture),
                entry.EffectType.ToString(CultureInfo.InvariantCulture),
                entry.EffectParameter1.ToString(CultureInfo.InvariantCulture),
                entry.EffectParameter2.ToString(CultureInfo.InvariantCulture),
                entry.EffectParameter3.ToString(CultureInfo.InvariantCulture)
            ))
        );
    }
}
