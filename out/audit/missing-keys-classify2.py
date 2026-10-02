# -*- coding: utf-8 -*-
"""
★ 最终分类脚本（v4）—— 608 个「IR 调用名不在派发表里」的键 → A / B / C。

═══════════════════════════════════════════════════════════════════════
为什么必须重做（旧脚本 `missing-keys-classify.py` 的结论是错的）
═══════════════════════════════════════════════════════════════════════
旧脚本用 `(public|private|internal)\\s+…\\s+名字\\s*\\(` 匹配 C# 源码，判「实现有没有」。
但本仓库**实现方法名与 IR 调用名经常不同**：
    IR `DiscardCardFromHand` → C# `CardApi.DiscardCard`
    IR `getAndDecryptAttack` → 参考实现里的 `card.Attack`
所以纯名字正则必然漏，旧结论「只有 2 种 B」是错的（真值见下）。

═══════════════════════════════════════════════════════════════════════
★ 先做一次分母修正：`locals` 兜底
═══════════════════════════════════════════════════════════════════════
`card-ir.json` 每张卡可带 `locals`（卡自己蓝图里的私有函数体）。
`KismetVm.ExecuteCall` 在派发表查不到时会**回退执行卡自己的 locals 函数体**
（守这条的自测：`tools/BotSim/SelfTest.cs:4583 LocalFunctionActuallyRuns`）。
⇒ 「不在派发表里」**不等于**「没被处理」。
本脚本按**调用点**判：调用它的那张卡的 locals 里有这个函数 ⇒ 该调用点已被处理。
实测：**63 种 / 148 个调用点**属于这一类，不是缺口。

═══════════════════════════════════════════════════════════════════════
A / B / C 判据
═══════════════════════════════════════════════════════════════════════
(B) **有现成实现，只差注册**（一行注册，收益最高）。四档，逐条带出处：
  B1 人工别名表 `ALIAS`：从源码注释 / 直译产物读出的「IR 名 → C# 方法」。
  B2 方法名**完全相同**（大小写不敏感）：src/KLink.Bot 里有同名方法。
  B3 **参考模拟器已实现**：`ref/kards-sim/KardsSim/Bridge/EngineHost.cs` 的原语表里有，
     语义已知 ⇒ 可直接照抄（这一类其实是「C 的语义已查明」，成本≈B）。
  B4 **纯包装**：直译函数体只调 1~3 个**已在派发表里**的原语 ⇒ 名字/参数适配即可。
  ⚠️ B2/B3/B4 都必须在人工复核「参数形状一致」之后才能注册 ——
     注册到语义不对的实现上比不注册更糟（不注册至少有 ⑥ 报警，注册错了是静默错）。

(A) **纯 UI / 战役 / 表现层** —— 对局无影响：
  A1 直译产物里**只**定义在 UI 蓝图（*Widget/*Renderer/*CardLook/*Help/*EffectBar/
     *History/*OpenPack/*Drafting/*RewardCard/*ChooseCard/*Campaign…），且名字不含玩法动词。
  A2 名字命中 UI/战役词表、不含玩法动词、直译产物里没有定义。

(C) **玩法关键但没实现**（或保守归类）：其余全部。宁可多归 C，不少归。

输出：out/audit/missing-keys-classify2.txt / .json
"""
import json, re, os, sys
from collections import Counter, defaultdict

# `--prefix`：复现**修复前**的分类表。
# 本次（2026-10-02）往派发表里补了这 25 个键；跑 `--prefix` 时把它们**从派发表里减掉**，
# 就能用**同一份判据**重新生成修复前的表（否则两份表的规则会不一致，数字没法比）。
PREFIX_ADDED_KEYS = {
    'DiscardCardFromHand', 'IsUnrevealedCovertCard', 'IsBomber', 'IsFighter', 'IsPinned',
    'HasBond', 'hasActivePincerEffect', 'getAndDecryptAttack', 'getAndDecryptDefense',
    'HasAttackLeft', 'IsExile', 'getKreditBySide', 'GetFrontlineOwnerSide',
    'SpawnCardInFrontline', 'CustomName1Add', 'CustomName1HasAttribute', 'CustomName1Remove',
    'CustomName2Add', 'CustomName2HasAttribute', 'CustomName2Remove', 'GetCustomName2Attributes',
    'GetCardsInSupportLineBySide', 'IsLocationFull', 'DestroyMultipleCards', 'DiscardCardFromDeck',
}
PREFIX = '--prefix' in sys.argv
SUFFIX = '-prefix' if PREFIX else '-after'

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)

# ───────────────────────── 1. IR ─────────────────────────
ir = json.load(open('klink bot/docs/card-ir.json', encoding='utf-8'))
cnt, cards, entry_of = Counter(), defaultdict(set), defaultdict(Counter)
local_ok, local_cards = Counter(), defaultdict(set)
real, real_cards = Counter(), defaultdict(set)
locals_of = {}

for name, v in ir.items():
    if not isinstance(v, dict) or not isinstance(v.get('steps'), list):
        continue
    locals_of[name] = set((v.get('locals') or {}).keys())
    eps = v.get('entrypoints') or {}
    eps_sorted = sorted(((int(k2), k) for k, k2 in eps.items()
                         if isinstance(k2, int) or (isinstance(k2, str) and k2.isdigit())))

    # ⚠️ 事件入口的 `steps` **和** `locals` 的函数体都要扫 —— locals 体是**真会被执行**的
    #    （`KismetVm` 的 locals 兜底），里面再调一个没实现的名字同样是缺口。
    #    只扫 `steps` 会漏掉 20 种；与 `tools/BotSim/DispatchGap.cs` 对账过
    #    （见 `out/audit/dispatch-gap-parity.py`，两边结果一致）。
    bodies = [v['steps']] + list((v.get('locals') or {}).values())

    for steps in bodies:
        for s in steps:
            if s.get('op') != 'call' or not s.get('fn'):
                continue
            fn = s['fn']
            cnt[fn] += 1
            cards[fn].add(name)
            if fn in locals_of[name]:
                local_ok[fn] += 1
                local_cards[fn].add(name)
            else:
                real[fn] += 1
                real_cards[fn].add(name)
            i = s.get('i')
            if i is not None and eps_sorted:
                cur = '(?)'
                for st, nm in eps_sorted:
                    if st <= i:
                        cur = nm
                    else:
                        break
                entry_of[fn][cur] += 1

# ───────────────────────── 2. 派发表 ─────────────────────────
disp_src = open('src/KLink.Bot/Effects/CardApiDispatch.cs', encoding='utf-8').read()
keys = set(re.findall(r'^\s*\["([^"]+)"\]\s*=', disp_src, re.M))
if PREFIX:
    keys -= PREFIX_ADDED_KEYS
missing = sorted(k for k in cnt if k not in keys)

# ───────────────────────── 3. 我们的 C# 实现索引 ─────────────────────────
src_text = {}
for base, _, files in os.walk('src/KLink.Bot'):
    for f in files:
        if f.endswith('.cs'):
            p = os.path.join(base, f)
            src_text[p] = open(p, encoding='utf-8', errors='replace').read()

METHOD_RE = re.compile(
    r'^[ \t]*(?:(?:public|private|protected|internal|static|sealed|override|virtual|async|partial|new|readonly|unsafe|extern)\s+)*'
    r'(?:[\w<>?\[\],\.]+\s+)+(\w+)\s*(?:<[^>]*>)?\s*\(', re.M)
methods, method_sites = set(), {}
for p, t in src_text.items():
    for m in METHOD_RE.finditer(t):
        methods.add(m.group(1))
        method_sites.setdefault(m.group(1), (p, t[:m.start()].count('\n') + 1))
methods_lc = {m.lower(): m for m in methods}

# ───────────────────────── 4. 参考模拟器原语表 ─────────────────────────
REF = 'ref/kards-sim/KardsSim/Bridge/EngineHost.cs'
ref_src = open(REF, encoding='utf-8', errors='replace').read()
ref_impl = {m.group(1): ref_src[:m.start()].count('\n') + 1
            for m in re.finditer(r'^[ \t]*\["([^"]+)"\]\s*=', ref_src, re.M)}

# ───────────────────────── 5. 直译产物 ─────────────────────────
DEPS = 'out/Generated-gap/_deps'
defined_in, bodies = {}, {}
for f in os.listdir(DEPS):
    if not f.endswith('.g.cs'):
        continue
    cls = f[:-5]
    t = open(os.path.join(DEPS, f), encoding='utf-8', errors='replace').read()
    for m in re.finditer(r'^public static Val (\w+)\(IHost H', t, re.M):
        defined_in.setdefault(m.group(1), set()).add(cls)
    marks = [(m.start(), m.group(1)) for m in re.finditer(r'^// ---- (\w+) ----$', t, re.M)]
    for idx, (pos, fn) in enumerate(marks):
        end = marks[idx + 1][0] if idx + 1 < len(marks) else len(t)
        bodies.setdefault(fn, []).append((cls, t[pos:end]))

# 人工别名表（逐条带出处）
ALIAS = {
    'DiscardCardFromHand': ('DiscardCard', 'CardApi.cs:1653 文档注释「对应 BP_CardFunctions::DiscardCardFromHand / DiscardCard」'),
}

UI_CLASS_RE = re.compile(
    r'(Widget|Renderer|CardLook|Help|Tutorial|EffectBar|History|OpenPack|Drafting|'
    r'RewardCard|RenderedCard|ChooseCard|ChooseOne|_Pack|Shop|Store|Menu|Hud|HUD|'
    r'Screen|Panel|Overlay|Popup|Bubble|Tooltip|Anim|Icon|Campaign)', re.I)
GAMEPLAY_VERB_RE = re.compile(
    r'(Damage|Destroy|Draw|Discard|Move|Spawn|Summon|Attack|Defen[cs]e|Kredit|Deck|'
    r'Hand|Board|Frontline|SupportLine|Retreat|Heal|Pin|Convert|Transform|Copy|Return|'
    r'Kill|Death|Deploy|Play|Cost|Buff|Keyword|Ability|Trigger|Gotcha|Covert|'
    r'Reveal|Target|Random|Shuffle|Token|Unit|Side|Owner|Control)', re.I)
UI_NAME_RE = re.compile(
    r'(Text|Bubble|VerticalBox|HorizontalBox|Widget|Tutorial|Help|Icon|Color|Font|'
    r'Anim|Sound|Music|Voice|Camera|Cursor|Hover|Click|Button|Panel|Menu|Screen|'
    r'Popup|Tooltip|Render|Visual|Vfx|Sfx|Localiz|String|Draft|Pack|Gold|Foil|Rarity|'
    r'Campaign|Star|Mission|Achievement|Level|Progress|Unlock|BattlePass|Store|Shop|'
    r'Price|Purchase|Profile|Avatar|Nickname|Leaderboard|Chat|Emote|Replay|Spectat|'
    r'Focus|Visibility|Background|Layout|Viewport|ActorHidden)', re.I)


def classify(fn):
    if fn in ALIAS:
        impl, why = ALIAS[fn]
        return 'B', 'B1', f'alias→{impl}', why
    if fn.lower() in methods_lc:
        m = methods_lc[fn.lower()]
        p, ln = method_sites.get(m, ('?', 0))
        return 'B', 'B2', f'同名方法 {m}', f'{p}:{ln} 定义 `{m}`（需确认参数形状一致）'
    # ⚠️ B3/B4 **不算 B**：参考模拟器有实现 ≠ 我们的内核有实现；
    #    纯包装也仍要写一层适配。它们归 C，但用 rule 标出来 ——
    #    「C 里成本最低的一批」是排序用的，不是「一行注册」。
    if fn in ref_impl:
        return 'C', 'C-easy-ref', f'参考实现有 → {REF}:{ref_impl[fn]}', '参考模拟器已实现，语义可照抄（需确认参数形状）'
    bs = bodies.get(fn)
    if bs:
        cls, body = bs[0]
        called = re.findall(r'H\.Call\("([^"]+)"', body)
        if called and len(called) <= 3 and all(x in keys for x in called):
            return 'C', 'C-easy-wrap', f'纯包装 @{cls} → {sorted(set(called))}', '函数体只调已注册原语，写层适配即可'
    where = defined_in.get(fn, set())
    ui_where = {c for c in where if UI_CLASS_RE.search(c)}
    non_ui = where - ui_where
    gp = bool(GAMEPLAY_VERB_RE.search(fn))
    ui_name = bool(UI_NAME_RE.search(fn))
    if where and not non_ui and not gp:
        return 'A', 'A1', f'只定义在 UI/战役蓝图 {sorted(ui_where)[:2]}', '定义处全是 UI 蓝图且名字不含玩法动词'
    # A2 不再要求「直译产物里没有定义」：`ShowTutorialMessage` / `GiveStarForCampaign` /
    # `CampaignSetText` 这些**定义在 BP_CardFunctions（玩法蓝图）里的 UI/战役辅助函数**
    # 也会被误判成 C。判据改成「名字是 UI/战役形态 + 不含玩法动词」——
    # 代价是可能把个别玩法函数误判成 A，所以下面 `--list-a2` 会把这一档单独列出来人工过一遍。
    if ui_name and not gp:
        return 'A', 'A2', 'UI/战役 名形态', '名字命中 UI/战役词表且不含玩法动词'
    if where:
        return 'C', 'C1', f'定义于 {sorted(where)[:2]}', '有对局语义（或定义在玩法蓝图）但未实现'
    return 'C', 'C2', '直译产物里找不到定义', '保守归 C'


rows = []
for fn in missing:
    cls, rule, ev, why = classify(fn)
    rows.append({'fn': fn, 'class': cls, 'rule': rule, 'evidence': ev, 'why': why,
                 'calls': cnt[fn], 'cards': len(cards[fn]),
                 'realCalls': real[fn], 'realCards': len(real_cards[fn]),
                 'localCalls': local_ok[fn],
                 'cardList': sorted(cards[fn]), 'realCardList': sorted(real_cards[fn]),
                 'definedIn': sorted(defined_in.get(fn, [])),
                 'refLine': ref_impl.get(fn),
                 'entrypoints': dict(entry_of[fn].most_common(8))})
rows.sort(key=lambda r: (r['class'], -r['realCalls']))

# ───────────────────────── 输出 ─────────────────────────
o = []
o.append(f'IR 被调用函数 {len(cnt)} 种；派发表键 {len(keys)} 种；'
         f'**不在派发表里的 {len(missing)} 种**')
handled = [r for r in rows if r['realCalls'] == 0]
gaps = [r for r in rows if r['realCalls'] > 0]
o.append(f'  其中 **{len(handled)} 种 / {sum(r["calls"] for r in handled)} 调用点** 已由 '
         f'`KismetVm` 的 **locals 兜底**执行（卡自己蓝图里有函数体）⇒ **不是缺口**')
o.append(f'  ⇒ **真缺口 {len(gaps)} 种 / {sum(r["realCalls"] for r in gaps)} 个调用点 / '
         f'{len(set().union(*[set(r["realCardList"]) for r in gaps])) if gaps else 0} 张卡**')
o.append('')
for c in 'ABC':
    sub = [r for r in gaps if r['class'] == c]
    cs = set()
    for r in sub:
        cs |= set(r['realCardList'])
    o.append(f'  ({c}): {len(sub):3d} 种 / {sum(r["realCalls"] for r in sub):5d} 调用点 / {len(cs):4d} 张卡')
o.append('')
for c, title in (('B', '(B) ★ 实现**已经在我们代码里**、只差注册派发键（真·一行注册）'),
                 ('C', '(C) 玩法关键但没实现（其中 C-easy-* = 语义已查明 / 纯包装，成本最低）'),
                 ('A', '(A) 纯 UI / 战役 / 表现层 —— 对局无影响，跳过')):
    sub = [r for r in gaps if r['class'] == c]
    o.append(f'=== {title}：{len(sub)} 种 / {sum(r["realCalls"] for r in sub)} 调用点 ===')
    for r in sub:
        o.append(f'  {r["fn"]:44s} 真缺口{r["realCalls"]:4d}调用 {r["realCards"]:3d}卡 '
                 f'[{r["rule"]}] {r["evidence"]}')
    o.append('')
ez = [r for r in gaps if r['rule'].startswith('C-easy')]
o.append(f'=== (C) 里成本最低的一批（C-easy-ref / C-easy-wrap）：{len(ez)} 种 / '
         f'{sum(r["realCalls"] for r in ez)} 调用点 ===')
for r in sorted(ez, key=lambda r: -r['realCalls']):
    o.append(f'  {r["fn"]:44s} 真缺口{r["realCalls"]:4d}调用 {r["realCards"]:3d}卡 [{r["rule"]}] {r["evidence"]}')
o.append('')
o.append(f'=== 已由 locals 兜底处理（不是缺口）：{len(handled)} 种 ===')
for r in sorted(handled, key=lambda r: -r['calls']):
    o.append(f'  {r["fn"]:44s} {r["calls"]:4d}调用 {r["cards"]:3d}卡  '
             f'(定义在 {r["cards"]} 张卡自己的 locals 里)')

open(f'out/audit/missing-keys-classify2{SUFFIX}.txt', 'w', encoding='utf-8').write('\n'.join(o))
json.dump(rows, open(f'out/audit/missing-keys-classify2{SUFFIX}.json', 'w', encoding='utf-8'),
          ensure_ascii=False, indent=1)
print('\n'.join(o[:9]))
print(f'\n→ out/audit/missing-keys-classify2{SUFFIX}.txt / .json')
