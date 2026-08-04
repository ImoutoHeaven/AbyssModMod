using System;
using Project.ItemList.Top;
using Project.Outgame.UI.Popup;
using TMPro;

namespace AbyssMod.Services;

/// <summary>
/// 依 UI 层级判定文本类别，避免任务/深渊代码/图鉴等页面被误分到 equipment_effect。
/// </summary>
public static class UiContextHelper
{
    public static string ResolveCategory(TMP_Text tmp, string text)
    {
        if (tmp == null)
            return null;

        try
        {
            var itemList = tmp.GetComponentInParent<LeftView>();
            if (
                itemList?._nameText != null
                && itemList._nameText.GetInstanceID() == tmp.GetInstanceID()
            )
                return TranslationPaths.Items;
        }
        catch { }

        try
        {
            var itemPopup = tmp.GetComponentInParent<ItemDetailPopup>();
            if (
                itemPopup?._nameText != null
                && itemPopup._nameText.GetInstanceID() == tmp.GetInstanceID()
            )
                return TranslationPaths.Items;
        }
        catch { }

        try
        {
            if (IsUnder(tmp, "Mission", "Weekly", "Daily", "Achievement", "MissionPass", "BeginnerMission"))
                return TextClassifier.Mission;

            if (IsUnder(tmp, "AbyssCode", "ImpactCode", "RushCode", "SafeCode", "RiskCode", "CodeDetail", "CodeBook"))
                return TextClassifier.AbyssCode;

            if (IsUnder(tmp, "Dictionary", "Encyclopedia", "Library", "NovelList", "StoryReplay", "MainStory", "ScenarioList"))
                return TranslationPaths.Titles;

            if (IsUnder(tmp, "Option", "Settings", "Config", "MenuPopup", "GameMenu"))
                return TextClassifier.System;
        }
        catch { }

        return null;
    }

    private static bool IsUnder(TMP_Text tmp, params string[] markers)
    {
        var go = tmp.gameObject;
        if (go == null)
            return false;

        for (var t = go.transform; t != null; t = t.parent)
        {
            var name = t.name ?? string.Empty;
            foreach (var marker in markers)
            {
                if (name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        return false;
    }
}
