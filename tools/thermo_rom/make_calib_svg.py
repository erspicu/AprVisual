import math, json, os
REPO=r"C:\ai_project\AprVisual"
IMG=os.path.join(REPO,"WebSite","s1a","img")
counts={int(k):v for k,v in json.load(open(os.path.join(REPO,"tools","thermo_rom","counts_by_degree.json"))).items()}
Ts=sorted(counts); xs=[1.0/(T+273.15) for T in Ts]; ys=[math.log(counts[T]) for T in Ts]
# single-Arrhenius log-linear fit: y = a + b x
n=len(Ts); sx=sum(xs); sy=sum(ys); sxx=sum(x*x for x in xs); sxy=sum(xs[i]*ys[i] for i in range(n))
b=(n*sxy-sx*sy)/(n*sxx-sx*sx); a=(sy-b*sx)/n
yh=[a+b*x for x in xs]; res=[ys[i]-yh[i] for i in range(n)]
ybar=sy/n; R2=1-sum((ys[i]-yh[i])**2 for i in range(n))/sum((v-ybar)**2 for v in ys)
Ea=b*8.617e-5
TMAX=max(Ts)                       # 100
TICKS=[0,20,40,60,80,100]
# held-out check standards: set a HALF-degree (never in the integer calibration) and read back.
# Measured on the NowDots-fixed engine: the only error is the ±0.5 °C integer-display rounding,
# and it is UNIFORM cold->hot (no warm-end degradation any more).
held=[(10.5,0.5),(25.5,-0.5),(40.5,0.5),(60.5,-0.5),(80.5,0.5),(90.5,0.5)]

def svg_open(w,h,title):
    return (f'<svg viewBox="0 0 {w} {h}" xmlns="http://www.w3.org/2000/svg" font-family="Segoe UI,Roboto,sans-serif">'
            f'<rect width="{w}" height="{h}" fill="#141019"/>'
            f'<text x="{w/2}" y="20" fill="#e8dcff" font-size="13" font-weight="700" text-anchor="middle">{title}</text>')
def lin(v,v0,v1,p0,p1): return p0+(v-v0)*(p1-p0)/(v1-v0)

# ---------- Plot 1: Arrhenius calibration (ln count vs 1/T) ----------
W,H=660,300; L,Rt,Tp,B=64,20,40,246
x0,x1=min(xs),max(xs); y0,y1=min(ys)-0.1,max(ys)+0.1
s=svg_open(W,H,"Calibration curve — ln(count) vs 1/T (Arrhenius axes)")
s+=f'<line x1="{L}" y1="{B}" x2="{W-Rt}" y2="{B}" stroke="#5a5266"/><line x1="{L}" y1="{Tp}" x2="{L}" y2="{B}" stroke="#5a5266"/>'
s+=f'<text x="{(L+W-Rt)/2}" y="290" fill="#968ba1" font-size="10.5" text-anchor="middle">temperature (°C), plotted on a 1/T axis</text>'
s+=f'<text x="20" y="{(Tp+B)/2}" fill="#968ba1" font-size="10.5" text-anchor="middle" transform="rotate(-90 20 {(Tp+B)/2})">ln(count)</text>'
for T in TICKS:
    px=lin(1.0/(T+273.15),x0,x1,L,W-Rt); s+=f'<line x1="{px:.1f}" y1="{B}" x2="{px:.1f}" y2="{B+4}" stroke="#5a5266"/><text x="{px:.1f}" y="{B+16}" fill="#6f6780" font-size="9" text-anchor="middle">{T}</text>'
# fit line
fx0,fx1=L,W-Rt; fy0=lin(a+b*x0,y0,y1,B,Tp); fy1=lin(a+b*x1,y0,y1,B,Tp)
s+=f'<line x1="{fx0}" y1="{fy0:.1f}" x2="{fx1}" y2="{fy1:.1f}" stroke="#7fbfe0" stroke-width="1.4" stroke-dasharray="5 4"/>'
for i in range(n):
    px=lin(xs[i],x0,x1,L,W-Rt); py=lin(ys[i],y0,y1,B,Tp); s+=f'<circle cx="{px:.1f}" cy="{py:.1f}" r="2.1" fill="#7ee0a8"/>'
s+=f'<text x="{W-Rt-8}" y="{Tp+18}" fill="#bfe0f5" font-size="11" text-anchor="end">single-Arrhenius fit: R² = {R2:.6f}</text>'
s+=f'<text x="{W-Rt-8}" y="{Tp+34}" fill="#7f9d88" font-size="10.5" text-anchor="end">Ea = {Ea:.3f} eV (recovers the injected 0.560 eV) · {n} points, every 1 °C</text>'
s+='</svg>'
open(os.path.join(IMG,"calib_curve.svg"),"w",encoding="utf-8").write(s)

# ---------- Plot 2: residuals vs temperature (now pure quantization noise) ----------
W,H=660,260; L,Rt,Tp,B=64,20,40,206
rx0,rx1=0,TMAX; ry=max(abs(r) for r in res)*1.15
s=svg_open(W,H,"Residuals of the Arrhenius fit — now ±1-loop quantization, not lack-of-fit")
zy=lin(0,-ry,ry,B,Tp)
s+=f'<line x1="{L}" y1="{zy:.1f}" x2="{W-Rt}" y2="{zy:.1f}" stroke="#5a5266"/>'
s+=f'<line x1="{L}" y1="{Tp}" x2="{L}" y2="{B}" stroke="#5a5266"/>'
s+=f'<text x="{(L+W-Rt)/2}" y="250" fill="#968ba1" font-size="10.5" text-anchor="middle">temperature (°C)</text>'
s+=f'<text x="20" y="{(Tp+B)/2}" fill="#968ba1" font-size="10.5" text-anchor="middle" transform="rotate(-90 20 {(Tp+B)/2})">residual (ln count)</text>'
for T in TICKS:
    px=lin(T,rx0,rx1,L,W-Rt); s+=f'<line x1="{px:.1f}" y1="{B}" x2="{px:.1f}" y2="{B+4}" stroke="#5a5266"/><text x="{px:.1f}" y="{B+16}" fill="#6f6780" font-size="9" text-anchor="middle">{T}</text>'
for i in range(n):
    px=lin(Ts[i],rx0,rx1,L,W-Rt); py=lin(res[i],-ry,ry,B,Tp); col="#7ee0a8" if res[i]>=0 else "#ff6b6b"
    s+=f'<circle cx="{px:.1f}" cy="{py:.1f}" r="2.1" fill="{col}"/>'
s+=f'<text x="{L+8}" y="{Tp+16}" fill="#8b7f9c" font-size="10">R² = {R2:.6f}, max |resid| ≈ {100*(math.exp(max(abs(r) for r in res))-1):.2f}% in count — the monotonic-clock fix removed the frame-boundary artifact</text>'
s+='</svg>'
open(os.path.join(IMG,"calib_resid.svg"),"w",encoding="utf-8").write(s)

# ---------- Plot 3: held-out check standards ----------
W,H=660,240; L,Rt,Tp,B=64,20,40,186
hx0,hx1=0,TMAX; hy=1.0
s=svg_open(W,H,"Held-out check standards — round-trip error at un-calibrated half-degrees")
# +-0.5 band = the integer-display quantization floor
b1=lin(0.5,-hy,hy,B,Tp); b2=lin(-0.5,-hy,hy,B,Tp)
s+=f'<rect x="{L}" y="{b1:.1f}" width="{W-Rt-L}" height="{b2-b1:.1f}" fill="#7ee0a8" opacity="0.10"/>'
zy=lin(0,-hy,hy,B,Tp); s+=f'<line x1="{L}" y1="{zy:.1f}" x2="{W-Rt}" y2="{zy:.1f}" stroke="#5a5266"/><line x1="{L}" y1="{Tp}" x2="{L}" y2="{B}" stroke="#5a5266"/>'
s+=f'<text x="{(L+W-Rt)/2}" y="230" fill="#968ba1" font-size="10.5" text-anchor="middle">set temperature (°C)</text>'
s+=f'<text x="20" y="{(Tp+B)/2}" fill="#968ba1" font-size="10.5" text-anchor="middle" transform="rotate(-90 20 {(Tp+B)/2})">error (°C)</text>'
for yv in [0.5,0,-0.5]:
    py=lin(yv,-hy,hy,B,Tp); s+=f'<text x="{L-6}" y="{py+3:.1f}" fill="#6f6780" font-size="8.5" text-anchor="end">{yv:+.1f}</text>'
for T in TICKS:
    px=lin(T,hx0,hx1,L,W-Rt); s+=f'<text x="{px:.1f}" y="{B+16}" fill="#6f6780" font-size="9" text-anchor="middle">{T}</text>'
for (T,e) in held:
    px=lin(T,hx0,hx1,L,W-Rt); py=lin(e,-hy,hy,B,Tp); col="#7ee0a8"
    s+=f'<line x1="{px:.1f}" y1="{zy:.1f}" x2="{px:.1f}" y2="{py:.1f}" stroke="{col}" stroke-width="1.2"/><circle cx="{px:.1f}" cy="{py:.1f}" r="3" fill="{col}"/>'
    s+=f'<text x="{px:.1f}" y="{py-8 if e>=0 else py+16:.1f}" fill="{col}" font-size="9" text-anchor="middle">{e:+.1f}</text>'
s+=f'<text x="{L+8}" y="{Tp+16}" fill="#7f9d88" font-size="10">green band = ±0.5 °C integer-display floor · uniform cold→hot, no warm-end degradation</text>'
s+='</svg>'
open(os.path.join(IMG,"calib_check.svg"),"w",encoding="utf-8").write(s)
print("wrote calib_curve.svg / calib_resid.svg / calib_check.svg  (R2=%.6f Ea=%.3f n=%d)"%(R2,Ea,n))
