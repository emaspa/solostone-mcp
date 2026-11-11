#!/bin/bash
# The SoloStone lab: a second Linux install with a test-hooks build, in its own XDG folders, so the user's own install,
# saves and window are never touched. The MCP server's live_* tools connect to it.
#
#   lab.sh build [worktree]   stage a --hooks build of the worktree (default ~/solostone-merge) and package it for Linux
#   lab.sh install [tarball]  install the last build (or a tarball) into the lab and set it up from ~/games/Hearthstone
#   lab.sh play [--window]    start the lab game, headless in gamescope unless --window
#   lab.sh stop               stop the lab game
#   lab.sh status             what is installed and running
set -euo pipefail
LAB=${SOLOSTONE_LAB:-$HOME/.local/share/solostone-lab}
SOURCE=${SOLOSTONE_LAB_SOURCE:-$HOME/games/Hearthstone}
REFS=${SOLOSTONE_REFERENCES:-$HOME/solostone/references}
export PATH=$HOME/.dotnet:$PATH
export XDG_DATA_HOME=$LAB/data XDG_CONFIG_HOME=$LAB/config XDG_STATE_HOME=$LAB/state
L=$LAB/data/SoloStone/launcher
mkdir -p "$LAB/builds" "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$XDG_STATE_HOME"

game_pids() {
  # Hearthstone.exe processes whose WINEPREFIX is the lab's
  for p in $(pgrep -f '[H]earthstone\.exe' || true); do
    tr '\0' '\n' < /proc/$p/environ 2>/dev/null | grep -q "^WINEPREFIX=$LAB/" && echo $p
  done
}

case "${1:-status}" in
  build)
    WT=$(realpath "${2:-$HOME/solostone-merge}")
    REV=$(git -C "$WT" rev-parse --short HEAD)
    N=$(( $(ls "$LAB/builds" | grep -c '^lab\.' || true) + 1 ))
    V=0.2.0-lab.$N
    B=$LAB/builds/lab.$N
    rm -rf "$B"; mkdir -p "$B"
    echo "building $V from $WT@$REV"
    # Build in a throwaway worktree: the source tree's HEAD, its uncommitted changes, then the lab patches
    # (patches/*.patch, never upstream). The source tree itself is not touched.
    SRC=$LAB/src
    git -C "$WT" worktree remove --force "$SRC" 2>/dev/null || rm -rf "$SRC"
    git -C "$WT" worktree prune
    git -C "$WT" worktree add -q --detach "$SRC" HEAD
    git -C "$WT" diff HEAD --binary > "$B/uncommitted.diff"
    [ -s "$B/uncommitted.diff" ] && git -C "$SRC" apply --whitespace=nowarn "$B/uncommitted.diff"
    DIRTY=$(git -C "$WT" diff HEAD --stat | tail -1)
    for p in "$(dirname "$(realpath "$0")")"/patches/*.patch; do git -C "$SRC" apply "$p" && echo "  patch $(basename "$p")"; done
    (cd "$SRC" && python3 installer/stage.py --hooks --dotnet "$(command -v dotnet)" --references "$REFS" --out "$B/stage" --version "$V") > "$B/stage.log" 2>&1 \
      || { tail -30 "$B/stage.log"; exit 1; }
    (cd "$SRC" && python3 installer/package_linux.py --staged "$B/stage" --version "$V" --out "$B/linux" --cache "$LAB/linux-cache") > "$B/package.log" 2>&1 \
      || { tail -30 "$B/package.log"; exit 1; }
    git -C "$WT" worktree remove --force "$SRC"; git -C "$WT" worktree prune
    [ -n "$DIRTY" ] && REV="$REV+dirty($DIRTY)"
    T=$(ls "$B"/linux/*.tar.gz | head -1)
    echo "$WT@$REV" > "$B/source.txt"
    echo "$T"
    ;;
  install)
    T=${2:-$(ls -t "$LAB"/builds/lab.*/linux/*.tar.gz 2>/dev/null | head -1)}
    [ -f "$T" ] || { echo "no build; run lab.sh build first"; exit 1; }
    [ -z "$(game_pids)" ] || { echo "lab game is running; lab.sh stop first"; exit 1; }
    if [ -x "$L/solostone" ]; then "$L/solostone" uninstall >/dev/null 2>&1 || true; fi
    mkdir -p "$L" && tar -xzf "$T" --strip-components=1 -C "$L"
    "$L/solostone" setup --source "$SOURCE" --accept-warning 2>&1 | tail -3
    cat "$L/release.json"
    ;;
  play)
    [ -x "$L/solostone" ] || { echo "not installed; lab.sh build && lab.sh install"; exit 1; }
    [ -z "$(game_pids)" ] || { echo "already running: $(game_pids)"; exit 0; }
    LOG=$LAB/play.log
    if [ "${2:-}" = "--window" ] || ! command -v gamescope >/dev/null; then
      nohup "$L/solostone" play --accept-warning > "$LOG" 2>&1 &
    else
      nohup gamescope --backend headless -W 1600 -H 900 -r 30 -- "$L/solostone" play --accept-warning > "$LOG" 2>&1 &
    fi
    for i in $(seq 1 90); do
      [ -n "$(game_pids)" ] && grep -qs "runId\|^[0-9a-f-]\{36\}" "$LAB"/data/SoloStone/prefix/drive_c/users/*/AppData/Local/SoloStone/reports/plugin-loaded.txt \
        && [ "$(find "$LAB"/data/SoloStone/prefix/drive_c/users/*/AppData/Local/SoloStone/reports/plugin-loaded.txt -newer "$LOG" 2>/dev/null)" ] \
        && { echo "running: $(game_pids | tr '\n' ' ')"; exit 0; }
      sleep 2
    done
    echo "no plugin start after 180s; see $LOG"; tail -20 "$LOG"; exit 1
    ;;
  stop)
    P=$(game_pids)
    [ -n "$P" ] && kill $P && echo "stopped $P" || echo "not running"
    pkill -f "gamescope --backend headless -W 1600" 2>/dev/null || true
    ;;
  status)
    [ -f "$L/release.json" ] && grep version "$L/release.json" || echo "not installed"
    ls -d "$LAB"/builds/lab.* 2>/dev/null | while read b; do echo "$(basename $b): $(cat $b/source.txt 2>/dev/null)"; done
    P=$(game_pids); echo "running: ${P:-no}"
    ;;
  *) sed -n 2,10p "$0"; exit 1 ;;
esac
