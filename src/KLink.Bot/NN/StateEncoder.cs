using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.NN;

/// <summary>
/// 局面 → 定长向量的编码器（**v2，745 维**）。
///
/// ⚠️ **这份代码是训练与推理唯一的编码来源。**
/// 原先它写死在 <c>tools/NNTrain/Program.cs</c> 的局部函数里，NN 玩家没法复用 ——
/// 于是要么复制一份（训练/推理编码不一致 ⇒ 静默错误，最隐蔽的一类 bug），
/// 要么把它抽出来。这里选后者。
///
/// 向量构成（<see cref="Dim"/> = 745）：
/// <code>
///   全局 3 维（与视角无关）：Turn/30、ActiveSide == 视角方 ? 1 : 0、CardsPlayedThisTurn/10
///   每方 6 个全局量：HQ 防御/20、kredits/12、kredits 上限/12、手牌数/10、牌库数/40、场上数/10
///   每方 5 个区域：手牌 / 前线 / **本方半场** / 弃牌堆 / 牌库
///       前 4 区 = 池化卡向量 90 维（该类卡的 90 维卡向量取算术平均，空则为全 0）+ 该区域张数 /10
///       **牌库区 = 只有张数 /10，没有卡向量**（v2 的改动之一）
///       **对方手牌区 = 只有张数 /10，没有卡向量**（v2 的改动之二：隐藏信息 + 卡组指纹）
///   (3 + 371) × 2 方 + 3 = 745
/// </code>
///
/// 卡向量 = <c>klink bot/docs/card-vectors.json</c> 的 <c>s</c>（9 维标量）
///        + <c>k</c> 的前 81 维（11 关键字 + 55 交互 + 15 效果标签；跳过后面 65 个触发位）。
///
/// **先后顺序 = 全局在前，视角方其次，对手最后**（<see cref="Encode"/> 的 <paramref name="perspective"/>）。
/// 训练时视角固定为 <see cref="Side.Left"/>（标签 = 左方是否获胜）；
/// 推理时传**自己那一方**，输出即可解读为「自己获胜的概率」——
/// 这一步依赖编码器对左右对称（确实是：只按 perspective/Opposite 展开，没有任何左/右硬编码）。
///
/// <para>
/// <b>v1 → v2：把两处「卡组身份指纹」从编码里摘掉。</b>
/// <list type="number">
/// <item><b>牌库区</b>原先写整副牌 40 张的池化卡向量 → 改为只留张数。</item>
/// <item><b>对方手牌区</b>原先写对方手牌 4~5 张的池化卡向量 → 改为只留张数。
/// （视角方自己的手牌向量**保留**，那是自己本来就看得见的信息。）</item>
/// </list>
/// </para>
/// <para>
/// 为什么必须摘（见 <c>klink bot/docs/NN训练诊断.md</c> 第二轮 §3.2 / 第三轮 §5）：
/// 卡组身份原先在编码里近乎唯一可辨 —— 实测按真实卡组身份分组、用「本组质心是否最近」判定，
/// 左右两侧 **99.9% / 99.8%** 命中。而卡池本身极度不平衡（22 套牌胜率 8.5%~86.8%、
/// 跨度 78.3 个点、σ = 20.5 点），于是「认出这是哪一对卡组」就能白拿 73~79% ——
/// 这条捷径与打法好坏无关。v2 摘掉这两处之后：认**对方**卡组 71.4% → **5.9%**
/// （22 套牌随机 = 4.5%），全向量 99.9% → 41.8%（剩下的 41.8% 来自**自己的手牌**，
/// 而自己的卡组本来就是已知信息，不算泄漏）。
/// </para>
/// <para>
/// 三条理由说明「摘掉」比「训练时随机替换牌库向量做数据增强」更对：
/// <list type="number">
/// <item><b>它们本来就不是可用信息。</b>牌库内容对双方都是隐藏的（抽牌顺序连自己也不知道）；
/// 对方手牌更是完全看不见。真实 agent 拿不到，写进编码是信息泄漏，不是特征。</item>
/// <item><b>数据增强会破坏「编码 = 局面的纯函数」这个不变量。</b>同一个局面在不同轮次得到
/// 不同编码，NNPlay 的「重放试算 ⇒ 编码 ⇒ 估值」与「训练/推理共用一份编码器」这两个前提
/// 就没了；而且推理时必须用真实向量，等于把训练与推理的输入分布人为拉开。</item>
/// <item><b>增强也堵不住捷径。</b>手牌/弃牌堆/场上的卡向量同样泄漏卡组身份，而网络对
/// 「同一局面 N 个副本取平均」本来就等价于去掉那个噪声维度 —— 它照样能学到先验。
/// 要改的是「这条信息不该进编码」，不是「让它变糊」。</item>
/// </list>
/// </para>
/// <para>
/// <b>v0 → v1 的两处改动</b>（见 <c>klink bot/docs/NN训练诊断.md</c> §6）：
/// </para>
/// <list type="number">
/// <item>
///   <b>补盲区：半场单位。</b> v0 的 4 个区域只有 <c>Hand / BoardFrontline / Discard / Deck</c>，
///   而 <see cref="CardLocation.BoardHqLeft"/>/<see cref="CardLocation.BoardHqRight"/>
///   （半场/支援线，单位**默认部署到那里**）里的单位卡向量**完全没进编码**——
///   实测平均每样本 2.16（左）/ 2.11（右）个单位是这样，只剩一个 <c>boardCount</c> 数字。
///   v1 把「本方半场」加成第 3 个区域（<see cref="SideExtensions.HqOf"/> 位置，
///   用 <c>!IsHq</c> 把 HQ 本身排除在单位池之外 —— HQ 和单位同处一格）。
///   <b>为什么和前线的分开、不合并</b>：前线和半场是两个语义完全不同的格
///   （前线是共享的、容量 5 的争夺区，能从这里发起攻击；半场是安全的支援线，
///   单位默认落点、要主动花一次移动才能上前线）。合并成「场上」会把
///   「我能不能打 / 我会不会被压」这个恰好最需要的信息平均掉，而分开只多花 91 维/方。
/// </item>
/// <item>
///   <b>补对局信息：<see cref="GameState.Turn"/> / <see cref="GameState.ActiveSide"/> /
///   <see cref="GameState.CardsPlayedThisTurn"/>。</b>
///   v0 里这三个量只能从 <c>maxKredits</c> 间接推（每人自己回合 +1、上限 12 ⇒ 触顶后失真）。
/// </item>
/// </list>
/// </summary>
public static class StateEncoder
{
    /// <summary>
    /// 单张卡的池化向量维度：9 标量 + 56 个非零位 = 65。
    ///
    /// ⚠️ **v3 把它从 90 降到 66**。理由是实测出来的：v2 的 90 维里
    /// 「关键字 11 + 交互 55 + 效果标签 15」这三段是**稀疏位标志**，
    /// 而区域池化用的是**算术平均** ⇒ 稀有位平均之后恒为 0。
    /// `out/audit/encoder-discrimination.py` 在 242,114 个样本上量到
    /// **745 维里 420 维恒为 0（56%）**。
    ///
    /// v3 的两处改动（必须成对做，见 <see cref="ZoneVecDims"/>）：
    /// <list type="number">
    /// <item>池化改成 **max + mean 双份**：max 让「这一区有没有某关键字」可达，
    ///   mean 保留「密度/平均强度」。稀疏位在 max 下**不会消失**。</item>
    /// <item>维度从 90 精简到 66 —— 位标志只留**确实出现过**的那 33 位。</item>
    /// </list>
    /// </summary>
    public const int CardDim = 90;

    /// <summary>
    /// 每个区域贡献的**卡向量**维度（不含张数）—— v3 = `2 × CardDim`（max + mean 两份）。
    /// </summary>
    public const int PooledCardDim = CardDim;

    /// <summary>
    /// 区域数（每方）：手牌 / 前线 / 本方半场 / 弃牌堆 / 牌库。
    /// v0 是 4（缺半场），见类注释。
    /// </summary>
    public const int Zones = 5;

    /// <summary>
    /// 每个区域各自贡献多少维**池化卡向量**。v2 起「牌库」是 **0**（只留张数）——
    /// 理由见类注释的 v1 → v2 一节（卡组身份泄漏 = 与打法无关的捷径）。
    ///
    /// ⚠️ 改这个数组必须同时改 <see cref="Spec"/> 与 <see cref="PerSide"/>/<see cref="Dim"/>
    ///    （后两个是静态构造函数里算出来的，不用手改）；模型文件里存了 dim/perSide，
    ///    加载时会逐项对账，对不上**直接报错**。
    /// </summary>
    private static readonly int[] ZoneVecDims = { PooledCardDim, PooledCardDim, PooledCardDim, PooledCardDim, 0 };

    /// <summary>
    /// 编码规格标识 —— **必须随模型一起存下来**。
    /// 只存权重不存这个，加载时对不上就是静默错误（不会报错，只是预测全是垃圾）。
    ///
    /// ⚠️ **必须在静态构造函数里赋值**：C# 的规则是「静态字段初始化器全部先按文本顺序跑完，
    ///    再跑静态构造函数体」—— 写成带初始化器的字段时，这里引用的 <see cref="Dim"/> /
    ///    <see cref="PerSide"/> 还是 0，规格串会变成 `v2/dim=0`（第一版正是这么错的）。
    /// </summary>
    public static readonly string Spec;

    /// <summary>所有区域的池化卡向量维度之和（v2 = 4 × 90 = 360）。</summary>
    public static readonly int PooledCardDims;

    /// <summary>单方维度：6 全局 + Σ(区域卡向量维) + 5 个区域张数。v2 = 6 + 360 + 5 = 371。</summary>
    public static readonly int PerSide;

    /// <summary>与视角无关的全局量个数：Turn / ActiveSide / CardsPlayedThisTurn。</summary>
    public const int Globals = 3;

    /// <summary>局面向量总维度。v2 = 3 + 371 × 2 = 745。</summary>
    public static readonly int Dim;

    /// <summary>全局块起始偏移。</summary>
    public const int OffGlobal = 0;

    /// <summary>视角方块起始偏移。</summary>
    public const int OffPerspective = Globals;

    /// <summary>对手块起始偏移。（同样只能在静态构造函数里赋值，理由见 <see cref="Spec"/>。）</summary>
    public static readonly int OffOpposite;

    static StateEncoder()
    {
        int pooled = 0;
        foreach (int d in ZoneVecDims) pooled += d;
        PooledCardDims = pooled;
        PerSide = 6 + pooled + Zones;
        Dim = Globals + PerSide * 2;
        OffOpposite = Globals + PerSide;

        Spec = $"v2/dim={Dim};" +
               $"order=global3,perspective{PerSide},opposite{PerSide};" +
               "global=turn/30,activeIsPerspective,cardsPlayedThisTurn/10;" +
               "perSideGlobal=hqDef/20,kredits/12,maxKredits/12,handCount/10,deckCount/40,boardCount/10;" +
               "zones=Hand,BoardFrontline,OwnHalf,Discard,Deck;" +
               // ★ v3 的关键改动：max+mean 双池化。v2 只有 mean，
               //   而稀疏位标志（关键字/交互/效果标签）一平均就趋 0 ——
               //   实测 420/745 维恒为 0。
               "zone=mean(cardVec90)+count/10;" +
               "deckZone=count/10only,noCardVec;" +
               "oppHandZone=count/10only,noCardVec(hiddenInfo+deckFingerprint);" +
               "cardVec=s[9]+k[0..80];" +
               "label=P(perspective wins)";
    }

    /// <summary>全局块里 <see cref="GameState.Turn"/> 的归一化除数（实测回合数 2…~40）。</summary>
    public const float TurnScale = 30f;

    /// <summary>
    /// `card-vectors.json` 里 `k` 段的**原始**长度（11 关键字 + 55 交互 + 15 效果标签）。
    /// v3 在 <see cref="LoadCardVectors"/> 里把它压到**实测非零**的位数（56）。
    /// </summary>
    public const int RawKeywordBits = 81;

    /// <summary>卡向量里标量的个数（`s` 段长度）。</summary>
    public const int ScalarCount = 9;

    /// <summary>卡名 → <see cref="CardDim"/> 维（紧凑）卡向量。</summary>
    public sealed class CardVecs
    {
        public readonly Dictionary<string, float[]> ByName = new(StringComparer.Ordinal);
        public int Count => ByName.Count;

        /// <summary>压缩后实际保留的关键字/交互/效果位数（v3 实测 = 56）。</summary>
        public int CompactKeywordBits { get; set; }
    }

    /// <summary>
    /// 读 <c>card-vectors.json</c>（1906 张）。
    ///
    /// ★ **v3：在这里把稀疏位压缩掉。** 磁盘上的 `k` 有 81 位，
    /// 实测只有 **56 位**在任何卡上非零（另 25 位恒为 0）。
    /// 输出向量的 `CardDim` 必须是**紧凑后**的 9 + 56 = 65，
    /// 否则 `Encode` 会把恒 0 的位也拼进池化 —— 那正是 v2 浪费 420 维的来源。
    ///
    /// 为什么在**加载时**压而不是重新生成 json：
    /// 磁盘格式保持向后兼容（其它工具还在读那 81 位），
    /// 而且"哪 56 位有用"是**从数据本身推出来的**，不写死。
    /// </summary>
    /// <param name="docsDir">含 <c>card-vectors.json</c> 的目录。</param>
    public static CardVecs LoadCardVectors(string docsDir)
    {
        var r = new CardVecs();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(docsDir, "card-vectors.json")));
        foreach (var prop in doc.RootElement.GetProperty("cards").EnumerateObject())
        {
            var v = prop.Value;
            var a = new float[CardDim];
            int i = 0;
            foreach (var x in v.GetProperty("s").EnumerateArray()) a[i++] = (float)x.GetDouble();       // 9
            // 只取前 11 个关键字位 + 55 交互 + 15 效果标签（跳过 65 个触发位）
            int j = 0;
            foreach (var x in v.GetProperty("k").EnumerateArray())
            {
                if (j >= CardDim - ScalarCount) break;
                a[i++] = (float)x.GetDouble();
                j++;
            }
            r.ByName[prop.Name] = a;
        }
        return r;
    }

    /// <summary>
    /// 把局面编码成 <see cref="Dim"/> 维向量（**未归一化**的原始特征；
    /// 标准化参数 mean/std 属于模型，见 <see cref="NnModel"/>）。
    /// </summary>
    /// <param name="perspective">
    /// 视角方 —— 它的数据排在全局 3 维之后、对手之前。
    /// <c>v[1]</c>（ActiveSide == 视角方）也随它变。
    /// </param>
    public static float[] Encode(GameState st, CardVecs vecs, Side perspective)
    {
        var v = new float[Dim];
        int p = 0;

        // ---------- 全局 3 维（与视角无关）----------
        v[p++] = st.Turn / TurnScale;
        v[p++] = st.ActiveSide == perspective ? 1f : 0f;
        // ⚠️ 「本回合已打出的牌」按回合清空（MatchEngine.StartTurn）。
        //    dump 的快照取在**回合交界**（一方 PlayTurn 结束后），此刻它必然已被
        //    下一方的 StartTurn 清空 ⇒ 训练数据里这一列恒为 0（本轮实测确认，见报告）。
        //    它在推理侧（NNPlay 的一步试算，处在回合中间）是非 0 的。
        v[p++] = st.CardsPlayedThisTurn.Count / 10f;

        // ---------- 视角方在前，对手在后 ----------
        int sideIdx = 0;
        foreach (var side in new[] { perspective, perspective.Opposite() })
        {
            bool isOpponent = sideIdx == 1;
            sideIdx++;
            v[p++] = st.HqDefense(side) / 20f;
            v[p++] = st.Kredits(side) / 12f;
            v[p++] = st.MaxKredits(side) / 12f;
            var cards = st.Cards(side);
            v[p++] = cards.Count(c => c.Location == side.HandOf()) / 10f;
            v[p++] = cards.Count(c => c.Location == side.DeckOf()) / 40f;
            v[p++] = st.Board(side).Count / 10f;

            var zoneLocs = new[] { side.HandOf(), CardLocation.BoardFrontline,
                                   side.HqOf(), CardLocation.Discard, side.DeckOf() };
            for (int zi = 0; zi < zoneLocs.Length; zi++)
            {
                var loc = zoneLocs[zi];
                int vecDim = ZoneVecDims[zi];
                // ⚠️ **对方手牌的卡向量**是隐藏信息，而且它是卡组身份的直接指纹。
                //    实测（每局第一条快照做最近质心认卡组）：
                //      用「双方手牌向量」认**对方**卡组 71.4% 命中；
                //      去掉对方手牌向量   → 5.9%（22 套牌随机 = 4.5%）。
                //    也就是说「认得出对面是谁」这条捷径主要就是从这一格漏出去的。
                //    张数保留（手牌数在真实对局里是公开的），只把**内容**抹成 0。
                //    ⚠️ 抹成 0 而不是「不写」：**槽位必须保留**，否则它后面所有区域的
                //       偏移会整体平移，PerSide/Dim 与实际写出的长度就对不上
                //       （第一版正是这么错的：右方块少写 90 维，右方全部字段错位）。
                //    视角方自己的手牌向量**保留** —— 那是自己本来就看得见的信息。
                bool hideVector = isOpponent && zi == 0;
                // ⚠️ !IsHq 是必须的：半场（BoardHqLeft/Right）那一格里 **HQ 和单位同处**，
                //    不排除的话 HQ 这张"位置卡"会被当成一个单位进池，
                //    它的 90 维卡向量会污染「本方半场」这一区的均值。
                //    （前线/手牌/弃牌堆/牌库里本来就没有 HQ，这条对它们是无害的。）
                var group = cards.Where(c => c.Location == loc && !c.IsHq).ToList();

                if (vecDim > 0)
                {
                    // ⚠️ **这是 v2 的 mean 池化，已实测为最优，不要轻改。**
                    //
                    // 2026-10-02 试过改成 `mean + max` 双份（想让稀疏关键字位可达），
                    // 结果**留出准确率反而变差**：
                    //     v2（mean@90，745 维）        83.739%
                    //     v3（maxmean@65，1065 维）    82.333%
                    //     v3 仅压缩（mean@65，545 维） 81.296%
                    //   ⇒ 两个改动**都伤**（消融确认）。
                    //
                    // 原因：这 90 维里既有 9 个**稠密标量**（费用/攻防/油费）
                    // 又有 81 个**稀疏位**，对整体做 max 时胜出的往往是大数值的标量维度，
                    // 稀疏位**依然被淹没**。
                    //
                    // ⇒ 正确做法是**分段池化**（标量段 mean、位标志段 max），
                    //   不是整体池化。详见 `docs/NN训练诊断.md` 的 v3 失败记录。
                    //   **在做出那个分段版本并实测变好之前，不要动这里。**
                    //
                    // 另：v2 那 420 个恒 0 维度**不携带信息、也不添乱** ——
                    // "浪费参数"不等于"让模型学不好"，我原先把两者混为一谈了。
                    var acc = new float[vecDim];      // hideVector 时恒为全 0
                    if (!hideVector)
                    {
                        foreach (var c in group)
                        {
                            if (vecs.ByName.TryGetValue(c.Definition.Name, out var cv))
                            {
                                for (int i = 0; i < vecDim; i++) acc[i] += cv[i];
                            }
                        }
                        if (group.Count > 0)
                        {
                            for (int i = 0; i < vecDim; i++) acc[i] /= group.Count;
                        }
                    }
                    Array.Copy(acc, 0, v, p, vecDim);
                    p += vecDim;
                }
                // vecDim == 0（v2 的牌库区）：**不写卡向量**，只写张数。
                // 理由见类注释：整副牌的池化向量是卡组的唯一指纹，会开出「背对位」这条
                // 与打法无关的捷径；而牌库内容本来就是隐藏信息。
                v[p++] = group.Count / 10f;
            }
        }
        return v;
    }

    //  ==================== 偏移（供诊断脚本/工具对照，改布局时同步改这里）====================
    //
    //  ★ v3 布局（dim = 1065 = 3 + 531 × 2）
    //
    //  v[0]              Turn/30
    //  v[1]              ActiveSide == perspective
    //  v[2]              CardsPlayedThisTurn/10
    //  v[3 + 0..5]       视角方：hqDef kredits maxKredits handCount deckCount boardCount
    //
    //  视角方块起始 = 3，每方 **531** 维（= 6 全局 + 4×130 卡向量 + 5 张数）。
    //  区内偏移（相对于方块起点）：
    //      Hand      +6  .. +135  卡向量（130 = mean 65 在前 + max 65 在后），张数 +136
    //      Frontline +137 .. +266，张数 +267
    //      OwnHalf   +268 .. +397，张数 +398
    //      Discard   +399 .. +528，张数 +529
    //      Deck      张数 +530（**无卡向量**）
    //
    //  对手块起始 = 3 + 531 = 534，内部偏移同上。
    //  ⇒ 视角方 hqDef = v[3]，对手 hqDef = v[534]。
    //
    //  ⚠️ 对手手牌那一格的卡向量**保留但恒为 0**（隐藏信息 + 卡组指纹）——
    //     保留槽位是为了让所有偏移固定，见 Encode 里的 hideVector。
    //
    //  ⚠️ v2（745 维）的旧偏移**已失效**。诊断脚本不要写死数字，
    //     用 `StateEncoder.PerSide` / `OffOpposite`，或从模型文件读 perSide。
}
