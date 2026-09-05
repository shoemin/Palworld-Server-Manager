"""Static semantic settings patterns; no catalog or settings behavior implemented."""
import argparse
import importlib.util
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location("workspace_design",ROOT.parent/"astra-35"/"generate.py")
workspace=importlib.util.module_from_spec(spec); spec.loader.exec_module(workspace)
B=workspace.B


def settings(width):
    narrow=width<1200; rw=88 if narrow else 280
    b=B(width,900,"Semantic settings / "+str(width)+" wide")
    b.rect(0,0,width,56)
    b.text(20,35,"PALWORLD" if narrow else "PALWORLD / SERVER MANAGER",16,"text",600)
    b.text(width-440,35,"Activity",14,"accent")
    b.text(width-348,35,"Alerts",14)
    b.text(width-264,35,"Manager Settings",14)
    b.text(width-96,35,"—  □  ×",14)
    workspace.foundation.rail(b,narrow)
    if narrow:
        b.rect(16,370,56,26,"selected"); b.text(24,389,"○",12,"muted")
    else: workspace.rail_selection(b,"R1","Stopped")
    x=rw+24; right=width-24
    b.text(x,100,"SERVER SETTINGS / REMOTE",12,"accent",600)
    b.text(x,140,"Main Server",28,"text",600)
    b.text(x,169,"Family PC · R1 · c58a1000…a001 · Identity details",14,"accent")
    b.text(x,194,"Stopped · configured values · synthetic snapshot",14,"muted")
    b.rect(x,214,right-x,40,"surface","border")
    b.text(x+16,240,"Search settings by name or description…",14,"muted")
    if narrow:
        b.text(x,280,"All settings  ▾",14,"accent",600); fx=x
    else:
        b.rect(x,274,180,478)
        for i,name in enumerate(["All settings","Server management","Performance","Features","Game balance","Advanced / unknown"]):
            b.text(x+12,310+i*48,name,14,"accent" if i==0 else "muted",600 if i==0 else 400)
        fx=x+204
    fw=right-fx
    rows=[("Fast travel","Allows fast travel.","Off → On","toggle",True),
          ("Experience gain","Multiplier · saved 1.0×; draft 2.0×.","2.0 ×","number",True),
          ("Death penalty","Known choices: None, Item, ItemAndEquipment, All.","All  ▾","enum",False),
          ("Administrator password","Stored value is not returned to this view.","Stored · unchanged","secret",False)]
    for i,(label,desc,value,kind,changed) in enumerate(rows):
        y=300+i*114
        b.rect(fx,y,fw,102,"surface")
        b.text(fx+16,y+28,label+("  ·  Modified" if changed else ""),18,"text",600)
        b.text(fx+16,y+52,desc,12,"muted")
        if narrow: b.text(right-100,y+28,"Details ▾",14,"accent")
        cx=fx+16 if narrow else right-324; cy=y+60 if narrow else y+14
        if kind=="toggle":
            b.button(cx,cy,100,"Off"); b.button(cx+112,cy,100,"On",True)
        elif kind=="secret": b.button(cx,cy,280,"Replace password…")
        else: b.button(cx,cy,212,value)
        if not narrow:
            b.text(fx+16,y+84,"Default not supplied · reset unavailable",12,"muted")
            if changed: b.text(right-112,y+82,"Revert",14,"accent")
    b.text(fx,786,"2 unsaved changes · effective values / restart rules not reported",12,"warning")
    b.button(fx,804,140,"Discard")
    b.button(fx+156,804,168,"Save changes",True)
    if not narrow: b.text(fx+348,830,"Save writes configuration; it does not claim a live change.",12,"muted")
    b.line(0,856,width,856)
    b.text(20,884,"Static design · no setting default, limit or restart rule is invented",12,"muted")
    return b


def narrow_details():
    b=settings(800)
    b.rect(112,414,664,338,"canvas")
    b.rect(112,414,664,282,"surface","accent")
    b.text(128,442,"Experience gain · Modified",18,"text",600)
    b.text(128,468,"Multiplier · saved 1.0×; draft 2.0×.",12,"muted")
    b.button(128,486,212,"2.0 ×")
    b.text(660,442,"Details ▴",14,"accent")
    b.text(128,554,"Default: not provided · range: not provided",14,"muted")
    b.text(128,586,"Effective value / restart requirement: not reported",14,"muted")
    b.button(128,614,200,"Reset — no default")
    b.button(344,614,260,"Revert to saved 1.0×",True)
    b.text(128,734,"2 more settings below · scroll to continue",14,"muted")
    return b


def gallery():
    b=B(1600,1060,"Semantic editor family patterns / conditional schema bindings")
    b.text(40,54,"SEMANTIC CONTROL FAMILIES",28,"text",600)
    b.text(40,88,"A pattern does not add a setting. Bind only to a Host definition with an established domain meaning.",14,"muted")
    kinds=[
        ("Boolean","Fast travel","On / Off; not a True/False textbox.","On  |  Off"),
        ("Enumeration","Death penalty","Only documented choices; unknown value preserved.","All  ▾"),
        ("Bounded numeric","Conditional pattern","Slider only with authoritative min/max/step.","slider"),
        ("Integer","Player limit","Integer entry; no guessed maximum or default.","−    value    +"),
        ("Text","Server name","Single-line text; validation comes from Host.","Weekend World"),
        ("Multiline text","Server description","Soft wrapping; stored newlines only if Host schema allows.","Welcome to our world…"),
        ("Path","Conditional pattern","No baseline path key bound; no generic remote browser.","Host-defined setting path"),
        ("Password","Administrator password","Unchanged / Replace / explicit Clear; no stored-value echo.","Replace password…"),
        ("Duration","Huge Egg hatching time","Known hours unit; no arbitrary timer range.","value   hours"),
        ("Rate / multiplier","Experience gain","Numeric magnitude plus ×; bounds only when known.","value   ×"),
        ("Compound","Crossplay platforms","Host-defined known platform choices; preserve unknown tokens.","Steam  Xbox  PS5  Mac"),
        ("Unknown / raw","Future setting","Explicit Advanced/raw edit; unchanged bytes preserved.","Review raw value…"),
    ]
    for i,(kind,label,desc,control) in enumerate(kinds):
        x=40+(i%2)*780; y=122+(i//2)*148
        b.rect(x,y,740,132,"surface","border")
        b.text(x+16,y+28,kind,18,"accent",600)
        b.text(x+16,y+55,label,14,"text",600)
        b.text(x+16,y+110,desc,12,"muted")
        cx=x+410; cy=y+34
        if control=="slider":
            b.rect(cx,cy,92,40,"raised","border"); b.text(cx+12,cy+25,"value",14)
            b.line(x+520,y+58,x+702,y+58,"accent",3)
            b.rect(x+610,y+48,16,20,"accent")
            b.text(x+520,y+86,"min",12,"muted"); b.text(x+674,y+86,"max",12,"muted")
        elif kind=="Boolean":
            b.button(cx,cy,140,"On",True); b.button(cx+152,cy,140,"Off")
        elif kind=="Integer":
            b.button(cx,cy,44,"−"); b.rect(cx+52,cy,180,40,"raised","border")
            b.text(cx+70,cy+25,"value",14); b.button(cx+240,cy,44,"+")
        elif kind=="Multiline text":
            b.rect(cx,cy,306,56,"raised","border")
            b.text(cx+12,cy+22,"Welcome to our world.",14)
            b.text(cx+12,cy+44,"Bring your friends.",14)
        elif kind=="Compound":
            for j,label in enumerate(["Steam","Xbox","PS5","Mac"]):
                bx=cx+j*76; b.rect(bx,cy,68,40,"selected","accent")
                b.text(bx+6,cy+24,"✓ "+label,12,"accent")
        elif kind in ("Duration","Rate / multiplier"):
            b.rect(cx,cy,204,40,"raised","border"); b.text(cx+12,cy+25,"value",14)
            b.text(cx+220,cy+25,"hours" if kind=="Duration" else "×",14,"accent")
        else: b.button(cx,cy,306,control)
    b.text(40,1034,"Missing metadata never becomes a made-up default, validation range, restart badge or domain meaning.",14,"warning")
    return b


def states():
    b=B(1600,900,"Settings validation and navigation states")
    b.text(40,56,"SETTINGS / SAVE AND LEAVE STATES",28,"text",600)
    b.text(40,92,"Main Server · Family PC · R1 · c58a1000…a001 · independent scenario panels",14,"accent")
    cards=[("Invalid draft",["Experience gain: two", "Enter a numeric multiplier.", "Draft remains in the editor.", "Save is unavailable until valid."],["Return to field","Save —"]),
           ("Server running",["2 unsaved changes", "Stop server before saving.", "No automatic restart or forced stop.", "Draft changes are not effective values."],["Keep editing","Save —"]),
           ("Leave settings?",["2 unsaved changes on this server", "Save and leave if valid and stopped.", "Discard leaves saved values untouched.", "Stay keeps the draft and focus."],["Stay","Discard and leave"])]
    for i,(heading,lines,buttons) in enumerate(cards):
        x=40+i*520; b.rect(x,132,480,580,"surface","border")
        b.text(x+24,176,heading,24,"text",600)
        for j,line in enumerate(lines): b.text(x+24,232+j*48,line,14,"warning" if j==1 else "text")
        b.button(x+24,542,204,buttons[0],True); b.button(x+244,542,212,buttons[1])
        if i==2: b.button(x+24,598,432,"Save and leave · only when allowed")
    b.text(40,774,"Save rejects stale revisions. Keep the draft, explain the rejection and enter #38's review/reload flow.",14,"muted")
    b.text(40,810,"Offline / not authorized / unsupported schema: explain the cause; no queue, forced overwrite or raw-file fallback.",14,"muted")
    b.text(40,846,"Saving success comes from the Host acknowledgment and returned revision, never merely closing a panel.",14,"muted")
    return b


def advanced():
    b=B(1600,900,"Unknown setting preservation and secret editing states")
    b.text(40,54,"ADVANCED / UNKNOWN AND SECRET STATES",28,"text",600)
    b.text(40,90,"Main Server · Family PC · R1 · c58a1000…a001 · independent pattern panels",14,"accent")
    panels=[("Unknown future key",["FutureOption", "Original opaque value preserved", "Description / bounds / default: unknown", "Edit raw is an explicit choice.", "Other unknown fields remain unchanged."],"Review raw value…"),
            ("Secret unchanged",["Administrator password", "Stored · unchanged", "No stored secret is returned or revealed.", "Blank replacement does not mean delete.", "Revert abandons only the draft replacement."],"Replace password…"),
            ("Secret replacement",["Administrator password", "New value: ••••••••", "Only the newly typed value may be revealed.", "Clear stored secret is a separate action.", "No logging, diagnostic copy or draft disk save."],"Discard replacement")]
    for i,(title,lines,action) in enumerate(panels):
        x=40+i*520; b.rect(x,132,480,572,"surface","border")
        b.text(x+24,176,title,24,"text",600)
        for j,line in enumerate(lines): b.text(x+24,232+j*52,line,14,"muted" if j>=2 else "text")
        b.button(x+24,622,432,action,True)
    b.text(40,766,"Host redaction applies before every normal, raw, error, search, effective-value and comparison presentation.",14,"warning")
    b.text(40,804,"Raw editing is a bounded setting value, not arbitrary INI file access. No unknown value is normalized on an unrelated save.",14,"muted")
    b.text(40,842,"Conditional metadata pattern: Reset is unavailable without a known default; Revert restores the loaded configured value.",14,"muted")
    return b


if __name__=="__main__":
    parser=argparse.ArgumentParser(); parser.add_argument("--check",action="store_true"); args=parser.parse_args()
    workspace.foundation.validate_tokens()
    boards={"settings-16x9.svg":settings(1600),"settings-21x9.svg":settings(2100),"settings-narrow.svg":settings(800),"settings-narrow-details.svg":narrow_details(),"control-families.svg":gallery(),"save-states.svg":states(),"advanced-secrets.svg":advanced()}
    for name,b in boards.items():
        data="\n".join(b.parts+["</svg>"])+"\n"; ET.fromstring(data); path=ROOT/name
        if args.check: assert path.read_text(encoding="utf-8")==data,"Stale board: "+name
        else: path.write_text(data,encoding="utf-8",newline="\n")
    print("PASS: seven semantic-settings SVGs and shared contrast checks")
