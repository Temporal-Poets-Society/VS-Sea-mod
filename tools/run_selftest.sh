#!/usr/bin/env bash
# Runs Sea's server self-test on a local dedicated server: a small fresh world, then checks that Sea leaves vanilla's
# weather alone, the wind without and with a provider, a fixed wind, the waves, the ring (with Cylinder World) and speed.
# Prints the SELFTEST lines and stops.
#   dotnet build -c Release && tools/run_selftest.sh [--with-cworld]
# Packages (each may also be a mod folder):
#   SEA_ZIP  default this checkout's release/sea-<modinfo version>.zip, else its bin/Release/mod
#   CW_ZIP   (--with-cworld) default the newest cylinderworld-*.zip in the suite's Cworld/release or the player's
#            VintagestoryData/Mods
# TESTSERVER (default <this checkout>/testserver) holds the server's data; PORT overrides the template's port.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TS="${TESTSERVER:-$ROOT/testserver}"
VS="${VINTAGE_STORY:-$APPDATA/Vintagestory}"
PLAYER="$(cygpath -u "$APPDATA")/VintagestoryData"
# the suite root: the first folder above this checkout with suite.json (a worktree sits deeper than the main checkout)
SUITE="$ROOT"; while [ "$SUITE" != "/" ] && [ ! -f "$SUITE/suite.json" ]; do SUITE="$(dirname "$SUITE")"; done
TEMPLATE="${TEMPLATE_CONFIG:-$SUITE/Earth/testserver/serverconfig.json}"
WITH_CWORLD=0
[ "${1:-}" = "--with-cworld" ] && WITH_CWORLD=1
# the newest <id>-<version>.zip in the given folders, by version (a pre-release sorts below its release)
newest() {
  python - "$@" <<'EOF'
import glob, os, re, sys
mod, dirs = sys.argv[1], sys.argv[2:]
def key(p):
    m = re.match(re.escape(mod) + r"-(\d+)\.(\d+)\.(\d+)(?:-(.+))?\.zip$", os.path.basename(p))
    if not m: return ((0,), 0, ())
    pre = m.group(4)
    return (tuple(int(g) for g in m.group(1, 2, 3)), 0 if pre else 1,
            tuple((0, int(t), "") if t.isdigit() else (1, 0, t) for t in re.split(r"[.-]", pre or "")))
found = [p for d in dirs for p in glob.glob(os.path.join(d, mod + "-*.zip"))]
print(max(found, key=key) if found else "")
EOF
}
version() { sed -n 's/.*"version"[^"]*"\([^"]*\)".*/\1/p' "$1/modinfo.json" | head -1; }
SEA_ZIP="${SEA_ZIP:-$ROOT/release/sea-$(version "$ROOT").zip}"
[ -e "$SEA_ZIP" ] || SEA_ZIP="$ROOT/bin/Release/mod"
[ -e "$SEA_ZIP" ] || { echo "no Sea build: run dotnet build -c Release, or set SEA_ZIP" >&2; exit 1; }
echo "Sea: $SEA_ZIP"
if [ "$WITH_CWORLD" = 1 ]; then
  CW_ZIP="${CW_ZIP:-$(newest cylinderworld "$SUITE/Cworld/release" "$PLAYER/Mods")}"
  [ -n "$CW_ZIP" ] && [ -e "$CW_ZIP" ] || { echo "no Cylinder World package: set CW_ZIP" >&2; exit 1; }
  echo "Cylinder World: $CW_ZIP"
fi

mkdir -p "$TS/Mods" "$TS/ModConfig"
# a package is copied as it is: a zip, or a mod folder under its own name
add() { if [ -d "$1" ]; then cp -r "$1" "$TS/Mods/$2"; else cp "$1" "$TS/Mods/"; fi; }
rm -f "$TS/Mods/"*.zip
rm -rf "$TS/Mods/sea" "$TS/Mods/cylinderworld"
add "$SEA_ZIP" sea
[ "$WITH_CWORLD" = 1 ] && add "$CW_ZIP" cylinderworld
rm -rf "$TS/Saves" "$TS/Logs"
[ -f "$TS/serverconfig.json" ] || cp "$TEMPLATE" "$TS/serverconfig.json"

python - "$TS" "${PORT:-}" <<'EOF'
import json, sys
ts, port = sys.argv[1], sys.argv[2]
p = ts + "/serverconfig.json"
d = json.load(open(p, encoding="utf-8-sig"))
d["MapSizeX"], d["MapSizeZ"], d["MapSizeY"] = 8192, 8192, 256
d["ModPaths"] = ["Mods", ts.replace("/", "\\") + "\\Mods"]
if port: d["Port"] = int(port)
wc = d["WorldConfig"]
wc["Seed"] = "4242"
wc["SaveFileLocation"] = ts.replace("/", "\\") + "\\Saves\\seatest.vcdbs"
wc["WorldName"] = "Sea test"
wc["MapSizeY"] = 256
wc["WorldConfiguration"] = {"worldWidth": "8192", "worldLength": "8192", "worldEdge": "blocked", "gameMode": "creative"}
json.dump(d, open(p, "w", encoding="utf-8"), indent=2)
json.dump({"Enabled": True, "Shutdown": True}, open(ts + "/ModConfig/sea-selftest.json", "w"))
EOF

cd "$VS"
status=0
./VintagestoryServer.exe --dataPath "$(cygpath -w "$TS")" > "$TS/server-console.log" 2>&1 || status=$?
echo "server exited ($status)"
LOG="$TS/Logs/server-main.log"
grep -h "SELFTEST\|\[sea\]\|sea.*rror\|Exception" "$LOG" 2>/dev/null | head -80 || true
# a gate: the run passes only if the self-test finished and every check held
if grep -q "SELFTEST ALL OK" "$LOG" 2>/dev/null; then exit 0; fi
echo "self-test did not pass (no 'SELFTEST ALL OK' in $LOG)" >&2
exit 1
