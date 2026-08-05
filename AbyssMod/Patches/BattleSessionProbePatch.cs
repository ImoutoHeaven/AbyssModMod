using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Absf.Api;
using HarmonyLib;
using UnityEngine.Networking;
using AbyssMod.Services;

namespace AbyssMod.Patches;

/// <summary>
/// F11-only synchronous response probe. It observes the common HTTP response
/// boundary and never replaces a UniTask or response object.
/// </summary>
[HarmonyPatch(typeof(ApiRequestTask<>), "CreateResponseData")]
public static class BattleSessionProbePatch
{
    [HarmonyPostfix]
    private static void Postfix(UnityWebRequest uwr, ApiResponseData __result)
    {
        if (!Config.BattleSessionProbe.Value || uwr == null || __result == null)
            return;

        string url = uwr.url ?? string.Empty;
        if (url.IndexOf("battle-session", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        string json = __result.JsonBody ?? string.Empty;
        Logger.Info(
            $"[F11][BattleProbe][HTTP] url={url}, responseCode={__result.ResponseCode}, "
                + $"jsonLength={json.Length}, jsonSha256={ComputeHash(json)}"
        );

        string stageDetail = ReadStageDetail(json);
        if (stageDetail.Length == 0)
        {
            Logger.Info("[F11][BattleProbe][HTTP] stage_detail=missing");
            return;
        }

        BattleDropProbeReport report = BattleSessionDropProbe.Parse(stageDetail);
        Logger.Info(
            $"[F11][BattleProbe][HTTP] stageDetailLength={stageDetail.Length}, "
                + $"stageDetailSha256={ComputeHash(stageDetail)}, drops={report.DropCount}, "
                + $"rare={report.RareDropCount}, error={report.Error}"
        );
        Logger.Info($"[F11][BattleProbe][HTTP] items={report.FormatItemList()}");
    }

    private static string ReadStageDetail(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return FindStageDetail(document.RootElement);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string FindStageDetail(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (element.TryGetProperty("stage_detail", out JsonElement direct)
            && direct.ValueKind == JsonValueKind.String)
            return direct.GetString() ?? string.Empty;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                string nested = FindStageDetail(property.Value);
                if (nested.Length > 0)
                    return nested;
            }
        }
        return string.Empty;
    }

    private static string ComputeHash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
