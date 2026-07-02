"""Generate tools/testrom/catalog.json — the A / A-r / C test set (93 ROMs).

Re-derives the classification (same logic as classify_testroms.py): supported =
mapper 0, not pal_apu_tests; class from AprNes empirical results.json result_text.
B-class (screen-text) ROMs are excluded for now (later phase).
"""
import os, glob, json

CHECKED = r"C:\ai_project\AprVisual\nes-test-roms-master\checked"
RESULTS = r"C:\ai_project\AprNes\site\report\results.json"
OUT     = r"C:\ai_project\AprVisual\tools\testrom\catalog.json"

EXPECTED_CRCS = {
    "dmc_dma_during_read4/dma_2007_read.nes":    ["159A7A8F", "5E3DF9C4"],
    "dmc_dma_during_read4/double_2007_read.nes": ["85CFD627", "F018C287", "440EF923", "E52F41A5"],
}

recs = {}
for r in json.load(open(RESULTS, encoding="utf-8")):
    recs[f"{r['suite']}/{r['rom']}"] = r

def classify(rel):
    rec = recs.get(rel)
    if rec is None:
        return None
    txt = rec["result_text"]
    rom = rel.split("/")[-1]
    marker = f"PASS | {rom} | "
    i = txt.find(marker)
    tail = txt[i + len(marker):].strip() if i >= 0 else ""
    softreset = "Auto soft reset" in txt
    if tail.startswith("(screen CRC:"):
        return "C"
    if tail.startswith("(screen:") or tail.startswith("(no $6000"):
        return "B"
    if rel in EXPECTED_CRCS:
        return "C"
    return "A-r" if softreset else "A"

tests = []
for path in sorted(glob.glob(os.path.join(CHECKED, "**", "*.nes"), recursive=True)):
    with open(path, "rb") as fp:
        b = fp.read(16)
    rel = os.path.relpath(path, CHECKED).replace("\\", "/")
    d = rel.split("/")[0]
    if len(b) < 16 or b[:4] != b"NES\x1a":
        continue
    if ((b[6] >> 4) | (b[7] & 0xF0)) != 0 or d == "pal_apu_tests":
        continue
    cls = classify(rel)
    if cls in ("A", "A-r", "C"):
        suite = "/".join(rel.split("/")[:-1])
        rom = rel.split("/")[-1]
        entry = {"suite": suite, "rom": rom, "class": cls, "maxFrames": 900}
        if cls == "A-r":
            entry["maxFrames"] = 1500   # reset loops add sim time
        if rel in EXPECTED_CRCS:
            entry["expectedCrcs"] = EXPECTED_CRCS[rel]
        tests.append(entry)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8") as fp:
    json.dump({"schema": "aprvisual-testrom-catalog/1", "romBase": "nes-test-roms-master/checked", "tests": tests}, fp, indent=2)

from collections import Counter
c = Counter(t["class"] for t in tests)
print(f"CATALOG_TESTS={len(tests)} A={c['A']} Ar={c['A-r']} C={c['C']}")
print(f"OUT_BYTES={os.path.getsize(OUT)}")
