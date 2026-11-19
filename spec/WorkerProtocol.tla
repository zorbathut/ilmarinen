---------------------------- MODULE WorkerProtocol ----------------------------
\* The server-worker job protocol with fenced sessions and reported worker state. See README.md.
\* One worker W is modelled in full, plus an optional competitor that takes queued jobs.
\* Every server action is one atomic step: the design serializes all of a worker's handlers, dispatch and cancel behind one per-worker lock, with the fence check inside it.
EXTENDS Naturals, Sequences, FiniteSets, TLC

CONSTANTS
    NumJobs,            \* jobs, all queued at the start; ids 1..NumJobs, dispatched lowest id first (standing in for the server's dispatch order; which queued job goes first affects no property)
    MaxConns,           \* hub connections the worker may open, the first included
    MaxStalls,          \* connections that may go silent while both ends think they are up
    MaxFalseTimeouts,   \* times the server may time out a connection that is in fact working
    MaxRestarts,        \* worker process restarts
    MaxCancels,         \* user cancels
    MaxOperatorDiags,   \* "Run diagnostic" clicks
    MaxUnhealthy,       \* diagnostics that may come back unhealthy, each starting a retry
    MaxQueue,           \* messages the worker may have outstanding on a connection before it waits for the server to catch up
    WithCompetitor,     \* another worker takes queued jobs too (it would mask a stranded W, so it has its own configurations)
    \* Each replaces one design element with its realistic alternative; every one must break the property pinned for it in check-protocol.sh
    AltClockEpochs,             \* epochs from the clock, which may step back across a restart
    AltReconcileBeforeResume,   \* a connection takes work as soon as it is accepted, before its Resume reconciles
    AltFailCollision,           \* a refused assignment is failed rather than handed back
    AltNoHandBackGuard,         \* a hand-back is applied even for the job W reports running
    AltFailUndelivered,         \* Resume fails an unmentioned job even when the same process instance never received it
    AltNoAbortOnResume,         \* Resume doesn't tell W to stop a job the server no longer has running for it
    AltFoldDiagnostic,          \* a diagnostic request arriving during a diagnostic is satisfied by the running one
    AltReadyFromTransitions,    \* readiness raised by a completion's status transition, not derived from reported state
    AltStateDoesNotDispatch,    \* a state report updates readiness but doesn't trigger dispatch
    AltNoInvocationEpoch        \* W acts on an invocation queued on one of its own older connections

Jobs == 1..NumJobs
NoJob == 0
Conns == 1..MaxConns
Outcomes == {"Success", "Failed", "Cancelled"}
Statuses == {"Queued", "Running"} \cup Outcomes
Owners == {"none", "W", "C"}
Diags == {"None", "Healthy", "Unhealthy"}

Min(S) == CHOOSE x \in S : \A y \in S : x <= y
Range(s) == {s[i] : i \in DOMAIN s}

\* What the worker reports about itself
State(act, job, diag) == [act |-> act, job |-> job, diag |-> diag]
NoState == State("none", NoJob, "None")

\* Results: kept by the worker until the server acknowledges them
RCompleted(j, s) == [t |-> "JobCompleted", j |-> j, s |-> s]
RRejected(j) == [t |-> "RejectJob", j |-> j, s |-> "none"]
NoResult == [t |-> "none", j |-> NoJob, s |-> "none"]

\* Worker -> server messages; every record has the same fields so TLC can compare them
Msg(t, r, st, rs) == [t |-> t, r |-> r, st |-> st, rs |-> rs]
MConnect == Msg("Connect", NoResult, NoState, {})
MResume(st, rs) == Msg("Resume", NoResult, st, rs)
MState(st) == Msg("State", NoResult, st, {})
MResult(r) == Msg("Result", r, NoState, {})
MHeartbeat == Msg("Heartbeat", NoResult, NoState, {})

\* Server -> worker invocations, all naming the job they are about
IAssign(j) == [t |-> "AssignJob", j |-> j]
ICancel(j) == [t |-> "CancelJob", j |-> j]
IRecheck == [t |-> "RecheckHost", j |-> NoJob]
IRunDiagnostic == [t |-> "RunDiagnostic", j |-> NoJob]

\* Server -> worker completions of the worker's invocations; delivered apart from queued invocations, so they can overtake them
Reply(t, ok, r) == [t |-> t, ok |-> ok, r |-> r]

VARIABLES
    \* Server: database
    status,           \* job -> status
    owner,            \* job -> who it was handed to: "none" | "W" | "C"
    assignedInst,     \* job -> W's process instance it was assigned to (0 if never)
    \* Server: W's session, guarded by the per-worker lock
    last,             \* the connection last accepted for W
    curAlive,         \* that connection hasn't been seen to disconnect
    resumed,          \* its Resume has been applied
    lastState,        \* W's latest reported state
    awaitDiag,        \* a host re-check or operator diagnostic has been asked for and not yet started
    wake,             \* something that can change W's readiness or the queue has happened since dispatch last ran
    readyFlag,        \* only with AltReadyFromTransitions
    connInst,         \* connection -> the W process instance that opened it (0 if not opened)
    retired,          \* W process instances a newer instance has superseded; their handshakes are refused for good
    srvDown,          \* connections the server has seen disconnect
    \* Channels
    toServer, toWorker, replies, stalled,
    \* Worker
    wInst, wConn, wUp, connsUsed,
    wSync,            \* "none" (live) | "connecting" | "resuming"
    wLock,            \* the worker's state lock, held by Resume from its snapshot until its answer
    wAct, wJob, wJobDone, wCancelReq,
    wDiag, wDiagWanted,
    wResults,         \* results not yet acknowledged
    wResumeSet,       \* the results the in-flight Resume carries
    wHeartbeat,       \* a heartbeat is awaiting its answer
    \* Budgets
    stallsUsed, falseTimeoutsUsed, restartsUsed, cancelsUsed, opDiagsUsed, unhealthyUsed,
    \* Ghosts, for the properties
    reportedFailed,   \* jobs W reported Failed and the server applied
    lostFailed,       \* jobs Resume failed as lost
    handedBack,       \* whether any assignment has been handed back (non-vacuity witness)
    spurious          \* Resume failed as lost a job assigned to, or still held by, W's live process

srvVars == <<status, owner, assignedInst, last, curAlive, resumed, lastState, awaitDiag, wake, readyFlag, connInst, retired, srvDown>>
chanVars == <<toServer, toWorker, replies, stalled>>
wVars == <<wInst, wConn, wUp, connsUsed, wSync, wLock, wAct, wJob, wJobDone, wCancelReq, wDiag, wDiagWanted, wResults, wResumeSet, wHeartbeat>>
budgetVars == <<stallsUsed, falseTimeoutsUsed, restartsUsed, cancelsUsed, opDiagsUsed, unhealthyUsed>>
ghostVars == <<reportedFailed, lostFailed, handedBack, spurious>>
vars == <<srvVars, chanVars, wVars, budgetVars, ghostVars>>

-----------------------------------------------------------------------------
\* Helpers

\* JobRepository.UpdateStatusAsync's finality: Success and Failed are final; Cancelled gives way only to Success or Failed
After(cur, s) ==
    IF cur \in {"Success", "Failed"} \/ (cur = "Cancelled" /\ s \notin {"Success", "Failed"}) THEN cur ELSE s

Queued == {j \in Jobs : status[j] = "Queued"}
RunningForW == {j \in Jobs : status[j] = "Running" /\ owner[j] = "W"}

\* With AltClockEpochs a restarted process's clock has stepped back: every epoch of the new process falls below the old one's
ClockEpoch(c) == IF connInst[c] > 1 THEN c ELSE c + MaxConns

\* Whether a handshake on c may take over from the last accepted connection: a later connection of the same process, or a process that hasn't been superseded
Accepts(c) ==
    IF AltClockEpochs
    THEN ClockEpoch(c) > ClockEpoch(last)
    ELSE connInst[c] \notin retired /\ (connInst[c] # connInst[last] \/ c > last)

\* A connection the server takes W's calls from
Current(c) == c = last /\ curAlive

\* The server's derived readiness
Ready ==
    /\ curAlive /\ resumed
    /\ lastState.act = "Idle" /\ lastState.diag = "Healthy"
    /\ ~awaitDiag
    /\ RunningForW = {}
    /\ AltReadyFromTransitions => readyFlag

Listening(c) == wUp /\ c = wConn /\ c \notin stalled /\ c \notin srvDown
ServerHears(c) == c \notin stalled /\ c \notin srvDown
Live == wUp /\ wSync = "none"
MyState == State(wAct, wJob, wDiag)

\* Room for two more messages on the worker's connection (a result and the state after it); a bound on the model, not a protocol rule
Room == Len(toServer[wConn]) + 2 <= MaxQueue

\* The live process still has j: running it, about to receive it, or holding its result
JobAlive(j) ==
    \/ wAct = "Job" /\ wJob = j
    \/ wUp /\ IAssign(j) \in Range(toWorker[wConn])
    \/ \E r \in wResults : r.j = j

-----------------------------------------------------------------------------
Init ==
    \* Connected, resumed, idle and healthy, every job queued
    /\ status = [j \in Jobs |-> "Queued"]
    /\ owner = [j \in Jobs |-> "none"]
    /\ assignedInst = [j \in Jobs |-> 0]
    /\ last = 1
    /\ curAlive = TRUE
    /\ resumed = TRUE
    /\ lastState = State("Idle", NoJob, "Healthy")
    /\ awaitDiag = FALSE
    /\ wake = TRUE
    /\ readyFlag = TRUE
    /\ connInst = [c \in Conns |-> IF c = 1 THEN 1 ELSE 0]
    /\ retired = {}
    /\ srvDown = {}
    /\ toServer = [c \in Conns |-> <<>>]
    /\ toWorker = [c \in Conns |-> <<>>]
    /\ replies = [c \in Conns |-> {}]
    /\ stalled = {}
    /\ wInst = 1
    /\ wConn = 1
    /\ wUp = TRUE
    /\ connsUsed = 1
    /\ wSync = "none"
    /\ wLock = FALSE
    /\ wAct = "Idle"
    /\ wJob = NoJob
    /\ wJobDone = "none"
    /\ wCancelReq = FALSE
    /\ wDiag = "Healthy"
    /\ wDiagWanted = FALSE
    /\ wResults = {}
    /\ wResumeSet = {}
    /\ wHeartbeat = FALSE
    /\ stallsUsed = 0
    /\ falseTimeoutsUsed = 0
    /\ restartsUsed = 0
    /\ cancelsUsed = 0
    /\ opDiagsUsed = 0
    /\ unhealthyUsed = 0
    /\ reportedFailed = {}
    /\ lostFailed = {}
    /\ handedBack = FALSE
    /\ spurious = FALSE

-----------------------------------------------------------------------------
\* Server

SrvCanTake(c) == ServerHears(c) /\ toServer[c] # <<>>

\* Connect + Authenticate: accept the connection as W's current one, or fence it
SrvConnect(c) ==
    /\ SrvCanTake(c)
    /\ Head(toServer[c]).t = "Connect"
    /\ toServer' = [toServer EXCEPT ![c] = Tail(@)]
    /\ IF Accepts(c)
       THEN /\ last' = c
            /\ retired' = IF connInst[c] # connInst[last] THEN retired \cup {connInst[last]} ELSE retired
            /\ curAlive' = TRUE
            /\ resumed' = AltReconcileBeforeResume
            /\ replies' = [replies EXCEPT ![c] = @ \cup {Reply("Connect", TRUE, NoResult)}]
       ELSE /\ replies' = [replies EXCEPT ![c] = @ \cup {Reply("Connect", FALSE, NoResult)}]
            /\ UNCHANGED <<last, retired, curAlive, resumed>>
    /\ UNCHANGED <<status, owner, assignedInst, lastState, awaitDiag, wake, readyFlag, connInst, srvDown>>
    /\ UNCHANGED <<toWorker, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* One result, applied idempotently: a completion only for a job handed to W; a hand-back only for a job W isn't reporting as running
ResultStatus(r, st, cur, own) ==
    IF r.t = "JobCompleted"
    THEN IF own = "W" THEN After(cur, r.s) ELSE cur
    ELSE IF cur = "Running" /\ own = "W" /\ (st.job # r.j \/ AltNoHandBackGuard)
         THEN IF AltFailCollision THEN "Failed" ELSE "Queued"
         ELSE cur

\* Resume: apply the results, then reconcile what the database says W is running with what W says it is running
SrvResume(c) ==
    /\ SrvCanTake(c)
    /\ Head(toServer[c]).t = "Resume"
    /\ LET m == Head(toServer[c])
           st == m.st
           resultFor(j) == {r \in m.rs : r.j = j}
           \* Status and owner after the results
           s1 == [j \in Jobs |-> IF resultFor(j) = {} THEN status[j] ELSE ResultStatus(CHOOSE r \in resultFor(j) : TRUE, st, status[j], owner[j])]
           o1 == [j \in Jobs |-> IF s1[j] = "Queued" THEN "none" ELSE owner[j]]
           failedNow == {j \in Jobs : s1[j] = "Failed" /\ status[j] # "Failed" /\ \E r \in resultFor(j) : r.t = "JobCompleted"}
           \* Running for W, yet W doesn't say so: never delivered if this process instance had it; possibly part-run if another did
           unmentioned == {j \in Jobs : s1[j] = "Running" /\ o1[j] = "W" /\ st.job # j}
           requeue == {j \in unmentioned : assignedInst[j] = connInst[c] /\ ~AltFailUndelivered}
           lose == unmentioned \ requeue
           s2 == [j \in Jobs |-> IF j \in requeue THEN "Queued" ELSE IF j \in lose THEN "Failed" ELSE s1[j]]
           o2 == [j \in Jobs |-> IF j \in requeue THEN "none" ELSE o1[j]]
           \* W runs a job the server no longer has running for it: tell it to stop
           abort == ~AltNoAbortOnResume /\ st.job # NoJob /\ ~(s2[st.job] = "Running" /\ o2[st.job] = "W")
           recheck == awaitDiag \/ failedNow # {}
           sends == (IF abort THEN <<ICancel(st.job)>> ELSE <<>>) \o (IF recheck THEN <<IRecheck>> ELSE <<>>)
       IN /\ toServer' = [toServer EXCEPT ![c] = Tail(@)]
          /\ IF ~Current(c)
             THEN /\ replies' = [replies EXCEPT ![c] = @ \cup {Reply("Resume", FALSE, NoResult)}]
                  /\ UNCHANGED <<status, owner, resumed, lastState, awaitDiag, wake, readyFlag, toWorker, ghostVars>>
             ELSE /\ status' = s2
                  /\ owner' = o2
                  /\ resumed' = TRUE
                  /\ lastState' = st
                  /\ awaitDiag' = recheck
                  /\ wake' = TRUE
                  /\ readyFlag' = IF AltReadyFromTransitions THEN st.act = "Idle" ELSE readyFlag
                  /\ toWorker' = [toWorker EXCEPT ![c] = @ \o sends]
                  /\ replies' = [replies EXCEPT ![c] = @ \cup {Reply("Resume", TRUE, NoResult)}]
                  /\ reportedFailed' = reportedFailed \cup failedNow
                  /\ lostFailed' = lostFailed \cup lose
                  /\ UNCHANGED handedBack
                  \* Ground truth: a job assigned to the process that is still alive either reached it (and Resume would name it) or never did (and should go back to the queue)
                  /\ spurious' = (spurious \/ \E j \in lose : assignedInst[j] = wInst \/ JobAlive(j))
    /\ UNCHANGED <<assignedInst, last, curAlive, connInst, retired, srvDown>>
    /\ UNCHANGED stalled
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars

\* A live state report from the current, resumed connection
SrvState(c) ==
    /\ SrvCanTake(c)
    /\ Head(toServer[c]).t = "State"
    /\ toServer' = [toServer EXCEPT ![c] = Tail(@)]
    /\ IF Current(c) /\ resumed
       THEN /\ lastState' = Head(toServer[c]).st
            /\ awaitDiag' = (awaitDiag /\ Head(toServer[c]).st.act # "Diag")
            /\ wake' = (wake \/ ~AltStateDoesNotDispatch)
       ELSE UNCHANGED <<lastState, awaitDiag, wake>>
    /\ UNCHANGED <<status, owner, assignedInst, last, curAlive, resumed, readyFlag, connInst, retired, srvDown>>
    /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* A live result (completion or hand-back): refused as fenced unless on the current, resumed connection
SrvResult(c) ==
    /\ SrvCanTake(c)
    /\ Head(toServer[c]).t = "Result"
    /\ LET r == Head(toServer[c]).r
           s1 == ResultStatus(r, lastState, status[r.j], owner[r.j])
           transitioned == s1 # status[r.j]
       IN /\ toServer' = [toServer EXCEPT ![c] = Tail(@)]
          /\ IF ~(Current(c) /\ resumed)
             THEN /\ replies' = [replies EXCEPT ![c] = @ \cup {Reply("Ack", FALSE, r)}]
                  /\ UNCHANGED <<status, owner, awaitDiag, wake, readyFlag, toWorker, reportedFailed>>
             ELSE /\ status' = [status EXCEPT ![r.j] = s1]
                  /\ owner' = IF s1 = "Queued" THEN [owner EXCEPT ![r.j] = "none"] ELSE owner
                  /\ replies' = [replies EXCEPT ![c] = @ \cup {Reply("Ack", TRUE, r)}]
                  /\ wake' = TRUE
                  \* Only a real transition acts: a duplicate or a refused write asks nothing of the host
                  /\ IF transitioned /\ r.t = "JobCompleted" /\ r.s = "Failed"
                     THEN /\ awaitDiag' = TRUE
                          /\ toWorker' = [toWorker EXCEPT ![c] = Append(@, IRecheck)]
                          /\ reportedFailed' = reportedFailed \cup {r.j}
                     ELSE UNCHANGED <<awaitDiag, toWorker, reportedFailed>>
                  /\ readyFlag' = IF AltReadyFromTransitions THEN readyFlag \/ (transitioned /\ r.t = "JobCompleted") ELSE readyFlag
    /\ UNCHANGED <<assignedInst, last, curAlive, resumed, lastState, connInst, retired, srvDown>>
    /\ UNCHANGED stalled
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED <<lostFailed, handedBack, spurious>>

SrvHeartbeat(c) ==
    /\ SrvCanTake(c)
    /\ Head(toServer[c]).t = "Heartbeat"
    /\ toServer' = [toServer EXCEPT ![c] = Tail(@)]
    /\ replies' = [replies EXCEPT ![c] = @ \cup {Reply("Heartbeat", Current(c), NoResult)}]
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* The server's own timeout closes a connection: one the worker has left or that has gone silent, or (bounded) one that was in fact working. Jobs stay as they are.
SrvDisconnected(c) ==
    /\ c <= connsUsed
    /\ c \notin srvDown
    /\ \/ ~Listening(c) \/ c \in stalled
       \/ falseTimeoutsUsed < MaxFalseTimeouts /\ connsUsed < MaxConns
    /\ falseTimeoutsUsed' = IF Listening(c) THEN falseTimeoutsUsed + 1 ELSE falseTimeoutsUsed
    /\ srvDown' = srvDown \cup {c}
    /\ curAlive' = IF c = last THEN FALSE ELSE curAlive
    /\ resumed' = IF c = last THEN FALSE ELSE resumed
    /\ UNCHANGED <<status, owner, assignedInst, last, lastState, awaitDiag, wake, readyFlag, connInst, retired>>
    /\ UNCHANGED chanVars
    /\ UNCHANGED wVars /\ UNCHANGED <<stallsUsed, restartsUsed, cancelsUsed, opDiagsUsed, unhealthyUsed>> /\ UNCHANGED ghostVars

\* JobScheduler.DispatchAsync for W, under W's lock. Edge-triggered, as in the code: it runs when a handler asked for it, and assigns only if W is ready then.
SrvDispatch ==
    /\ wake
    /\ wake' = FALSE
    /\ IF Ready /\ Queued # {}
       THEN LET j == Min(Queued) IN
            /\ status' = [status EXCEPT ![j] = "Running"]
            /\ owner' = [owner EXCEPT ![j] = "W"]
            /\ assignedInst' = [assignedInst EXCEPT ![j] = connInst[last]]
            /\ toWorker' = [toWorker EXCEPT ![last] = Append(@, IAssign(j))]
            /\ readyFlag' = IF AltReadyFromTransitions THEN FALSE ELSE readyFlag
       ELSE UNCHANGED <<status, owner, assignedInst, toWorker, readyFlag>>
    /\ UNCHANGED <<last, curAlive, resumed, lastState, awaitDiag, connInst, retired, srvDown>>
    /\ UNCHANGED <<toServer, replies, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* The competitor: another worker taking any queued job, and finishing what it took
SrvCompetitorTake ==
    /\ WithCompetitor
    /\ \E j \in Queued :
        /\ status' = [status EXCEPT ![j] = "Running"]
        /\ owner' = [owner EXCEPT ![j] = "C"]
    /\ UNCHANGED <<assignedInst, last, curAlive, resumed, lastState, awaitDiag, wake, readyFlag, connInst, retired, srvDown>>
    /\ UNCHANGED chanVars /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

SrvCompetitorFinish ==
    /\ \E j \in Jobs :
        /\ owner[j] = "C"
        /\ status[j] = "Running"
        /\ status' = [status EXCEPT ![j] = "Success"]
    /\ UNCHANGED <<owner, assignedInst, last, curAlive, resumed, lastState, awaitDiag, wake, readyFlag, connInst, retired, srvDown>>
    /\ UNCHANGED chanVars /\ UNCHANGED wVars /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* JobScheduler.CancelJobAsync, under W's lock: cancel, and tell W if the job is W's
SrvCancel(j) ==
    /\ cancelsUsed < MaxCancels
    /\ status[j] \in {"Queued", "Running"}
    /\ cancelsUsed' = cancelsUsed + 1
    /\ status' = [status EXCEPT ![j] = "Cancelled"]
    /\ wake' = TRUE
    /\ toWorker' = IF owner[j] = "W" /\ curAlive THEN [toWorker EXCEPT ![last] = Append(@, ICancel(j))] ELSE toWorker
    /\ UNCHANGED <<owner, assignedInst, last, curAlive, resumed, lastState, awaitDiag, readyFlag, connInst, retired, srvDown>>
    /\ UNCHANGED <<toServer, replies, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED <<stallsUsed, falseTimeoutsUsed, restartsUsed, opDiagsUsed, unhealthyUsed>> /\ UNCHANGED ghostVars

\* "Run diagnostic" on the worker page, under W's lock: no work until it has started
SrvOperatorDiagnostic ==
    /\ opDiagsUsed < MaxOperatorDiags
    /\ curAlive /\ resumed
    /\ opDiagsUsed' = opDiagsUsed + 1
    /\ awaitDiag' = TRUE
    /\ toWorker' = [toWorker EXCEPT ![last] = Append(@, IRunDiagnostic)]
    /\ UNCHANGED <<status, owner, assignedInst, last, curAlive, resumed, lastState, wake, readyFlag, connInst, retired, srvDown>>
    /\ UNCHANGED <<toServer, replies, stalled>>
    /\ UNCHANGED wVars /\ UNCHANGED <<stallsUsed, falseTimeoutsUsed, restartsUsed, cancelsUsed, unhealthyUsed>> /\ UNCHANGED ghostVars

-----------------------------------------------------------------------------
\* Worker. Every change to its activity happens under its lock, and reports the new state afterwards, in order with its results.

\* Report the state after a change, if there's a resumed connection to report it on (Resume carries it otherwise)
ReportState(st) ==
    toServer' = IF Live THEN [toServer EXCEPT ![wConn] = Append(@, MState(st))] ELSE toServer

\* Record a result and, if live, send it; it stays recorded until acknowledged
ReportResult(r, st) ==
    /\ wResults' = wResults \cup {r}
    /\ toServer' = IF Live THEN [toServer EXCEPT ![wConn] = Append(Append(@, MResult(r)), MState(st))] ELSE toServer

\* Where W takes invocations from: its current connection only. With AltNoInvocationEpoch, also anything still queued on its own older connections.
InvocationConns ==
    IF AltNoInvocationEpoch
    THEN {c \in Conns : c <= wConn /\ connInst[c] = wInst /\ c \notin stalled}
    ELSE {wConn}

\* The next invocation on connection c is of type t (W must be connected and its current connection reachable)
CanTake(c, t) == Listening(wConn) /\ c \in InvocationConns /\ toWorker[c] # <<>> /\ Head(toWorker[c]).t = t

\* An assignment: run it if idle; otherwise hand it back
WOnAssign ==
    /\ ~wLock
    /\ Room
    /\ \E c \in Conns :
        /\ CanTake(c, "AssignJob")
        /\ toWorker' = [toWorker EXCEPT ![c] = Tail(@)]
        /\ LET j == Head(toWorker[c]).j IN
           IF wAct = "Idle"
           THEN /\ wAct' = "Job"
                /\ wJob' = j
                /\ wJobDone' = "none"
                /\ wCancelReq' = FALSE
                /\ ReportState(State("Job", j, wDiag))
                /\ UNCHANGED <<wResults, handedBack>>
           ELSE /\ ReportResult(RRejected(j), MyState)
                /\ handedBack' = TRUE
                /\ UNCHANGED <<wAct, wJob, wJobDone, wCancelReq>>
    /\ UNCHANGED srvVars /\ UNCHANGED <<replies, stalled>>
    /\ UNCHANGED <<wInst, wConn, wUp, connsUsed, wSync, wLock, wDiag, wDiagWanted, wResumeSet, wHeartbeat>>
    /\ UNCHANGED budgetVars /\ UNCHANGED <<reportedFailed, lostFailed, spurious>>

WOnCancel ==
    /\ \E c \in Conns :
        /\ CanTake(c, "CancelJob")
        /\ toWorker' = [toWorker EXCEPT ![c] = Tail(@)]
        /\ wCancelReq' = (wCancelReq \/ (wAct = "Job" /\ wJob = Head(toWorker[c]).j))
    /\ UNCHANGED srvVars /\ UNCHANGED <<toServer, replies, stalled>>
    /\ UNCHANGED <<wInst, wConn, wUp, connsUsed, wSync, wLock, wAct, wJob, wJobDone, wDiag, wDiagWanted, wResults, wResumeSet, wHeartbeat>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* A host re-check or operator diagnostic: start now if idle, else as soon as the worker is. A request that arrives during a diagnostic gets a fresh one afterwards: the server can't tell which of the two came first.
WOnDiagnosticRequest ==
    /\ ~wLock
    /\ Room
    /\ \E c \in Conns : \E t \in {"RecheckHost", "RunDiagnostic"} :
        /\ CanTake(c, t)
        /\ toWorker' = [toWorker EXCEPT ![c] = Tail(@)]
    /\ IF wAct = "Idle"
       THEN /\ wAct' = "Diag"
            /\ ReportState(State("Diag", NoJob, wDiag))
            /\ UNCHANGED wDiagWanted
       ELSE /\ wDiagWanted' = (wDiagWanted \/ ~(AltFoldDiagnostic /\ wAct = "Diag"))
            /\ UNCHANGED <<wAct, toServer>>
    /\ UNCHANGED srvVars /\ UNCHANGED <<replies, stalled>>
    /\ UNCHANGED <<wInst, wConn, wUp, connsUsed, wSync, wLock, wJob, wJobDone, wCancelReq, wDiag, wResults, wResumeSet, wHeartbeat>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* A diagnostic starts by itself: one was asked for while busy, the last one was unhealthy (the retry loop), or there has been none since the process started
WDiagnosticStart ==
    /\ ~wLock
    /\ Room
    /\ wAct = "Idle"
    /\ wDiagWanted \/ wDiag # "Healthy"
    /\ wAct' = "Diag"
    /\ wDiagWanted' = FALSE
    /\ ReportState(State("Diag", NoJob, wDiag))
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED <<wInst, wConn, wUp, connsUsed, wSync, wLock, wJob, wJobDone, wCancelReq, wDiag, wResults, wResumeSet, wHeartbeat>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

WDiagnosticDone ==
    /\ ~wLock
    /\ Room
    /\ wAct = "Diag"
    /\ \E d \in IF unhealthyUsed < MaxUnhealthy THEN {"Healthy", "Unhealthy"} ELSE {"Healthy"} :
        /\ wDiag' = d
        /\ unhealthyUsed' = IF d = "Unhealthy" THEN unhealthyUsed + 1 ELSE unhealthyUsed
        /\ ReportState(State("Idle", NoJob, d))
    /\ wAct' = "Idle"
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED <<wInst, wConn, wUp, connsUsed, wSync, wLock, wJob, wJobDone, wCancelReq, wDiagWanted, wResults, wResumeSet, wHeartbeat>>
    /\ UNCHANGED <<stallsUsed, falseTimeoutsUsed, restartsUsed, cancelsUsed, opDiagsUsed>> /\ UNCHANGED ghostVars

WJobFinish ==
    /\ wAct = "Job"
    /\ wJobDone = "none"
    /\ wJobDone' \in (IF wCancelReq THEN {"Cancelled"} ELSE {"Success", "Failed"})
    /\ UNCHANGED srvVars /\ UNCHANGED chanVars
    /\ UNCHANGED <<wInst, wConn, wUp, connsUsed, wSync, wLock, wAct, wJob, wCancelReq, wDiag, wDiagWanted, wResults, wResumeSet, wHeartbeat>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* The job's result is recorded and sent, then the idle state
WReportCompletion ==
    /\ ~wLock
    /\ Room
    /\ wAct = "Job"
    /\ wJobDone # "none"
    /\ wAct' = "Idle"
    /\ wJob' = NoJob
    /\ wJobDone' = "none"
    /\ wCancelReq' = FALSE
    /\ ReportResult(RCompleted(wJob, wJobDone), State("Idle", NoJob, wDiag))
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED <<wInst, wConn, wUp, connsUsed, wSync, wLock, wDiag, wDiagWanted, wResumeSet, wHeartbeat>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* Giving up on the connection: nothing recorded is lost, a Resume in progress is abandoned
Abandon ==
    /\ wUp' = FALSE
    /\ wSync' = "none"
    /\ wLock' = FALSE
    /\ wHeartbeat' = FALSE
    /\ wResumeSet' = {}

\* A completion arrives: acknowledged results are done with; a fenced answer means reconnect
WOnReply ==
    /\ Listening(wConn)
    /\ \E r \in replies[wConn] :
        /\ replies' = [replies EXCEPT ![wConn] = @ \ {r}]
        /\ CASE r.t = "Ack" ->
                    IF r.ok
                    THEN /\ wResults' = wResults \ {r.r}
                         /\ UNCHANGED <<wUp, wSync, wLock, wHeartbeat, wResumeSet>>
                    ELSE /\ Abandon /\ UNCHANGED wResults
             [] r.t = "Heartbeat" ->
                    IF r.ok
                    THEN /\ wHeartbeat' = FALSE
                         /\ UNCHANGED <<wUp, wSync, wLock, wResumeSet, wResults>>
                    ELSE /\ Abandon /\ UNCHANGED wResults
             [] r.t = "Connect" /\ wSync = "connecting" ->
                    IF r.ok
                    THEN \* Take the lock, snapshot state and results into one Resume
                         /\ wSync' = "resuming"
                         /\ wLock' = TRUE
                         /\ wResumeSet' = wResults
                         /\ UNCHANGED <<wUp, wHeartbeat, wResults>>
                    ELSE /\ Abandon /\ UNCHANGED wResults
             [] r.t = "Resume" /\ wSync = "resuming" ->
                    IF r.ok
                    THEN /\ wResults' = wResults \ wResumeSet
                         /\ wSync' = "none"
                         /\ wLock' = FALSE
                         /\ wResumeSet' = {}
                         /\ UNCHANGED <<wUp, wHeartbeat>>
                    ELSE /\ Abandon /\ UNCHANGED wResults
             [] OTHER -> UNCHANGED <<wUp, wSync, wLock, wHeartbeat, wResumeSet, wResults>>
        /\ toServer' = IF r.t = "Connect" /\ wSync = "connecting" /\ r.ok
                       THEN [toServer EXCEPT ![wConn] = Append(@, MResume(MyState, wResults))]
                       ELSE toServer
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, stalled>>
    /\ UNCHANGED <<wInst, wConn, connsUsed, wAct, wJob, wJobDone, wCancelReq, wDiag, wDiagWanted>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* The periodic heartbeat, an invocation answered "ok" or "fenced": how a fenced but otherwise quiet worker finds out
WHeartbeat ==
    /\ Live
    /\ Room
    /\ ~wHeartbeat
    /\ wHeartbeat' = TRUE
    /\ toServer' = [toServer EXCEPT ![wConn] = Append(@, MHeartbeat)]
    /\ UNCHANGED srvVars /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED <<wInst, wConn, wUp, connsUsed, wSync, wLock, wAct, wJob, wJobDone, wCancelReq, wDiag, wDiagWanted, wResults, wResumeSet>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* The worker gives up on its connection: a network drop, its own timeout on a silent one, or the server having closed it. Only while a later connection is in the budget, so a bounded run never strands it offline for good.
WAbandon ==
    /\ wUp
    /\ connsUsed < MaxConns
    /\ Abandon
    /\ UNCHANGED srvVars /\ UNCHANGED chanVars
    /\ UNCHANGED <<wInst, wConn, connsUsed, wAct, wJob, wJobDone, wCancelReq, wDiag, wDiagWanted, wResults>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* Open a new connection: counter + 1 within this process
WConnect ==
    /\ ~wUp
    /\ connsUsed < MaxConns
    /\ LET c == connsUsed + 1 IN
       /\ connsUsed' = c
       /\ wConn' = c
       /\ connInst' = [connInst EXCEPT ![c] = wInst]
       /\ toServer' = [toServer EXCEPT ![c] = <<MConnect>>]
    /\ wUp' = TRUE
    /\ wSync' = "connecting"
    /\ UNCHANGED <<status, owner, assignedInst, last, curAlive, resumed, lastState, awaitDiag, wake, readyFlag, retired, srvDown>>
    /\ UNCHANGED <<toWorker, replies, stalled>>
    /\ UNCHANGED <<wInst, wLock, wAct, wJob, wJobDone, wCancelReq, wDiag, wDiagWanted, wResults, wResumeSet, wHeartbeat>>
    /\ UNCHANGED budgetVars /\ UNCHANGED ghostVars

\* The process dies and starts again: a new instance with nothing remembered, no diagnostic yet
WRestart ==
    /\ restartsUsed < MaxRestarts
    /\ connsUsed < MaxConns
    /\ restartsUsed' = restartsUsed + 1
    /\ wInst' = wInst + 1
    /\ wUp' = FALSE
    /\ wSync' = "none"
    /\ wLock' = FALSE
    /\ wAct' = "Idle"
    /\ wJob' = NoJob
    /\ wJobDone' = "none"
    /\ wCancelReq' = FALSE
    /\ wDiag' = "None"
    /\ wDiagWanted' = FALSE
    /\ wResults' = {}
    /\ wResumeSet' = {}
    /\ wHeartbeat' = FALSE
    /\ UNCHANGED srvVars /\ UNCHANGED chanVars
    /\ UNCHANGED <<wConn, connsUsed>>
    /\ UNCHANGED <<stallsUsed, falseTimeoutsUsed, cancelsUsed, opDiagsUsed, unhealthyUsed>> /\ UNCHANGED ghostVars

\* The current connection goes silent both ways while both ends still think it is up
Stall ==
    /\ stallsUsed < MaxStalls
    /\ connsUsed < MaxConns
    /\ wUp
    /\ wConn \notin stalled
    /\ stallsUsed' = stallsUsed + 1
    /\ stalled' = stalled \cup {wConn}
    /\ UNCHANGED srvVars /\ UNCHANGED <<toServer, toWorker, replies>>
    /\ UNCHANGED wVars /\ UNCHANGED <<falseTimeoutsUsed, restartsUsed, cancelsUsed, opDiagsUsed, unhealthyUsed>> /\ UNCHANGED ghostVars

-----------------------------------------------------------------------------
Next ==
    \/ \E c \in Conns :
        \/ SrvConnect(c) \/ SrvResume(c) \/ SrvState(c) \/ SrvResult(c) \/ SrvHeartbeat(c) \/ SrvDisconnected(c)
    \/ SrvDispatch \/ SrvCompetitorTake \/ SrvCompetitorFinish
    \/ \E j \in Jobs : SrvCancel(j)
    \/ SrvOperatorDiagnostic
    \/ WOnAssign \/ WOnCancel \/ WOnDiagnosticRequest \/ WDiagnosticStart \/ WDiagnosticDone
    \/ WJobFinish \/ WReportCompletion \/ WOnReply \/ WHeartbeat
    \/ WAbandon \/ WConnect \/ WRestart \/ Stall

Spec == Init /\ [][Next]_vars

\* Everything the system does by itself eventually happens. Left unforced: connection failures, restarts, cancels, operator clicks, and abandoning a connection that still works.
Fairness ==
    /\ \A c \in Conns :
        WF_vars(SrvConnect(c)) /\ WF_vars(SrvResume(c)) /\ WF_vars(SrvState(c)) /\ WF_vars(SrvResult(c)) /\ WF_vars(SrvHeartbeat(c))
    \* The server's timeout, for a connection the worker has left or that has gone silent
    /\ \A c \in Conns : WF_vars(SrvDisconnected(c) /\ (~Listening(c) \/ c \in stalled))
    /\ WF_vars(SrvDispatch) /\ WF_vars(SrvCompetitorTake) /\ WF_vars(SrvCompetitorFinish)
    /\ WF_vars(WOnAssign) /\ WF_vars(WOnCancel) /\ WF_vars(WOnDiagnosticRequest) /\ WF_vars(WDiagnosticStart) /\ WF_vars(WDiagnosticDone)
    /\ WF_vars(WJobFinish) /\ WF_vars(WReportCompletion) /\ WF_vars(WOnReply) /\ WF_vars(WHeartbeat)
    /\ WF_vars(WConnect)
    \* The worker's timeout on a silent connection, and its noticing that the server closed one
    /\ WF_vars(WAbandon /\ (wConn \in stalled \/ wConn \in srvDown))

FairSpec == Spec /\ Fairness

-----------------------------------------------------------------------------
\* Properties

TypeOK ==
    /\ status \in [Jobs -> Statuses]
    /\ owner \in [Jobs -> Owners]
    /\ wAct \in {"Idle", "Job", "Diag"}
    /\ wDiag \in Diags
    /\ wSync \in {"none", "connecting", "resuming"}
    /\ (wAct = "Job") <=> (wJob \in Jobs)

\* W will be told to stop j, or will find out when it next resumes
StopPending(j) ==
    \/ ~Live \/ wConn \in stalled \/ wConn \in srvDown \/ wConn # last
    \/ ICancel(j) \in Range(toWorker[wConn])
    \/ wCancelReq
    \/ wJobDone # "none"

\* The server never silently gives up on a job W is still running
NoUntrackedRun ==
    (wAct = "Job" /\ ~(status[wJob] = "Running" /\ owner[wJob] = "W")) => StopPending(wJob)

\* A job is Failed only because W's run of it failed, or because the process it was assigned to died
NoUnexplainedFailure == \A j \in Jobs : status[j] = "Failed" => j \in reportedFailed \cup lostFailed

\* A job is failed as lost only when the process it was assigned to has died and the live process doesn't have it
NoSpuriousFailure == ~spurious

\* Two workers never run the same job at once
NoDoubleRun == ~(wAct = "Job" /\ owner[wJob] = "C" /\ status[wJob] = "Running")

\* The incident: W runs a job the server failed as lost, and the server considers it ready for more
NotIncident == ~(wAct = "Job" /\ wJob \in lostFailed /\ Ready)

\* Non-vacuity witnesses: each is expected to be violated in the configuration the script names, showing the path the properties above guard is actually taken
NeverHandsBack == ~handedBack
NeverLosesAJob == lostFailed = {}

\* Every job eventually reaches a final status
AllJobsSettle == <>(\A j \in Jobs : status[j] \in Outcomes)

\* A connected, idle, healthy worker is eventually seen by the server as ready for work
IdleAndHealthy == Live /\ wConn = last /\ wAct = "Idle" /\ wDiag = "Healthy" /\ ~wDiagWanted
WorkerNotStranded == []<>(Ready \/ ~IdleAndHealthy)

=============================================================================
