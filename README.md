# AbyssMod

> 🎮 ドットアビス 漢化 MOD（社群擴展版）

本 repo 基於原作者 [anosu/AbyssMod](https://github.com/anosu/AbyssMod) v1.0.4，在其劇情翻譯基礎上新增：

- **介面文字翻譯**（道具說明、角色技能、武器效果、通用 UI）
- **啟發式文字分類收集**（細分 9 個子類別）
- **機翻預處理**（可選，調用本地或雲端 LLM 補翻未收錄文字）
- **角色名全介面共用**（強化、編隊等介面共用 `names` 字典）

適用於 **Windows 平台 DMM Game Player 端**。

---

## 📋 目錄

- [架構說明](#-架構說明)
- [安裝（Release）](#-安裝release)
- [配置項](#-配置項)
- [機翻預處理（可選）](#-機翻預處理可選)
- [快捷鍵](#-快捷鍵)
- [翻譯資料](#-翻譯資料)
- [常見問題](#-常見問題)
- [開發者：編譯與打包](#-開發者編譯與打包)
- [社群](#-社群)

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

插件會在 Unity 主執行緒每 `0.25` 秒檢查設定檔；變更連續兩次保持穩定後自動呼叫 BepInEx `Reload()`，通常約 `0.25–0.5` 秒生效。這可避免編輯器尚未完成寫入時讀取半份 cfg。F10 仍可手動立即重載作為備援。Auto-SL 的開關、cooldown、StopMode、rarity、AND/OR 模式和物品白名單會從下一次響應判定開始使用新值。

### `[General]`

| 配置項              | 預設值  | 說明                   |
| ------------------- | ------- | ---------------------- |
| `DynamicMosaic`     | `false` | 是否啟用動態馬賽克     |
| `SoundCaution`      | `false` | 是否彈出音量提醒       |
| `VoiceInterruption` | `false` | 是否啟用語音中斷       |
| `TitleMovie`        | `true`  | 是否播放標題動畫       |
| `BattleSessionAutoSL` | `false` | F11 自動重投 normal/Nether 開戰響應，命中目標後才初始化戰鬥模型 |
| `BattleSessionAutoSLCooldown` | `4.0` | 兩次重投請求之間的冷卻秒數 |

### `[BattleSessionAutoSL.Targets]`

`is_rare_drop` 與 `rarity_level` 是兩個不同訊號。Nether 金袋為 `rarity_level=Gold(3)`，其 `is_rare_drop` 通常仍是 `false`。

| 配置項 | 預設值 | 說明 |
| ------ | ------ | ---- |
| `NormalStopMode` | `IsRare` | Normal/Disaster 截止模式：`IsRare`、`Rarity`、`IsRareOrRarity`、`IsRareAndRarity` |
| `NormalMinimumRarity` | `Gold` | Normal 模式包含 `Rarity` 時的最低掉落等級 |
| `NetherStopMode` | `Rarity` | Nether 每層敵人掉落的截止模式 |
| `NetherMinimumRarity` | `Gold` | `Gold` 表示金袋或更好；也可設為 `Red` 或 `UniqueWeapon` |
| `NetherEquipmentOnly` | `true` | 只接受經 `MItems` 驗證為 Nether 裝備袋（type 91）的目標 |
| `NetherPreserveMode` | `AND` | 白名單與裝備 StopMode 的組合方式：`AND` 或 `OR` |
| `NetherPreserveItemIds` | 空 | type 90 物品 ID 白名單；空值表示停用保留分支 |

rarity 可選值依序為：`NoEffect`、`Silver`、`Purple`、`Gold`、`Red`、`UniqueWeapon`。
`NetherPreserveItemIds` 接受逗號、分號或空白分隔的十進制 ID，且不使用掉落的 `is_rare_drop` / `rarity_level`：

- `200001`：Lost Signal「深淵」（戰敗時也可帶回已獲得物品）
- `200002`：Gate Key「深淵」（深淵入場道具）
- `200003`：被侵蝕的齒輪（深部調查素材）
- `200004`：侵蝕方塊（深部調查素材）
- `200005`：被侵蝕的寶石（深部調查素材）
- `200006`：被侵蝕的結晶（深部調查素材）

`NetherPreserveMode = AND` 要求同一次響應同時包含裝備目標和白名單物品；`OR` 接受任一類。白名單留空時組合模式不生效，仍只按裝備 StopMode 判斷。

例如保留全部深部調查素材：`NetherPreserveItemIds = 200003,200004,200005,200006`。普通 Silver/Purple 袋的開戰響應可能把 `rarity_level` 折疊為 `0`，因此 `MItems.rarity` 精確一致性只檢查 Gold/Red 裝備候選。
若開戰響應無法解析、cfg 枚舉無效或 Nether 主資料交叉驗證失敗，Auto-SL 會記錄 `accept-error` 並放行當前響應，避免永久卡住。

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
| `F11`  | 開啟 / 關閉進戰前 Auto-SL；關閉時放行目前已取得的響應 |

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

## 💬 社群

- 海外詢問：添加 Discord 好友 `.lienchu9420`（Lienchu 恋曲）
- Issues：[GitHub Issues](https://github.com/ImoutoHeaven/AbyssModMod/issues)

---

> 本 fork 基於 [anosu/AbyssMod](https://github.com/anosu/AbyssMod)，感謝原作者的劇情翻譯框架。
