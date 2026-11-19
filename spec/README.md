# Server–worker protocol models

TLA+ models of how the server and a worker agree on who is running what: assignment, completion, reconnects, readiness, host re-checks and cancellation. They exist because this protocol fails in interleavings nobody thinks to test — the 2026-11-18 queue drain was one — and TLC explores every interleaving of a small configuration exhaustively.

Run everything with `scripts/check-protocol.sh` (Java and `tla2tools.jar` v1.7.4: the Arch AUR package `tla-tools`, or the script downloads it).

`WorkerProtocol.tla` models the protocol as built: fenced sessions with reported worker state. Every property must hold, and every design element must be load-bearing (replacing it with its realistic alternative must break a property).

## Why it exists

On 2026-11-18 two workers each failed ~15 queued jobs in milliseconds while really running one job the server had stopped tracking. A worker reconnected mid-job; its buffered `JobCompleted` was replayed before its `Reconnect`, dispatched the next job, and `Reconnect`'s snapshot — taken before the worker had processed that assignment — failed it as lost while the worker ran it. Busy rejections, `RecheckHost` and a blocked diagnostic's `Ready` then looped the rest of the queue into Failed. A model of that protocol (`WorkerProtocolLegacy.tla`, in git history at ad74449) breaks every safety property below; this one is what replaced it. `WorkerReconnectTests` reproduces the incident against a real worker.

## The protocol

1. **Per-worker serialization on the server.** One async lock per worker around connect/supersede, `Resume`, state reports, results, hand-backs, dispatch to that worker, cancel, and the operator's diagnostic, with the fence check inside it. This is what lets the model treat each server handler as one atomic step.
2. **Session epochs without clocks.** A connection is identified by (process instance, a GUID made at start; connection counter, per process), bound into the signed challenge. A later connection of the same process supersedes the current one; so does a process instance not yet superseded, which *retires* the old instance — its handshakes are refused from then on. Anything on a fenced connection is refused with a distinct `Fenced` answer; the worker reconnects rather than counting it as a handshake failure.
3. **Worker state, not events.** The worker reports `{activity, job, diagnostic status}` after every change. The server derives readiness: resumed session ∧ reported idle and healthy ∧ no diagnostic owed ∧ nothing Running for it. There is no `Ready` message.
4. **`Resume`** is the first message on a connection: the worker's state plus its unacknowledged results, sent under the worker's state lock. The server applies the results idempotently — host re-checks and notifications only on a real status transition — then reconciles: a job Running for the worker that it didn't mention is re-queued if the same process had it (it never arrived) and failed as lost only if the process changed; a job the worker reports that the server no longer has gets `CancelJob`.
5. **Hand back, never fail, on collision.** An assignment the worker can't take goes back with `RejectJob` and is re-queued — but never the job the worker reports running.
6. **The server says when it gives up**: failing or cancelling a job the worker may be running sends `CancelJob`.
7. **Diagnostic requests are never folded** into a diagnostic already running: one that arrives during a diagnostic gets a fresh one afterwards.
8. **Edge-triggered dispatch**, as today: every handler that can change readiness or the queue — `Resume`, state reports, results, cancel — runs dispatch afterwards.
9. **The worker acts only on invocations from its current connection.**

### What the model doesn't prove

Two elements are kept although the model shows them saving churn rather than correctness: the counter ordering within a process, and instance retirement. Without either, a late handshake can take the session from the worker's live connection; the live connection is then fenced, reconnects, and takes it back, and heartbeats and `Resume` repair everything else. In a bounded run that churn runs out the connection budget, which is all TLC can show. They stay because they're cheap and because without retirement two processes sharing a key would take turns superseding each other forever.

### Implementation obligations

The model takes these for granted; this is where the C# makes them true.

- **Handlers are atomic.** `WorkerSessions` holds one async lock per worker; every hub handler that reads or changes a worker's state, dispatch to it, cancel and the operator's diagnostic run under it, with the fence check inside.
- **Invocations from the current connection only.** The worker builds a fresh `HubConnection` per transport, and registers its handlers on that connection alone, so an invocation is always from the session it arrived on (`WorkerService.HubSession`). Handlers post to the session's inbox, processed in order; those that change the worker's state wait for the state lock (cancel doesn't, as `WOnCancel` needs no lock) — so an invocation that arrives during Resume waits rather than being dropped — and act only if their session is still current and resumed.
- **Nothing changes during Resume.** The worker holds its state lock from the Resume snapshot until the answer (`WorkerService.SyncAsync`).
- **Ordered reports.** A state change and the report of it happen under the worker's state lock, onto the session's single ordered outbox; reports also carry a sequence number, and the server keeps the highest.
- **Ordered sends.** `WorkerMessenger` keeps one queue per connection, written under the worker's lock and drained by one task, so an `AssignJob` and the `CancelJob` after it arrive in that order, and no lock waits on a stalled connection.
- **Retirement** keeps the last 16 superseded instances per worker, in memory; a server restart forgets them, which costs nothing but a possible round of churn.
- **Locking.** The global dispatch lock is taken before a worker's lock, never inside it; handlers dispatch after releasing the worker's lock.
- **Heartbeat** is an invocation answered Ok or Fenced, with a timeout; either failure starts a new session.
- **Outcomes carry an assignment id** (`Job.AssignmentId`), so a stale completion or hand-back can't touch a later assignment; a requeue clears the job's worker, start time, assignment and instance. The assignment id is what implements the model's hand-back guard.
- **Outcomes are kept until acknowledged** (`WorkerProtocolState`), apart from the lossy log buffer. They don't survive a process restart, which Resume treats as a different instance (`Job.WorkerInstanceId`).
- **Every exit from a job records an outcome**, atomically with going idle, so the same instance never comes back idle without having said what became of its job.
- **Server restart** drops every connection; workers resume and rebuild the session state. Whether a diagnostic is owed is persisted (`Worker.DiagnosticOwed`), set in the same transaction as the Failed that caused it.
- **Errors as values.** Calls answer `SessionAnswer` rather than throwing, so the worker tells a fenced session from a server fault without parsing messages.

### Model action ↔ code

| Action | C# |
| --- | --- |
| `SrvConnect` | `WorkerHub.Connect` + `Authenticate` → `WorkerSessions.TryStart` (`SessionEpoch.Supersedes`) |
| `SrvResume` | `WorkerHub.Resume` |
| `SrvState` | `WorkerHub.ReportState` |
| `SrvResult` | `WorkerHub.ReportOutcome` → `JobScheduler.ApplyOutcomeAsync` |
| `SrvHeartbeat` | `WorkerHub.Heartbeat` |
| `SrvDispatch` | `JobScheduler.DispatchAsync` (readiness: `WorkerReadiness.IsReady`) |
| `SrvCancel` | `JobScheduler.CancelJobAsync` |
| `SrvOperatorDiagnostic` | `JobScheduler.RequestDiagnosticAsync` |
| `SrvDisconnected` | `WorkerHub.OnDisconnectedAsync` |
| `WConnect`, `WOnReply` | `WorkerService.OpenSessionAsync`, `SyncAsync`, `SendAsync` |
| `WOnAssign`, `WReportCompletion` | `WorkerService.OnJobAssignedAsync`, `FinishJobAsync` (`WorkerProtocolState.Assign`, `FinishJob`) |
| `WOnDiagnosticRequest`, `WDiagnosticStart`, `WDiagnosticDone` | `WorkerService.OnDiagnosticRequestedAsync`, `TryStartDiagnosticAsync`, `RunDiagnosticAsync` |
| `WHeartbeat`, `WAbandon` | `WorkerService.KeepAliveAsync`, `EndSession` |

Out of scope: sweeping Running jobs after a server restart, fencing artifact uploads (plain HTTP), log streaming (which needs only a current connection), the bundle-update drain, priorities beyond one competitor, the competitor being cancelled.

### Modelling choices

- One worker W in full, plus an optional competitor that takes and finishes queued jobs, so double runs are observable. It's off in most configurations because it would mask a stranded W.
- Connections may drop, go silent while both ends think they're up, be timed out by the server while working, or have their handshakes arrive late and out of order. The worker may restart. Within a connection, replies may overtake queued invocations: the SignalR client's symbols (`invocationMessageChannel`) are consistent with invocations running on their own loop, which is an assumption, not a reading of its code.
- Diagnostics come back Healthy or Unhealthy (bounded), with the retry loop.
- `MaxQueue` bounds how far the worker may run ahead of the server on one connection; it's a bound on the model, not a protocol rule.

## Properties

| Property | Meaning |
| --- | --- |
| `NoUntrackedRun` | The server never silently gives up on a job the worker is still running. |
| `NoUnexplainedFailure` | A job is Failed only because the worker's run of it failed or its process died — never because the worker was busy when it arrived. |
| `NoSpuriousFailure` | A job is failed as lost only when the process it was assigned to has died and the live process doesn't have it. |
| `NoDoubleRun` | Two workers never run the same job. |
| `NotIncident` | The worker never runs a job the server failed as lost while the server considers it ready for more. |
| `AllJobsSettle` | Every job eventually reaches a final status (under fairness). |
| `WorkerNotStranded` | A connected, idle, healthy worker is eventually seen by the server as ready (under fairness). |

`NoSpuriousFailure` and `NotIncident` only bite where a job can be failed as lost, which takes a process restart; the restart configurations exercise them, and the witness `NeverLosesAJob` checks that they do. `NeverHandsBack` likewise checks that hand-back is exercised.

## Results

`scripts/check-protocol.sh`, TLC 1.7.4 on 32 cores: about 46 minutes in full, about 17 with `quick` (which skips `reorder`, `restart1`, `restart` and the checks that need them).

| Check | Result |
| --- | --- |
| Base, stall, false timeout, cancel, operator diagnostic, unhealthy retry, competitor, cancel + operator diagnostic, competitor + operator diagnostic, out-of-order handshakes (3 connections), restart with 1 job | every safety property and both liveness properties hold |
| Restart with 2 jobs | safety holds; liveness at this size doesn't finish in reasonable time |
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
