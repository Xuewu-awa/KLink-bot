#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Phase 1 —— **机械化「卡面文字 vs 实际行为」对账**（覆盖全部有文字的卡）。

## 为什么写它
烟雾测试（4088 用例）能抓崩溃 / 未实现原语 / 步数上限 / 非确定性 / 零状态变化，
但**抓不到语义错**（跑得通却算错）。回放对拍能抓语义错，但 7 局回放只覆盖 81 个卡名，
而 IR 有 1610 张卡 ⇒ **「语义错」至今没有被系统查过**，全靠真人对局症状倒推。

本脚本做那个「系统查」。对每一张卡，把
    (a) **卡面文字**抽出的「应有的可观测后果」（effect family 集合）
    (b) **IR 里实际存在的原语**（静态；`card-ir.json` 的 `steps` **和** `locals`）
    (c) **烟雾测试里实际发生的事**（动态；`smoke-all-cards.tsv` 的 called/changed/kind）
逐卡对账，分档 A 明显不符 / B 可疑 / C 看起来对，并单独分出两类系统性缺口。

## ★ 三个必须先讲清的口径（否则结论会被误读）

### 口径 1：KARDS 的卡面有两类句子，只有一类能被本脚本判
- **ACTIVE（主动效果）**："Deal 2 damage to a unit." / "Draw a card." / "Add a T-34 to your support line."
  ⇒ 卡自己的程序**必须**调到对应族的原语。缺了就是硬缺口。
- **PASSIVE（静态修正）**："Deals double damage against tanks." / "Your orders deal +1 damage." /
  "British air units you deploy get +2+2." / "cost 1 less to deploy."
  ⇒ 这些是**引擎级的持续修正**，实现方式**不是**卡自己的 steps，
  而是（i）IR 的 `locals` 钩子（`OnCardDealDamage_ModifyDamageDealt` 等，由
  `CardApi.ExecuteOnDealDamageAddDamage` 按名字调用），或（ii）`CardInnateTable` 的字段。
  **拿「IR steps 里有没有该族原语」去判 PASSIVE 卡，一律是假阳性。**
  ⇒ 本脚本把 PASSIVE 卡单独归一类（P），不与 ACTIVE 卡混在一起判 A/B。

### 口径 2：`locals` 是**第二套派发路径**，烟雾测试完全没覆盖它
`card-ir.json` 里 98 张卡的 `entrypoints` 是**空的**、逻辑全在 `locals` 里
（`OnCardDealDamage_ModifyDamageDealt` ×26、`OnOtherCardDealDamageAddDamage` ×16 …）。
`SmokeAllCards` 是按 `card.Entrypoints` 枚举用例的 ⇒ **这 98 张卡烟雾测试 0 用例**。
它们不是没实现，是**没被测**。⇒ 本脚本把它们标成 `LOCALS-ONLY`。

### 口径 3：IR 注册了、内核从不派发的入口点
IR 里注册的入口名有 449 个，`SmokeAllCards.LiveEntrypoints`（= 内核源码里出现过的字面量）
只有 64 个。剔除 UI/动画名后仍有 ~53 个**玩法相关**入口点是内核从不派发的
（`OnPincerEffectApplied/Removed`、`OnIntelTriggered`、`OnOtherCovertCardPlayedFromHand` …）。
注册了这些入口的卡，效果「没发生」的原因是**事件根本没发**，不是「原语缺失」。
⇒ 本脚本把它们标成 `EVENT-NEVER-FIRED`，与 A 档分开。

## 用法
    python out/audit/semantic-reconcile.py              # 对账，落盘 tsv + txt
    python out/audit/semantic-reconcile.py --dump-map   # dump 原语→族映射表（供人工复核）
    python out/audit/semantic-reconcile.py --card NAME  # 单卡详情（含 IR 偏移 + 烟雾用例）
"""

import argparse
import collections
import csv
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DOCS = os.path.join(ROOT, "klink bot", "docs")
OUT = os.path.join(ROOT, "out", "audit")

CARDS_LIVE = os.path.join(DOCS, "cards.live.json")
CARD_IR = os.path.join(DOCS, "card-ir.json")
SMOKE_TSV = os.path.join(OUT, "smoke-all-cards.tsv")
INNATE = os.path.join(ROOT, "src", "KLink.Bot", "Cards", "CardInnateTable.cs")
SMOKE_SRC = os.path.join(ROOT, "tools", "BotSim", "SmokeAllCards.cs")
DB_SRC = os.path.join(ROOT, "src", "KLink.Bot", "Cards", "CardDatabase.cs")

# ======================================================================
#  1. 效果族（effect family）—— 对账的公共词汇表
# ======================================================================

# ----------------------------------------------------------------------
#  1a. 原语名 → 族（**名字启发式**，整表可 --dump-map 复核）
# ----------------------------------------------------------------------
PRIM_RULES = [
    ("DAMAGE", r"Damage"),
    ("DESTROY", r"Destroy|TriggerDestruction"),
    ("DRAW", r"Draw"),
    ("SPAWN", r"Spawn|CreateCard|AddCardTo|Summon|CreateUnit"
              # `selectCardToDraw` = Develop 族：挑一张卡**创建并加到手牌**
              # （内核 `SelectCardToDraw` → `DevelopChosenCard` / `DrawChosenCardToHand`）。
              # 「Choose 1 of 3 … to add to your hand」「add a copy to your hand」都走它。
              r"|^selectCardToDraw$"),
    ("BUFF_ATK", r"ChangeAttack|AddAttack|SetAttack|UpdateBuff|ApplyTheBuff|RemoveTheBuff|"
                 r"getAttackTempBuff|AddAttackToMultiple"),
    ("BUFF_DEF", r"ChangeDefense|AddDefense|SetDefense|AddDefenseToMultiple|getDefenseTempBuff"),
    ("HEAL", r"Heal|Repair|Restore"),
    ("KREDIT", r"Kredit"),
    ("OPCOST", r"OperationCost"),
    ("PIN", r"Pin"),
    ("SUPPRESS", r"Suppress"),
    ("RETREAT", r"Retreat"),
    ("DISCARD", r"Discard"),
    ("MOVE", r"MoveCard|MoveUnit|MoveMultiple|MoveToFrontline|MoveToSupportLine|MoveCardInHand"),
    ("BOUNCE", r"ToHand|ReturnToDeck|ToTopOfOwnersDeck|StealCardFromBoardToDeck"),
    ("CONTROL", r"TakeControl"),
    # ⚠️ 授予关键字的原语**名字里没有 "Keyword"**，是一个个 `Give<关键字>`：
    #    实测调用点 `card_event_finest_hour` i=195 `GiveBlitz`、
    #    `card_event_entrenched` i=124 `GiveFury` / i=200 `GiveSalvage`、
    #    `card_unit_spitfire_mk_v_pol` i=195 `GiveGuard`。
    #    第一版漏了这条 ⇒ 「Give X Blitz」一族全被判成「IR 里没有关键字原语」（假阳性）。
    ("KEYWORD", r"CustomAbility|GiveRandomCombatKeyword|GameplayTag|Keyword|CustomName2"
                r"|^Give(Blitz|Fury|Shock|Guard|Smoke|Smokescreen|Ambush|Pincer|Mobilize|Veteran"
                r"|Covert|Alpine|Salvage|Spy|Deployment|Intel|HeavyArmor|Tank)$"
                r"|Remove(Blitz|Fury|Shock|Guard|Smoke|Smokescreen|Ambush|Pincer|Mobilize|Veteran|Covert)"),
    ("RESTRICT", r"GameplayRestriction|Restriction"),
    ("COUNTDOWN", r"Countdown"),
    ("INTEL", r"Intel"),
    ("OBJECTIVE", r"Objective"),
    ("PLAY_FREE", r"PlayCardDirectlyFromHand|PlayCardFromHand"),
    ("DECK_ORDER", r"ToTopOfOwnersDeck|ToBottomOfOwnersDeck|Shuffle|AdjustCardPositionInDeck"),
    ("COUNTER", r"Counter|Marker"),
    ("TRANSFORM", r"ConvertCard|TransformCard|ChangeCard"),
]

# 这些族**只**产生「修正器」，不直接产生可观测后果 ⇒ 缺失时只进 B，不进 A
MODIFIER_ONLY = {"OPCOST", "DECK_ORDER", "COUNTER"}

# 名字是纯查询、不能当「写原语」的原语（避免把「扫了一遍卡池」当成「改了状态」）
#
# ⚠️ **必须大小写不敏感**。`SmokeAllCards.IsPurePrimitiveName` 用的是
#    `name.StartsWith("Get")` —— **区分大小写**，于是内核里那些小写开头的查询原语
#    （`isBuffedByCard` / `getTotalDefense` / `getHasGameplayTag` / `getTotalKreditCost`
#    / `getAndDecryptKredit` / `doesSideControlTheFrontline` / `didPlayBritishInfantryLastTurn` …）
#    被当成了「写原语」⇒ `smoke-all-cards.txt` 里 D2 的「306 用例 / 157 张卡」是**高估**。
#    本脚本用 `re.I` 重算，得到的是收紧后的数。
PURE_PRIM = re.compile(
    r"^(Get|Is|Has|Can|Was|Which|Show|Notify|Log|Enum|Conv_|Array_|JSON_|Map_|Concat|Boolean|"
    r"Equal|NotEqual|Less|Greater|Select|doOn|onOther|SelfCustom|CustomEvent|CustomOn|Receive|"
    r"Persist|Check|Update|Refresh|Validate|Find|Make|Block|Unblock|AddOverride|hasActive|"
    r"PincerHover|SetAttackText|SetDefenseText|SetDrawSize|SetCountermeasure|Set_|"
    r"K2_|math:|CheckAnd|show|getCountdown|Countdown|Does|Did|DoesSide)",
    re.I,
)

# ----------------------------------------------------------------------
#  1b. 卡面文字 → 族（正则）
# ----------------------------------------------------------------------
TEXT_RULES = [
    ("DAMAGE", r"\bdeal(?:s|ing)?\b|\bdamages?\b"),
    ("DESTROY", r"\bdestroy"),
    # `Develop` 是 KARDS 的关键字：从牌库/卡池里**挑一张并加到手牌**，
    # 内核实现是 `selectCardToDraw` + 卡自己的 `GetChooseSpawnCards`（见 CardApiDispatch
    # 的 `SelectCardToDraw`）。所以它的族是 DRAW，**不是** SPAWN。
    # 出处：`card_event_a_few_good_men` i=10 `selectCardToDraw`、
    #       `card_unit_151st_infantry_regiment` steps=DRAW。
    ("DRAW", r"\bdraw\b|\bdrawn\b|\bdevelop\b"),
    # ⚠️ 第一版把 `\bdeploy\b` 也算进 SPAWN ⇒ 「The enemy cannot deploy units next turn」
    #    （`card_event_tirpitz` / `card_event_u_99`）被判成「IR 里没有生成原语」。
    #    "deploy" 在卡面里指的是**玩家部署单位这个动作**（多为条件/限制），不是"生成一张卡"。
    ("SPAWN", r"\badd\b[^.]{0,60}\b(support line|battlefield|to your hand|to your deck|to the enemy deck)\b"
              r"|\bcreate\b|\bsummon\b|\breinforce|\bput\b[^.]{0,40}\binto play\b"
              r"|\badd a copy\b|\bfill\b[^.]{0,30}\bsupport line\b|\bduplicate\b"),
    # ⚠️ 第一版的 `\bgets? \+\d+` / `\bgains? \+\d+` 太宽：它把
    #    「Your HQ gets +3 defense」（`card_event_aans`）也算成 BUFF_ATK
    #    ⇒ 10 张卡的 A 档全是这一条造成的假阳性。必须要求 "attack" 在附近。
    ("BUFF_ATK", r"\+\s*\d+\s*(?:\+\s*\d+\s*)?attack"
                 r"|\+\s*\d+\s*/\s*\+\s*\d+"
                 r"|attack is increased|attack by \d"
                 r"|\bgains? \+\d+ attack|\bgets? \+\d+ attack"
                 r"|\bgains? \+\d+\s*\+\s*\d+|\bgets? \+\d+\s*\+\s*\d+"
                 r"|increase[^.]{0,30}attack|\breduce[^.]{0,30}attack|\b-\s*\d+\s*attack"),
    ("BUFF_DEF", r"\+\s*\d+\s*defense"
                 r"|defense is increased|defense by \d"
                 r"|increase[^.]{0,30}defense|\breduce[^.]{0,30}defense|\b-\s*\d+\s*defense"
                 r"|\bgets? \+\d+\s*defense"),
    ("HEAL", r"\bheal|\brepair|\brestore"),
    ("KREDIT", r"\bkredit"),
    ("OPCOST", r"operation cost"),
    ("PIN", r"\bpin(?:ned|s)?\b"),
    ("SUPPRESS", r"\bsuppress"),
    ("RETREAT", r"\bretreat"),
    ("DISCARD", r"\bdiscard"),
    ("MOVE", r"\bmove[sd]?\b|\bto the frontline\b|\bfrom the frontline\b"),
    ("BOUNCE", r"\breturn\b[^.]{0,60}\b(hand|deck)\b|\bsend\b[^.]{0,40}\bdeck\b"),
    ("CONTROL", r"\btake control\b|\bsteal\b"),
    # ⚠️ `(?!\bwith\b)` 是必须的：「Give a unit **with Guard** +1+3」里 Guard 是**条件**不是授予，
    #    第一版没排除它 ⇒ `card_event_resolute_defense` 被判成「IR 里没有关键字原语」。
    ("KEYWORD", r"\bgive\b(?:(?!\bwith\b|\bwithout\b)[^.]){0,60}?"
                r"\b(smoke|ambush|blitz|fury|guard|pincer|mobilize|veteran|"
                r"covert|intel|alpine|salvage|shock|spy|deployment|smokescreen)\b"
                r"|\bgains?\b(?:(?!\bwith\b)[^.]){0,40}?"
                r"\b(smoke|ambush|blitz|fury|guard|pincer|mobilize|veteran|covert)\b"
                r"|\bgive (it|them)\b"),
    ("RESTRICT", r"\bcan(?:no|')t\b|\bcannot\b|\bunable to\b|\bprevent|\bimmune\b"),
    ("COUNTDOWN", r"\bcountdown\b"),
    ("INTEL", r"\bintel\b"),
    ("OBJECTIVE", r"\bobjective\b"),
    ("PLAY_FREE", r"\bplay\b[^.]{0,40}\bfor free\b|\bwithout paying\b"),
    ("DECK_ORDER", r"\btop of\b[^.]{0,30}\bdeck\b|\bbottom of\b[^.]{0,30}\bdeck\b|\bshuffle\b"),
    ("COUNTER", r"\bmarker\b|\bcounter\b"),
    ("TRANSFORM", r"\bconvert\b|\btransform\b|\bbecomes?\b a copy\b"),
]

# ----------------------------------------------------------------------
#  1c. **PASSIVE 子句**判据 —— 命中则这一句不产生「必须调原语」的义务
# ----------------------------------------------------------------------
# ⚠️ 这是本脚本**最关键也最脆**的一环：判错会把真 bug 洗成「静态修正」。
#    所以每条都写成「明确的静态措辞」，宁可漏判（把 passive 当 active ⇒ 假阳性 A），
#    也不要多判（把 active 当 passive ⇒ 漏掉真 bug）。漏判会在报告里如实说明。
PASSIVE_CLAUSE = [
    # 伤害修正器
    r"\bdeals?\s+(double|triple|half|\+\s*\d+|\d+\s*(less|more))\s+damage",
    r"\bdeal\s+\+?\d+\s+damage\b(?!\s+to\b)",
    r"\bdamage\b[^.]{0,70}\b(is|are|gets?|got)\b[^.]{0,40}\b(increased|reduced|doubled|halved|ignored|immune)\b",
    r"\b(is|are)\s+immune to\b",
    r"\btakes?\s+\d+\s+(less|more)\b",
    r"\bignores?\s+(heavy\s+)?armor\b",
    r"\bcombat damage\b",
    r"\bnon-combat\b",
    r"\bdeal[s]?\s+(its|their|his)\s+attack\b",
    r"\bdamage dealt by\b",
    # 费用修正器
    r"\bcosts?\s+\d+\s+(less|more)\b",
    r"\bcost\s+\d+\s+(less|more)\b",
    r"\bcosts?\s+(less|more)\b",
    r"\bto deploy\b",
    # 持续光环（"you deploy / you add / your units have"）
    r"\b(you|your)\s+(deploy|add|play|control)\b",
    r"\bunits?\s+you\s+(deploy|add|play|control)\b",
    r"\byour\s+\w+\s+units?\b",
    r"\byour\s+units?\b",
    r"\bfriendly\s+units?\b",
    # 不能/免疫类静态限制
    r"\bcan(?:no|')?t\s+be\b",
    r"\bcannot\s+be\b",
    r"\bcan\s+move and attack\b",
    r"\bdo(?:es)? not\b",
    r"\bare not\b",
    r"\bdon't\b",
    # 触发条件（"every time it takes damage" 之类）
    r"\b(every|each)\s+time\b",
    # 持续光环（"Has +2 attack against ground targets" —— card_unit_henschel_he_129）
    r"\bhas\s+\+?\s*\d+\s*(attack|defense)\b",
    r"\bhave\s+\+?\s*\d+\s*(attack|defense)\b",
    r"\bhas\s+\+\s*\d+\b",
]

# 条件性 —— 条件不满足时「没效果」是**正确**的
CONDITION_RE = re.compile(
    r"\bif\b|\bwhen\b|\bwhenever\b|\bunless\b|\bwhile\b|\bfor each\b"
    r"|\bat the (start|end) of\b|\bafter\b|\bbefore\b|\buntil\b|\bother than\b"
    r"|\bas long as\b|\bthis turn\b|\bthis battle\b|\bonly\b|\bevery time\b|\beach time\b",
    re.I,
)

INNATE_KEYWORDS = ["Smoke", "Smokescreen", "Ambush", "Blitz", "Fury", "Guard", "Pincer",
                   "Mobilize", "Veteran", "Covert", "Alpine", "Salvage", "Shock", "Deployment"]

# 玩法相关的「从不派发」入口点里要**排除**的 UI/动画名
UI_ENTRY = re.compile(
    r"Actor|Timeline|Fly|Hover|Fade|Animation|Opacity|Border|Construct|Rarity|Showcase|"
    r"Look|Redraw|Tick|BeginPlay|EndPlay|VFX|Widget|Renderer|Glow|Touch|Drag|Clicked|"
    r"Reveal|Sound|Mesh|Material|Wildcard|GoldCard|Recycle|Recycled|Topbar|View|Focus|"
    r"Destruct|Initialized|Confirmed|DoTheRecycle|EventGoldCard|createConfirmed"
)

# 变体后缀（与 CardDatabase.VariantSuffixes 对齐；用前先从源码抽，抽不到用这份兜底）
FALLBACK_SUFFIXES = ["_vet", "_bal", "_blank", "_cam1", "_cov", "_trop", "_waw", "_elit",
                     "_home", "_inv", "_desert", "_tut", "_ai", "_vet2"]

# ======================================================================
#  2. 载入
# ======================================================================


def load_json(p):
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def load_innate():
    """从 CardInnateTable.cs 抽 `["name"] = (["Kw",...], n)`。"""
    table = {}
    if not os.path.exists(INNATE):
        return table
    pat = re.compile(r'\["([^"]+)"\]\s*=\s*\(\[(.*?)\],\s*(\d+)\)')
    with open(INNATE, encoding="utf-8") as f:
        for line in f:
            m = pat.search(line)
            if not m:
                continue
            kws = [k.strip().strip('"') for k in m.group(2).split(",") if k.strip()]
            table[m.group(1)] = (kws, int(m.group(3)))
    return table


def load_live_entrypoints():
    """从 SmokeAllCards.cs 抽 LiveEntrypoints（= 内核真的会派发的入口名）。"""
    with open(SMOKE_SRC, encoding="utf-8-sig") as f:
        src = f.read()
    blk = src.split("LiveEntrypoints = new(StringComparer.Ordinal)")[1].split("};")[0]
    return set(re.findall(r'"([^"]+)"', blk))


def load_variant_suffixes():
    try:
        with open(DB_SRC, encoding="utf-8-sig") as f:
            src = f.read()
        blk = src.split("VariantSuffixes")[1].split("];")[0]
        got = re.findall(r'"([^"]+)"', blk)
        if got:
            return got
    except Exception:
        pass
    return FALLBACK_SUFFIXES


def load_smoke():
    by_card = collections.defaultdict(list)
    with open(SMOKE_TSV, encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f, delimiter="\t"):
            by_card[row["card"]].append(row)
    return by_card


# ======================================================================
#  3. 抽取
# ======================================================================


def ir_split(entry):
    """→ (entry_prims, local_prims)：两套派发路径分开统计。

    `steps`   = 由 `entrypoints` 派发（烟雾测试覆盖的那套）
    `locals`  = 由内核按名字调用（`RunOwnLocal` / `FireTrigger`；烟雾测试**不覆盖**）
    """
    ep = collections.defaultdict(list)
    lo = collections.defaultdict(list)

    def walk(steps, sink):
        for s in steps or []:
            if not isinstance(s, dict):
                continue
            if s.get("op") == "call" and s.get("fn"):
                sink[s["fn"]].append(s.get("i"))
            if s.get("math"):
                sink["math:" + s["math"]].append(s.get("i"))
            for key in ("src", "cond", "dst"):
                v = s.get(key)
                if isinstance(v, dict) and v.get("math"):
                    sink["math:" + v["math"]].append(s.get("i"))
            for a in s.get("args") or []:
                if isinstance(a, dict):
                    if a.get("math"):
                        sink["math:" + a["math"]].append(s.get("i"))
                    if a.get("fn"):
                        sink[a["fn"]].append(s.get("i"))

    walk(entry.get("steps"), ep)
    for _fn, body in (entry.get("locals") or {}).items():
        walk(body.get("steps") if isinstance(body, dict) else body, lo)
    return ep, lo


def prim_families(prim_name):
    bare = prim_name[5:] if prim_name.startswith("math:") else prim_name
    return {fam for fam, rx in PRIM_RULES if re.search(rx, bare)}


def fams_of(prims):
    out = set()
    for p in prims:
        out |= prim_families(p)
    return out


def spawned_tokens(entry):
    """卡自己的 steps 里 `{"name": "card_xxx"}` 字面量 —— 它**生成的那些卡**。

    为什么需要：很多卡的后续效果长在**被生成的 token 卡自己**的程序里。
    实例：`card_event_siberian_transfer`「Add a T-34 1942 to the support line.
    Destroy it at end of turn.」自己的 steps 只有 `SpawnCardOnBattlefield`
    （参数里 `{"name":"card_unit_t_34eot"}`），而 "Destroyed at end of turn" 是
    `card_unit_t_34eot.OnEndOfTurn` 里的 `DestroyCard`。
    不看 token 就会把这张卡误判成「IR 里没有 DESTROY 原语」。
    """
    names = set()

    def walk(steps):
        for s in steps or []:
            if not isinstance(s, dict):
                continue
            for a in s.get("args") or []:
                if isinstance(a, dict) and isinstance(a.get("name"), str):
                    names.add(a["name"])

    walk(entry.get("steps"))
    for _fn, body in (entry.get("locals") or {}).items():
        walk(body.get("steps") if isinstance(body, dict) else body)
    return names


def strip_quoted(text):
    """去掉 `"..."` —— 那是**给别的卡**授予的能力（token 卡自己的卡面），不是这张卡的效果。"""
    return re.sub(r'"[^"]*"', " ", text)


def split_clauses(text):
    """按句号/分号/换行切子句；`Choose one - A OR B` 再按 OR 切。"""
    parts = re.split(r"[.;\n]", text)
    out = []
    for p in parts:
        p = p.strip()
        if not p:
            continue
        if re.search(r"\bchoose one\b|\bchoose 1\b", p, re.I):
            out.extend(x.strip() for x in re.split(r"\bOR\b", p, flags=re.I) if x.strip())
        else:
            out.append(p)
    return out


def is_passive_clause(clause):
    for rx in PASSIVE_CLAUSE:
        if re.search(rx, clause, re.I):
            return True
    return False


def clause_families(clause):
    fams = {}
    for fam, rx in TEXT_RULES:
        for m in re.finditer(rx, clause, re.I):
            fams.setdefault(fam, []).append(m.group(0))
    return fams


def analyze_text(text):
    """→ (active_fams, passive_fams, all_fams, conditional)

    active_fams  = 出现在**非被动**子句里的族 ⇒ 必须能在 IR 里找到对应原语
    passive_fams = 只出现在**被动**子句里的族 ⇒ 静态修正，不由本卡 steps 承担
    """
    body = strip_quoted(text)
    active, passive = set(), set()
    for clause in split_clauses(body):
        fams = set(clause_families(clause).keys())
        if not fams:
            continue
        if is_passive_clause(clause):
            passive |= fams
        else:
            active |= fams
    # 只在被动子句里出现过的族，从 active 里去掉
    active -= (passive - active)
    return active, passive, (active | passive), bool(CONDITION_RE.search(text))


# ======================================================================
#  4. 对账
# ======================================================================


def classify(card_name, ctype, text, ep_fams, lo_fams, ep_names, live, dyn):
    """→ (tier, why, detail)

    档位：
      A                  ACTIVE 无条件效果族在 IR（steps+locals+生成的 token）里没有任何原语
      B                  可疑：有条件句 / 只缺修正器族 / 只缺被动族
      P                  卡面基本是静态修正 ⇒ 不在本脚本判据范围内（另列）
      NEVER-FIRED        卡注册了内核从不派发的入口点 ⇒ 事件缺口，不是原语缺失
      LOCALS-ONLY        entrypoints 空、逻辑全在 locals ⇒ 烟雾测试 0 用例
      C                  看起来对
    """
    active, passive, allf, cond = analyze_text(text)
    # 生成的 token 卡自己的程序也算「这张卡的效果」（见 `spawned_tokens` 的注释）
    token_fams = dyn.get("token_fams") or set()
    ir_fams = ep_fams | lo_fams | token_fams

    if not allf:
        return "SKIP", "卡面无可观测后果（风味/纯关键字）", {}

    # ---- ① 事件从不派发（与「原语缺失」分开）----
    dead = sorted(e for e in ep_names if e not in live and not UI_ENTRY.search(e))
    if dead and not ep_names.keys() & live:
        # 这张卡的**全部**入口都是死的 ⇒ 它的效果永远不会被触发
        return "NEVER-FIRED", "注册的入口内核从不派发：" + ",".join(dead), {
            "dead_entrypoints": dead}

    # ---- ② 卡面基本是静态修正 ----
    if not active:
        return "P", "卡面只有静态修正（无主动效果子句）：" + ",".join(sorted(passive)), {
            "passive": sorted(passive), "ir_fams": sorted(ir_fams)}

    # ---- ③ 主动效果 vs IR ----
    missing = {f for f in active if f not in ir_fams}

    # 天生关键字不算缺口（由 CardInnateTable 承载；token 的天生关键字也算）
    if "KEYWORD" in missing:
        text_l = text.lower()
        claimed = {k for k in INNATE_KEYWORDS if re.search(r"\b" + k.lower() + r"\b", text_l)}
        innate = set(dyn.get("innate", [])) | set(dyn.get("token_innate", []))
        if claimed and all(k in innate for k in claimed):
            missing.discard("KEYWORD")

    if not missing:
        tier = "C"
        why = "主动效果族都有对应原语"
        if not ep_names:
            tier, why = "LOCALS-ONLY", "entrypoints 空、逻辑全在 locals（烟雾测试 0 用例）"
        return tier, why, {"active": sorted(active), "ir_fams": sorted(ir_fams)}

    # ⚠️ KEYWORD 一族**故意**只进 B、不进 A：卡面里「Give a friendly **Guard** unit +3 attack,
    #    Fury and Salvage」把**条件**（Guard）和**授予**（Fury/Salvage）写在同一句里，
    #    纯文本判据分不开；而且关键字也可能由 token 卡自带。
    #    把它判成 A 会制造假阳性 ⇒ 按「拿不准的一律进 B」处理。
    hard = missing - MODIFIER_ONLY - {"KEYWORD"}
    detail = {"active": sorted(active), "passive": sorted(passive),
              "missing": sorted(missing), "ir_fams": sorted(ir_fams),
              "cases": dyn.get("cases", 0), "changed": dyn.get("changed_cases", 0)}

    if not ep_names:
        return "LOCALS-ONLY", "entrypoints 空（烟雾 0 用例）；缺族：" + ",".join(sorted(missing)), detail
    if hard and not cond:
        return "A", "无条件主动效果族在 IR 里没有任何原语：" + ",".join(sorted(hard)), detail
    if hard and cond:
        return "B", "有条件句；缺主动族：" + ",".join(sorted(hard)), detail
    return "B", "只缺修正器/被动族：" + ",".join(sorted(missing)), detail


def compute_write_capable(smoke):
    """**观测式**判据：在别的用例里「调了它就有状态变化」的原语。

    与 `SmokeAllCards.cs` 里那段 `mutating` / `writeCapable` **同一口径**
    （先取伴随状态变化的原语，再滤掉名字是查询形状的），
    这样本脚本报的 D2 数量能与 `smoke-all-cards.txt` 对得上。
    """
    mutating = set()
    for rows in smoke.values():
        for r in rows:
            if r["changed"] != "1":
                continue
            for p in r["called"].split(";"):
                if p:
                    mutating.add(p)
    return {p for p in mutating if not PURE_PRIM.search(p)}


def dynamic_tier(rows, write_capable, active_fams):
    """动态判据 → (档位, 证据)

    - **D2**（最可疑）：调了「写原语」状态却**零变化** —— `changeType` 实参错、
      门错、守卫错都会长这样。已知实例：`ChangeAttack` 的 `changeType=4`（撤销 buff）
      落到 `ChangeAttack(target, 0, …)`，而 `CardApi.cs` 里 `delta == 0 → return`。
    - **D1**（中等）：一条写原语都没调、状态零变化，**但卡面有无条件的主动效果**
      ⇒ 要么守卫在合成局面下不成立，要么效果根本没接上。
    - **NO-CASES**：这张卡在烟雾测试里一个用例都没有（见 LOCALS-ONLY / 无活入口）。
    """
    if not rows:
        return "NO-CASES", ""
    changed = [r for r in rows if r["changed"] == "1"]
    if changed:
        return "OK", ""
    called = sorted({p for r in rows for p in r["called"].split(";") if p})
    writers = [p for p in called if p in write_capable]
    if writers:
        return "D2", ",".join(writers)
    if active_fams:
        return "D1", ""
    return "D0", ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump-map", action="store_true")
    ap.add_argument("--card")
    ap.add_argument("--tier")
    ap.add_argument("--tsv", default=os.path.join(OUT, "semantic-reconcile.tsv"))
    ap.add_argument("--txt", default=os.path.join(OUT, "semantic-reconcile.txt"))
    args = ap.parse_args()

    cards = load_json(CARDS_LIVE)
    ir = load_json(CARD_IR)
    innate = load_innate()
    smoke = load_smoke()
    live = load_live_entrypoints()
    suffixes = load_variant_suffixes()

    def resolve(name):
        """变体卡 → 基名（与 CardDatabase.ResolveBaseName 同口径）。"""
        for s in suffixes:
            if name.endswith(s) and name[: -len(s)] in ir:
                return name[: -len(s)]
        return name

    # ---------------- dump 映射表 ----------------
    if args.dump_map:
        allprims = collections.Counter()
        for _n, e in ir.items():
            a, b = ir_split(e)
            for p, offs in list(a.items()) + list(b.items()):
                allprims[p] += len(offs)
        rows = [(p, n, ",".join(sorted(prim_families(p))) or "—")
                for p, n in allprims.most_common()]
        path = os.path.join(OUT, "prim-family-map.tsv")
        with open(path, "w", encoding="utf-8", newline="") as f:
            w = csv.writer(f, delimiter="\t")
            w.writerow(["primitive", "ir_uses", "families"])
            w.writerows(rows)
        print(f"原语 {len(rows)} 个 → {path}")
        print(f"已归族 {sum(1 for r in rows if r[2] != '—')} / 未归族 {sum(1 for r in rows if r[2] == '—')}")
        return

    # ---------------- 单卡详情 ----------------
    if args.card:
        name = args.card
        if name not in cards:
            for k in cards:
                if name.lower() in k.lower():
                    name = k
                    break
        c = cards.get(name, {})
        text = c.get("text") or ""
        print(f"=== {name} ===")
        print(f"type={c.get('type')} faction={c.get('faction')} kredits={c.get('kredits')} "
              f"atk={c.get('attack')} def={c.get('defense')} opcost={c.get('operationcost')}")
        print(f"text: {text}")
        print(f"innate: {innate.get(name)}   resolve-> {resolve(name)}")
        a, p, allf, cond = analyze_text(text)
        print(f"active_fams={sorted(a)} passive_fams={sorted(p)} conditional={cond}")
        print(f"clauses:")
        for cl in split_clauses(strip_quoted(text)):
            if clause_families(cl):
                print(f"   [{'PASSIVE' if is_passive_clause(cl) else 'ACTIVE '}] {cl}")
        base = resolve(name)
        e = ir.get(base)
        if e is None:
            print(f"IR: **没有 {base} 的 IR 条目**")
        else:
            eps, los = ir_split(e)
            print(f"entrypoints: {e.get('entrypoints')}")
            print(f"locals: {list((e.get('locals') or {}).keys())}")
            print("  -- steps（由 entrypoints 派发）--")
            for pr, offs in sorted(eps.items()):
                print(f"     i={offs} {pr} → {sorted(prim_families(pr)) or '—'}")
            print("  -- locals（内核按名字调用；烟雾测试不覆盖）--")
            for pr, offs in sorted(los.items()):
                print(f"     i={offs} {pr} → {sorted(prim_families(pr)) or '—'}")
        rows = smoke.get(name, [])
        print(f"smoke cases: {len(rows)}")
        for r in rows:
            print(f"   {r['entry']:34s} {r['placement']:8s} kind={r['kind']:2s} "
                  f"steps={r['steps']:>6s} changed={r['changed']} called={r['called'][:160]}")
        return

    # ---------------- 主对账 ----------------
    write_capable = compute_write_capable(smoke)
    out_rows = []
    stats = collections.Counter()
    dyn_stats = collections.Counter()
    fam_hits = collections.Counter()
    a_by_family = collections.Counter()

    for name in sorted(cards):
        c = cards[name]
        text = c.get("text") or ""
        ctype = c.get("type") or ""
        if not text.strip():
            stats["SKIP:no-text"] += 1
            continue
        # 战役地图 location 卡的 `text` 是**地点描述**，不是卡牌效果
        if ctype == "location" and name not in ir:
            stats["SKIP:location-flavor"] += 1
            continue

        base = resolve(name)
        e = ir.get(base)
        ep_prims, lo_prims = ir_split(e) if e else ({}, {})
        ep_names = (e.get("entrypoints") or {}) if e else {}
        ep_fams, lo_fams = fams_of(ep_prims), fams_of(lo_prims)

        # 生成的 token 卡：它们自己的程序/天生关键字也算这张卡的效果（见 spawned_tokens 注释）
        token_fams, token_innate = set(), []
        if e:
            for tname in spawned_tokens(e):
                te = ir.get(resolve(tname))
                if te:
                    tp, tl = ir_split(te)
                    token_fams |= fams_of(tp) | fams_of(tl)
                token_innate.extend(innate.get(resolve(tname), ([], 0))[0])

        rows = smoke.get(name, [])
        dyn = {
            "cases": len(rows),
            "changed_cases": sum(1 for r in rows if r["changed"] == "1"),
            "kinds": dict(collections.Counter(r["kind"] for r in rows)),
            "entries": sorted({r["entry"] for r in rows}),
            "called": sorted({p for r in rows for p in r["called"].split(";") if p}),
            "actions": sorted({a for r in rows for a in r["new_actions"].split(";") if a}),
            "innate": innate.get(name, ([], 0))[0],
            "token_fams": token_fams,
            "token_innate": token_innate,
        }

        tier, why, detail = classify(name, ctype, text, ep_fams, lo_fams, ep_names, live, dyn)
        stats[tier] += 1
        if tier == "A":
            for f in detail.get("missing", []):
                a_by_family[f] += 1

        active, passive, allf, cond = analyze_text(text)
        for f in allf:
            fam_hits[f] += 1

        dyn_tier, dyn_why = dynamic_tier(rows, write_capable, active)
        dyn_stats[dyn_tier] += 1

        out_rows.append({
            "card": name, "base": base, "type": ctype, "tier": tier, "why": why,
            "dyn_tier": dyn_tier, "dyn_why": dyn_why,
            "conditional": int(cond),
            "active_fams": ",".join(sorted(active)),
            "passive_fams": ",".join(sorted(passive)),
            "ir_ep_fams": ",".join(sorted(ep_fams)),
            "ir_local_fams": ",".join(sorted(lo_fams)),
            "entrypoints": ",".join(sorted(ep_names)),
            "dead_entrypoints": ",".join(detail.get("dead_entrypoints", [])),
            "cases": dyn["cases"], "changed_cases": dyn["changed_cases"],
            "kinds": ",".join(f"{k}:{v}" for k, v in sorted(dyn["kinds"].items())),
            "ir_prims": ";".join(f"{p}@{offs[0]}" for p, offs in sorted(ep_prims.items())),
            "ir_local_prims": ";".join(f"{p}@{offs[0]}" for p, offs in sorted(lo_prims.items())),
            "dyn_called": ";".join(dyn["called"]),
            "dyn_actions": ";".join(dyn["actions"]),
            "text": text.replace("\n", " "),
        })

    with open(args.tsv, "w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, delimiter="\t", fieldnames=list(out_rows[0].keys()))
        w.writeheader()
        w.writerows(out_rows)

    # ---------------- 摘要 ----------------
    sb = []
    total = len(cards)
    sb.append("=== Phase 1 卡面 vs 实际行为 机械化对账 ===")
    sb.append(f"卡池 {total} 张；IR 条目 {len(ir)}；参与对账 {len(out_rows)}")
    sb.append("")
    sb.append("  档位分布：")
    for k in ["A", "B", "C", "P", "NEVER-FIRED", "LOCALS-ONLY", "SKIP"]:
        if stats[k]:
            sb.append(f"     {k:14s} {stats[k]}")
    sb.append(f"     {'SKIP:no-text':14s} {stats['SKIP:no-text']}（卡面为空）")
    sb.append(f"     {'SKIP:loc-flavor':14s} {stats['SKIP:location-flavor']}（战役地图 location 卡，text 是地点描述）")
    sb.append("")
    sb.append("  (A) 缺族分布：" + ", ".join(f"{k}={v}" for k, v in a_by_family.most_common()))
    sb.append("")
    sb.append("  ★ 动态判据（与 smoke-all-cards.txt 同口径的 writeCapable = "
              f"{len(write_capable)} 个原语）：")
    for k in ["OK", "D2", "D1", "D0", "NO-CASES"]:
        if dyn_stats[k]:
            sb.append(f"     {k:10s} {dyn_stats[k]}")
    sb.append("     D2 = 调了写原语却零变化（最可疑）；D1 = 没调写原语 + 卡面有无条件主动效果")
    sb.append("     D0 = 没调写原语 + 卡面只有被动/条件；NO-CASES = 烟雾测试 0 用例")
    sb.append("")
    sb.append("  各效果族的卡面出现次数（分母=参与对账的卡）：")
    for fam, n in fam_hits.most_common():
        sb.append(f"     {fam:12s} {n}")
    sb.append("")

    # ---- D2 明细（最有价值的一节）----
    d2 = [r for r in out_rows if r["dyn_tier"] == "D2"]
    sb.append(f"=== ★ (D2) 调了写原语却零状态变化：{len(d2)} 张卡 ===")
    sb.append("  判据：该卡**所有**烟雾用例 changed=0，且 called 里出现了"
              "「在别的用例里确实伴随状态变化」的原语。")
    sb.append("  这一档最可能藏着语义错（changeType 实参错 / 门错 / 守卫错）。")
    for r in sorted(d2, key=lambda x: (x["dyn_why"], x["card"])):
        sb.append(f"  {r['card']}  [{r['type']}]  条件句={r['conditional']}  "
                  f"active={r['active_fams'] or '—'}")
        sb.append(f"      卡面: {r['text'][:170]}")
        sb.append(f"      零变化用例={r['cases']}  写原语={r['dyn_why']}")
        sb.append(f"      IRsteps={r['ir_ep_fams']} IRlocals={r['ir_local_fams']}")
    sb.append("")

    # ---- D1 明细（无条件主动效果 + 零变化）----
    d1 = [r for r in out_rows if r["dyn_tier"] == "D1"]
    sb.append(f"=== (D1) 没调写原语 + 零变化 + 卡面有主动效果：{len(d1)} 张卡 ===")
    sb.append("  多一半是「合成局面不满足守卫」（例如卡面要「a Suppressed unit」而场上没有），")
    sb.append("  但**无条件**的那些要单独看（那才可能是效果根本没接上）。")
    for r in sorted(d1, key=lambda x: (x["conditional"], x["card"])):
        sb.append(f"  {r['card']}  [{r['type']}] 条件句={r['conditional']} "
                  f"active={r['active_fams']} entrypoints={r['entrypoints']}")
        sb.append(f"      卡面: {r['text'][:150]}")
    sb.append("")

    order = ["A", "NEVER-FIRED", "LOCALS-ONLY", "B", "P"]
    titles = {
        "A": "(A) 明显不符 —— 无条件主动效果族在 IR 里没有任何原语",
        "B": "(B) 可疑/歧义 —— 需要人判（条件句/修正器/数值/目标范围）",
        "P": "(P) 卡面只有静态修正 —— 不在「steps 有没有该族原语」的判据范围内",
        "NEVER-FIRED": "(N) 事件从不派发 —— 注册的入口内核从不派发，效果永远不会发生",
        "LOCALS-ONLY": "(L) 逻辑全在 locals —— entrypoints 为空，烟雾测试 0 用例",
    }
    for tier in order:
        sel = [r for r in out_rows if r["tier"] == tier]
        if not sel:
            continue
        sb.append(f"=== {titles[tier]}：{len(sel)} 张 ===")
        for r in sorted(sel, key=lambda x: x["card"]):
            sb.append(f"  {r['card']}  [{r['type']}]  cases={r['cases']} changed={r['changed_cases']}"
                      f"{'  条件句' if r['conditional'] == '1' else ''}")
            sb.append(f"      卡面: {r['text'][:180]}")
            sb.append(f"      active={r['active_fams']}  passive={r['passive_fams']}")
            sb.append(f"      IR(steps)={r['ir_ep_fams']}  IR(locals)={r['ir_local_fams']}"
                      f"  entrypoints={r['entrypoints']}")
            sb.append(f"      判读: {r['why']}")
        sb.append("")

    txt = "\n".join(sb)
    with open(args.txt, "w", encoding="utf-8") as f:
        f.write(txt)
    sys.stdout.reconfigure(encoding="utf-8")
    print(txt[:7000])
    print(f"\nTSV → {args.tsv}\nTXT → {args.txt}")


if __name__ == "__main__":
    main()
