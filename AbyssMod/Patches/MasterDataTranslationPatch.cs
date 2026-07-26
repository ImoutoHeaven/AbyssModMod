using System.Collections.Generic;
using AbyssMod.Services;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSystem;
using Project.Master;

namespace AbyssMod.Patches;

[HarmonyPatch]
public static class MasterDataTranslationPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MasterDataStore), "_DownloadFirstAsync_b__8_0")]
    public static void TranslateBeforeCache(Il2CppSystem.Type elementType, Object rowsArray)
    {
        if (!Config.Translation.Value || Plugin.Trans == null || rowsArray == null)
            return;

        try
        {
            Plugin.Trans.EnsureStaticTranslationsLoaded();
            string tableName = elementType?.Name;
            if (
                tableName == null
                || !MasterMapping.Tables.TryGetValue(tableName, out TableMapping table)
            )
                return;

            List<System.IntPtr> rows = MasterMapping.GetArrayElements(
                ((Il2CppObjectBase)rowsArray).Pointer
            );
            if (rows == null)
                return;

            int translated = 0;
            foreach (System.IntPtr rowPtr in rows)
            {
                if (rowPtr != System.IntPtr.Zero)
                    translated += TranslateRow(rowPtr, table);
            }

            if (translated > 0)
                Logger.Info($"MasterData translated: {tableName}, fields: {translated}");
        }
        catch (System.Exception ex)
        {
            Logger.Error($"[MasterDataTranslation] threw: {ex}");
        }
    }

    private static int TranslateRow(System.IntPtr rowPtr, TableMapping table)
    {
        int count = 0;
        foreach (FieldEntry field in table.Fields)
        {
            string original = MasterMapping.ReadField(rowPtr, field);
            if (string.IsNullOrEmpty(original))
                continue;

            foreach (FieldRule rule in field.Rules)
            {
                Dictionary<string, string> dict = Plugin.Trans.GetFieldTable(rule.Dict, field.Name);
                if (
                    dict != null
                    && dict.TryGetValue(original, out string translated)
                    && !string.IsNullOrEmpty(translated)
                )
                {
                    MasterMapping.WriteField(
                        rowPtr,
                        field,
                        rule.Seal ? RestoreSealNames(translated) : translated
                    );
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    private static string RestoreSealNames(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        text = text.Replace("纹章：冲击", "紋章：衝撃");
        text = text.Replace("纹章：热情", "紋章：情熱");
        return text;
    }
}
