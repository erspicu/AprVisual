# **閘級網表模擬器效能最佳化研究：無須中間碼與動態生成之純演算法與資料結構重構策略**

現代數位積體電路設計在進入實體佈局與繞線（Place and Route）之前，極度仰賴閘級網表模擬（Gate-Level Netlist Simulation）來驗證電路的邏輯功能正確性、時序特性以及功耗預估。然而，隨著先進製程技術的演進，系統單晶片（SoC）的規模呈現指數級增長，單一晶片內包含數千萬甚至數十億個邏輯閘已成為常態 1。在這樣的背景下，模擬器處理的節點與互連線數量極其龐大，導致模擬過程中面臨極高的運算開銷與記憶體存取瓶頸。為了加速模擬，產業界與學術界的高效能模擬器架構（例如 Verilator 或傳統的商業編譯型模擬器）通常會依賴中間表示（Intermediate Representation, IR）的建構，並利用動態程式碼生成（Code Generation, CodeGen）技術將網表直接編譯為 C++ 程式碼或底層機器碼 3。這類做法能深度利用底層編譯器（如 GCC 或 LLVM）的暫存器配置、指令排程與迴圈展開能力來極大化執行速度。  
然而，建構 IR 與 CodeGen 引擎不僅開發成本高昂、系統架構極度複雜，且在每次網表變更時都需要經歷漫長的重新編譯時間（Compilation Time），這對於需要頻繁除錯的設計階段而言是一大痛點 3。更重要的是，對於使用 C\# 等受控語言（Managed Language）開發的模擬器而言，直接生成機器碼的難度較高，且容易與通用語言執行平台（CLR）的垃圾回收（Garbage Collection, GC）機制產生衝突。因此，在「不進入 IR 與 CodeGen 階段」的前提下，純粹針對軟體架構、資料結構與核心演算法進行深度的重構與最佳化，成為節省計算量與提升模擬吞吐量的關鍵命題。  
本研究報告將從四個核心維度深入剖析此一議題：首先探討運算圖的靜態優化與網表結構重構，透過演算法消除邏輯層面的冗餘運算；其次分析模擬核心排程演算法的革新，探討如何消除傳統事件驅動模型中的佇列開銷；第三部分將引入位元切片（Bit-Slicing）與資料層級平行化技術，最大化中央處理器（CPU）算術邏輯單元的利用率；最後，針對 C\# 語言的獨特運行環境，提出高階記憶體佈局與底層效能最佳化策略。綜合這些純演算法層面的改進，將能建構出極致高效的 C\# 閘級網表模擬器。

## **1\. 靜態圖論優化與網表結構重構 (Static Graph and Netlist Optimization)**

在啟動任何模擬迴圈之前，原始輸入的閘級網表拓樸結構往往包含了巨量的冗餘邏輯與次佳的連線配置。未經優化的邏輯閘網路不僅增加了每一次模擬週期所需計算的節點總數，更因較深的邏輯層級（Logic Depth）加劇了事件傳播的延遲與記憶體存取次數。因此，前端邏輯優化（Front-end Logic Optimization）是降低總體運算量、從根本上節省計算開銷的首要步驟 7。透過引入邏輯合成（Logic Synthesis）領域的演算法，模擬器能在初始化階段對資料結構進行降維打擊。

### **1.1 及-反相器圖 (And-Inverter Graph, AIG) 之建構與正規化**

原始網表中通常包含各式各樣的異質邏輯閘（例如 XOR、MUX、AOI、OAI 以及多輸入的 NAND/NOR 閘）。這種異質性在模擬器中會迫使執行緒頻繁使用 switch-case 或虛擬函式呼叫（Virtual Function Call）來決定每個節點的求值行為，進而引發大量不可預測的分支指令（Branch Misprediction），嚴重破壞 CPU 管線（Pipeline）的執行效率。將所有組合邏輯統一轉換為「及-反相器圖」（And-Inverter Graph, AIG）是一種極具突破性的標準化策略 10。  
在 AIG 的資料結構中，整個邏輯網路被簡化為僅由雙輸入 AND 閘與邊緣上的反相標記（Inverter Marker）所組成 10。由於反相操作可以直接依附在節點之間的連接邊（Edge）上，以指標最低有效位元（LSB）的翻轉來表示，因此反相器本身不再佔用任何獨立的節點或記憶體空間 10。這種均一化的圖結構不僅使後續的記憶體佈局更為緊湊，也讓求值迴圈變得極度單一化，有利於 CPU 的指令預取與向量化。

### **1.2 結構雜湊 (Structural Hashing) 與冗餘運算消除**

在建構與轉換 AIG 的過程中，最關鍵的減少計算量技術為「結構雜湊」（Structural Hashing, 或稱 Strashing） 13。結構雜湊的核心機制在於利用空間換取時間，透過消除邏輯網路中的等效子圖，從根本上減少需要模擬的閘數量。

* **單層結構雜湊 (One-level Strashing)：** 當模擬器在記憶體中建立一個新的 AND 節點時，會先擷取其兩個輸入來源的指標（包含反相屬性），將這兩個指標經過排序後作為鍵值（Key），在全域的雜湊表（Hash Table）中查找是否已存在結構完全相同的節點 14。若雜湊表命中（Cache Hit），則放棄建立新節點，直接回傳既有節點的參考。這項技術透過動態規劃的思維，實現了子運算式的最大化共用（Sub-expression Sharing），確保在整個運算圖中，沒有任何兩個節點具有相同的輸入驅動源 14。  
* **雙層結構雜湊 (Two-level Strashing)：** 進一步窮舉並正規化兩層邏輯深度的 AND-INV 組合。當發現邏輯等效但結構不同（例如因結合律或分配律導致的差異）的子圖時，強制對應到預先定義的標準型（Canonical Form）。這通常能在單層雜湊的基礎上，額外減少 5% 至 10% 的節點總數 14。

| 優化技術類型 | 演算法複雜度 | 節點縮減效益預估 | 模擬期記憶體頻寬節省 | 核心優勢 |
| :---- | :---- | :---- | :---- | :---- |
| **原始網表解析** | **![][image1]** | 基準值 (0%) | 基準值 | 無額外初始化成本 |
| **AIG 轉換 \+ 反相器邊緣化** | **![][image1]** | 約 15% \- 25% | 中度提升 | 消除 NOT 閘，指令均一化 |
| **單層結構雜湊** | **![][image1]** (均攤) | 約 20% \- 40% | 高度提升 | 消除結構相同的冗餘邏輯 |
| **雙層結構雜湊 / AIG 重寫** | **![][image2]** | 約 30% \- 50% | 極高提升 | 識別代數等效子圖，極小化狀態空間 |

透過結構雜湊，模擬器在執行前就能將需模擬的節點數量壓制到理論最低極限，這直接且等比例地減少了後續每個模擬週期的計算量。此外，引入如 Berkeley ABC 工具中廣泛使用的 AIG 重寫（AIG Rewriting）與代數重構演算法，能進一步利用切割列舉（Cut Enumeration）尋找功能等效但節點數更少的子圖進行替換，大幅壓縮整體模擬狀態空間 12。

### **1.3 查找表映射 (Technology/LUT Mapping) 的降維演算法**

雖然將網表轉換為 AIG 標準化了運算邏輯，但其代價是邏輯深度（Logic Depth）顯著增加。過深的邏輯層級在事件驅動模擬中會導致事件在不同的時間步（Time Steps）中頻繁傳遞，增加了佇列操作的次數。為了解決此問題，可以將 FPGA 邏輯合成中極為關鍵的「查找表映射」（Technology Mapping to K-LUTs）演算法應用於模擬器的前置優化中 10。  
在不進入 CodeGen 的情況下，模擬器可以透過割集枚舉（Cut Enumeration）演算法，在記憶體中動態將多個連續的微小 AIG 節點「打包」成一個虛擬的 ![][image3]\-輸入查找表（通常 ![][image4] 或 ![][image5]） 26。一個 ![][image3]\-LUT 能夠實現任何 ![][image3] 個輸入變數的布林函數。在 C\# 的執行期，一個 6-LUT 的行為與真值表可以被完美且緊湊地編碼為一個 64 位元的無號整數（ulong）。  
其求值（Evaluation）演算法將原先需要遍歷 ![][image5] 個至 ![][image6] 個 AIG 內部節點的複雜分支與記憶體跳躍，簡化為一次單純的位元平移與遮罩運算：  
![][image7]  
在此公式中，InputVector 是將 ![][image3] 個輸入信號的當前狀態組合成的整數索引。這種映射演算法不僅顯著降低了邏輯深度（從而減少事件傳遞與層級跳躍次數），更將計算一個複雜邏輯錐（Logic Cone）的記憶體存取次數從 ![][image8] 降低至 ![][image9] 24。這對節省計算量與改善 CPU 快取表現具有決定性的貢獻。

### **1.4 巨集閘集結與邏輯錐分割 (Macro-Gate Clustering and Logic Cone Partitioning)**

為了在軟體架構層次上進一步降低排程開銷，可實作「巨集閘」（Macro-Gate）分群演算法 28。該演算法基於邏輯錐（Cone of Influence）的重疊性，將高度相依的邏輯閘聚合成一個單一的執行單元 29。在軟體模擬中，排程器管理一百萬個獨立邏輯閘的成本，遠大於管理一萬個包含一百個邏輯閘的巨集閘。  
巨集閘集結策略的主要演算法約束條件包括：

1. **無循環相依確保：** 集結後的巨集閘之間必須構成嚴格的有向無環圖（DAG）。這是為了保證在單一模擬週期內，每個巨集閘最多只需要被排程與評估一次，避免產生無窮迴圈或重複計算 29。  
2. **內部狀態封裝與記憶體局部性：** 巨集閘內部的連線（Internal Nets）狀態無需寫回全域狀態陣列中，可以直接在 CPU 的暫存器或 L1 快取中作為區域變數消耗掉。這極大地降低了主記憶體的讀寫頻寬壓力，並減少了全域記憶體同步的成本 32。  
3. **負載平衡（Load Balancing）：** 透過寬度與高度的限制演算法（例如從底層向上的貪婪填裝演算法），確保每個巨集閘內部的運算量大致相等，這不僅有利於單執行緒的迴圈展開，也為未來擴展至多執行緒平行模擬打下穩固基礎 30。

透過此種圖論級別的最佳化，模擬器在運作時的管理粒度被大幅提升。邏輯網路的邊界變得清晰，內部的高頻運算被隱藏在巨集閘內，顯著降低了事件的觸發頻率與系統排程負擔。

## **2\. 模擬核心排程演算法之革新 (Innovations in Simulation Engine Algorithms)**

在排除了靜態圖論優化後，模擬器最核心的計算量瓶頸在於「何時」以及「如何」去評估每一個節點。這牽涉到模擬引擎的排程演算法。傳統的閘級模擬器主要分為兩大派別：基於拓樸排序的無知型模擬（Oblivious / Cycle-Based Simulation）與基於事件觸發的事件驅動模擬（Event-Driven Simulation） 6。

### **2.1 傳統事件驅動的效能瓶頸與佇列開銷**

傳統的純事件驅動模擬器（如早期的軟體模擬架構或 IVerilog 的某些模式）依賴時間輪（Time Wheel）或優先權佇列（Priority Queue）來管理信號變更事件 6。當某個邏輯閘的輸出發生變化時，模擬器會將受該信號驅動的所有下游閘（Fanouts）實體化為「事件物件」，並插入到按時間排序的佇列中 39。  
這種機制的初衷是為了「只計算狀態發生改變的節點」，理論上在活躍度極低的電路中能帶來巨大的計算量節省 41。然而，實務上其維護成本極其高昂。將事件插入優先權佇列的時間複雜度為 ![][image10]（其中 ![][image11] 為佇列中的事件數），且頻繁的物件配置與指標跳躍會導致極為嚴重的快取未命中（Cache Thrashing）與記憶體碎片化。在許多現代 SoC 的模擬中，排程與維護佇列所消耗的 CPU 週期，甚至遠遠超過了評估布林邏輯本身的成本 38。當電路活躍度僅有 10% 時，佇列維護的額外計算量就足以抵銷其帶來的效能增益。

### **2.2 層級化事件驅動排程 (Levelized Event-Driven Scheduling)**

為了解決傳統佇列的效能黑洞，應實作一種混合了無知型模擬的拓樸排序優勢與事件驅動篩選能力的演算法，即「層級化事件驅動」（Levelized Event-Driven）演算法（例如學術界 LECSIM 模擬器所倡導的概念） 6。  
在此演算法架構下，模擬器在初始化階段會對整個網表進行廣度優先搜尋（BFS），將所有節點依據其依賴關係進行嚴格的「分層」（Levelization） 35。定義主輸入端（Primary Inputs）或暫存器輸出端的層級為 0，任何節點的層級 ![][image12] 必定大於其所有輸入節點的最高層級。  
在 C\# 實作中，我們徹底摒棄複雜且動態的優先權佇列，改以「活躍度位元遮罩陣列」（Active Bitmask Array）或極為輕量的固定大小循環陣列（Circular Array）來記錄每一層中需要被評估的節點 40。 當層級 ![][image13] 的某個節點輸出狀態改變時，模擬器不再生成事件物件並推入佇列，而是直接透過常數時間 ![][image9] 的記憶體寫入，將層級 ![][image12] 對應後繼節點的「活躍位元」設為 1 47。  
在執行模擬的時脈週期內，主迴圈僅需循序地從層級 ![][image14] 迭代至最高層級。在每一層中，利用現代硬體原生的位元掃描指令（如 C\# 中的 System.Numerics.BitOperations.TrailingZeroCount），能夠在單一 CPU 週期內快速跳躍並找出下一個值為 1 的活躍位元，僅對這些標記為活躍的節點進行求值 40。 這種演算法徹底移除了樹狀結構或堆積（Heap）的管理開銷，使得事件排程的成本逼近為零，同時又保留了過濾無效計算的能力 6。由於每一層的節點都是按順序評估的，這自然消除了傳統模擬中常見的「零延遲突波」（Zero-Delay Glitches）問題，進一步減少了重複求值的浪費 44。

### **2.3 活動力驅動與捷徑預測求值 (Activity-Driven Fastpath Predictors)**

對於包含大量暫存器反饋、時脈閘控（Clock Gating）以及低功耗控制機制的數位電路而言，網表通常在時間與空間上展現出極端的稀疏性（Sparsity） 50。若模擬演算法能提前預知某些邏輯區塊在當前週期內必定處於靜止狀態，便可將整塊運算邏輯略過，這對於節省計算量至關重要。  
此處可引入「捷徑預測」（Fastpath Predictor）演算法 53。在層級化模擬與巨集閘（Macro-Gate）架構的基礎上，為每一個巨集閘配置一個摘要指標（Summary Indicator）或控制位元。 在靜態分析階段，模擬器會識別出驅動該巨集閘的關鍵控制信號（例如 Enable 訊號、Reset 訊號或 Clock 訊號） 51。在動態執行期間，若在某個時脈週期初，演算法判定這些控制信號皆處於未觸發的靜止狀態，則演算法將直接觸發 Fastpath 分支，瞬間跳過該巨集閘內部數百甚至數千個子節點的評估 51。  
這種「活動力驅動」（Activity-Driven）的方法在處理大型 SoC 的重置序列、休眠模式（Sleep Mode）或等待記憶體回應的閒置週期時，能以極低的條件判斷成本，換取數量級別的運算量節省 2。透過結合層級化位元遮罩與 Fastpath 預測，模擬核心的排程效率將達到軟體演算法的極限。

## **3\. 位元切片與資料層級平行化技術 (Bit-Slicing and Data-Level Parallelism)**

在不涉及作業系統層級的多執行緒（Multi-threading）與圖形處理器（GPU）卸載的前提下，單一執行緒內部的演算法效能極致，取決於能否發揮 CPU 內部算術邏輯單元（ALU）的最大資料吞吐量。在傳統以位元組（Byte）或布林值（Boolean）為單位的模擬中，ALU 的利用率極端低落。「位元切片」（Bit-Slicing）技術是網表模擬演算法中最具破壞性創新且能成倍節省計算量的最佳化技術 59。

### **3.1 位元切片演算法的數學基礎與狀態轉置**

在標準的閘級模擬實作中，每個節點的當前狀態通常由一個單一的布林值（0 或 1）來表示。然而，現代微處理器的通用暫存器寬度為 64 位元，若是採用 AVX2 或 AVX-512 等單指令多資料流（SIMD）指令集，暫存器寬度更可高達 256 或 512 位元。若每次求值只對一個布林值進行 AND 或 OR 運算，實際上浪費了超過 98% 的資料路徑（Data Path）運算資源。  
位元切片演算法的本質是將資料的維度進行「轉置」（Transpose） 64：不以一個純量變數代表一個節點在單一時間點的單一狀態，而是以一個 64 位元無號整數（ulong）的各個位元，平行代表該節點在 64 個「獨立平行宇宙」下的狀態 59。這些平行的狀態維度可以有以下幾種應用場景：

1. **多重測試向量平行模擬（Multi-Pattern Simulation）：** 同時將 64 組獨立的輸入測試樣式（Test Patterns）封裝到各個位元中，一次模擬運算即得出 64 種情境的結果，整體計算量直接除以 64 60。  
2. **平行錯誤模擬（Concurrent Fault Simulation）：** 在測試覆蓋率分析中，需要模擬電路在發生各種卡死（Stuck-At）錯誤時的反應。位元切片允許位元 0 代表良好機器（Good Machine），而位元 1 至 63 則分別代表注入了不同錯誤的 63 台錯誤機器（Faulty Machines） 36。一次閘的求值即可同時傳遞 64 種電路組態的狀態。  
3. **時序展開（Time-Frame Unrolling）：** 對於純組合邏輯區塊，可以將連續 64 個時脈週期的輸入資料封裝於同一個變數中平行處理 37。

在實作層面，閘的求值函數從針對單一邏輯值的條件運算，轉化為針對暫存器長度變數的純粹逐位元邏輯操作（Bitwise Operations）。例如，當模擬一個 AND 閘 ![][image15] 時，在 C\# 中僅需一行代碼： ulong Z \= A & B; 這行單一的處理器指令在 1 個時脈週期內，實際上完成了 64 次獨立且平行的閘級邏輯運算 37。若是進一步利用.NET 的 System.Runtime.Intrinsics 命名空間引入硬體加速向量型別 Vector256\<ulong\>，更可將並發計算量壓縮達 256 倍。這項最佳化在演算法層面完全不改變原始的圖論拓樸結構，卻能藉由資料層級平行化，將純粹的布林邏輯求值成本攤銷至趨近於零 62。

### **3.2 多態邏輯 (X/Z) 的正交位元代數 (Orthogonal Bit Algebra)**

在真實的硬體晶片設計與閘級模擬中，訊號並非只有完美的 0 與 1，往往需要支援 4 態邏輯系統以反映真實物理行為：0（低電位）、1（高電位）、X（變數未定或驅動衝突）、Z（高阻抗或浮接） 38。  
在傳統的 C\# 實作中，開發者通常會使用枚舉（Enum）或位元組結構來表示這四種狀態，並在每次模擬求值時使用 switch-case 或巢狀 if-else 來決定輸出狀態。然而，這種做法在位元切片模型下會徹底失效，且密集的條件分支會引發極高比例的分支預測錯誤（Branch Prediction Penalty），摧毀程式的執行效能。  
為了解決 4 態邏輯與位元切片的相容性問題，演算法上的根本解決方案是採用「正交位元編碼」（Orthogonal Bit Coding）：使用兩個完全獨立的 64 位元整數陣列（例如 ValL 代表 Low bit，與 ValH 代表 High bit）來共同表述一個節點在 64 個平行狀態下的 4 態資訊 61。其編碼規則如下：

| 真實物理狀態 | 邏輯意義 | ValH 編碼 | ValL 編碼 |
| :---- | :---- | :---- | :---- |
| **0** | 強制低電位 | 0 | 1 |
| **1** | 強制高電位 | 1 | 0 |
| **X** | 未知或衝突 | 1 | 1 |
| **Z** | 高阻抗 (浮接) | 0 | 0 |

以此正交模型運作時，所有複雜多態邏輯閘的評估規則都可以用嚴謹的布林代數展開為純位元運算，徹底消滅條件判斷式。例如，一個支援 4 態邏輯的 AND 閘，其求值演算法將轉化為以下純粹的位元邏輯操作：

* Result.ValH \= A.ValH & B.ValH; （只有當 A 和 B 都可能為 1 時，結果才可能為 1）  
* Result.ValL \= A.ValL | B.ValL; （只要 A 或 B 任一為 0，結果必定為 0）

這種精妙的雙暫存器位元代數運算，不僅免除了所有的條件分支指令，保證了 CPU 執行管線的滿載運作，更完美地契合了前述的 64 位元平行切片特性。這意味著在支援完整 4 態時序違規檢查與不定值傳播的精確模擬前提下，我們依然能享有最高級別的運算量減免。

## **4\. 高效能記憶體佈局與 C\# 底層架構優化 (C\#-Specific Memory Layout and Optimizations)**

在當代電腦硬體架構中，運算核心的計算能力增長幅度遠遠超過主記憶體的存取速度，形成了著名的「記憶體牆」（Memory Wall）瓶頸。對於像網表模擬這樣資料密集（Data-Intensive）的演算法，若是資料結構的記憶體佈局不佳，即使上述邏輯圖與排程層面的計算量降至最低，處理器也會因無止境地等待主記憶體資料載入（Cache Misses）而耗盡效能 74。  
此外，在 C\# 語言的生態系中，通用語言執行平台（CLR）的受控堆積（Managed Heap）與垃圾回收（Garbage Collection, GC）機制，雖然提供了記憶體安全的便利性，卻也為講求極致效能的模擬引擎帶來了不可忽視的額外負擔 75。因此，針對 C\# 特性的資料佈局最佳化，是實現演算法效能落地的最後一哩路。

### **4.1 物件導向模型的徹底解構：從 AoS 轉向 SoA**

絕大多數初階模擬器在 C\# 中的直覺實作，會採用傳統的物件導向設計範式，建構陣列物件結構（Array of Structs/Objects, AoS）。例如，定義一個包含型別、輸入參考與狀態的類別：

C\#  
class Gate {  
    public int Type;  
    public Gate Input1;  
    public Gate Input2;  
    public ulong StateValH;  
    public ulong StateValL;  
}

在一個包含數千萬個邏輯閘的 SoC 網表中，這種設計會迫使 C\# 運行環境在受控堆積上產生數以千萬計的微小離散物件。這不僅會造成極其嚴重的記憶體碎片化（Memory Fragmentation），加重 GC 追蹤根節點的負擔；更致命的是，當排程器依照拓樸順序遍歷這些節點時，指標（References）會指向記憶體中隨機散落的位址，每次解指標（Dereferencing）幾乎都會引發緩存未命中（Cache Miss），導致 CPU 閒置等待數百個時脈週期 74。  
高效能演算法與資料結構必須重構為「結構陣列」（Struct of Arrays, SoA）佈局 77。整個模擬引擎不應存在任何獨立的「節點物件」，而是將所有節點的屬性拆解，展開成平行的連續記憶體基本型別陣列（Primitive Arrays）：

* int GateTypes  
* int Fanin1\_Indices  
* int Fanin2\_Indices  
* ulong State\_ValH  
* ulong State\_ValL

當實施前述的層級化模擬（Levelized Simulation）時，節點的整數索引（Index）配置應嚴格按照 BFS 拓樸排序的順序給定 29。如此一來，主模擬迴圈在層級內遍歷節點進行求值時，存取 State\_ValH 與 State\_ValL 陣列的記憶體位址將是嚴格單調遞增的線性存取（Sequential Access）。現代 CPU 的硬體預取器（Hardware Prefetcher）能極其完美地捕捉這種極具規律的線性存取模式，精準預測並提前將所需的資料區塊載入 L1/L2 快取中。在實務驗證中，這種 SoA 轉換能消弭高達 80% 的記憶體延遲等待週期，顯著提升演算法的實際運算吞吐量 79。

### **4.2 C\# 邊界檢查消除與零分配記憶體管理 (Zero-Allocation & Bounds-Checking Elimination)**

C\# 的安全機制預設會對所有的陣列存取進行邊界檢查（Bounds Checking），以防止緩衝區溢位。然而，在每秒需要進行數十億次節點求值的主模擬迴圈中，這些隱藏在陣列索引操作背後的比較指令與分支跳躍，積少成多便構成了極為龐大的額外計算量 82。  
為進一步壓榨效能極限，必須利用現代 C\# 7.0+ 與.NET Core 引入的高階記憶體存取特性來設計演算法：

1. **使用 Span\<T\>、Memory\<T\> 與安全指標繞過：** 透過將原始陣列封裝為 Span\<T\>，編譯器的即時編譯（JIT）優化器能在迴圈展開時進行安全分析，進而自動省略（Elide）迴圈內部的陣列邊界檢查 75。若面對極端效能瓶頸，甚至可利用 System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference 搭配 ref 運算，取得陣列首個元素的參考後進行指標偏移運算，徹底跳過所有的抽象安全檢查，達到與 C/C++ 指標運算同等的高效。  
2. **避免 GC 介入之 ArrayPool\<T\> 動態緩衝區：** 在巨集閘分群或層級佇列動態建構的演算法過程中，無可避免需要可變長度的容器。傳統頻繁地呼叫 new List\<T\>() 會導致舊陣列不斷被廢棄，最終觸發 GC 導致模擬過程產生難以預測的延遲暫停（Stop-The-World）。透過引入 System.Buffers.ArrayPool\<T\>.Shared 來租用（Rent）與歸還（Return）演算法所需的暫存緩衝區，能確保模擬核心在執行期的配置記憶體量達到「零分配」（Zero Allocation），徹底消除記憶體回收機制帶來的效能抖動與不必要的背景計算量浪費 75。

### **4.3 靜態迴圈展開與批次同構執行 (Static Loop Unrolling and Homogeneous Batching)**

在 C\# 的一般實作中，動態分派（Dynamic Dispatch，例如虛擬函式 virtual evaluate() 或是對型別的 switch(type) 敘述）會嚴重打斷執行緒管線的預測機制。既然我們的架構排除了將邏輯寫死為特定機器碼的 CodeGen 階段，我們必須在演算法設計上採用「批次分群」（Batch Grouping）策略來彌補指令調度的不足。  
在網表初始化階段進行拓樸排序後，演算法不應僅依照邏輯層級（Level）分群，更應在同一層級內，進一步依照「邏輯閘的類型」進行次分群。例如，在層級 ![][image12] 中，先連續排列所有的 AND 閘，接著排列所有的 OR 閘，最後排列所有的 XOR 閘。  
在主求值迴圈中，排程器不再對每一個節點詢問其類型，而是採取預先定義好邊界的特化連續迴圈：

C\#  
// Evaluate all AND gates in this homogenous batch  
for (int i \= startAnd; i \< endAnd; i++) {  
    ref ulong outH \= ref StateH\[i\];  
    ref ulong outL \= ref StateL\[i\];  
    // Orthogonal bit logic for AND without any conditionals  
    outH \= StateH\[Fanin1\[i\]\] & StateH\[Fanin2\[i\]\];  
    outL \= StateL\[Fanin1\[i\]\] | StateL\[Fanin2\[i\]\];  
}

// Evaluate all OR gates in the next batch  
for (int i \= startOr; i \< endOr; i++) {  
    //... specialized logic for OR...  
}

此種批次同構執行（Homogeneous Batch Execution）徹底消除了深層迴圈內部的型別判斷與分支跳躍。更具威力的是，這種純粹且無分支的線性記憶體存取與位元運算，能讓 C\# 的 JIT 編譯器輕易地辨識出運算模式，並自動對這些熱點迴圈實施向量化展開（Auto-Vectorization） 75。這是在完全不編寫專門的 CodeGen 或底層組合語言的情況下，純粹透過演算法設計來逼近硬體理論極限的最佳實踐。

## **5\. 總結與展望 (Conclusion)**

在現代超大規模積體電路的開發流程中，閘級網表模擬的效能直接決定了驗證週期的長短與產品上市的時程。儘管透過中介碼（IR）與動態編譯（CodeGen）生成特定設計專屬的執行檔，被廣泛認為是提升模擬器絕對效能的終極手段，但其衍生出的龐大編譯開銷、工具鏈複雜度以及記憶體爆發等問題，往往讓一般開發者與迭代頻繁的驗證情境望之卻步 3。  
本研究報告詳細論證並探討了多項深度演算法策略，強烈證明了在進入任何程式碼生成階段之前，透過純粹的圖論優化、排程演算法改良與資料結構的現代化設計，便能消弭網表模擬過程中絕大部分的冗餘運算，達成極致的「計算量節約」。  
總結而言，效能的提升源自於四個維度的層層堆疊與互相加成：

1. **空間與邏輯的濃縮：** 前端的及-反相器圖（AIG）標準化、結構雜湊（Strashing）以及查找表映射（LUT Mapping），在空間上直接移除了邏輯等效的冗餘節點與過深的拓樸路徑，從源頭限制了總體計算規模的上限 13。  
2. **時間與排程的濾除：** 中段透過層級化事件驅動（Levelized Event-Driven）架構與捷徑預測（Fastpath Predictors），徹底捨棄了傳統優先權佇列的高昂管理成本，精準地只針對狀態發生改變的有效路徑進行計算，解決了時間軸上的無效排程與休眠區塊的運算浪費 6。  
3. **算力與資料的平行化：** 後段引入的位元切片（Bit-Slicing）演算法與正交 4 態邏輯代數，將原本單調的布林值求值轉換為 64 維度的平行宇宙，將算術邏輯單元的吞吐量放大了數十倍，同時消滅了所有破壞管線效能的條件分支 60。  
4. **軟硬體的完美契合：** 最終在底層資料結構上，透過摒棄傳統物件導向思維，轉向結構陣列（SoA）佈局，並深度結合 C\# 特有的高效能記憶體視圖（Span\<T\>）、陣列池（ArrayPool\<T\>）與批次同構執行設計，使得軟體行為完美契合現代處理器的快取預取機制與 JIT 編譯器的向量化優化能力 75。

對於一套以 C\# 開發的閘級網表模擬器而言，徹底落實並整合上述所有的演算法與資料結構機制，將能使其效能表現超越許多未經深度優化的原生 C++ 模擬引擎。更重要的是，這種高度線性化、陣列連續化且無鎖（Lock-free）的底層資料結構，為未來進一步擴展至多執行緒平行計算或是 GPU 異質協同運算提供了最堅實且無縫接軌的基礎。透過軟體工程與計算機結構理論的深度結合，在不依賴任何編譯器魔術的純演算法領域，我們依然能將數位電路模擬的計算效率推升至一個嶄新的境界。

#### **引用的著作**

1. Fast and Scalable Gate-level Simulation in Massively Parallel Systems \- Fangming Liu, 檢索日期：6月 9, 2026， [https://fangmingliu.github.io/files/ICCAD23\_ZhouBi\_CR.pdf](https://fangmingliu.github.io/files/ICCAD23_ZhouBi_CR.pdf)  
2. Optimizing Gate-Level Simulation Performance Through Cloud-Based Distributed Computing \- IJFMR, 檢索日期：6月 9, 2026， [https://www.ijfmr.com/papers/2024/6/31349.pdf](https://www.ijfmr.com/papers/2024/6/31349.pdf)  
3. FAQ/Frequently Asked Questions — Verilator Devel 5.049 documentation \- Veripool, 檢索日期：6月 9, 2026， [https://veripool.org/guide/latest/faq.html](https://veripool.org/guide/latest/faq.html)  
4. Verilator Devel 5.049 documentation, 檢索日期：6月 9, 2026， [https://verilator.org/guide/latest/verilating.html](https://verilator.org/guide/latest/verilating.html)  
5. Tango: An Optimizing Compiler for Just-In-Time RTL Simulation, 檢索日期：6月 9, 2026， [https://past.date-conference.com/proceedings-archive/2020/pdf/0923.pdf](https://past.date-conference.com/proceedings-archive/2020/pdf/0923.pdf)  
6. Compiled Code in Distributed Logic Simulation \- McGill School Of Computer Science, 檢索日期：6月 9, 2026， [https://www.cs.mcgill.ca/\~carl/cidvs.pdf](https://www.cs.mcgill.ca/~carl/cidvs.pdf)  
7. RTLScout: Joint Agentic Code and Synthesis Optimization for Efficient Digital Circuits \- arXiv, 檢索日期：6月 9, 2026， [https://arxiv.org/html/2606.06530v1](https://arxiv.org/html/2606.06530v1)  
8. Evaluation and Optimization of Glitch Power Based on Signal Transition Behavior of Logic Gates \- IEEE Xplore, 檢索日期：6月 9, 2026， [https://ieeexplore.ieee.org/iel8/6287639/10820123/11300891.pdf](https://ieeexplore.ieee.org/iel8/6287639/10820123/11300891.pdf)  
9. Netlist Enabled Emulation Platform for Accelerated Gate Level Verification \- DVCon Proceedings, 檢索日期：6月 9, 2026， [https://dvcon-proceedings.org/wp-content/uploads/1B1\_DVCon\_India\_2023\_Final\_Paper\_6285.pdf](https://dvcon-proceedings.org/wp-content/uploads/1B1_DVCon_India_2023_Final_Paper_6285.pdf)  
10. Overview (1) Problems in Synthesis Terminology, 檢索日期：6月 9, 2026， [http://cc.ee.ntu.edu.tw/\~jhjiang/instruction/courses/fall14-lsv/lec01-abc\_4p.pdf](http://cc.ee.ntu.edu.tw/~jhjiang/instruction/courses/fall14-lsv/lec01-abc_4p.pdf)  
11. And-inverter graph \- Wikipedia, 檢索日期：6月 9, 2026， [https://en.wikipedia.org/wiki/And-inverter\_graph](https://en.wikipedia.org/wiki/And-inverter_graph)  
12. Introduction to Logic Synthesis with ABC, 檢索日期：6月 9, 2026， [https://cc.ee.ntu.edu.tw/\~jhjiang/instruction/courses/fall12-lsv/lec02-abc\_2p.pdf](https://cc.ee.ntu.edu.tw/~jhjiang/instruction/courses/fall12-lsv/lec02-abc_2p.pdf)  
13. BoolE: Exact Symbolic Reasoning via Boolean Equality Saturation \- IEEE Xplore, 檢索日期：6月 9, 2026， [https://ieeexplore.ieee.org/document/11132728/](https://ieeexplore.ieee.org/document/11132728/)  
14. FRAIGs: A Unifying Representation for Logic Synthesis and Verification \- UC Berkeley, 檢索日期：6月 9, 2026， [http://www-cad.eecs.berkeley.edu/\~alanmi/publications/2005/tech05\_fraigs.pdf](http://www-cad.eecs.berkeley.edu/~alanmi/publications/2005/tech05_fraigs.pdf)  
15. Control Logic Restructuring for Area Optimization \- People @EECS, 檢索日期：6月 9, 2026， [https://people.eecs.berkeley.edu/\~alanmi/publications/2022/iwls22\_reshape.pdf](https://people.eecs.berkeley.edu/~alanmi/publications/2022/iwls22_reshape.pdf)  
16. Modeling Relational Logic Circuits for And-Inverter Graph Convolutional Network \- arXiv, 檢索日期：6月 9, 2026， [https://arxiv.org/html/2508.11991v1](https://arxiv.org/html/2508.11991v1)  
17. ABC: A Simple System for Sequential Synthesis and Verification \- People @EECS, 檢索日期：6月 9, 2026， [http://people.eecs.berkeley.edu/\~alanmi/abc/abc.htm](http://people.eecs.berkeley.edu/~alanmi/abc/abc.htm)  
18. BoolE: Exact Symbolic Reasoning via Boolean Equality Saturation \- arXiv, 檢索日期：6月 9, 2026， [https://arxiv.org/html/2504.05577v2](https://arxiv.org/html/2504.05577v2)  
19. Local Two-Level And-Inverter Graph Minimization without Blowup \- JKU, 檢索日期：6月 9, 2026， [https://fmv.jku.at/papers/BrummayerBiere-MEMICS06.pdf](https://fmv.jku.at/papers/BrummayerBiere-MEMICS06.pdf)  
20. Fast Algebraic Rewriting Based on And-Inverter Graphs \- People @EECS, 檢索日期：6月 9, 2026， [https://people.eecs.berkeley.edu/\~alanmi/publications/2018/tcad18\_polyn.pdf](https://people.eecs.berkeley.edu/~alanmi/publications/2018/tcad18_polyn.pdf)  
21. AIG Rewriting \- ISS, 檢索日期：6月 9, 2026， [https://iss.oden.utexas.edu/?p=projects/galois/eda/aig\_rewriting](https://iss.oden.utexas.edu/?p=projects/galois/eda/aig_rewriting)  
22. AIG rewriting using 5-input cuts \- IEEE Xplore, 檢索日期：6月 9, 2026， [https://ieeexplore.ieee.org/document/6081434/](https://ieeexplore.ieee.org/document/6081434/)  
23. Global Delay Optimization using Structural Choices \- People @EECS, 檢索日期：6月 9, 2026， [https://people.eecs.berkeley.edu/\~alanmi/publications/2009/tech09\_speed.pdf](https://people.eecs.berkeley.edu/~alanmi/publications/2009/tech09_speed.pdf)  
24. A Parallelized Iterative Improvement Approach to Area Optimization for LUT-Based Technology Mapping, 檢索日期：6月 9, 2026， [https://www.csl.cornell.edu/\~zhiruz/pdfs/pimap-fpga2017.pdf](https://www.csl.cornell.edu/~zhiruz/pdfs/pimap-fpga2017.pdf)  
25. AIG transformations to improve LUT mapping for FPGAs \- UPCommons, 檢索日期：6月 9, 2026， [https://upcommons.upc.edu/bitstreams/837c2f9a-3efa-441b-b38b-1ee976294a67/download](https://upcommons.upc.edu/bitstreams/837c2f9a-3efa-441b-b38b-1ee976294a67/download)  
26. Improvements to Technology Mapping for LUT-Based FPGAs \- People @EECS, 檢索日期：6月 9, 2026， [https://people.eecs.berkeley.edu/\~alanmi/publications/2006/tcad06\_map.pdf](https://people.eecs.berkeley.edu/~alanmi/publications/2006/tcad06_map.pdf)  
27. Improvements to Technology Mapping for LUT-Based FPGAs \- University of Toronto, 檢索日期：6月 9, 2026， [https://janders.eecg.utoronto.ca/pdfs/area.pdf](https://janders.eecg.utoronto.ca/pdfs/area.pdf)  
28. Gate-Level Simulation with GPU Computing \- Andrew DeOrio, 檢索日期：6月 9, 2026， [https://andrewdeorio.com/assets/research/TODAES11GCS.pdf](https://andrewdeorio.com/assets/research/TODAES11GCS.pdf)  
29. Gate-level logic simulator using multiple processor architectures \- Google Patents, 檢索日期：6月 9, 2026， [https://patents.google.com/patent/US8738349B2/en](https://patents.google.com/patent/US8738349B2/en)  
30. GCS: High-Performance Gate-Level Simulation with GP-GPUs \- Andrew DeOrio, 檢索日期：6月 9, 2026， [https://andrewdeorio.com/assets/research/DATE09GCS.pdf](https://andrewdeorio.com/assets/research/DATE09GCS.pdf)  
31. Macro-gate balancing algorithm. Macro-gates are considered one at a... \- ResearchGate, 檢索日期：6月 9, 2026， [https://www.researchgate.net/figure/Macro-gate-balancing-algorithm-Macro-gates-are-considered-one-at-a-time-and-reshaped-to\_fig4\_220305728](https://www.researchgate.net/figure/Macro-gate-balancing-algorithm-Macro-gates-are-considered-one-at-a-time-and-reshaped-to_fig4_220305728)  
32. US 8738349 B2 \- Andrew DeOrio, 檢索日期：6月 9, 2026， [https://andrewdeorio.com/assets/research/US8738349.pdf](https://andrewdeorio.com/assets/research/US8738349.pdf)  
33. Debapriya Chatterjee, Andrew DeOrio and Valeria Bertacco The University of Michigan, Ann Arbor \- NVIDIA, 檢索日期：6月 9, 2026， [https://www.nvidia.com/content/GTC/posters/2010/A16-Gate-Level-Simulation-with-GP-GPUs.pdf](https://www.nvidia.com/content/GTC/posters/2010/A16-Gate-Level-Simulation-with-GP-GPUs.pdf)  
34. US5696942A \- Cycle-based event-driven simulator for hardware designs \- Google Patents, 檢索日期：6月 9, 2026， [https://patents.google.com/patent/US5696942A/en](https://patents.google.com/patent/US5696942A/en)  
35. The Counting Algorithm for simulation of million-gate designs \- Diva-Portal.org, 檢索日期：6月 9, 2026， [https://www.diva-portal.org/smash/get/diva2:19808/FULLTEXT01.pdf](https://www.diva-portal.org/smash/get/diva2:19808/FULLTEXT01.pdf)  
36. Extend IVerilog to Support Batch RTL Fault Simulation \- arXiv, 檢索日期：6月 9, 2026， [https://arxiv.org/html/2505.06687v1](https://arxiv.org/html/2505.06687v1)  
37. A PARALLEL ARCHITECTURE FOR NON-DETERMINISTIC DISCRETE EVENT SIMULATION \- Electronic Theses and Dissertations, 檢索日期：6月 9, 2026， [https://etda.libraries.psu.edu/files/final\_submissions/4014](https://etda.libraries.psu.edu/files/final_submissions/4014)  
38. The Verilog Language, 檢索日期：6月 9, 2026， [http://www.cs.columbia.edu/\~sedwards/classes/2005/languages-summer/verilog.9up.pdf](http://www.cs.columbia.edu/~sedwards/classes/2005/languages-summer/verilog.9up.pdf)  
39. Chapter 8 The Inversion Algorithm, 檢索日期：6月 9, 2026， [https://cs.baylor.edu/\~maurer/aida/desauto/chapter8.pdf](https://cs.baylor.edu/~maurer/aida/desauto/chapter8.pdf)  
40. lecsim: a levelized event driven compiled logic simulator, 檢索日期：6月 9, 2026， [https://cs.baylor.edu/\~maurer/aida/tech-reports/da20\_89.pdf](https://cs.baylor.edu/~maurer/aida/tech-reports/da20_89.pdf)  
41. Event-Driven Gate-Level Simulation with GP-GPUs \- Andrew DeOrio, 檢索日期：6月 9, 2026， [https://andrewdeorio.com/assets/research/DAC09Event.pdf](https://andrewdeorio.com/assets/research/DAC09Event.pdf)  
42. Logic Simulation Engines: Languages, Algorithms, Simulators, 檢索日期：6月 9, 2026， [http://www.engr.newpaltz.edu/\~bai/CSE45493/Sim\_Engines\_Class.pdf](http://www.engr.newpaltz.edu/~bai/CSE45493/Sim_Engines_Class.pdf)  
43. Parendi: Thousand-Way Parallel RTL Simulation \- arXiv, 檢索日期：6月 9, 2026， [https://arxiv.org/html/2403.04714v1](https://arxiv.org/html/2403.04714v1)  
44. Fast and Coherent Simulation with Zero Delay Elements | IEEE Journals & Magazine, 檢索日期：6月 9, 2026， [https://ieeexplore.ieee.org/document/1270249/](https://ieeexplore.ieee.org/document/1270249/)  
45. Branch Predictor Design for Low Resource Architectures, 檢索日期：6月 9, 2026， [http://20.198.91.3:8080/jspui/bitstream/123456789/1072/1/PhD%20thesis%20%28Information%20Technology%29%20Moumita%20Das.pdf](http://20.198.91.3:8080/jspui/bitstream/123456789/1072/1/PhD%20thesis%20%28Information%20Technology%29%20Moumita%20Das.pdf)  
46. (PDF) VHDL switch level fault simulation. \- ResearchGate, 檢索日期：6月 9, 2026， [https://www.researchgate.net/publication/220811556\_VHDL\_switch\_level\_fault\_simulation](https://www.researchgate.net/publication/220811556_VHDL_switch_level_fault_simulation)  
47. NAVAL POSTGRADUATE SCHOOL Monterey, California THESIS \- DTIC, 檢索日期：6月 9, 2026， [https://apps.dtic.mil/sti/tr/pdf/ADA289134.pdf](https://apps.dtic.mil/sti/tr/pdf/ADA289134.pdf)  
48. PROOFS: a fast, memory-efficient sequential circuit fault simulator, 檢索日期：6月 9, 2026， [https://www.researchgate.net/profile/Janak\_Patel4/publication/3223318\_PROOFS\_A\_Fast\_Memory-Efficient\_Sequential\_Circuit\_Fault\_Simulator/links/0fcfd50cf461443f79000000.pdf?origin=publication\_detail](https://www.researchgate.net/profile/Janak_Patel4/publication/3223318_PROOFS_A_Fast_Memory-Efficient_Sequential_Circuit_Fault_Simulator/links/0fcfd50cf461443f79000000.pdf?origin=publication_detail)  
49. 983 www.cambridge.org © Cambridge University Press Cambridge, 檢索日期：6月 9, 2026， [https://assets.cambridge.org/97805217/73560/index/9780521773560\_index.pdf](https://assets.cambridge.org/97805217/73560/index/9780521773560_index.pdf)  
50. Transitive Array: An Efficient GEMM Accelerator with Result Reuse \- arXiv, 檢索日期：6月 9, 2026， [https://arxiv.org/html/2504.16339v1](https://arxiv.org/html/2504.16339v1)  
51. Activity-driven optimised bus-specific-clock-gating for ultra-low-power smart space applications \- IET Digital Library, 檢索日期：6月 9, 2026， [https://digital-library.theiet.org/doi/pdf/10.1049/iet-com.2010.0933?download=true](https://digital-library.theiet.org/doi/pdf/10.1049/iet-com.2010.0933?download=true)  
52. (PDF) Activity-driven clock design for low power circuits \- ResearchGate, 檢索日期：6月 9, 2026， [https://www.researchgate.net/publication/3619676\_Activity-driven\_clock\_design\_for\_low\_power\_circuits](https://www.researchgate.net/publication/3619676_Activity-driven_clock_design_for_low_power_circuits)  
53. The Pennsylvania State University The Graduate School College of Engineering IRREGULAR GRAPH ALGORITHMS ON MODERN MULTICORE, MAN \- Computer Science, 檢索日期：6月 9, 2026， [https://www.cs.rpi.edu/\~slotag/pub/GeorgeSlota\_ThesisPhD.pdf](https://www.cs.rpi.edu/~slotag/pub/GeorgeSlota_ThesisPhD.pdf)  
54. A Prediction System Service \- UCSB Computer Science, 檢索日期：6月 9, 2026， [https://sites.cs.ucsb.edu/\~sherwood/pubs/ASPLOS-23-pss.pdf](https://sites.cs.ucsb.edu/~sherwood/pubs/ASPLOS-23-pss.pdf)  
55. This paper is included in the Proceedings of the 2022 USENIX Annual Technical Conference. DepFast: Orchestrating Code of Quorum, 檢索日期：6月 9, 2026， [https://www.usenix.org/system/files/atc22-luo.pdf](https://www.usenix.org/system/files/atc22-luo.pdf)  
56. Performance Modeling and Analysis at AMD: A Guided Tour, 檢索日期：6月 9, 2026， [https://www.ispass.org/ispass2007/keynote2.pdf](https://www.ispass.org/ispass2007/keynote2.pdf)  
57. Simulating Human Cognition: Heartbeat-Driven Autonomous Thinking Activity Scheduling for LLM-based AI systems \- arXiv, 檢索日期：6月 9, 2026， [https://arxiv.org/html/2604.14178v1](https://arxiv.org/html/2604.14178v1)  
58. (PDF) Event-driven gate-level simulation with GP-GPUS \- ResearchGate, 檢索日期：6月 9, 2026， [https://www.researchgate.net/publication/224585516\_Event-driven\_gate-level\_simulation\_with\_GP-GPUS](https://www.researchgate.net/publication/224585516_Event-driven_gate-level_simulation_with_GP-GPUS)  
59. RECTANGLE: A Bit-slice Lightweight Block Cipher Suitable for Multiple Platforms \- NIST Computer Security Resource Center, 檢索日期：6月 9, 2026， [https://csrc.nist.gov/csrc/media/events/lightweight-cryptography-workshop-2015/documents/papers/session8-wentao-paper.pdf](https://csrc.nist.gov/csrc/media/events/lightweight-cryptography-workshop-2015/documents/papers/session8-wentao-paper.pdf)  
60. Design of a Bit-Sliced Processor Array with Built-In-Self-Test. \- DTIC, 檢索日期：6月 9, 2026， [https://apps.dtic.mil/sti/tr/pdf/ADA149785.pdf](https://apps.dtic.mil/sti/tr/pdf/ADA149785.pdf)  
61. Architectural power analysis: The dual bit type method \- Electrical and Computer Engineering | UC Davis Engineering, 檢索日期：6月 9, 2026， [https://www.ece.ucdavis.edu/\~ramirtha/EEC289O/W04/landman95.pdf](https://www.ece.ucdavis.edu/~ramirtha/EEC289O/W04/landman95.pdf)  
62. CPC Definition \- G06F ELECTRIC DIGITAL DATA PROCESSING (computer systems based on specific compu... \- USPTO, 檢索日期：6月 9, 2026， [https://www.uspto.gov/web/patents/classification/cpc/html/defG06F.html](https://www.uspto.gov/web/patents/classification/cpc/html/defG06F.html)  
63. Bit slicing \- Grokipedia, 檢索日期：6月 9, 2026， [https://grokipedia.com/page/Bit\_slicing](https://grokipedia.com/page/Bit_slicing)  
64. BugHunter Pro and the VeriLogger Simulators \- SynaptiCAD, 檢索日期：6月 9, 2026， [https://www.syncad.com/pdf-docs/BugHunterVeriLogger.pdf](https://www.syncad.com/pdf-docs/BugHunterVeriLogger.pdf)  
65. Bitslice Implementation of AES | Request PDF \- ResearchGate, 檢索日期：6月 9, 2026， [https://www.researchgate.net/publication/221282461\_Bitslice\_Implementation\_of\_AES](https://www.researchgate.net/publication/221282461_Bitslice_Implementation_of_AES)  
66. SDR Platform for Wireless Cooperative Protocols, 檢索日期：6月 9, 2026， [https://projekter.aau.dk/projekter/files/32313099/report.pdf](https://projekter.aau.dk/projekter/files/32313099/report.pdf)  
67. BFT—Low-Latency Bit-Slice Design of Discrete Fourier Transform \- MDPI, 檢索日期：6月 9, 2026， [https://www.mdpi.com/2079-9268/13/3/45](https://www.mdpi.com/2079-9268/13/3/45)  
68. Novel Bit-Sliced In-Memory Computing Based VLSI Architecture for Fast Sobel Edge Detection in IoT Edge Devices \- Digital Commons @ USF \- University of South Florida, 檢索日期：6月 9, 2026， [https://digitalcommons.usf.edu/cgi/viewcontent.cgi?article=10149\&context=etd](https://digitalcommons.usf.edu/cgi/viewcontent.cgi?article=10149&context=etd)  
69. Weight Transformations in Bit-Sliced Crossbar Arrays for Fault Tolerant Computing-in-Memory: Design Techniques and Evaluation Framework \- arXiv, 檢索日期：6月 9, 2026， [https://arxiv.org/html/2512.18459v1](https://arxiv.org/html/2512.18459v1)  
70. BIT-SLICE DESIGN OF A GRAPH REDUCTION PROCESSOR \- OHSU Digital Collections, 檢索日期：6月 9, 2026， [https://digitalcollections.ohsu.edu/record/224/files/224\_etd.pdf](https://digitalcollections.ohsu.edu/record/224/files/224_etd.pdf)  
71. May 31,1983 \- NASA Technical Reports Server, 檢索日期：6月 9, 2026， [https://ntrs.nasa.gov/api/citations/19830026325/downloads/19830026325.pdf](https://ntrs.nasa.gov/api/citations/19830026325/downloads/19830026325.pdf)  
72. ELECTRIC DIGITAL DATA PROCESSING (computer systems based on specific computational models G06N), 檢索日期：6月 9, 2026， [https://www.cooperativepatentclassification.org/sites/default/files/cpc/definition/G/definition-G06F.pdf](https://www.cooperativepatentclassification.org/sites/default/files/cpc/definition/G/definition-G06F.pdf)  
73. Using Structural Netlists for Simulation \- 2025.2 English \- UG892, 檢索日期：6月 9, 2026， [https://docs.amd.com/r/en-US/ug892-vivado-design-flows-overview/Using-Structural-Netlists-for-Simulation](https://docs.amd.com/r/en-US/ug892-vivado-design-flows-overview/Using-Structural-Netlists-for-Simulation)  
74. High performance array with buffer to prevent unnecessary copying on resize?, 檢索日期：6月 9, 2026， [https://stackoverflow.com/questions/21946348/high-performance-array-with-buffer-to-prevent-unnecessary-copying-on-resize](https://stackoverflow.com/questions/21946348/high-performance-array-with-buffer-to-prevent-unnecessary-copying-on-resize)  
75. Turbocharged: Writing High-performance C\# and .NET code, by Steve Gordon \- YouTube, 檢索日期：6月 9, 2026， [https://www.youtube.com/watch?v=g8MYUfplpt8](https://www.youtube.com/watch?v=g8MYUfplpt8)  
76. Simulation software similar to SPICE \- Huskie Commons, 檢索日期：6月 9, 2026， [https://huskiecommons.lib.niu.edu/cgi/viewcontent.cgi?article=5923\&context=allgraduate-thesesdissertations](https://huskiecommons.lib.niu.edu/cgi/viewcontent.cgi?article=5923&context=allgraduate-thesesdissertations)  
77. A shiny, new and faster topology system \- MDAnalysis, 檢索日期：6月 9, 2026， [https://www.mdanalysis.org/2017/04/03/new-topology-system/](https://www.mdanalysis.org/2017/04/03/new-topology-system/)  
78. A comparison of the Array-of-Structs (AoS) and Struct-of-Arrays (SoA) memory layouts. \- ResearchGate, 檢索日期：6月 9, 2026， [https://www.researchgate.net/figure/A-comparison-of-the-Array-of-Structs-AoS-and-Struct-of-Arrays-SoA-memory-layouts\_fig2\_259132352](https://www.researchgate.net/figure/A-comparison-of-the-Array-of-Structs-AoS-and-Struct-of-Arrays-SoA-memory-layouts_fig2_259132352)  
79. PauLIB: A High-Performance Library for Processing Pauli Strings \- arXiv, 檢索日期：6月 9, 2026， [https://arxiv.org/html/2605.25974v1](https://arxiv.org/html/2605.25974v1)  
80. Synthesis of quantum simulators by compilation \- White Rose Research Online, 檢索日期：6月 9, 2026， [https://eprints.whiterose.ac.uk/id/eprint/226188/1/3696443.3708949.pdf](https://eprints.whiterose.ac.uk/id/eprint/226188/1/3696443.3708949.pdf)  
81. Zero Copy Arrays \- Kitware Inc., 檢索日期：6月 9, 2026， [https://www.kitware.com/zero-copy-arrays/](https://www.kitware.com/zero-copy-arrays/)  
82. Performance of Arrays vs. Lists \- Stack Overflow, 檢索日期：6月 9, 2026， [https://stackoverflow.com/questions/454916/performance-of-arrays-vs-lists](https://stackoverflow.com/questions/454916/performance-of-arrays-vs-lists)  
83. How To Optimize Arrays In C\# Code \- YouTube, 檢索日期：6月 9, 2026， [https://www.youtube.com/watch?v=geSrhG84l4c](https://www.youtube.com/watch?v=geSrhG84l4c)

[image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADMAAAAaCAYAAAAaAmTUAAACkklEQVR4Xu2XT6hPQRTHj1DkvycSJbIhZSGLVxYWLCgWbBR7FnZKUeqVZCtZSa8sbCRslIXFLyuxYKHYkrKQjaKQP+fj3PE79/zm3t+83iXqfurbe/ecuTN3zpw5Mz+Rnp7/jmWqm9E4S/arnkTjTJmjOqW6q3qgOld3Z6Hd1WisWK1a6xRZXinCd5xWbYiOElaqvqtuB/si1Q+xD85xWLU3Gh1rxCZBH2iy7v41ke2Vb1o1ITaRxDvVefc8ls2q56ozqrnBB4/FBvODAFHnvYXBHlmhuiPWxxUZ7QcGqsXRqFxSfYzGJsh3BnkYHY5dqq+q48F+X/Um2HIcFfvQKbGxTta8IqtU+4ItwXsD1cZgH4EIsYTfpD1VdohF51qwY7sVbDkuV3+3iU3mkfPBTtWmYPNcUB2JxghLTudEro0DYu2uBzs2fG1sUT1zz5/F3ptXPRPQG0N3FvbUQPJp+BtShI7bogJnxdqlCAOF4ZNYVNsgUAP3TGrSF6sEpNjToTsLBeRV9beRVGHG8VasHVFOFA0gVjwOumeq24tK/M8+vOf8OVgRUpp0b6R0MqldSg0onQwpFld+Sqw/9gF7jj3RRmeTOSTWhhLpKZ2MT80EQaFPzrWXqvV19widTCadI5RtSriHXH8v9dSLcL74FPMwEcYmxRYEX4R+PogVgkZOiHWYK8schPjI7Sbwt1UzzpMl0VhBivH+nujIUFTN5ovdqV5LvSptFdu4lOKlzh7hY6h0kd1i6fVFdVGGlcuTCsG4FAMO1Fy6ZmGZiRAT4yXuRyVw+JXcAGZDugGsC/bOIY3a9lwXpNtH7j7XOcckv+e6glszF+C/AvuO/VWamjOBvvnpESvpH+Wf/aXZ09Mzyk8eWYOkkFV6EgAAAABJRU5ErkJggg==>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAGcAAAAaCAYAAACq/ULmAAAEf0lEQVR4Xu2ZX8iPZxjHL83KYmMjMita24qUlpByIFlxsKlRVnPmgNaKKEyUknAmkVpLOZCGUEaSgzdOtB1sB8TBFFppB9IOthox18f1XL33c73388fr/b2v2vOpb7/fe93373nu+7n+3Nfv94p0dHR0dNQwXnUyGv/nvK/6WTUqDrwKXGyz6pzqimpneTgL876PRuUN1WTV1EJjysMvmBANGaaoPlCtUL0XxnrNROlfP4qME5uTY75qZTQOBjb9THUm2Meq/hNzQA5u/lk0Frwp9mBPiV3jX9XC0gxzzmWx8dmS3yiO2aj6WzU3jPUaDy7Wh74qD79wzsfF2AWx+QSlc14sgwbNR6obqu+kfGGHi3PzmKIshM+9FewRPn9W7BqHwhhsVfVFYwCnjIRzYLTqklhw8Zrb713VtGgUC9yn0dgWzgse2tU4kLBI9US1LthZ6B/BlqNPLMI8+iI4eHk0BkbSOeyfB0/W5AKMcr0p2FIIvphxjZAJu8U8W1WawB/MD8GO7XSw5ThYvD4S2xwBkXJb9WGwRUbSOQQlDqBEs/4YkJRdHFgFYyfEMrA1RAA3+zoOBD4Xm3cs2LExVsdM6X/wnDeUhnShBMie4n0dOefw2dWqv8SugXiPLYVzk/P0ruqx6nfVr6ofpT7iYZKUzwwcEwNsu9Q/eO4f196I36gpark58zwDgBv+o5qX2HLgeEoaUKsphWQQhz+w+aXF+zpyztksti6y3+F9ej7SlNDmbylsM1R3VAtUS6Q5uIj6m8nfHtDLir/JKBqBJtoEcomqMyDyQGweWeDQwdwrXqt4VwZ2Kl4abhV/e8loIjqHz3CdWGpxwHHpz07qPfPSdWLDQaylCco2850YYAQWWdgEa0iv00hb5/i8NHXbOAdn/haNUr5emzMLonPI9qoNU36pCpwFlK3oHCrBL2LB0wTBFc8Tbwx2iZXSNnuoWmslbZzzpdicA8HexjlEdVoKHaKaa+4XawbaEJ3Da9WGsaVzN6guqj4Ry1QaoNiUVEHJymW2PzvW31TaoWqtlTQ5x7/H0GbHzXBWPJRyqYsQdV9Eo1i95oBGbeo1ROd45sSyBmSOly3K0HnVT2LNwJ+qff1Ta/GymMM7T54Pz6IJ5jY1HyXWi30o10azKcb8bMjBeN0hd031djQWUBpwTptmAKJzgAeUNhfAe2yfFn8TPAQRDczLQhBRGnN45xm/++UYVLdGJ8NvYvelnJqzxKKeCHwnsUdwDvU7slisnFE+9kq+LBDVfi40MV21TcyZ3M+vR8klG9Iv0LzH5njmeJVwHZbqe9Nd7hBry4+K/UYW8cagTUljr3R8bTJsAGyWCMZRPNSJ5eFKrsvAL2QjBWdf7vw7IhYkBKLDT1TsFSfFct0LKI258t5TvpX6M+t1gJKW66bIjj7VnGAfavw+nN/DzhrJn1mvC3wBJXPij7ZrC3uaUb2Aklr3u2VPYXOcT21L4XDDYUyp5oviN6pVxSsZn2vzhxq6w9yZNWxQt7v/hJbpyX9COzo6Ojo6hpLnocQF9N2nWu0AAAAASUVORK5CYII=>

[image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAaCAYAAABVX2cEAAAA9UlEQVR4Xu2TwQoBURSGj1hQFkJhSdnJUilLa6+h8AgWnsCTsJKdxay8gchOKVlYWin8p3OmrtsdM5OVmq++pvufuWfmztxLlPArWViwwxB4jhMu1OANvmBbx+YDSnAFT3AE80bNyYOkmYsBvMK0XXCRIWnEb2dShEu9RqZM0syz8gOcWlkoQ5JmvJwcnOs4NrzEBbzABtySNGK5Foseyce/w6NmE5JmMx1Hxl/iBlY1q8Mz3MOKZpHYkTSzN2JXc8/KvxK0v/hHcP60C0H4+4s/vgteJtdbdsEkBTtwTHIz76cmfR4TPkJ8fLi+hn2SeQkJ/80byEkv/usHTksAAAAASUVORK5CYII=>

[image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEwAAAAaCAYAAAAdQLrBAAACPklEQVR4Xu2XvUsdQRTFj6igGNCoqEEbJSmCYhM1ILFT0SaFhFSmSpEiIWAj2PkXiFimUQsVNJaCEAsrsdTCD+wMgRQSrFKImOSe3BmcN7zVeSv6Fp0fHN7unY83e/feO7NAJBKJ3B8qRDW+8Ro45sHCh38i+iX6K+o09y51pu1Y9Ez0KLe5KJyIfvvGAOxzfje6MPdlbqcQzqEDfUpEr0WfRaVeW7GohK71Jg6zosPGc3oEQO9yMCdzqRWtmt+s8FS0h/QO2xG98I2FUg9dwKZj6xYdisodWxZYFk2gyA77AF0AU4/hPiV6m9MjGzC6+sz1TRzWJZqDBkQvCiw1TMevop+iNtEWdDFLpq1Q+OcN0I0jVCG7LiN9FlpTSVqH7Yt2Re9Fb6DzrOT0uIZX0ILPPz8ytk/QiSbNfRZg3Rpw7tM6jHO4Ndk+/5BjuxKbjhuiJmNrNbYD26nIMLqmcRldJK3DfJ5DNzs3eq/E7jh+WpwZexbOXPPQtSSJ7SFUQ48RY46NJYHny00EPmvS+Wsdag8OVUOLaBuXB8MQvfs/MpmX0Hrjimvj2nnN9hD6oeNYwyxc7w9ohlU59rzY8xcLvk8jNCVPRR1eWxbIl5Ltxs4Xlg9GGOuzm3q2XnNsIhzQI/oI7czt1f/kGYGGKtvXoJ9HQTl+yzSLBqHr+gN9mfZbeNjYGXlJMDgYaRYGBNP0wcKXuuAbPfjyv4hmkL2D+Z3zWPTNN0aSobNGfWMkP9zEFhHTLBKJRCJp+QdlyYwVRiBhZQAAAABJRU5ErkJggg==>

[image5]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAoAAAAZCAYAAAAIcL+IAAAAyElEQVR4XmNgGHqAEYiNgXgWEDeiycGBARDfBWJNKN8TiK8CsQhcBRCIAfEVILaF8nmA+AAQvwZiNagYGDwA4v/IAgwQZzCjiYEV/YOyQZIYCkCAhQGi8BsDxAOcQGwDxO+BWB5JHdg9IIUgDGLDQA4Q70IWgyn8DROAAlAwgcTnIAuCBL4iCzAgFO5BFsSn8ACy4BOoIDIwhYqlIwtOgQoig2ggvg7EMmji4Cj8C8QbGSBB9RFVGhW4AfEkIA4FYm40uVGAGwAAo5Qor8CcoqgAAAAASUVORK5CYII=>

[image6]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABUAAAAZCAYAAADe1WXtAAAA5klEQVR4XmNgGAX0AIxAbIcuiAT4gDgZiKcBcSiaHAroA+J3QPwXiP8DcTmqNByYAfEeIOaH8kH0e6g4BjAH4hAglgPihwzYDeUE4h1A7IEm/g+IN6OJoQBJBtyGgsRAvhBHEwepB4njBPgMXcgA0cyDJn4aKo4T4DIUZNABBuyG4hKHg6FvKDcDJClh03wAKs6BJg4HuAwFAVhE8aKJX4WK4wT4DLUE4p9ArIQm/gmIv6KJoQB8huJK/CBXYk38sISNjkEWgCxCBiBXgcSroLQTqjR5ABRpoIJkEhC7ocmNglFACwAA6ElDZPfVY6QAAAAASUVORK5CYII=>

[image7]: <data:image/png;base64,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>

[image8]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADMAAAAaCAYAAAAaAmTUAAACeUlEQVR4Xu2Xz4uNURjHvzKKTORHRCkjFiM7sZqVKBsWWChqlig1NbOYKCv/gGQlpVlNSSIbxUIWmsaWrEnJQlYsyIzn23NP89zvnPfcM907ot5Pfbv3Puc55z3neZ/nnHOBlpb/js2mB2rsk92medMabVgJ7Dxlemx6YbrR3ZyFfnfV2GGbaa0aC6zDkv9R09nQVs1W04Lpkdg3mhbhE87Bh51QY2CHaQ98jF+mXR1bYr3piumn6TZ88fFtPIW/oWr2m96ariEfRQ7Gyegr56TYb4PYlS3w/u+0wdgLH5/BzMFA/VZjE8x3PuiVNgTG4FG9JPZnpk9iy3EB/ozYn0GbMB0JtiamTefVqDDSN+ErL6XKYdN30z2x0/ZQbDmYPvTlOAluGM/D7xIM5qxpSBsid+ARY+RKnIL7zYidNraVGDV9Nd2HB+80fGG5dG6CdavBWAZThBPapw3CdbgfI5zgA36gd5qkFOMna4vfqZHoVEHPwKWBe/EZ7scoJ7grfeh8lngD78u3wxo5ZPpmmoPXay0cg7XTSO1ikl/M2drFcBHse6zzm2Mw/3kMnExOFQxkMWfgPrfEXruY9FYiaQdlHdTS92LSOcJtW1NiO3ySMfWUdL4w1ZT0bD27mqDvpBojl+FOuW05Fet7bQiwvVSUsfiVtJNe1YYMVbsZ70G8U31E9650EH4qcyveFOwKJ8OdTjlgOmd6DT9smR7xmsIrzBN4fz57HD6XJnbCbw/Mhp5w8OPwhaX7UQ3ckWpuAP3CYPB8WlWYIqWaGwTDppfovpyuGheRr7lBwVtz6d44UJjrrK/a1FwpX+D/a/4a/+w/zZaWluX8AVCzibZrZODzAAAAAElFTkSuQmCC>

[image9]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACsAAAAaCAYAAAAue6XIAAAB5klEQVR4Xu2XPyhHURTHj1BEFCJGMZBNTEZGBhbFqBhMDKJMdkkmWUxKkrIYDL9MMpOZRAYjA/lzvp33q/u+3ffe/dUPv+F96tuvvue8e89977x7308kJ6fsNKsOVcscSKFLdaWq4kAp4GJMeqI6V63Hw16Qt6uq5UAGw6opNkNoUX2pjslvUH2LFeQDk42xGYGFb6reVY0UK3IqdoeD6VFdq1ZV1RQDGAwF8yNrF7uunnzwKHbNZ/SbVCwWipwg0G8Y7IIDDiOqD9U8+WeqB/KYfUkvFqyoptlkcKc2xFaW9CjBoOpVtUc+vCPymJBicTMOVDUccNkRG2iGA8S4WB4mdoGHWBohxeK9wMJxUxLBI8RA3Rwg1sTyth0PE7yphhzPR0ixIHPhSICyeBLL63O8TtVd9JtGKcWidxMJLbaY5/ZURRY7KZazRX7FFVvcR7GtYYtzaVO9SLw1fJRS7BKbLgtiSb5tCxs9YrcccEA89aWQsGKDdgOc5TjT7yX+VveLnVqYqMnxGRSBncJHh2pAdSOWN6fqVdW5SRHIRR6eViYYYFSscGxPrfFwIpeSfYKFgF6dYLPcLEp6z4eA9iiIvR+/zqz4ez4UfHWlfZeUFfQ9+ju0dZhnse/aP+Pf/ink5FQiP/Ojc5TBNIS5AAAAAElFTkSuQmCC>

[image10]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAE8AAAAaCAYAAAD2dwHCAAADk0lEQVR4Xu2YS6hOURTHl1DklUeklDxSDBiIUjIQSiF5ZMCcZEQSMpAMzCQjKa8MPPIoykC6GUhMDMjIgESSRCjksX6ts+9dd91zvnO+r+9+1+P86t/9vrX3d/Y+a6+99tpXpKam5j9njOpiNP4lPFBtjMZWGaTapbqmuq060Ls5F/qdyD7z+8mqharZ3T3+XJjn62hslnGqn6orwT5C9UvMQXmsVy133werlqqeq/Y4eydg4aaodojN+abYQibhqDtZm1/Y4WIRON7ZKjNT9Vi1V+zlIzyYAZmcZ6LY7xjcM1LVJZ13XmKz2Hz5G2Gut8Sc6fkhLcyXfMVAd2ODY7Hqu2prsDOJl8EGA+m8saqHmficB/OKgYANP1SGBxwS87rfepH5qs+qk8GO7XKwwUA6b57qo/SdKwcaqQn2+YaMFCBDYkMRx6U4vD2rxfqdCXZstEWKnDdLLAWcVu0UW7RjvoPYgm5SfRJ7xjexw4sD6YtYDm4ETmNeODExVfrOJcJzyesESiXYcgw0PTYEWCn6+RdlMF5mgbMlipz3VnXffV8hFr2kjsQWsbGS7VT2fZLqnpRHBtuV/ufFHH5BbJ5EVhkER14w5MIgVfY5Rzn9/AlFwuVEjYkX8pxHvnwm5gTPOrFFnCY96cHPKdlWOlsj4jsNVV2VcqcD840LXkgcqIjUz0+gGefxO3IjNto8KSXgxJSv/JyI7KqRw5bnt++CjShMzFDtdt89bXceL0Wfo8HejPPSdxSdR2Tx/NR3ruqDWKSNEovW91lbGelZMY96nqoWRWNGW52X6jjKGJ+XYILYCufdIqLzUuTlbdsUeWlbUuCeVb3J7DfEDpoqNKrvgCikvIp1aeKw2EFWiW1ig+WVKQxAGytVBO15CTY6D1I9iXMSqWDlOgjDxG4Fj7p7VIfFfCLF9R3vyIEVFy/R9GlLMuVEeiG9T805YiUFp89oZ4/gjFgzcUNZJbalr4tdiRJEMVsw3WIOil0JmUciLagXZcsS6VvYAs9ih+wXK33YJWul50q2QbVdyncZTmVnsAhNwYovE3Mk+aLqHY+yI++GUQYT5cWiM9aIOcAneGA+nLhfg72dsEsaObftsAXbOSBblrIor+7s75frEtsZHYWiNi9ntkIqkM/FBrEt9Soa2wS5N+9Q7HfIVy3/OyeQrmaUKkfE8hUn5yWxMaqeus3CmD43d5T6P8k1NTU1/yi/Ad7I2SiUSEWuAAAAAElFTkSuQmCC>

[image11]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAbCAYAAAB1NA+iAAAA20lEQVR4XmNgGAVUB5OA+D+R2BeqByv4zQBRhA0wAvFXIDZGl0AGMFtwgYdALIkuCAPiDBDNe5DExIB4AxJ/KxBzIPFRgA0DxIBWJDEPID6GxE9GYqMAFiBeA8T/gHg9EM8C4oUMEAOjkdThBCDn3wXiX0D8CIrfMRARaDBQzgCxzQVNfA4SW5gBEiZYAcj5IANkkMSYgTgCiZ8DxJxIfBTwgAFiAK4Q5mdADUwMANIMCkBcABQ+DeiCMACyFWTAc3QJKNAD4vdArIMuIc+Amc5xYUuonlEwCqgLAKUiOuLw/10bAAAAAElFTkSuQmCC>

[image12]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAA4AAAAaCAYAAACHD21cAAAAqUlEQVR4XmNgGHmAA4gF0AWBgAddAB2AFEgC8Vsg/g/E5lA+yECiwG8GiEaSAAsDRNNzdAlCQIQBonErugQhkA7E/4DYBV0CHwA5cw0DxJlKaHJ4gQ0DJGBa0SUIAZAzQf7D5cwqIBZDFwSBqwwQjdzoEgwQb+SgC8IAvvizBWIddEEQwBd/exiwGMgIxGZAnM0AkbwBxDJALA/E0UC8Eir+BKZhFIwCBgCr9h2EwjrI8AAAAABJRU5ErkJggg==>

[image13]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADEAAAAaCAYAAAAe97TpAAABDUlEQVR4Xu2WMYoCUQyGIygICgqKICwsWNsJbqNgYSsewANYrCfY3sob2Nkp2Np5h60EDyBY2WmlqP8jYxOYGSM4b2HzwVdMwoP8kDczRIZhRJGFRVkEeVn4y7hhq/AAb/AreHbhfNGHv7AnG3GciUP4ZA6P8EI8iypEmvjQXjY80YAnUoYoE4dYyYYnXgoxhFfYlQ1PqEO4VVoSr1JN9HyhDtEivtRj2fCIOoRbJXcfwlbpB1ZkMaAApwozfCwWdYgNcYicbBCv2kgWE0AdIur70IZ1WUwAVYio78OawsO9m6dCpGATfhMPuoUf8BMO4CKo7x4HEsL9w7k5JsSv/BnswBLxzIZhGIbxf7gDGCY5TOu0RwIAAAAASUVORK5CYII=>

[image14]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAoAAAAZCAYAAAAIcL+IAAAApUlEQVR4XmNgGHogGYhnAXEoEHOjyYGBGRC/B2J+KB9E74GKo4AdQPwPTcwDiDcDMSey4H8gvossAASSUPF0ZEGQwAFkASAQhIrPgQnwQAUOwARwiWMI4BLHEMAlDgovfAq3IguCBA4jCwCBCFQc7hkQ+AnEz5EFgEAfiL8CsTGyILYA92PAEuAg4A3ED4G4Ckq/RpVGBaDEMA2I3YCYFU1uFOAGAA+IKW/sA1x0AAAAAElFTkSuQmCC>

[image15]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAGIAAAAaCAYAAABM1ImiAAAC1UlEQVR4Xu2YTahNURTH/0JRvkJKKVFMGPgImRMGDEQpZgYMlBkTgycZyECZ6BXJSKEMfGQgvQxkpkRMFCbKxIiifKxf62z3vvXuO+fsp949Zf/q3z3tdc7e++y918e5UqFQKBQKeSwzLa/R7N6tnWCF6U1snCbmaOL6JP0Tx0y/Ta9Ma9TrlJelHc34e3c3+Cqf1/xomAYWmXaZvpkuafxG7Dd9rLQhPdAGdveB6aRpZl87C39K/rJX+tq7AN6ZDshUTuFc09rYmMke+fj8RnbLbXejoY7DGuziX+SdLYyGIbPE9Nx0VT6/zePNjcwy3ZS/86pgy4HDy/gc5Mhque11NNRx3TQS2raqF6q6xmnT0+qXOW4Zb25kvXyBHplOBFsOn+TjD+KI3HYuGnJ4Iu+EzeganGYODqFpr3yeeHQOeMJ2uac/C7YcGJvNiNA/tsXRkAOTo5Of0dAA+SRWD02aCjtN66rrtBF4Rg735DkC8Ih0nQPhiLHfmg5UOmt6Ybpv2ta7NZ+UmPntWoUEx+W5IUFuoHK60dfWxCFNzHnkCzwthx3ytRqUqFM1N6U15CEeRvF7gQXoApw+yuzkUZvkoeGO2i3kZKGIfskbOZyXj01SjhA6WUfCXxbkAiqkx5p4WijDloa2YYCrbwxt80xj8sTbZo6c/JHYKF+wQVXjZLD4bAKbMYjL8o3g0GSRytSVoR3PuKV2LrZAvY+YtmoLMZy4HueRNuKD2uWclxpcrqb+20JY+mXaFw0VhE/Ws3VZzenHC9iIWCHxYffD9D60D4N3mrzMJD8Qk5temg2oCxWsBfmjDWPyheYg9MOaHa1st4OtFm7moWvqZX40avpc2XDnYcHhOCOfB3mgP3dxfVAelrBfkH/oRa9JsJFNeeSh6iso/otjTt/lY1K1EUXwRnIMFRPtF1Xfz38LG8TfN03gEbkfh4VCoVAoFAqFQqEwrfwB11iVQ3DYldgAAAAASUVORK5CYII=>