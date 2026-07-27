using HarmonyLib;
using Absf;
using Absf.Novel;
using Il2CppSystem;
using Il2CppSystem.Collections.Generic;
using Il2CppSystem.Threading;
using AbyssMod.Services;
using Project;
using Project.Library;
using Project.MainStory;
using Project.Novel;
using Project.Outgame;
using Project.User;

namespace AbyssMod.Patches;

/// <summary>
/// 剧情翻译补丁：标题、人名、对话文本的翻译注入。
/// </summary>
[HarmonyPatch]
public static class TranslationPatch
{
    private const string UserPlaceholder = "<user>";
    private const string HiddenUserPlaceholder = "%user%";
    private static NovelController _novelController;
    private static NovelViewMessageWindow _messageWindow;
    private static NovelText _messageText;
    private static NovelViewMessageWindow _staticMessageWindow;
    private static NovelText _staticMessageText;
    private static StaticNovelMessageState _staticMessageState;
    private static string _machineTranslationSource;
    private static string _lastRefreshedMachineTranslationSource;
    private static string _lastRefreshDiagnostic;
    private static bool _refreshingMessage;
    private static string NovelId
    {
        get => _novelController?._common?.ScriptId ?? string.Empty;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelController), nameof(NovelController.InitNovel))]
    public static void InitNovelController(NovelController __instance)
    {
        _novelController = __instance;
        ClearStaticMessage();
        ClearMachineTranslationMessage();
    }

    public static bool TryGetCurrentNovel(
        out System.Collections.Generic.Dictionary<string, string> translation
    )
    {
        return TryGetNovel(NovelId, out translation);
    }

    private static bool TryGetNovel(
        string novelId,
        out System.Collections.Generic.Dictionary<string, string> translation
    )
    {
        translation = null;
        return Config.Translation.Value
            && !string.IsNullOrEmpty(novelId)
            && Plugin.Trans.Novels.TryGetValue(novelId, out translation);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NovelPathUtility), nameof(NovelPathUtility.GetNovelScenarioDirectory))]
    public static void SetupTranslation(string novelId)
    {
        if (!Config.Translation.Value)
            return;

        Plugin.Log.LogInfo($"NovelId: {novelId}");

        Plugin.Trans.GetNovelTranslationAsync(novelId).Wait();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NovelScriptInfoUtility), nameof(NovelScriptInfoUtility.GetScriptInfo))]
    public static void SetTitleAndDescription(ValueTuple<string, string> __result)
    {
        if (TryGetCurrentNovel(out var _))
        {
            string title = __result.Item1;
            if (
                !string.IsNullOrEmpty(title)
                && Plugin.Trans.Titles.TryGetValue(title, out string tTitle)
            )
                __result.Item1 = tTitle;

            string description = __result.Item2;
            if (
                !string.IsNullOrEmpty(description)
                && Plugin.Trans.Descriptions.TryGetValue(description, out string tDescription)
            )
                __result.Item2 = tDescription;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelTitle), nameof(NovelTitle.SetTitle))]
    public static void SetTitle(ref string title)
    {
        if (TryGetCurrentNovel(out var _))
        {
            if (
                !string.IsNullOrEmpty(title)
                && Plugin.Trans.Titles.TryGetValue(title, out string tTitle)
            )
                title = tTitle;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelViewMessageWindow), nameof(NovelViewMessageWindow.SetName))]
    public static void SetName(ref string name)
    {
        if (TryGetCurrentNovel(out var _))
        {
            if (
                !string.IsNullOrEmpty(name)
                && Plugin.Trans.Names.TryGetValue(name, out string tName)
            )
                name = tName;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelText), nameof(NovelText.Parse))]
    public static void SetText(NovelText __instance, List<Letter> letters, ref string message)
    {
        if (!_refreshingMessage)
        {
            ClearStaticMessage();
            ClearMachineTranslationMessage();
        }

        if (!NovelMessageRefreshPolicy.ShouldProcessNovelText(
                translationEnabled: Config.Translation.Value,
                isRefreshReplay: _refreshingMessage,
                message: message))
            return;

        var parentWindow = __instance.GetComponentInParent<NovelViewMessageWindow>();
        if (TryGetCurrentNovel(out var translation)
            && translation.TryGetValue(message, out string staticTranslation)
            && !string.IsNullOrEmpty(staticTranslation))
        {
            CaptureStaticMessage(parentWindow, __instance, message, staticTranslation);
            NovelTextTranslation.TryTranslate(
                translation,
                message,
                GetDisplayUserName(),
                out message
            );
            return;
        }

        // Parse is the verified full-sentence boundary used by the live story view. It must
        // resolve the MT cache before the game creates the individual letter TMP objects.
        string source = TextTranslator.Process(TextClassifier.Dialogue, message);
        message = MachineTranslator.Handle(TextClassifier.Dialogue, source);
        message = NovelTextTranslation.ExpandUserPlaceholder(message, GetDisplayUserName());
        if (NovelMessageRefreshPolicy.ShouldTrackRefreshCandidate(
                translationEnabled: Config.Translation.Value,
                belongsToCurrentMessage: parentWindow != null,
                source: source,
                displayed: message)
            && TextTranslator.HasKana(source))
        {
            _messageWindow = parentWindow;
            _messageText = __instance;
            _machineTranslationSource = source;
            _lastRefreshedMachineTranslationSource = null;
            _lastRefreshDiagnostic = null;
            Logger.Info($"Novel MT refresh candidate: '{Abbreviate(source)}'");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelText), nameof(NovelText.Show))]
    public static void ShowMachineTranslationImmediately(ref bool isImmediately)
    {
        if (_refreshingMessage)
            isImmediately = true;
    }

    /// <summary>
    /// Invoked on the Unity main thread. It replaces only the still-current, fully displayed
    /// message after its complete-source LLM result enters the cache.
    /// </summary>
    public static void RefreshCurrentMessage()
    {
        RefreshStaticMessage();
        RefreshMachineTranslationMessage();
    }

    private static void RefreshStaticMessage()
    {
        if (_staticMessageWindow == null || _staticMessageText == null || _staticMessageState == null)
            return;

        try
        {
            if (!_staticMessageState.TrySelect(Config.Translation.Value, out string message))
                return;

            if (Config.Translation.Value)
                message = NovelTextTranslation.ExpandUserPlaceholder(message, GetDisplayUserName());

            ReplayMessage(_staticMessageWindow, _staticMessageText, message);
            Logger.Info("Current static novel message refreshed");
        }
        catch (System.Exception e)
        {
            Logger.Warn($"Current static novel message refresh failed: {e.Message}");
        }
    }

    private static void RefreshMachineTranslationMessage()
    {
        if (!Config.Translation.Value
            || _messageWindow == null
            || string.IsNullOrEmpty(_machineTranslationSource))
            return;

        try
        {
            if (!MachineTranslator.TryGetCachedTranslation(_machineTranslationSource, out var translated))
                return;

            bool shouldRefresh = NovelMessageRefreshPolicy.ShouldRefresh(
                translationEnabled: Config.Translation.Value,
                typewriterFinished: !_messageWindow._isPlay,
                source: _machineTranslationSource,
                lastRefreshedSource: _lastRefreshedMachineTranslationSource,
                translated: translated
            );
            string diagnostic = $"Novel MT refresh: typing={_messageWindow._isPlay}, "
                + $"shouldRefresh={shouldRefresh}, source='{Abbreviate(_machineTranslationSource)}'";
            if (!string.Equals(diagnostic, _lastRefreshDiagnostic, System.StringComparison.Ordinal))
            {
                _lastRefreshDiagnostic = diagnostic;
                Logger.Info(diagnostic);
            }
            if (!shouldRefresh)
                return;

            translated = NovelTextTranslation.ExpandUserPlaceholder(translated, GetDisplayUserName());
            ReplayMessage(_messageWindow, _messageText, translated);
            _lastRefreshedMachineTranslationSource = _machineTranslationSource;
            Logger.Info($"Novel MT refreshed: '{Abbreviate(_machineTranslationSource)}'");
        }
        catch (System.Exception e)
        {
            Logger.Warn($"RefreshCurrentMessage failed: {e.Message}");
        }
    }

    private static void CaptureStaticMessage(
        NovelViewMessageWindow messageWindow,
        NovelText messageText,
        string source,
        string translated
    )
    {
        if (messageWindow == null || messageText == null)
            return;

        _staticMessageWindow = messageWindow;
        _staticMessageText = messageText;
        _staticMessageState = new StaticNovelMessageState(source, translated);
    }

    private static void ClearStaticMessage()
    {
        _staticMessageWindow = null;
        _staticMessageText = null;
        _staticMessageState?.Clear();
        _staticMessageState = null;
    }

    private static void ClearMachineTranslationMessage()
    {
        _messageWindow = null;
        _messageText = null;
        _machineTranslationSource = null;
        _lastRefreshedMachineTranslationSource = null;
        _lastRefreshDiagnostic = null;
    }

    private static void ReplayMessage(
        NovelViewMessageWindow messageWindow,
        NovelText messageText,
        string message
    )
    {
        _refreshingMessage = true;
        try
        {
            var letters = messageWindow._letters;
            messageText.ReturnLetterObj(letters);
            letters.Clear();
            messageText.Parse(letters, message);
            messageText.Show(letters, messageWindow._LineYPosPairs, messageText._isAdult, isImmediately: true);
        }
        finally
        {
            _refreshingMessage = false;
        }
    }

    private static string Abbreviate(string text) =>
        text.Length <= 80 ? text : text.Substring(0, 80) + "...";

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelModelMessageLog), nameof(NovelModelMessageLog.Add))]
    public static bool SetLogAdd(
        string scriptId,
        string assetId,
        ref string charaName,
        ref string message,
        string logId,
        NovelSound voice,
        CancellationToken ct
    )
    {
        if (_refreshingMessage)
            return false;

        charaName = HideUserPlaceholder(charaName);
        message = HideUserPlaceholder(message);
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelLogPopup), nameof(NovelLogPopup.SetData))]
    public static void SetLog(ref List<NovelLogData> dataList)
    {
        List<NovelLogData> list = new();
        foreach (var data in dataList)
        {
            string name = RestoreUserPlaceholder(data.Name);
            string message = RestoreUserPlaceholder(data.Message);

            if (TryGetNovel(data.ScriptId, out var translation))
            {
                if (
                    !string.IsNullOrEmpty(name)
                    && Plugin.Trans.Names.TryGetValue(name, out string tName)
                )
                    name = tName;

                if (
                    !string.IsNullOrEmpty(message)
                    && translation.TryGetValue(message, out string tMessage)
                )
                    message = tMessage;
            }

            list.Add(
                new NovelLogData(
                    data.ScriptId,
                    data.AssetId,
                    name,
                    message,
                    data.LogId,
                    data.Voice,
                    data.Ct
                )
            );
        }
        dataList = list;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelModelDotBalloon), nameof(NovelModelDotBalloon.StartBalloonMessage))]
    public static void SetBalloon(CommandDotMessageData messageData)
    {
        if (TryGetCurrentNovel(out var translation))
        {
            string message = messageData.Message;
            if (
                !string.IsNullOrEmpty(message)
                && translation.TryGetValue(message, out string tMessage)
            )
                messageData.Message = tMessage;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(
        typeof(NovelCmdMessageTextCenter),
        nameof(NovelCmdMessageTextCenter.OnCommandStartASync)
    )]
    public static void HideCenterTextUserPlaceholder(NovelArguments args)
    {
        string message = args.GetString(2);
        if (ContainsUserPlaceholder(message))
            args._list[2] = NovelArgument.SetString(HideUserPlaceholder(message));
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelModelMessageText), nameof(NovelModelMessageText.SetMessage))]
    public static void TranslateCenterText(CommandMessageTextData data)
    {
        data.Message = RestoreUserPlaceholder(data.Message);

        if (TryGetCurrentNovel(out var translation)
            && !string.IsNullOrEmpty(data.Message)
            && translation.TryGetValue(data.Message, out string translated))
            data.Message = translated;

        if (ContainsUserPlaceholder(data.Message))
            data.Message = ExpandUserPlaceholder(data.Message, GetDisplayUserName());
    }

    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(LibraryNovelPlayPopupController),
        nameof(LibraryNovelPlayPopupController.InitializePopup)
    )]
    public static void SetLibraryPopup(LibraryNovelPlayPopupController __instance, TextPopup popup)
    {
        if (Config.Translation.Value)
        {
            string title = __instance._model.Title;
            if (
                !string.IsNullOrEmpty(title)
                && Plugin.Trans.Titles.TryGetValue(title, out string tTitle)
            )
                popup._titleText.text = tTitle;

            string description = __instance._model.Description;
            if (
                !string.IsNullOrEmpty(description)
                && Plugin.Trans.Descriptions.TryGetValue(description, out string tDescription)
            )
                popup._contentText.text = tDescription;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StoryQuestDetailPopup), nameof(StoryQuestDetailPopup.Setup))]
    public static void SetStoryQuestDetail(StoryQuestDetailPopup __instance)
    {
        if (Config.Translation.Value)
        {
            string description = __instance._storyDescription.text;
            if (
                !string.IsNullOrEmpty(description)
                && Plugin.Trans.Descriptions.TryGetValue(description, out string tDescription)
            )
                __instance._storyDescription.text = tDescription;
        }
    }

    private static bool ContainsUserPlaceholder(string value) =>
        !string.IsNullOrEmpty(value) && value.Contains(UserPlaceholder, System.StringComparison.Ordinal);

    private static string HideUserPlaceholder(string value) =>
        value?.Replace(UserPlaceholder, HiddenUserPlaceholder, System.StringComparison.Ordinal);

    private static string RestoreUserPlaceholder(string value) =>
        value?.Replace(HiddenUserPlaceholder, UserPlaceholder, System.StringComparison.Ordinal);

    private static string GetDisplayUserName()
    {
        try
        {
            string userName = Engine.Get<UserData>().UserStatus.Name.Value;
            return StringUtility.ToDisplayUserName(userName);
        }
        catch
        {
            return null;
        }
    }

    private static string ExpandUserPlaceholder(string value, string displayName)
    {
        return NovelTextTranslation.ExpandUserPlaceholder(value, displayName);
    }
}
