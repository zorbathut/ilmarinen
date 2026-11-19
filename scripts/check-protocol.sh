#!/usr/bin/env bash
# Model-checks the server-worker protocol (spec/, see spec/README.md):
#   - the protocol must keep every property, in each configuration;
#   - the paths those properties guard must actually be taken (non-vacuity witnesses);
#   - each design element, replaced by its realistic alternative, must break the property pinned for it.
#
# Usage: scripts/check-protocol.sh [quick]
#   quick: skips the large configurations, for iterating on the spec
set -euo pipefail

case "${1:-}" in
    "") QUICK=0 ;;
    quick) QUICK=1 ;;
    *) echo "usage: $0 [quick]" >&2; exit 2 ;;
esac

TLA_VERSION=1.7.4
TLA_SHA256=936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88
# Generous enough for the largest configuration; a run that exceeds it reports "error"
RUN_TIMEOUT=1800

jar_ok() {
    [[ -f "$1" ]] && echo "$TLA_SHA256  $1" | sha256sum --check --quiet --status
}
# The Arch AUR package tla-tools installs the same release here
JAR=/usr/share/java/tla2tools.jar
if ! jar_ok "$JAR"; then
    JAR="${XDG_CACHE_HOME:-$HOME/.cache}/ilmarinen/tla2tools-$TLA_VERSION.jar"
    if ! jar_ok "$JAR"; then
        mkdir -p "$(dirname "$JAR")"
        curl -fsSL -o "$JAR.partial" "https://github.com/tlaplus/tlaplus/releases/download/v$TLA_VERSION/tla2tools.jar"
        if ! jar_ok "$JAR.partial"; then
            rm -f "$JAR.partial"
            echo "downloaded tla2tools.jar doesn't match the pinned SHA-256" >&2
            exit 1
        fi
        mv "$JAR.partial" "$JAR"
    fi
fi

SPEC_DIR="$(cd "$(dirname "$0")/../spec" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
failures=0

# tlc MODULE NAME CFG-BODY: runs TLC, echoes the violated invariant's name, "liveness", "deadlock", "none", or "error"
tlc() {
    local module="$1" name="$2" body="$3"
    local cfg="$WORK/$name.cfg" out="$WORK/$name.out"
    echo "$body" > "$cfg"
    cp "$SPEC_DIR/$module.tla" "$WORK/"
    (cd "$WORK" && timeout "$RUN_TIMEOUT" java -XX:+UseParallelGC -cp "$JAR" tlc2.TLC -workers auto -metadir "$WORK/meta-$name" -config "$cfg" "$module.tla") > "$out" 2>&1 || true
    if grep -q "Model checking completed. No error has been found" "$out"; then
        echo none
    elif violated=$(grep -oP '^Error: Invariant \K\w+(?= is violated)' "$out"); then
        echo "$violated"
    elif grep -q "^Error: Temporal properties were violated" "$out"; then
        echo liveness
    elif grep -q "^Error: Deadlock reached" "$out"; then
        echo deadlock
    else
        echo error
    fi
}

expect() {
    local name="$1" got="$2" want="$3"
    if [[ "$got" == "$want" ]]; then
        printf '  ok    %-44s %s\n' "$name" "$got"
    else
        printf '  FAIL  %-44s got %s, wanted %s\n' "$name" "$got" "$want"
        tail -40 "$WORK/$name.out" 2>/dev/null | sed 's/^/        /'
        failures=$((failures + 1))
    fi
}

# ---------------------------------------------------------------------------
# NumJobs MaxConns MaxStalls MaxFalseTimeouts MaxRestarts MaxCancels MaxOperatorDiags WithCompetitor MaxUnhealthy
declare -A FEATURE=(
    [base]="2 2 0 0 0 0 0 FALSE 0"
    [stall]="2 2 1 0 0 0 0 FALSE 0"
    [falsetimeout]="2 2 0 1 0 0 0 FALSE 0"
    [cancel]="2 2 0 0 0 1 0 FALSE 0"
    [opdiag]="2 2 0 0 0 0 1 FALSE 0"
    [unhealthy]="2 2 0 0 0 0 0 FALSE 1"
    [competitor]="2 2 0 0 0 0 0 TRUE 0"
    [cancel-opdiag]="2 2 0 0 0 1 1 FALSE 0"
    [competitor-opdiag]="2 2 0 0 0 0 1 TRUE 0"
    [reorder]="2 3 0 0 0 0 0 FALSE 0"
    [restart1]="1 3 0 0 1 0 0 FALSE 0"
    [restart]="2 3 0 0 1 0 0 FALSE 0"
)
SMALL=(base stall falsetimeout cancel opdiag unhealthy competitor cancel-opdiag competitor-opdiag)
LARGE=(reorder restart1)
ALTS=(AltClockEpochs AltReconcileBeforeResume AltFailCollision AltNoHandBackGuard AltFailUndelivered AltNoAbortOnResume AltFoldDiagnostic AltReadyFromTransitions AltStateDoesNotDispatch AltNoInvocationEpoch)
SAFETY="TypeOK NoUntrackedRun NoUnexplainedFailure NoSpuriousFailure NoDoubleRun NotIncident"
LIVENESS="AllJobsSettle WorkerNotStranded"

# new_cfg FEATURE SPEC ALT DEADLOCK INVARIANTS PROPERTIES
new_cfg() {
    local nj mc ms mf mr mca mo wc mu
    read -r nj mc ms mf mr mca mo wc mu <<< "${FEATURE[$1]}"
    echo "SPECIFICATION $2"
    echo "CONSTANTS"
    echo "NumJobs = $nj"; echo "MaxConns = $mc"; echo "MaxStalls = $ms"; echo "MaxFalseTimeouts = $mf"; echo "MaxRestarts = $mr"
    echo "MaxCancels = $mca"; echo "MaxOperatorDiags = $mo"; echo "WithCompetitor = $wc"; echo "MaxUnhealthy = $mu"
    echo "MaxQueue = 4"
    for a in "${ALTS[@]}"; do if [[ "$3" == "$a" ]]; then echo "$a = TRUE"; else echo "$a = FALSE"; fi; done
    echo "CHECK_DEADLOCK $4"
    echo "INVARIANTS $5"
    if [[ -n "$6" ]]; then echo "PROPERTIES $6"; fi
}

# check FEATURE ALT DEADLOCK: safety, then (if that holds) liveness; echoes the first failure, or "none"
check() {
    local f="$1" alt="$2" deadlock="$3" name="$1-${2:-design}" got
    got=$(tlc WorkerProtocol "$name" "$(new_cfg "$f" Spec "$alt" "$deadlock" "$SAFETY" "")")
    if [[ "$got" == none ]]; then
        got=$(tlc WorkerProtocol "$name-live" "$(new_cfg "$f" FairSpec "$alt" "$deadlock" TypeOK "$LIVENESS")")
    fi
    echo "$got"
}

features=("${SMALL[@]}")
if (( ! QUICK )); then features+=("${LARGE[@]}"); fi

echo "The protocol: every property must hold"
for f in "${features[@]}"; do
    expect "design-$f" "$(check "$f" "" TRUE)" none
done
if (( ! QUICK )); then
    # Liveness at two jobs with a restart is beyond TLC in reasonable time: safety here, liveness at one job above
    expect "design-restart-safety" "$(tlc WorkerProtocol restart-design "$(new_cfg restart Spec "" TRUE "$SAFETY" "")")" none
fi

echo "Non-vacuity: the guarded paths are taken"
expect "witness-hand-back" "$(tlc WorkerProtocol witness-hand-back "$(new_cfg opdiag Spec "" FALSE "TypeOK NeverHandsBack" "")")" NeverHandsBack
if (( ! QUICK )); then
    expect "witness-lost-job" "$(tlc WorkerProtocol witness-lost-job "$(new_cfg restart1 Spec "" FALSE "TypeOK NeverLosesAJob" "")")" NeverLosesAJob
fi

echo "Each design element replaced by its alternative must break what it protects"
# Deadlock checking off for these: running out of the bounded reconnects isn't the failure being shown
(( QUICK )) || expect "alt-AltClockEpochs" "$(check restart1 AltClockEpochs FALSE)" liveness
expect "alt-AltReconcileBeforeResume" "$(check base AltReconcileBeforeResume FALSE)" NoUntrackedRun
expect "alt-AltFailCollision" "$(check opdiag AltFailCollision FALSE)" NoUnexplainedFailure
expect "alt-AltNoHandBackGuard" "$(check competitor-opdiag AltNoHandBackGuard FALSE)" NoDoubleRun
expect "alt-AltFailUndelivered" "$(check base AltFailUndelivered FALSE)" NoSpuriousFailure
expect "alt-AltNoAbortOnResume" "$(check cancel AltNoAbortOnResume FALSE)" NoUntrackedRun
expect "alt-AltFoldDiagnostic" "$(check opdiag AltFoldDiagnostic FALSE)" liveness
expect "alt-AltReadyFromTransitions" "$(check cancel AltReadyFromTransitions FALSE)" liveness
expect "alt-AltStateDoesNotDispatch" "$(check base AltStateDoesNotDispatch FALSE)" liveness
expect "alt-AltNoInvocationEpoch" "$(check stall AltNoInvocationEpoch FALSE)" NoUntrackedRun

if (( failures )); then
    echo "$failures check(s) failed"
    exit 1
fi
echo "All checks passed"
