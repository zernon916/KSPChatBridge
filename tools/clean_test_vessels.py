"""Remove leftover test vessels from a KSP save (KSP must be CLOSED).

  python tools/clean_test_vessels.py "<save dir>"            # dry run: list vessels
  python tools/clean_test_vessels.py "<save dir>" --apply    # remove ships/debris/probes, free their crew

Keeps asteroids/comets (SpaceObject), flags and anything whose type isn't Ship/Probe/Debris/Lander/Rover/
Station/Base/Plane/Relay. A timestamped backup of persistent.sfs is written first.
"""
import re
import shutil
import sys
import time
from pathlib import Path

REMOVE_TYPES = {"Ship", "Probe", "Debris", "Lander", "Rover", "Station", "Base", "Plane", "Relay"}


def blocks(lines, name, start=0, end=None):
    """Yield (begin, end_exclusive) line spans of top-level `name { ... }` nodes inside lines[start:end]."""
    end = len(lines) if end is None else end
    i = start
    while i < end:
        if lines[i].strip() == name and i + 1 < end and lines[i + 1].strip() == "{":
            depth, j = 0, i + 1
            while j < end:
                t = lines[j].strip()
                depth += t.count("{") - t.count("}")
                if depth == 0:
                    break
                j += 1
            yield i, j + 1
            i = j + 1
        else:
            i += 1


def field(lines, a, b, key):
    for l in lines[a:b]:
        m = re.match(r"\s*" + key + r"\s*=\s*(.*)", l)
        if m:
            return m.group(1).strip()
    return ""


def main():
    save = Path(sys.argv[1])
    apply = "--apply" in sys.argv
    sfs = save / "persistent.sfs"
    lines = sfs.read_text(encoding="utf-8").splitlines(keepends=True)
    fs = next(blocks(lines, "FLIGHTSTATE"))
    remove, crew = [], set()
    for a, b in blocks(lines, "VESSEL", fs[0] + 2, fs[1] - 1):
        name, typ = field(lines, a, b, "name"), field(lines, a, b, "type")
        sit = field(lines, a, b, "sit")
        kill = typ in REMOVE_TYPES
        names = {re.sub(r"\s*crew\s*=\s*", "", l).strip() for l in lines[a:b] if re.match(r"\s*crew\s*=", l)}
        print(f"{'REMOVE' if kill else 'keep  '} {typ:8} {sit:12} {name}  crew={sorted(names)}")
        if kill:
            remove.append((a, b))
            crew |= names
    if not apply:
        print(f"dry run: would remove {len(remove)} vessel(s), free crew {sorted(crew)}")
        return
    shutil.copy2(sfs, sfs.with_name(f"persistent.backup-{time.strftime('%Y%m%d-%H%M%S')}.sfs"))
    for a, b in reversed(remove):
        del lines[a:b]
    for k, l in enumerate(lines):  # active vessel index may now point past the list
        if re.match(r"\s*activeVessel\s*=", l):
            lines[k] = re.sub(r"=\s*-?\d+", "= 0", l)
            break
    # free crew: state Assigned -> Available for kerbals that were aboard removed vessels
    for a, b in list(blocks(lines, "KERBAL")):
        if field(lines, a, b, "name") in crew:
            for k in range(a, b):
                if re.match(r"\s*state\s*=\s*Assigned", lines[k]):
                    lines[k] = re.sub(r"Assigned", "Available", lines[k])
    sfs.write_text("".join(lines), encoding="utf-8")
    print(f"removed {len(remove)} vessel(s); freed crew {sorted(crew)}; backup written")


if __name__ == "__main__":
    main()
