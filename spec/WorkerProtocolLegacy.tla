------------------------ MODULE WorkerProtocolLegacy ------------------------
\* The server-worker job protocol as built before the 2026-11-18 queue drain: assignment, completion, buffered replay after a reconnect, the Reconnect reconciliation, readiness, host re-checks and cancellation, for one worker.
\* Every safety property below is expected to fail. See README.md for what is modelled and which C# handler each action mirrors.
EXTENDS Naturals, Sequences, FiniteSets, TLC

CONSTANTS
    NumJobs,            \* jobs, all queued at the start; job ids 1..NumJobs, oldest first
    MaxConns,           \* hub connections the worker may open, the first included
    MaxStalls,          \* connections that may go silent while both ends think they are up
    MaxRestarts,        \* worker process restarts
    MaxCancels,         \* user cancels
    MaxOperatorDiags    \* "Run diagnostic" clicks on the worker page

Jobs == 1..NumJobs
NoJob == 0
Conns == 1..MaxConns
Outcomes == {"Success", "Failed", "Cancelled"}
Statuses == {"Queued", "Running"} \cup Outcomes

Min(S) == CHOOSE x \in S : \A y \in S : x <= y
Range(s) == {s[i] : i \in DOMAIN s}
SetToSeq(S) == CHOOSE f \in [1..Cardinality(S) -> S] : \A i, k \in DOMAIN f : i # k => f[i] # f[k]

\* Worker -> server messages. Every record has the same fields so TLC can compare them.
NoMsg == [t |-> "none", j |-> NoJob, s |-> "none"]
MAuth == [t |-> "Authenticate", j |-> NoJob, s |-> "none"]
MReady == [t |-> "Ready", j |-> NoJob, s |-> "none"]
MReconnect(snap) == [t |-> "Reconnect", j |-> snap, s |-> "none"]
MJobCompleted(j, s) == [t |-> "JobCompleted", j |-> j, s |-> s]

\* Server -> worker invocations
IAssign(j) == [t |-> "AssignJob", j |-> j]
ICancel(j) == [t |-> "CancelJob", j |-> j]
IRecheck == [t |-> "RecheckHost", j |-> NoJob]
IRunDiagnostic == [t |-> "RunDiagnostic", j |-> NoJob]

\* Server -> worker completions of the worker's invocations. SignalR delivers these apart from queued invocations, so they can overtake them.
RAck(m, ok) == [t |-> "Ack", m |-> m, ok |-> ok, e |-> NoJob]
RReconnect(ok, e) == [t |-> "ReconnectReply", m |-> NoMsg, ok |-> ok, e |-> e]

VARIABLES
    \* Server: database
    status,           \* job -> status
    \* Server: in-memory worker state (WorkerRepository)
    mapped,           \* the worker's current connection (_workerToConnection), 0 if none
    authed,           \* connections mapped to the worker (_connectionToWorker)
    ready,            \* _readyWorkers
    srvDown,          \* connections the server has seen disconnect
    \* Server: handlers suspended at an await
    reconPhase,       \* connection -> "idle" | "read": Reconnect between reading the running job and acting on it
    reconRead, reconSnap,
    dispatchPending,  \* connections whose handler is waiting on DispatchAsync
    cancelSends,      \* <<job, connection>>: CancelJobAsync has cancelled the job and has yet to send CancelJob
    \* Channels
    toServer,         \* connection -> Seq of worker messages, FIFO
    toWorker,         \* connection -> Seq of server invocations, FIFO
    replies,          \* connection -> set of completions, in any order
    stalled,          \* connections that silently stopped delivering
    \* Worker
    wConn, wUp, connsUsed,
    wAct,             \* "Idle" | "Job" | "Diag"
    wJob,             \* running job, NoJob if none
    wJobDone,         \* "none", or the outcome of a finished job waiting to be reported
    wCancelReq,       \* the running job has been told to stop
    wDiagCached,      \* _lastDiagnostic is set (always Healthy here)
    wLock,            \* _syncLock: "free" | "sync" | "report"
    wReportMsg,       \* the JobCompleted ReportCompletionAsync is waiting on, holding the lock
    wBuffer,          \* MessageBuffer
    wPending,         \* <<connection, message>> sent through BufferedHubSender, not yet acknowledged
    wSync,            \* "none" | "auth" | "lock" | "replay" | "snap" | "await" | "post"
    wReplayPending, wSnap, wExpected,
    \* Budgets
    stallsUsed, restartsUsed, cancelsUsed, opDiagsUsed,
    \* Ghosts, for the properties
    lostFailed,       \* jobs Reconnect failed as lost
    busyRejections,   \* why the worker refused an assignment by failing it: "Job" | "Diag" | "Redelivery"
    deadConns,        \* connections opened by worker processes that have since died
    spurious          \* the server failed as lost a job that was still alive

srvVars == <<status, mapped, authed, ready, srvDown, reconPhase, reconRead, reconSnap, dispatchPending, cancelSends>>
chanVars == <<toServer, toWorker, replies, stalled>>
wVars == <<wConn, wUp, connsUsed, wAct, wJob, wJobDone, wCancelReq, wDiagCached, wLock, wReportMsg, wBuffer, wPending, wSync, wReplayPending, wSnap, wExpected>>
budgetVars == <<stallsUsed, restartsUsed, cancelsUsed, opDiagsUsed>>
ghostVars == <<lostFailed, busyRejections, deadConns, spurious>>
vars == <<srvVars, chanVars, wVars, budgetVars, ghostVars>>

-----------------------------------------------------------------------------
\* Helpers

RunningJobs == {j \in Jobs : status[j] = "Running"}
\* JobRepository.GetRunningJobForWorkerAsync
RunningJob == IF RunningJobs = {} THEN NoJob ELSE Min(RunningJobs)
Queued == {j \in Jobs : status[j] = "Queued"}

\* JobRepository.UpdateStatusAsync's finality: Success and Failed are final; Cancelled gives way only to Success or Failed
StatusAfter(j, s) ==
    IF status[j] \in {"Success", "Failed"} \/ (status[j] = "Cancelled" /\ s \notin {"Success", "Failed"})
    THEN status
    ELSE [status EXCEPT ![j] = s]

\* A report on how j went from the live worker process: held by it, or in flight on one of its connections the server will still accept. A dead process's reports are what a restart costs.
ReportOutstanding(j) ==
    \E m \in Range(wBuffer) \cup {p[2] : p \in wPending} \cup {wReplayPending} \cup UNION {Range(toServer[c]) : c \in authed \ deadConns} :
        m.t = "JobCompleted" /\ m.j = j

\* The worker is running j, or will be once an assignment on its live connection reaches it, or will report on it
JobAlive(j) ==
    \/ wAct = "Job" /\ wJob = j
    \/ wUp /\ IAssign(j) \in Range(toWorker[wConn])
    \/ ReportOutstanding(j)

Listening(c) == wUp /\ c = wConn /\ c \notin stalled
ServerHears(c) == c \notin stalled /\ c \notin srvDown

-----------------------------------------------------------------------------
Init ==
    \* Connected, synced, idle, ready, every job queued
    /\ status = [j \in Jobs |-> "Queued"]
    /\ mapped = 1
    /\ authed = {1}
    /\ ready = {1}
    /\ srvDown = {}
    /\ reconPhase = [c \in Conns |-> "idle"]
    /\ reconRead = [c \in Conns |-> NoJob]
    /\ reconSnap = [c \in Conns |-> NoJob]
    /\ dispatchPending = {}
    /\ cancelSends = {}
    /\ toServer = [c \in Conns |-> <<>>]
    /\ toWorker = [c \in Conns |-> <<>>]
    /\ replies = [c \in Conns |-> {}]
    /\ stalled = {}
    /\ wConn = 1
    /\ wUp = TRUE
    /\ connsUsed = 1
    /\ wAct = "Idle"
    /\ wJob = NoJob
    /\ wJobDone = "none"
    /\ wCancelReq = FALSE
    /\ wDiagCached = TRUE
    /\ wLock = "free"
    /\ wReportMsg = NoMsg
    /\ wBuffer = <<>>
    /\ wPending = {}
    /\ wSync = "none"
    /\ wReplayPending = NoMsg
    /\ wSnap = NoJob
    /\ wExpected = NoJob
    /\ stallsUsed = 0
    /\ restartsUsed = 0
    /\ cancelsUsed = 0
    /\ opDiagsUsed = 0
    /\ lostFailed = {}
    /\ busyRejections = {}
    /\ deadConns = {}
    /\ spurious = FALSE

-----------------------------------------------------------------------------
\* Server

\* JobScheduler.DispatchAsync: hand the oldest queued job to a ready worker connection, unless the database says it is already running one
DoDispatch ==
    \/ /\ ready \cap authed = {}
       /\ UNCHANGED <<status, ready, toWorker>>
    \/ \E c \in ready \cap authed :
        IF RunningJob # NoJob
        THEN /\ ready' = ready \ {c}
             /\ UNCHANGED <<status, toWorker>>
        ELSE IF Queued = {}
        THEN UNCHANGED <<status, ready, toWorker>>
        ELSE LET j == Min(Queued) IN
             /\ status' = [status EXCEPT ![j] = "Running"]
             /\ ready' = ready \ {c}
             /\ toWorker' = [toWorker EXCEPT ![c] = Append(@, IAssign(j))]

\* The tail of a handler that awaited DispatchAsync
SrvDispatchFor(c) ==
    /\ c \in dispatchPending
    /\ DoDispatch
    /\ dispatchPending' = dispatchPending \ {c}
    /\ UNCHANGED <<mapped, authed, srvDown, reconPhase, reconRead, reconSnap, cancelSends>>
    /\ UNCHANGED <<toServer, replies, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* DispatchAsync raised by anything else: a submission, another worker finishing, a priority change
SrvDispatchElsewhere ==
    /\ DoDispatch
    /\ UNCHANGED <<mapped, authed, srvDown, reconPhase, reconRead, reconSnap, dispatchPending, cancelSends>>
    /\ UNCHANGED <<toServer, replies, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* SignalR runs one invocation per connection at a time
SrvCanTake(c) ==
    /\ ServerHears(c)
    /\ reconPhase[c] = "idle"
    /\ c \notin dispatchPending
    /\ toServer[c] # <<>>

\* WorkerHub.Authenticate -> WorkerRepository.ConnectAsync, which supersedes the previous connection, whichever of the two is newer
SrvAuthenticate(c) ==
    /\ SrvCanTake(c)
    /\ Head(toServer[c]) = MAuth
    /\ toServer' = [toServer EXCEPT ![c] = Tail(@)]
    /\ LET old == mapped
           forget == old # 0 /\ old # c
       IN /\ mapped' = c
          /\ authed' = (IF forget THEN authed \ {old} ELSE authed) \cup {c}
          /\ ready' = IF forget THEN ready \ {old} ELSE ready
    /\ UNCHANGED <<status, srvDown, reconPhase, reconRead, reconSnap, dispatchPending, cancelSends>>
    /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* WorkerHub.Ready
SrvReady(c) ==
    /\ SrvCanTake(c)
    /\ Head(toServer[c]) = MReady
    /\ toServer' = [toServer EXCEPT ![c] = Tail(@)]
    /\ IF c \notin authed
       THEN UNCHANGED <<ready, dispatchPending>>
       ELSE /\ ready' = ready \cup {c}
            /\ dispatchPending' = dispatchPending \cup {c}
    /\ UNCHANGED <<status, mapped, authed, srvDown, reconPhase, reconRead, reconSnap, cancelSends>>
    /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* WorkerHub.JobCompleted. Readying and RecheckHost don't depend on whether the status write took: a replayed duplicate still re-readies.
SrvJobCompleted(c) ==
    /\ SrvCanTake(c)
    /\ Head(toServer[c]).t = "JobCompleted"
    /\ LET m == Head(toServer[c]) IN
       /\ toServer' = [toServer EXCEPT ![c] = Tail(@)]
       /\ IF c \notin authed
          THEN \* HubException: not authenticated
               /\ replies' = [replies EXCEPT ![c] = @ \cup {RAck(m, FALSE)}]
               /\ UNCHANGED <<status, ready, dispatchPending, toWorker>>
          ELSE IF RunningJob # NoJob /\ RunningJob # m.j
          THEN \* Names a job other than the worker's current one: ignored
               /\ replies' = [replies EXCEPT ![c] = @ \cup {RAck(m, TRUE)}]
               /\ UNCHANGED <<status, ready, dispatchPending, toWorker>>
          ELSE /\ status' = StatusAfter(m.j, m.s)
               /\ replies' = [replies EXCEPT ![c] = @ \cup {RAck(m, TRUE)}]
               /\ IF m.s = "Failed"
                  THEN /\ toWorker' = [toWorker EXCEPT ![c] = Append(@, IRecheck)]
                       /\ UNCHANGED <<ready, dispatchPending>>
                  ELSE /\ ready' = ready \cup {c}
                       /\ dispatchPending' = dispatchPending \cup {c}
                       /\ UNCHANGED toWorker
    /\ UNCHANGED <<mapped, authed, srvDown, reconPhase, reconRead, reconSnap, cancelSends>>
    /\ UNCHANGED stalled
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* WorkerHub.Reconnect, up to its first await: read the worker's running job
SrvReconnectRead(c) ==
    /\ SrvCanTake(c)
    /\ Head(toServer[c]).t = "Reconnect"
    /\ LET m == Head(toServer[c]) IN
       /\ toServer' = [toServer EXCEPT ![c] = Tail(@)]
       /\ IF c \notin authed
          THEN /\ replies' = [replies EXCEPT ![c] = @ \cup {RReconnect(FALSE, NoJob)}]
               /\ UNCHANGED <<reconPhase, reconRead, reconSnap>>
          ELSE /\ reconPhase' = [reconPhase EXCEPT ![c] = "read"]
               /\ reconRead' = [reconRead EXCEPT ![c] = RunningJob]
               /\ reconSnap' = [reconSnap EXCEPT ![c] = m.j]
               /\ UNCHANGED replies
    /\ UNCHANGED <<status, mapped, authed, ready, srvDown, dispatchPending, cancelSends>>
    /\ UNCHANGED <<toWorker, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* WorkerHub.Reconnect, the rest: a running job the worker didn't report is failed as lost
SrvReconnectAct(c) ==
    /\ reconPhase[c] = "read"
    /\ ServerHears(c)
    /\ LET run == reconRead[c]
           snap == reconSnap[c]
           lost == run # NoJob /\ snap # run
       IN /\ IF lost
             THEN /\ status' = StatusAfter(run, "Failed")
                  /\ lostFailed' = lostFailed \cup {run}
                  /\ spurious' = (spurious \/ JobAlive(run))
                  \* ... and still answers that the worker should be running it
                  /\ replies' = [replies EXCEPT ![c] = @ \cup {RReconnect(TRUE, run)}]
                  /\ UNCHANGED toWorker
             ELSE /\ replies' = [replies EXCEPT ![c] = @ \cup {RReconnect(TRUE, run)}]
                  /\ UNCHANGED <<status, lostFailed, spurious, toWorker>>
          /\ reconPhase' = [reconPhase EXCEPT ![c] = "idle"]
    /\ UNCHANGED <<mapped, authed, ready, srvDown, reconRead, reconSnap, dispatchPending, cancelSends>>
    /\ UNCHANGED <<toServer, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED <<busyRejections, deadConns>>

\* WorkerHub.OnDisconnectedAsync -> WorkerRepository.SetDisconnectedAsync. The server's own timeout: only for a connection the worker has left or that has gone silent. The job stays Running.
SrvDisconnected(c) ==
    /\ c <= connsUsed
    /\ c \notin srvDown
    /\ ~Listening(c) \/ c \in stalled
    /\ srvDown' = srvDown \cup {c}
    /\ authed' = authed \ {c}
    /\ ready' = ready \ {c}
    /\ mapped' = IF mapped = c THEN 0 ELSE mapped
    /\ UNCHANGED <<status, reconPhase, reconRead, reconSnap, dispatchPending, cancelSends>>
    /\ UNCHANGED chanVars
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* JobScheduler.CancelJobAsync, up to sending CancelJob: find the job's connection, cancel it
SrvCancel(j) ==
    /\ cancelsUsed < MaxCancels
    /\ status[j] \in {"Queued", "Running"}
    /\ cancelsUsed' = cancelsUsed + 1
    /\ status' = [status EXCEPT ![j] = "Cancelled"]
    /\ cancelSends' = cancelSends \cup {<<j, IF status[j] = "Running" THEN mapped ELSE 0>>}
    /\ UNCHANGED <<mapped, authed, ready, srvDown, reconPhase, reconRead, reconSnap, dispatchPending>>
    /\ UNCHANGED chanVars
    /\ UNCHANGED wVars /\ UNCHANGED <<stallsUsed, restartsUsed, opDiagsUsed>> /\ UNCHANGED ghostVars

SrvCancelSend(p) ==
    /\ p \in cancelSends
    /\ cancelSends' = cancelSends \ {p}
    /\ toWorker' = IF p[2] # 0 THEN [toWorker EXCEPT ![p[2]] = Append(@, ICancel(p[1]))] ELSE toWorker
    /\ UNCHANGED <<status, mapped, authed, ready, srvDown, reconPhase, reconRead, reconSnap, dispatchPending>>
    /\ UNCHANGED <<toServer, replies, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* WorkerDetail.RunDiagnostic: not ready while it runs, then ask the worker
SrvOperatorDiagnostic ==
    /\ opDiagsUsed < MaxOperatorDiags
    /\ mapped # 0
    /\ opDiagsUsed' = opDiagsUsed + 1
    /\ ready' = ready \ {mapped}
    /\ toWorker' = [toWorker EXCEPT ![mapped] = Append(@, IRunDiagnostic)]
    /\ UNCHANGED <<status, mapped, authed, srvDown, reconPhase, reconRead, reconSnap, dispatchPending, cancelSends>>
    /\ UNCHANGED <<toServer, replies, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED <<stallsUsed, restartsUsed, cancelsUsed>> /\ UNCHANGED ghostVars

-----------------------------------------------------------------------------
\* Worker

\* Changes to toServer, wPending and wBuffer for one send. BufferedHubSender: invoke if connected, else buffer.
SendBuffered(m) ==
    IF wUp
    THEN /\ toServer' = [toServer EXCEPT ![wConn] = Append(@, m)]
         /\ wPending' = wPending \cup {<<wConn, m>>}
         /\ UNCHANGED wBuffer
    ELSE /\ wBuffer' = Append(wBuffer, m)
         /\ UNCHANGED <<toServer, wPending>>

\* A plain SendAsync: lost if the connection is down
SendFireAndForget(m) ==
    /\ toServer' = IF wUp THEN [toServer EXCEPT ![wConn] = Append(@, m)] ELSE toServer
    /\ UNCHANGED <<wPending, wBuffer>>

\* WorkerService.OnJobAssigned
WOnAssign ==
    /\ Listening(wConn)
    /\ toWorker[wConn] # <<>>
    /\ Head(toWorker[wConn]).t = "AssignJob"
    /\ LET j == Head(toWorker[wConn]).j IN
       /\ toWorker' = [toWorker EXCEPT ![wConn] = Tail(@)]
       /\ IF wAct = "Idle"
          THEN /\ wAct' = "Job"
               /\ wJob' = j
               /\ wJobDone' = "none"
               /\ wCancelReq' = FALSE
               /\ UNCHANGED <<toServer, wPending, wBuffer, busyRejections>>
          ELSE IF wAct = "Job" /\ wJob = j
          THEN \* RejectBusyJobAsync's already-running branch
               /\ busyRejections' = busyRejections \cup {"Redelivery"}
               /\ SendBuffered(MJobCompleted(j, "Failed"))
               /\ UNCHANGED <<wAct, wJob, wJobDone, wCancelReq>>
          ELSE \* RejectJobAsync -> ReportRejectionAsync, which takes no lock
               /\ busyRejections' = busyRejections \cup {wAct}
               /\ SendBuffered(MJobCompleted(j, "Failed"))
               /\ UNCHANGED <<wAct, wJob, wJobDone, wCancelReq>>
    /\ UNCHANGED srvVars /\ UNCHANGED <<replies, stalled>>
    /\ UNCHANGED <<wConn, wUp, connsUsed, wDiagCached, wLock, wReportMsg, wSync, wReplayPending, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED <<lostFailed, deadConns, spurious>>

\* WorkerService.OnCancelJob
WOnCancel ==
    /\ Listening(wConn)
    /\ toWorker[wConn] # <<>>
    /\ Head(toWorker[wConn]).t = "CancelJob"
    /\ toWorker' = [toWorker EXCEPT ![wConn] = Tail(@)]
    /\ wCancelReq' = (wCancelReq \/ (wAct = "Job" /\ wJob = Head(toWorker[wConn]).j))
    /\ UNCHANGED srvVars /\ UNCHANGED <<toServer, replies, stalled>>
    /\ UNCHANGED <<wConn, wUp, connsUsed, wAct, wJob, wJobDone, wDiagCached, wLock, wReportMsg, wBuffer, wPending, wSync, wReplayPending, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* WorkerService.OnRecheckHost / RunDiagnostic -> ExecuteDiagnosticAsync
WOnDiagnosticRequest ==
    /\ Listening(wConn)
    /\ toWorker[wConn] # <<>>
    /\ Head(toWorker[wConn]).t \in {"RecheckHost", "RunDiagnostic"}
    /\ toWorker' = [toWorker EXCEPT ![wConn] = Tail(@)]
    /\ IF wAct = "Idle"
       THEN /\ wAct' = "Diag"
            /\ UNCHANGED <<toServer, wPending, wBuffer>>
       ELSE \* Blocked: re-push the cached report and, today, announce Ready
            /\ UNCHANGED wAct
            /\ IF wDiagCached
               THEN SendFireAndForget(MReady)
               ELSE UNCHANGED <<toServer, wPending, wBuffer>>
    /\ UNCHANGED srvVars /\ UNCHANGED <<replies, stalled>>
    /\ UNCHANGED <<wConn, wUp, connsUsed, wJob, wJobDone, wCancelReq, wDiagCached, wLock, wReportMsg, wSync, wReplayPending, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* The diagnostic finishes (always Healthy) and announces Ready
WDiagnosticDone ==
    /\ wAct = "Diag"
    /\ wAct' = "Idle"
    /\ wDiagCached' = TRUE
    /\ SendFireAndForget(MReady)
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED <<wConn, wUp, connsUsed, wJob, wJobDone, wCancelReq, wLock, wReportMsg, wSync, wReplayPending, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* The job's run ends: cancelled if asked to stop, otherwise either way
WJobFinish ==
    /\ wAct = "Job"
    /\ wJobDone = "none"
    /\ wJobDone' \in (IF wCancelReq THEN {"Cancelled"} ELSE {"Success", "Failed"})
    /\ UNCHANGED srvVars /\ UNCHANGED chanVars
    /\ UNCHANGED <<wConn, wUp, connsUsed, wAct, wJob, wCancelReq, wDiagCached, wLock, wReportMsg, wBuffer, wPending, wSync, wReplayPending, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* WorkerService.ReportCompletionAsync: under _syncLock, leave the activity, then send JobCompleted and wait for it
WReportCompletion ==
    /\ wAct = "Job"
    /\ wJobDone # "none"
    /\ wLock = "free"
    /\ LET m == MJobCompleted(wJob, wJobDone) IN
       /\ wAct' = "Idle"
       /\ wJob' = NoJob
       /\ wJobDone' = "none"
       /\ wCancelReq' = FALSE
       /\ SendBuffered(m)
       /\ IF wUp
          THEN /\ wReportMsg' = m
               /\ wLock' = "report"
          ELSE UNCHANGED <<wReportMsg, wLock>>
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED <<wConn, wUp, connsUsed, wDiagCached, wSync, wReplayPending, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* A completion for one of the worker's invocations arrives
WOnAck ==
    /\ Listening(wConn)
    /\ \E r \in replies[wConn] :
        /\ r.t = "Ack"
        /\ replies' = [replies EXCEPT ![wConn] = @ \ {r}]
        /\ IF wSync = "replay" /\ r.m = wReplayPending
           THEN \* MessageBuffer.ReplayAsync: dequeue on success; a failure throws out of the sync
                IF r.ok
                THEN /\ wBuffer' = Tail(wBuffer)
                     /\ wReplayPending' = NoMsg
                     /\ UNCHANGED <<wUp, wPending, wLock, wReportMsg, wSync>>
                ELSE \* Sync failed: tear the connection down
                     /\ wUp' = FALSE
                     /\ wSync' = "none"
                     /\ wReplayPending' = NoMsg
                     /\ wLock' = "free"
                     /\ wBuffer' = wBuffer \o SetToSeq({p[2] : p \in {q \in wPending : q[1] = wConn}})
                     /\ wPending' = {q \in wPending : q[1] # wConn}
                     /\ wReportMsg' = NoMsg
          ELSE /\ <<wConn, r.m>> \in wPending
               /\ wPending' = wPending \ {<<wConn, r.m>>}
               /\ wBuffer' = IF r.ok THEN wBuffer ELSE Append(wBuffer, r.m)
               /\ IF r.m = wReportMsg
                  THEN /\ wReportMsg' = NoMsg
                       /\ wLock' = IF wLock = "report" THEN "free" ELSE wLock
                  ELSE UNCHANGED <<wReportMsg, wLock>>
               /\ UNCHANGED <<wUp, wSync, wReplayPending>>
    /\ UNCHANGED srvVars /\ UNCHANGED <<toServer, toWorker, stalled>>
    /\ UNCHANGED <<wConn, connsUsed, wAct, wJob, wJobDone, wCancelReq, wDiagCached, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* ConnectAndSyncCore: Authenticate, outside the lock
WSyncAuthenticate ==
    /\ wUp
    /\ wSync = "auth"
    /\ SendFireAndForget(MAuth)
    /\ wSync' = "lock"
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED <<wConn, wUp, connsUsed, wAct, wJob, wJobDone, wCancelReq, wDiagCached, wLock, wReportMsg, wReplayPending, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* ... take _syncLock for the replay and the reconcile
WSyncLock ==
    /\ wUp
    /\ wSync = "lock"
    /\ wLock = "free"
    /\ wLock' = "sync"
    /\ wSync' = "replay"
    /\ UNCHANGED srvVars /\ UNCHANGED chanVars
    /\ UNCHANGED <<wConn, wUp, connsUsed, wAct, wJob, wJobDone, wCancelReq, wDiagCached, wReportMsg, wBuffer, wPending, wReplayPending, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* ... replay the buffer one message at a time, oldest first
WSyncReplaySend ==
    /\ wUp
    /\ wSync = "replay"
    /\ wReplayPending = NoMsg
    /\ wBuffer # <<>>
    /\ wReplayPending' = Head(wBuffer)
    /\ toServer' = [toServer EXCEPT ![wConn] = Append(@, Head(wBuffer))]
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED <<wConn, wUp, connsUsed, wAct, wJob, wJobDone, wCancelReq, wDiagCached, wLock, wReportMsg, wBuffer, wPending, wSync, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

WSyncReplayDone ==
    /\ wUp
    /\ wSync = "replay"
    /\ wReplayPending = NoMsg
    /\ wBuffer = <<>>
    /\ wSync' = "snap"
    /\ UNCHANGED wLock
    /\ UNCHANGED srvVars /\ UNCHANGED chanVars
    /\ UNCHANGED <<wConn, wUp, connsUsed, wAct, wJob, wJobDone, wCancelReq, wDiagCached, wReportMsg, wBuffer, wPending, wReplayPending, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* ... read CurrentJobId and call Reconnect with it
WSyncReconnect ==
    /\ wUp
    /\ wSync = "snap"
    /\ LET snap == IF wAct = "Job" THEN wJob ELSE NoJob IN
       /\ wSnap' = snap
       /\ toServer' = [toServer EXCEPT ![wConn] = Append(@, MReconnect(snap))]
    /\ wSync' = "await"
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED <<wConn, wUp, connsUsed, wAct, wJob, wJobDone, wCancelReq, wDiagCached, wLock, wReportMsg, wBuffer, wPending, wReplayPending, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* ... Reconnect's answer releases the lock
WOnReconnectReply ==
    /\ Listening(wConn)
    /\ wSync = "await"
    /\ \E r \in replies[wConn] :
        /\ r.t = "ReconnectReply"
        /\ replies' = [replies EXCEPT ![wConn] = @ \ {r}]
        /\ IF r.ok
           THEN /\ wExpected' = r.e
                /\ wSync' = "post"
                /\ wLock' = "free"
                /\ UNCHANGED <<wUp, wBuffer, wPending, wReportMsg>>
           ELSE \* Sync failed: tear the connection down
                /\ wUp' = FALSE
                /\ wSync' = "none"
                /\ wLock' = "free"
                /\ wBuffer' = wBuffer \o SetToSeq({p[2] : p \in {q \in wPending : q[1] = wConn}})
                /\ wPending' = {q \in wPending : q[1] # wConn}
                /\ wReportMsg' = NoMsg
                /\ UNCHANGED wExpected
    /\ UNCHANGED srvVars /\ UNCHANGED <<toServer, toWorker, stalled>>
    /\ UNCHANGED <<wConn, connsUsed, wAct, wJob, wJobDone, wCancelReq, wDiagCached, wReplayPending, wSnap>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* ... then abort an unexpected job, keep going with an expected one, or announce readiness
WSyncFinish ==
    /\ wSync = "post"
    /\ wSync' = "none"
    /\ IF wSnap # NoJob /\ wExpected # wSnap
       THEN /\ wCancelReq' = (wCancelReq \/ (wAct = "Job" /\ wJob = wSnap))
            /\ UNCHANGED <<wAct, toServer, wPending, wBuffer>>
       ELSE IF wSnap # NoJob
       THEN UNCHANGED <<wCancelReq, wAct, toServer, wPending, wBuffer>>
       ELSE IF ~wDiagCached
       THEN \* First connect after a restart: run the diagnostic, which announces Ready itself
            /\ wAct' = IF wAct = "Idle" THEN "Diag" ELSE wAct
            /\ UNCHANGED <<wCancelReq, toServer, wPending, wBuffer>>
       ELSE /\ SendFireAndForget(MReady)
            /\ UNCHANGED <<wCancelReq, wAct>>
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED <<wConn, wUp, connsUsed, wJob, wJobDone, wDiagCached, wLock, wReportMsg, wReplayPending, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* The worker gives up on its connection (a network drop, or its server timeout on a silent one): invocations in flight fail and are buffered, a sync in progress fails
\* Only while a later connection is still in the budget, so a bounded run never strands the worker offline for good
WAbandon ==
    /\ wUp
    /\ connsUsed < MaxConns
    /\ wUp' = FALSE
    /\ wBuffer' = wBuffer \o SetToSeq({p[2] : p \in {q \in wPending : q[1] = wConn}})
    /\ wPending' = {q \in wPending : q[1] # wConn}
    /\ wSync' = "none"
    /\ wReplayPending' = NoMsg
    /\ wReportMsg' = NoMsg
    /\ wLock' = "free"
    /\ UNCHANGED srvVars /\ UNCHANGED chanVars
    /\ UNCHANGED <<wConn, connsUsed, wAct, wJob, wJobDone, wCancelReq, wDiagCached, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* Automatic reconnect or the heartbeat loop opens a new connection and starts ConnectAndSync
WConnect ==
    /\ ~wUp
    /\ connsUsed < MaxConns
    /\ connsUsed' = connsUsed + 1
    /\ wConn' = connsUsed + 1
    /\ wUp' = TRUE
    /\ wSync' = "auth"
    /\ UNCHANGED srvVars /\ UNCHANGED chanVars
    /\ UNCHANGED <<wAct, wJob, wJobDone, wCancelReq, wDiagCached, wLock, wReportMsg, wBuffer, wPending, wReplayPending, wSnap, wExpected>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* The worker process dies and starts over: no buffer, no job, no cached diagnostic
WRestart ==
    /\ restartsUsed < MaxRestarts
    /\ connsUsed < MaxConns
    /\ restartsUsed' = restartsUsed + 1
    /\ wUp' = FALSE
    /\ wAct' = "Idle"
    /\ wJob' = NoJob
    /\ wJobDone' = "none"
    /\ wCancelReq' = FALSE
    /\ wDiagCached' = FALSE
    /\ wLock' = "free"
    /\ wReportMsg' = NoMsg
    /\ wBuffer' = <<>>
    /\ wPending' = {}
    /\ wSync' = "none"
    /\ wReplayPending' = NoMsg
    /\ UNCHANGED srvVars /\ UNCHANGED chanVars
    /\ UNCHANGED <<wConn, connsUsed, wSnap, wExpected>>
    /\ UNCHANGED <<stallsUsed, cancelsUsed, opDiagsUsed>>
    /\ deadConns' = 1..connsUsed
    /\ UNCHANGED <<lostFailed, busyRejections, spurious>>

\* The current connection goes silent in both directions while both ends still think it is up
Stall ==
    /\ stallsUsed < MaxStalls
    /\ connsUsed < MaxConns
    /\ wUp
    /\ wConn \notin stalled
    /\ stallsUsed' = stallsUsed + 1
    /\ stalled' = stalled \cup {wConn}
    /\ UNCHANGED srvVars /\ UNCHANGED <<toServer, toWorker, replies>>
    /\ UNCHANGED wVars /\ UNCHANGED <<restartsUsed, cancelsUsed, opDiagsUsed>> /\ UNCHANGED ghostVars

-----------------------------------------------------------------------------
Next ==
    \/ \E c \in Conns :
        \/ SrvAuthenticate(c) \/ SrvReady(c) \/ SrvJobCompleted(c)
        \/ SrvReconnectRead(c) \/ SrvReconnectAct(c)
        \/ SrvDispatchFor(c) \/ SrvDisconnected(c)
    \/ SrvDispatchElsewhere
    \/ \E j \in Jobs : SrvCancel(j)
    \/ \E p \in cancelSends : SrvCancelSend(p)
    \/ SrvOperatorDiagnostic
    \/ WOnAssign \/ WOnCancel \/ WOnDiagnosticRequest \/ WDiagnosticDone
    \/ WJobFinish \/ WReportCompletion \/ WOnAck
    \/ WSyncAuthenticate \/ WSyncLock \/ WSyncReplaySend \/ WSyncReplayDone
    \/ WSyncReconnect \/ WOnReconnectReply \/ WSyncFinish
    \/ WAbandon \/ WConnect \/ WRestart \/ Stall

Spec == Init /\ [][Next]_vars

-----------------------------------------------------------------------------
\* Properties

TypeOK ==
    /\ status \in [Jobs -> Statuses]
    /\ wAct \in {"Idle", "Job", "Diag"}
    /\ wLock \in {"free", "sync", "report"}
    /\ wSync \in {"none", "auth", "lock", "replay", "snap", "await", "post"}
    /\ (wAct = "Job") <=> (wJob \in Jobs)

\* The worker will be told to stop j, or will find out at its next reconcile
StopPending(j) ==
    \/ ~wUp \/ wSync # "none" \/ wConn \in stalled
    \/ ICancel(j) \in Range(toWorker[wConn])
    \/ <<j, wConn>> \in cancelSends
    \/ wCancelReq
    \/ wJobDone # "none"

\* The server never silently gives up on a job the worker is still running
NoUntrackedRun ==
    (wAct = "Job" /\ status[wJob] # "Running") => StopPending(wJob)

\* No job is ever failed because the worker was busy when it arrived
NoBusyRejection == busyRejections = {}

\* The server never fails as lost a job the worker is running, is about to receive, or has a result for
NoSpuriousFailure == ~spurious

\* The incident: the worker runs a job the server failed as lost, and the server considers it ready for more
NotIncident == ~(wAct = "Job" /\ wJob \in lostFailed /\ mapped \in ready)

=============================================================================
