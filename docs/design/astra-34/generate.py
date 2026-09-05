"""Deterministic, project-owned SVG design boards. Python standard library only.

Run: python docs/design/astra-34/generate.py [--check]
No production behavior, network access, or product state is implemented here.
"""
import argparse
import html
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent
TOKENS = json.loads((ROOT / "tokens.json").read_text(encoding="utf-8"))
D = TOKENS["dimensions"]
HOSTS = [
    ("This PC", "L", "7d3a9100-1000-4000-8000-000000000001"),
    ("Family PC", "R1", "c58a1000-1000-4000-8000-00000000a001"),
    ("Family PC", "R2", "c58a1000-1000-4000-8000-00000000a002"),
]
PROFILE = "680a9100-1000-4000-8000-000000000007"


class Board:
    def __init__(self, width, height, title, theme="refined"):
        self.width, self.height = width, height
        self.t = TOKENS["themes"][theme]
        self.parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img" aria-labelledby="title desc">',
                      f'<title id="title">{html.escape(title)}</title>',
                      '<desc id="desc">Static Astra design prototype. All names, identifiers and status values are synthetic. Read the accompanying specification for keyboard and responsive behavior.</desc>']
        self.rect(0, 0, width, height, "canvas")

    def rect(self, x, y, w, h, fill="surface", stroke=None, sw=1):
        assert x >= 0 and y >= 0 and x+w <= self.width and y+h <= self.height
        self.parts.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{D["radius"]}" fill="{self.t.get(fill, fill)}" stroke="{self.t.get(stroke, stroke) if stroke else "none"}" stroke-width="{sw}"/>')

    def text(self, x, y, value, size=14, color="text", weight=400):
        self.parts.append(f'<text x="{x}" y="{y}" font-family="Segoe UI, Arial, sans-serif" font-size="{size}" font-weight="{weight}" fill="{self.t[color]}">{html.escape(value)}</text>')

    def line(self, x1, y1, x2, y2, color="border", width=1):
        self.parts.append(f'<path d="M{x1} {y1}L{x2} {y2}" fill="none" stroke="{self.t[color]}" stroke-width="{width}"/>')

    def button(self, x, y, w, label, accent=False):
        self.rect(x, y, w, D["control"], "selected" if accent else "surface", "accent" if accent else "border")
        self.text(x+12, y+25, label, 14, "accent" if accent else "text", 600)

    def corners(self, x, y, w, h):
        for sx, sy, dx, dy in [(x,y,1,1),(x+w,y,-1,1),(x,y+h,1,-1),(x+w,y+h,-1,-1)]:
            self.parts.append(f'<path d="M{sx+dx*12} {sy}H{sx}V{sy+dy*12}" fill="none" stroke="{self.t["accent"]}" stroke-width="2"/>')

    def save(self, name, check):
        data = "\n".join(self.parts + ["</svg>"]) + "\n"
        ET.fromstring(data)
        target = ROOT / name
        if check:
            assert target.read_text(encoding="utf-8") == data, f"Stale generated board: {name}"
        else:
            target.write_text(data, encoding="utf-8", newline="\n")


def rail(b, collapsed):
    rw = D["collapsedRail"] if collapsed else D["rail"]
    b.rect(12, 72, rw-24, 44, "surface", "border")
    b.text(24, 100, "ALL" if collapsed else "▦  ALL SERVERS", 14, "text", 600)
    y = 156
    for idx, (name, alias, host_id) in enumerate(HOSTS):
        b.text(20, y, alias if collapsed else f"{alias}  {name.upper()}", 12, "muted", 600)
        if not collapsed:
            # This deliberately duplicated prefix demonstrates why suffixes are needed.
            b.text(20, y+20, f"Host {host_id[:8]}…{host_id[-4:]}", 12, "muted")
        ry = y+32
        selected = idx == 1
        b.rect(12, ry, rw-24, 64, "selected" if selected else "surface", "accent" if selected else None)
        if selected:
            b.corners(12, ry, rw-24, 64)
        b.text(24, ry+25, "S1" if collapsed else "Main Server", 14, "text", 600)
        b.text(24, ry+48, ["○", "●", "!"][idx] if collapsed else ["○  Stopped", "●  Running · 5 / 32 players", "!  Host offline · last known"][idx], 12,
               ["muted", "success", "warning"][idx])
        y += 148
    if collapsed:
        b.button(12, 686, 64, "+ / ↓")
        b.button(12, 738, 64, "»")
    else:
        b.button(12, 686, 256, "+  ADD / IMPORT", True)
        b.button(12, 738, 256, "«  Collapse server rail")
    b.line(rw, 56, rw, 856)


def shell(width, name, collapsed=False, drawer=True, theme="refined"):
    b = Board(width, 900, name, theme)
    rw = D["collapsedRail"] if collapsed else D["rail"]
    dw = D["drawer"] if drawer else 0
    x, right = rw+24, width-dw-24
    cw = right-x
    b.rect(0, 0, width, D["chrome"], "surface")
    b.text(20, 35, "PALWORLD" if collapsed else "PALWORLD  /  SERVER MANAGER", 16, "text", 600)
    b.text(width-464, 35, "Activity  2", 14, "accent")
    b.text(width-344, 35, "Alerts  1", 14, "text")
    b.text(width-236, 35, "Settings", 14, "text")
    b.text(width-116, 35, "—   □   ×", 16, "muted")
    rail(b, collapsed)
    b.text(x, 100, "SERVER WORKSPACE  /  REMOTE", 12, "accent", 600)
    b.rect(x, 120, cw, 220, "surface")
    if theme == "refined":
        # Original angular horizon, no copied game artwork or logos.
        b.parts.append(f'<path d="M{right-260} 121L{right-150} 185L{right-70} 130L{right} 165V339H{right-310}L{right-230} 230Z" fill="{b.t["raised"]}"/>')
    b.corners(x, 120, cw, 220)
    b.text(x+20, 166, "Main Server", TOKENS["type"]["title"], "text", 600)
    b.text(x+20, 194, "Family PC · R1 · Remote", 14, "accent", 600)
    b.text(x+20, 216, "Host c58a1000…a001   ·   Identity details", 12, "muted")
    b.text(x+20, 250, "●  Running     5 / 32 players     Uptime 6h 42m", 14, "success")
    b.button(x+20, 278, 126, "Safe stop", True)
    b.button(x+158, 278, 112, "Restart")
    b.button(x+282, 278, 62, "···")
    tabs = ["Overview", "Players", "Metrics", "Settings", "Backups"]
    for i, label in enumerate(tabs):
        tx = x+i*(cw/5)
        b.text(tx+8, 380, label, 14, "accent" if i == 0 else "muted", 600)
    b.line(x, 396, right, 396)
    b.line(x, 396, x+cw/5, 396, "accent", 2)
    metricw = (cw-24)/3
    for i, (label, value, detail) in enumerate([("PLAYERS", "5 / 32", "Current occupancy"), ("PERFORMANCE", "59.8 FPS", "Host-reported"), ("UPTIME", "6h 42m", "Since latest start")]):
        mx = x+i*(metricw+12)
        b.rect(mx, 420, metricw, 132, "surface")
        b.text(mx+16, 448, label, 12, "muted", 600)
        b.text(mx+16, 488, value, 24 if collapsed else 32, "text", 600)
        b.text(mx+16, 524, detail, 12, "muted")
    b.rect(x, 568, cw, 224, "surface")
    b.text(x+20, 600, "WORKSPACE CONTENT", 12, "muted", 600)
    b.text(x+20, 634, "Server information and recent activity", 18, "text", 600)
    b.text(x+20, 664, "Bounded workspace surfaces are detailed in #35.", 14, "muted")
    b.line(x+20, 688, right-20, 688)
    b.text(x+20, 720, "Authoritative Host", 14, "muted")
    b.text(x+20, 746, "Family PC · R1 · c58a1000…a001", 14, "text")
    b.text(x, 826, "Synthetic design data · controls show placement only", 12, "muted")
    if drawer:
        dx = width-dw
        b.rect(dx, 72, dw-12, 768, "surface")
        b.text(dx+20, 108, "ACTIVITY  2", 14, "text", 600)
        b.text(width-44, 108, "×", 18, "muted")
        for i, (title, target, status) in enumerate([("Backup", "R1 · Main Server", "In progress · 42%"), ("Update", "L · Main Server", "Waiting for operation lock")]):
            ay=136+i*156
            b.rect(dx+12, ay, dw-36, 136, "raised")
            b.text(dx+28, ay+30, title, 18, "text", 600)
            b.text(dx+28, ay+56, target, 14, "accent")
            b.text(dx+28, ay+88, status, 12, "muted")
            if i == 0:
                b.rect(dx+28, ay+106, dw-68, 4, "border")
                b.rect(dx+28, ay+106, (dw-68)*.42, 4, "accent")
        b.text(dx+20, 500, "Host-owned work continues", 14, "muted")
        b.text(dx+20, 524, "when this drawer closes.", 14, "muted")
        b.text(dx+20, 800, "View activity", 14, "accent")
    b.line(0, 856, width, 856)
    b.text(20, 882, "Local Host · connected" + ("" if collapsed else "     /     Only authorized servers shown"), 12, "muted")
    b.text(width-208, 882, "ASTRA · DESIGN PROTOTYPE", 12, "muted")
    return b


def narrow_identity():
    b = shell(800, "Collapsed rail identity popover reached by keyboard", True, False)
    b.rect(16, 340, 56, 56, "selected", "accent", 2)
    b.text(24, 364, "S1", 14, "text", 600)
    b.text(24, 389, "●", 12, "success")
    b.rect(100, 344, 676, 160, "raised", "accent", 2)
    b.text(120, 374, "Main Server · Family PC · R1 · Remote", 18, "text", 600)
    b.text(120, 404, "HostId  " + HOSTS[1][2], 14, "accent")
    b.text(120, 430, "ServerProfileId  " + PROFILE, 14, "text")
    b.text(120, 468, "Focus reveals identity · Enter selects · Escape dismisses", 14, "muted")
    return b


def components():
    b=Board(1600, 900, "Astra shell component and interaction states")
    b.text(40, 54, "PALWORLD REFINED DESKTOP / COMPONENT FOUNDATION", 24, "text", 600)
    b.text(40, 86, "State is always conveyed by a label or shape as well as color. Synthetic examples; no authority is implied.", 14, "muted")
    states = [("Default", "surface", None, "○  Stopped"), ("Hover", "raised", None, "○  Stopped"), ("Selected", "selected", "accent", "●  Running"), ("Keyboard focus", "surface", "accent", "○  Stopped"), ("New", "surface", None, "+  New server"), ("Offline", "surface", None, "!  Host offline · stale"), ("Degraded", "surface", None, "!  Metrics unavailable"), ("Unavailable action", "surface", None, "Restart · Host offline")]
    for i,(label,fill,stroke,status) in enumerate(states):
        x=40+(i%4)*380; y=126+(i//4)*146
        b.text(x,y,label,14,"muted",600)
        b.rect(x,y+16,340,88,fill,stroke,2)
        if label=="Selected": b.corners(x,y+16,340,88)
        if label=="Keyboard focus": b.rect(x+4,y+20,332,80,fill,"accent",2)
        b.text(x+16,y+46,"Main Server · R1",16,"text",600)
        b.text(x+16,y+76,status,14,"warning" if i>=5 else "muted")
    b.text(40,454,"IDENTITY EXPANSION / SAME HOST NAME + SAME SERVER NAME + SAME ID PREFIX",18,"text",600)
    for i,(name,alias,host_id) in enumerate(HOSTS[1:]):
        x=40+i*760
        b.rect(x,474,720,112,"surface","border")
        b.text(x+16,502,f"Main Server · {name} · {alias} · Remote",16,"text",600)
        b.text(x+16,530,f"HostId  {host_id}",14,"accent")
        b.text(x+16,556,f"ServerProfileId  {PROFILE}",14,"muted")
    b.text(40,636,"FOUNDATION TOKENS",18,"text",600)
    for i,key in enumerate(["canvas","surface","raised","accent","success","warning","danger"]):
        x=40+i*216
        b.rect(x,656,184,40,key,"border")
        b.text(x,722,key,14,"muted")
        b.text(x,746,b.t[key],14,"text")
    b.text(40,806,"Tab: chrome → rail → workspace → open drawer. Escape closes transient UI and restores focus.",14,"text")
    b.text(40,838,"Reduced motion: no slide, pulse or animated geometry. Focus ring persists independently of selection.",14,"muted")
    return b


def validate_tokens():
    def lum(color):
        vals=[int(color[i:i+2],16)/255 for i in (1,3,5)]
        vals=[v/12.92 if v<=.04045 else ((v+.055)/1.055)**2.4 for v in vals]
        return sum(v*w for v,w in zip(vals,[.2126,.7152,.0722]))
    def contrast(a,b):
        x,y=sorted((lum(a),lum(b)))
        return (y+.05)/(x+.05)
    for name,t in TOKENS["themes"].items():
        for foreground in ("text","muted","accent","success","warning","danger"):
            for background in ("canvas","surface","raised","selected"):
                assert contrast(t[foreground],t[background]) >= 4.5, (name,foreground,background)
        assert contrast(t["onAccent"],t["accent"]) >= 4.5
        for background in ("canvas", "surface", "raised", "selected"):
            assert contrast(t["accent"],t[background]) >= 3
            assert contrast(t["border"],t[background]) >= 3, (name,"boundary",background)
    assert len({h[2] for h in HOSTS}) == len(HOSTS)
    assert len({h[2][:8]+h[2][-4:] for h in HOSTS}) == len(HOSTS)
    assert len({(h[2], PROFILE) for h in HOSTS}) == len(HOSTS)


if __name__ == "__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("--check",action="store_true")
    args=parser.parse_args()
    validate_tokens()
    for filename,board in [
        ("shell-16x9.svg",shell(1600,"16:9 shell with nonblocking Activity drawer")),
        ("shell-21x9.svg",shell(2100,"21:9 shell with nonblocking Activity drawer")),
        ("shell-narrow.svg",shell(800,"Narrow shell with collapsed server rail",True,False)),
        ("shell-narrow-identity.svg",narrow_identity()),
        ("shell-dark.svg",shell(1600,"Dark Minimal token feasibility",theme="dark")),
        ("shell-light.svg",shell(1600,"Light Minimal token feasibility",theme="light")),
        ("components.svg",components()),
    ]:
        board.save(filename,args.check)
    print("PASS: seven deterministic SVG boards; semantic text and boundary contrast; collision fixtures")
