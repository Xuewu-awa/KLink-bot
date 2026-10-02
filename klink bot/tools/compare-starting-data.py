"""比较 5 局回放的 starting_data 是否其实是同一份占位数据。"""
import glob
import hashlib
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8")

for path in sorted(glob.glob("docs/live-replays/replay-*.json")):
    if ".actions." in path:
        continue
    d = json.load(open(path, encoding="utf-8"))
    sd = d["starting_info"]["match_and_starting_data"]["starting_data"]
    h = hashlib.sha1(json.dumps(sd, sort_keys=True).encode()).hexdigest()[:12]
    s = d["summary"]
    print("%-34s match=%-7s sha1=%-12s L=%-8s R=%-8s" % (
        os.path.basename(path), s["match_id"], h, s["left_player_tag"], s["right_player_tag"]))
    print("    handL=%s" % [c["card_id"] for c in sd["starting_hand_left"]])
    print("    handR=%s" % [c["card_id"] for c in sd["starting_hand_right"]])
    print("    deckL[:8]=%s  deckR[:8]=%s" % (
        [c["card_id"] for c in sd["deck_left"][:8]],
        [c["card_id"] for c in sd["deck_right"][:8]]))
    print("    pidL=%s pidR=%s nameL=%s nameR=%s" % (
        sd["player_id_left"], sd["player_id_right"],
        sd["left_player_name"], sd["right_player_name"]))
