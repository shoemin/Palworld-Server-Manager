import json
import sys
from pathlib import Path
m=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
nodes={n['id']:n for n in m['resolve']['nodes']}; pending=[m['resolve']['root']]; reachable=set()
while pending:
    item=pending.pop()
    if item in reachable: continue
    reachable.add(item); pending.extend(nodes[item]['dependencies'])
packages=[p for p in m['packages'] if p['id'] in reachable and p['name']!='palworld-spake2']
parts=['Pinned Windows x64 SPAKE2 dependency notices. Compiler/runtime licenses are separate.\n']
texts={}; missing=[]
for p in sorted(packages,key=lambda p:(p['name'],p['version'])):
    root=Path(p['manifest_path']).parent
    files=sorted({f for pattern in ('LICENSE*','COPYING*') for f in root.glob(pattern) if f.is_file()})
    if not files: missing.append(p['name'])
    for f in files:
        content='\n'.join(line.rstrip() for line in f.read_text(encoding='utf-8').splitlines())+'\n'
        texts.setdefault(content,[]).append(f"{p['name']} {p['version']} ({f.name}; {p['license']})")
assert not missing, missing
for content,owners in texts.items():
    parts.append('\n'+'='*72+'\n'+'\n'.join(owners)+'\n\n'+content)
Path(sys.argv[2]).write_text('\n'.join(parts),encoding='utf-8')
print(len(packages),'reachable dependencies;',len(texts),'distinct notice texts')
