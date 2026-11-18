# Server–worker protocol models

TLA+ models of how the server and a worker agree on who is running what: assignment, completion, reconnects, readiness, host re-checks and cancellation. They exist because this protocol fails in interleavings nobody thinks to test — the 2026-11-18 queue drain was one — and TLC explores every interleaving of a small configuration exhaustively.

Run everything with `scripts/check-protocol.sh` (Java and `tla2tools.jar` v1.7.4: the Arch AUR package `tla-tools`, or the script downloads it).

| File | What it models |
| --- | --- |
| `WorkerProtocolLegacy.tla` | The protocol as built before the queue drain. Every safety property is expected to **fail**; it's the record of why the protocol was redesigned. Delete it once the code no longer matches it. |
| `WorkerProtocol.tla` | The redesigned protocol: fenced sessions with reported worker state. Every property must hold, and every design element must be load-bearing. |

## The incident (legacy model)

On 2026-11-18 two workers each failed ~15 queued jobs in milliseconds while really running one job the server had stopped tracking. The server log for worker sst:

```
09:27:23.796 Worker authenticated                              -- reconnect after a silent connection
09:27:23.798 Job completed: 01a1edde-d8d0 - Success           -- the buffered JobCompleted, replayed before Reconnect
09:27:23.812 WRN reconnected without job 01a1ede0, marking failed   -- Reconnect's snapshot predates the assignment the replay triggered
09:27:23.815 Job started: 01a1ede0                             -- the worker runs it anyway
09:27:23.828… failed job …; re-checking its host / diagnostic: Healthy   -- busy rejection → RecheckHost → blocked diagnostic sends Ready → next assignment
```

In `WorkerProtocolLegacy.tla` the root is the same: the reconcile snapshot (`WSyncReconnect`, `wSnap = 0`) is taken before the worker processes the assignment its own completion just triggered — SignalR delivers completions apart from queued invocations — so `SrvReconnectAct` fails a job the worker is about to run. TLC's shortest trace reaches it with the completion sent live early in the re-sync (`WReportCompletion` after `SrvAuthenticate`, before `WSyncLock`); the incident's own path, the completion replayed from the buffer (`WSyncReplaySend` → `SrvJobCompleted` → `SrvDispatchFor`), is the same race one step later. The busy-rejection loop follows from `WOnAssign` rejecting with a Failed `JobCompleted`, `SrvJobCompleted` answering with `RecheckHost`, and `WOnDiagnosticRequest`'s blocked branch sending `Ready`.

The legacy model differs from the code in one way that doesn't matter here: `SrvJobCompleted` acknowledges before its dispatch runs, where the C# completes the invocation after `DispatchAsync` returns; moving the acknowledgement after the dispatch reaches the incident just the same. Not modelled: `MessageBuffer`'s caps, `ReportDiagnostic`, `JobStarted`.

### Legacy action ↔ code

| Action | C# |
| --- | --- |
| `SrvAuthenticate` | `WorkerHub.Authenticate` → `WorkerRepository.ConnectAsync` |
| `SrvReady` | `WorkerHub.Ready` |
| `SrvJobCompleted` | `WorkerHub.JobCompleted` |
| `SrvReconnectRead` / `SrvReconnectAct` | `WorkerHub.Reconnect`, split at its await |
| `SrvDispatchFor` / `SrvDispatchElsewhere` | `JobScheduler.DispatchAsync` |
| `SrvDisconnected` | `WorkerHub.OnDisconnectedAsync` → `WorkerRepository.SetDisconnectedAsync` |
| `SrvCancel` / `SrvCancelSend` | `JobScheduler.CancelJobAsync` |
| `SrvOperatorDiagnostic` | `WorkerDetail.RunDiagnostic` |
| `WOnAssign` | `WorkerService.OnJobAssigned` / `RejectBusyJobAsync` |
| `WOnDiagnosticRequest` / `WDiagnosticDone` | `WorkerService.OnRecheckHost` → `ExecuteDiagnosticAsync` |
| `WReportCompletion` | `WorkerService.ReportCompletionAsync` |
| `WSync*`, `WOnReconnectReply` | `WorkerService.ConnectAndSyncCore` |
| `WOnAck` | `BufferedHubSender.SendOrBufferAsync`, `MessageBuffer.ReplayAsync` |

## The redesigned protocol (`WorkerProtocol.tla`)

1. **Per-worker serialization on the server.** One async lock per worker around connect/supersede, `Resume`, state reports, results, hand-backs, dispatch to that worker, cancel, and the operator's diagnostic, with the fence check inside it. This is what lets the model treat each server handler as one atomic step.
2. **Session epochs without clocks.** A connection is identified by (process instance, a GUID made at start; connection counter, per process), bound into the signed challenge. A later connection of the same process supersedes the current one; so does a process instance not yet superseded, which *retires* the old instance — its handshakes are refused from then on. Anything on a fenced connection is refused with a distinct `Fenced` answer; the worker reconnects rather than counting it as a handshake failure.
3. **Worker state, not events.** The worker reports `{activity, job, diagnostic status}` after every change. The server derives readiness: resumed session ∧ reported idle and healthy ∧ no diagnostic owed ∧ nothing Running for it. There is no `Ready` message.
4. **`Resume`** is the first message on a connection: the worker's state plus its unacknowledged results, sent under `_syncLock`. The server applies the results idempotently — host re-checks and notifications only on a real status transition — then reconciles: a job Running for the worker that it didn't mention is re-queued if the same process had it (it never arrived) and failed as lost only if the process changed; a job the worker reports that the server no longer has gets `CancelJob`.
5. **Hand back, never fail, on collision.** An assignment the worker can't take goes back with `RejectJob` and is re-queued — but never the job the worker reports running.
6. **The server says when it gives up**: failing or cancelling a job the worker may be running sends `CancelJob`.
7. **Diagnostic requests are never folded** into a diagnostic already running: one that arrives during a diagnostic gets a fresh one afterwards.
8. **Edge-triggered dispatch**, as today: every handler that can change readiness or the queue — `Resume`, state reports, results, cancel — runs dispatch afterwards.
9. **The worker acts only on invocations from its current connection.**

### What the model doesn't prove

Two elements are kept although the model shows them saving churn rather than correctness: the counter ordering within a process, and instance retirement. Without either, a late handshake can take the session from the worker's live connection; the live connection is then fenced, reconnects, and takes it back, and heartbeats and `Resume` repair everything else. In a bounded run that churn runs out the connection budget, which is all TLC can show. They stay because they're cheap and because without retirement two processes sharing a key would take turns superseding each other forever.

### Implementation obligations

The model takes these for granted; the C# has to make them true.

- **Invocation epochs.** Every invocation carries its connection's epoch, and the worker drops one whose epoch isn't current once it holds its lock. (An `AssignJob` already queued in the client for an old connection can otherwise run after a `Resume` that said "idle", and `AltNoInvocationEpoch` shows what follows.)
- **Ordered state reports.** A state change and its report happen under one lock, so reports reach the server in the order the changes happened; or reports carry a sequence number and the server keeps the highest. The model relies on per-connection FIFO between results and the state after them.
- **Locking.** The global dispatch lock is taken before the per-worker lock, never inside it, and dispatch runs after a handler releases the worker's lock. Sends to the worker don't block under the lock on a stalled connection.
- **Cancel** looks up the job's owner, takes that worker's lock, and re-reads the owner before acting, retrying if it changed.
- **Heartbeat** is an invocation answered "ok" or "fenced", not today's fire-and-forget send.
- **`JobStarted`** is removed, or fenced like every other call: today it writes Running with no worker and no authentication check.
- **Results carry an assignment id**, so two hand-backs of the same job, or a stale one, can't be confused; a requeue clears the job's worker and start time.
- **Results are kept until acknowledged**, separately from the lossy log buffer. They don't survive a process restart, which Resume treats as a different instance.
- **Server restart** drops every connection; workers resume and rebuild the session state. The one thing to persist is whether a host re-check is owed, so a restart doesn't skip it.

Out of scope: sweeping Running jobs after a server restart, fencing artifact uploads (plain HTTP), log streaming, the bundle-update drain, priorities beyond one competitor, the competitor being cancelled.

### Modelling choices

- One worker W in full, plus an optional competitor that takes and finishes queued jobs, so double runs are observable. It's off in most configurations because it would mask a stranded W.
- Connections may drop, go silent while both ends think they're up, be timed out by the server while working, or have their handshakes arrive late and out of order. The worker may restart. Within a connection, replies may overtake queued invocations: the SignalR client's symbols (`invocationMessageChannel`) are consistent with invocations running on their own loop, which is an assumption, not a reading of its code.
- Diagnostics come back Healthy or Unhealthy (bounded), with the retry loop.
- `MaxQueue` bounds how far the worker may run ahead of the server on one connection; it's a bound on the model, not a protocol rule.

## Properties

| Property | Meaning |
| --- | --- |
| `NoUntrackedRun` | The server never silently gives up on a job the worker is still running. |
| `NoBusyRejection` (legacy) / `NoUnexplainedFailure` | No job is failed because the worker was busy when it arrived; in the redesign, a job is Failed only because the worker's run of it failed or its process died. |
| `NoSpuriousFailure` | A job is failed as lost only when the process it was assigned to has died and the live process doesn't have it. |
| `NoDoubleRun` | Two workers never run the same job (redesign). |
| `NotIncident` | The worker never runs a job the server failed as lost while the server considers it ready for more. |
| `AllJobsSettle` | Every job eventually reaches a final status (redesign, under fairness). |
| `WorkerNotStranded` | A connected, idle, healthy worker is eventually seen by the server as ready (redesign, under fairness). |

`NoSpuriousFailure` and `NotIncident` only bite where a job can be failed as lost, which in the redesign takes a process restart; the restart configurations exercise them, and the witness `NeverLosesAJob` checks that they do. `NeverHandsBack` likewise checks that hand-back is exercised.

## Results

`scripts/check-protocol.sh`, TLC 1.7.4 on 32 cores: about 46 minutes in full, about 17 with `quick` (which skips `reorder`, `restart1`, `restart` and the checks that need them).

| Check | Result |
| --- | --- |
| Legacy: `NoUntrackedRun`, `NoBusyRejection`, `NoSpuriousFailure`, `NotIncident` | all violated, as expected |
| Redesign: base, stall, false timeout, cancel, operator diagnostic, unhealthy retry, competitor, cancel + operator diagnostic, competitor + operator diagnostic, out-of-order handshakes (3 connections), restart with 1 job | every safety property and both liveness properties hold |
| Redesign: restart with 2 jobs | safety holds; liveness at this size doesn't finish in reasonable time |
| Witnesses `NeverHandsBack` (operator diagnostic), `NeverLosesAJob` (restart, 1 job) | violated, as expected: the guarded paths are taken |

Each design element replaced by its alternative (deadlock checking off, so a bounded run exhausting its reconnects doesn't count):

| Alternative | Configuration | Breaks |
| --- | --- | --- |
| `AltReconcileBeforeResume` — a connection takes work as soon as it's accepted | base | `NoUntrackedRun` (the incident's shape) |
| `AltFailCollision` — a refused assignment is failed | operator diagnostic | `NoUnexplainedFailure` |
| `AltNoHandBackGuard` — a hand-back requeues the job W reports running | competitor + operator diagnostic | `NoDoubleRun` |
| `AltFailUndelivered` — Resume fails a job the same process never received | base | `NoSpuriousFailure` |
| `AltNoAbortOnResume` — Resume doesn't stop a job the server gave up | cancel | `NoUntrackedRun` |
| `AltFoldDiagnostic` — a request during a diagnostic is folded into it | operator diagnostic | liveness |
| `AltReadyFromTransitions` — readiness raised by completion transitions | cancel | liveness |
| `AltStateDoesNotDispatch` — a state report doesn't trigger dispatch | base | liveness |
| `AltNoInvocationEpoch` — W acts on an invocation from an older connection | stall | `NoUntrackedRun` |
| `AltClockEpochs` — clock epochs step back across a restart | restart, 1 job | liveness (the restarted process is fenced out for good) |
