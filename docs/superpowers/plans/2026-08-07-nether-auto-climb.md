# Nether F12 Auto-Climb Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a fail-closed F12 Nether auto-climber that follows the server-authoritative state machine, chooses safe routes and events, coordinates with F11 drop rerolls, controls native battle Auto/speed through a recoverable lease, and stops or settles at the configured depth or affordable checkpoint.

**Architecture:** Pure, .NET-only policy files own snapshots, route planning, erosion/code/event/checkpoint decisions, and transaction state so they can be tested without Unity. A thin IL2CPP runtime bridge captures native Nether models and invokes existing native controller flows; a frame-driven coordinator applies one decision at a time and reconciles every non-idempotent action against a new server snapshot. Harmony patches only register native controllers and report lifecycle/action boundaries.

**Tech Stack:** C# 10+/net6.0 BepInEx IL2CPP plugin, Harmony, Cysharp UniTask, xUnit net8.0 linked-source tests, Docker `mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim`, Docker `alpine/git`.

## Global Constraints

- The approved design is `docs/superpowers/specs/2026-08-07-nether-auto-climb-design.md`; it is the source of truth.
- One persistent implementer owns every task in this plan. Only after all implementation tasks finish do two other persistent agents run spec and code reviews in parallel. Fixes return to the same implementer; re-reviews return to the same reviewer sessions.
- All development, reverse-engineering, build, test, and verification commands run through `docker run --rm`.
- Mount `C:\Users\Eden\PixelAbyssX\dotabyss_x_cl` read-only at `/game` whenever game files are needed.
- Mount `C:\Users\Eden\PixelAbyssX\reverse_out` read-only at `/reverse` whenever decompiled evidence is needed.
- Use PowerShell `[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($taskScript))` for complex scripts passed into containers; do not hand-roll Base64.
- Use `apply_patch` for repository file edits. Do not edit or deploy into the game directory.
- Preserve the user's modified `README.md` and untracked `build/`; never stage either path.
- F12 is runtime-only and starts OFF after every plugin load. F11 remains independent.
- Defaults are exact: `MaxDepth=130`, `SoftErosionLimit=90`, `MinimumCharacterHpPermille=300`, `CombatLane=Auto`, `CodeReloadReserve=1`, `TreasureMode=KeyOnly`, `ShopMode=Off`, `DetailedLogging=true`.
- IDs are exact: Ticket `200002`, Lost Signal `200001`, preferred Safe code `30024`, rejected Risk code `40024`.
- Hard erosion limit is 100 and cannot be configured upward. Ticket use per `continue` is exactly 1. Never auto-use boost, Lost Signal, `api/nether/cancel`, HP/erosion treasure payment, or auto-disassembly.
- Follow strict TDD for every production behavior: record a focused RED command and expected failure before implementation, then GREEN output after the minimal implementation.
- The baseline is 199 passing tests with pre-existing nullable warnings. Add no new warning category or warning location.
- Commit each task with the exact files named in that task. Do not push and do not deploy a DLL.

---

## File Structure

### Pure/testable production files

- `AbyssMod/Services/NetherAutoClimbModels.cs` — immutable snapshot, node, resource, effect, action, settings, fingerprint, pause reason, and result types.
- `AbyssMod/Services/NetherAutoClimbStateMachine.cs` — runtime toggle and single-flight send/await/reconcile transitions.
- `AbyssMod/Services/NetherRoutePlanner.cs` — server graph validation, reverse reachability, safety filtering, and deterministic candidate ranking.
- `AbyssMod/Services/NetherErosionPolicy.cs` — event and battle erosion projection, Safe/Risk cancellation, hard/soft limit enforcement, and drift comparison.
- `AbyssMod/Services/NetherCodePolicy.cs` — Safe anchor, Auto/Rush/Impact lane lock, reload reserve, and capacity replacement.
- `AbyssMod/Services/NetherEventPolicy.cs` — generic three-effect event/recovery/treasure/shop decisions.
- `AbyssMod/Services/NetherCheckpointPolicy.cs` — max-depth, ticket, Sleep continuation, and Result decisions.
- `AbyssMod/Services/NetherReturnItemPolicy.cs` — deterministic `LockReward` ranking.
- `AbyssMod/Services/NetherBattleSettingsLeaseState.cs` — serializable lease state and pure recovery transitions.

### Runtime-only production files

- `AbyssMod/Services/NetherRuntimeBridge.cs` — maps live IL2CPP/master data to pure models and invokes verified native flows.
- `AbyssMod/Services/NetherBattleSettingsLease.cs` — reads/writes the lease atomically and controls exact native Auto/speed settings.
- `AbyssMod/Services/NetherAutoClimbController.cs` — frame-driven coordinator, logging, pending UniTask polling, and policy orchestration.
- `AbyssMod/Patches/NetherAutoClimbPatch.cs` — Harmony lifecycle/battle/result hooks that register live controllers and report boundaries.

### Existing integration files

- `AbyssMod/Core/Config.cs` — bind documented live-reload settings.
- `AbyssMod/Core/Hotkey.cs` — update coordinator and toggle F12.
- `AbyssMod/Core/Plugin.cs` — initialize and unload/recover lease.
- `AbyssMod/Patches/PatchManager.cs` — register the new patch set.
- `AbyssMod/Services/BattleSessionAutoSL.cs` — expose read-only Nether-operation busy state for F12 coordination.
- `AbyssMod.Tests/AbyssMod.Tests.csproj` — link pure production files into the test assembly.

### Tests

- `AbyssMod.Tests/NetherAutoClimbStateMachineTests.cs`
- `AbyssMod.Tests/NetherRoutePlannerTests.cs`
- `AbyssMod.Tests/NetherErosionPolicyTests.cs`
- `AbyssMod.Tests/NetherCodePolicyTests.cs`
- `AbyssMod.Tests/NetherEventPolicyTests.cs`
- `AbyssMod.Tests/NetherCheckpointPolicyTests.cs`
- `AbyssMod.Tests/NetherReturnItemPolicyTests.cs`
- `AbyssMod.Tests/NetherBattleSettingsLeaseStateTests.cs`

---

### Task 1: Pure snapshots and single-flight reconciliation state machine

**Files:**
- Create: `AbyssMod/Services/NetherAutoClimbModels.cs`
- Create: `AbyssMod/Services/NetherAutoClimbStateMachine.cs`
- Create: `AbyssMod.Tests/NetherAutoClimbStateMachineTests.cs`
- Modify: `AbyssMod.Tests/AbyssMod.Tests.csproj`

**Interfaces:**
- Produces `NetherSessionStatus`, `NetherFloorNodeType`, `NetherAutoClimbPhase`, `NetherActionKind`, `NetherPauseReason`, `NetherSnapshot`, `NetherSnapshotFingerprint`, `NetherFloorNode`, `NetherCharacterState`, `NetherCodeState`, `NetherEffect`, `NetherRewardItem`, `NetherAutoClimbSettings`, and `NetherPlannedAction`.
- Produces `NetherAutoClimbStateMachine.Toggle(bool isInNether)`, `BeginReconcile()`, `ObserveStable(NetherSnapshotFingerprint)`, `TryBegin(NetherPlannedAction, NetherSnapshotFingerprint)`, `ObserveActionResult(NetherSnapshotFingerprint)`, `ObserveUnknownOutcome()`, `ObserveF11Busy(bool)`, `Pause(NetherPauseReason,string)`, and `Complete()`.
- Later tasks consume these types without referencing Unity or game assemblies.

- [ ] **Step 1: Write the failing state-machine tests**

Add tests whose names and literal outcomes cover:

```csharp
[Fact]
public void Toggle_outside_nether_stays_disabled_with_not_in_nether_reason();

[Fact]
public void One_in_flight_action_rejects_a_second_action();

[Fact]
public void Unknown_outcome_requires_reconcile_before_another_action();

[Fact]
public void Changed_fingerprint_confirms_action_and_returns_stable();

[Fact]
public void Unchanged_fingerprint_after_known_failure_returns_stable_without_retrying();

[Fact]
public void Ambiguous_fingerprint_pauses_instead_of_replaying_action();

[Theory]
[InlineData(NetherSessionStatus.Clear)]
[InlineData(NetherSessionStatus.Lose)]
public void Clear_or_lose_is_not_completed_until_result_response(NetherSessionStatus status);

[Fact]
public void F11_busy_moves_battle_wait_to_awaiting_f11_and_back();
```

Construct fingerprints with hand-written values; never compute expected fingerprints with the production builder.

- [ ] **Step 2: Run RED in a disposable container**

Run:

```powershell
docker run --rm --mount type=bind,source="C:\Users\Eden\PixelAbyssX\AbyssModMod",target=/work -w /work mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim dotnet test AbyssMod.Tests/AbyssMod.Tests.csproj --configuration Release --filter FullyQualifiedName~NetherAutoClimbStateMachineTests --nologo
```

Expected: compilation fails because the new production types do not exist. Save the command and relevant failure in the implementation report.

- [ ] **Step 3: Implement immutable models and the transition table**

Use enum values matching the server where applicable:

```csharp
internal enum NetherSessionStatus { Unknown = 0, NotPlayed = 1, Play = 2, Wait = 3, Battle = 5, Sleep = 6, Lose = 7, Clear = 8 }
internal enum NetherFloorNodeType { Unknown = 0, Battle = 1, Boss = 2, MiniBoss = 3, Event = 4, Recovery = 5, Shop = 6, Treasure = 7, Default = 8 }
internal enum NetherAutoClimbPhase { Disabled, Reconciling, Stable, ExecutingNativeAction, AwaitingBattle, AwaitingF11, AwaitingBattleSettlement, AwaitingSceneChange, Paused, Completed }
internal enum NetherActionKind { None, Reconcile, SelectFloor, SelectEventOption, LeaveShop, BuyShopItem, SelectCode, ReloadCode, Continue, FinishAtCheckpoint, AwaitNativeFlow, RestoreBattleSettings }
```

`TryBegin` succeeds only from `Stable`; it stores action and pre-action fingerprint. `ObserveUnknownOutcome` clears no evidence and moves to `Reconciling`. `ObserveActionResult` accepts an explicit outcome classification (`Applied`, `NotApplied`, `Ambiguous`) so the caller cannot infer success from inequality alone. `Clear` and `Lose` move to `AwaitingSceneChange`; only an observed successful result response calls `Complete`.

- [ ] **Step 4: Run focused GREEN and the full baseline suite**

Run the focused command from Step 2, then:

```powershell
docker run --rm --mount type=bind,source="C:\Users\Eden\PixelAbyssX\AbyssModMod",target=/work -w /work mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim dotnet test AbyssMod.Tests/AbyssMod.Tests.csproj --configuration Release --nologo
```

Expected: all new state-machine tests and all 199 baseline tests pass; no new warning location appears.

- [ ] **Step 5: Commit Task 1**

Use Dockerized git and stage only the four Task 1 paths. Commit message:

```text
feat: add nether auto-climb state model
```

---

### Task 2: Server graph validation and safe deterministic route planning

**Files:**
- Create: `AbyssMod/Services/NetherRoutePlanner.cs`
- Create: `AbyssMod.Tests/NetherRoutePlannerTests.cs`
- Modify: `AbyssMod.Tests/AbyssMod.Tests.csproj`

**Interfaces:**
- Consumes `NetherSnapshot`, `NetherFloorNode`, `NetherFloorNodeType`, `NetherAutoClimbSettings`.
- Produces `NetherRoutePlanner.Plan(NetherSnapshot, NetherRouteSafetyContext)` returning `NetherRoutePlan` with selected node or exact pause reason and a candidate audit list.
- Produces `NetherRouteSafetyContext` containing minimum worst-case erosion to the segment terminal, HP safety, known-node flags, and Safe-code opportunity.

- [ ] **Step 1: Write failing route tests**

Use literal graphs to prove these mutations are caught:

```csharp
[Fact] public void Event_type_four_remains_event_and_is_not_classified_as_battle();
[Fact] public void Locked_hidden_node_is_never_selected();
[Fact] public void Candidate_leading_to_dead_end_is_rejected_even_when_reward_is_higher();
[Fact] public void Planner_uses_server_prev_ids_instead_of_master_next_guess();
[Fact] public void Newly_opened_node_is_considered_only_in_the_next_snapshot();
[Fact] public void Unknown_or_default_floor_causes_fail_closed_pause();
[Fact] public void Candidate_whose_terminal_erosion_budget_reaches_100_is_rejected();
[Fact] public void Equivalent_candidates_use_floor_index_then_id_for_stable_tie_breaking();
```

- [ ] **Step 2: Run RED**

Run the Task 1 Docker test command with filter `FullyQualifiedName~NetherRoutePlannerTests` and record the missing-type/compiler failure.

- [ ] **Step 3: Implement route planning**

Build adjacency only from the current server snapshot. Determine current candidates from explicit predecessor IDs plus `IsUnlocked`; do a reverse traversal from current segment Boss/terminal to mark nodes that retain a route to completion. Use a comparison tuple rather than opaque summed weights:

```text
HardSafe descending,
TerminalReachable descending,
ProjectedErosionDelta ascending,
ProjectedHpDelta descending,
SafeCodeOpportunity descending,
RewardTier descending,
OptionalCombatCount ascending,
FloorIndex ascending,
FloorId ascending
```

If the graph has multiple current nodes, missing IDs, cycles that prevent determining the current frontier, no terminal, or no safe candidate, return a pause decision with the graph audit instead of guessing.

- [ ] **Step 4: Run focused GREEN and full tests**

Run `NetherRoutePlannerTests`, then the complete test project in fresh disposable containers. Expected: all pass with no new warnings.

- [ ] **Step 5: Commit Task 2**

Stage only Task 2 paths. Commit message:

```text
feat: add safe nether route planning
```

---

### Task 3: Dynamic erosion projection and Safe-first code selection

**Files:**
- Create: `AbyssMod/Services/NetherErosionPolicy.cs`
- Create: `AbyssMod/Services/NetherCodePolicy.cs`
- Create: `AbyssMod.Tests/NetherErosionPolicyTests.cs`
- Create: `AbyssMod.Tests/NetherCodePolicyTests.cs`
- Modify: `AbyssMod.Tests/AbyssMod.Tests.csproj`

**Interfaces:**
- Produces `NetherErosionPolicy.ProjectBattle(...)`, `ProjectEffects(...)`, and `CompareObserved(...)`.
- Produces `NetherCodePolicy.Decide(NetherCodePortfolio, candidates, settings)` returning `Select`, `Reload`, `Keep`, or `Pause`, plus protected and removable code IDs.
- Runtime mapping supplies exact master effect types and parameters; pure policies reject `Known=false` effects.

- [ ] **Step 1: Write failing erosion tests**

Cover literal expectations:

```csharp
[Theory]
[InlineData(40, 5, 45)]
[InlineData(40, 0, 40)]
[InlineData(40, 10, 50)]
public void Battle_projection_uses_effective_dynamic_delta(int current, int delta, int expected);

[Fact] public void Optional_action_reaching_soft_limit_90_is_rejected();
[Fact] public void Mandatory_boss_below_hard_limit_is_allowed_at_soft_limit();
[Fact] public void Any_projection_reaching_100_is_rejected();
[Fact] public void Three_event_effects_are_aggregated_before_limit_check();
[Fact] public void Unknown_rate_or_addition_effect_pauses();
[Fact] public void Unchanged_code_fingerprint_with_wrong_observed_delta_reports_drift();
[Fact] public void Changed_code_fingerprint_requires_rebaseline_instead_of_drift_claim();
```

- [ ] **Step 2: Write failing code-policy tests**

Cover:

```csharp
[Fact] public void Exact_30024_beats_all_other_candidates();
[Fact] public void Effective_safe_is_max_zero_safe_minus_risk();
[Fact] public void Effective_rush_and_impact_cancel_each_other();
[Fact] public void Risk_40024_is_never_selected();
[Fact] public void Existing_risk_is_first_capacity_replacement();
[Fact] public void Safe_five_is_protected_from_replacement();
[Fact] public void Auto_lane_locks_to_party_coverage_and_does_not_oscillate();
[Fact] public void Reload_is_used_only_when_remaining_is_greater_than_reserve_one();
[Fact] public void Last_reload_is_preserved_and_best_safe_candidate_is_selected();
[Fact] public void Missing_master_or_over_capacity_snapshot_pauses();
```

- [ ] **Step 3: Run RED for both suites**

Run a Docker filter matching `NetherErosionPolicyTests|NetherCodePolicyTests`; if the test runner filter cannot express OR, run two disposable containers. Record expected missing-type failures.

- [ ] **Step 4: Implement the policies**

Represent erosion effects as explicit operation and parameter records. Apply additions and rates in the exact order recovered from current master/client logic; if the current codebase cannot prove a rate order, return `unknown-effect-order` instead of choosing one. Always clamp arithmetic with checked operations and reject outside `0..100`.

Implement code decisions as deterministic tuple ranking with `30024` first, then reaching Safe5, then the locked combat lane. Replacement order is exactly Risk, research-only, off-lane, zero coverage, low rarity/coverage. Preserve one reload by default.

- [ ] **Step 5: Run GREEN and full tests**

Run both focused suites, then all tests. Expected: all pass; no new warnings.

- [ ] **Step 6: Commit Task 3**

Stage only Task 3 paths. Commit message:

```text
feat: add nether erosion and code policies
```

---

### Task 4: Generic event, recovery, treasure, and shop policy

**Files:**
- Create: `AbyssMod/Services/NetherEventPolicy.cs`
- Create: `AbyssMod.Tests/NetherEventPolicyTests.cs`
- Modify: `AbyssMod.Tests/AbyssMod.Tests.csproj`

**Interfaces:**
- Consumes `NetherEffect`, current resource/HP snapshot, erosion policy, code policy preview, and configured `TreasureMode`/`ShopMode`.
- Produces `NetherEventDecision` with selected 1-based option number, optional code replacement ID, exact rejection audit, or pause.
- Produces `NetherShopDecision` with `Leave`, `Buy(contentId,amount)`, or `Pause`.

- [ ] **Step 1: Write failing tests**

Cover the behavior rather than localized strings:

```csharp
[Fact] public void Event_option_combines_all_three_effect_targets();
[Fact] public void Lethal_damage_option_is_rejected();
[Fact] public void Erosion_option_reaching_hard_limit_is_rejected();
[Fact] public void Erosion_heal_beats_hp_heal_when_erosion_pressure_is_higher();
[Fact] public void Hp_heal_beats_code_change_when_character_is_below_soft_hp();
[Fact] public void Unknown_target_or_content_pauses_instead_of_selecting();
[Fact] public void Event_triggered_battle_is_marked_battle_only_after_event_selection();
[Fact] public void KeyOnly_selects_the_exact_key_cost_option_when_key_is_available();
[Fact] public void KeyOnly_pauses_when_already_in_treasure_without_a_key();
[Fact] public void Treasure_never_selects_hp_or_erosion_payment();
[Fact] public void ShopOff_never_creates_a_purchase_request();
[Fact] public void EquipmentBags_requires_type_91_gold_or_better_and_nether_gold_cost();
```

- [ ] **Step 2: Run RED**

Run the focused test in a disposable SDK container and record the expected missing behavior.

- [ ] **Step 3: Implement deterministic event evaluation**

Validate 1–3 known effects per option and a positive 1-based selection number. Hard-filter unsafe effects first. Rank safe choices by erosion reduction, HP recovery, Safe portfolio improvement, known item/currency/key benefit, and avoiding optional battle. `ShopMode.Off` returns only `Leave`; `EquipmentBags` requires all conditions from the design and never spends a different content type. Text fields may be copied into logs but cannot influence decisions.

- [ ] **Step 4: Run GREEN and full tests**

Run focused and full test commands. Expected: all pass and no new warning location.

- [ ] **Step 5: Commit Task 4**

Stage only Task 4 paths. Commit message:

```text
feat: add safe nether node action policy
```

---

### Task 5: Sleep, MaxDepth, ticket, Result, and return-item policies

**Files:**
- Create: `AbyssMod/Services/NetherCheckpointPolicy.cs`
- Create: `AbyssMod/Services/NetherReturnItemPolicy.cs`
- Create: `AbyssMod.Tests/NetherCheckpointPolicyTests.cs`
- Create: `AbyssMod.Tests/NetherReturnItemPolicyTests.cs`
- Modify: `AbyssMod.Tests/AbyssMod.Tests.csproj`

**Interfaces:**
- Produces `NetherCheckpointPolicy.Decide(snapshot, settings)` with `ContinueOneTicket`, `FinishNormally`, `PauseAtNonCheckpointTarget`, or `AwaitResult`.
- Produces `NetherReturnItemPolicy.Select(items, lockReward, preserveIds)` returning whole item/amount entries and an audit list.

- [ ] **Step 1: Write failing checkpoint tests**

```csharp
[Fact] public void Effective_max_depth_is_minimum_of_config_server_and_master();
[Fact] public void F12_never_enters_a_floor_above_effective_target();
[Fact] public void Non_sleep_target_floor_pauses_without_cancel_or_result();
[Fact] public void Sleep_below_target_with_ticket_continues_with_exactly_one_ticket();
[Fact] public void Sleep_at_target_finishes_normally();
[Fact] public void Sleep_without_ticket_finishes_normally_instead_of_refusing_start();
[Fact] public void Clear_awaits_result_scene_response();
[Fact] public void Lose_pauses_without_using_signal();
```

- [ ] **Step 2: Write failing return-item tests**

Use literal item fixtures to prove priority:

```csharp
[Fact] public void Preserve_id_beats_equipment_rarity();
[Fact] public void Type_91_equipment_beats_non_equipment();
[Fact] public void Unique_red_gold_purple_silver_order_is_stable();
[Fact] public void Master_rarity_then_item_id_breaks_ties();
[Fact] public void Selection_never_exceeds_lock_reward();
[Fact] public void Whole_amount_is_preserved_without_splitting_stack();
[Fact] public void Missing_master_for_positive_lock_reward_pauses_before_continue();
```

- [ ] **Step 3: Run RED**

Run both focused suites in disposable containers and save expected failures.

- [ ] **Step 4: Implement checkpoint and return policies**

Treat only `Sleep` as permission to continue. Use exactly one ticket. Reaching target or lacking the next ticket selects the native “do not continue” path. A non-Sleep target pauses in place. Return items use the exact priority in the design, select whole entries, and require complete master mapping before producing a continue payload.

- [ ] **Step 5: Run GREEN and full tests**

Run focused and complete suites. Expected: all pass with no new warnings.

- [ ] **Step 6: Commit Task 5**

Stage only Task 5 paths. Commit message:

```text
feat: add nether checkpoint resource policies
```

---

### Task 6: Reverse-confirmed IL2CPP runtime bridge and Harmony lifecycle hooks

**Files:**
- Create: `AbyssMod/Services/NetherRuntimeBridge.cs`
- Create: `AbyssMod/Patches/NetherAutoClimbPatch.cs`
- Modify: `AbyssMod/Patches/PatchManager.cs`
- Modify: `AbyssMod/Services/NetherAutoClimbModels.cs`
- Modify: `AbyssMod.Tests/NetherAutoClimbStateMachineTests.cs`

**Interfaces:**
- Produces `INetherRuntimeBridge` operations for snapshot/reconcile, native floor selection, event selection, shop leave/purchase, code select/reload, Sleep continue/finish, and Result observation.
- Produces static registration entry points used by patches: `RegisterFloorSelection`, `UnregisterFloorSelection`, `RegisterCodePopup`, `RegisterReturnPopup`, `ObserveBattleStart`, `ObserveBattleClear`, `ObserveBattleClose`, `ObserveResult`.
- Runtime methods return a structured `Started`, `Completed`, `Rejected`, `UnknownOutcome`, or `BindingUnavailable` result; no `bool` may collapse unknown outcome into failure.

- [ ] **Step 1: Inspect exact native bindings in Docker with RO mounts**

Use PowerShell Base64 wrapping and a disposable Debian container to search these evidence roots and current interop assemblies:

```text
/reverse/cpp2il_isil/IsilDump/Project/Project/Nether/FloorSelection/SubViewController_*
/reverse/cpp2il_isil/IsilDump/Project/Project/NetherTop/Result/SubViewController_*
/reverse/cpp2il_isil/IsilDump/Project/Project/Ingame/Exploration/NetherAPIService_*
/reverse/cpp2il_latest/DiffableCs/Project/Project/Api/NetherApiDataStore.cs
/game/BepInEx/interop/*.dll
```

Record in the implementation report the exact current type/method names and parameter shapes for `_HandleStartEventByStatusAsync`, `_OnFloorClickedEventAsync`, `_ExecuteNextFloorMovementSequenceAsync`, event option callbacks, code popup callbacks, return-item callbacks, continue confirmation, shop close, and Result response. If a required mutation can only be implemented by issuing a raw endpoint while bypassing native model updates, report `BLOCKED`; do not substitute the raw request.

- [ ] **Step 2: Add a failing fail-closed binding-selection characterization test**

Represent candidates with plain descriptors and assert that the exact name, arity, parameter type names, and return type select one method; zero or multiple matches return `BindingUnavailable`. Run RED before implementing the selector.

- [ ] **Step 3: Implement snapshot mapping**

Map server/runtime values into pure models without caching authority across responses: status, IDs, current/max/continuance floor, erosion, HP, ticket `200002`, signal `200001`, keys, gold, code reload/capacity/candidates, actual returned floor graph, effects, shop contents, acquired items, and `LockReward`. Any missing required master row produces a named mapping error.

- [ ] **Step 4: Implement native action adapters and lifecycle patches**

Invoke the verified native controller flow for exactly one action. Patches capture live controller instances and action boundaries but do not choose policy. Ensure Event remains Event until its selected result actually starts battle. Patch registration is added once in `PatchManager.Initialize`.

All reflection/dynamic binding must require exactly one current-version match and fail closed with a detailed log. Do not search for “nearest” overload by argument count alone.

- [ ] **Step 5: Compile the plugin against the RO game directory**

Run one disposable container with repository RW and game RO, overriding output away from `/game`:

```powershell
docker run --rm --mount type=bind,source="C:\Users\Eden\PixelAbyssX\AbyssModMod",target=/work --mount type=bind,source="C:\Users\Eden\PixelAbyssX\dotabyss_x_cl",target=/game,readonly -w /work mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim sh -lc 'dotnet restore AbyssMod/AbyssMod.csproj -p:GameDir=/game && dotnet build AbyssMod/AbyssMod.csproj --configuration Release --no-restore -p:GameDir=/game -p:OutputPath=/work/release/nether-auto-climb/'
```

Expected: build exit 0; no attempt writes below `/game`; no new warnings. If the current game interop makes a binding impossible, stop with the exact missing symbol rather than weakening fail-closed behavior.

- [ ] **Step 6: Run all pure tests**

Run the complete test project in a fresh container. Expected: all pass.

- [ ] **Step 7: Commit Task 6**

Stage only Task 6 source/test/project paths; exclude `release/`. Commit message:

```text
feat: bridge nether native runtime flow
```

---

### Task 7: Battle Auto/speed lease and F11 coordination

**Files:**
- Create: `AbyssMod/Services/NetherBattleSettingsLeaseState.cs`
- Create: `AbyssMod/Services/NetherBattleSettingsLease.cs`
- Create: `AbyssMod.Tests/NetherBattleSettingsLeaseStateTests.cs`
- Modify: `AbyssMod/Services/BattleSessionAutoSL.cs`
- Modify: `AbyssMod.Tests/AbyssMod.Tests.csproj`

**Interfaces:**
- Produces pure `NetherBattleSettingsLeaseState` transitions `Empty`, `Saved`, `Forced`, `RestorePending`, `Restored`, and `Faulted`.
- Produces runtime `NetherBattleSettingsLease.AcquireAndForce()`, `Restore(reason)`, `RecoverOnLoad()`, and `Dispose()`.
- Adds read-only `BattleSessionAutoSL.HasActiveNetherOperation`; it must not expose mutable operation collections.

- [ ] **Step 1: Reverse-confirm exact native battle-setting accessors**

Search current interop and reverse output in a disposable RO-mounted container for the same Auto and highest-speed state manipulated by the game's native battle UI. Record exact types, values, and setters. Do not infer settings from `NetherFloorSizeType.Triple`.

- [ ] **Step 2: Write failing lease tests**

```csharp
[Fact] public void Acquire_saves_original_values_before_force_transition();
[Fact] public void Restore_returns_exact_original_auto_and_speed();
[Fact] public void F12_off_requests_restore_even_during_battle();
[Fact] public void Plugin_unload_requests_restore();
[Fact] public void Persisted_active_lease_is_recovered_on_next_load();
[Fact] public void Failed_atomic_save_faults_without_forcing_settings();
[Fact] public void Failed_restore_remains_recoverable_and_pauses_climber();
```

- [ ] **Step 3: Run RED**

Run `NetherBattleSettingsLeaseStateTests` in a fresh SDK container and record the expected missing types.

- [ ] **Step 4: Implement pure lease state and runtime adapter**

Write the lease as a temporary file followed by atomic replace in BepInEx `Paths.ConfigPath`; store schema version, active flag, original Auto, original speed, and creation timestamp. Never store tokens or server payloads. Save must succeed before forcing. Restore deletes the lease only after both native values are confirmed restored.

Acquire only after Nether battle starts; restore after the battle bridge completes, on F12 off, on leaving Nether, and on unload. `RecoverOnLoad` runs before F12 can enable.

- [ ] **Step 5: Implement F11 busy exposure**

Track active Nether operations by increment/decrement at operation lifetime boundaries, including cancellation and faults. `HasActiveNetherOperation` is true only while a Nether operation remains in the list. F12 waits; it never mutates the F11 ConfigEntry.

- [ ] **Step 6: Run focused tests, all tests, and plugin build**

Run the focused lease suite, complete test suite, then the Task 6 plugin build command. Expected: all exit 0 with no new warnings.

- [ ] **Step 7: Commit Task 7**

Stage only Task 7 paths. Commit message:

```text
feat: coordinate nether battle automation
```

---

### Task 8: Frame-driven coordinator, F12/config integration, logging, and final artifact

**Files:**
- Create: `AbyssMod/Services/NetherAutoClimbController.cs`
- Modify: `AbyssMod/Core/Config.cs`
- Modify: `AbyssMod/Core/Hotkey.cs`
- Modify: `AbyssMod/Core/Plugin.cs`
- Modify: `AbyssMod/Services/NetherRuntimeBridge.cs`
- Modify: `AbyssMod/Patches/NetherAutoClimbPatch.cs`
- Modify: `AbyssMod.Tests/NetherAutoClimbStateMachineTests.cs`
- Modify: `AbyssMod.Tests/NetherCheckpointPolicyTests.cs`

**Interfaces:**
- Produces static `NetherAutoClimbController.Initialize()`, `Toggle()`, `Update()`, `OnPluginUnload()`, native lifecycle observations, and `IsEnabled`/`Phase` read-only diagnostics.
- Reads ConfigEntries only when creating a decision snapshot in `Stable`.

- [ ] **Step 1: Add failing pure integration tests for final uncovered branches**

Before coordinator code, add tests proving:

```csharp
[Fact] public void Invalid_live_config_pauses_instead_of_clamping_to_a_dangerous_default();
[Fact] public void Reloaded_settings_apply_only_after_the_current_action_reconciles();
[Fact] public void Max_depth_lowered_below_current_floor_pauses_at_the_next_stable_boundary();
[Fact] public void F12_disable_stops_new_actions_but_preserves_unknown_outcome_reconciliation();
```

Run RED and record the expected failure.

- [ ] **Step 2: Bind documented configuration**

Add the exact keys/defaults from Global Constraints under `[NetherAutoClimb]`. Descriptions include ticket `200002`, signal `200001`, `30024`, `40024`, Safe/Risk cancellation, 90/100 semantics, one-ticket Continue, and F11/F12 independence. Existing config auto-reload supplies changes; validate each value and pause on invalid input.

- [ ] **Step 3: Implement the coordinator**

Follow the phase machine exactly:

1. `Update` returns immediately while disabled.
2. Confirm a registered Nether runtime; otherwise disable actions and log once.
3. Poll the one pending native UniTask without blocking Unity's main thread.
4. Reconcile unknown outcomes through `api/nether` only.
5. At `Stable`, capture one settings snapshot and ask status/checkpoint/code/event/route policies for one action.
6. Log the snapshot, candidate audit, selected action, and prediction.
7. Begin the action in the state machine, invoke one native bridge method, and wait.
8. Observe battle/F11/settlement/scene callbacks before another action.
9. On any fail-closed condition, stop scheduling and restore the battle settings lease.

Use `[F12][NetherClimb]` and log only transitions/action boundaries. Never issue a request from a Harmony postfix directly; enqueue an observation and let `Hotkey.Update` drive it on Unity's main thread.

- [ ] **Step 4: Integrate F12 and lifecycle**

`Hotkey.Update` calls `NetherAutoClimbController.Update()` beside existing config/F11 updates. Debounced F12 calls `Toggle()` and logs ON/OFF plus current policy summary. `Plugin.Load` initializes/recoveries the lease before patches can enable automation. `Plugin.Unload` calls `OnPluginUnload()` before base unload. Register the patch exactly once.

- [ ] **Step 5: Run GREEN, complete tests, and Release build**

Run the focused integration tests, all tests, and the RO game build command with `-p:OutputPath=/work/release/nether-auto-climb/`. Remove a prior `release/nether-auto-climb/` output only after resolving and validating its absolute path inside the repository; then rebuild it in a disposable container. Expected artifact:

```text
C:\Users\Eden\PixelAbyssX\AbyssModMod\release\nether-auto-climb\AbyssMod.dll
```

The explicit `OutputPath` must produce this exact path; a different path is a build-verification failure and must be corrected in the command rather than copied afterward.

- [ ] **Step 6: Verify repository scope**

Use Dockerized git to inspect status, diff stat, and diff. Requirements:

- No diff or staging entry for `README.md`.
- `build/` remains untracked and untouched.
- No game-directory file is present in the diff.
- No generated `release/`, `bin/`, or `obj/` artifact is staged.
- Every production behavior has recorded RED/GREEN evidence in the report.
- Full test count is baseline 199 plus the exact new passing count reported by the implementer.

- [ ] **Step 7: Commit Task 8**

Stage only Task 8 source/test/project paths. Commit message:

```text
feat: add F12 nether auto-climb
```

- [ ] **Step 8: Self-review against the full spec**

Read the design once and produce a requirement matrix in the implementation report. For every spec section 1–17, cite the implementing file/tests or mark it as an explicit first-real-run validation boundary. Run a placeholder scan over this plan and a source scan for `TODO`, `NotImplementedException`, swallowed empty catch blocks added by this feature, and raw `api/nether/cancel` calls. Any production placeholder or forbidden call must be removed with a covering RED/GREEN cycle before reporting DONE.

---

## Implementation Reporting Contract

The persistent implementer writes one append-only report at:

```text
.superpowers/sdd/2026-08-07-nether-auto-climb/implementation-report.md
```

For each task it records:

- base/head commit and exact staged paths;
- RED command, relevant expected failure, and why it proves the missing behavior;
- GREEN focused command/output;
- full-suite command, pass/fail/skip count, and warning delta from baseline;
- reverse-confirmed native symbols used by runtime tasks;
- concerns, deviations, and runtime-only validation boundaries;
- self-review findings and fixes.

The short completion response contains only status, commits, final test count, build artifact path, concerns, and report path.

## Post-Implementation Review Sequence

After the implementer reports all tasks complete:

1. Generate one review package from the design/plan commit to implementation HEAD containing commit list, diff stat, and full diff.
2. Resume the fixed spec-reviewer session with the design path, plan path, implementation report, base/head, and review package. It checks requirement coverage, missing/extra behavior, and evidence boundaries read-only.
3. Resume the fixed code-reviewer session in parallel with the same artifacts. It checks correctness, state-machine safety, API idempotency, Unity/main-thread behavior, settings recovery, test quality, and maintainability read-only.
4. Send all verified Critical/Important findings in one batch to the same implementer. The implementer appends fix evidence, runs focused and full tests, builds, and commits fixes.
5. Resume the same reviewer sessions for scoped re-review. Repeat with the same three persistent sessions until both have no Critical/Important findings or a genuine load-bearing blocker is reported.
6. The root agent independently runs fresh Dockerized full tests, RO-game Release build, git-scope verification, and artifact existence checks before claiming convergence.
