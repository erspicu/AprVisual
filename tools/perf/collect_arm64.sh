#!/bin/bash
# ARM collect @ current OC (3.2GHz). Per-version temp; ABORT if temp > 60C after any version.
# Outputs build_json-compatible CSVs: boost_arm.csv / sizes_arm.csv / temp_arm.csv
export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$PATH; export DOTNET_ROLL_FORWARD=Major
cd ~/aprvisual
ORIG=$(git rev-parse HEAD)
SRC=$(find ~ -name full_palette.nes 2>/dev/null | head -1); cp "$SRC" /tmp/fp.nes
ROM=/tmp/fp.nes; SD=$HOME/aprvisual/run/data/system-def; MAXT=60
O=/tmp/armout; mkdir -p $O
~/pi_freq_lock.sh
ITEMS=(
 "2026.05.30|benchmark-2026.05.30" "2026.05.31|a80dab4" "2026.06.01|9cc0dc7"
 "2026.06.03|benchmark-2026.06.03" "2026.06.04|benchmark-2026.06.04" "2026.06.05|benchmark-2026.06.05"
 "2026.06.07|benchmark-2026.06.07" "2026.06.07b|benchmark-2026.06.07b" "2026.06.08|benchmark-2026.06.08"
 "2026.06.09|benchmark-2026.06.09" "2026.06.09b|benchmark-2026.06.09b" "2026.06.09c|benchmark-2026.06.09c"
 "2026.06.09d|benchmark-2026.06.09d" "2026.06.09e|benchmark-2026.06.09e" "2026.06.11|benchmark-2026.06.11"
 "2026.06.12|benchmark-2026.06.12" "2026.06.18|benchmark-2026.06.18" "2026.06.19|benchmark-2026.06.19"
 "2026.06.20|benchmark-2026.06.20"
)
# build the IL-hash helper once (platform-neutral fingerprint of the WireCore engine)
ILHASH=$(find ~/aprvisual/tools/perf/ilhash/bin/Release -name ilhash.dll 2>/dev/null | head -1)
if [ -z "$ILHASH" ]; then
  dotnet build ~/aprvisual/tools/perf/ilhash/ilhash.csproj -c Release -v quiet >/dev/null 2>&1
  ILHASH=$(find ~/aprvisual/tools/perf/ilhash/bin/Release -name ilhash.dll 2>/dev/null | head -1)
fi
echo "Version,BoostTop3Avg,BoostMax,Samples,Checksum" > $O/boost_arm.csv
echo "Version,IL,Native" > $O/sizes_arm.csv
echo "Version,EngineILHash" > $O/ilhash_arm.csv
echo "Version,Temp" > $O/temp_arm.csv
for item in "${ITEMS[@]}"; do
  IFS='|' read lbl ref <<< "$item"
  git checkout -q "$ref" 2>/dev/null
  rm -rf src/AprVisual.S1/bin src/AprVisual.S1/obj
  dotnet build src/AprVisual.S1/AprVisual.S1.csproj -c Release -p:PlatformTarget=arm64 -p:Platforms=arm64 -v quiet >/dev/null 2>&1
  DLL=$(find src/AprVisual.S1/bin/Release -name AprVisual.S1.dll 2>/dev/null | head -1)
  if [ -z "$DLL" ]; then echo "$lbl BUILD_FAIL"; continue; fi
  rates=""; ck=""
  for i in 1 2 3 4 5; do
    out=$(dotnet "$DLL" --benchmark "$ROM" --bench-hc 400000 --extra-ram --system-def-dir "$SD" 2>/dev/null)
    r=$(echo "$out" | grep -oE 'rate:[^(]*' | grep -oE '[0-9,]+' | head -1 | tr -d ','); rates="$rates $r"
    [ -z "$ck" ] && ck=$(echo "$out" | grep -oE '0x[0-9A-F]{16}' | head -1)
  done
  srt=$(echo $rates | tr ' ' '\n' | grep -E '[0-9]' | sort -rn)
  best3=$(echo "$srt" | head -3 | awk '{s+=$1;n++}END{if(n)print int(s/n)}')
  mx=$(echo "$srt" | head -1)
  samp=$(echo $rates | tr -s ' ' | sed 's/^ //;s/ /\//g')
  sz=$(DOTNET_JitDisasmSummary=1 DOTNET_TieredCompilation=0 dotnet "$DLL" --benchmark "$ROM" --bench-hc 3000 --extra-ram --system-def-dir "$SD" 2>&1 | grep -oE 'WireCore:ProcessQueue(Interp)?\(\) \[FullOpts, IL size=[0-9]+, code size=[0-9]+\]' | head -1)
  il=$(echo "$sz" | grep -oE 'IL size=[0-9]+' | grep -oE '[0-9]+'); nat=$(echo "$sz" | grep -oE 'code size=[0-9]+' | grep -oE '[0-9]+')
  ilh=""; [ -n "$ILHASH" ] && ilh=$(dotnet "$ILHASH" "$DLL" 2>/dev/null | tr -d '[:space:]')
  temp=$(vcgencmd measure_temp | grep -oE '[0-9.]+' | head -1)
  echo "$lbl,$best3,$mx,$samp,$ck" >> $O/boost_arm.csv
  echo "$lbl,$il,$nat" >> $O/sizes_arm.csv
  [ -n "$ilh" ] && echo "$lbl,$ilh" >> $O/ilhash_arm.csv
  echo "$lbl,$temp" >> $O/temp_arm.csv
  echo "$lbl: best3=$best3 max=$mx ck=${ck:0:6} nat=$nat temp=${temp}C"
  if awk -v t="$temp" -v m="$MAXT" 'BEGIN{exit !(t>m)}'; then
    echo "!!! ABORT: temp ${temp}C > ${MAXT}C — stopping as instructed (partial data kept) !!!"; break
  fi
done
git checkout -q "$ORIG" 2>/dev/null
~/pi_freq_restore.sh
echo "DONE"
