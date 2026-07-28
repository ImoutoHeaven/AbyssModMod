using System;
using System.Collections.Generic;
using AbyssMod.Services;
using HarmonyLib;
using Project;
using Project.Novel;
using TMPro;
using AbyssMod;
using UnityEngine;

namespace AbyssMod.Patches;

/// <summary>
/// 通用文本补丁：
///   1. 拦截 TMP_Text 文本设置（属性 setter 与纯字符串 SetText 重载），
///      涵盖技能名、装备效果、UI、酒馆卡片标签等几乎所有界面文字。
///   2. 拦截技能描述格式化器构造，翻译技能原始模板（含占位符）。
/// </summary>
[HarmonyPatch]
public static class GeneralTextPatch
{
    private static readonly HashSet<int> NovelLetterTextIds = new();
    // ──────────────────────────────────────────────────
    // text = "..." 属性 setter（参数名为 value）
    // ──────────────────────────────────────────────────

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TMP_Text), "set_text")]
    public static void OnSetText(ref string value, TMP_Text __instance)
    {
        ApplyTranslation(ref value, __instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TextMeshProUGUI), nameof(TextMeshProUGUI.OnEnable))]
    public static void OnUiTextEnable(TextMeshProUGUI __instance) =>
        TranslateStaticUiText(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TextMeshPro), nameof(TextMeshPro.OnEnable))]
    public static void OnWorldTextEnable(TextMeshPro __instance) =>
        TranslateStaticUiText(__instance);

    private static void TranslateStaticUiText(TMP_Text text)
    {
        if (text == null || !Config.Translation.Value)
            return;

        try
        {
            string translated = UiTextTranslator.Translate(text, text.text);
            if (!string.Equals(translated, text.text, StringComparison.Ordinal))
            {
                _inTranslation = true;
                try
                {
                    TmpTextHelper.TrySetTextDirect(text, translated);
                }
                finally
                {
                    _inTranslation = false;
                }
            }
        }
        catch (Exception e)
        {
            Logger.Warn($"TranslateStaticUiText failed: {e.Message}");
        }
    }

    // ──────────────────────────────────────────────────
    // 注意：绝不可 patch TMP_Text.SetText(string) / SetText(string, bool)！
    // 在 Harmony + IL2CPP 下，无论 Prefix 还是 Postfix，patch SetText 都会让
    // il2cpp trampoline 自我递归 dispatch（SetText → SetText …）→ Stack Overflow。
    // 底部导航等走 SetText 的界面，改由 RefreshVisibleText() 周期扫描 + m_text 直写翻译。
    // ──────────────────────────────────────────────────

    /// <summary>
    /// 周期性扫描当前场景的 TMP 文本，翻译未命中条目。
    /// 经 m_text 直写（TmpTextHelper），不调用 SetText，避免递归崩溃。
    /// 仅由 Hotkey.Update 在 Unity 主线程上节流调用。
    /// </summary>
    public static void RefreshVisibleText()
    {
        if (!Config.Translation.Value || _inTranslation)
            return;

        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<TMP_Text>(true);
            foreach (var tmp in all)
            {
                if (tmp == null)
                    continue;

                string s = tmp.text;
                if (string.IsNullOrEmpty(s) || !TextTranslator.HasKana(s))
                    continue;

                string before = s;
                ApplyTranslation(ref s, tmp);
                if (s == before)
                    continue;

                _inTranslation = true;
                try
                {
                    TmpTextHelper.TrySetTextDirect(tmp, s);
                }
                finally
                {
                    _inTranslation = false;
                }
            }
        }
        catch (Exception e)
        {
            Logger.Warn($"RefreshVisibleText failed: {e.Message}");
        }
    }

    // ──────────────────────────────────────────────────
    // 共用处理逻辑
    // [ThreadStatic] 防止 TMP_Text 内部重入导致 Stack Overflow
    // ──────────────────────────────────────────────────

    [ThreadStatic]
    private static bool _inTranslation;

    private static void ApplyTranslation(ref string s, TMP_Text instance = null)
    {
        if (_inTranslation) return;
        _inTranslation = true;
        try
        {
            // ui_texts 精准查表（作者逻辑）
            if (instance != null)
            {
                string uiHit = UiTextTranslator.Translate(instance, s);
                if (!string.Equals(uiHit, s, StringComparison.Ordinal))
                {
                    s = uiHit;
                    return;
                }
            }

            // 角色名字段（由 GameObject 名称精确判定）强制归入 name 类别，
            // 使所有界面的新角色名统一收集到 name，便于补入 names 字典。
            string cat;
            if (IsNovelLetterText(instance))
                cat = MachineTranslationCategoryPolicy.NovelTypewriter;
            else if (IsNameField(instance))
                cat = TextClassifier.Name;
            else if (IsAbilityDescriptionField(instance) || TextClassifier.IsActionSkillDescription(s))
                cat = TranslationPaths.AbilityDescriptions;
            else
                cat = UiContextHelper.ResolveCategory(instance, s)
                    ?? (Config.ClassifyText.Value ? TextClassifier.Classify(s) : "ui_misc");

            s = MachineTranslationCategoryPolicy.IsExcludedFromGenericProcessing(cat)
                ? s
                : TextTranslator.Process(cat, s);
            if (MachineTranslationCategoryPolicy.CanProcess(Config.Translation.Value, cat))
                s = MachineTranslator.Handle(cat, s);
        }
        finally
        {
            _inTranslation = false;
        }
    }

    private static bool IsNovelLetterText(TMP_Text text)
    {
        if (text == null)
            return false;

        try
        {
            return text.GetComponentInParent<NovelLetter>() != null
                || NovelLetterTextIds.Contains(text.GetInstanceID());
        }
        catch { }

        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NovelLetter), nameof(NovelLetter.Iniialize))]
    public static void RegisterNovelLetterText(NovelLetter __instance)
    {
        if (__instance._mainText != null)
            NovelLetterTextIds.Add(__instance._mainText.GetInstanceID());
        if (__instance._rubyText != null)
            NovelLetterTextIds.Add(__instance._rubyText.GetInstanceID());
    }

    /// <summary>
    /// 依 TMP 元件的 GameObject 名称判定是否为「角色名字段」。
    /// 精确规则（避免误判技能名 Set_NameLv/TextSkill、二つ名 Chara1 等）：
    ///   1. 自身名含 "CharaName"（如 TextCharaName）
    ///   2. 自身名等于 "TextName"
    ///   3. 父层名精确等于 "Name"
    /// </summary>
    private static bool IsNameField(TMP_Text tmp)
    {
        if (tmp == null) return false;
        try
        {
            var go = tmp.gameObject;
            if (go == null) return false;

            var self = go.name ?? string.Empty;
            if (self.IndexOf("CharaName", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (string.Equals(self, "TextName", StringComparison.OrdinalIgnoreCase))
                return true;

            var parent = go.transform?.parent;
            if (parent != null && string.Equals(parent.name, "Name", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // 访问 GameObject 失败时按非角色名处理
        }
        return false;
    }

    /// <summary>
    /// 角色详情页技能描述字段（限界突破/技能说明等），强制走 ability_descriptions。
    /// </summary>
    private static bool IsAbilityDescriptionField(TMP_Text tmp)
    {
        if (tmp == null) return false;
        try
        {
            var go = tmp.gameObject;
            if (go == null) return false;

            var self = go.name ?? string.Empty;
            if (ContainsAnyIgnoreCase(self, "SkillDesc", "SkillDescription", "ActionSkill", "ChainSkill", "LimitBreak", "BreakEffect", "TextEffect"))
                return true;
            if (ContainsAnyIgnoreCase(self, "Description") && !ContainsAnyIgnoreCase(self, "Item", "Flavor"))
                return true;

            for (var t = go.transform?.parent; t != null; t = t.parent)
            {
                var n = t.name ?? string.Empty;
                if (ContainsAnyIgnoreCase(
                        n,
                        "LimitBreak",
                        "BreakLimit",
                        "ActionSkill",
                        "ChainSkill",
                        "SkillDetail",
                        "SkillInfo",
                        "CharaDetail",
                        "CharacterDetail",
                        "BreakThrough",
                        "AbilityDetail"))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static bool ContainsAnyIgnoreCase(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    // ──────────────────────────────────────────────────
    // 技能描述格式化器（战斗技能，不走分类器）
    // ──────────────────────────────────────────────────

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SkillTextFormatter), nameof(SkillTextFormatter.CreateActionSkill))]
    public static void OnCreateActionSkill(ref string description)
    {
        description = TextTranslator.Process(TranslationPaths.AbilityDescriptions, description);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SkillTextFormatter), nameof(SkillTextFormatter.CreateChainSkill))]
    public static void OnCreateChainSkill(ref string description)
    {
        description = TextTranslator.Process(TranslationPaths.AbilityDescriptions, description);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SkillTextFormatter), nameof(SkillTextFormatter.CreateAbility))]
    public static void OnCreateAbility(ref string skillText, ref string awakeText)
    {
        skillText = TextTranslator.Process(TranslationPaths.AbilityDescriptions, skillText);
        awakeText = TextTranslator.Process(TranslationPaths.AbilityDescriptions, awakeText);
    }
}
