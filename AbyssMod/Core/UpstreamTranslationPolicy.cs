namespace AbyssMod;

public static class UpstreamTranslationPolicy
{
    public const string Cdn = "https://raw.githubusercontent.com/anosu/dotabyss-translation/refs/heads/main/translations";
    public const string Language = "zh_Hans";

    public static (string Cdn, string Language) Resolve(string cdn, string language) => (Cdn, Language);
}
