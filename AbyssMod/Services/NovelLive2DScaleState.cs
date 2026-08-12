using System;

namespace AbyssMod.Services;

/// <summary>Pure scale adjustment and delayed-save state used by the Unity patch.</summary>
public sealed class NovelLive2DScaleState
{
    public const float Minimum = 0.1f;
    public const float Maximum = 10f;

    private readonly float _saveDelaySeconds;
    private float _current = float.NaN;
    private float _saveAt;
    private bool _savePending;

    public NovelLive2DScaleState(float saveDelaySeconds)
    {
        _saveDelaySeconds = Math.Max(0f, saveDelaySeconds);
    }

    public float Current(float configuredScale)
    {
        if (float.IsNaN(_current))
            _current = Clamp(configuredScale);
        return _current;
    }

    public bool TryAdjust(
        float wheelDelta,
        float now,
        float configuredScale,
        out float adjustedScale
    )
    {
        if (wheelDelta == 0f)
        {
            adjustedScale = Current(configuredScale);
            return false;
        }

        float scale = Current(configuredScale);
        _current = Clamp(MathF.Round((scale + wheelDelta * 0.01f) * 100f) / 100f);
        _saveAt = now + _saveDelaySeconds;
        _savePending = true;
        adjustedScale = _current;
        return true;
    }

    public bool ShouldSave(float now) => _savePending && now >= _saveAt;

    public void MarkSaved() => _savePending = false;

    public bool Reload(float configuredScale, out float reloadedScale)
    {
        _savePending = false;
        float clamped = Clamp(configuredScale);
        bool changed = float.IsNaN(_current) || _current != clamped;
        _current = clamped;
        reloadedScale = _current;
        return changed;
    }

    private static float Clamp(float value) => Math.Clamp(value, Minimum, Maximum);
}
