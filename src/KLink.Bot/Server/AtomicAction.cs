using System.Text;
using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Server;

/// <summary>
/// 引擎的**一个原子动作**（出牌 / 攻击 / 移动 / 结束回合）。
///
/// 为什么需要它：内核**没有**「状态副本 / 试算后回滚」的能力
/// （<c>GameState.Snapshot()</c> 只够对拍，不足以还原 —— 缺 buff 明细、
/// 疲劳、前线限制者集合、随机流位置、Kismet VM 状态）。
/// 但内核是**确定性**的（所有随机走 <c>State.Random</c>，同一份输入 + 同一个种子
/// ⇒ 完全相同的输出，见 <c>DeterministicRandom</c> 的注释），
/// 所以「把动作流水账重放一遍」就是一份**语义等价的状态副本** ——
/// 不需要改内核，也不会有「克隆漏了某个字段」的静默错误。
///
/// 动作流水的来源有两条：
/// <list type="bullet">
/// <item>NN 自己走的每一步 —— 直接记下来</item>
/// <item>对手（<c>GreedyBot</c>）走的一整个回合 —— 从 <c>State.ActionLog</c> 反推
///   （<c>XActionPlayCardFromHand</c> / <c>XActionAttackCard</c> /
///    <c>XActionMoveCardToLine</c> / <c>XActionEndOfTurn</c> 这四类，
///    出牌目标与移动槽位在紧跟着的子动作里）</item>
/// </list>
/// </summary>
public abstract record AtomicAction
{
    /// <summary>
    /// 这条动作**发生当时**的回合号与行动方。
    ///
    /// 从 <c>State.ActionLog</c> 反推时填（那里有 <c>GameAction.TurnNumber</c> / <c>Player</c>），
    /// 直接构造时为 0 / NotAvailable。
    ///
    /// ⚠️ 为什么需要：对手一整个回合是**打完之后**才反推的，
    ///    那时 <c>State.Turn</c> / <c>ActiveSide</c> 已经翻到下一方了 ——
    ///    直接读实时状态会把「对手 T2 出的牌」标成「T3」。
    /// </summary>
    public int LogTurn { get; init; }

    /// <summary>见 <see cref="LogTurn"/>。</summary>
    public Side LogSide { get; init; }

    /// <summary>应用到引擎。返回 false = 这一步在当前局面下不合法（候选会被丢弃）。</summary>
    public abstract bool Apply(MatchEngine engine);

    /// <summary>
    /// 给人看的**完整**描述（含位置与攻防）——
    /// 只有在「局面就是这条动作发生前的局面」时才准确（NN 决策表用）。
    /// </summary>
    public abstract string Describe(GameState st);

    /// <summary>
    /// 只写动作 + 卡名的描述 —— **什么时候看都准确**（卡名不随状态变），
    /// 用于动作流水：对手那一串是事后反推的，位置/数值早变了。
    /// </summary>
    public abstract string DescribeName(GameState st);

    /// <summary>日志里用的短标签。</summary>
    public abstract string Kind { get; }

    protected static CardInstance? Maybe(GameState st, int id)
        => id == 0 ? null : st.ById(id);

    protected static string NameOf(GameState st, int id)
        => st.ById(id)?.Name ?? $"#{id}";
}

/// <summary>从手牌打出一张牌。<paramref name="TargetId"/> = 0 表示不需要目标。</summary>
public sealed record PlayCardAction(int CardId, int TargetId) : AtomicAction
{
    public override string Kind => "出牌";

    public override bool Apply(MatchEngine engine)
    {
        var card = engine.State.ById(CardId);
        if (card is null) return false;
        var target = Maybe(engine.State, TargetId);
        return engine.PlayCard(card, target);
    }

    public override string Describe(GameState st)
    {
        var card = Maybe(st, CardId);
        var target = Maybe(st, TargetId);
        return $"出牌 {card?.ToString() ?? $"#{CardId}"}"
             + (target is null ? "" : $"  目标 {target}");
    }

    public override string DescribeName(GameState st)
        => $"出牌 {NameOf(st, CardId)}"
         + (TargetId == 0 ? "" : $" → {NameOf(st, TargetId)}");
}

/// <summary>单位攻击目标。</summary>
public sealed record AttackAction(int AttackerId, int DefenderId) : AtomicAction
{
    public override string Kind => "攻击";

    public override bool Apply(MatchEngine engine)
    {
        var a = engine.State.ById(AttackerId);
        var d = engine.State.ById(DefenderId);
        if (a is null || d is null) return false;
        return engine.Attack(a, d);
    }

    public override string Describe(GameState st)
    {
        var a = Maybe(st, AttackerId);
        var d = Maybe(st, DefenderId);
        string dt = d is null ? $"#{DefenderId}" : d.IsHq ? $"{d.Owner.ToWire()} HQ" : d.ToString();
        return $"攻击 {a?.ToString() ?? $"#{AttackerId}"} → {dt}";
    }

    public override string DescribeName(GameState st)
    {
        var d = Maybe(st, DefenderId);
        string dt = d is not null && d.IsHq ? $"{d.Owner.ToWire()} HQ" : NameOf(st, DefenderId);
        return $"攻击 {NameOf(st, AttackerId)} → {dt}";
    }
}

/// <summary>把单位推到前线（或在前线内挪槽位）。</summary>
public sealed record MoveAction(int UnitId, int Slot) : AtomicAction
{
    public override string Kind => "移动";

    public override bool Apply(MatchEngine engine)
    {
        var unit = engine.State.ById(UnitId);
        return unit is not null && engine.MoveUnit(unit, Slot);
    }

    public override string Describe(GameState st)
        => $"移动 {Maybe(st, UnitId)?.ToString() ?? $"#{UnitId}"} → 前线槽位 {Slot}";

    public override string DescribeName(GameState st)
        => $"移动 {NameOf(st, UnitId)} → 前线槽位 {Slot}";
}

/// <summary>结束当前行动方的回合。</summary>
public sealed record EndTurnAction : AtomicAction
{
    public override string Kind => "结束回合";

    public override bool Apply(MatchEngine engine)
    {
        engine.EndTurn(engine.State.ActiveSide);
        return true;
    }

    public override string Describe(GameState st) => $"结束 {st.ActiveSide.ToWire()} 回合";

    public override string DescribeName(GameState st)
        => $"结束 {(LogSide == Side.NotAvailable ? st.ActiveSide : LogSide).ToWire()} 回合";
}

/// <summary>
/// **选牌答复** —— 对应线协议里的 `CS`（`XActionCardToDrawSelected`）。
///
/// ## 为什么要有这个类型（2026-10-02 从真回放查出来的 bug）
///
/// 「开发」这类效果让玩家**从 3 张候选里挑 1 张**，客户端用一条 `CS` 把选择
/// 告诉对方：
/// <code>
///   CS { "0": 触发选牌的卡 cardID, "1": 候选下标, "2": 选中卡的 deck code }
/// </code>
///
/// 实测 `out/_server-replays/replay-630801`：**人类发了 3 条 CS，bot 一条都没有** ——
/// 因为 `AtomicAction` 只有出牌/攻击/移动/结束回合，**根本产不出 `CS`**。
/// 内核里 `CardApiDispatch` 确实选了（退化成候选表第一张），
/// 但那个选择**从不发给客户端** ⇒ 客户端看到的是一张**没有任何选择**的开发牌。
///
/// 用户报的「AI 不会选开发」，根因就是这个 —— **缺一条协议，不是 AI 强弱**。
///
/// ## 关于 `Apply`
///
/// 在**重建路径**上，`CS` 的落实是由 `MatchEngine.PickCardToDraw` 在
/// `PC` 结算时取走的（见 `ReplayRunner` 的 `csQueue`）。
/// 所以本动作被 apply 时**不该再改状态** —— 它只是把动作流补齐，
/// 让「发出的动作」与「客户端记录的动作」一致。
/// </summary>
public sealed record ChooseCardAction(int SelectingCardId, int Index, string Code) : AtomicAction
{
    public override string Kind => "选牌";

    /// <summary>`CS` 不自己改状态 —— 答复已由效果的 `PickCardToDraw` 落实。</summary>
    public override bool Apply(MatchEngine engine) => true;

    public override string Describe(GameState st)
        => $"选牌 {NameOf(st, SelectingCardId)} 第 {Index} 个候选（码 {Code}）";

    public override string DescribeName(GameState st)
        => $"选牌 {NameOf(st, SelectingCardId)} 第 {Index} 个候选（码 {Code}）";
}

/// <summary>
/// 动作流水账 + 「重放出来的状态副本」。
/// </summary>
public static class ActionJournal
{
    /// <summary>
    /// 从 <paramref name="engine"/> 的动作日志里，把 <paramref name="from"/> 之后的
    /// **原子动作**反推出来（用于记录对手 GreedyBot 一整个回合做了什么）。
    /// </summary>
    public static List<AtomicAction> Derive(GameState st, int from)
    {
        var result = new List<AtomicAction>();
        var log = st.ActionLog;

        for (int i = from; i < log.Count; i++)
        {
            var a = log[i];
            switch (a.ActionType)
            {
                case "XActionPlayCardFromHand":
                {
                    int cardId = Int(a, "cardID");
                    int targetId = 0;
                    // 紧随其后的子动作携带 targetCardID（PlayCard 里 RecordAction → FireSubAction 相邻）
                    if (i + 1 < log.Count && TrySub(log[i + 1], out var sub)
                        && sub.Name is "ZActionPlayCardFromHand" or "ZActionPlayOrderCardFromHand")
                    {
                        targetId = SubInt(sub, "targetCardID");
                    }
                    result.Add(new PlayCardAction(cardId, targetId) { LogTurn = a.TurnNumber, LogSide = a.Player });
                    break;
                }

                case "XActionAttackCard":
                    result.Add(new AttackAction(Int(a, "attackerCardID"), Int(a, "defenderCardID"))
                    {
                        LogTurn = a.TurnNumber,
                        LogSide = a.Player,
                    });
                    break;

                case "XActionMoveCardToLine":
                {
                    int cardId = Int(a, "cardID");
                    int slot = 0;
                    if (i + 1 < log.Count && TrySub(log[i + 1], out var sub)
                        && sub.Name == "ZActionMoveCardToNewLocation")
                    {
                        slot = SubInt(sub, "locationNumber");
                    }
                    result.Add(new MoveAction(cardId, slot) { LogTurn = a.TurnNumber, LogSide = a.Player });
                    break;
                }

                case "XActionEndOfTurn":
                    result.Add(new EndTurnAction { LogTurn = a.TurnNumber, LogSide = a.Player });
                    break;
            }
        }

        return result;
    }

    private static bool TrySub(GameAction a, out SubAction sub)
    {
        if (a.ActionType == "SubAction" && a.SubActions.Count > 0)
        {
            sub = a.SubActions[0];
            return true;
        }
        sub = null!;
        return false;
    }

    private static int Int(GameAction a, string key)
        => a.ActionData.TryGetValue(key, out var v) && v is not null ? Convert.ToInt32(v) : 0;

    private static int SubInt(SubAction s, string key)
    {
        foreach (var v in s.Values)
        {
            if (v.Name == key) return v.Value;
        }
        return 0;
    }

    /// <summary>
    /// **状态指纹** —— 重放出来的副本与实时引擎必须逐字相同，否则说明重放不可信。
    ///
    /// 组成：完整快照 JSON（回合/行动方/kredit/前线归属/每张卡的攻防位置关键字…）
    ///      + 随机流位置（<c>Random.State</c>，最能反映"调用序列是否一致"）
    ///      + 双方疲劳计数 + 本回合打出过的牌数 + 动作日志条数。
    /// </summary>
    public static string Fingerprint(MatchEngine e)
    {
        var st = e.State;
        var sb = new StringBuilder(st.SnapshotJson().Length + 64);
        sb.Append(st.SnapshotJson());
        sb.Append("|rng=").Append(st.Random.State);
        sb.Append("|fat=").Append(st.Fatigue(Side.Left)).Append(',').Append(st.Fatigue(Side.Right));
        sb.Append("|played=").Append(st.CardsPlayedThisTurn.Count);
        sb.Append("|log=").Append(st.ActionLog.Count);
        return sb.ToString();
    }
}

/// <summary>
/// 「把流水账重放成一个新引擎」—— 这就是本工具的状态副本机制。
///
/// 用法：<c>Build()</c> 造一个与实时引擎**完全同状态**的新引擎；
/// 在上面试算候选动作不会碰到实时引擎（各跑各的）。
/// </summary>
public sealed class Replayer
{
    private readonly CardDatabase _db;
    private readonly IReadOnlyList<string> _left;
    private readonly IReadOnlyList<string> _right;
    private readonly ulong _seed;

    public Replayer(CardDatabase db, IReadOnlyList<string> left, IReadOnlyList<string> right, ulong seed)
    {
        _db = db;
        _left = left;
        _right = right;
        _seed = seed;
    }

    public int Builds { get; private set; }
    public double BuildMs { get; private set; }
    public long StepsReplayed { get; private set; }

    /// <summary>
    /// **在 journal 之前**把引擎摆到「本回合开始时的真实局面」的回调。
    ///
    /// ## 为什么必须有它（这是个真 bug 的修复）
    ///
    /// `Build` 原来是 `new MatchEngine(...) → Start() → 重放 journal`。
    /// 那个流程只对**从头打到底**的自对弈成立（`NNPlay` 就是这种：
    /// 它拥有整局的 journal，所以 `Start()` 之后重放 journal 恰好等于实时局面）。
    ///
    /// 但**服务器是中途接管的**：它拿到的是一局已经在进行的对局，
    /// journal 里只有「本回合到目前为止的几步」，前面几百条动作**不在里面**。
    /// 于是 `Build` 造出来的是**一局毫不相干的新游戏**。
    ///
    /// 实测症状（`ServerDeckProbe` / 真对局日志）：
    /// <code>
    /// 右方手牌（live 引擎）: #42 card_unit_panzer_ii_a(费1), #43 iron_from_the_north …
    /// tryEngine 里的 #42    : card_location_london        ← 另一局游戏
    /// → 枚举 8~16 个候选，**合法只有 1 个**（就是"结束回合"），其余全被拒
    /// → AI 一张牌都打不出去，手牌从 6 涨到 9
    /// </code>
    /// 对照（`NNPlay`，journal 完整）：枚举 12 → 合法 12、**拒绝 0**。
    ///
    /// ## 约定
    ///
    /// 由调用方提供闭包，内部**必须走和主重建完全同一条路径**
    /// （同种子、同批卡、同串动作）—— 否则副本与 live 会漂开。
    /// 本类不自己拼装引擎，就是为了避免"第二套重建逻辑"这种漂移源。
    ///
    /// 返回一个**已经摆在「本回合开始时真实局面」**的新引擎。
    /// 调用方负责一切（种子、卡、动作流）—— 本类不自己拼装，
    /// 就是为了避免"第二套重建逻辑"这种漂移源。
    /// </summary>
    public Func<MatchEngine>? Prelude { get; set; }

    /// <summary>重放 <paramref name="journal"/> 造出一个与实时引擎同状态的新引擎。</summary>
    public MatchEngine Build(IReadOnlyList<AtomicAction> journal)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Builds++;

        // 服务器场景：`Prelude` 负责把引擎摆到当前真实局面。
        // 自对弈（NNPlay）：不设 `Prelude`，走「从头 Start + 重放整局 journal」，
        //                   行为与改动前**完全一致**。
        MatchEngine e;
        if (Prelude is not null)
        {
            e = Prelude();
        }
        else
        {
            e = new MatchEngine(_db, _left, _right, _seed);
            e.Start();
        }

        foreach (var a in journal)
        {
            a.Apply(e);
            StepsReplayed++;
        }

        BuildMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return e;
    }

    public string Stats()
        => $"重放 {Builds:N0} 次，累计 {StepsReplayed:N0} 步，{BuildMs / 1000.0:F1}s"
         + $"（平均 {BuildMs / Math.Max(1, Builds):F1} ms/次）";
}
