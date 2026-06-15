// ours.cpp — a C++ port of AprVisual's OWN (optimized) event-driven algorithm, for the language
// axis at the "ours" level: how fast is OUR algorithm in C++ vs C#?
//
// It is the canonical hot path of the C# engine — SoA arrays, double-buffered event-driven settle,
// the 256-entry NMOS-priority group-resolution LUT, the floating largest-capacitance tie-break, and
// the P-1..P-4 event-count prunes (mask form) — driven by the same NOP sled. It deliberately OMITS
// the bit-exact-NEUTRAL micro-optimizations (class-major renumber/range-prune, the inline-payload
// node layout, the cls1/cls2 fast path, the B1 pair path, self-captured relayout): those are
// performance-only, so leaving them out keeps the result identical and the port tractable. It is
// therefore "our algorithm's ARCHITECTURE in C++", not a peak-vs-peak micro-op race.
//
// Loads the engine state exported by `AprVisual.etc --export-engine` (post-lower, identity ids =
// --no-renumber, post-init): per-node flags/state/pruneMask/connections + the lowered transistor
// list + the name→id map. Bit-exactness is checked against the C# `--cpu-bench --no-renumber` run
// (same FNV-1a NodeStates checksum).
//
// Build: clang++ -O3 -std=c++17 ours.cpp -o ours
// Run:   ours <6502|6800|z80> <engine.txt> [hc] [warmup] [rounds]

#include <cstdio>
#include <cstdint>
#include <string>
#include <vector>
#include <unordered_map>
#include <algorithm>
#include <chrono>
#include <fstream>

// NodeFlags (mirror WireCore.cs)
enum { F_STATE=1, F_PULLUP=2, F_SETHIGH=4, F_SETLOW=8, F_PWR=16, F_GND=32, F_FC=64, F_CB=128 };
enum { P_TURNON_UNSAFE=1, P_TURNOFF_SKIP=2 };

static int N=0, NGND=0, NPWR=0, NOP=0;
static std::vector<uint8_t> flags, states, prune;       // per node
static std::vector<int> conn;                            // capacitance proxy (exported)
static std::vector<std::vector<int>> gatesC1c2;          // node gates these transistors -> (c1,c2) pairs (SetNodeState enqueue)
static std::vector<std::vector<int>> chan;               // node is a channel end -> (gate,other) pairs (group walk)
static std::vector<std::vector<int>> gndGates, pwrGates; // ON-gate -> Gnd / Pwr contribution
static std::unordered_map<std::string,int> names;
static uint8_t flagsToState[256];

// double-buffered settle queues (mirror RecalcList/Next + RecalcHash/Next)
static std::vector<int> cur, nxt;
static std::vector<uint8_t> curHash, nxtHash;
// group scratch
static std::vector<int> groupBuf;
static std::vector<uint8_t> inGroup;
static int gf;

static void buildLUT(){
    for(int i=0;i<256;i++){
        int f=i;
        if((f&F_FC) && (f&F_GND) && (f&F_PWR)){ f&=~F_GND; f&=~F_PWR; }
        uint8_t v=0;
        if(f&F_GND) v=0; else if(f&F_PWR) v=1; else if(f&F_SETHIGH) v=1;
        else if(f&F_SETLOW) v=0; else if(f&F_PULLUP) v=1; else if(f&F_STATE) v=1; else v=0;
        flagsToState[i]=v;
    }
}

// ---- group resolution (ComputeNodeGroup + GetNodeValue) ----
static uint8_t computeNodeGroup(int nn){
    for(size_t i=0;i<groupBuf.size();++i) inGroup[groupBuf[i]]=0;
    groupBuf.clear(); gf=0;
    inGroup[nn]=1; groupBuf.push_back(nn); curHash[nn]=0; gf|=flags[nn];
    size_t ri=0;
    while(ri<groupBuf.size()){
        int m=groupBuf[ri++];
        const std::vector<int>& c=chan[m];
        for(size_t k=0;k<c.size();k+=2){ int gate=c[k]; if(states[gate]){ int o=c[k+1]; if(!inGroup[o]){ inGroup[o]=1; groupBuf.push_back(o); curHash[o]=0; gf|=flags[o]; } } }
        const std::vector<int>& gg=gndGates[m]; for(size_t k=0;k<gg.size();++k){ if(states[gg[k]]){ gf|=F_GND; break; } }
        const std::vector<int>& pg=pwrGates[m]; for(size_t k=0;k<pg.size();++k){ if(states[pg[k]]){ gf|=F_PWR; break; } }
    }
    if(gf!=0) return flagsToState[gf];
    int maxConn=-1; uint8_t maxState=0;
    for(size_t i=0;i<groupBuf.size();++i){ int x=groupBuf[i]; if(conn[x]>maxConn){ maxState=states[x]; maxConn=conn[x]; } }
    return maxState;
}

// ---- SetNodeState (enqueue with the P-1..P-4 prune, mask form) ----
static int nxtCount=0;
static inline void setNodeState(int nn, uint8_t v){
    if(states[nn]==v) return;
    states[nn]=v;
    const std::vector<int>& g=gatesC1c2[nn];
    if(v==0){
        for(size_t k=0;k<g.size();k+=2){
            int c1=g[k], c2=g[k+1];
            if(!(prune[c1]&P_TURNOFF_SKIP) && !nxtHash[c1]){ nxt[nxtCount++]=c1; nxtHash[c1]=1; }
            if(!(prune[c2]&P_TURNOFF_SKIP) && !nxtHash[c2]){ nxt[nxtCount++]=c2; nxtHash[c2]=1; }
        }
    } else {
        for(size_t k=0;k<g.size();k+=2){
            int c1=g[k], c2=g[k+1];
            if(((prune[c1]&P_TURNON_UNSAFE) || states[c1]!=states[c2]) && !nxtHash[c1]){ nxt[nxtCount++]=c1; nxtHash[c1]=1; }
        }
    }
}
static inline void enqueue(int nn){ if(nn==NPWR||nn==NGND) return; if(!nxtHash[nn]){ nxt[nxtCount++]=nn; nxtHash[nn]=1; } }

static void recalcNode(int nn){
    uint8_t v=computeNodeGroup(nn);
    for(size_t i=0;i<groupBuf.size();++i) setNodeState(groupBuf[i], v);
}
static void processQueue(){
    while(nxtCount!=0){
        cur.swap(nxt); curHash.swap(nxtHash);
        int count=nxtCount; nxtCount=0;
        for(int i=0;i<count;i++){ int nn=cur[i]; if(curHash[nn]){ recalcNode(nn); curHash[nn]=0; } }
    }
}

// ---- bus / driving (NOP sled), mirror WireCore.RawCpu ----
static int ab[16], db[8];
static std::vector<uint8_t> mem;
static std::string chip;
static int pRw,pClk0,pClk,pPhi1,pPhi2,pDbe,pRd,pMreq,pWr;

static inline int id(const std::string& s){ auto it=names.find(s); return it==names.end()?-1:it->second; }
static inline int readBus(const int* a,int len){ int v=0; for(int i=0;i<len;i++){ int nn=a[i]; if(nn>=0 && states[nn]) v|=1<<i; } return v; }
static inline void drive(int nn,bool hi){
    if(nn<0) return;
    uint8_t nf = hi ? (uint8_t)((flags[nn]&~F_SETLOW)|F_SETHIGH) : (uint8_t)((flags[nn]&~F_SETHIGH)|F_SETLOW);
    if(nf!=flags[nn]){ flags[nn]=nf; enqueue(nn); processQueue(); }
}
static inline void writeDataBus(int value){
    for(int i=0;i<8;i++){ int nn=db[i]; if(nn<0) continue; uint8_t nf=((value>>i)&1)? (uint8_t)((flags[nn]&~F_SETLOW)|F_SETHIGH) : (uint8_t)((flags[nn]&~F_SETHIGH)|F_SETLOW); if(nf!=flags[nn]){ flags[nn]=nf; enqueue(nn); } }
    processQueue();
}
static inline void busRead(){ if(pRw<0||states[pRw]) writeDataBus(mem[readBus(ab,16)]); }
static inline void busWrite(){ if(pRw>=0 && !states[pRw]) mem[readBus(ab,16)]=(uint8_t)readBus(db,8); }

static void halfStep(){
    if(chip=="6800"){
        if(states[pPhi2]){ drive(pPhi2,false); drive(pDbe,false); drive(pPhi1,true); busRead(); }
        else { drive(pPhi1,true); drive(pPhi1,false); drive(pPhi2,true); drive(pDbe,true); busWrite(); }
    } else if(chip=="z80"){
        drive(pClk, states[pClk]==0);
        if(!states[pRd] && !states[pMreq]) writeDataBus(mem[readBus(ab,16)]); else writeDataBus(0xFF);
        if(pWr>=0 && !states[pWr]) mem[readBus(ab,16)]=(uint8_t)readBus(db,8);
    } else {
        if(states[pClk0]){ drive(pClk0,false); busRead(); } else { drive(pClk0,true); busWrite(); }
    }
}

static uint64_t checksum(){ uint64_t h=14695981039346656037ULL; for(int i=0;i<N;i++){ h^=states[i]; h*=1099511628211ULL; } return h; }

static void load(const std::string& path){
    std::ifstream f(path);
    if(!f){ fprintf(stderr,"cannot open %s\n",path.c_str()); exit(2); }
    std::string tok; int transCount=0;
    f>>tok>>NGND>>NPWR>>N>>transCount>>NOP;            // META
    flags.assign(N,0); states.assign(N,0); prune.assign(N,0); conn.assign(N,0);
    gatesC1c2.assign(N,{}); chan.assign(N,{}); gndGates.assign(N,{}); pwrGates.assign(N,{});
    inGroup.assign(N,0); curHash.assign(N,0); nxtHash.assign(N,0); cur.assign(N+8,0); nxt.assign(N+8,0); groupBuf.reserve(64);
    f>>tok>>N;                                          // NODES <n>  (same n)
    for(int i=0;i<N;i++){ int fl,st,pm,cn; f>>fl>>st>>pm>>cn; flags[i]=(uint8_t)fl; states[i]=(uint8_t)st; prune[i]=(uint8_t)pm; conn[i]=cn; }
    f>>tok>>transCount;                                 // TRANS <count>
    for(int i=0;i<transCount;i++){
        int g,c1,c2; f>>g>>c1>>c2;
        gatesC1c2[g].push_back(c1); gatesC1c2[g].push_back(c2);   // for SetNodeState enqueue
        if(g!=NGND){                                              // gate tied to GND can never turn on -> no channel
            // endpoint c1 (other=c2)
            if(c2==NGND) gndGates[c1].push_back(g); else if(c2==NPWR) pwrGates[c1].push_back(g); else { chan[c1].push_back(g); chan[c1].push_back(c2); }
            // endpoint c2 (other=c1)
            if(c1==NGND) gndGates[c2].push_back(g); else if(c1==NPWR) pwrGates[c2].push_back(g); else { chan[c2].push_back(g); chan[c2].push_back(c1); }
        }
    }
    int nameCount; f>>tok>>nameCount;                   // NAMES <count>
    for(int i=0;i<nameCount;i++){ std::string nm; int v; f>>nm>>v; names[nm]=v; }
}

int main(int argc,char**argv){
    if(argc<3){ fprintf(stderr,"usage: ours <6502|6800|z80> <engine.txt> [hc] [warmup] [rounds]\n"); return 2; }
    chip=argv[1];
    long HC = argc>3? atol(argv[3]) : 1000000;
    long WARM = argc>4? atol(argv[4]) : 50000;
    int ROUNDS = argc>5? atoi(argv[5]) : 5;
    load(argv[2]); buildLUT();
    for(int i=0;i<16;i++) ab[i]=id("ab"+std::to_string(i));
    for(int i=0;i<8;i++)  db[i]=id("db"+std::to_string(i));
    pRw=id("rw"); pClk0=id("clk0"); pClk=id("clk"); pPhi1=id("phi1"); pPhi2=id("phi2"); pDbe=id("dbe");
    pRd=id("_rd"); pMreq=id("_mreq"); pWr=id("_wr");
    mem.assign(65536,(uint8_t)NOP);

    uint64_t ckLoad=checksum();                          // must equal the export's post-init checksum
    int ab0=readBus(ab,16);
    for(long i=0;i<WARM;i++) halfStep();
    int ab1=readBus(ab,16);
    std::vector<double> rates;
    for(int r=0;r<ROUNDS;r++){
        auto t0=std::chrono::high_resolution_clock::now();
        for(long k=0;k<HC;k++) halfStep();
        auto t1=std::chrono::high_resolution_clock::now();
        rates.push_back(HC/std::chrono::duration<double>(t1-t0).count());
    }
    std::sort(rates.begin(),rates.end());
    printf("# C++ OURS (event-driven + LUT group + P-1..P-4 prune; no renumber/fastpath/inline) — chip %s\n",chip.c_str());
    printf("#   nodes: %d   transistors: -   NOP: 0x%02X\n", N, NOP);
    printf("#   post-load checksum 0x%016llX  (must match the C# --export-engine post-init checksum)\n",(unsigned long long)ckLoad);
    printf("#   AB sample: post-reset=0x%04X  post-warmup=0x%04X  %s\n", ab0, ab1, ab0!=ab1?"(advancing)":"(NOT advancing)");
    for(size_t r=0;r<rates.size();++r) printf("#   round %zu: %.0f hc/s\n", r+1, rates[r]);
    printf("#   median: %.0f hc/s   best: %.0f   (%ld hc/round, warmup %ld)\n", rates[rates.size()/2], rates.back(), HC, WARM);
    printf("#   final checksum 0x%016llX  (compare to C# --cpu-bench --no-renumber, same warmup+rounds*hc)\n",(unsigned long long)checksum());
    return (ab0!=ab1)?0:1;
}
