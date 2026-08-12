# AbyssMod

> 🎮 ドットアビス 漢化 MOD（社群擴展版）

本 repo 基於原作者 [anosu/AbyssMod](https://github.com/anosu/AbyssMod) v1.0.4，在其劇情翻譯基礎上新增：

- **介面文字翻譯**（道具說明、角色技能、武器效果、通用 UI）
- **啟發式文字分類收集**（細分 9 個子類別）
- **機翻預處理**（可選，調用本地或雲端 LLM 補翻未收錄文字）
- **角色名全介面共用**（強化、編隊等介面共用 `names` 字典）
- **戰鬥掉落 Auto-SL**（Normal、Idle Exploration、Nether；支援稀有度、裝備 ID 與深淵分層策略）

適用於 **Windows 平台 DMM Game Player 端**。

---

## 📋 目錄

- [架構說明](#-架構說明)
- [安裝（Release）](#-安裝release)
- [配置項](#-配置項)
- [Auto-SL（F11）](#-auto-slf11)
- [機翻預處理（可選）](#-機翻預處理可選)
- [快捷鍵](#-快捷鍵)
- [翻譯資料](#-翻譯資料)
- [常見問題](#-常見問題)
- [開發者：編譯與打包](#-開發者編譯與打包)

---

## 🗂 架構說明

本專案分為兩個獨立 repo：

| Repo | 內容 | 說明 |
|------|------|------|
| **[ImoutoHeaven/AbyssModMod](https://github.com/ImoutoHeaven/AbyssModMod)** | 插件本體 C# 原始碼 | 此 repo，含 Release 下載 |
| **[anosu/dotabyss-translation](https://github.com/anosu/dotabyss-translation)** | 簡體中文翻譯 JSON | 啟動時從上游自動下載 |

翻譯資料不包含在 Release 壓縮包內，插件啟動時會依 `AbyssMod.cfg` 的 `CDN` 設定自動下載到：

```
BepInEx/plugins/AbyssMod/cache/translations/
```

其中 `add-on/{category}/` 為本地人工覆蓋，`other/{category}/` 為本地 LLM 快取；兩者不會從遠端同步。

---

## 🚀 安裝（Release）

### 1. 確認遊戲已安裝

確保已透過 DMM Game Player 安裝遊戲，並知道遊戲根目錄（含 `.exe` 的資料夾）。

### 2. 下載 Release

前往 [Releases](https://github.com/ImoutoHeaven/AbyssModMod/releases) 頁面，找到最新版本（綠色 `Latest` 標識），展開 `Assets` 下載對應壓縮包。

> ⚠️ 請下載 `.zip` 壓縮包，**不要**下載 `Source code`（那是原始碼，需要自行編譯）

### 3. 解壓到遊戲根目錄

將壓縮包解壓到遊戲根目錄（與 `.exe` 同層），解壓後結構如下：

```
遊戲根目錄/
├── ドットアビス.exe
├── dotnet/              ← 解壓後新增
├── .doorstop_version
├── changelog.txt
├── winhttp.dll
├── doorstop_config.ini
└── BepInEx/
    ├── core/
    ├── patchers/
    ├── unity-libs/
    └── plugins/AbyssMod/
        ├── AbyssMod.dll
        ├── Utility.dll
        └── fonts/
```

### 4. 首次啟動

正常啟動遊戲。若是第一次安裝 BepInEx，啟動時會顯示一個控制台視窗並自動下載 Unity 補丁，稍等片刻即可。

> ⚠️ 若使用 ACGP 等加速器，控制台可能出現紅色報錯（無法連接 BepInEx 官網），請開啟代理後重試

### 5. 翻譯來源

首次啟動後會自動生成 `BepInEx\config\AbyssMod.cfg`。插件固定使用上游簡體中文翻譯：

```ini
[Translation]
CDN      = https://raw.githubusercontent.com/anosu/dotabyss-translation/refs/heads/main/translations
Language = zh_Hans
```

這兩個設定會由插件強制還原為上游值；翻譯資料將自動下載並套用。

> 🌐 若 GitHub 連線困難，請先確認代理或網路設定；插件不支援自訂 CDN 鏡像。

---

## ⚙️ 配置項

設定檔位於 `BepInEx\config\AbyssMod.cfg`，首次啟動自動生成。

插件會在 Unity 主執行緒每 `0.25` 秒檢查設定檔；變更連續兩次保持穩定後自動呼叫 BepInEx `Reload()`，通常約 `0.25–0.5` 秒生效。這可避免編輯器尚未完成寫入時讀取半份 cfg。F10 仍可手動立即重載作為備援。Auto-SL 的開關、cooldown、Normal 精確目標、Nether 分層策略及白名單會從下一次響應判定開始使用新值。

### `[General]`

| 配置項              | 預設值  | 說明                   |
| ------------------- | ------- | ---------------------- |
| `DynamicMosaic`     | `false` | 是否啟用動態馬賽克     |
| `SoundCaution`      | `false` | 是否彈出音量提醒       |
| `VoiceInterruption` | `false` | 是否啟用語音中斷       |
| `TitleMovie`        | `true`  | 是否播放標題動畫       |
| `BattleSessionProbe` | `false` | 記錄戰鬥 session 的開始、掛起與恢復響應；僅供診斷 |
| `BattleSessionAutoSL` | `false` | F11 Auto-SL 開關；支援 Normal、Idle Exploration 與 Nether，命中目標後才初始化戰鬥模型 |
| `BattleSessionAutoSLCooldown` | `4.0` | 重投鏈中每個後續 API 請求前的冷卻秒數；必須大於或等於 `0`，不建議低於預設值 |

### `[BattleSessionAutoSL.Targets]`

#### Normal / Idle Exploration 目標

| 配置項 | 預設值 | 說明 |
| ------ | ------ | ---- |
| `NormalStopMode` | `IsRare` | 一般稀有度截止模式；可選值見下表 |
| `NormalMinimumRarity` | `Gold` | `NormalStopMode` 包含 `Rarity` 時要求的最低 `rarity_level` |
| `NormalContentTypes` | `Any` | 一般目標的種類篩選：`Any`、`Weapon`、`Armor`、`Accessory`，可用英文逗號組合 |
| `NormalExactTargets` | 空 | 精確或同族裝備目標；非空時改用嚴格 TargetOnly 規則 |

`is_rare_drop` 與 `rarity_level` 是兩個獨立訊號：

| `NormalStopMode` | 接受條件 |
| ---------------- | -------- |
| `IsRare` | 只接受 `is_rare_drop=true`，忽略 `NormalMinimumRarity` |
| `Rarity` | 只接受 `rarity_level >= NormalMinimumRarity` |
| `IsRareOrRarity` | 上述任一條件成立 |
| `IsRareAndRarity` | 上述兩個條件同時成立 |

rarity 可選值依序為：`NoEffect(0)`、`Silver(1)`、`Purple(2)`、`Gold(3)`、`Red(4)`、`UniqueWeapon(5)`。

`NormalContentTypes` 在一般 StopMode 命中後才篩選種類。`Weapon`、`Armor`、`Accessory` 分別對應遊戲 `content_type=70/80/90`；例如 `Weapon, Armor` 表示只接受武器或防具。請在 cfg 填名稱，不要填枚舉內部的組合掩碼。

`NormalExactTargets` 支援以下格式，多個目標用英文逗號分隔，任意一個命中即停止：

- `Weapon:<MasterDataId>`：指定武器。
- `Armor:<MasterDataId>`：指定防具。
- `Accessory:<MasterDataId>`：指定飾品。
- ID 尾端加 `+`：接受同 `group_no`、同 MasterData rarity 且 Rank 不低於錨點的同族裝備，例如 `Armor:23010440+`。

> ⚠️ **優先級：只要 `NormalExactTargets` 非空，便啟用嚴格 TargetOnly。`NormalStopMode`、`NormalMinimumRarity` 與 `NormalContentTypes` 都不會擴大或否決結果。** 清空 `NormalExactTargets` 才會恢復一般稀有度規則。

這裡填的是武器／防具／飾品的 **MasterData ID**，不是每次掉落的 `sid`，也不是已持有裝備的 `t_weapon_id`、`t_armor_id` 或 `t_accessory_id`。在 Normal 或 Idle Exploration 的關卡掉落預覽中點開裝備詳情，再按 F6，Toast 會顯示可直接貼入 cfg 的推薦 token 與同族 Rank/ID；BepInEx 控制台同時記錄精確 token 和 `+` token。

例如只刷森林披風及同族更高 Rank：

```ini
[BattleSessionAutoSL.Targets]
NormalExactTargets = Armor:23010440+
```

若只想用一般稀有度與種類條件：

```ini
[BattleSessionAutoSL.Targets]
NormalExactTargets =
NormalStopMode = IsRareOrRarity
NormalMinimumRarity = Red
NormalContentTypes = Weapon, Armor
```

#### Nether 分層策略

Nether 採用分層策略，不使用單一全局截止條件；普通戰、強敵與 Boss 各自使用一條策略。

| 配置項 | 預設值 | 說明 |
| ------ | ------ | ---- |
| `NetherBattleStrategy` | `1-49=Off;50-*=Gold` | 普通戰（Battle）的樓層策略 |
| `NetherMiniBossStrategy` | `1-49=Off;50-*=Gold` | 強敵（MiniBoss）的樓層策略 |
| `NetherBossStrategy` | `1-49=Off;50-99=Gold;100-*=Red` | 每段 Boss 的樓層策略 |
| `NetherEquipmentOnly` | `true` | 只接受經 `MItems` 驗證為 Nether 裝備袋（type 91）的目標 |
| `NetherPreserveMode` | `AND` | 白名單與裝備目標的組合方式：`AND` 或 `OR` |
| `NetherPreserveItemIds` | 空 | type 90 物品 ID 白名單；空值表示停用保留分支 |

策略語法為 `selector=target`，多段以分號分隔：

- `selector` 支援單層 `N`、閉區間 `N-M`、向後開放區間 `N-*`、全部樓層 `*`，以及逗號清單。
- `target` 支援 `Off`、`NoEffect`、`Silver`、`Purple`、`Gold`、`Red`、`UniqueWeapon`。
- `NoEffect` 至 `Red` 表示最低門檻（該等級或更好）；`UniqueWeapon` 只接受 `rarity_level=5` 的精確匹配。
- 同一樓層匹配多段時，**最後一段生效**。例如 `*=Gold;100,110,120,130=Red` 會在全部樓層刷金袋，但指定 Boss 樓層改刷紅袋。
- `Off` 表示該樓層／遭遇完全跳過 SL，直接放行當前響應；同時略過裝備袋與白名單判定。
- 無效或沒有匹配樓層的策略會 fail-open，記錄錯誤後放行，避免卡死。

預設值採保守策略：1–49 層全部不刷；50 層起普通戰與強敵刷 Gold；Boss 在 50–99 層刷 Gold，100 層起刷 Red。若要讓 50 層前也全部刷 Gold，可改為：

```ini
[BattleSessionAutoSL.Targets]
NetherBattleStrategy = *=Gold
NetherMiniBossStrategy = *=Gold
NetherBossStrategy = *=Gold;100-*=Red
```

Nether 金袋通常是 `rarity_level=Gold(3)`，但 `is_rare_drop` 仍可能為 `false`，因此 Nether 策略直接以袋子 `rarity_level` 判定。`NetherEquipmentOnly=true` 時只接受 `MItems.type=91` 的裝備袋，Gold/Red 候選還要求主資料 rarity 與掉落 rarity 一致；設為 `false` 才會允許其他敵人掉落按策略稀有度命中。

#### Nether 保留物品

`NetherPreserveItemIds` 接受逗號、分號或空白分隔的十進制 ID，且不使用掉落的 `is_rare_drop` / `rarity_level`：

- `200001`：Lost Signal「深淵」（戰敗時也可帶回已獲得物品）
- `200002`：Gate Key「深淵」（深淵入場道具）
- `200003`：被侵蝕的齒輪（深部調查素材）
- `200004`：侵蝕方塊（深部調查素材）
- `200005`：被侵蝕的寶石（深部調查素材）
- `200006`：被侵蝕的結晶（深部調查素材）

`NetherPreserveMode = AND` 要求同一次響應同時包含裝備目標和至少一個白名單物品；`OR` 接受任一類。白名單留空時保留分支停用，組合模式不生效，仍只按裝備策略判斷。

例如保留全部深部調查素材：`NetherPreserveItemIds = 200003,200004,200005,200006`。白名單只識別 `content_type=31` 且 `MItems.type=90` 的敵人掉落；無效 ID、非 type 90 ID 或主資料缺失會記錄 `accept-error` 並放行當前響應。

### `[Translation]`

| 配置項     | 可選值                                                         | 預設值       | 說明                                         |
| ---------- | -------------------------------------------------------------- | ------------ | -------------------------------------------- |
| `Enabled`  | `true` / `false`                                               | `true`       | 是否開啟遊戲內翻譯                           |
| `CDN`      | 上游固定 URL                                                    | an osu 上游  | 自動強制為 an osu 翻譯資料來源               |
| `Language` | `zh_Hans`                                                       | `zh_Hans`    | 上游僅發布簡體中文，機翻也固定輸出簡體       |

### `[Translation.Font]`

| 配置項            | 預設值                     | 說明                                        |
| ----------------- | -------------------------- | ------------------------------------------- |
| `AssetBundlePath` | `AbyssMod/fonts/ttcuyuanj` | TMP 字體 AssetBundle 路徑（相對或絕對路徑） |

### `[Collector]`

開啟後，遊戲中出現的未翻日文原文會按類別寫入 `BepInEx\plugins\AbyssMod\dump\`，格式為 `{ "日文原文": "" }`，供後續翻譯使用。

| 配置項         | 預設值  | 說明                                                                       |
| -------------- | ------- | -------------------------------------------------------------------------- |
| `CollectText`  | `true`  | 是否收集未翻原文到 `dump/` 目錄。默認開啟，遊玩即持續為社群貢獻覆蓋         |
| `ClassifyText` | `true`  | 是否啟用啟發式分類器，將通用 UI 文字歸入細分子類別，關閉時全部歸入 `ui_misc` |

生成的 dump 檔案：

| 檔案 | 內容 |
|------|------|
| `equipment_effect_raw.json` | 裝備效果 / 被動（含 紋章 / 會心率 / 連擊率 等） |
| `facility_raw.json` | 設施 / 酒館建設 / 升級 |
| `bar_raw.json` | 酒館營業系統（員工 / 滿意度 / 服裝） |
| `mission_raw.json` | 任務目標句 |
| `materials_raw.json` | 素材 / 貨幣 / 結晶 |
| `abyss_code_raw.json` | 深淵代碼系統 |
| `dialogue_raw.json` | NPC 情感台詞 |
| `system_raw.json` | 系統短文字（按鈕 / 標籤等） |
| `ui_misc_raw.json` | 其餘通用文字 |
| `name_raw.json` | 新角色名（未在 `names` 字典中的，機翻不處理，需人工翻譯後補入） |
| `items_raw.json` | 道具說明（精確鉤子，不經分類器） |

### `[MachineTranslation]`

| 配置項           | 可選值                                            | 預設值                                            | 說明                                                         |
| ---------------- | ------------------------------------------------- | ------------------------------------------------- | ------------------------------------------------------------ |
| `Enabled`        | `true` / `false`                                  | `false`                                           | 是否啟用 LLM 機翻（詳見下方說明）                            |
| `Engine`         | `openai` / `claude` / `sugoi` / `libre`           | `openai`                                          | 翻譯引擎（`openai` 相容 Ollama、DeepSeek、OpenAI 等）        |
| `Endpoint`       | 任意有效 API 地址                                 | `http://127.0.0.1:11434/v1/chat/completions`      | 翻譯服務 API 地址                                            |
| `Model`          | 模型名稱                                          | `qwen2.5:3b`                                      | `openai` / `ollama` 引擎使用的模型名稱                       |
| `ApiKey`         | API 金鑰字串                                      | （空）                                            | 雲端服務需填入（OpenAI / DeepSeek / Claude API Key）         |
| `TimeoutSeconds` | 整數                                              | `30`                                              | 單次翻譯請求超時秒數，雲端 API 建議調高至 60                 |
| `llmRequestPerSecond` | 整數                                         | `2`                                               | 每秒最多向 LLM Endpoint 發起的請求數                        |
| `llmRequestMaxInFlight` | 整數                                      | `10`                                              | 同時等待 LLM 回應的請求上限                                  |
| `llmTranslatePeriod` | 整數（秒）                                    | `30`                                              | 待翻隊列的週期清理及週期重試間隔                             |
| `llmRetryCount` | 整數                                              | `3`                                               | 失敗後的快速低優先級重試次數；耗盡後只走週期重試             |

---

## 🎲 Auto-SL（F11）

Auto-SL 在遊戲取得開戰響應後、建立戰鬥模型前攔截結果。未命中目標時按模式重開 session；命中後才把最終響應交回原生流程並進入戰鬥。它只負責**進戰前重投掉落**，不會自動戰鬥、跳過戰鬥或保證關卡掉落池中不存在的物品。

### 支援模式與請求時序

| 模式 | 未命中時的請求順序 | 說明 |
| ---- | ------------------ | ---- |
| Normal / Disaster | `start response → cooldown → start` | 主線、活動等使用一般 Exploration／Disaster 戰鬥 session 的關卡 |
| Idle Exploration | `start response → cooldown → close/lose → cooldown → start` | 探索任務不能在仍開啟的 session 上直接再次 start，必須先走原生 close/lose 流程 |
| Nether | `start response → cooldown → start` | 只處理深淵內的 Battle、MiniBoss 與 Boss 掉落；目標由三條分層策略決定 |

`BattleSessionAutoSLCooldown` 作用於**每兩個 API 請求之間**。因此預設 `4.0` 秒時，Normal／Nether 每輪等待一次；Idle Exploration 每輪會在 close 前和重新 start 前各等待一次。這是為了避免請求過密與伺服器 rate limit，不建議為追求速度而設成 `0`。

Idle Exploration 的關閉行為也會維持 session 完整性：若在 close 前關閉 F11，插件直接放行仍開啟的當前響應；若 close 已成功，插件會先等待 cooldown 並補一次 start，恢復可進戰的 session 後才停止，不會把客戶端留在已關閉但無響應可用的狀態。

### 使用方式

1. 編輯 `BepInEx\config\AbyssMod.cfg` 的 `[BattleSessionAutoSL.Targets]`。需要指定裝備時，可在 Normal 或 Idle Exploration 關卡掉落預覽中打開裝備詳情並按 F6 取得 token。
2. 在出擊或恢復戰鬥前按 F11；控制台出現 `Battle session auto-SL ON` 代表已啟用。也可把 `[General] BattleSessionAutoSL` 設為 `true`。
3. 插件會把戰鬥卡在模型初始化前反覆判定。看到 `decision=retry` 表示未命中；`decision=accept-target` 表示已命中並將進入戰鬥。
4. 想停止時再按一次 F11。插件會在安全邊界放行當前或恢復後的響應，不會中途提交撤退結算。

### Normal / Idle Exploration 的掉落與結算

開戰響應的 `stage_detail` 可能同時列出互斥路線、未啟用寶箱或不同 treasure Rank 的候選掉落。Auto-SL 不會把這整份列表一律當成可取得物品：

- 一般判定會排除當前路線不可達的 inactive fork 掉落。
- 若已接受目標位於當前可達的 `BoxGold` 寶箱，只有實際通過該樓層後，才把該寶箱由伺服器下發的完整 drops 併入原生結算 payload。
- 若已接受目標位於 treasure battle 的 Rank 列表，同一樓層只選一個含目標的 Rank：先比較命中目標數，再選較高 Rank；實際通過該樓層後才併入該 Rank 的完整 drops。
- 插件不混合互斥 Rank，也不憑空生成 item ID；補入內容必須來自本次已接受的伺服器開戰響應，並與原生客戶端可能生成的結算格式一致。

這套結算補全同時用於 Normal 與 Idle Exploration，避免開戰響應已命中精確裝備，但原生客戶端只提交該分支部分 `drop_items` 而導致結算缺件。

### 安全策略與排障

無效 cfg、無法解析的 MasterData ID、未知 Nether 樓層類型或掉落資料不完整時，插件採 fail-open：依情況記錄 `accept-error` 或 `accept-off` 後放行當前響應，避免無限重投或永久卡在載入畫面。

排查時查看 `BepInEx\LogOutput.log`：

- `[F11][BattleAutoSL]`：Normal、Disaster 與 Idle Exploration 的每次判定、目標與 close/start 時序。
- `[F11][NetherAutoSL]`：Nether 遭遇分類、樓層策略、裝備袋／白名單判定。
- `[F11][SettlementProbe]`：最終接受響應與 clear payload 的關聯，以及寶箱／Rank drops 的補全結果。
- `[F6][EquipmentTarget]`：關卡預覽裝備的 MasterData ID、精確 token 與同族 `+` token。

提交問題時請保留從按下 F11、重投、進戰到結算完成的完整連續日誌；只截取最後一行通常無法判斷是哪個 session 或分支。

---

## 🤖 機翻預處理（可選）

對尚未收錄到翻譯資料的文字，可啟用機翻預處理，讓插件**自動補翻**。

工作方式（事件優先，背景執行，不阻塞遊戲）：

1. 遊戲運行中，僅含假名的未命中日文才會按數字模板去重並加入待翻隊列；已譯文字不會再成為翻譯候選。
2. 新出現的文字會進入高優先級 FIFO，立即由背景調度器嘗試翻譯；啟動遺留項目與週期清理項目走一般 FIFO。每連續處理 4 項高優先級文字後，會處理 1 項一般項目，避免週期重試飢餓。
3. 請求啟動頻率受 `llmRequestPerSecond` 限制，同時等待回應的數量受 `llmRequestMaxInFlight` 限制。
4. 請求失敗後會快速低優先級重試 `llmRetryCount` 次；耗盡後只在每次 `llmTranslatePeriod` 清理時重試。
5. `<...>` 標籤、`{0}` 格式參數及實際/轉義換行會轉成受驗證的 `__ABYSS_TOKEN_n__`；模型未完整保留時不寫入快取並視為失敗。
6. 成功結果按類別寫入 `translations/other/{類別}/`，畫面下一次文字刷新命中快取後即替換為中文。

機翻固定輸出簡體中文，以便與上游 `zh_Hans` 人工譯文一致。

> ⚠️ 機翻品質不及人工校對。角色名（`name_raw.json`）不走機翻，需人工翻譯後補入 `names/` 字典。

### 方案 A：本地 Ollama（免費，需要電腦資源）

1. 安裝 [Ollama](https://ollama.com)（安裝後自動常駐於 `127.0.0.1:11434`）
2. 拉取模型：

   ```bash
   ollama pull qwen2.5:3b
   ```

3. 編輯 `AbyssMod.cfg`：

   ```ini
   [MachineTranslation]
   Enabled  = true
   Engine   = openai
   Endpoint = http://127.0.0.1:11434/v1/chat/completions
   Model    = qwen2.5:3b
   ```

4. 啟動遊戲即可。想要更好品質可改用 `qwen2.5:7b`。

### 方案 B：DeepSeek API（雲端，品質較佳，付費計量）

1. 至 [DeepSeek 官網](https://platform.deepseek.com) 取得 API Key
2. 編輯 `AbyssMod.cfg`：

   ```ini
   [MachineTranslation]
   Enabled         = true
   Engine          = openai
   Endpoint        = https://api.deepseek.com/v1/chat/completions
   Model           = deepseek-v4-flash
   ApiKey          = sk-你的DeepSeek金鑰
   TimeoutSeconds  = 60
   ```

### 方案 C：Claude API

1. 至 [Anthropic 官網](https://console.anthropic.com) 取得 API Key
2. 編輯 `AbyssMod.cfg`：

   ```ini
   [MachineTranslation]
   Enabled  = true
   Engine   = claude
   ApiKey   = sk-ant-你的Claude金鑰
   Model    = claude-haiku-4-5
   ```

---

## ⌨️ 快捷鍵

| 快捷鍵 | 功能              |
| ------ | ----------------- |
| `F8`   | 開啟 / 關閉劇情翻譯 |
| `F9`   | 開啟 / 關閉語音中斷 |
| `F10`  | 熱重載配置檔案    |
| `F6`   | 在 Normal／Idle Exploration 關卡掉落預覽的裝備詳情中，顯示 MasterData ID 與 `NormalExactTargets` token |
| `F11`  | 開啟 / 關閉 Normal、Idle Exploration、Nether 進戰前 Auto-SL；關閉時在安全邊界放行響應 |

---

## 📦 翻譯資料

翻譯 JSON 存放於獨立 repo：

**[anosu/dotabyss-translation](https://github.com/anosu/dotabyss-translation)**

目錄結構：

```
translations/
├── manifest/zh_Hans.json  資源雜湊清單
├── static/zh_Hans.json    MasterData 欄位翻譯合併包
├── names/zh_Hans.json     角色名
├── ui_texts/zh_Hans.json  UI 文字
└── novels/{id}/zh_Hans.json  劇情對話
```

本地人工覆蓋存放於 `cache/translations/add-on/{category}/zh_Hans.json`；LLM 快取存放於 `cache/translations/other/{category}/zh_Hans.json`。兩者不會上傳或從遠端同步。

---

## ❓ 常見問題

<details>
<summary><b>啟動時控制台出現紅色報錯</b></summary>
通常是 BepInEx 無法連接其官網下載 Unity 補丁，請開啟代理 / 梯子後重啟遊戲。也可能是初始化檔案因網路波動損壞，此時可嘗試刪除 Mod 資料夾後重新安裝。
</details>

<details>
<summary><b>如何隱藏控制台視窗</b></summary>
編輯 <code>BepInEx\config\BepInEx.cfg</code>，找到 <code>[Logging.Console]</code>，將 <code>Enabled</code> 設為 <code>false</code>。
</details>

<details>
<summary><b>無法連接 GitHub 下載翻譯</b></summary>
<ul>
  <li>請確認 GitHub 可正常連線；插件固定使用 an osu 上游翻譯資料。</li>
</ul>
</details>

<details>
<summary><b>為何機翻輸出簡體中文</b></summary>
插件固定使用 an osu 上游發布的 <code>zh_Hans</code> 翻譯資料，因此 LLM 快取也固定輸出簡體中文。
</details>

<details>
<summary><b>crash / 崩潰怎麼排查</b></summary>
查看 <code>BepInEx\ErrorLog.log</code> 與 <code>BepInEx\LogOutput.log</code>，搜尋 <code>Exception</code> 或 <code>Stack overflow</code>，然後在 <a href="https://github.com/ImoutoHeaven/AbyssModMod/issues">Issues</a> 附上 log 回報。
</details>

---

## 🛠 開發者：編譯與打包

### 環境需求

- .NET 6.0 SDK
- 遊戲本體安裝完成（需要 `BepInEx/interop/*.dll` 與 `Utility.dll`）

### 編譯

1. 設定環境變數（或直接修改 `AbyssMod.csproj` 中的備選 `GameDir`，**不要提交**）：

   ```powershell
   $env:ABYSS_GAME_DIR = "D:\Games\ドットアビス"
   ```

2. 執行 build：

   ```bash
   cd AbyssMod-main
   dotnet build -c Release
   ```

   輸出 DLL 位於 `$ABYSS_GAME_DIR/BepInEx/plugins/AbyssMod/Release/net6.0/AbyssMod.dll`

### 打包 Release

打包 `AbyssMod-v1.0.8.zip`，解壓到遊戲根目錄（與 `.exe` 同層），應包含：

```
dotnet/
.doorstop_version
changelog.txt
doorstop_config.ini
winhttp.dll
BepInEx/
    core/
    patchers/
    unity-libs/
    plugins/AbyssMod/
        AbyssMod.dll
        Utility.dll
        fonts/
```

**應排除**：

- `BepInEx/config/`（含 ApiKey，勿外洩）
- `BepInEx/interop/`、`BepInEx/cache/`
- `BepInEx/plugins/AbyssMod/cache/`（翻譯由 CDN 提供）
- `BepInEx/plugins/AbyssMod/dump/`、`Release/`
- `BepInEx/*.log`

發布流程：

```bash
git tag v1.0.8
git push origin v1.0.8
# 在 GitHub Releases 建立 Release，上傳 AbyssMod-v1.0.8.zip
```

---

> 本 fork 基於 [anosu/AbyssMod](https://github.com/anosu/AbyssMod)，感謝原作者的劇情翻譯框架。
