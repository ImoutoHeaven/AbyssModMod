#nullable enable

namespace AbyssMod.Patches;

public sealed class StaticNovelMessageState
{
    private string? _source;
    private string? _translated;
    private string? _displayed;

    public StaticNovelMessageState(
        string source,
        string? translated,
        bool displayedTranslated = true
    )
    {
        _source = source;
        _translated = translated;
        _displayed = displayedTranslated ? translated : source;
    }

    public string? Source => _source;

    public void SetTranslation(string translated)
    {
        if (!string.IsNullOrEmpty(translated))
            _translated = translated;
    }

    public bool SetDisplayedTranslation(string translated)
    {
        if (string.IsNullOrEmpty(translated) || string.Equals(_source, translated, System.StringComparison.Ordinal))
            return false;

        _translated = translated;
        _displayed = translated;
        return true;
    }

    public bool TrySelect(bool translationEnabled, out string message)
    {
        message = string.Empty;
        if (_source == null)
            return false;

        string? selected = translationEnabled ? _translated : _source;
        if (selected == null || string.Equals(_displayed, selected, System.StringComparison.Ordinal))
            return false;

        message = selected;
        _displayed = selected;
        return true;
    }

    public void Clear()
    {
        _source = null;
        _translated = null;
        _displayed = null;
    }
}
