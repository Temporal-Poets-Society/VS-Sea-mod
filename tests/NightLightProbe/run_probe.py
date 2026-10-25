# Runs an isolated dedicated server with the night light probe and the given mod zips, sweeps night light and stops:
#   run_probe.py <absolute new run dir> <earth|vanilla> [mod zip or folder ...]
# Writes <run>/sweep.csv and the probe's block readings in <run>/stdout.log. Needs the probe built (dotnet build). Earth
# worlds need Earth's and Cylinder World's zips among the mods. Never touches the player's data folder.
import json, sys, os, shutil, subprocess, threading, time, queue, socket
run, world, mods = sys.argv[1], sys.argv[2], sys.argv[3:]
here = os.path.dirname(os.path.abspath(__file__))
game = os.path.join(os.environ["APPDATA"], "Vintagestory")
assert os.path.isabs(run) and not os.path.exists(run), run
for d in ("Mods", "ModConfig", "Saves"): os.makedirs(os.path.join(run, d))
shutil.copytree(os.path.join(here, "bin", "mod"), os.path.join(run, "Mods", "nightlightprobe"))
for m in mods:
    if os.path.isdir(m): shutil.copytree(m, os.path.join(run, "Mods", os.path.basename(os.path.normpath(m))))
    else: shutil.copy(m, os.path.join(run, "Mods"))
if world == "earth":
    json.dump({"MoveStoryLocations": False, "SelfTest": {"Enabled": False}}, open(os.path.join(run, "ModConfig", "earthmap.json"), "w"), indent=2)
template = os.environ.get("SERVERCONFIG_TEMPLATE", os.path.join(here, "..", "..", "..", "..", "..", "..", "Devkit", "tests", "server", "serverconfig-template.json"))
d = json.load(open(template, encoding="utf-8-sig"))
with socket.socket() as probe:
    probe.bind(("127.0.0.1", 0)); port = probe.getsockname()[1]
d["Ip"] = "127.0.0.1"; d["Port"] = port; d["AdvertiseServer"] = False; d["Upnp"] = False; d["LastLaunchMods"] = None
d["ModPaths"] = ["Mods", os.path.join(run, "Mods")]
wc = d["WorldConfig"]
wc["Seed"] = "386217"; wc["SaveFileLocation"] = os.path.join(run, "Saves", "probe.vcdbs"); wc["WorldName"] = f"Night probe {world}"
wc["AllowCreativeMode"] = True
if world == "earth":
    wc["MapSizeY"] = 384
    wc["WorldConfiguration"] = {"worldWidth": "256000", "worldLength": "102400", "worldEdge": "blocked", "startingClimate": "temperate", "gameMode": "survival"}
else:
    wc["MapSizeY"] = 256; d["MapSizeX"] = d["MapSizeZ"] = 1024000
    wc["WorldType"] = "standard"; wc["PlayStyle"] = "surviveandbuild"
    wc["WorldConfiguration"] = {"worldWidth": "1024000", "worldLength": "1024000", "gameMode": "survival"}
json.dump(d, open(os.path.join(run, "serverconfig.json"), "w", encoding="utf-8"), indent=2)

out = open(os.path.join(run, "stdout.log"), "w", encoding="utf-8")
p = subprocess.Popen([os.path.join(game, "VintagestoryServer.exe"), "--dataPath", run], cwd=game,
                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
lines = queue.Queue()
def pump():
    for l in p.stdout:
        out.write(l); out.flush(); lines.put(l)
threading.Thread(target=pump, daemon=True).start()
def send(c):
    print(">", c, flush=True); p.stdin.write(c + "\n"); p.stdin.flush()
def wait(text, timeout):
    end = time.time() + timeout
    while time.time() < end:
        try: l = lines.get(timeout=1)
        except queue.Empty:
            if p.poll() is not None: return None
            continue
        if text in l: return l
    return None
print("running:", bool(wait("Dedicated Server now running", 900)), flush=True)
time.sleep(5)
send("/nightprobe sweep " + os.path.join(run, "sweep.csv"))
print("sweep:", (wait("[nightprobe] sweep", 600) or "timeout").strip(), flush=True)
send("/nightprobe block")
for name in ("now", "newmoon", "fullmoon"): print((wait("[nightprobe] block " + name, 300) or "timeout").strip(), flush=True)
send("/stop")
try: rc = p.wait(timeout=300)
except subprocess.TimeoutExpired: p.kill(); rc = "killed"
print("exit", rc)
