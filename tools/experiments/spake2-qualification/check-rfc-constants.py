"""Compare the pinned upstream test oracle with an independently obtained RFC text."""
from pathlib import Path
import re
import sys

if len(sys.argv) != 3:
    raise SystemExit("Usage: check-rfc-constants.py <upstream checkout> <rfc9382.txt>")
source = (Path(sys.argv[1]) / "pakery-tests/tests/spake2_p256_vectors.rs").read_text()
rfc = re.sub(r"\s+", "", Path(sys.argv[2]).read_text())
constants = re.findall(r'const (V[1-4]_\w+): &str = "([0-9a-f]+)";', source)
if len(constants) != 42:
    raise SystemExit(f"Unexpected oracle shape: {len(constants)} constants")
for name, value in constants:
    if value not in rfc:
        raise SystemExit(f"RFC constant mismatch: {name}")
print("PASS: all 42 upstream vector constants found in RFC 9382 after whitespace normalization")
