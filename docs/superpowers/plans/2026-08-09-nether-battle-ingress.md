# Nether Battle Ingress Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make F12 follow the packaged map-to-battle scene lifecycle and enter battle settlement without replaying a request or misclassifying expected FloorSelection teardown.

**Architecture:** A pure ingress coordinator waits for the exact captured StartQuest task and then performs one existing GET-only reconciliation. The production controller owns phase transitions; the IL2CPP bridge only captures and polls exact native evidence.

**Tech Stack:** C#/.NET 6 plugin, .NET 8 xUnit tests, BepInEx IL2CPP interop, HarmonyX, disposable Docker SDK containers.

## Global Constraints

- Use `docker run --rm` for every test, build, and reverse-engineering command.
- Mount `dotabyss_x_cl` as `/game:ro` and `reverse_out` as `/reverse:ro`.
- Never issue, replay, cancel, or synthesize a Nether start/mutation request from ingress code.
- Do not write or deploy into the game directory.
- Preserve existing user-owned working-tree changes, `README.md`, and `build/`.
- Do not commit or push this live-debug iteration.

---

### Task 1: Correct packaged read-only binding

**Files:**
- Modify: `AbyssMod/Services/NetherReadOnlyReconcileNativeBinding.cs`
- Modify: `AbyssMod.Tests/NetherReadOnlyReconcileCoordinatorTests.cs`
- Modify: `AbyssMod.Tests/NetherLifecycleInteropBindingsTests.cs`

**Interfaces:**
- Consumes: packaged `Project.User.NetherDataStore.SyncNetherDataAsync`.
- Produces: exact descriptor parameter `Il2CppSystem.Threading.CancellationToken`.

- [x] Add a packaged `Project.dll` characterizer that prints the real method shape on failure.
- [x] Run the characterizer and retain the RED evidence showing the current System token cannot resolve.
- [x] Change the descriptor to the exact Il2Cpp token type; runtime construction already imports `Il2CppSystem.Threading`.
- [x] Run the lifecycle and read-only focused suites GREEN.

### Task 2: Add the pure battle-ingress coordinator

**Files:**
- Create: `AbyssMod/Services/NetherBattleIngressCoordinator.cs`
- Create: `AbyssMod.Tests/NetherBattleIngressCoordinatorTests.cs`
- Modify: `AbyssMod.Tests/AbyssMod.Tests.csproj`

**Interfaces:**
- Consumes: `INetherBattleIngressDriver.PollBattleStart()` and `INetherReadOnlyReconcileDriver`.
- Produces: `NetherBattleIngressStep` values for awaiting native start, reconciling, entered battle, wrong target, binding unavailable, canceled, and faulted.

- [ ] Write RED tests proving a missing/Pending StartQuest performs zero GET calls.
- [ ] Write RED tests proving Succeeded performs exactly one GET and requires the selected floor plus `Battle` status.
- [ ] Write RED tests proving canceled/faulted/wrong-target results are terminal and never replay ingress.
- [ ] Implement the minimum stateful coordinator with one immutable action/pre-snapshot and reset semantics.
- [ ] Run only `NetherBattleIngressCoordinatorTests` GREEN.

### Task 3: Capture exact runtime StartQuest evidence

**Files:**
- Modify: `AbyssMod/Services/NetherRuntimeBridge.cs`
- Modify: `AbyssMod.Tests/NetherAutoClimbControllerTestStubs.cs`
- Modify: `AbyssMod.Tests/NetherAutoClimbControllerEndToEndTests.cs`

**Interfaces:**
- Consumes: the existing exact Harmony postfix for `NetherAPIService.StartQuestAsync`.
- Produces: `PollBattleStart()` that distinguishes missing registration, Pending, Succeeded, Canceled, and Faulted.

- [ ] Add an expected-start latch and `NetherNativeWaitGate` when `BeginFloorParent` receives a combat projection.
- [ ] Correlate only the next exact StartQuest task to that latch and log whether capture was expected.
- [ ] Poll the task without retry/cancel; clear it only on a terminal result.
- [ ] Reset the latch on rejected invocation, plugin clear, or terminal ingress failure.
- [ ] Add fake-runtime controls for delayed registration and task terminal state.

### Task 4: Wire the native battle scene handoff into the controller

**Files:**
- Modify: `AbyssMod/Services/NetherAutoClimbModels.cs`
- Modify: `AbyssMod/Services/NetherAutoClimbStateMachine.cs`
- Modify: `AbyssMod/Services/NetherAutoClimbController.cs`
- Modify: `AbyssMod.Tests/NetherAutoClimbStateMachineTests.cs`
- Modify: `AbyssMod.Tests/NetherAutoClimbControllerEndToEndTests.cs`

**Interfaces:**
- Consumes: `NetherBattleIngressCoordinator` and the combat `BattleProjection` stored before native selection.
- Produces: `AwaitingBattleSceneHandoff` and immediate creation of `BattleSettlement` after authoritative ingress.

- [ ] Add RED state tests for enabled and F12-off drain through the new phase.
- [ ] Add a RED production E2E matching the live order: parent terminal, no FloorSelection, delayed StartQuest, one GET, BattleSettlement.
- [ ] On direct combat parent terminal, start ingress rather than generic reconciliation.
- [ ] Pump ingress before the ordinary floor-controller gate and accept expected teardown in all battle phases.
- [ ] On authoritative Battle snapshot, settle SelectFloor and immediately call the existing `BeginBattleWait`.
- [ ] Treat canceled/faulted/wrong-target ingress as named fail-closed terminal outcomes.

### Task 5: Wait safely for battle settings and preserve lease ownership

**Files:**
- Modify: `AbyssMod/Services/NetherAutoClimbController.cs`
- Modify: `AbyssMod.Tests/NetherAutoClimbControllerEndToEndTests.cs`
- Modify: `AbyssMod.Tests/NetherBattleSettingsLeaseControllerLifecycleTests.cs`

**Interfaces:**
- Consumes: `IsExactAccessorRegistered`, `BlocksRoute`, and the existing lease enter/exit operations.
- Produces: an accessor-wait boundary that performs no settings write until the exact owner exists.

- [ ] Add RED tests showing expected combat FloorSelection teardown performs no `OnLeaveNether` restore.
- [ ] Add RED tests showing a clean missing accessor waits, then acquires exactly once after registration.
- [ ] Keep persisted/recovery/faulted lease states as hard blockers.
- [ ] Add bounded, deduplicated diagnostics for accessor wait and acquisition.

### Task 6: Verification and release artifact

**Files:**
- Output: `release/nether-auto-climb/Release/net6.0/AbyssMod.dll`

**Interfaces:**
- Consumes: all production and test changes above.
- Produces: one testable DLL plus SHA-256 and deployment instructions.

- [ ] Run battle-ingress, controller E2E, state, lease, lifecycle-binding, read-only, and settlement focused suites in fresh Docker containers.
- [ ] Run the complete xUnit project in a fresh Docker container.
- [ ] Build Release with `/game:ro`, `/reverse:ro`, and `BaseOutputPath=/work/release/nether-auto-climb/`.
- [ ] Run `git diff --check`, inspect status, and verify the release DLL hash.
- [ ] Report the exact expected logs and the fail-closed logs the user should send if native StartQuest remains Pending.
