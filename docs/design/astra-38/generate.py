"""Deterministic final design boards, using accepted shared semantic tokens."""
import argparse
import importlib.util
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location("security_design",ROOT.parent/"astra-37"/"generate.py")
s=importlib.util.module_from_spec(spec); spec.loader.exec_module(s)
w=s.w

def frame(title,width=1600,theme="refined"):
    b=w.B(width,900,title,theme)
    b.rect(0,0,width,56); b.text(24,35,"PALWORLD / SERVER MANAGER",16,"text",600)
    b.text(width-440,35,"Activity",14,"accent"); b.text(width-328,35,"Alerts",14); b.text(width-240,35,"Manager Settings",14)
    w.foundation.rail(b,False); w.rail_selection(b,None)
    b.text(328,110,title,28,"text",600)
    b.text(328,148,"Independent synthetic snapshot · exact targets retained in every detail",14,"muted")
    b.line(0,856,width,856); b.text(24,882,"Design only · Host owns operations · closing a surface does not cancel work",12,"muted")
    return b

def activity(width=1600,theme="refined"):
    b=frame("Activity Center",width,theme); right=width-352
    for x,label in [(328,"All"),(424,"Active"),(560,"Needs attention"),(792,"History")]: b.text(x,204,label,16,"accent",600)
    cards=[("Backup · op B01","Main Server · This PC · L · Stopped","Verifying archive · last report 14:20:04 · live","Server target / server lock · Host continues after UI disconnect", "View operation"),
           ("Host update · op U02","Family PC · R1 · Host c58a1000…a001","Recovery Required · interrupted during apply","Host target / Host-wide lock retained · inspect declared recovery", "Review recovery"),
           ("Incoming transfer · offer T03","From Family PC · R2 → This PC","Awaiting your decision · not receiving/importing yet","Exact offer and destination review; other servers stay usable", "Review offer")]
    for i,(title,target,state,detail,action) in enumerate(cards):
        y=236+i*188; b.rect(328,y,right-352,168,"surface","border")
        b.text(348,y+30,title,18,"text",600); b.text(348,y+60,target,14,"accent")
        b.text(348,y+88,state,14,"warning" if i else "success"); b.text(348,y+116,detail,12,"muted")
        b.button(348,y+124,240,action)
    b.rect(right,188,324,596,"raised","border")
    b.text(right+20,226,"Operation B01",20,"text",600)
    for i,line in enumerate(["Authoritative Host: This PC","Server: Main Server · L","Full identity in Details","Phase: Verifying archive","Progress: not reported","Initiator UI disconnected","Host operation remains active","No cancel support reported"]): b.text(right+20,270+i*38,line,14,"muted")
    b.button(right+20,632,284,"Close details")
    return b

def send():
    b=frame("Send to PC · review before offer"); w.rail_selection(b,"L","Running")
    s.panel(b,328,200,596,568,"Source server",["Main Server · This PC · L", "Host 7d3a9100…0001 / profile 680a9100…0007", "Running · must save and stop before export", "No live copy and no automatic restart promised.", "TransferExport and safe-stop authority are rechecked.", "Stop/finalization failure prevents package creation."])
    s.panel(b,948,200,624,568,"Destination / consequences",["Family PC · R1 · Host c58a1000…a001", "Active trust; actual transfer authorization required", "Package includes world/config; excludes runtime.", "Destination reviews size and integrity information.", "No bytes sent until this offer is accepted.", "Verified receipt does not automatically import."])
    b.button(348,696,228,"Cancel"); b.button(592,696,304,"Save, stop and offer…",True)
    b.text(328,814,"Unavailable destination explains offline / unpaired / not authorized / unsupported; nothing is queued offline.",14,"muted")
    return b

def incoming():
    b=frame("Incoming transfer · scoped review")
    b.rect(12,484,256,64); b.text(24,509,"Main Server",14,"text",600); b.text(24,532,"Stopped · Host connected",12,"muted")
    s.panel(b,328,200,788,568,"Offer T03 · receive on This PC",["Sender: Family PC · R2 · c58a1000…a002", "Source: Main Server · R2 / profile 680a9100…0007", "Package: MainServer.palserver · 2.4 GiB · example", "Expected SHA-256: available in integrity details", "Accept receives this package; it does not import it.", "Only verified bytes become a usable package.", "Host rechecks offer identity, expiry, authority and capacity."])
    b.button(348,696,320,"Reject offer T03"); b.button(688,696,404,"Accept receipt on This PC",True)
    s.panel(b,1140,200,432,568,"Other work stays available",["This is a contextual panel.", "No application-wide modal lock.", "Escape closes without deciding.", "Reopen from Activity / Alerts.", "Decision stays Host-owned.", "Expired offer cannot be accepted."])
    b.text(328,816,"Received and verified → separate Import review (#35); rejected / expired → terminal explanation, no import.",14,"muted")
    return b

def conflict():
    b=frame("Settings changed elsewhere · review required")
    s.panel(b,328,200,1244,132,"Main Server · This PC · L · stopped",["Your save using revision 42 was rejected. Host is at revision 43; nothing from your draft was applied."])
    for x,title,lines in [(328,"Original · revision 42",["Fast travel: On","Experience: 1×","Admin secret: redacted"]),(748,"Your unsaved draft",["Fast travel: Off","Experience: 2×","Admin secret: no change"]),(1168,"Current Host · revision 43",["Fast travel: On","Experience: 3×","Admin secret: redacted"])]: s.panel(b,x,356,404,216,title,lines)
    b.button(328,610,388,"Reload / discard this draft…")
    b.button(740,610,404,"Review draft against latest",True)
    b.text(328,704,"Reload asks before discarding. Review retains nonsecret choices in memory; each choice is explicit.",14,"muted")
    b.text(328,746,"Save after review submits revision 43; another change rejects again. No force overwrite or automatic merge.",14,"muted")
    b.text(328,788,"Newly typed secrets are cleared and must be re-entered; stored secrets never enter comparison or diagnostics.",14,"warning")
    return b

def states():
    b=frame("Busy, offline and recovery are different states")
    data=[("Operation locked","Main Server · This PC · L","Blocked by B01 · server lock","Read-only observations remain when safe","View blocking operation"),
          ("Host-wide operation locked","Family PC · R1","U02 Host lock blocks all server-exclusive work here","Other Hosts remain independently usable","View Host operation"),
          ("Recovery Required","Family PC · R1 · U02","Host reported RequiresManualReview","Lock remains until Host resolves it; no blind resume","Review recovery details"),
          ("Host offline / last known","Main Server · Family PC · R2","Last observed 14:17:02 · not live","Writes unavailable; reconnect refreshes before actions","Reconnect / inspect last known")]
    for i,(title,target,state,detail,action) in enumerate(data):
        x=328+(i%2)*632; y=200+(i//2)*312
        s.panel(b,x,y,612,288,title,[target,state,detail]); b.button(x+20,y+220,572,action)
    return b

def failures():
    b=frame("Failure and availability details")
    rows=[("Transfer failed · T04","Integrity mismatch; no usable package or import","Review failure; new offer only after Host permits retry"),
          ("Update failed · U05","Host reports terminal failure; lock resolved","Inspect result; no automatic apply or success claim"),
          ("REST degraded · Main Server / L","Process status fresh; players/REST last known 14:17","Refresh status; safe stop unavailable while REST is unavailable"),
          ("Not authorized · Host update / R1","Exact-target ManageHostUpdates missing","Explain denial; pairing and ManageHostSettings do not grant it"),
          ("Unsupported capability","Requested stream not negotiated","Control unavailable; no arbitrary REST/file fallback"),
          ("Protocol incompatible","Protocol major mismatch","Management unavailable; display versions are informational")]
    for i,(title,state,action) in enumerate(rows):
        y=192+i*104; b.rect(328,y,1244,92,"surface","border")
        b.text(348,y+27,title,18,"warning",600); b.text(348,y+53,state,14,"text"); b.text(348,y+77,action,14,"muted")
    return b

def logs():
    b=frame("Main Server · bounded log detail")
    b.text(328,194,"This PC · L · Host 7d3a9100…0001 / profile 680a9100…0007",14,"accent")
    b.text(328,232,"Display paused · last shown 14:20:04 · not following live output",16,"warning",600)
    b.rect(328,260,1244,424,"surface","border")
    for i,line in enumerate(["14:19:59  Host observation connected · synthetic log excerpt", "14:20:00  Server process observed · authorized local target", "14:20:04  Last visible line before presentation pause", "", "Resume follows the Host stream; any unavailable interval is labelled as a gap.", "Secrets are redacted before display. No command input or arbitrary path exists."]): b.text(348,300+i*48,line,14,"muted")
    b.button(328,714,300,"Follow latest"); b.button(652,714,300,"Close log details")
    b.text(328,812,"Opening a folder is a separate bounded local-client action, unavailable for a remote server target.",14,"muted")
    return b

if __name__=="__main__":
    p=argparse.ArgumentParser(); p.add_argument("--check",action="store_true"); a=p.parse_args(); w.foundation.validate_tokens()
    boards={"activity.svg":activity(),"activity-wide.svg":activity(2100),"activity-dark.svg":activity(theme="dark"),"activity-light.svg":activity(theme="light"),"send.svg":send(),"incoming.svg":incoming(),"conflict.svg":conflict(),"states.svg":states(),"failures.svg":failures(),"logs.svg":logs()}
    for name,b in boards.items():
        value="\n".join(b.parts+["</svg>"])+"\n"; ET.fromstring(value); path=ROOT/name
        if a.check: assert path.read_text(encoding="utf-8")==value,"Stale: "+name
        else: path.write_text(value,encoding="utf-8",newline="\n")
    print("PASS: ten final design boards; deterministic XML and shared palette contrast")
