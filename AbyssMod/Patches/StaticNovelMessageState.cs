#nullable enable

namespace AbyssMod.Patches;

public sealed class StaticNovelMessageState
{
    private string? _source;
    private string? _translated;
    private bool? _displayedTranslated = true;

    public StaticNovelMessageState(string source, string translated)
    {
        _source = source;
        _translated = translated;
    }

    public bool TrySelect(bool translationEnabled, out string message)
    {
        message = string.Empty;
        if (_source == null || _translated == null || _displayedTranslated == translationEnabled)
            return false;

        message = translationEnabled ? _translated : _source;
        _displayedTranslated = translationEnabled;
        return true;
    }

    public void Clear()
    {
        _source = null;
        _translated = null;
        _displayedTranslated = null;
    }
}
