#!/usr/bin/env bash
#
# kill-unity.sh — kill Unity processes on macOS (with optional snapshot)
#
# Usage:
#   kill-unity.sh                # kill Unity Editor (and child processes), keep Hub
#   kill-unity.sh --editor | -e  # same as above
#   kill-unity.sh --hub          # kill only Unity Hub
#   kill-unity.sh --all  | -a    # kill Editor + Hub
#   kill-unity.sh --list | -l    # list processes only, do not kill
#   kill-unity.sh --help | -h    # show this help

set -u

EDITOR_PATTERN='Unity\.app/Contents/MacOS/Unity'
HUB_PATTERN='Unity Hub\.app'
UNITY_LOG="${HOME}/Library/Logs/Unity/Editor.log"
SNAPSHOT_BASE="${HOME}/Library/Application Support/MacGPUSafeGuard/snapshots"
WATCHDOG_LOG="${HOME}/Library/Application Support/MacGPUSafeGuard/watchdog/watchdog.log"

ts() { date '+%Y-%m-%d %H:%M:%S'; }
log_watchdog() {
    echo "[$(ts)] $*" >> "$WATCHDOG_LOG"
}

print_help() {
    sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
}

list_pids() {
    local label="$1" pattern="$2"
    local pids
    pids=$(pgrep -f "$pattern" || true)
    if [[ -z "$pids" ]]; then
        printf '  %-15s (none)\n' "$label"
        return 1
    fi
    printf '  %s:\n' "$label"
    ps -o pid=,comm= -p $pids | sed 's/^/    /'
    return 0
}

save_snapshot() {
    local pid="$1"
    local label="$2"
    local stamp
    stamp=$(date '+%Y%m%d_%H%M%S')
    local dir="${SNAPSHOT_BASE}/${label}_${stamp}"
    mkdir -p "$dir"

    if [[ -f "$UNITY_LOG" ]]; then
        cp "$UNITY_LOG" "${dir}/Editor.log"
    fi

    local sample_file="${dir}/sample.txt"
    sample "$pid" 3 -file "$sample_file" >/dev/null 2>&1 || true

    {
        echo "timestamp=$(date '+%Y-%m-%d %H:%M:%S')"
        echo "pid=$pid"
        echo "label=$label"
        echo '--- ps ---'
        ps -p "$pid" -o pid,ppid,stat,%cpu,etime,command || true
        echo '--- recent MacGPUSafeGuard ---'
        [[ -f "${dir}/Editor.log" ]] && grep -n '\[MacGPUSafeGuard\]' "${dir}/Editor.log" | tail -40 || true
        echo '--- recent ShadowCache out of range ---'
        [[ -f "${dir}/Editor.log" ]] && grep -n 'The RT of per object shadow is out of range!' "${dir}/Editor.log" | tail -40 || true
        echo '--- recent crash-skipping draws ---'
        [[ -f "${dir}/Editor.log" ]] && grep -n 'Skipping draw calls to avoid crashing\|ComputeBuffer.*none provided' "${dir}/Editor.log" | tail -40 || true
    } > "${dir}/summary.txt"

    echo "snapshot saved => ${dir}"
}

kill_pattern() {
    local label="$1" pattern="$2"
    local pids
    pids=$(pgrep -f "$pattern" || true)
    if [[ -z "$pids" ]]; then
        echo "  $label: no process"
        return 0
    fi

    # save snapshot before kill
    local first_pid
    first_pid=$(echo "$pids" | head -1)
    if [[ -n "$first_pid" ]]; then
        save_snapshot "$first_pid" "$label"
    fi

    echo "  $label: killing $(echo "$pids" | wc -l | tr -d ' ') process(es)"
    kill -9 $pids 2>/dev/null || true
    log_watchdog "manual kill: $label pids=$pids"
    sleep 0.3
    local remain
    remain=$(pgrep -f "$pattern" || true)
    if [[ -n "$remain" ]]; then
        echo "  $label: WARN, still running: $remain"
        log_watchdog "manual kill: $label failed, still running: $remain"
        return 1
    fi
    echo "  $label: done"
    return 0
}

mode="editor"
case "${1:-}" in
    -h|--help)   print_help; exit 0 ;;
    -l|--list)   mode="list" ;;
    -a|--all)    mode="all" ;;
    --hub)       mode="hub" ;;
    -e|--editor) mode="editor" ;;
    "")          ;;
    *)
        echo "unknown arg: $1" >&2
        print_help
        exit 2
        ;;
esac

case "$mode" in
    list)
        echo "Unity processes:"
        list_pids "Editor" "$EDITOR_PATTERN" || true
        list_pids "Hub"    "$HUB_PATTERN"    || true
        ;;
    editor)
        echo "Killing Unity Editor (Hub kept):"
        kill_pattern "Editor" "$EDITOR_PATTERN"
        ;;
    hub)
        echo "Killing Unity Hub:"
        kill_pattern "Hub" "$HUB_PATTERN"
        ;;
    all)
        echo "Killing Unity Editor + Hub:"
        kill_pattern "Editor" "$EDITOR_PATTERN"
        kill_pattern "Hub"    "$HUB_PATTERN"
        ;;
esac
