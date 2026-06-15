// naive.cpp — a faithful C/C++ port of the ORIGINAL visual6502 algorithm (chipsim.js recursive
// group-walk), for the language-vs-algorithm comparison. It is the C++ sibling of
// src/AprVisual.etc/Sim/NaiveSim.cs and tools/visual6502-node (the JavaScript original):
//   JS naive  ->  C# naive  ->  C++ naive  : same algorithm, three languages (pure language axis)
//   C# naive  ->  AprVisual (C# event-driven + prunes)         : pure algorithm axis
//
// Loads the netlist exported by `AprVisual.etc --export-netlist` (identical nodes / transistors /
// pin map to the C# naive — no .js parser here, so the data is provably the same), then drives the
// chip with an infinite NOP sled at the pin boundary and times the half-cycle rate.
//
// Build:  clang++ -O3 -std=c++17 naive.cpp -o naive
// Run:    naive <chip 6502|6800|z80> <netlist.txt> [hc] [warmup] [rounds]

#include <cstdio>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include <unordered_map>
#include <algorithm>
#include <chrono>
#include <fstream>

struct Node { bool state=false, pullup=false, pulldown=false; std::vector<int> gates, c1c2s; bool exists=false; };
struct Trans { bool on=false; int gate, c1, c2; };

static int N=0, NGND=0, NPWR=0, NOP=0;
static std::vector<Node> nodes;
static std::vector<Trans> trans;
static std::unordered_map<std::string,int> names;

// ---- chipsim.js core (recursive group-walk), verbatim algorithm ----
static std::vector<int> group;
static std::vector<int> recalclist;
static std::vector<char> recalcHash;

static inline bool isHigh(int nn){ return nn>=0 && nodes[nn].exists && nodes[nn].state; }

static void addNodeToGroup(int i){
    // linear membership (chipsim.js group.indexOf)
    for(size_t k=0;k<group.size();++k) if(group[k]==i) return;
    group.push_back(i);
    if(i==NGND || i==NPWR) return;
    const std::vector<int>& c = nodes[i].c1c2s;
    for(size_t k=0;k<c.size();++k){
        const Trans& t = trans[c[k]];
        if(!t.on) continue;
        int other = (t.c1==i) ? t.c2 : t.c1;
        addNodeToGroup(other);
    }
}
static inline void getNodeGroup(int i){ group.clear(); addNodeToGroup(i); }

static bool getNodeValue(){
    for(size_t k=0;k<group.size();++k) if(group[k]==NGND) return false;
    for(size_t k=0;k<group.size();++k) if(group[k]==NPWR) return true;
    for(size_t k=0;k<group.size();++k){
        const Node& n = nodes[group[k]];
        if(n.pullup) return true;
        if(n.pulldown) return false;
        if(n.state) return true;
    }
    return false;
}

static inline void addRecalc(int nn){
    if(nn==NGND || nn==NPWR) return;
    if(recalcHash[nn]) return;
    recalclist.push_back(nn);
    recalcHash[nn]=1;
}
static void recalcNode(int node){
    if(node==NGND || node==NPWR) return;
    getNodeGroup(node);
    bool newState = getNodeValue();
    for(size_t gi=0; gi<group.size(); ++gi){
        Node& n = nodes[group[gi]];
        if(n.state==newState) continue;
        n.state=newState;
        for(size_t k=0;k<n.gates.size();++k){
            Trans& t = trans[n.gates[k]];
            if(n.state){ if(!t.on){ t.on=true; addRecalc(t.c1); } }
            else       { if(t.on){ t.on=false; addRecalc(t.c1); addRecalc(t.c2); } }
        }
    }
}
static void recalcNodeList(std::vector<int> list){
    recalclist.clear();
    std::fill(recalcHash.begin(), recalcHash.end(), 0);
    for(int j=0;j<100;++j){               // loop limiter (chipsim.js)
        if(list.empty()) return;
        for(size_t i=0;i<list.size();++i) recalcNode(list[i]);
        list.swap(recalclist);
        recalclist.clear();
        std::fill(recalcHash.begin(), recalcHash.end(), 0);
    }
}

static std::vector<int> one(1);
static inline void setHigh(int nn){ nodes[nn].pullup=true; nodes[nn].pulldown=false; one.assign(1,nn); recalcNodeList(one); }
static inline void setLow (int nn){ nodes[nn].pullup=false;nodes[nn].pulldown=true;  one.assign(1,nn); recalcNodeList(one); }
static inline int  id(const std::string& s){ auto it=names.find(s); return it==names.end()? -1 : it->second; }
static inline void setHighN(const std::string& s){ int nn=id(s); if(nn>=0) setHigh(nn); }
static inline void setLowN (const std::string& s){ int nn=id(s); if(nn>=0) setLow(nn); }

static std::vector<int> allNodes(){
    std::vector<int> l; l.reserve(N);
    for(int i=0;i<N;++i) if(i!=NGND && i!=NPWR && nodes[i].exists) l.push_back(i);
    return l;
}

// ---- bus / driving (macros.js + support.js), NOP sled ----
static int ab[16], db[8];
static std::vector<uint8_t> mem;            // 64K, NOP-filled
static std::string chip;

static inline int readBus(const int* arr, int len){ int v=0; for(int i=0;i<len;++i){ int nn=arr[i]; if(nn>=0 && isHigh(nn)) v|=1<<i; } return v; }
static void writeDataBus(int value){
    std::vector<int> rc;
    for(int i=0;i<8;++i){ int nn=db[i]; if(nn<0) continue; if((value&1)==0){nodes[nn].pulldown=true;nodes[nn].pullup=false;} else {nodes[nn].pulldown=false;nodes[nn].pullup=true;} rc.push_back(nn); value>>=1; }
    recalcNodeList(rc);
}
static int pRw,pClk0,pClk,pPhi1,pPhi2,pDbe,pRd,pMreq,pWr;
static void resolvePins(){
    for(int i=0;i<16;++i) ab[i]=id("ab"+std::to_string(i));
    for(int i=0;i<8;++i)  db[i]=id("db"+std::to_string(i));
    pRw=id("rw"); pClk0=id("clk0"); pClk=id("clk"); pPhi1=id("phi1"); pPhi2=id("phi2"); pDbe=id("dbe");
    pRd=id("_rd"); pMreq=id("_mreq"); pWr=id("_wr");
}

static void halfStep(){
    if(chip=="6800"){
        if(isHigh(pPhi2)){ setLow(pPhi2); setLow(pDbe); setHigh(pPhi1); if(pRw<0||isHigh(pRw)) writeDataBus(mem[readBus(ab,16)]); }
        else { setHigh(pPhi1); setLow(pPhi1); setHigh(pPhi2); setHigh(pDbe); if(pRw>=0 && !isHigh(pRw)) mem[readBus(ab,16)]=(uint8_t)readBus(db,8); }
    } else if(chip=="z80"){
        if(isHigh(pClk)) setLow(pClk); else setHigh(pClk);
        if(!isHigh(pRd) && !isHigh(pMreq)) writeDataBus(mem[readBus(ab,16)]);
        else writeDataBus(0xFF);
        if(pWr>=0 && !isHigh(pWr)) mem[readBus(ab,16)]=(uint8_t)readBus(db,8);
    } else { // 6502
        if(isHigh(pClk0)){ setLow(pClk0); if(pRw<0||isHigh(pRw)) writeDataBus(mem[readBus(ab,16)]); }
        else { setHigh(pClk0); if(pRw>=0 && !isHigh(pRw)) mem[readBus(ab,16)]=(uint8_t)readBus(db,8); }
    }
}

static void initChip(){
    for(int i=0;i<N;++i) if(nodes[i].exists) nodes[i].state=false;
    nodes[NGND].state=false; nodes[NPWR].state=true;
    for(size_t i=0;i<trans.size();++i) trans[i].on=false;
    if(chip=="6800"){
        setLowN("reset"); setHighN("phi1"); setLowN("phi2"); setLowN("dbe");
        setHighN("dbe"); setLowN("tsc"); setHighN("halt"); setHighN("irq"); setHighN("nmi");
        recalcNodeList(allNodes());
        for(int i=0;i<8;++i){ setLow(pPhi1); setHigh(pPhi2); setHigh(pDbe); setLow(pPhi2); setLow(pDbe); setHigh(pPhi1); }
        setHighN("reset");
        for(int i=0;i<6;++i) halfStep();
    } else if(chip=="z80"){
        setLowN("_reset"); setHighN("clk"); setHighN("_busrq"); setHighN("_int"); setHighN("_nmi"); setHighN("_wait");
        recalcNodeList(allNodes());
        for(int i=0;i<31;++i) halfStep();
        setHighN("_reset");
    } else { // 6502
        setLowN("res"); setLowN("clk0"); setHighN("rdy"); setLowN("so"); setHighN("irq"); setHighN("nmi");
        recalcNodeList(allNodes());
        for(int i=0;i<8;++i){ setHigh(pClk0); setLow(pClk0); }
        setHighN("res");
        for(int i=0;i<18;++i) halfStep();
    }
}

static void load(const std::string& path){
    std::ifstream f(path);
    if(!f){ fprintf(stderr,"cannot open %s\n", path.c_str()); exit(2); }
    std::string tok; int transCount=0;
    f>>tok>>NGND>>NPWR>>N>>transCount>>NOP;        // META
    nodes.assign(N, Node());
    f>>tok; int puCount; f>>puCount;               // PULLUPS <count>
    for(int i=0;i<puCount;++i){ int x; f>>x; nodes[x].pullup=true; }
    f>>tok>>transCount;                            // TRANS <count>
    trans.reserve(transCount);
    for(int i=0;i<transCount;++i){ Trans t; f>>t.gate>>t.c1>>t.c2; int idx=(int)trans.size(); trans.push_back(t);
        nodes[t.gate].gates.push_back(idx); nodes[t.c1].c1c2s.push_back(idx); nodes[t.c2].c1c2s.push_back(idx); }
    int nameCount; f>>tok>>nameCount;              // NAMES <count>
    for(int i=0;i<nameCount;++i){ std::string nm; int v; f>>nm>>v; names[nm]=v; }
    for(int i=0;i<N;++i) nodes[i].exists=true;     // every slot is a real node (NaiveSim allocates dense)
    recalcHash.assign(N,0);
}

int main(int argc,char**argv){
    if(argc<3){ fprintf(stderr,"usage: naive <6502|6800|z80> <netlist.txt> [hc] [warmup] [rounds]\n"); return 2; }
    chip=argv[1];
    long HC = argc>3? atol(argv[3]) : 1000000;
    long WARM = argc>4? atol(argv[4]) : 50000;
    int ROUNDS = argc>5? atoi(argv[5]) : 5;
    load(argv[2]);
    resolvePins();
    mem.assign(65536, (uint8_t)NOP);

    initChip();
    int ab0=readBus(ab,16);
    for(long i=0;i<WARM;++i) halfStep();
    int ab1=readBus(ab,16);

    std::vector<double> rates;
    for(int r=0;r<ROUNDS;++r){
        auto t0=std::chrono::high_resolution_clock::now();
        for(long k=0;k<HC;++k) halfStep();
        auto t1=std::chrono::high_resolution_clock::now();
        double secs=std::chrono::duration<double>(t1-t0).count();
        rates.push_back(HC/secs);
    }
    std::sort(rates.begin(),rates.end());
    double median=rates[rates.size()/2], best=rates.back();
    printf("# naive C++ (original visual6502 algorithm, recursive group-walk) — chip %s\n", chip.c_str());
    printf("#   nodes: %d   transistors: %zu   NOP: 0x%02X\n", N, trans.size(), NOP);
    printf("#   AB sample: post-reset=0x%04X  post-warmup=0x%04X  %s\n", ab0, ab1, ab0!=ab1?"(advancing)":"(NOT advancing)");
    for(size_t r=0;r<rates.size();++r) printf("#   round %zu: %.0f hc/s\n", r+1, rates[r]);
    printf("#   median: %.0f hc/s   best: %.0f   (%ld hc/round, warmup %ld)\n", median, best, HC, WARM);
    return (ab0!=ab1)?0:1;
}
