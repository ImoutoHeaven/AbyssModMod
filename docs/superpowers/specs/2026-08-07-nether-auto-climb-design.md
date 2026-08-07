# Nether F12 自动爬塔设计规格

**日期：** 2026-08-07

**状态：** 已由用户确认，可进入实施计划

**目标版本：** 当前游戏最高 130 层，同时按服务器返回的最高层动态兼容未来扩层

## 1. 目标

为 AbyssMod 增加由 F12 单独控制的 Nether 自动爬塔功能。用户手动进入一个已经存在的 Nether 会话后，F12 根据服务器当前状态、当前区段地图、角色 HP、侵蚀、票、钥匙、金币、掉落物和 Nether 代码逐步选择安全动作，直到达到配置深度、票不足、服务器 Clear，或者遇到无法安全判断的状态。

自动化必须复用游戏原生流程保持客户端模型、动画和服务端记录一致；不得通过连续裸发 API 猜测客户端状态。

## 2. 不在本次范围内

- 不从 Nether 外部自动创建新局。
- 不替用户选择队伍、起始层、初始票倍率或主动开始 `NotPlayed` 会话。
- 不自动撤退，不调用语义未证实的 `api/nether/cancel`。
- 不自动消耗 Lost Signal 保险。
- 不自动分解背包物品。
- 不修改 F11 的开关状态或既有掉落截止策略。
- 不向游戏目录部署 DLL；本次只在仓库的忽略目录中生成验证产物。

## 3. 全局约束

- 主代理与所有子代理的开发、反编译、构建、测试和验证命令必须在一次性 `docker run --rm` 容器中执行。
- 游戏目录 `C:\Users\Eden\PixelAbyssX\dotabyss_x_cl` 在所有容器中只读挂载。
- 复杂 PowerShell 命令使用 PowerShell 的 `[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(...))` 包裹脚本，再在容器中解码；不得手工编码 Base64。
- 保留用户现有的 `README.md` 修改和未跟踪 `build/`，提交时只精确暂存本功能文件。
- 所有生产行为变更采用 TDD：先运行会因缺少行为而失败的测试，再写最小实现并运行通过。
- 现有基线为 199 个测试通过，同时存在既有 nullable 警告；本功能不得新增编译或测试警告。
- 不为旧版或猜测中的 F12 配置语法提供兼容层。

## 4. 权威状态与资源

自动器只把服务器响应视为权威。已确认的 Nether 状态为：

| 状态 | 值 | 自动器语义 |
|---|---:|---|
| `NotPlayed` | 1 | 不自动开局，暂停并等待用户 |
| `Play` | 2 | 通过原生恢复处理器回到可选楼层状态 |
| `Wait` | 3 | 通过原生恢复处理器继续当前节点，不硬编码具体子分支 |
| `Battle` | 5 | 通过原生恢复处理器恢复当前战斗，不重发 start |
| `Sleep` | 6 | 处理区段结算、带出物品和 `continue` |
| `Lose` | 7 | 关闭 F12 动作能力，保留原生保险/失败 UI |
| `Clear` | 8 | 等待进入 Result 场景并完成 `api/nether/result` |

关键资源：

- Nether Ticket：普通物品 ID `200002`。
- Lost Signal：普通物品 ID `200001`，只用于失败保险，默认永不自动消耗。
- Treasure Key：Nether 会话字段 `treasure_key`，不是续行票。
- Nether Gold：Nether 会话字段 `nether_gold`。
- 带出容量：研究数据 `NetherPointData.LockReward`。

`Sleep` 是唯一续行权威信号。当前玩法中的“每十层”不能实现为 `floor % 10 == 0`；未来扩层、恢复层或服务端规则变化均以 `status=Sleep` 为准。

## 5. F12 生命周期

F12 是运行时开关，不持久化为下次启动自动开启：

1. 不在 Nether 场景或没有有效 Nether 会话模型时按 F12，只记录 `not-in-nether`，不做服务器请求。
2. 在有效会话内按 F12，建立自动化运行实例并立刻读取当前服务器/客户端快照。
3. 再按 F12、离开 Nether、插件卸载、进入未知场景或发生安全暂停时，停止调度新动作。
4. 正在进行的非幂等请求不能被补发；等待其完成或重新用 `api/nether` 对账。
5. 游戏重启后 F12 默认关闭。若存在未清理的原生战斗设置租约，插件先恢复设置并清理租约，再允许新的 F12 会话。

## 6. 单飞对账状态机

运行态至少包含：

- `Disabled`
- `Reconciling`
- `Stable`
- `ExecutingNativeAction`
- `AwaitingBattle`
- `AwaitingF11`
- `AwaitingBattleSettlement`
- `AwaitingSceneChange`
- `Paused`
- `Completed`

每次变更动作保存动作前指纹：

`status + nether_id + map_id + floor_level + floor_index + erosion_point + character HP hash + code hash + map hash`

规则：

- 同一时刻最多一个变更动作。
- UI 点击成功不代表服务器完成；必须等待原生异步链结束和新快照。
- 请求超时、取消或异常导致结果未知时，进入 `Reconciling` 并只调用 `api/nether`。
- 新快照证明动作已生效后继续；证明未生效时才允许重新规划，不能机械重发同一请求。
- 快照与动作既不匹配也无法解释时暂停，理由为 `ambiguous-server-outcome`。
- `clear-battle` 和 `close-battle` 是互斥结束路径，绝不补发另一个。
- `Clear`/`Lose` 不是最终完成；只有 Result 场景中的 `api/nether/result` 成功返回后才标记 `Completed`。

## 7. 地图与路线规划

自动器每次只规划服务器当前返回的区段图：

- 节点来自 `t_nether_map_floors`。
- 边由实际前置节点 ID、客户端 `IsUnlocked` 和服务器当前位置共同确定。
- 隐藏节点只有在服务器已返回并标记可达后才可进入。
- `MNetherMapFloors` 只用于解释节点类型、侵蚀区间、顺序和 master 元数据，不能替代服务器授权。
- 每次 `update`、`update-event`、`clear-battle`、`continue` 或隐藏路线开启后重建图。

节点类型保持严格区分：

| 原始值 | 类型 |
|---:|---|
| 1 | Battle |
| 2 | Boss |
| 3 | MiniBoss |
| 4 | Event |
| 5 | Recovery |
| 6 | Shop |
| 7 | Treasure |
| 8 | Default/未知行为，暂停 |

现有 F11 将事件触发的战斗按普通 Battle 处理的逻辑保留；F12 在事件尚未选择前不得把 `Event=4` 当作 Battle。

规划算法：

1. 从当前明确可达候选中构图。
2. 对当前区段 Boss/终点做反向可达分析，排除进入后无法到达终点的死路。
3. 排除锁定隐藏节点、未知类型、已知致死 HP 选项、预测侵蚀达到 100 的节点。
4. 计算抵达区段终点所需的最小已知最坏侵蚀预算；预算达到 100 时不进入该路线。
5. 对剩余候选按字典序评分：安全可达、侵蚀下降、HP 恢复、Safe 代码机会、已知高价值奖励、较少非必要战斗、稳定的楼层 index/ID。
6. 选择一个节点，调用原生移动/节点执行链，并等待新快照后重新规划。

任何节点都不能在整个 1–130 路径上提前静态预排。

## 8. 侵蚀与 HP 策略

固定约束：

- 游戏原生警告值：80，仅作为日志标记。
- F12 默认软上限：90，可配置为 1–99。
- 游戏硬上限：100，不可配置放宽。
- 角色 HP 使用服务器 `NetherCharacterEntity.hp`，1000 表示 100%。
- 默认角色 HP 软下限为 300/1000，可配置为 1–1000。

侵蚀计算必须动态化：

- 普通战斗的基础增加量只作为输入，不假定最终一定为 5。
- 解析活动代码中的 `ErosionAdditionUp/Down` 与 `ErosionRateUp/Down`。
- Safe/Risk、Rush/Impact 的成对抵消按有效层数计算。
- 未识别代码效果、无法证明的计算顺序或数值溢出均暂停。
- `clear-battle` 前记录预测值，返回后比较实际值。
- 代码指纹未变化但预测和实际不一致时，以 `erosion-drift` 暂停；不能继续用旧模型冒险。

软上限语义：

- 可选动作若预测后侵蚀达到或超过 90，拒绝。
- 当前为 90–99 时只允许已完全识别、预测后仍低于 100 的必要 Boss/终点动作。
- 即使当前低于 90，若到区段终点的最小最坏侵蚀预算会达到 100，也必须提前换路或暂停。
- 所有事件选项在发送 `update-event` 前合并检查最多三段 effect。

HP 语义：

- 任一仍在队伍中的角色低于配置软下限时，优先 Recovery/Heal，避开可选 Damage 和可选战斗。
- 已知 Damage 导致任一角色 HP 小于等于 0 时拒绝。
- 没有安全恢复路线且下一必要战斗无法满足 HP 软限制时暂停，不假设战斗必胜。

## 9. Nether 代码策略

默认 `CombatLane=Auto`，可选 `Auto`、`Rush`、`Impact`，实时配置在下一次稳定决策生效。

Safe 优先级：

1. 精确代码 `30024`。
2. 使有效 Safe 层数达到至少 5 的候选。
3. 与当前锁定战斗路线匹配且有队伍覆盖的高收益代码。

硬性规则：

- Risk，包括 `40024`，不主动选择；已有 Risk 是容量满时的第一替换目标。
- Safe 与 Risk、Rush 与 Impact 分别用 `max(0, own - paired)` 计算有效层数。
- `Auto` 根据队伍覆盖、已有代码和距离阈值锁定 Rush 或 Impact；同一会话中除非当前路线失去全部覆盖，否则不反复摇摆。
- 保护 `30024` 和构成 Safe5 的代码。
- 容量满时按以下顺序移除：Risk、仅研究用途、非当前路线、零队伍覆盖、低稀有度/低覆盖。
- Safe 未成型且 `code_reload > CodeReloadReserve` 时允许 reload；默认 `CodeReloadReserve=1`。
- 剩余次数达到 reserve 后不再 reload，选择当前最强安全候选。
- 候选、容量、reload 次数或 master 数据不完整时暂停。

## 10. 节点动作策略

### Event

解析 `MNetherFloorEvents` 与 `MNetherFloorEventParts`，每个选项最多三个目标：Heal、Damage、Erosion、ErosionHeal、NetherGoldUsed、TreasureKeyUsed、AbyssCodeChanged、Battle。

- 先执行 HP/侵蚀/资源硬过滤。
- 安全选项按侵蚀下降、HP 恢复、Safe 代码、已知物品/金币/钥匙收益、避免非必要战斗排序。
- 文本只用于日志，不参与逻辑。
- 未知 target/content、选择号不合法或代码置换目标不明确时暂停。

### Recovery

优先顺序为净化侵蚀、恢复 HP、明确提升代码组合的置换。不存在正收益的安全选项时选择已确认无负面效果的选项；若没有则暂停。

### Treasure

默认 `TreasureMode=KeyOnly`：只选择 master 中明确消耗一把 Treasure Key 且当前钥匙充足的选项。不会自动用 HP 或侵蚀开箱；无钥匙时避免该路线，已经进入则暂停。

### Shop

默认 `ShopMode=Off`：不调用 `update-shop`。进入商店后只走已验证的原生关闭/离开操作并重新对账；若当前版本无法证明关闭无副作用，则暂停。预留 `EquipmentBags` 枚举值，但只有在能同时确认商品为 `MItems.type=91`、rarity 至少 Gold、支付资源为 Nether Gold 且余额足够时才允许购买。

### Default、未知和自动分解

`Default=8`、未知 floor type、无法识别的 popup、背包满或自动分解弹窗都暂停 F12，由用户接管。

## 11. 战斗、F11 与原生 Auto/倍速

- Battle、MiniBoss、Boss 和 Event 触发战斗均使用游戏原生 `start-battle` 链。
- F11 与 F12 独立：F12 不开启或关闭 F11。
- F11 已开启时，F12 等待其 Nether 重投操作完成并接受目标响应，再允许战斗模型继续。
- F11 关闭时直接进入原生战斗。
- F12 只在 Nether 战斗期间强制原生 Auto 和最高战斗倍速。
- 地图的 `NetherFloorSizeType.Triple` 与战斗倍速没有关系，不得混用。

设置租约：

1. 强制前读取用户原 Auto/倍速。
2. 先将原值和 lease-active 原子写入 BepInEx 配置目录中的独立租约文件，再修改游戏设置。
3. 正常战斗返回、F12 关闭、离开 Nether 或插件卸载时恢复原值并删除租约。
4. 异常退出留下租约时，下次插件启动先恢复原值。
5. 读取、写入或恢复失败均暂停 F12并打印路径与错误，不继续修改设置。

## 12. 最大深度、票与 Sleep

默认 `MaxDepth=130`：

- 有效目标为 `min(config MaxDepth, server MaxFloorLevel, MNetherMaps.max_floor_num)`。
- F12 不进入高于有效目标的楼层。
- 目标若不是 Sleep 关口，到达该层后暂停并交给用户，不自动撤退或提前结算。
- 不因票不足而拒绝启用 F12。
- 每次仅在 `status=Sleep` 时检查下一段票数，默认 `use_ticket=1`，不启用双倍/三倍票。
- 已达到目标或剩余票不足一张时，在 Sleep 原生流程中选择“不继续”，进入正常 Result 结算。
- Continue 成功会切回 NetherTop；必须等待场景切换后重新 `api/nether`，不能本地把楼层加十。

## 13. 十层带出物品

当 `LockReward > 0` 时，自动选择最多该数量的已获得条目，并随 `continue` 提交：

1. 现有 `NetherPreserveItemIds` 中的 item ID。
2. `MItems.type=91` 的装备袋。
3. 掉落 `rarity_level` 从 UniqueWeapon、Red、Gold、Purple、Silver 到 NoEffect。
4. master rarity 降序。
5. item ID 升序作为稳定平局规则。

选择整个服务器返回的条目/amount，不自行拆分堆叠。master 缺失、服务端返回的条目上限语义与预期不符，或选中条目无法映射时，在发送 `continue` 前暂停。

## 14. 配置与实时重载

新增 `[NetherAutoClimb]`：

| Key | 默认值 | 合法值/语义 |
|---|---|---|
| `MaxDepth` | `130` | 正整数；按服务器上限收缩 |
| `SoftErosionLimit` | `90` | `1..99` |
| `MinimumCharacterHpPermille` | `300` | `1..1000` |
| `CombatLane` | `Auto` | `Auto, Rush, Impact` |
| `CodeReloadReserve` | `1` | 非负整数 |
| `TreasureMode` | `KeyOnly` | `Off, KeyOnly` |
| `ShopMode` | `Off` | `Off, EquipmentBags` |
| `DetailedLogging` | `true` | 布尔值 |

配置注释必须说明票 `200002`、保险 `200001`、`30024`、`40024`、Safe/Risk 抵消、90/100 限制，以及 F11/F12 独立关系。

现有 `ConfigAutoReload` 负责实时 reload。运行中的非幂等动作使用动作开始时的配置快照；新配置从下一个 `Stable` 决策开始生效。非法配置不回退到危险默认值，而是暂停并打印具体 key/value。

## 15. 日志与可观测性

日志前缀统一为 `[F12][NetherClimb]`。只在状态变化或动作边界记录，避免逐帧刷屏：

- toggle、进入/离开 Nether、暂停、恢复、完成；
- 服务器 status、floor/map、HP、侵蚀、票、钥匙、金币、代码摘要；
- 当前图候选、拒绝理由、反向可达结果、最终选点；
- 事件三段效果和选择理由；
- 战斗前侵蚀预测、战斗后实际差值和代码指纹；
- F11 是否阻塞战斗进入；
- Sleep 目标判断、票判断、带出物品排序；
- 请求动作的发送、确认、未知结果和对账结论；
- Auto/倍速租约的保存、强制、恢复和故障。

任何错误日志不得吞掉异常类型、当前状态或暂停原因。

## 16. 安全暂停与结束

以下情况必须停止调度新动作并保留现场：

- 不在 Nether、NotPlayed、未知 status；
- 无安全可达路线或图结构不一致；
- 未知 master/事件/代码效果；
- 侵蚀预测达到 100、预测漂移或 HP 不安全；
- 非幂等请求结果无法对账；
- 未支持 popup、自动分解、背包满；
- Battle 设置租约失败；
- Lose、用户关闭 F12、离开 Nether。

票不足发生在 Sleep 时不是错误：选择不继续并正常进入 Result。

## 17. 验收条件

### 自动化测试

- 状态机覆盖单飞、未知结果先同步、互斥 clear/close、Result 才完成。
- 路线规划覆盖死路、隐藏锁定、Event/Battle 区分、稳定平局和每响应重规划。
- 侵蚀覆盖 0/5/10、Safe/Risk 抵消、rate/unknown、90 软限、100 硬限和漂移暂停。
- 代码策略覆盖 `30024`、Safe5、Risk 拒绝、Auto lane、容量替换和保留一次 reload。
- 事件覆盖最多三段 effect、HP 致死、侵蚀致死、未知 target、Recovery、KeyOnly 和 Shop Off。
- Sleep 覆盖达到目标、票不足、继续一票、非关口 MaxDepth 暂停和锁定物品排序。
- 战斗设置租约覆盖正常恢复、F12 关闭、卸载和崩溃后恢复。
- 配置覆盖默认值、非法值暂停和 reload 后下一稳定决策生效。

### 构建验证

- 在单个 `docker run --rm` 中 restore+test，全部测试通过且无本功能新增警告。
- 使用同一个临时容器生命周期编译插件；游戏目录以 `/game:ro` 挂载，覆盖输出到仓库忽略的 `release/nether-auto-climb/`。
- 检查生成 `AbyssMod.dll`，不得复制到游戏目录。
- 审查 git diff，确认不包含 `README.md`、`build/`、游戏文件或生成的 DLL。

### 首次实机验证边界

Docker 能证明纯策略、状态机和插件编译，不能运行 Windows Unity 客户端。首次实机运行应保持 `DetailedLogging=true`；任何未在静态反编译中证实的 native 调用绑定失败都必须 fail closed，而不是猜测请求。
