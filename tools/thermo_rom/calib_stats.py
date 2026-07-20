import math, json, subprocess, os, re
REPO=r"C:\ai_project\AprVisual"
EXE=os.path.join(REPO,"tools","aprnes","bin","Release","AprNes.exe")
ROM=os.path.join(REPO,"tools","thermo_rom","thermo.nes")
def run(temp, dump="0010", tt=10):
    out=subprocess.run([EXE,"--rom",ROM,"--openbus-temp",str(temp),"--time",str(tt),
                        "--dump-mem",dump],capture_output=True,text=True,cwd=REPO).stdout
    m=re.search(r"u24_le=(\d+)",out); return int(m.group(1)) if m else None

# ---- A. determinism / per-point %RSD ----
print("A) DETERMINISM (per-point %RSD): 25.0C x5 raw counts:")
reps=[run(25) for _ in range(5)]
print("   ",reps, "-> RSD =", 0.0 if len(set(reps))==1 else "NONZERO")

# ---- B. fit diagnostics on the per-degree calibration ----
counts={int(k):v for k,v in json.load(open(os.path.join(REPO,"tools","thermo_rom","counts_by_degree.json"))).items()}
Ts=sorted(counts); x=[1.0/(T+273.15) for T in Ts]; y=[math.log(counts[T]) for T in Ts]
def linfit(X,Y,deg):
    # least squares polynomial in X of given degree; return coeffs (c0..cdeg), R2, residuals
    import itertools
    n=len(X); m=deg+1
    A=[[sum(X[i]**(r+c) for i in range(n)) for c in range(m)] for r in range(m)]
    B=[sum((X[i]**r)*Y[i] for i in range(n)) for r in range(m)]
    # gauss
    M=[row[:]+[B[i]] for i,row in enumerate(A)]
    for i in range(m):
        p=M[i][i]
        for j in range(i,m+1): M[i][j]/=p
        for k in range(m):
            if k!=i:
                f=M[k][i]
                for j in range(i,m+1): M[k][j]-=f*M[i][j]
    coef=[M[i][m] for i in range(m)]
    yh=[sum(coef[r]*X[i]**r for r in range(m)) for i in range(n)]
    ybar=sum(Y)/n; ss_res=sum((Y[i]-yh[i])**2 for i in range(n)); ss_tot=sum((v-ybar)**2 for v in Y)
    return coef, 1-ss_res/ss_tot, [Y[i]-yh[i] for i in range(n)]
k=8.617e-5
c1,r2_1,res1=linfit(x,y,1)     # log-linear = single Arrhenius: ln c = a + b/T
c2,r2_2,res2=linfit(x,y,2)     # +1/T^2
print("\nB) FIT DIAGNOSTICS (ln count vs 1/T), n=%d points:"%len(Ts))
print("   single-Arrhenius (log-linear): R2=%.6f  Ea=%.4f eV  max|resid|=%.4f (ln) ~ %.2f%% in count"
      %(r2_1, c1[1]*k, max(abs(r) for r in res1), 100*(math.exp(max(abs(r) for r in res1))-1)))
print("   quadratic (+1/T^2)          : R2=%.6f  max|resid|=%.4f (ln)"%(r2_2, max(abs(r) for r in res2)))
# residual sign pattern (systematic vs random) for the Arrhenius fit
signs="".join("+" if r>0 else "-" for r in res1)
print("   Arrhenius residual signs cold->warm:", signs, " (runs, not random = model mismatch)")

# ---- C. held-out high/low check: fractional temps NOT in the integer calibration ----
print("\nC) HELD-OUT CHECK (temps not in the calibration; ROM displayed vs set):")
for T in [3.5,12.5,22.5,27.5,37.5,47.5]:
    idx=run(T, dump="0013")
    print("   set %5.1fC -> displayed %4.1fC  (err %+.1f)"%(T, idx/10.0, idx/10.0-T))
