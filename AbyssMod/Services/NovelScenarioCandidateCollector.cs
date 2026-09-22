#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AbyssMod.Services;

public enum NovelMachineTranslationMode
{
    Sentence = 0,
    Script = 1,
}

internal sealed class NovelScenarioSnapshot
{
    public NovelScenarioSnapshot(
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<string> candidates,
        bool isComplete
    )
    {
        Rows = rows;
        Candidates = candidates;
        IsComplete = isComplete && rows.Count > 0;
    }

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    public IReadOnlyList<string> Candidates { get; }

    public bool IsComplete { get; }
}

internal static class NovelScenarioCandidateCollector
{
    private static readonly HashSet<string> MessageCommands = new(
        [
            "message",
            "l2dmessage",
            "dotmessage",
            "asyncdotmessage",
            "messagetextcenter",
            "messagetextunder",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    public static IReadOnlyList<string> Collect(IEnumerable<IReadOnlyList<string>> rows) =>
        Capture(rows, isComplete: true).Candidates;

    public static NovelScenarioSnapshot Capture(
        IEnumerable<IReadOnlyList<string>?>? rows,
        bool isComplete
    )
    {
        var capturedRows = new List<IReadOnlyList<string>>();
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (rows == null)
            return new NovelScenarioSnapshot(capturedRows, candidates, isComplete: false);

        foreach (var row in rows)
        {
            if (row == null)
            {
                isComplete = false;
                continue;
            }

            var copy = row.ToArray();
            capturedRows.Add(copy);
            if (TryGetTranslatableText(copy, out string text)
                && MachineTranslationTextProtection.HasKana(text)
                && seen.Add(text))
                candidates.Add(text);
        }

        return new NovelScenarioSnapshot(capturedRows, candidates, isComplete);
    }

    internal static bool TryGetTranslatableText(
        IReadOnlyList<string> row,
        out string text
    )
    {
        text = string.Empty;
        if (row == null || row.Count <= 2)
            return false;

        string command = row[0];
        if (!MessageCommands.Contains(command)
            && !string.Equals(command, "select", StringComparison.OrdinalIgnoreCase))
            return false;

        text = row[2];
        return !string.IsNullOrEmpty(text);
    }
}

internal readonly record struct NovelScriptTranslationTarget(string Source, string Template);

internal static class NovelScriptTranslationProtocol
{
    public const int Version = 1;

    public static bool CanUse(
        NovelMachineTranslationMode mode,
        string? engine,
        bool hasCompleteScene
    )
    {
        if (mode != NovelMachineTranslationMode.Script || !hasCompleteScene)
            return false;

        string normalized = (engine ?? "openai").Trim().ToLowerInvariant();
        return normalized != "sugoi" && normalized != "libre";
    }

}

internal sealed class NovelScriptTranslationBatch
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IReadOnlyList<TargetState> _targets;

    public NovelScriptTranslationBatch(
        string sceneId,
        NovelScenarioSnapshot scene,
        IEnumerable<NovelScriptTranslationTarget> targets
    )
    {
        var targetStates = new List<TargetState>();
        var byTemplate = new Dictionary<string, TargetState>(StringComparer.Ordinal);
        var idBySource = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var target in targets)
        {
            if (string.IsNullOrEmpty(target.Source) || string.IsNullOrEmpty(target.Template))
                continue;

            if (!byTemplate.TryGetValue(target.Template, out var state))
            {
                state = new TargetState(
                    $"t{targetStates.Count:D4}",
                    target.Template,
                    MachineTranslationTextProtection.Protect(NormalizeSceneText(target.Template))
                );
                byTemplate.Add(target.Template, state);
                targetStates.Add(state);
            }

            idBySource[target.Source] = state.Id;
        }

        _targets = targetStates;
        var occurrences = targetStates.ToDictionary(
            target => target.Id,
            _ => new List<int>(),
            StringComparer.Ordinal
        );
        var sceneRows = new List<SceneRowPayload>(scene.Rows.Count);
        for (int i = 0; i < scene.Rows.Count; i++)
        {
            string[] fields = scene.Rows[i].ToArray();
            if (NovelScenarioCandidateCollector.TryGetTranslatableText(
                    fields,
                    out string text
                ))
            {
                fields[2] = NormalizeSceneText(text);
                if (idBySource.TryGetValue(text, out string? id))
                    occurrences[id].Add(i);
            }

            sceneRows.Add(new SceneRowPayload { Row = i, Fields = fields });
        }

        Prompt = JsonSerializer.Serialize(
            new ScriptRequestPayload
            {
                Version = NovelScriptTranslationProtocol.Version,
                SceneId = sceneId ?? string.Empty,
                Scene = sceneRows,
                Targets = targetStates.Select(target => new TargetPayload
                {
                    Id = target.Id,
                    Source = target.Protected.Text,
                    Occurrences = occurrences[target.Id],
                }).ToList(),
            },
            JsonOptions
        );
    }

    public string Prompt { get; }

    public int TargetCount => _targets.Count;

    public bool TryParseResponse(
        string? response,
        out Dictionary<string, string> translations
    )
    {
        translations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            var document = JsonSerializer.Deserialize<ScriptResponsePayload>(
                StripCodeFence(response),
                JsonOptions
            );
            if (document?.Version != NovelScriptTranslationProtocol.Version
                || document.Translations == null
                || document.Translations.Count != _targets.Count)
                return false;

            for (int i = 0; i < _targets.Count; i++)
            {
                TargetState expected = _targets[i];
                TranslationPayload? actual = document.Translations[i];
                if (actual == null
                    || !string.Equals(actual.Id, expected.Id, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(actual.Text)
                    || !expected.Protected.TryRestore(actual.Text.Trim(), out string restored)
                    || string.IsNullOrWhiteSpace(restored))
                    return false;

                translations.Add(expected.Template, restored);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeSceneText(string text) =>
        text.Replace("%user%", "<user>", StringComparison.Ordinal)
            .Replace("\\r\\n", "<br>", StringComparison.Ordinal)
            .Replace("\\n", "<br>", StringComparison.Ordinal)
            .Replace("\\r", "<br>", StringComparison.Ordinal)
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\r", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal);

    private static string StripCodeFence(string response)
    {
        string trimmed = response.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        int start = trimmed.IndexOf('\n');
        int end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return start >= 0 && end > start
            ? trimmed.Substring(start + 1, end - start - 1).Trim()
            : trimmed;
    }

    private sealed record TargetState(
        string Id,
        string Template,
        ProtectedMachineTranslationText Protected
    );

    private sealed class ScriptRequestPayload
    {
        public int Version { get; set; }
        public string SceneId { get; set; } = string.Empty;
        public List<SceneRowPayload> Scene { get; set; } = new();
        public List<TargetPayload> Targets { get; set; } = new();
    }

    private sealed class SceneRowPayload
    {
        public int Row { get; set; }
        public IReadOnlyList<string> Fields { get; set; } = Array.Empty<string>();
    }

    private sealed class TargetPayload
    {
        public string Id { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public IReadOnlyList<int> Occurrences { get; set; } = Array.Empty<int>();
    }

    private sealed class ScriptResponsePayload
    {
        public int Version { get; set; }
        public List<TranslationPayload?>? Translations { get; set; }
    }

    private sealed class TranslationPayload
    {
        public string Id { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }
}
