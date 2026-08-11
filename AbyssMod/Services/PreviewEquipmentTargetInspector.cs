using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

#nullable enable

namespace AbyssMod.Services;

public readonly record struct QuestPreviewBindingDescriptor(
    string TypeName,
    string MethodName,
    int ActionParameterIndex
);

public static class QuestPreviewBindingCatalog
{
    public static IReadOnlyList<QuestPreviewBindingDescriptor> Bindings { get; } =
        new QuestPreviewBindingDescriptor[]
        {
            new(
                "Project.MainStory.ExplorationQuestDetail.ExplorationQuestDetailPopup",
                "Initialize",
                2
            ),
            new(
                "Project.MainStory.StaminaQuestDetailPopup",
                "Initialize",
                2
            ),
            new(
                "Project.StoryEvent.StoryEventQuestDetailPopup",
                "Initialize",
                2
            ),
            new(
                "Project.Outgame.Event.BonusQuest.EventBonusQuestDetailPopup",
                "Initialize",
                2
            ),
            new(
                "Project.TrainingEvent.TrainingEventQuestDetailPopup",
                "Initialize",
                2
            ),
            new(
                "Project.HuntEvent.HuntEventQuestDetailPopup",
                "Initialize",
                2
            ),
            new(
                "Project.CommissionEvent.CommissionEventQuestDetailPopup",
                "Initialize",
                2
            ),
            new(
                "Project.MiningEvent.MiningEventQuestDetailPopup",
                "Initialize",
                3
            ),
            new(
                "Project.Disaster.DisasterQuestDetailPopup",
                "InitializeView",
                0
            ),
            new(
                "Project.IdleExploration.EncounterQuestEventQuestDetailPopup",
                "Initialize",
                1
            ),
            new(
                "Project.UnionRequest.UnionRequestDetailPopup",
                "Initialize",
                2
            ),
            new(
                "Project.UnionRequest.UnionRequestContentView",
                "Initialize",
                1
            ),
        };
}

public sealed class PreviewEquipmentTargetSnapshot
{
    public int ContentType { get; }
    public long ContentId { get; }
    public string Name { get; }
    public long GroupNo { get; }
    public int Rank { get; }
    public IReadOnlyList<int> Rarities { get; }
    public NormalExactDropTarget Target => new(ContentType, ContentId);
    public string Token => Target.Token;
    public string ToastBody => $"{Token}\n{Name} | Rank {Rank}";
    public string LogFields =>
        $"token={Token} contentType={ContentType} contentId={ContentId.ToString(CultureInfo.InvariantCulture)} "
        + $"groupNo={GroupNo.ToString(CultureInfo.InvariantCulture)} rank={Rank} "
        + $"rarities={(Rarities.Count == 0 ? "none" : string.Join("|", Rarities))} "
        + $"name={SanitizeLogValue(Name)}";

    public PreviewEquipmentTargetSnapshot(
        int contentType,
        long contentId,
        string name,
        long groupNo,
        int rank,
        IEnumerable<int> rarities
    )
    {
        ContentType = contentType;
        ContentId = contentId;
        Name = name ?? string.Empty;
        GroupNo = groupNo;
        Rank = rank;
        Rarities = (rarities ?? Array.Empty<int>()).ToArray();
    }

    private static string SanitizeLogValue(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');
}

public sealed class QuestPreviewOpenIntentTracker
{
    private NormalExactDropTarget? _pending;
    private double _recordedAtSeconds;

    public bool Record(int contentType, long contentId, double nowSeconds)
    {
        Clear();
        if (!NormalExactDropTarget.TryFormatTypeName(contentType, out _)
            || contentId <= 0)
            return false;

        _pending = new NormalExactDropTarget(contentType, contentId);
        _recordedAtSeconds = nowSeconds;
        return true;
    }

    public bool TryConsume(
        NormalExactDropTarget target,
        double nowSeconds,
        double lifetimeSeconds
    )
    {
        NormalExactDropTarget? pending = _pending;
        double recordedAt = _recordedAtSeconds;
        Clear();
        if (!pending.HasValue)
            return false;

        double age = nowSeconds - recordedAt;
        return age >= 0 && age <= lifetimeSeconds && pending.Value.Equals(target);
    }

    public void Clear()
    {
        _pending = null;
        _recordedAtSeconds = 0;
    }
}

public sealed class PreviewEquipmentTargetInspector
{
    public static PreviewEquipmentTargetInspector Shared { get; } = new();

    private readonly double _intentLifetimeSeconds;
    private readonly QuestPreviewOpenIntentTracker _intent = new();
    private object? _popup;
    private PreviewEquipmentTargetSnapshot? _snapshot;
    private Func<object, bool>? _isActive;

    public PreviewEquipmentTargetInspector(double intentLifetimeSeconds = 10)
    {
        if (intentLifetimeSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(intentLifetimeSeconds));
        _intentLifetimeSeconds = intentLifetimeSeconds;
    }

    public bool RecordQuestPreviewIntent(
        int contentType,
        long contentId,
        double nowSeconds
    )
    {
        ClearActive();
        return _intent.Record(contentType, contentId, nowSeconds);
    }

    public bool TryRegisterPopup(
        object? popup,
        PreviewEquipmentTargetSnapshot? snapshot,
        double nowSeconds,
        Func<object, bool>? isActive
    )
    {
        ClearActive();
        if (popup == null || snapshot == null || isActive == null)
        {
            _intent.Clear();
            return false;
        }
        if (!_intent.TryConsume(snapshot.Target, nowSeconds, _intentLifetimeSeconds))
            return false;

        _popup = popup;
        _snapshot = snapshot;
        _isActive = isActive;
        return true;
    }

    public bool TryGetActive(out PreviewEquipmentTargetSnapshot snapshot)
    {
        snapshot = null!;
        if (_popup == null || _snapshot == null || _isActive == null)
            return false;

        try
        {
            if (!_isActive(_popup))
            {
                ClearActive();
                return false;
            }
        }
        catch
        {
            ClearActive();
            return false;
        }

        snapshot = _snapshot;
        return true;
    }

    public void Clear()
    {
        _intent.Clear();
        ClearActive();
    }

    private void ClearActive()
    {
        _popup = null;
        _snapshot = null;
        _isActive = null;
    }
}
