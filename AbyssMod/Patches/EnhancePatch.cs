using System.Collections.Generic;
using Absf;
using AbyssMod.Services;
using HarmonyLib;
using Il2CppSystem.Threading;
using Project.Notice;
using Project.Novel;
using UnityEngine;

namespace AbyssMod.Patches;

/// <summary>
/// 游戏通用增强补丁：帧率修改 + 跳过大招动画。
/// </summary>
[HarmonyPatch]
public static class EnhancePatch
{
    private const float NovelLive2DScaleSaveDelay = 1f;

    private static readonly Dictionary<Transform, Vector3> NovelLive2DOriginalScales = new();
    private static readonly NovelLive2DScaleState NovelLive2DScale = new(
        NovelLive2DScaleSaveDelay
    );
    private static int _allowStopVoiceCount;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NovelLive2DObject), nameof(NovelLive2DObject.Initialize))]
    public static void DisableMosaic(NovelLive2DObject __instance)
    {
        if (Config.DynamicMosaic.Value)
            return;

        var drawables = __instance.GetDrawables();
        if (drawables == null)
            return;

        foreach (var drawable in drawables)
            if (drawable.name.StartsWith("Mosaic"))
                drawable.gameObject.SetActive(false);
    }

    [HarmonyPrefix]
    [HarmonyPatch(
        typeof(SoundCautionPopupController),
        nameof(SoundCautionPopupController.SetupPopupEvent)
    )]
    public static bool DisableSoundCaution(SoundCautionPopupController __instance)
    {
        if (!Config.SoundCaution.Value)
        {
            __instance._onClickOk.Invoke();
            return false;
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelSoundManager), nameof(NovelSoundManager.StopCategory))]
    public static bool CancelStoppingVoice(int nCategory, bool playFade)
    {
        if (Config.VoiceInterruption.Value || _allowStopVoiceCount > 0)
            return true;

        return nCategory != 2 || playFade;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelSoundManager), nameof(NovelSoundManager.PlaySound))]
    public static void StopVoiceBeforePlaying(NovelSoundManager __instance, SoundCategory category)
    {
        if (!Config.VoiceInterruption.Value && category == SoundCategory.Voice)
        {
            _allowStopVoiceCount++;
            try
            {
                __instance.StopCategory(2, false);
            }
            finally
            {
                _allowStopVoiceCount--;
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Project.Title.TopView), nameof(Project.Title.TopView.PlayMovie))]
    public static void DisableTitleMovie(Project.Title.TopView __instance, CancellationToken ct)
    {
        if (!Config.TitleMovie.Value)
        {
            __instance.MovieSkip(ct);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NovelLive2DController), nameof(NovelLive2DController.Setup))]
    public static void BeginNovelLive2DScale(NovelLive2DController __instance)
    {
        var root = __instance?._canvasRoot;
        if (root == null)
            return;

        if (!NovelLive2DOriginalScales.TryGetValue(root, out var originalScale))
        {
            originalScale = root.localScale;
            NovelLive2DOriginalScales[root] = originalScale;
        }
        root.localScale = ScaleNovelLive2D(
            originalScale,
            NovelLive2DScale.Current(Config.NovelLive2DScale.Value)
        );
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NovelLive2DController), nameof(NovelLive2DController.Release))]
    public static void EndNovelLive2DScale(NovelLive2DController __instance)
    {
        var root = __instance?._canvasRoot;
        if (root != null && NovelLive2DOriginalScales.Remove(root, out var originalScale))
            root.localScale = originalScale;
    }

    internal static void UpdateNovelLive2DScale()
    {
        if (NovelLive2DScale.ShouldSave(Time.unscaledTime))
            SaveNovelLive2DScale();

        bool controlPressed =
            Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (NovelLive2DOriginalScales.Count == 0 || !controlPressed)
            return;

        if (
            NovelLive2DScale.TryAdjust(
                Input.mouseScrollDelta.y,
                Time.unscaledTime,
                Config.NovelLive2DScale.Value,
                out _
            )
        )
            ApplyNovelLive2DScale();
    }

    internal static void ReloadNovelLive2DScale()
    {
        if (NovelLive2DScale.Reload(Config.NovelLive2DScale.Value, out _))
            ApplyNovelLive2DScale();
    }

    internal static void FlushNovelLive2DScale()
    {
        if (NovelLive2DScale.ShouldSave(float.PositiveInfinity))
            SaveNovelLive2DScale();
    }

    private static void ApplyNovelLive2DScale()
    {
        float scale = NovelLive2DScale.Current(Config.NovelLive2DScale.Value);
        foreach (var (root, originalScale) in NovelLive2DOriginalScales)
            if (root != null)
                root.localScale = ScaleNovelLive2D(originalScale, scale);
    }

    private static Vector3 ScaleNovelLive2D(Vector3 originalScale, float scale) =>
        new(originalScale.x * scale, originalScale.y * scale, originalScale.z);

    private static void SaveNovelLive2DScale()
    {
        Config.NovelLive2DScale.Value = NovelLive2DScale.Current(
            Config.NovelLive2DScale.Value
        );
        NovelLive2DScale.MarkSaved();
    }
}
