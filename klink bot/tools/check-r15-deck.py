"""查 replay-15 的初始牌库里有没有 card_event_rain1_mist / card_event_storm1_gale。

背景：CS 失败信息说「牌库族：card_event_rain1_mist 不在 Left 的牌库里」，
      挑牌的卡是 card_event_overcast（它没有 GetChooseSpawnCards，所以路由是对的）。
如果回放自己的 deck_left 里就有这张卡 → 是**我们引擎的牌库初始化漏了卡**。
"""
import json
from collections import Counter

REP = r"klink bot\docs\fresh-replays\replay-15.json"
d = json.load(open(REP, encoding="utf-8"))
sd = d["starting_info"]["match_and_starting_data"]["starting_data"]

TARGETS = ["card_event_rain1_mist", "card_event_storm1_gale", "card_event_sunny1_blue_sky",
           "card_event_overcast"]

print("=" * 78)
print("① 回放里左右两侧各区域的内容")
print("=" * 78)
for side in ("left", "right"):
    print(f"  [{side}]")
    for zone in ("starting_hand", "deck"):
        key = f"{zone}_{side}"
        arr = sd.get(key) or []
        names = Counter(c.get("name") for c in arr)
        print(f"    {key:<20} {len(arr):>2} 张")
        for t in TARGETS:
            if t in names:
                print(f"        ★ 含 {t} ×{names[t]}")
    loc = sd.get(f"location_card_{side}")
    if loc:
        print(f"    location_card_{side:<6} {loc.get('name')}")

print()
print("=" * 78)
print("② 结论")
print("=" * 78)
all_names = Counter()
for side in ("left", "right"):
    for zone in ("starting_hand", "deck"):
        for c in sd.get(f"{zone}_{side}") or []:
            all_names[(side, c.get("name"))] += 1

for t in TARGETS[:3]:
    inleft = all_names.get(("left", t), 0)
    inright = all_names.get(("right", t), 0)
    print(f"  {t:<32} 左={inleft} 右={inright}")

print()
print("  ⇒ 如果左方有 rain1_mist，而引擎说'不在左方牌库里' → 引擎初始化漏卡")
print("  ⇒ 如果左方没有，那这张卡是局中来的（要另查来源）")

print()
print("=" * 78)
print("③ 左方牌库/手牌全表（看有没有别的可疑）")
print("=" * 78)
for zone in ("starting_hand_left", "deck_left"):
    arr = sd.get(zone) or []
    print(f"  {zone} ({len(arr)} 张):")
    for c in arr[:45]:
        print(f"     card_id={c.get('card_id'):<6} loc_num={c.get('location_number'):<3} {c.get('name')}")
