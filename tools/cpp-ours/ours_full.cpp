// ours_full.cpp — the FULL AprVisual optimized engine ported to C++ (peak vs peak).
//
// Unlike ours.cpp (architecture core only), this replicates the ENTIRE hot path of the C# engine,
// INCLUDING the cache layout: a packed 16-byte NodeInfo struct (4 per cache line) with the inline
// channel payload as a union with the overflow Tlist indices, the cls1/cls2 fast-path dispatch +
// RecalcNodeFast, the B1 two-node pair path, the class-major range-prune in SetNodeState, the
// double-buffered settle, and the original-id-order (permutation) checksum. It loads the
// fully-optimized engine state exported by `AprVisual.etc --export-engine-full` (post-renumber +
// self-capture), so it reads byte-for-byte the same data the C# hot path reads and is validated
// BIT-EXACT against the C# ours run.
//
// Build: clang++ -O3 -std=c++17 ours_full.cpp -o ours_full
// Run:   ours_full <6502|6800|z80> <engine_full.txt> [hc] [warmup] [rounds]

#include <cstdio>
#include <cstdint>
#include <string>
#include <vector>
#include <unordered_map>
#include <algorithm>
#include <chrono>
#include <fstream>

enum { F_PULLUP=2, F_SETHIGH=4, F_SETLOW=8, F_PWR=16, F_GND=32, F_FC=64, F_CB=128 };

// Mirror of WireCore.NodeInfo — 16 bytes, 4 per 64B cache line. Inline payload and the overflow
// Tlist indices share one 12-byte union (mutually exclusive, gated by `inl`).
struct NI {
    uint8_t flags, inl, c1c2cnt, gndpwr;          // gndpwr: GndCount = low nibble, PwrCount = high nibble
    union { uint16_t pay[6]; struct { int32_t tc, tg, tp; } o; } u;
    inline int gnd() const { return gndpwr & 0x0F; }
    inline int pwr() const { return gndpwr >> 4; }
};

static int N=0, NGND=0, NPWR=0, NOP=0, TLLEN=0, rA=0, rS=0, rB=0, PERMLEN=0;
static std::vector<NI> ni;                        // packed hot per-node record
static std::vector<uint8_t> states, ipl;          // separate hot/warm arrays (as C# NodeStates / IsPureLogic)
static std::vector<int> conn, tlgates;            // cold (floating tie-break / SetNodeState writeback)
static std::vector<uint16_t> TL;                  // TransistorList
static std::vector<int> perm;
static std::unordered_map<std::string,int> names;
static uint8_t LUT[256];

// settle + group scratch
static std::vector<int> cur, nxt; static std::vector<uint8_t> curHash, nxtHash; static int nxtCount=0;
static std::vector<int> groupBuf; static std::vector<uint8_t> inGroup; static int gf;

static void buildLUT(){
    for(int i=0;i<256;i++){ int f=i; if((f&F_FC)&&(f&F_GND)&&(f&F_PWR)){f&=~F_GND;f&=~F_PWR;}
        LUT[i]= (f&F_GND)?0:(f&F_PWR)?1:(f&F_SETHIGH)?1:(f&F_SETLOW)?0:(f&F_PULLUP)?1:0; }
}

// ---- AddNodeToGroup / GetNodeValue (Group.cs) ----
static inline void addNodeToGroup(int seed){
    if(!inGroup[seed]){ inGroup[seed]=1; groupBuf.push_back(seed); curHash[seed]=0; gf|=ni[seed].flags; }
    size_t ri=0;
    while(ri<groupBuf.size()){
        int nn=groupBuf[ri++]; const NI& s=ni[nn];
        if(s.inl){
            const uint16_t* pay=s.u.pay; int n2=s.c1c2cnt<<1;
            for(int k=0;k<n2;k+=2) if(states[pay[k]]){ int o=pay[k+1]; if(!inGroup[o]){ inGroup[o]=1; groupBuf.push_back(o); curHash[o]=0; gf|=ni[o].flags; } }
            int gndEnd=n2+s.gnd(); for(int k=n2;k<gndEnd;k++){ if(states[pay[k]]){ gf|=F_GND; break; } }
            int pwrEnd=gndEnd+s.pwr(); for(int k=gndEnd;k<pwrEnd;k++){ if(states[pay[k]]){ gf|=F_PWR; break; } }
        } else {
            if(s.u.o.tc){ const uint16_t* p=&TL[s.u.o.tc]; while(*p){ if(states[*p]){ int o=*(p+1); if(!inGroup[o]){ inGroup[o]=1; groupBuf.push_back(o); curHash[o]=0; gf|=ni[o].flags; } } p+=2; } }
            if(s.u.o.tg){ const uint16_t* p=&TL[s.u.o.tg]; while(*p){ if(states[*p]){ gf|=F_GND; break; } p++; } }
            if(s.u.o.tp){ const uint16_t* p=&TL[s.u.o.tp]; while(*p){ if(states[*p]){ gf|=F_PWR; break; } p++; } }
        }
    }
}
static inline uint8_t getNodeValue(){
    if(gf!=0) return LUT[gf];
    int maxConn=-1; uint8_t maxState=0;
    for(size_t i=0;i<groupBuf.size();++i){ int x=groupBuf[i]; if(conn[x]>maxConn){ maxState=states[x]; maxConn=conn[x]; } }
    return maxState;
}
static inline uint8_t computeNodeGroup(int nn){
    for(size_t i=0;i<groupBuf.size();++i) inGroup[groupBuf[i]]=0;
    groupBuf.clear(); gf=0; addNodeToGroup(nn); return getNodeValue();
}

// ---- SetNodeState (range-prune dual-pair) ----
static inline void setNodeState(int nn, uint8_t v){
    if(states[nn]==v) return;
    states[nn]=v;
    int tg=tlgates[nn]; if(!tg) return;
    const uint16_t* p=&TL[tg];
    if(v==0){
        while(true){ int c1=p[0]; if(!c1) break; int c2=p[1];
            if(c1>=rS && !nxtHash[c1]){ nxt[nxtCount++]=c1; nxtHash[c1]=1; }
            if(c2>=rS && !nxtHash[c2]){ nxt[nxtCount++]=c2; nxtHash[c2]=1; }
            int c1b=p[2]; if(!c1b) break; int c2b=p[3];
            if(c1b>=rS && !nxtHash[c1b]){ nxt[nxtCount++]=c1b; nxtHash[c1b]=1; }
            if(c2b>=rS && !nxtHash[c2b]){ nxt[nxtCount++]=c2b; nxtHash[c2b]=1; }
            p+=4; }
    } else {
        while(true){ int c1=p[0]; if(!c1) break; int c2=p[1];
            if((c1<rA||c1>=rB||states[c1]!=states[c2]) && !nxtHash[c1]){ nxt[nxtCount++]=c1; nxtHash[c1]=1; }
            int c1b=p[2]; if(!c1b) break; int c2b=p[3];
            if((c1b<rA||c1b>=rB||states[c1b]!=states[c2b]) && !nxtHash[c1b]){ nxt[nxtCount++]=c1b; nxtHash[c1b]=1; }
            p+=4; }
    }
}
static inline void enqueue(int nn){ if(nn==NPWR||nn==NGND) return; if(!nxtHash[nn]){ nxt[nxtCount++]=nn; nxtHash[nn]=1; } }

// ---- RecalcNodeFast (FastPath.cs) ----
static inline void recalcNodeFast(int nn){
    const NI& s=ni[nn]; int f=s.flags;
    if(s.inl){
        const uint16_t* pay=s.u.pay; int gs=s.c1c2cnt<<1; int ge=gs+s.gnd();
        int anyG=0; for(int k=gs;k<ge;k++) anyG|=states[pay[k]]; f|=anyG<<5;
        int pe=ge+s.pwr(); int anyP=0; for(int k=ge;k<pe;k++) anyP|=states[pay[k]]; f|=anyP<<4;
    } else {
        if(s.u.o.tg){ const uint16_t* p=&TL[s.u.o.tg]; int any=0; while(*p) any|=states[*p++]; f|=any<<5; }
        if(s.u.o.tp){ const uint16_t* p=&TL[s.u.o.tp]; int any=0; while(*p) any|=states[*p++]; f|=any<<4; }
    }
    if(f!=0) setNodeState(nn, LUT[f]);
}

// ---- RecalcNode (cls dispatch + B1 pair path; no callbacks for a bare CPU) ----
static void recalcNode(int nn){
    int cls=ipl[nn];
    if(cls==1){ recalcNodeFast(nn); return; }
    if(cls==2){
        const NI& s=ni[nn];
        if(s.inl){
            const uint16_t* pay=s.u.pay; int n2=s.c1c2cnt<<1;
            for(int k=0;k<n2;k+=2){
                if(states[pay[k]]){
                    int o=pay[k+1];
                    if(o==nn) goto fb;
                    for(int k2=k+2;k2<n2;k2+=2) if(states[pay[k2]]) goto fb;
                    { const NI& os=ni[o];
                      if(!os.inl || (os.flags&(F_CB|F_FC))) goto fb;
                      const uint16_t* opay=os.u.pay; int on2=os.c1c2cnt<<1;
                      for(int k2=0;k2<on2;k2+=2) if(states[opay[k2]] && opay[k2+1]!=nn) goto fb;
                      curHash[o]=0;
                      int f=s.flags|os.flags; int anyG=0,anyP=0;
                      int sGe=n2+s.gnd(), sPe=sGe+s.pwr();
                      for(int j=n2;j<sGe;j++) anyG|=states[pay[j]];
                      for(int j=sGe;j<sPe;j++) anyP|=states[pay[j]];
                      int oGe=on2+os.gnd(), oPe=oGe+os.pwr();
                      for(int j=on2;j<oGe;j++) anyG|=states[opay[j]];
                      for(int j=oGe;j<oPe;j++) anyP|=states[opay[j]];
                      f|=(anyG<<5)|(anyP<<4);
                      uint8_t v = f!=0 ? LUT[f] : (conn[o]>conn[nn]?states[o]:states[nn]);
                      setNodeState(nn,v); setNodeState(o,v); return; }
                }
            }
        } else {
            const uint16_t* p=&TL[s.u.o.tc]; while(*p){ if(states[*p]) goto fb; p+=2; }
        }
        recalcNodeFast(nn); return;
    }
fb:
    { uint8_t v=computeNodeGroup(nn); for(size_t i=0;i<groupBuf.size();++i) setNodeState(groupBuf[i], v); }
}
static void processQueue(){
    while(nxtCount){ cur.swap(nxt); curHash.swap(nxtHash); int c=nxtCount; nxtCount=0;
        for(int i=0;i<c;i++){ int nn=cur[i]; if(curHash[nn]){ recalcNode(nn); curHash[nn]=0; } } }
}

// ---- bus / driving (NOP sled), mirror RawCpu ----
static int ab[16], db[8]; static std::vector<uint8_t> mem; static std::string chip;
static int pRw,pClk0,pClk,pPhi1,pPhi2,pDbe,pRd,pMreq,pWr;
static inline int id(const std::string& s){ auto it=names.find(s); return it==names.end()?-1:it->second; }
static inline int readBus(const int* a,int len){ int v=0; for(int i=0;i<len;i++){ int nn=a[i]; if(nn>=0&&states[nn]) v|=1<<i; } return v; }
static inline void drive(int nn,bool hi){ if(nn<0) return; uint8_t nf=hi?(uint8_t)((ni[nn].flags&~F_SETLOW)|F_SETHIGH):(uint8_t)((ni[nn].flags&~F_SETHIGH)|F_SETLOW); if(nf!=ni[nn].flags){ ni[nn].flags=nf; enqueue(nn); processQueue(); } }
static inline void writeDataBus(int value){ for(int i=0;i<8;i++){ int nn=db[i]; if(nn<0) continue; uint8_t nf=((value>>i)&1)?(uint8_t)((ni[nn].flags&~F_SETLOW)|F_SETHIGH):(uint8_t)((ni[nn].flags&~F_SETHIGH)|F_SETLOW); if(nf!=ni[nn].flags){ ni[nn].flags=nf; enqueue(nn); } } processQueue(); }
static inline void busRead(){ if(pRw<0||states[pRw]) writeDataBus(mem[readBus(ab,16)]); }
static inline void busWrite(){ if(pRw>=0&&!states[pRw]) mem[readBus(ab,16)]=(uint8_t)readBus(db,8); }
static void halfStep(){
    if(chip=="6800"){ if(states[pPhi2]){ drive(pPhi2,false); drive(pDbe,false); drive(pPhi1,true); busRead(); } else { drive(pPhi1,true); drive(pPhi1,false); drive(pPhi2,true); drive(pDbe,true); busWrite(); } }
    else if(chip=="z80"){ drive(pClk, states[pClk]==0); if(!states[pRd]&&!states[pMreq]) writeDataBus(mem[readBus(ab,16)]); else writeDataBus(0xFF); if(pWr>=0&&!states[pWr]) mem[readBus(ab,16)]=(uint8_t)readBus(db,8); }
    else { if(states[pClk0]){ drive(pClk0,false); busRead(); } else { drive(pClk0,true); busWrite(); } }
}
static uint64_t checksum(){ uint64_t h=14695981039346656037ULL; for(int i=0;i<N;i++){ int idx=(PERMLEN&&i<PERMLEN)?perm[i]:i; h^=states[idx]; h*=1099511628211ULL; } return h; }

static void load(const std::string& path){
    std::ifstream f(path); if(!f){ fprintf(stderr,"cannot open %s\n",path.c_str()); exit(2); }
    std::string t;
    f>>t>>NGND>>NPWR>>N>>NOP>>TLLEN>>rA>>rS>>rB>>PERMLEN;
    ni.assign(N, NI{}); states.assign(N,0); ipl.assign(N,0); conn.assign(N,0); tlgates.assign(N,0);
    inGroup.assign(N,0); curHash.assign(N,0); nxtHash.assign(N,0); cur.assign(N+8,0); nxt.assign(N+8,0); groupBuf.reserve(128);
    f>>t>>N;
    for(int nn=0;nn<N;nn++){ int fl,in,cc,gc,pc,p0,p1,p2,p3,p4,p5,tc,tg,tp,tlg,ip,cn,st;
        f>>fl>>in>>cc>>gc>>pc>>p0>>p1>>p2>>p3>>p4>>p5>>tc>>tg>>tp>>tlg>>ip>>cn>>st;
        NI& s=ni[nn]; s.flags=(uint8_t)fl; s.inl=(uint8_t)in; s.c1c2cnt=(uint8_t)cc; s.gndpwr=(uint8_t)((gc&0xF)|((pc&0xF)<<4));
        if(in){ s.u.pay[0]=p0;s.u.pay[1]=p1;s.u.pay[2]=p2;s.u.pay[3]=p3;s.u.pay[4]=p4;s.u.pay[5]=p5; }
        else { s.u.o.tc=tc; s.u.o.tg=tg; s.u.o.tp=tp; }
        ipl[nn]=(uint8_t)ip; conn[nn]=cn; tlgates[nn]=tlg; states[nn]=(uint8_t)st; }
    f>>t>>TLLEN; TL.assign(TLLEN+8,0); for(int i=0;i<TLLEN;i++){ int x; f>>x; TL[i]=(uint16_t)x; }
    f>>t>>PERMLEN; perm.assign(PERMLEN,0); for(int i=0;i<PERMLEN;i++){ int x; f>>x; perm[i]=x; }
    int nameCount; f>>t>>nameCount; for(int i=0;i<nameCount;i++){ std::string nm; int v; f>>nm>>v; names[nm]=v; }
}

int main(int argc,char**argv){
    if(argc<3){ fprintf(stderr,"usage: ours_full <6502|6800|z80> <engine_full.txt> [hc] [warmup] [rounds]\n"); return 2; }
    chip=argv[1];
    long HC=argc>3?atol(argv[3]):1000000; long WARM=argc>4?atol(argv[4]):50000; int ROUNDS=argc>5?atoi(argv[5]):5;
    load(argv[2]); buildLUT();
    for(int i=0;i<16;i++) ab[i]=id("ab"+std::to_string(i));
    for(int i=0;i<8;i++)  db[i]=id("db"+std::to_string(i));
    pRw=id("rw");pClk0=id("clk0");pClk=id("clk");pPhi1=id("phi1");pPhi2=id("phi2");pDbe=id("dbe");pRd=id("_rd");pMreq=id("_mreq");pWr=id("_wr");
    mem.assign(65536,(uint8_t)NOP);
    // Entering the hot path: free what the timed loop never touches (the name->id map) — mirrors the
    // C# ClearPostLoadBuildState / ReleaseBenchResidualState hygiene. perm is kept (checksum uses it).
    { std::unordered_map<std::string,int> empty; names.swap(empty); }
    uint64_t ckLoad=checksum();
    int ab0=readBus(ab,16);
    for(long i=0;i<WARM;i++) halfStep();
    int ab1=readBus(ab,16);
    std::vector<double> rates;
    for(int r=0;r<ROUNDS;r++){ auto t0=std::chrono::high_resolution_clock::now(); for(long k=0;k<HC;k++) halfStep(); auto t1=std::chrono::high_resolution_clock::now(); rates.push_back(HC/std::chrono::duration<double>(t1-t0).count()); }
    std::sort(rates.begin(),rates.end());
    printf("# C++ OURS-FULL (packed NodeInfo + cls fast-path + B1 pair + range-prune) — chip %s  [sizeof(NI)=%zu]\n",chip.c_str(),sizeof(NI));
    printf("#   nodes: %d   range A=%d S=%d B=%d\n", N, rA, rS, rB);
    printf("#   post-load checksum 0x%016llX  (must match the C# --export-engine-full post-init checksum)\n",(unsigned long long)ckLoad);
    printf("#   AB sample: post-reset=0x%04X  post-warmup=0x%04X  %s\n", ab0, ab1, ab0!=ab1?"(advancing)":"(NOT advancing)");
    for(size_t r=0;r<rates.size();++r) printf("#   round %zu: %.0f hc/s\n", r+1, rates[r]);
    printf("#   median: %.0f hc/s   best: %.0f   (%ld hc/round, warmup %ld)\n", rates[rates.size()/2], rates.back(), HC, WARM);
    printf("#   final checksum 0x%016llX  (compare to C# --cpu-bench, same warmup+rounds*hc)\n",(unsigned long long)checksum());
    return (ab0!=ab1)?0:1;
}
