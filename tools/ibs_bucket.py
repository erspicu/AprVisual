import csv, re, sys
from collections import defaultdict

OUT = sys.argv[1] if len(sys.argv) > 1 else r"C:\ai_project\AprVisual\temp\ibs_g2"
fp_txt = OUT + "_o.txt"
dump   = OUT + r"\IbsOpDump.csv"

ranges = []
for ln in open(fp_txt, encoding="utf-8", errors="ignore"):
    m = re.search(r'#\s+(\w+)\s+0x([0-9A-Fa-f]+)\.\.0x([0-9A-Fa-f]+)\s+([\d.]+)\s*KB', ln)
    if m: ranges.append((m.group(1), int(m.group(2),16), int(m.group(3),16), float(m.group(4))))
print(f"parsed {len(ranges)} array ranges from {fp_txt}")

def bucket(a):
    for name, lo, hi, kb in ranges:
        if lo <= a < hi: return name
    return None

rows = list(csv.reader(open(dump, encoding="utf-8", errors="ignore")))
col = {n:i for i,n in enumerate(rows[0])}
def gi(*ns):
    for n in ns:
        if n in col: return col[n]
    return None
c_ld=gi("IbsLdOp"); c_valid=gi("IbsDcLinAddrValid"); c_miss=gi("IbsDcMiss")
c_l2=gi("IbsL2Miss"); c_lat=gi("IbsDcMissLat"); c_addr=gi("IbsDcLinAd"); c_km=gi("Kern_mode")

def T(s): return (s or "").strip() in ("1","TRUE","True","true")
def H(s):
    s=(s or "").strip()
    if not s: return 0
    try: return int(s,16) if (s.lower().startswith("0x") or re.search('[a-fA-F]',s)) else int(s)
    except:
        try: return int(s,16)
        except: return 0

USERMAX = 0x0000_8000_0000_0000   # canonical user vs kernel split
ld=defaultdict(int); l1=defaultdict(int); l2=defaultdict(int); lat=defaultdict(float); latn=defaultdict(int)
u_tot=u_in=u_unb=0; k_tot=0; u_l1=u_l1_in=0; u_l2=u_l2_in=0
for r in rows[1:]:
    if c_ld is None or len(r)<=c_addr or not T(r[c_ld]): continue
    if c_valid is not None and not T(r[c_valid]): continue
    a=H(r[c_addr])
    if a==0: continue
    if a>=USERMAX: k_tot+=1; continue          # kernel-mode load
    u_tot+=1
    miss=T(r[c_miss]) if c_miss is not None else False
    isl2=T(r[c_l2]) if c_l2 is not None else False
    if miss: u_l1+=1
    if isl2: u_l2+=1
    b=bucket(a)
    if b is None: u_unb+=1; continue
    u_in+=1; ld[b]+=1
    if miss:
        l1[b]+=1; u_l1_in+=1
        v=H(r[c_lat]) if c_lat is not None else 0
        if v>0: lat[b]+=v; latn[b]+=1
    if isl2: l2[b]+=1; u_l2_in+=1

print(f"\nLOAD samples: kernel={k_tot}  user={u_tot}  (user in-array={u_in}, user other heap/JIT/stack={u_unb})")
print(f"USER-mode L1-miss={u_l1} (in-array {u_l1_in}) | USER-mode L2-miss={u_l2} (in-array {u_l2_in})\n")
print(f"{'array':<18}{'loads':>7}{'load%':>7}{'L1miss':>7}{'L1m%':>7}{'L2miss':>7}{'L2m%':>7}{'avgLat':>8}")
order=sorted(set(list(ld)+list(l1)+list(l2)), key=lambda k:-ld.get(k,0))
for k in order:
    lp=100*ld[k]/max(u_in,1); m1p=100*l1.get(k,0)/max(u_l1_in,1); m2p=100*l2.get(k,0)/max(u_l2_in,1)
    al=lat.get(k,0)/latn[k] if latn.get(k) else 0
    print(f"{k:<18}{ld[k]:>7}{lp:>6.1f}%{l1.get(k,0):>7}{m1p:>6.1f}%{l2.get(k,0):>7}{m2p:>6.1f}%{al:>7.0f}c")
print(f"{'(other user mem)':<18}{u_unb:>7}{100*u_unb/max(u_tot,1):>6.1f}%   (.NET heap / JIT code / stack)")
