using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Replay;

namespace KLink.Bot.ServerBridgeTest;

/// <summary>
/// **kredit 花费表** —— 逐条人类出牌导出「**实际支付**」，用来判「自然增长」到底是
/// 「自己每回合 +1」还是「每个回合双方各 +1」。
///
/// ## 为什么必须有它（而不是像上一版那样读卡面费用）
///
/// 上一版（`out/_tmp-kredit-verify.py`）用 `cards.live.json` 的 `kredits` 当花费，
/// 于是对 **PAMS 开发出来的牌系统性高估**：
/// `card_event_pams` 的 IR i=348 是
/// `ChangeKreditCost(卡, 自己, 0, changeType=2)`（`EChangeType::SetValue`），
/// 卡面原文「Add it to your deck with a cost of **0**.」——
/// 客户端把它当 **0 费**，卡面表却记着它原本的费用。
/// 实测被这条坑到的一行：`542091 t7 花费 8`（`convoy_175` 费 3 + `war_bonds` 费 5），
/// 真实支付是 `0 + 5 = 5`。
///
/// ⇒ 本工具**不读卡面表**，而是让内核把整局重放一遍，
///    在每一步之后读 `CardInstance.KreditCost`（= `EffectiveKreditCost`，
///    已经把 `ChangeKreditCost` 的所有 buff 叠进去了，见 `CardInstance.cs:302`）。
///
/// ## 口径
///
/// `kreditsBefore = kreditsAfter + cost`（出牌只扣不加）。
/// 动作没被应用时 `kreditsAfter == kreditsBefore`，直接取 `kreditsAfter`。
/// 出牌前一刻的池子值**客户端是权威**：`kreditsBefore &lt; cost` 而动作仍然成功，
/// 就是**内核池子算少了**的直接反例（不依赖任何"花费反推"）。
/// </summary>
internal static class KreditTable
{
    public static int Run(string repoRoot, string dir)
    {
        string dataDir = Path.Combine(repoRoot, "tem", "fyserver", "bin", "Release", "net10.0", "BotData");
        if (!Directory.Exists(dataDir))
        {
            dataDir = Path.Combine(repoRoot, "klink bot", "docs");
        }

        var db = CardDatabase.Load(dataDir);
        string ir = Path.Combine(dataDir, "card-ir.json");
        if (File.Exists(ir))
        {
            KLink.Bot.Effects.Blueprint.KismetLibrary.Initialize(ir);
        }

        var bases = Directory.GetFiles(dir, "replay-*.actions.json")
            .Select(f => f[..^".actions.json".Length])
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (bases.Count == 0)
        {
            Console.Error.WriteLine($"在 {dir} 里没找到 replay-*.actions.json");
            return 2;
        }

        // 表头：机器可读（后续用脚本做模型判定）
        //
        // `kreditsBefore` 取**上一条动作结算之后**的池子值（精确），
        // 而不是 `kreditsAfter + cost` —— 后者在"出牌本身带加费/回费"时会偏。
        // 实测对局 `508065` 的 `#45 t11` 就是这么暴露出来的：
        // 内核在人类 t11 开始时池子只有 **4**，而它的 `maxKredits` 是 **7** ——
        // 说明 `StartTurn` 的「回满」那一步在回放路径上**没有对 left 生效**。
        Console.WriteLine("replay,actionId,turn,side,action,cardId,code,kernelName,cost,opCost,kreditsBefore,maxKreditsBefore,applied");

        foreach (string basePath in bases)
        {
            string mid = Path.GetFileName(basePath)["replay-".Length..];
            var replay = ReplayData.Load(basePath + ".json", basePath + ".actions.json");

            // 每个动作**结算之后**的状态快照，按 actionId 存下来，跑完再与 StepResult 对账。
            var snap = new Dictionary<int, (int Cost, int OpCost, int Kredits, int MaxKredits, string Name, int CardId, int KreditsBefore, int MaxBefore)>();
            var lastK = new Dictionary<Side, (int K, int M)>
            {
                [Side.Left] = (0, 0),
                [Side.Right] = (0, 0),
            };
            var runner = new ReplayRunner(db);
            var report = runner.Run(replay, verbose: false, onStepped: (act, st) =>
            {
                var card = st.ById(act.CardId);
                var sd = replay.SideOf(act.PlayerId);
                var prev = lastK.TryGetValue(sd, out var p) ? p : (K: 0, M: 0);
                snap[act.ActionId] = (
                    card?.KreditCost ?? -1,
                    card?.OperationCost ?? -1,
                    st.Kredits(sd),
                    st.MaxKredits(sd),
                    card?.Name ?? "",
                    act.CardId,
                    prev.K,
                    prev.M);
                lastK[Side.Left] = (st.Kredits(Side.Left), st.MaxKredits(Side.Left));
                lastK[Side.Right] = (st.Kredits(Side.Right), st.MaxKredits(Side.Right));
            });

            foreach (var s in report.Steps)
            {
                if (s.ActionType is not ("PC" or "ML" or "AC") || !snap.TryGetValue(s.ActionId, out var v))
                {
                    continue;
                }

                // 卡身份以**动作流自带的卡组码**为准（客户端数据），
                // 内核的 `kernelName` 只用来标注「内核认成了哪张」。
                var act = replay.Actions.FirstOrDefault(a => a.ActionId == s.ActionId);
                string code = act?.CardCodes.FirstOrDefault() ?? "";

                Console.WriteLine($"{mid},{s.ActionId},{s.Turn},{s.PlayerSide},{s.ActionType},{v.CardId}," +
                                  $"{code},{v.Name.Replace(',', '/')},{v.Cost},{v.OpCost}," +
                                  $"{v.KreditsBefore},{v.MaxBefore},{(s.Applied ? 1 : 0)}");
            }
        }

        return 0;
    }

    /// <summary>
    /// 逐条动作打印双方 kredit 轨迹 —— 用来对账「某一个回合开始时到底回满了没有」。
    /// 用法：<c>--kredit-trace out\_server-replays\replay-508065</c>
    /// </summary>
    public static int Trace(string repoRoot, string basePath)
    {
        string dataDir = Path.Combine(repoRoot, "tem", "fyserver", "bin", "Release", "net10.0", "BotData");
        if (!Directory.Exists(dataDir))
        {
            dataDir = Path.Combine(repoRoot, "klink bot", "docs");
        }

        var db = CardDatabase.Load(dataDir);
        string ir = Path.Combine(dataDir, "card-ir.json");
        if (File.Exists(ir))
        {
            KLink.Bot.Effects.Blueprint.KismetLibrary.Initialize(ir);
        }

        var replay = ReplayData.Load(basePath + ".json", basePath + ".actions.json");
        var trace = new Dictionary<int, string>();
        var cards = new Dictionary<int, string>();
        var created = new Dictionary<int, List<string>>();
        var seenIds = new HashSet<int>();
        var runner = new ReplayRunner(db);
        var report = runner.Run(replay, verbose: false, onStepped: (act, st) =>
        {
            trace[act.ActionId] = $"L={st.Kredits(Side.Left)}/{st.MaxKredits(Side.Left)} " +
                                  $"R={st.Kredits(Side.Right)}/{st.MaxKredits(Side.Right)}";
            var card = st.ById(act.CardId);
            if (card is not null)
            {
                cards[act.ActionId] = $"{card.Name}#{card.CardId} 费{card.KreditCost} 油{card.OperationCost}" +
                                      $" {card.Location}";
            }

            var fresh = new List<string>();
            foreach (var c in st.CardsUnordered())
            {
                if (seenIds.Add(c.CardId))
                {
                    fresh.Add($"{c.Name}#{c.CardId}@{c.Location}费{c.KreditCost}");
                }
            }

            if (fresh.Count > 0)
            {
                created[act.ActionId] = fresh;
            }
        });

        foreach (var s in report.Steps)
        {
            string mark = s.Applied ? "  " : "✗ ";
            Console.WriteLine($"{mark}#{s.ActionId,-4} t{s.Turn,-3} {s.ActionType,-22} {s.PlayerSide,-5} " +
                              $"{trace.GetValueOrDefault(s.ActionId, "?"),-18} " +
                              $"{cards.GetValueOrDefault(s.ActionId, "")}" +
                              (s.Applied ? "" : $"   ⟵ {s.Failure}"));
            if (created.TryGetValue(s.ActionId, out var fresh))
            {
                Console.WriteLine($"        ＋新建 {string.Join(" | ", fresh)}");
            }
        }

        return 0;
    }
}
