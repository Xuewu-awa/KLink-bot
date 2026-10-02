"""校验 nn-r5-baselines.py 的向量化 build_pairs。

两条独立校验：
  (1) **逐位对照**第四轮 `nn-r4-baselines.py::build_pairs` 的循环版，
      两边都按「赢局样本下标升序」重排后必须**完全相同**（配对内容是唯一确定的）；
  (2) **不变量自检**（不依赖任何参考实现）：每对的单元/胜负/计数/无重复。
"""
import importlib.util
import time

import numpy as np

spec = importlib.util.spec_from_file_location("r5", r"klink bot\tools\nn-r5-baselines.py")
r5 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r5)


def ref_build(pair, turn, y, mask):
    """第四轮的循环版（逐字复制，只把 dict 遍历改成 key 升序以便可复现）。"""
    idx = np.flatnonzero(mask)
    cells = {}
    for i in idx:
        key = (int(pair[i]), int(turn[i]))
        c = cells.get(key)
        if c is None:
            c = cells[key] = ([], [])
        (c[0] if y[i] > 0.5 else c[1]).append(int(i))
    ai, bi = [], []
    for k in sorted(cells):
        w, l = cells[k]
        m = min(len(w), len(l))
        if m:
            ai.extend(w[:m])
            bi.extend(l[:m])
    return np.asarray(ai, dtype=np.int64), np.asarray(bi, dtype=np.int64)


def canon(a, b):
    if len(a) == 0:
        return a, b
    o = np.argsort(a, kind="stable")
    return a[o], b[o]


def invariants(pair, turn, y, mask, a, b, tag):
    """不变量：① 逐对同单元 ② 左必赢右必输 ③ 无重复 ④ 对数 = m 之和 ⑤ 单元内按序配对。"""
    errs = []
    if len(a) != len(b):
        errs.append(f"左右对数不等 {len(a)} vs {len(b)}")
        return errs
    if len(np.unique(a)) != len(a) or len(np.unique(b)) != len(b):
        errs.append("有样本被用了两次")
    ka = pair[a] * 462 + turn[a]
    kb = pair[b] * 462 + turn[b]
    if not np.array_equal(ka, kb):
        errs.append("存在跨单元的配对")
    if not (y[a] > 0.5).all():
        errs.append("左侧出现输局样本")
    if (y[b] > 0.5).any():
        errs.append("右侧出现赢局样本")
    # ④ 与循环版算出的对数一致（按单元求和）
    sel = np.flatnonzero(mask)
    key = pair[sel] * 462 + turn[sel]
    _, grp = np.unique(key, return_inverse=True)
    win = y[sel] > 0.5
    n_all = np.bincount(grp)
    n_w = np.bincount(grp, weights=win.astype(float)).astype(np.int64)
    expect = int(np.minimum(n_w, n_all - n_w).sum())
    if len(a) != expect:
        errs.append(f"对数 {len(a)} ≠ 期望 {expect}")
    # ⑤ 单元内按序配对：单元内第 i 个赢样本 ↔ 单元内第 i 个输样本
    for c in np.unique(ka):
        w = np.sort(a[ka == c])
        l = np.sort(b[kb == c])
        exp_w = np.sort(sel[(key == c) & win])[:len(w)]
        exp_l = np.sort(sel[(key == c) & ~win])[:len(l)]
        if not (np.array_equal(w, exp_w) and np.array_equal(l, exp_l)):
            errs.append(f"单元 {c} 的配对不是「各取前 m 个」")
            break
    return errs


rng = np.random.default_rng(20260926)
bad_ref = bad_inv = 0
for trial in range(200):
    n = int(rng.integers(1, 3000))
    pair = rng.integers(0, 462, n)
    turn = rng.integers(1, 30, n)
    y = (rng.random(n) > 0.42).astype(np.float32)
    mask = rng.random(n) > 0.25

    a1, b1 = r5.build_pairs(pair, turn, y, mask)
    a2, b2 = canon(*ref_build(pair, turn, y, mask))
    a1c, b1c = canon(a1, b1)
    if not (len(a1) == len(a2) and np.array_equal(a1c, a2) and np.array_equal(b1c, b2)):
        bad_ref += 1
        if bad_ref <= 3:
            print(f"  trial {trial}: 与循环版不一致（{len(a1)} vs {len(a2)}）")
    e = invariants(pair, turn, y, mask, a1, b1, f"trial{trial}")
    if e:
        bad_inv += 1
        if bad_inv <= 3:
            print(f"  trial {trial}: 不变量失败 {e}")

print(f"200 组随机数据：与循环版不一致 {bad_ref} 组；不变量失败 {bad_inv} 组")
print("结论:", "✅ 向量化 build_pairs 与第四轮循环版逐位一致，且不变量全部成立"
      if bad_ref == 0 and bad_inv == 0 else "❌ 有问题")

# 规模测试
n = 2_400_000
pair = rng.integers(0, 462, n)
turn = rng.integers(1, 30, n)
y = (rng.random(n) > 0.5).astype(np.float32)
mask = np.ones(n, dtype=bool)
t0 = time.time()
a, b = r5.build_pairs(pair, turn, y, mask)
print(f"240 万样本配对：{len(a):,} 对，耗时 {time.time()-t0:.2f}s")
