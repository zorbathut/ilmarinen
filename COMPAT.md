# Compatibility register

Code kept only so the server can still talk to workers on older worker protocol versions (`WorkerProtocol`; see CLAUDE.md, "Protocol compatibility"). Each entry lives under the version that retires it: once `WorkerProtocol.MinSupportedVersion` reaches that version, no worker that needs the code can connect, so it goes, along with the test that sends it an older worker's message.

`CompatRegisterTests` fails once the minimum reaches a heading here, naming what to delete. Delete the code, its test and the entry, then the heading once it's empty.

Compatibility code is kept for a month, not until every worker has caught up. A heading ends with the date its first entry was written, and a month after that date its entries are due: propose raising the minimum past them, naming any worker that still speaks the old version and so needs rebuilding or updating.

An entry names the code and says what it does for older workers:

```
## Until v3 (added 2026-12-01)

- `WorkerHub.ReportOutcome`: treats a missing `DiskUse` as unknown, for workers before v3. Test: `DiskUseTests.AnOutcomeWithoutDiskUse_IsStored`.
```

## Until v2 (added 2026-11-23)

- `WorkerReadiness.EffectiveHold`: keeps a worker's in-use hold even in `WorkerMode.EvenWhileInUse` when it speaks v1, which hands back any assignment while it is held (it doesn't know `JobAssignment.TakeWhileInUse`). Going with it:
  - the version check and `WorkerReadiness.TakesJobsWhileInUseFromVersion`;
  - `WorkerView.SpeaksTooOldAProtocolToTakeJobsWhileInUse` and the label it gives (`WorkerBadge.GetStatusLabel`);
  - the README sentence on workers older than protocol v2;
  - the `protocolVersion` parameters of `DirectedWorker.ReadyAsync`, `ConnectAsync` and `ConnectAsCounterAsync`, and of `WorkerHoldTests.InUseAsync`, unless something else uses them by then.
  - Tests: `WorkerHoldTests.AnOlderWorkerInUse_StaysHeld_EvenWhileInUse`, `WorkerBadgeTests.AWorkerTooOldToTakeJobsWhileInUse_SaysWhyTheOverrideDoesNothing`, and the `Older` rows of `WorkerReadinessTests.EffectiveHold_WaivesOnlyInUse_OnlyEvenWhileInUse_AndOnlyForAWorkerThatCanTakeTheJob`.
- `LogStreamService.ProcessChunkAsync` and `WorkerHub.StreamMetrics`: take a log or metrics chunk that names no assignment (`LogChunk.AssignmentId`, `JobMetricsChunk.AssignmentId`) as the current run's, for workers before v2, which don't send it. With it go the "names no assignment" branches, and the two members become `required`. Test: `JobLogRunTests.OutputNamingNoRun_FromAWorkerBeforeV2_CountsAsTheCurrentRuns`.
