using KLink.Bot.Engine;

namespace KLink.Bot.Bots;

/// <summary>一个决策策略。将来神经网络也是实现这个接口。</summary>
public interface IPlayerPolicy
{
    /// <summary>执行该方的一整个回合，返回是否正常结束（false 表示对局已结束）。</summary>
    bool PlayTurn(MatchEngine engine, Side side);

    string Name { get; }
}

/// <summary>
/// 贪心策略（baseline）。规则很简单，目的只是让对局能跑完、能产出数据：
/// 1. 能打牌就打（费用高的优先，单位优先于指令）
/// 2. 能攻击就攻击（优先打得死的、其次是 HQ）
/// 3. 没得做就结束回合
///
/// 这就是 goal.txt 里说的「简单决策树」，也是将来神经网络要打败的基准。
/// </summary>
public sealed class GreedyBot : IPlayerPolicy
{
    private readonly int _maxActionsPerTurn;

    public GreedyBot(string name = "greedy", int maxActionsPerTurn = 200)
    {
        Name = name;
        _maxActionsPerTurn = maxActionsPerTurn;
    }

    public string Name { get; }

    public bool PlayTurn(MatchEngine engine, Side side)
    {
        var state = engine.State;
        int guard = 0;

        while (!state.IsFinished && guard++ < _maxActionsPerTurn)
        {
            if (state.ActiveSide != side)
            {
                return true;
            }

            if (TryPlayCard(engine, side))
            {
                continue;
            }

            if (TryAttack(engine, side))
            {
                continue;
            }

            if (TryMove(engine, side))
            {
                continue;
            }

            engine.EndTurn(side);
            return !state.IsFinished;
        }

        return !state.IsFinished;
    }

    private static bool TryPlayCard(MatchEngine engine, Side side)
    {
        var state = engine.State;
        // 单位优先（能站场），其次按费用从高到低
        var playable = state.Hand(side)
            .Where(c => engine.CanPlay(c, out _))
            .OrderByDescending(c => c.Definition.IsUnit)
            .ThenByDescending(c => c.KreditCost)
            .ToList();

        foreach (var card in playable)
        {
            var target = ChooseTarget(engine, card, side);
            // 需要目标但选不到目标的牌先不打
            if (NeedsTarget(card) && target is null)
            {
                continue;
            }

            if (engine.PlayCard(card, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAttack(MatchEngine engine, Side side)
    {
        var state = engine.State;
        foreach (var unit in state.Board(side).Where(u => u.CanOperateThisTurn(state)).ToList())
        {
            var targets = engine.LegalTargets(unit).ToList();
            if (targets.Count == 0)
            {
                continue;
            }

            // 优先能一击打死的单位，其次打 HQ
            var kill = targets
                .Where(t => !t.IsHq && t.Defense <= unit.Attack)
                .OrderByDescending(t => t.Attack)
                .FirstOrDefault();

            var chosen = kill ?? targets.FirstOrDefault(t => t.IsHq) ?? targets[0];

            if (engine.Attack(unit, chosen))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 推进前线 —— 对手侧以前是 **空实现**（`return false`），后果不只是"对手弱一点"：
    ///
    /// 引擎里单位默认部署到**半场**，而跨前线攻击要 <c>range &gt;= 2</c>
    /// （见 `MatchEngine.CanReachAcrossFrontline`）。所以不推前线就几乎没有输出。
    /// 于是自对弈里 **只有一侧会上前线**，前线争夺成了单方面行为 ——
    /// 自对弈数据、NNPlay 的胜率、所有"前线相关"的结论都建立在这个不对称上
    /// （`klink bot/docs/NN训练诊断.md` r9 §5 已把它列为限制 1）。
    ///
    /// 这里的决策刻意保持「贪心」：**不评估、不搜索**，只做一条规则 ——
    /// 挑攻击力最高、能负担行动费的单位，推到第一个空槽位。
    /// 目的不是让对手变聪明，而是让**两侧的规则机会对等**，从根上消掉那个不对称。
    ///
    /// 成本已由 `MoveUnit` 收（`OperationCost`），这里再挡一道是为了不去试
    /// 明知会被拒的动作（保持"每次成功返回都真的动了一步"这个契约）。
    /// </summary>
    private static bool TryMove(MatchEngine engine, Side side)
    {
        var state = engine.State;

        // 前线互斥：对面占着就谁都推不进去（MoveUnit 里同样的门）。
        if (state.FrontlineOwner != Side.NotAvailable && state.FrontlineOwner != side)
        {
            return false;
        }

        // 已在场上的单位里，按「先能动的、再攻击力高的」排序 ——
        // 和 TryAttack 一样只看当前值，不做前瞻。
        foreach (var unit in state.Board(side)
                     .Where(u => u.CanMoveThisTurn(state))
                     .OrderByDescending(u => u.Attack)
                     .ToList())
        {
            if (unit.OperationCost > state.Kredits(side))
            {
                continue;   // 换下一个更便宜的单位试试，别直接放弃整个回合的移动
            }

            int slot = FirstFreeFrontlineSlot(state, unit);
            if (slot < 0)
            {
                return false;   // 前线满了，谁都推不进去
            }

            if (engine.MoveUnit(unit, slot))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 找一个空槽位。单位自己占着的槽位不算"占用"，所以在前线内挪位置也走得通。
    /// 找不到返回 -1。
    /// </summary>
    private static int FirstFreeFrontlineSlot(GameState state, CardInstance mover)
    {
        var occupied = state.Cards(Side.Left, CardLocation.BoardFrontline)
            .Concat(state.Cards(Side.Right, CardLocation.BoardFrontline))
            .Where(c => !c.IsHq && !ReferenceEquals(c, mover))
            .Select(c => c.LocationNumber)
            .ToHashSet();

        for (int slot = 0; slot < state.FrontlineCapacity; slot++)
        {
            if (!occupied.Contains(slot))
            {
                return slot;
            }
        }

        return -1;
    }

    private static bool NeedsTarget(CardInstance card)
        => card.Definition.ExternalCalls.Contains("GetTargetedCard", StringComparer.Ordinal);

    private static CardInstance? ChooseTarget(MatchEngine engine, CardInstance card, Side side)
    {
        if (!NeedsTarget(card))
        {
            return null;
        }

        // ★★ 目标必须过**客户端的两道门**（2026-10-02）。
        //
        // 旧实现是「敌方场上攻击力最高的单位」，对
        // 「Target air unit must retreat」/「只能指定老兵」/「Give a friendly unit +1+1」
        // 这类牌就是**硬塞一个非法目标** —— 客户端静默不执行（记牌器 +1、场上无变化），
        // 我们这边却把效果落了地 ⇒ 状态漂开。玩家报告的「虚空牌」第三个来源。
        //
        // 偏好**保持原样**（敌方优先、攻击力高的优先），只是把候选换成合法集 ——
        // 不在这里引入新策略，策略是 NN/GreedyBot 自己的事。
        var legal = engine.LegalPlayTargets(card);
        if (legal.Count == 0)
        {
            return null;   // 一个合法目标都没有 ⇒ 调用方跳过这张牌
        }

        var enemy = side.Opposite();
        return legal.Where(t => t.Owner == enemy).OrderByDescending(t => t.Attack).FirstOrDefault()
               ?? legal.OrderByDescending(t => t.Attack).First();
    }
}
