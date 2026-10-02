#nullable enable

namespace KLink.Bot.Cards;

/// <summary>
/// **卡蓝图成员变量的 CDO 默认值** —— IR 里"读了但从没被 <c>set</c>"的那些变量的初值。
///
/// ## 为什么需要它（这是"烟雾测试抓不到"的又一类语义错）
///
/// 卡蓝图的成员变量（`damageToDeal` 这类）在 UE 里有一个**默认值**，存在蓝图 CDO 上。
/// `gen-kismet-ir.py` 只编字节码（`steps` / `locals`），**不编 CDO 的默认值** ——
/// 于是 IR 里只留下 `{"var":"damageToDeal"}` 这种**裸读**，
/// 而 <c>KismetVm.Frame.Get</c> 的兜底是 `GetMember(ctx.Self, name)`，
/// 对不认识的成员返回 **null** ⇒ `IntArg(...)` 读成 **0**。
///
/// ## 证据（可复算）
///
/// `card_event_firestorm_skirm` 的 `OnPlayedFromHand` 是一条
/// 「把 `GetCardsOnBoardBySide` 的结果攒进 `cardsToDamage`，再调
/// `DamageMultipleCards(cardsToDamage, damageToDeal, cardID, out)`」的循环
/// （IR `i=382`）。整张卡**没有任何** `set dst="damageToDeal"`
/// ⇒ `damageToDeal` 恒为 0 ⇒ 伤害恒为 0 ⇒ 状态零变化。
/// 实测（`out/audit/smoke-all-cards.tsv`）：
/// <code>
/// card_event_firestorm_skirm  OnPlayedFromHand hand kind=D steps=91  changed=0
/// card_event_carpet_bombing  OnPlayedFromHand hand kind=D steps=91  changed=0
/// card_event_maelstrom       OnPlayedFromHand hand kind=D steps=179 changed=0
/// card_event_retribution5    OnPlayedFromHand hand kind=D steps=91  changed=0
/// card_event_wave_after_wave OnPlayedFromHand hand kind=OK steps=551 changed=1   ← 唯一会变的
/// </code>
/// 而 `card_event_wave_after_wave`（同一族、同一调用点 `i=382`）**是唯一会自己写
/// `damageToDeal` 的那张**（`i=557 set 4` / `i=660 set 2`）—— 它就跑得对。
/// 这条对照就是"变量没初值"的判决性证据：同一段程序，唯一的差别是变量有没有被赋值。
///
/// CDO 原文（`klink bot/decompiled/cards.all.json` → `assets[...]["cdo"]`）：
/// <code>
/// card_event_firestorm_skirm : "damageToDeal": "2"   卡面 "Deal 2 damage to all enemy units."
/// card_event_carpet_bombing  : "damageToDeal": "3"   卡面 "Deal 3 damage to all enemy units."
/// card_event_maelstrom       : "damageToDeal": "4"   卡面 "Deal 4 damage to each non-Veteran unit."
/// card_event_retribution5    : "damageToDeal": "2"   卡面 "Deal 2 damage to all enemy units."
/// </code>
/// **4 张的 CDO 默认值与卡面数字逐个吻合** —— 这也是"取值没猜错"的旁证。
///
/// ## ⚠️ 这是一张**手工表**，正确做法是让 IR 带上 CDO 默认值
///
/// 全卡池扫描（判据：IR 里 `{"var":X}` 裸读、同程序内没有 `set X`、且 X 在 CDO 里）
/// 一共只有 **10 张卡**命中，其中：
/// <list type="bullet">
/// <item>`faction`（2 张）→ <c>KismetVm.GetMember</c> **已经**认这个成员，不是问题；</item>
/// <item>`hasMobilize`（1 张）→ 同上，`GetMember` 已认；</item>
/// <item>`buffActive` / `friendlyAttacked` / `A6M2Effect`（3 张）→ 这三个是
///   `JSON_GetBool/JSON_SetBool/JSON_Clear` 的**键名**，CDO 里的"默认值"就是键名本身
///   （提取器把 Name 型默认值写成了自己的名字）。它们**读写用的是同一个（错的）键**，
///   行为自洽 ⇒ 改不改都行，**没验证过就不动**（见审计报告 (B) 档）。</item>
/// </list>
/// ⇒ 真正需要补的只有 `damageToDeal` 这一族（4 张）。
/// 根治办法是让 `gen-kismet-ir.py` 把 CDO 默认值编进 IR；在那之前用这张表兜住，
/// 并把生成判据写在这里以便复核。
/// </summary>
internal static class CardVarDefaults
{
    /// <summary>卡名（已解析变体）→ 成员变量名 → 默认值。</summary>
    private static readonly Dictionary<string, Dictionary<string, object>> ByCard =
        new(StringComparer.Ordinal)
        {
            // ---- damageToDeal：`DamageMultipleCards` 的伤害实参 ----
            // 调用点形状：`DamageMultipleCards(cardsToDamage, damageToDeal, cardID, out)` @ i=382
            ["card_event_firestorm_skirm"] = new() { ["damageToDeal"] = 2 },
            ["card_event_carpet_bombing"] = new() { ["damageToDeal"] = 3 },
            ["card_event_maelstrom"] = new() { ["damageToDeal"] = 4 },
            ["card_event_retribution5"] = new() { ["damageToDeal"] = 2 },
        };

    /// <summary>查这张卡的成员变量默认值；没有就返回 false（调用方照旧走原兜底）。</summary>
    public static bool TryGet(string cardName, string variable, out object value)
    {
        value = 0;
        if (!ByCard.TryGetValue(cardName, out var vars))
        {
            return false;
        }

        if (!vars.TryGetValue(variable, out var v))
        {
            return false;
        }

        value = v;
        return true;
    }
}
