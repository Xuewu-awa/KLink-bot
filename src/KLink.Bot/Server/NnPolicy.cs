using KLink.Bot.Bots;
using KLink.Bot.Engine;
using KLink.Bot.NN;

namespace KLink.Bot.Server;

/// <summary>一个候选动作 + 模型给它的胜率。</summary>
public sealed record ScoredAction(AtomicAction Action, float WinProb, bool Terminal, string Note)
{
    /// <summary>试算后是否直接分出了胜负（<see cref="Terminal"/> 为真时才有意义）。</summary>
    public bool WinsGame { get; init; }
}

/// <summary>一次决策的完整记录（用于日志与事后检查）。</summary>
public sealed record Decision(
    int Index,
    Side Side,
    int Turn,
    float CurrentWinProb,
    IReadOnlyList<ScoredAction> Candidates,
    ScoredAction Chosen,
    int Rejected,
    int Enumerated)
{
    public int LegalCount => Candidates.Count;
}

/// <summary>
/// NN 玩家：**枚举所有合法动作 → 逐个试算 → 编码结果局面 → 模型算胜率 → 取最大**。
///
/// 三个实现要点：
/// <list type="number">
/// <item>
///   <b>试算靠重放，不靠克隆。</b> 内核没有状态副本能力（见 <see cref="AtomicAction"/> 注释），
///   所以每个候选用「把动作流水账重放一遍」造一份独立状态。
/// </item>
/// <item>
///   <b>合法性交给内核判。</b> 这里只枚举一个**超集**（手上的牌 × 可能的目标、
///   能动的单位 × 内核给的目标、能动的单位 × 所有前线槽位、结束回合），
///   真正能不能做由 <c>PlayCard</c> / <c>Attack</c> / <c>MoveUnit</c> 的返回值决定 ——
///   被拒的候选丢掉并计数。**不在这里重写一份判据**（重写就是第二份规则，早晚漂移）。
/// </item>
/// <item>
///   <b>编码与训练同一份代码</b>（<see cref="StateEncoder"/>），视角 = NN 自己那一方，
///   模型输出的就是「NN 获胜概率」。
/// </item>
/// </list>
///
/// ⚠️ 这是 **1 层前瞻**（one-ply）：只试算「走这一步之后」的局面，不搜索后续。
///
/// ⚠️⚠️ **分布问题（重要）**：训练数据里的每一个局面都取在**回合交界处**
/// （NNTrain dump 是在一方 `PlayTurn` 整个回合打完之后才编码的）。
/// 而「走一步之后的局面」是**回合中间**的局面 —— 手牌少一张、kredit 少几点，
/// 训练时几乎没见过。所以一步试算的编码其实是**分布外**的。
/// <see cref="_rollout"/> = true 时改成：应用候选之后，**让 GreedyBot 把本方这个回合走完**，
/// 再编码 —— 这样编码又落回「回合交界」的分布里。
/// 两种都给出来做对比，不替模型掩饰。
/// </summary>
public sealed class NnPolicy
{
    private readonly NnModel _model;
    private readonly StateEncoder.CardVecs _vecs;
    private readonly Side _side;
    private readonly bool _allowMoves;
    private readonly bool _rollout;

    private readonly float[] _scratch = new float[StateEncoder.Dim];
    private readonly float[] _norm = new float[StateEncoder.Dim];

    public NnPolicy(NnModel model, StateEncoder.CardVecs vecs, Side side, bool allowMoves, bool rollout)
    {
        _model = model;
        _vecs = vecs;
        _side = side;
        _allowMoves = allowMoves;
        _rollout = rollout;
    }

    /// <summary>当前局面下，模型认为 NN 这一方的胜率。</summary>
    public float Evaluate(GameState st)
    {
        StateEncoder.Encode(st, _vecs, _side).CopyTo(_scratch, 0);
        _model.NormalizeInto(_scratch, _norm);
        return _model.ForwardNormalized(_norm);
    }

    public float Evaluate(MatchEngine e) => Evaluate(e.State);

    /// <summary>
    /// 枚举候选动作。
    ///
    /// **先用内核自己的谓词粗筛**（<c>CanPlay</c> / <c>CanOperateThisTurn</c> /
    /// <c>CanMoveThisTurn</c> —— 都是内核公开的 API，不是在这里重写规则），
    /// 只是为了少做无谓的重放；**最终合法性仍以 <c>Apply</c> 的返回值为准**
    /// （<c>PlayCard</c> / <c>Attack</c> / <c>MoveUnit</c>），被拒的候选丢弃并计数。
    /// </summary>
    public List<AtomicAction> Enumerate(MatchEngine engine)
    {
        var st = engine.State;
        var list = new List<AtomicAction>();

        // ---- ① 出牌：手上的每张牌 ×（需要的目标） ----
        foreach (var card in st.Hand(_side))
        {
            if (!engine.CanPlay(card, out _))
            {
                continue;   // 内核判的不能打（费用不够 / 半场满 / 不在手牌）
            }

            if (NeedsTarget(card))
            {
                // ★★ 候选 = **客户端的门判过的合法目标**（2026-10-02）。
                //
                // 旧实现是 `TargetCandidates(engine)` = 「敌方场上单位 + 敌方 HQ」这个
                // **超集**，注释里自己承认「这是本工具最弱的一环：内核里没有
                // 『某张牌能指哪些目标』的表」。后果有两类，都是玩家实测到的：
                //   · 只能指**空军**的牌（`card_event_aa_barrage`）被指到地面单位上；
                //   · 只能指**老兵**的牌（`card_event_breakout`）被指到非老兵上；
                //   · 只能指**友方**的牌（`card_event_tactical_withdrawal` /
                //     `card_event_air_corps_ferrying`）**一个合法候选都没有**，
                //     而旧代码不跳过、拿"攻击力最高的敌方单位"硬顶。
                // 客户端收到非法目标后**静默不执行** ⇒ 记牌器 +1、场上无变化
                // = 玩家报告的「虚空牌」第三个来源。
                //
                // 现在改成逐张候选过客户端的两道门
                //（卡自己的 `CanPlayFromHand` + 规则库的 `CanSelectAsTarget`，
                //  见 `CardApi.CanTarget` / `MatchEngine.LegalPlayTargets`）。
                // 一个合法目标都没有 ⇒ 这张牌枚举不出动作（和 GreedyBot 一样跳过）。
                foreach (var t in engine.LegalPlayTargets(card))
                {
                    list.Add(new PlayCardAction(card.CardId, t.CardId));
                }
            }
            else
            {
                list.Add(new PlayCardAction(card.CardId, 0));
            }
        }

        // ---- ② 攻击：内核自己给的合法目标 ----
        foreach (var unit in st.Board(_side))
        {
            if (!unit.CanOperateThisTurn(st))
            {
                continue;
            }

            foreach (var target in engine.LegalTargets(unit))
            {
                list.Add(new AttackAction(unit.CardId, target.CardId));
            }
        }

        // ---- ③ 移动：推到前线 / 前线内挪槽位 ----
        if (_allowMoves)
        {
            int cap = st.FrontlineCapacity;
            foreach (var unit in st.Board(_side))
            {
                if (!unit.CanMoveThisTurn(st))
                {
                    continue;
                }

                for (int slot = 0; slot < cap; slot++)
                {
                    list.Add(new MoveAction(unit.CardId, slot));
                }
            }
        }

        // ---- ④ 结束回合（永远是一个选项） ----
        list.Add(new EndTurnAction());
        return list;
    }

    /// <summary>
    /// 做一次决策：对每个候选试算 + 打分，取胜率最高的。
    /// </summary>
    public Decision Choose(MatchEngine live, IReadOnlyList<AtomicAction> journal, Replayer replayer,
                           int index, bool verifyEveryReplay)
    {
        var st = live.State;
        var candidates = Enumerate(live);
        var scored = new List<ScoredAction>(candidates.Count);
        int rejected = 0;

        string livePrint = ActionJournal.Fingerprint(live);

        foreach (var action in candidates)
        {
            var tryEngine = replayer.Build(journal);

            if (verifyEveryReplay && ActionJournal.Fingerprint(tryEngine) != livePrint)
            {
                throw new InvalidOperationException(
                    "重放出来的状态与实时引擎不一致 —— 试算结果不可信，拒绝继续。"
                    + "（说明内核里存在不经过 State.Random 的随机、或依赖遍历顺序/时间）");
            }

            if (!action.Apply(tryEngine))
            {
                rejected++;

                // ⚠️ 记下**拒绝原因**（诊断用）。
                //
                // 为什么需要：`合法 1 / 拒绝 15` 这种数字只说明"大部分候选不可行"，
                // 但分不清是"费用不够"、"不在手牌"、"召唤失调"还是别的新加的门。
                // 实测踩过：只报计数时完全看不出为什么一张牌都打不出。
                if (Diagnostics is not null && Diagnostics.Count < RejectLogLimit)
                {
                    // ⚠️ 关键：用 **tryEngine**（`Apply` 真正操作的那个）复查，
                    //    不是 `live`。两者理论上逐字节相同，但若诊断查 live 而
                    //    Apply 拒了 tryEngine，就会看到"CanPlay 说能打、Apply 说不能"
                    //    这种自相矛盾的输出（实测踩过：原因字段是空的）。
                    string why = DescribeRejection(action, tryEngine);
                    lock (Diagnostics)
                    {
                        if (Diagnostics.Count < RejectLogLimit)
                        {
                            Diagnostics.Add(why);
                        }
                    }
                }

                continue;
            }

            // 可选：让 GreedyBot 把本方这个回合走完，再编码 —— 把局面拉回
            // 「回合交界」这个训练分布（见类注释里的 ⚠️⚠️）。
            if (_rollout && !tryEngine.State.IsFinished && tryEngine.State.ActiveSide == _side)
            {
                new GreedyBot("rollout").PlayTurn(tryEngine, _side);
            }

            bool terminal = tryEngine.State.IsFinished;
            bool wins = terminal && tryEngine.State.Winner == _side;
            string note = terminal
                ? (wins ? $"⚑ 直接获胜（{tryEngine.State.WinnerReason}）" : "⚑ 直接落败")
                : "";
            if (_rollout && !terminal && action is not EndTurnAction)
            {
                note = "（已用 GreedyBot 走完本回合后估值）";
            }

            scored.Add(new ScoredAction(action, Evaluate(tryEngine), terminal, note) { WinsGame = wins });
        }

        if (scored.Count == 0)
        {
            throw new InvalidOperationException("一个合法动作都没有 —— 连结束回合都被拒了，内核行为异常");
        }

        // argmax；同分取先枚举到的（确定性 ⇒ 整局可复现）
        var chosen = scored[0];
        foreach (var s in scored)
        {
            if (s.WinProb > chosen.WinProb) chosen = s;
        }

        return new Decision(index, _side, st.Turn, Evaluate(live), scored, chosen, rejected, candidates.Count);
    }

    /// <summary>
    /// 候选被拒的**原因**收集器（诊断用，可选）。
    ///
    /// 实测需要它：日志里只看到 `合法 1 / 拒绝 15`，完全判断不出为什么
    /// **手牌 9 张却一个动作都做不了**。有了原因一眼就能看出是费用、位置还是别的门。
    /// </summary>
    public List<string>? Diagnostics { get; set; }

    private const int RejectLogLimit = 12;

    /// <summary>把一个被拒的候选描述成「动作 + 内核给的原因」。</summary>
    private string DescribeRejection(AtomicAction action, MatchEngine live)
    {
        var st = live.State;
        string Name(int id) => st.ById(id) is { } c
            ? $"{c.Name}#{c.CardId}(费{c.KreditCost}/油{c.OperationCost})"
            : $"#{id}";

        switch (action)
        {
            case PlayCardAction p:
                {
                    var card = st.ById(p.CardId);
                    if (card is null)
                    {
                        return $"出牌 #{p.CardId} 被拒：**这张卡不在引擎里**";
                    }

                    // ⚠️ 先直接报 `CanPlay` 的判据。若它返回 **true** 而 `Apply` 仍失败，
                    //    说明拒绝发生在 `CanPlay` **之后**（`PlayCard` 内部或更深），
                    //    那就是另一类 bug —— 光看 reason 是看不出来的。
                    bool can = live.CanPlay(card, out string reason);
                    return $"出牌 {Name(p.CardId)} 被拒：CanPlay={can} reason='{reason}' " +
                           $"| owner={card.Owner} 行动方={st.ActiveSide} " +
                           $"location={card.Location} 期望={_side.HandOf()} " +
                           $"费={card.KreditCost} 油={card.OperationCost} " +
                           $"kredit={st.Kredits(_side)}/{st.MaxKredits(_side)} " +
                           $"活着={card.IsAlive} 单位={card.Definition.IsUnit}";
                }

            case AttackAction a:
                {
                    // ⚠️ 用 `Attack(..., out reason)` 拿**真实原因**。
                    //    以前只打印"攻方在场/可行动"，说不出是哪道门
                    //    （射程/守护/烟幕/压制/油费）—— 排查成本极高。
                    var atk = st.ById(a.AttackerId);
                    var dfd = st.ById(a.DefenderId);
                    string why = atk is null || dfd is null
                        ? "找不到攻方或目标"
                        : (live.Attack(atk, dfd, out string r) ? "（居然成功了？）" : r);
                    return $"攻击 {Name(a.AttackerId)} → {Name(a.DefenderId)} 被拒：{why}";
                }

            case MoveAction m:
                {
                    // ⚠️ 用 `MoveUnit(..., out reason)` 拿**真实原因**（与 `Attack` 同构）。
                    //    以前只打印"可移动/油费/前线归属"，说不出是哪道门。
                    var u = st.ById(m.UnitId);
                    string mvWhy = u is null
                        ? "找不到单位"
                        : (live.MoveUnit(u, m.Slot, out string r) ? "（居然成功了？）" : r);
                    return $"移动 {Name(m.UnitId)} 被拒：{mvWhy}" +
                           $"（可移动={u?.CanMoveThisTurn(st)} 油费={u?.OperationCost} " +
                           $"kredit={st.Kredits(_side)} 前线归属={st.FrontlineOwner}）";
                }

            default:
                return $"{action.Kind} 被拒";
        }
    }

    /// <summary>
    /// 需要目标的牌 —— 判据是**卡定义里调用了 <c>GetTargetedCard</c>**，
    /// 与 <c>GreedyBot.NeedsTarget</c> 同源（那是卡数据的事实，不是规则判据）。
    /// </summary>
    public static bool NeedsTarget(CardInstance card)
        => card.Definition.ExternalCalls.Contains("GetTargetedCard", StringComparer.Ordinal);

    /// <summary>
    /// ~~可指目标的全集：敌方场上单位 + 敌方 HQ~~
    /// —— **已废弃（2026-10-02）**，被 <see cref="MatchEngine.LegalPlayTargets"/> 取代。
    ///
    /// 保留这个方法名是为了让"它曾经存在过、而且为什么不够"这件事留在代码里：
    /// 它给的是**超集**（只有敌方、没有类型判据），而真实游戏里每张牌的合法目标
    /// 各不相同。`NnPolicy.Enumerate` 现在不再调它。
    /// </summary>
    [Obsolete("用 MatchEngine.LegalPlayTargets（客户端两道门判过的合法目标）")]
    private List<CardInstance> TargetCandidates(MatchEngine engine)
    {
        var st = engine.State;
        var enemy = _side.Opposite();
        var list = st.Board(enemy).Where(c => c.IsAlive).ToList();
        list.Add(st.Hq(enemy));
        return list;
    }
}
