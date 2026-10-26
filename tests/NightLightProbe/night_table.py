# Rendered night brightness before and after Sea 0.4.7-night (issue #7), from a probe sweep's measured sun and moon:
#   night_table.py <sweep.csv> [after sweep.csv]
# The client's numbers are the game's formulas (ClientGameCalendar.Update, AmbientManager scene brightness, the
# sunglow ambient = reflect colour + (daylight - its mean)) with Sea's floor on top, as the mean of rgb under the open
# sky. Server columns are measured. A model, not a screenshot: SunsetMod 0, weather by its ambient values.
import csv, math, os, sys
from PIL import Image

png = Image.open(os.path.join(os.environ["APPDATA"], "Vintagestory", "assets", "game", "textures", "environment", "sunlight.png")).convert("RGB")
W, H = png.size
ROW = int(0.01 * H)


def tex(x):
    x = min(W - 1, max(0.0, x) * W); i = int(x); f = x - i
    a, b = png.getpixel((i, ROW)), png.getpixel((min(W - 1, i + 1), ROW))
    return [(a[k] * (1 - f) + b[k] * f) / 255 for k in range(3)]


def mean(c): return sum(c) / 3
def clamp(v, a, b): return max(a, min(b, v))
def smooth(a, b, x):
    t = clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t)
def tex_x(y): return (clamp(y * 1.4 + 0.2, -1, 1) + 1) / 2
def twilight_y(y):
    if y >= 0: return y
    return math.sin(max(math.asin(clamp(y, -1, 1)) * 2.5, -math.pi / 2))
TABLE = [-0.1, 0.1, 0.2, 0.26, 0.33, 0.26, 0.2, 0.1]
def brightness(phase):
    p = phase % 8; i = int(p); f = p - i
    return TABLE[i] * (1 - f) + TABLE[(i + 1) % 8] * f
def illum(phase): return 0.5 - 0.5 * math.cos(phase * math.pi / 4)
def client_moon(y, b): return clamp(b * (0.66 + 0.66 * y), -0.2, b) * clamp(y * 20, 0, 1)
def scene(dl, weather): return weather * (1 - clamp(1 - clamp(1.5 * dl - 0.2, 0.1, 1), 0, 0.65))
TINT = (0.78 + 0.88 + 1) / 3

WEATHER = {  # cloud thickness, rain, fog density, the weather's scene brightness
    "clear": (0, 0, 0.001, 1.0), "overcast": (0.8, 0, 0.003, 0.9), "rain": (1, 0.6, 0.003, 0.9), "fog": (0.3, 0, 0.03, 0.95)}


def before(r, w):
    cloud, rain, fog, sw = WEATHER[w]
    sy, yl, il = r["sun_y"], r["local_moon_y"], illum(r["local_phase"])
    sun = mean(tex(tex_x(sy)))
    moon = client_moon(r["vanilla_moon_y"], r["phase_brightness"])
    dl = max(sun, moon)
    local = 0.33 * il * clamp(0.66 + 0.66 * yl, 0, 1) * clamp(yl * 20, 0, 1)
    scale = clamp(max(sun, local) / dl, 0, 1) if dl > 0.001 else 1  # Sea's MoonAmbientScale (AstraTerra draws the moon)
    amb = dl * scene(dl, sw) * scale
    clear = clamp(1 - 0.92 * cloud, 0, 1) * (1 - 0.9 * rain)
    night = 1 - smooth(-0.18, 0.02, sy)
    floor = TINT * night * clear * (0.15 + 0.30 * il * smooth(0, 0.12, yl))
    return max(amb, floor), dl


def after(r, w):
    cloud, rain, fog, sw = WEATHER[w]
    sy, yl, il = r["sun_y"], r["local_moon_y"], illum(r["local_phase"])
    clear = clamp(1 - 0.95 * clamp(cloud, 0, 1) ** 0.75, 0, 1) * (1 - 0.9 * rain) * (1 - 0.5 * smooth(0.004, 0.03, fog))
    sun = mean(tex(tex_x(sy)))
    if sy < 0: sun = min(sun, mean(tex(tex_x(twilight_y(sy)))))
    moon = client_moon(yl, brightness(r["local_phase"])) * clear  # AstraTerra's local moon, cut by weather (Sea)
    dl = max(sun, moon)
    local = 0.33 * il * clamp(0.66 + 0.66 * yl, 0, 1) * clamp(yl * 20, 0, 1) * clear
    scale = clamp(max(sun, local) / dl, 0, 1) if dl > 0.001 else 1
    amb = dl * scene(dl, sw) * scale
    night = 1 - smooth(-0.1045, 0, sy)
    height = smooth(-0.01, 0.06, yl) * clamp(0.35 + 0.65 * yl / 0.574, 0, 1)
    floor = TINT * night * clear * (0.10 + 0.30 * il ** 1.5 * height)
    return max(amb, floor), dl


def load(path):
    rows = list(csv.DictReader(open(path)))
    for r in rows:
        for k in r:
            if k != "season" and k != "moon": r[k] = float(r[k])
    return rows


rows = load(sys.argv[1])
after_rows = load(sys.argv[2]) if len(sys.argv) > 2 else None
key = lambda r: (r["season"], r["moon"], round(r["lat"], 1), round(r["lon"]) if r["lon"] == r["lon"] else None, r["hours_from_midnight"])
server_after = {key(r): r for r in after_rows} if after_rows else {}

print("## Midnight, longitude 0 (rendered ambient before -> after; server time-of-day light before -> after)\n")
print("| season | moon | lat | sun alt | clear | overcast | rain | thick fog | server light |")
print("|---|---|---|---|---|---|---|---|---|")
for r in rows:
    if r["hours_from_midnight"] != 0 or abs(r["lon"]) > 1: continue
    cells = [f"{before(r, w)[0]:.3f} → {after(r, w)[0]:.3f}" for w in ("clear", "overcast", "rain", "fog")]
    a = server_after.get(key(r))
    srv = f"{int(r['tod_sunlight'])} → {int(a['tod_sunlight'])}" if a else f"{int(r['tod_sunlight'])}"
    print(f"| {r['season']} | {r['moon']} | {r['lat']:.1f} | {r['sun_alt']:.1f}° | " + " | ".join(cells) + f" | {srv} |")

print("\n## Far longitude 169°W, full moon, midnight (server light from the moon players see)\n")
print("| season | lat | drawn moon y | game's moon y | server light before -> after | rendered clear before -> after |")
print("|---|---|---|---|---|---|")
for r in rows:
    if r["hours_from_midnight"] != 0 or r["moon"] != "full" or not (r["lon"] < -100): continue
    a = server_after.get(key(r))
    print(f"| {r['season']} | {r['lat']:.1f} | {r['local_moon_y']:.2f} | {r['vanilla_moon_y']:.2f} | {int(r['tod_sunlight'])} → {int(a['tod_sunlight']) if a else '?'} | {before(r, 'clear')[0]:.3f} → {after(r, 'clear')[0]:.3f} |")

print("\n## Through one night: 45°N, March, new moon, longitude 0 (clear)\n")
print("| hours from midnight | sun alt | rendered daylight before -> after | rendered ambient before -> after |")
print("|---|---|---|---|")
for r in rows:
    if r["season"] != "mar" or r["moon"] != "new" or round(r["lat"]) != 45 or abs(r["lon"]) > 1: continue
    h = r["hours_from_midnight"]
    if h % 1 or h > 0 or h < -7: continue
    b, bd = before(r, "clear"); a, ad = after(r, "clear")
    print(f"| {h:+.0f} | {r['sun_alt']:.1f}° | {bd:.3f} → {ad:.3f} | {b:.3f} → {a:.3f} |")

print("\n## Darkest hour and full-dark time by latitude (longitude 0, new moon, clear, after)\n")
print("| season | lat | lowest sun | darkest rendered before -> after | hours with sun below -18° |")
print("|---|---|---|---|---|")
groups = {}
for r in rows:
    if r["moon"] != "new" or abs(r["lon"]) > 1: continue
    groups.setdefault((r["season"], round(r["lat"], 1)), []).append(r)
for (season, lat), rs in groups.items():
    lowest = min(rs, key=lambda r: r["sun_alt"])
    dark = sum(0.25 for r in rs if r["sun_alt"] < -18)
    print(f"| {season} | {lat:.1f} | {lowest['sun_alt']:.1f}° | {min(before(r, 'clear')[0] for r in rs):.3f} → {min(after(r, 'clear')[0] for r in rs):.3f} | {dark:.1f} |")
