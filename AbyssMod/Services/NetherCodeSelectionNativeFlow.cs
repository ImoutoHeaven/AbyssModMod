#nullable enable

namespace AbyssMod.Services;

internal enum NetherCodeSelectionNativeStage
{
    Idle,
    AwaitingConfirmationTask,
    AwaitingReplacementPopup,
    AwaitingCompletion,
    Completed,
}

/// <summary>
/// Models the native code-offer continuation.  The generated confirmation task is authoritative:
/// it encompasses the optional replacement popup and the subsequent server fix-code request.
/// </summary>
internal sealed class NetherCodeSelectionNativeFlow
{
    private long _selectedCodeId;
    private long _replaceCodeId;
    private long _popupSequenceBaseline;

    public NetherCodeSelectionNativeStage Stage { get; private set; } = NetherCodeSelectionNativeStage.Idle;

    public long SelectedCodeId => _selectedCodeId;

    public long ReplacementCodeId => _replaceCodeId;

    public bool Begin(long codeId, long replaceCodeId, long popupSequenceBaseline)
    {
        if (codeId <= 0 || replaceCodeId < 0 || codeId == replaceCodeId
            || Stage is not (NetherCodeSelectionNativeStage.Idle or NetherCodeSelectionNativeStage.Completed))
        {
            return false;
        }

        _selectedCodeId = codeId;
        _replaceCodeId = replaceCodeId;
        _popupSequenceBaseline = popupSequenceBaseline;
        Stage = NetherCodeSelectionNativeStage.AwaitingConfirmationTask;
        return true;
    }

    public bool ObserveConfirmationTask()
    {
        if (Stage != NetherCodeSelectionNativeStage.AwaitingConfirmationTask)
            return false;
        Stage = _replaceCodeId > 0
            ? NetherCodeSelectionNativeStage.AwaitingReplacementPopup
            : NetherCodeSelectionNativeStage.AwaitingCompletion;
        return true;
    }

    public bool CanSubmitReplacement(long popupSequence) =>
        Stage == NetherCodeSelectionNativeStage.AwaitingReplacementPopup
        && popupSequence > _popupSequenceBaseline;

    public bool SubmitReplacement(long popupSequence)
    {
        if (!CanSubmitReplacement(popupSequence))
            return false;
        Stage = NetherCodeSelectionNativeStage.AwaitingCompletion;
        return true;
    }

    public bool CompleteConfirmationTask()
    {
        if (Stage != NetherCodeSelectionNativeStage.AwaitingCompletion)
            return false;
        _selectedCodeId = 0;
        _replaceCodeId = 0;
        _popupSequenceBaseline = 0;
        Stage = NetherCodeSelectionNativeStage.Completed;
        return true;
    }

    public void Clear()
    {
        _selectedCodeId = 0;
        _replaceCodeId = 0;
        _popupSequenceBaseline = 0;
        Stage = NetherCodeSelectionNativeStage.Idle;
    }
}
