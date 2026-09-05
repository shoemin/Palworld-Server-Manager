"""Static #35 storyboards using accepted #34 primitives; no product execution."""
import argparse
import importlib.util
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("shell_foundation", ROOT.parent / "astra-34" / "generate.py")
foundation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(foundation)
B = foundation.Board


def rail_selection(b, selected="R1", state="Running"):
    for alias, y, default in [("L",188,"Stopped"),("R1",336,"Running")]:
        active = alias == selected
        b.rect(10,y-2,260,68,"canvas")
        b.rect(12,y,256,64,"selected" if active else "surface","accent" if active else None)
        if active: b.corners(12,y,256,64)
        b.text(24,y+25,"Main Server",14,"text",600)
        status=state if active else default
        b.text(24,y+48,status,12,"success" if status=="Running" else "muted")
    if selected=="All":
        b.rect(12,72,256,44,"selected","accent")
        b.text(24,100,"▦  ALL SERVERS",14,"accent",600)


def frame(title, tab="Overview", state="Running", location="Remote"):
    b = B(1600, 900, title)
    b.rect(0, 0, 1600, 56)
    b.text(24, 35, "PALWORLD / SERVER MANAGER", 16, "text", 600)
    b.text(1136, 35, "Activity", 14, "accent")
    b.text(1240, 35, "Alerts", 14)
    b.text(1330, 35, "Manager Settings", 14)
    b.text(1510, 35, "—  □  ×", 14)
    foundation.rail(b, False)
    rail_selection(b,"R1" if location=="Remote" else "L",state)
    b.text(304, 98, "SERVER WORKSPACE / " + location.upper(), 12, "accent", 600)
    b.text(304, 142, "Main Server", 28, "text", 600)
    host = "Family PC · R1 · Host c58a1000…a001" if location == "Remote" else "This PC · L · Host 7d3a9100…0001"
    b.text(304, 172, host + " · Identity details", 14, "accent")
    b.text(304, 202, state + " · synthetic snapshot", 14, "warning" if state != "Running" else "success")
    b.button(1180, 126, 142, "Safe stop" if state == "Running" else "Start", True)
    b.button(1336, 126, 122, "Restart" if state=="Running" else "Restart —")
    if state!="Running": b.text(1336,190,"Not running",12,"muted")
    b.button(1470, 126, 92, "More")
    for i, name in enumerate(["Overview", "Players", "Metrics", "Settings", "Backups"]):
        x = 304+i*220
        b.text(x, 256, name, 14, "accent" if name == tab else "muted", 600)
        if name == tab: b.line(x, 272, x+160, 272, "accent", 2)
    b.line(280, 856, 1600, 856)
    b.text(304, 882, "Static design · Host owns all data and operations · individual boards are separate snapshots", 12, "muted")
    return b


def table(b, headers, rows, x=328, y=380, widths=None, rowheight=48):
    widths = widths or [220]*len(headers)
    for rownum, row in enumerate([headers]+rows):
        yy = y+rownum*rowheight
        offset = x
        for i, value in enumerate(row):
            b.text(offset, yy, str(value), 12 if rownum == 0 else 14, "muted" if rownum == 0 else "text", 600 if rownum == 0 else 400)
            offset += widths[i]
        b.line(x, yy+16, x+sum(widths)-24, yy+16)


def fleet():
    b = frame("All Servers / local and remote authorized inventory")
    rail_selection(b,"All")
    b.rect(292, 72, 1296, 768, "canvas")
    b.text(304, 116, "All Servers", 28, "text", 600)
    b.text(304, 148, "3 known accessible servers · 1 running observation · 1 Host offline", 14, "muted")
    b.button(1320, 90, 240, "+  Add / Import", True)
    b.rect(304, 180, 1260, 84)
    b.text(328, 211, "Offline is unknown, not stopped", 18, "warning", 600)
    b.text(328, 239, "R2 data was last observed at 14:30. Counts below include only the inventory you can access.", 14, "muted")
    table(b, ["SERVER", "AUTHORITATIVE HOST", "STATE", "PLAYERS", "UPDATED"], [
        ["Main Server", "This PC · L · …0001", "Stopped", "Not running", "Now"],
        ["Main Server", "Family PC · R1 · …a001", "Running", "5 / 32", "2 seconds ago"],
        ["Main Server", "Family PC · R2 · …a002", "Host offline", "Last known: 3 / 32", "14:30 · stale"],
    ], y=314, widths=[230,340,200,220,190], rowheight=64)
    b.text(328, 596, "Select a row to open its workspace. All actions retain that row's full Host-qualified identity.", 14, "muted")
    b.text(328, 628, "No unshared server rows, hidden-server counts or paired-but-unauthorized server placeholders.", 14, "muted")
    return b


def overview(location="Remote"):
    b=frame("Selected " + location + " server overview",location=location)
    for i,(label,value) in enumerate([("PLAYERS","5 / 32"),("SERVER FPS","59.8"),("FRAME TIME","16.7 ms"),("UPTIME","6h 42m")]):
        x=304+i*318
        b.rect(x,300,300,122)
        b.text(x+16,330,label,12,"muted",600)
        b.text(x+16,382,value,32,"text",600)
    b.rect(304,444,612,348)
    b.text(328,480,"SERVER INFORMATION",18,"text",600)
    table(b,["PROPERTY","HOST-REPORTED VALUE"],[["World","MainWorld"],["Game version","Reported by Host"],["Game / REST port","8211 / 8212"],["Last Manager backup","Yesterday · 18:42"]],y=522,widths=[260,300])
    b.rect(932,444,636,348)
    b.text(956,480,"READ AVAILABILITY",18,"text",600)
    b.text(956,524,"Live · sampled 2 seconds ago",18,"success")
    b.text(956,566,"Missing observations display “Unavailable”, never zero.",14,"muted")
    b.text(956,602,"Remote data arrives through your local Host." if location=="Remote" else "Data comes from the authoritative local Host.",14,"muted")
    b.text(956,638,"Full identity stays in the header and every confirmation.",14,"muted")
    b.button(956,700,300,"Open folder · Remote unavailable" if location=="Remote" else "Open local folder")
    b.button(956,748,300,"Create backup · Not authorized")
    return b


def players():
    b=frame("Players roster / semantic read-only data", "Players")
    b.text(304,320,"Players · 5 connected",18,"text",600)
    b.text(304,350,"Live · sampled 2 seconds ago",14,"success")
    table(b,["PLAYER","LEVEL","PING","BUILDINGS","LOCATION"],[["Aster",42,"34 ms",84,"120, −84"],["Juniper",38,"28 ms",32,"118, −90"],["Mica",40,"41 ms",61,"Unavailable"],["Rowan",35,"32 ms",45,"126, −76"],["Wren",37,"38 ms",28,"122, −81"]], y=406,widths=[360,180,180,200,260])
    b.button(304,732,232,"Advanced details · off")
    b.text(560,758,"Account/user/network identifiers are not shown by default.",14,"muted")
    b.text(304,814,"Roster only: no unapproved kick, ban, chat, teleport or arbitrary REST action.",14,"muted")
    return b


def metrics():
    b=frame("Metrics with explicit missing-data gap", "Metrics")
    rail_selection(b,"R1","Running · REST data stale")
    b.text(304,320,"Metrics · recent observed history",18,"text",600)
    b.text(304,350,"Degraded · last sample 14:30 · REST data unavailable",14,"warning")
    for i,(label,unit) in enumerate([("Server FPS","FPS"),("Frame time","ms"),("Player count","players")]):
        x=304+i*424
        b.rect(x,384,408,302)
        b.text(x+20,418,label,18,"text",600)
        b.text(x+20,450,"Last known · " + ["59.8 FPS","16.7 ms","5 players"][i],14,"muted")
        b.line(x+48,616,x+382,616)
        b.line(x+48,490,x+48,616)
        b.text(x+20,482,unit,12,"muted")
        b.text(x+48,644,"14:00",12,"muted")
        b.text(x+218,644,"14:30",12,"muted")
        b.text(x+318,644,"14:40",12,"muted")
        low,high,values=[(50,60,[58,59,58.5,60,59.5,59.8]),(0,20,[16,17,16.5,16,17,16.7]),(0,8,[3,3,4,4,5,5])][i]
        b.text(x+20,516,str(high),12,"muted")
        b.text(x+20,616,str(low),12,"muted")
        points=[(x+50+j*38,616-(value-low)/(high-low)*106) for j,value in enumerate(values)]
        path=f'M{points[0][0]} {points[0][1]}'
        for px,py in points[1:]: path+=f'H{px}V{py}' if i==2 else f'L{px} {py}'
        b.parts.append(f'<path d="{path}" fill="none" stroke="{b.t["accent"]}" stroke-width="2"/>')
        b.text(x+264,562,"No data",14,"warning")
    b.text(304,730,"The trace stops at the last observation. Gaps are not interpolated into apparently live values.",14,"muted")
    b.text(304,766,"Unavailable stream: display the Host's capability/version reason. No local REST fallback.",14,"muted")
    return b


def backups():
    b=frame("Backup restore-point history and scoped review", "Backups", "Stopped")
    b.text(304,320,"Restore points",18,"text",600)
    b.button(1304,294,256,"Create backup",True)
    table(b,["CREATED / REFERENCE","REASON","SIZE","CONTENTS"],[["Today · 14:15 · b071","Manual","244 MB","Save, configuration, mods"],["Yesterday · 18:42 · b070","Pre-restore","240 MB","Save, configuration, mods"],["Yesterday · 12:10 · b06f","Manual","238 MB","Save, configuration, mods"]],y=384,widths=[300,260,180,490],rowheight=52)
    b.rect(304,580,1260,224,"surface","accent")
    b.text(328,616,"Review restore · Today 14:15 · b071",18,"text",600)
    b.text(328,648,"Destination: Main Server · Family PC · R1 · Host c58a1000…a001",14,"accent")
    b.text(328,678,"Replaces this server's save/configuration/mods. Host first creates a safety restore point.",14)
    b.text(328,706,"If that safety backup fails, restore does not proceed. Runtime binaries are not restored.",14,"muted")
    b.button(328,740,200,"Back to history")
    b.button(544,740,230,"Confirm restore",True)
    return b


def guided(importing):
    title="Import a .palserver package" if importing else "Create a server"
    b=B(1600,900,title+" / sequential guided panels")
    b.text(40,58,title,28,"text",600)
    b.text(40,94,"Storyboard: these are separate steps, shown side by side for review. Product shows one panel at a time.",14,"muted")
    steps = ([
        ("1  Choose package",["Select a .palserver package", "Host validates manifest and hashes", "Corrupt package → explain rejection", "Source package is preserved"],"Choose package"),
        ("2  Choose destination",["This PC · L · …0001", "Family PC · R1 · …a001  [selected]", "Family PC · R2 · …a002 · offline", "Remote route goes through local Host"],"Continue"),
        ("3  Review import",["Family PC · R1 · …a001", "Save/configuration/mods: verified", "Fresh runtime installed by SteamCMD", "Creates a new managed profile"],"Import on Family PC"),
    ] if importing else [
        ("1  Choose destination",["This PC · L · …0001", "Family PC · R1 · …a001  [selected]", "Family PC · R2 · …a002 · offline", "Available to create on selected Host"],"Continue"),
        ("2  Name server",["Name: Weekend World", "Destination: Family PC · R1 · …a001", "Name accepted", "You can edit settings after creation"],"Review"),
        ("3  Review creation",["Weekend World", "Destination: Family PC · R1 · …a001", "Fresh runtime installed by SteamCMD", "Creates a new managed profile"],"Create on Family PC"),
    ])
    for i,(heading,lines,action) in enumerate(steps):
        x=40+i*520
        b.rect(x,136,480,596,"surface","border")
        b.text(x+24,178,heading,24,"text",600)
        for j,line in enumerate(lines): b.text(x+24,236+j*52,line,14,"accent" if "selected" in line else "text")
        b.text(x+24,494,"Review full Host identity before confirming.",14,"muted")
        b.text(x+24,532,"Unavailable destinations cannot be submitted.",14,"warning")
        b.button(x+24,646,108,"Back")
        b.button(x+144,646,312,action,True)
    b.text(40,782,"Host revalidates authority, capability and target at submission. If any change, stay on review and explain.",14,"muted")
    b.text(40,816,"Accepted work opens its Host-owned Activity record. Closing the wizard does not cancel it or start another copy.",14,"muted")
    b.text(40,852,"No offline queue, arbitrary remote path browser, resumable transfer, or silent overwrite of an existing profile.",14,"muted")
    return b


if __name__ == "__main__":
    parser=argparse.ArgumentParser(); parser.add_argument("--check",action="store_true"); args=parser.parse_args()
    foundation.validate_tokens()
    boards={"fleet.svg":fleet(),"overview.svg":overview(),"overview-local.svg":overview("This PC"),"players.svg":players(),"metrics.svg":metrics(),"backups.svg":backups(),"create.svg":guided(False),"import.svg":guided(True)}
    for name,b in boards.items():
        data="\n".join(b.parts+["</svg>"])+"\n"; ET.fromstring(data)
        path=ROOT/name
        if args.check: assert path.read_text(encoding="utf-8")==data, "Stale board: "+name
        else: path.write_text(data,encoding="utf-8",newline="\n")
    print("PASS: eight reproducible workspace/flow SVGs; shared palette checks")
