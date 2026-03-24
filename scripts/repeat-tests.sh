#!/usr/bin/env bash
# Runs `dotnet test` N times, logging each run's full output.
# Usage: ./scripts/repeat-tests.sh [RUNS] [EXTRA_DOTNET_TEST_ARGS...]
# Example: ./scripts/repeat-tests.sh 10 --filter "Category=Integration"

set -euo pipefail

RUNS="${1:-10}"
shift 2>/dev/null || true

TIMESTAMP=$(date +%Y%m%d_%H%M%S)
LOG_DIR="test-runs/${TIMESTAMP}"
mkdir -p "$LOG_DIR"

SUMMARY_FILE="${LOG_DIR}/summary.txt"
echo "Test repeat run: ${RUNS} iterations" > "$SUMMARY_FILE"
echo "Started: $(date -Iseconds)" >> "$SUMMARY_FILE"
echo "Args: $*" >> "$SUMMARY_FILE"
echo "---" >> "$SUMMARY_FILE"

PASS=0
FAIL=0

for i in $(seq 1 "$RUNS"); do
    RUN_LOG="${LOG_DIR}/run_${i}.log"
    printf "Run %2d/%d ... " "$i" "$RUNS"

    START=$(date +%s%N)
    if dotnet test --no-restore --logger "console;verbosity=detailed" "$@" > "$RUN_LOG" 2>&1; then
        ELAPSED=$(( ($(date +%s%N) - START) / 1000000 ))
        printf "PASS (%d ms)\n" "$ELAPSED"
        echo "Run ${i}: PASS (${ELAPSED} ms)" >> "$SUMMARY_FILE"
        PASS=$((PASS + 1))
    else
        EXIT_CODE=$?
        ELAPSED=$(( ($(date +%s%N) - START) / 1000000 ))
        printf "FAIL (exit %d, %d ms) — see %s\n" "$EXIT_CODE" "$ELAPSED" "$RUN_LOG"
        echo "Run ${i}: FAIL (exit ${EXIT_CODE}, ${ELAPSED} ms)" >> "$SUMMARY_FILE"
        FAIL=$((FAIL + 1))
    fi
done

echo "---" >> "$SUMMARY_FILE"
echo "Finished: $(date -Iseconds)" >> "$SUMMARY_FILE"
echo "Result: ${PASS} passed, ${FAIL} failed out of ${RUNS}" >> "$SUMMARY_FILE"

echo ""
echo "=== Summary ==="
echo "${PASS} passed, ${FAIL} failed out of ${RUNS}"
echo "Logs: ${LOG_DIR}/"
echo "Summary: ${SUMMARY_FILE}"

# Exit non-zero if any run failed
[ "$FAIL" -eq 0 ]
