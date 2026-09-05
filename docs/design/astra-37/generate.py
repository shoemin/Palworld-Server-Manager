"""Static Manager Settings and authorization diagrams; no security implementation."""
import argparse
import importlib.util
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location("workspace_design",ROOT.parent/"astra-35"/"generate.py")
w=importlib.util.module_from_spec(spec); spec.loader.exec_module(w)
B=w.B


def frame(title,section="Connections / Security",actor="Owner · this Host"):
    b=B(1600,900,title)
    b.rect(0,0,1600,56); b.text(24,35,"PALWORLD / SERVER MANAGER",16,"text",600)
    b.text(1140,35,"Activity",14,"accent"); b.text(1240,35,"Alerts",14); b.text(1330,35,"Manager Settings",14,"accent")
    w.foundation.rail(b,False)
    w.rail_selection(b,None)
    for x,name in zip([328,504,700,1040,1250],["General","Appearance","Connections / Security","Updates","Diagnostics"]):
        b.text(x,96,name,14,"accent" if name==section else "muted",600)
        if name==section: b.line(x,108,x+140,108,"accent",2)
    b.text(328,138,title,28,"text",600)
    b.text(328,170,"This PC · local Host 7d3a9100-1000-4000-8000-000000000001",14,"muted")
    b.text(328,200,"Acting as: "+actor+" · independent synthetic snapshot",14,"muted")
    b.line(0,856,1600,856); b.text(24,882,"Design prototype · labels are presentation, never authorization",12,"muted")
    return b


def panel(b,x,y,width,height,title,lines):
    b.rect(x,y,width,height,"surface","border")
    b.text(x+20,y+34,title,20,"text",600)
    for i,line in enumerate(lines): b.text(x+20,y+74+i*34,line,14,"muted")


def general():
    b=frame("General","General")
    panel(b,328,232,592,268,"Host at machine boot",["Off · desktop default", "Machine-wide service setting", "Requires privileged setup / maintenance on this PC", "Ordinary activation grants start/query only."])
    b.button(348,432,552,"Change boot mode — privileged setup required")
    panel(b,940,232,632,268,"Open UI at sign-in",["Off · this OS user", "Per-user client preference", "Opening UI may activate/connect to the local Host.", "Closing UI does not stop Host or running servers."])
    b.button(960,432,272,"Off"); b.button(1244,432,272,"On")
    panel(b,328,524,1244,264,"This Host identity",["Stable HostId: 7d3a9100-1000-4000-8000-000000000001", "Display name: This PC · semantic identity is separate from its credential", "Owner: Alex · LocalPrincipal · authority is structural, not a removable grant", "Private credentials are never shown here. Recovery guidance is local and exceptional."])
    return b


def appearance():
    b=frame("Appearance","Appearance")
    for i,(name,key) in enumerate([("Palworld Refined Desktop","refined"),("Dark Minimal","dark"),("Light Minimal","light")]):
        x=328+i*420; t=w.foundation.TOKENS["themes"][key]
        b.rect(x,232,404,270,"surface","accent" if i==0 else "border")
        b.text(x+16,266,name,18,"text",600)
        b.rect(x+16,290,372,136,t["canvas"])
        for j,color in enumerate(["surface","raised","accent"]): b.rect(x+32+j*112,314,96,88,t[color])
        b.text(x+16,470,"Selected" if i==0 else "Same layout and components",14,"accent" if i==0 else "muted")
    panel(b,328,534,1244,236,"Motion and accessibility",["Follow OS reduced-motion preference; no decorative pulses or animated background", "Focus rings, labels and status remain visible when transitions are removed.", "Theme selection changes client presentation, never Host policy or grants.", "Final cross-surface palette/responsive checks belong to #38."])
    return b


def trust():
    b=frame("Trusted Hosts / pairing states")
    panel(b,328,228,1244,96,"Trust and permissions are separate",["Active trust permits authorization checks; only actual grants permit management."])
    rows=[("Family PC · R1 · c58a1000…a001","Active","Review actual grants"),("Family PC · R2 · c58a1000…a002","PeerBound","Awaiting reciprocal activation · no management"),("Studio PC · R3 · 930b1000…a003","Replacement pending","Local Owner review required · no old grants restored"),("Archive PC · R4 · 771c1000…a004","Revoked","Retained history · fresh pairing and Owner gate")]
    for i,(host,state,detail) in enumerate(rows):
        y=350+i*92; b.rect(328,y,1244,80)
        b.text(348,y+28,host,16,"text",600); b.text(348,y+57,state,14,"accent" if state=="Active" else "warning")
        b.text(840,y+48,detail,14,"muted")
    b.button(328,746,240,"Start pairing…",True)
    b.text(592,772,"One-time code entry opens a bounded pairing panel; no real code in this artifact.",14,"muted")
    b.text(328,820,"Failed/expired attempts explain the result. PeerBound recovery is not ordinary management or a stale-code retry.",14,"muted")
    return b


def defaults():
    b=frame("Defaults for newly activated peers")
    panel(b,328,232,592,224,"Shipped factory template",["Host-provided · least privilege", "Inspect actual entries; do not infer them from a label.", "Not automatically reapplied to existing peers."])
    panel(b,940,232,632,224,"Currently configured template",["Custom · example configured by this Host's Owner", "CreateServer → exact target: this Host", "Applied as fresh Owner-root grants at activation."])
    panel(b,328,480,1244,264,"Review default change · Owner only",["Changes apply to future activations, not existing grant rows.", "ManagePermissions does not authorize editing this template.", "PeerBound gets no grants; activation uses the template configured at that time.", "Known-identity credential replacement still requires Owner approval; prior grants never revive silently."])
    b.button(348,682,220,"Cancel"); b.button(584,682,340,"Save future defaults",True)
    b.text(328,806,"This is a configured-template example, not a new factory preset or an AllServers grant.",14,"muted")
    return b


def grants():
    b=frame("Sharing and exact capability grants",actor="Alice · LocalPrincipal · non-Owner")
    b.text(328,244,"Presets are previews of Host-defined grant entries, not stored roles.",14,"muted")
    for i,name in enumerate(["View Only","Server Operator","Server Administrator","Host Administrator"]): b.button(328+i*312,264,296,name)
    panel(b,328,328,592,156,"Host-level authority",["Target: this Host · 7d3a9100…0001", "CreateServer / ManageHostUpdates are never server grants."])
    panel(b,940,328,632,156,"Server-level authority · Custom selected",["Target: Main Server · This PC · full ServerRef", "ViewServer / StartStopRestart / EditSettings are exact-server grants."])
    panel(b,328,508,1244,288,"Preview one delegated grant",["Issuer Alice · source G101 · capability ViewServer · target Main Server on this Host", "Grantee Bob · LocalPrincipal · DerivedFromGrantId is exactly G101", "Source permits delegation: Yes · source permits giving delegation rights: Yes", "New grant: CanDelegate Yes · CanDelegateOnwardDelegation No (independent controls)"])
    b.button(348,724,220,"Cancel"); b.button(584,724,340,"Review exact grant",True)
    return b


def provenance():
    b=frame("Grant provenance / single-parent forest")
    b.text(328,246,"Owner authority is not a grant. Root grants below are issued by the Owner.",14,"muted")
    nodes=[("G101 · root","Alice · LocalPrincipal","Delegate: Yes · Give rights: Yes"),("G102 · from G101","Bob · LocalPrincipal","Delegate: Yes · Give rights: No"),("G103 · from G102","R1 · RemoteManager","Delegate: No · Give rights: No")]
    for i,(title,actor,flags) in enumerate(nodes):
        x=328+i*424
        panel(b,x,286,396,210,title,[actor,"ViewServer · Main Server @ This PC",flags,"One exact provenance parent or none"])
        if i<2: b.line(x+396,392,x+424,392,"accent",2)
    panel(b,328,540,396,218,"G201 · independent root",["Alice · LocalPrincipal", "EditSettings · same exact server", "No parent · unaffected by G102 revocation"])
    panel(b,748,540,824,218,"Revoking G102",["Revokes G102 and descendant G103 together.", "G101 and independent root G201 remain valid.", "Not every grant Alice or Bob ever issued is revoked.", "Full graph inspection needs ManagePermissions (or Owner)."])
    b.text(328,816,"G101/G102/G103/G201 are readable display aliases for distinct grant identities in this synthetic diagram.",14,"muted")
    return b


def revocation():
    b=frame("Review revocation consequences")
    panel(b,328,232,592,536,"Revoke grant G102",["Target: Main Server · This PC · exact ServerRef", "Capability: ViewServer", "Affected: G102 (Bob), G103 (R1)", "Unaffected: G101 and independent G201", "Grant revocation follows this subtree only.", "The Host rechecks the current graph before commit."])
    b.button(348,692,248,"Cancel"); b.button(612,692,288,"Revoke 2 grants",True)
    panel(b,940,232,632,536,"Unpair / revoke trust with R1",["Family PC · c58a1000…a001 · exact peer identity", "Affected here: G103 · ViewServer · Main Server @ This PC", "G103 has no descendants in this reviewed snapshot.", "G101 / G102 / G201 remain; current graph is rechecked.", "This Host revokes now; peer notice is best-effort.", "An unreachable peer may retain its own stale state.", "Fresh pairing cannot silently restore prior grants."])
    b.button(960,692,240,"Cancel"); b.button(1216,692,332,"Revoke trust here",True)
    b.text(328,816,"Owner cannot be revoked by an ordinary local or remote operation. These confirmations never change Owner.",14,"warning")
    return b


def updates():
    b=frame("Updates and diagnostics","Updates",actor="Alice · LocalPrincipal · non-Owner")
    panel(b,328,232,592,232,"Client update / this user",["Client package state is separate from Host state.", "Display version is informational, not compatibility.", "No publication or update is executed by this prototype."])
    panel(b,940,232,632,232,"Host update / this Host",["ManageHostUpdates: not authorized in this scenario", "ManageHostSettings does not include update authority.", "Host-targeted operation; never a fake server target."])
    b.button(960,392,580,"Update Host — Not authorized")
    panel(b,328,488,1244,300,"Diagnostics / security history",["14:12 · LocalPrincipal Alex · Owner issued root grant G101 · this Host / Main Server", "14:14 · LocalPrincipal Alice · delegated grant G102 · same target / source G101", "14:18 · RemoteManager R1 · request denied · missing exact-target capability", "Audit shows actor kind, target, outcome and provenance; never pairing codes, tokens or private keys.", "Unsupported capability / incompatible protocol: explain negotiation result; do not compare app versions."])
    b.button(348,730,556,"Export history — Unsupported by this Host")
    b.text(928,756,"Unavailable; no raw-file fallback",14,"muted")
    b.text(328,828,"This panel illustrates authorized inspection only; a denied history request reveals no hidden actor or resource data.",14,"muted")
    return b


def recovery():
    b=frame("Owner and credential recovery guidance")
    panel(b,328,232,592,492,"Owner is structural",["Exactly one active Owner after initialization.", "Owner status is not a role preset or grant checkbox.", "Remote actors cannot revoke, demote or replace it.", "Ordinary local clients have no Owner-replacement RPC.", "OS activation-group membership is not enrollment.", "Non-Owner enrollment/reactivation needs Owner approval."])
    panel(b,940,232,632,492,"Exceptional recovery is local and privileged",["Use the accepted offline Host recovery procedure", "on the affected machine with actual OS privilege.", "Offline preparation requires Host stopped and exclusive lock.", "Intended-user completion uses the authenticated local client.", "Lost/compromised Host credential requires fresh local trust", "and peer-local Owner-approved re-pairing; no old grant revival."])
    b.text(328,764,"Routine rotation is different: staged credentials wait for remaining peers, or explicit Owner revocation/exclusion.",14,"muted")
    b.text(328,802,"No generic administrator console, one-click remote Owner reset, or trust-anyway bypass is introduced.",14,"warning")
    return b


if __name__=="__main__":
    parser=argparse.ArgumentParser(); parser.add_argument("--check",action="store_true"); args=parser.parse_args(); w.foundation.validate_tokens()
    boards={"general.svg":general(),"appearance.svg":appearance(),"trust.svg":trust(),"defaults.svg":defaults(),"grants.svg":grants(),"provenance.svg":provenance(),"revocation.svg":revocation(),"updates-diagnostics.svg":updates(),"recovery.svg":recovery()}
    for name,b in boards.items():
        data="\n".join(b.parts+["</svg>"])+"\n"; ET.fromstring(data); path=ROOT/name
        if args.check: assert path.read_text(encoding="utf-8")==data,"Stale board: "+name
        else: path.write_text(data,encoding="utf-8",newline="\n")
    print("PASS: nine Manager Settings/security SVGs and shared contrast checks")
