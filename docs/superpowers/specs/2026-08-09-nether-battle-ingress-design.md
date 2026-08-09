# Nether Battle Ingress Design

## Goal

Make an F12-selected Nether combat floor survive the native map-to-battle scene handoff, prove the exact `StartQuestAsync` task and authoritative server state, and enter the existing battle-settlement flow without replaying, canceling, or synthesizing a request.

## Confirmed native order

The packaged client and RO reverse artifacts establish this order:

1. F12 invokes the existing `SubViewController.OnFloorClickedEventAsync(floorLevel, floorIndex)` parent.
2. `NetherBattleFloorEventFlow` calls `NetherUtility.TransitionNetherBattleAsync`.
3. `TransitionNetherBattleAsync` starts `PreRequestStartAsync(...).Forget()`, calls `SceneHistory.ChangeScene`, and completes.
4. The old FloorSelection controller terminates.
5. The new battle scene calls `NetherAPIService.StartQuestAsync`; the existing Harmony postfix captures its returned UniTask.

Therefore parent completion and FloorSelection teardown are expected ingress evidence. They are not proof that the server selected the floor, and they are not “leave Nether” events.

## Architecture

Add a pure `NetherBattleIngressCoordinator`. It starts only for a safety-approved direct combat `SelectFloor` action carrying `BattleProjection`. Once the floor parent is terminal, it waits for the exact captured native `StartQuestAsync` task. A successful task permits exactly one existing GET-only refresh; the coordinator accepts only an action-specific snapshot whose floor and `Battle` status match the pending selection. It never invokes a start or mutation endpoint.

The runtime bridge owns a bounded missing-registration gate for the expected StartQuest task. A captured Pending task remains observable without replay or cancellation. Canceled, faulted, missing, or wrong-target evidence pauses F12 with a named diagnostic.

The controller adds an `AwaitingBattleSceneHandoff` phase. This phase and the existing battle phases are legal while FloorSelection is absent. On an authoritative ingress result, the controller settles the `SelectFloor` action and immediately creates the existing `BattleSettlement` action before the next frame can apply the ordinary no-FloorSelection gate.

## Battle settings

The BottomRight battle-settings accessor does not exist during map teardown or early loading. A clean empty/restored lease waits for the exact accessor instead of pausing immediately. Once registered, the existing lease forces native Auto and maximum speed. Persisted, forced, recovery-pending, or faulted leases remain hard blockers.

FloorSelection termination during a named battle ingress/battle phase must not call `OnLeaveNether`; normal Continue, Result, F12-off, plugin unload, and genuine non-battle teardown keep their existing restoration behavior.

## Diagnostics

Detailed logging records:

- combat parent terminal and handoff phase entry;
- expected FloorSelection teardown;
- StartQuest registration wait, capture, Pending, Succeeded, Canceled, or Faulted;
- GET-only begin, pending, terminal, and authoritative snapshot;
- settings-accessor wait/acquire;
- every fail-closed reason without issuing a retry.

The missing StartQuest registration is bounded by the existing native wait-gate convention. Once the exact task exists, Pending is not converted into a retry or synthetic timeout.

## Tests and acceptance

- A packaged-interop characterizer must resolve `SyncNetherDataAsync(Il2CppSystem.Threading.CancellationToken)` exactly.
- Coordinator tests must prove no GET before StartQuest succeeds, one GET after success, and zero retries after cancel/fault/wrong target.
- A production-controller E2E must reproduce parent terminal → FloorSelection absent → delayed StartQuest → GET → `BattleSettlement` while never entering `Paused`.
- Existing popup, Continue, battle settlement, F12-off drain, full-suite, and RO-game Release build tests must remain green.
- Output is written only to `release/nether-auto-climb`; `/game` and `/reverse` stay read-only.
