#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// Holds an effect prediction only across the one native action that produced it.  The next
/// authoritative snapshot validates erosion exactly (unless code changed, which requires a
/// rebaseline) and rejects damage beyond the conservative per-character lower bound.
/// </summary>
internal readonly record struct NetherProjectionObservation(
    bool IsDrift,
    bool RequiresRebaseline,
    NetherPauseReason PauseReason,
    string Detail
);

internal sealed class NetherActionProjectionCalibration
{
    private readonly NetherErosionPolicy _erosionPolicy = new();
    private int? _predictedErosion;
    private string _codeFingerprint = string.Empty;
    private Dictionary<long, int>? _minimumExpectedHp;

    public void Expect(NetherEventDecision decision, NetherSnapshot before)
    {
        if (decision == null)
            throw new ArgumentNullException(nameof(decision));
        if (before == null)
            throw new ArgumentNullException(nameof(before));

        _predictedErosion = decision.ProjectedErosion;
        _codeFingerprint = before.CodeHash ?? string.Empty;
        _minimumExpectedHp = decision.HpDelta < 0
            ? before.Characters
                .Where(character => character.IsActive)
                .ToDictionary(
                    character => character.CharacterId,
                    character => checked(character.HpPermille + decision.HpDelta)
                )
            : null;
    }

    public NetherProjectionObservation Observe(NetherSnapshot after)
    {
        if (after == null)
            throw new ArgumentNullException(nameof(after));
        if (_predictedErosion == null)
            return new NetherProjectionObservation(false, false, NetherPauseReason.None, string.Empty);

        int predictedErosion = _predictedErosion.Value;
        string codeFingerprint = _codeFingerprint;
        Dictionary<long, int>? minimumHp = _minimumExpectedHp;
        Clear();

        NetherErosionObservation erosion = _erosionPolicy.CompareObserved(
            predictedErosion,
            after.ErosionPoint,
            codeFingerprint,
            after.CodeHash
        );
        if (erosion.IsDrift)
            return new NetherProjectionObservation(true, false, erosion.PauseReason, erosion.Detail);

        if (minimumHp != null)
        {
            var observedHp = after.Characters
                .Where(character => character.IsActive)
                .ToDictionary(character => character.CharacterId, character => character.HpPermille);
            foreach ((long characterId, int minimum) in minimumHp)
            {
                if (!observedHp.TryGetValue(characterId, out int observed) || observed < minimum)
                {
                    return new NetherProjectionObservation(
                        true,
                        erosion.RequiresRebaseline,
                        NetherPauseReason.UnsafeHp,
                        "hp-projection-drift:" + characterId
                    );
                }
            }
        }

        return new NetherProjectionObservation(
            false,
            erosion.RequiresRebaseline,
            NetherPauseReason.None,
            erosion.RequiresRebaseline ? erosion.Detail : string.Empty
        );
    }

    public void Clear()
    {
        _predictedErosion = null;
        _codeFingerprint = string.Empty;
        _minimumExpectedHp = null;
    }
}
