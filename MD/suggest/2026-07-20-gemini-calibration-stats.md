# 校準統計網友提問:真數字 + Gemini 把關(我驗證後全對)(2026-07-20)

一位以「化學儀器校準統計建模」為本業的網友問:怎麼驗證校準、哪種 fit、R²、per-point %RSD?
完整 Gemini 回覆 `2026-07-20-gemini-calibration-stats-reply.txt`。**做法:先從實際資料算真統計(`tools/thermo_rom/calib_stats.py`),再用 Gemini 把關統計描述 + 回覆術語,我逐項驗證。**

## 算出來的真數字(新 $2002/$2001 ROM)
- **A) %RSD**:25.0°C ×5 都是 66268 → **per-point %RSD = 0**(模擬器確定性,零 run-to-run 噪音)。
- **B) Fit 診斷**(ln count vs 1/T,52 點):單 Arrhenius **R²=0.99954**(Ea=0.574eV≈Eg/2);+1/T² 的 R²=0.99970。
  殘差符號冷→暖 = `----...++++...----++...` = **runs、非隨機 = Lack-of-Fit(結構偏差,不是噪音)**。
- **C) 做法**:非參數 fit,而是逐度密集量測 + (1/T, ln count) 分段線性內插到 0.1°C(表通過每點)。
- **D) High/low(held-out check standard)**:3.5→3.5、12.5→12.6、22.5→22.7、27.5→27.5、37.5→37.0、47.5→46.5
  → **±0.2°C 到 ~30°C**、40°C 約 −0.5、50°C 約 −1.0(暖端**失 sensitivity/斜率**→量化主導,不是噪音)。

## Gemini 把關 + 我的驗證(全對、無幻覺)
- %RSD=0 統計退化、量化≠散布 ✓;高 R²+系統殘差=**LOF** ✓;held-out=**check standard/ICV** ✓;暖端=失 sensitivity ✓。
- **⚠️ 別用 LOD/LOQ**(σ=0→LOD=0 無意義 + 溫度無 blank)✓;**別用 precision**(RSD=0),講 resolution / bias-trueness ✓。
- 推薦術語:Lack-of-Fit、check standard、sensitivity(=斜率)、working range。
- 回覆定位:先破題「這是模擬器軟體管線驗證、非物理」(接前一位的 circular),真硬體才 %RSD≠0、才需要他那套(replicate RSD / thermal-chamber high-low / 適當 fit)—— 邀請他對真硬體給意見。

關聯:前一位 NESdev 的裁判 [[../suggest/2026-07-20-gemini-nesdev-critique-referee]];[[build-our-own-no-vendor-refsim]]。
