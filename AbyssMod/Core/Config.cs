using AbyssMod.Services;
using BepInEx.Configuration;
using Utility.Toast;

namespace AbyssMod
{
    /// <summary>
    /// 全局配置管理器。
    /// 负责初始化所有配置项并绑定事件监听。
    /// </summary>
    public static class Config
    {
        private const string AutoSLStopModeHelp =
            "StopMode 可选值（判定开战响应 stage_detail.drops）：\n"
            + "- IsRare：仅接受 is_rare_drop=true；不检查 rarity_level。\n"
            + "- Rarity：接受 rarity_level >= 对应 MinimumRarity；不要求 is_rare_drop。\n"
            + "- IsRareOrRarity：上述任一条件成立即停止。\n"
            + "- IsRareAndRarity：上述两个条件同时成立才停止。";

        private const string AutoSLRarityHelp =
            "Rarity/MinimumRarity 等级：\n"
            + "- NoEffect=0\n"
            + "- Silver=1\n"
            + "- Purple=2\n"
            + "- Gold=3\n"
            + "- Red=4\n"
            + "- UniqueWeapon=5";

        private static bool _enforcingUpstreamTranslationPolicy;

#if DEBUG
        #region Debug
        public static ConfigEntry<bool> Offline;
        public static ConfigEntry<string> OfflineAPI;
        public static bool OfflineStartup;
        #endregion
#endif

        #region General
        public static ConfigEntry<bool> DynamicMosaic;
        public static ConfigEntry<bool> SoundCaution;
        public static ConfigEntry<bool> VoiceInterruption;
        public static ConfigEntry<bool> TitleMovie;
        public static ConfigEntry<bool> BattleSessionProbe;
        public static ConfigEntry<bool> BattleSessionAutoSL;
        public static ConfigEntry<float> BattleSessionAutoSLCooldown;
        public static ConfigEntry<BattleSessionAutoSLStopMode> BattleSessionAutoSLNormalStopMode;
        public static ConfigEntry<BattleSessionDropRarity> BattleSessionAutoSLNormalMinimumRarity;
        public static ConfigEntry<BattleSessionNormalContentTypeFilter> BattleSessionAutoSLNormalContentTypes;
        public static ConfigEntry<string> BattleSessionAutoSLNetherBattleStrategy;
        public static ConfigEntry<string> BattleSessionAutoSLNetherMiniBossStrategy;
        public static ConfigEntry<string> BattleSessionAutoSLNetherBossStrategy;
        public static ConfigEntry<bool> BattleSessionAutoSLNetherEquipmentOnly;
        public static ConfigEntry<NetherPreserveMode> BattleSessionAutoSLNetherPreserveMode;
        public static ConfigEntry<string> BattleSessionAutoSLNetherPreserveItemIds;
        internal static ConfigEntry<int> NetherAutoClimbMaxDepth;
        internal static ConfigEntry<int> NetherAutoClimbSoftErosionLimit;
        internal static ConfigEntry<int> NetherAutoClimbMinimumCharacterHpPermille;
        internal static ConfigEntry<NetherCombatLane> NetherAutoClimbCombatLane;
        internal static ConfigEntry<int> NetherAutoClimbCodeReloadReserve;
        internal static ConfigEntry<NetherTreasureMode> NetherAutoClimbTreasureMode;
        internal static ConfigEntry<NetherShopMode> NetherAutoClimbShopMode;
        internal static ConfigEntry<bool> NetherAutoClimbDetailedLogging;
        #endregion

        #region Translation
        public static ConfigEntry<bool> Translation;
        public static ConfigEntry<string> TranslationCDN;
        public static ConfigEntry<string> TranslationLanguage;
        public static ConfigEntry<string> TranslationCryptoTag;
        public static ConfigEntry<string> TranslationCryptoKey;
        #endregion

        #region Font
        public static ConfigEntry<string> FontBundlePath;
        #endregion

        #region Collector
        public static ConfigEntry<string> MTApiKey;
        public static ConfigEntry<bool> CollectText;
        public static ConfigEntry<bool> ClassifyText;
        #endregion

        #region MachineTranslation
        public static ConfigEntry<bool> MTEnabled;
        public static ConfigEntry<string> MTEngine;
        public static ConfigEntry<string> MTEndpoint;
        public static ConfigEntry<string> MTModel;
        public static ConfigEntry<int> MTTimeout;
        public static ConfigEntry<int> MTRequestPerSecond;
        public static ConfigEntry<int> MTRequestMaxInFlight;
        public static ConfigEntry<int> MTTranslatePeriod;
        public static ConfigEntry<int> MTRetryCount;
        #endregion

        /// <summary>
        /// 初始化配置系统。
        /// </summary>
        public static void Initialize()
        {
            BindAllEntries();
            EnforceUpstreamTranslationPolicy();
            BindSettingChangedLog();
        }

        private static void EnforceUpstreamTranslationPolicy()
        {
            if (_enforcingUpstreamTranslationPolicy)
                return;

            var resolved = UpstreamTranslationPolicy.Resolve(
                TranslationCDN.Value,
                TranslationLanguage.Value
            );
            _enforcingUpstreamTranslationPolicy = true;
            try
            {
                if (!string.Equals(TranslationCDN.Value, resolved.Cdn, System.StringComparison.Ordinal))
                    TranslationCDN.Value = resolved.Cdn;
                if (!string.Equals(TranslationLanguage.Value, resolved.Language, System.StringComparison.Ordinal))
                    TranslationLanguage.Value = resolved.Language;
            }
            finally
            {
                _enforcingUpstreamTranslationPolicy = false;
            }
        }

        private static void BindAllEntries()
        {
#if DEBUG
            #region Debug
            Offline = Plugin.ConfigFile.Bind(
                "Debug.Offline",
                "Enabled",
                false,
                "API localization for debug"
            );
            OfflineAPI = Plugin.ConfigFile.Bind(
                "Debug.Offline",
                "CDN",
                "http://localhost:33333/abyss/",
                "CDN for debug"
            );
            #endregion
#endif

            #region General
            DynamicMosaic = Plugin.ConfigFile.Bind(
                "General",
                "DynamicMosaic",
                false,
                "是否启用游戏内动态马赛克"
            );
            SoundCaution = Plugin.ConfigFile.Bind(
                "General",
                "SoundCaution",
                false,
                "是否启用进入游戏时的音量提醒弹窗"
            );
            VoiceInterruption = Plugin.ConfigFile.Bind(
                "General",
                "VoiceInterruption",
                false,
                "剧情中播放下一段无声文本时是否中断当前角色语音"
            );
            TitleMovie = Plugin.ConfigFile.Bind(
                "General",
                "TitleMovie",
                true,
                "是否开启进入游戏时的标题动画"
            );
            BattleSessionProbe = Plugin.ConfigFile.Bind(
                "General",
                "BattleSessionProbe",
                false,
                "F11 战斗 session 探针：记录开始、挂起和多次恢复响应"
            );
            BattleSessionAutoSL = Plugin.ConfigFile.Bind(
                "General",
                "BattleSessionAutoSL",
                false,
                "F11 自动刷目标掉落：在 normal/Nether 战斗模型初始化前持续重投"
            );
            BattleSessionAutoSLCooldown = Plugin.ConfigFile.Bind(
                "General",
                "BattleSessionAutoSLCooldown",
                4.0f,
                "自动重投间隔（秒），必须大于或等于 0"
            );
            BattleSessionAutoSLNormalStopMode = Plugin.ConfigFile.Bind(
                "BattleSessionAutoSL.Targets",
                "NormalStopMode",
                BattleSessionAutoSLStopMode.IsRare,
                "Normal/Disaster 截止条件。默认 IsRare，保持旧版行为。\n"
                    + AutoSLStopModeHelp
            );
            BattleSessionAutoSLNormalMinimumRarity = Plugin.ConfigFile.Bind(
                "BattleSessionAutoSL.Targets",
                "NormalMinimumRarity",
                BattleSessionDropRarity.Gold,
                "NormalStopMode 包含 Rarity 时使用的最低 rarity_level；IsRare 模式会忽略本项。\n"
                    + AutoSLRarityHelp
            );
            BattleSessionAutoSLNormalContentTypes = Plugin.ConfigFile.Bind(
                "BattleSessionAutoSL.Targets",
                "NormalContentTypes",
                BattleSessionNormalContentTypeFilter.Any,
                "Normal/Disaster 截止目标的 content_type 过滤器；在 StopMode 命中后再应用。\n"
                    + "- Any（默认）：不指定类型，保持旧行为；材料、武器、护甲等均可命中。\n"
                    + "- Weapon：只接受武器，游戏 content_type=70。\n"
                    + "- Armor：只接受护甲，游戏 content_type=80。\n"
                    + "- Accessory：只接受护符/饰品，游戏 content_type=90。\n"
                    + "可用英文逗号组合任意类型，例如 Weapon, Armor 或 Weapon, Accessory。\n"
                    + "注意：枚举内部的 1/2/4 是组合掩码，不是游戏 content_type；建议在 cfg 中填写名称。\n"
                    + "非法值会 accept-error 并放行当前响应，避免无限重投。"
            );
            BattleSessionAutoSLNetherBattleStrategy = Plugin.ConfigFile.Bind(
                "BattleSessionAutoSL.Targets",
                "NetherBattleStrategy",
                NetherStrategyDefaults.Battle,
                "Floor-aware strategy grammar: selector=target; selectors support N, N-M, N-*, *, and comma lists. "
                    + "Targets: Off, NoEffect, Silver, Purple, Gold, Red, UniqueWeapon. Ranges are inclusive and the last matching clause wins. "
                    + "Off bypasses drops/equipment/preserve; invalid or unmatched values fail open."
            );
            BattleSessionAutoSLNetherMiniBossStrategy = Plugin.ConfigFile.Bind(
                "BattleSessionAutoSL.Targets",
                "NetherMiniBossStrategy",
                NetherStrategyDefaults.MiniBoss,
                "Same floor-aware grammar as NetherBattleStrategy. The current ConfigEntry.Value is read for every response, so cfg reload applies next response. "
                    + "EquipmentOnly and Preserve AND/OR are global filters used only for non-Off targets."
            );
            BattleSessionAutoSLNetherBossStrategy = Plugin.ConfigFile.Bind(
                "BattleSessionAutoSL.Targets",
                "NetherBossStrategy",
                NetherStrategyDefaults.Boss,
                "Floor-aware strategy grammar: selector=target; selector supports N, N-M, N-*, *, and comma lists. "
                    + "Targets are Off, NoEffect, Silver, Purple, Gold, Red, UniqueWeapon. Ranges are inclusive; the last matching clause wins. "
                    + "Off bypasses drop, equipment, and preserve evaluation. Invalid or unmatched strategies fail open; e.g. *=Gold;100,110=Red."
            );
            BattleSessionAutoSLNetherEquipmentOnly = Plugin.ConfigFile.Bind(
                "BattleSessionAutoSL.Targets",
                "NetherEquipmentOnly",
                true,
                "Nether 主数据交叉验证：\n"
                    + "- true（默认）：只接受 MItems.type=91 的 NetherEquipment 装备袋，"
                    + "Gold/Red 候选还要求 MItems.rarity == 掉落 rarity_level。\n"
                    + "- false：跳过装备袋分类，任意敌人掉落均可按所选策略目标命中。\n"
                    + "普通袋实测可能是掉落 rarity_level=0、MItems.rarity=1/2，"
                    + "所以未命中所选策略目标前不会因二者不同而报错。\n"
                    + "NetherPreserveItemIds 是独立的 type=90 白名单分支，"
                    + "由 NetherPreserveMode 决定如何与装备目标组合。"
            );
            BattleSessionAutoSLNetherPreserveMode = Plugin.ConfigFile.Bind(
                "BattleSessionAutoSL.Targets",
                "NetherPreserveMode",
                NetherPreserveMode.AND,
                "NetherPreserveItemIds 与装备匹配分支的组合方式：\n"
                    + "- AND（默认）：同一次开战响应必须同时包含装备目标和至少一个白名单物品。\n"
                    + "- OR：出现装备目标或至少一个白名单物品，任一成立即停止重投。\n"
                    + "当 NetherPreserveItemIds 留空时，保留分支禁用，本项不生效，"
                    + "仍只按装备匹配分支判断。"
            );
            BattleSessionAutoSLNetherPreserveItemIds = Plugin.ConfigFile.Bind(
                "BattleSessionAutoSL.Targets",
                "NetherPreserveItemIds",
                string.Empty,
                "Nether MItems.type=90 物品保留白名单；填写十进制 item ID，"
                    + "多个 ID 用逗号、分号或空白分隔。默认留空表示禁用。\n"
                    + "只检查 enemies[*].drops；与装备匹配分支的 AND/OR 组合"
                    + "由 NetherPreserveMode 控制。\n"
                    + "白名单分支只认 content_type=31 且 MItems.type=90，"
                    + "不检查 is_rare_drop、rarity_level，也不要求 MItems.rarity 与掉落 rarity 一致。\n"
                    + "当前可配置 ID：\n"
                    + "- 200001 = Lost Signal「深渊」：战败时也可带回已获得物品\n"
                    + "- 200002 = Gate Key「深渊」：深渊入场道具\n"
                    + "- 200003 = 被侵蚀的齿轮：深部调查素材\n"
                    + "- 200004 = 侵蚀方块：深部调查素材\n"
                    + "- 200005 = 被侵蚀的宝石：深部调查素材\n"
                    + "- 200006 = 被侵蚀的结晶：深部调查素材\n"
                    + "示例（保留全部深部调查素材）：200003,200004,200005,200006\n"
                    + "无效 ID、非 type=90 ID 或主数据缺失会 accept-error 并放行，避免卡死。"
            );

            #region NetherAutoClimb
            NetherAutoClimbMaxDepth = Plugin.ConfigFile.Bind(
                "NetherAutoClimb",
                "MaxDepth",
                130,
                "F12 深渊自动爬塔最大目标层，默认 130；实际目标始终取配置、服务器与主数据上限的最小值。"
                    + "只会在 Sleep 十层结算点继续或正常结束，非 Sleep 达到目标会暂停，绝不取消或提交 Result。"
            );
            NetherAutoClimbSoftErosionLimit = Plugin.ConfigFile.Bind(
                "NetherAutoClimb",
                "SoftErosionLimit",
                90,
                "侵蚀软上限（1–99），默认 90；任何预测达到软上限的非强制路径暂停，100 是绝对硬停止。"
                    + "事件/战斗效果、顺序或主数据无法证明时也会暂停，不会猜测。"
            );
            NetherAutoClimbMinimumCharacterHpPermille = Plugin.ConfigFile.Bind(
                "NetherAutoClimb",
                "MinimumCharacterHpPermille",
                300,
                "最小活动角色 HP 千分比（1–1000），默认 300；预判会致死或低于安全条件的选项不会自动选择。"
            );
            NetherAutoClimbCombatLane = Plugin.ConfigFile.Bind(
                "NetherAutoClimb",
                "CombatLane",
                NetherCombatLane.Auto,
                "深渊 Code 战斗流派：Auto、Rush 或 Impact。默认 Auto 只在已证明覆盖时锁定单一流派。"
                    + "优先 30024 与 effective Safe>=5；40024/Risk 被硬拒绝，Safe/Risk、Rush/Impact 按游戏效果相互抵消。"
            );
            NetherAutoClimbCodeReloadReserve = Plugin.ConfigFile.Bind(
                "NetherAutoClimb",
                "CodeReloadReserve",
                1,
                "Code 重抽保留次数，默认 1；Safe 尚不足且剩余次数严格大于该值时才走原生 Reroll 控制器流程。"
            );
            NetherAutoClimbTreasureMode = Plugin.ConfigFile.Bind(
                "NetherAutoClimb",
                "TreasureMode",
                NetherTreasureMode.KeyOnly,
                "宝箱策略：KeyOnly（默认）仅接受已验证恰好消耗一把钥匙的安全选项；Off 会暂停而非猜测关闭弹窗。"
            );
            NetherAutoClimbShopMode = Plugin.ConfigFile.Bind(
                "NetherAutoClimb",
                "ShopMode",
                NetherShopMode.Off,
                "商店策略：Off（默认）只可走已验证原生关闭回调；EquipmentBags 仅在已知 type=91、Gold 以上且深渊金币充足时购买。"
            );
            NetherAutoClimbDetailedLogging = Plugin.ConfigFile.Bind(
                "NetherAutoClimb",
                "DetailedLogging",
                true,
                "记录 [F12][NetherClimb] 的状态转换、服务器快照、候选审计、选项与预测；不记录每帧轮询。"
                    + "F12 只在 Nether 内有效，临时强制原生 Auto+最高速度并在关闭/失败/卸载恢复；F11 自动刷本设置独立，F12 仅在其实际 Nether 操作期间等待。"
                    + "Gate Key=200002 仅由原生 Continue 消耗一张；Lost Signal=200001 不会由 F12 自动消耗。"
            );
            #endregion
            #endregion

            #region Translation
            Translation = Plugin.ConfigFile.Bind(
                "Translation",
                "Enabled",
                true,
                "是否开启游戏内剧情翻译"
            );
            TranslationCDN = Plugin.ConfigFile.Bind(
                "Translation",
                "CDN",
                UpstreamTranslationPolicy.Cdn,
                "上游翻译仓库 CDN"
            );
            TranslationLanguage = Plugin.ConfigFile.Bind(
                "Translation",
                "Language",
                UpstreamTranslationPolicy.Language,
                "上游翻译语言（固定为 zh_Hans）"
            );
            TranslationCryptoTag = Plugin.ConfigFile.Bind(
                "Translation.Crypto",
                "Tag",
                "ENC:",
                "翻译文本加密标签（可选）"
            );
            TranslationCryptoKey = Plugin.ConfigFile.Bind(
                "Translation.Crypto",
                "Key",
                "woshitonghuadawang",
                "翻译文本解密密钥（可选）"
            );
            #endregion

            #region Font
            FontBundlePath = Plugin.ConfigFile.Bind(
                "Translation.Font",
                "AssetBundlePath",
                $"{MyPluginInfo.PLUGIN_GUID}/fonts/ttcuyuanj",
                "TMP字体AssetBundle的路径，默认相对于插件目录，也可使用绝对路径"
            );
            #endregion

            #region Collector
            MTApiKey = Plugin.ConfigFile.Bind(
                "MachineTranslation",
                "ApiKey",
                "",
                "API 密钥（Engine=claude 时填入 Anthropic API Key；Engine=openai 且使用云端 OpenAI 时填入 OpenAI API Key）。Ollama 等本地服务留空即可"
            );
            CollectText = Plugin.ConfigFile.Bind(
                "Collector",
                "CollectText",
                true,
                "是否收集游戏内出现的原文（道具说明等）到 dump 目录，用于建立翻译数据。默认开启，写盘开销极小，可持续为社区贡献覆盖"
            );
            ClassifyText = Plugin.ConfigFile.Bind(
                "Collector",
                "ClassifyText",
                true,
                "是否启用启发式文本分类器，将通用 UI 文本自动归入 equipment_effect/facility/bar/mission/materials/abyss_code/dialogue/system/ui_misc 子类别，便于分类校对。关闭时全部归入 ui_misc"
            );
            #endregion

            #region MachineTranslation
            MTEnabled = Plugin.ConfigFile.Bind(
                "MachineTranslation",
                "Enabled",
                false,
                "是否启用机翻预处理：平时收集字典未命中的日文，启动时后台批量调用本地翻译引擎翻译并缓存（非实时，需自行运行翻译服务，如 ollama）"
            );
            MTEngine = Plugin.ConfigFile.Bind(
                "MachineTranslation",
                "Engine",
                "openai",
                "翻译引擎类型，可选：openai（OpenAI兼容，如 LM Studio）、ollama、sugoi、libre"
            );
            MTEndpoint = Plugin.ConfigFile.Bind(
                "MachineTranslation",
                "Endpoint",
                "http://127.0.0.1:11434/v1/chat/completions",
                "本地翻译服务的完整 API 地址。ollama(OpenAI兼容)默认 http://127.0.0.1:11434/v1/chat/completions；sugoi 通常 http://127.0.0.1:14366/；libre 通常 http://127.0.0.1:5000/translate"
            );
            MTModel = Plugin.ConfigFile.Bind(
                "MachineTranslation",
                "Model",
                "qwen2.5:3b",
                "模型名称（openai/ollama 引擎使用），如 qwen2.5:3b（质量更好可换 qwen2.5:7b）。sugoi/libre 可留空"
            );
            MTTimeout = Plugin.ConfigFile.Bind(
                "MachineTranslation",
                "TimeoutSeconds",
                30,
                "单次翻译请求超时秒数"
            );
            MTRequestPerSecond = Plugin.ConfigFile.Bind(
                "MachineTranslation",
                "llmRequestPerSecond",
                2,
                "每秒最多向 LLM Endpoint 发起的请求数"
            );
            MTRequestMaxInFlight = Plugin.ConfigFile.Bind(
                "MachineTranslation",
                "llmRequestMaxInFlight",
                10,
                "同时等待 LLM 响应的请求上限"
            );
            MTTranslatePeriod = Plugin.ConfigFile.Bind(
                "MachineTranslation",
                "llmTranslatePeriod",
                30,
                "待翻译队列的周期清理和重试间隔（秒）"
            );
            MTRetryCount = Plugin.ConfigFile.Bind(
                "MachineTranslation",
                "llmRetryCount",
                3,
                "请求失败后的快速低优先级重试次数；超过后只在周期清理时重试"
            );
            #endregion
        }

        /// <summary>
        /// 绑定配置变更日志输出。
        /// </summary>
        private static void BindSettingChangedLog()
        {
            Plugin.ConfigFile.SettingChanged += (_, e) =>
            {
                var c = e.ChangedSetting;
                Plugin.Log.LogInfo(
                    $"[{c.Definition.Section}] {c.Definition.Key} => {c.BoxedValue}"
                );
                Toast.Info($"[{c.Definition.Section}]", $"{c.Definition.Key} => {c.BoxedValue}");

                if (c == TranslationCDN || c == TranslationLanguage)
                    EnforceUpstreamTranslationPolicy();
            };
        }
    }
}
