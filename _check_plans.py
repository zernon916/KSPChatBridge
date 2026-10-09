"""One-off validation of FLIGHTPLAN.md plan blocks against the flight-plan parser."""
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from kspchat import flightplan as fp

text = pathlib.Path("FLIGHTPLAN.md").read_text(encoding="utf-8")
blocks = re.findall(r"```([^`]*)```", text, re.S)
bad = 0
for b in blocks:
    steps, errors = fp.parse(b.strip(), strict=True)
    for e in errors:
        bad += 1
        print("ERR", b.strip().splitlines()[0][:40], e)
print("blocks:", len(blocks), "bad:", bad)