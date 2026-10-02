#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P1：在 out/bp-*.json 里找谁调用了某个函数名。"""
import json, glob, os, sys

needle = sys.argv[1]
for f in sorted(glob.glob('out/bp-*.json')):
    try:
        d = json.load(open(f, encoding='utf-8'))
    except Exception as e:
        continue
    if not isinstance(d, dict):
        continue
    for k, v in d.items():
        s = json.dumps(v, ensure_ascii=False)
        c = s.count(needle)
        if c:
            print(f'{os.path.basename(f)} :: {k} :: cnt={c}')
