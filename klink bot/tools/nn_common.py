"""
NN 诊断脚本的共用件：**逐位复刻 C# 侧的切分**，让 Python 能在「与 NNTrain 完全同一份留出局」
上重算基线。

为什么必须复刻（而不是另切一份）：
  `tools/NNTrain/Program.cs` 的 Train/Verify 用
      var splitRng = new Random(splitSeed);
      var gameIds  = gids.Distinct().OrderBy(_ => splitRng.Next()).ToArray();
      int nValGames = max(1, (int)(gameIds.Length * 0.15));
      var valGames  = gameIds.Take(nValGames);
  切出来的 1500 局就是「留出集」。若 Python 自己另切一份，模型留出准确率与基线准确率
  就落在**不同的样本**上，两个数没法并列比较。

复刻的可靠性由 `nn-split-check.py` 反证：用 v1-noeffects 的已知数字
（`out/_verify-v1.txt`：验证集准确率 76.29% = 24,056/31,532）对账，
逐样本一致才说明 RNG 复刻成功。

⚠️ 复刻的是 .NET 的 **Net5CompatSeedImpl**（`new Random(seed)` 的兼容实现），
   算法出处：dotnet/runtime `System/Random.CompatSeedImpl.cs`（减法发生器，Knuth）。
"""

import struct


# ==================== .NET Random(seed) ====================

class DotNetRandom:
    """`new System.Random(seed)`（.NET 5+ 的 Net5CompatSeedImpl）的逐位复刻。"""

    __slots__ = ("_seed_array", "_inext", "_inextp")

    _INT_MAX = 0x7FFFFFFF

    def __init__(self, seed: int):
        imax = self._INT_MAX
        subtraction = imax if seed == -2**31 else abs(seed)
        mj = 161803398 - subtraction
        sa = [0] * 56
        sa[55] = mj
        mk = 1
        ii = 0
        for _ in range(1, 55):
            ii += 21
            if ii >= 55:
                ii -= 55
            sa[ii] = mk
            mk = mj - mk
            if mk < 0:
                mk += imax
            mj = sa[ii]
        for _ in range(1, 5):
            for i in range(1, 56):
                n = i + 30
                if n >= 55:
                    n -= 55
                sa[i] -= sa[1 + n]
                if sa[i] < 0:
                    sa[i] += imax
        self._seed_array = sa
        self._inext = 0
        self._inextp = 21

    def _internal_sample(self) -> int:
        sa = self._seed_array
        inext = self._inext + 1
        if inext >= 56:
            inext = 1
        inextp = self._inextp + 1
        if inextp >= 56:
            inextp = 1
        ret = sa[inext] - sa[inextp]
        if ret == self._INT_MAX:
            ret -= 1
        if ret < 0:
            ret += self._INT_MAX
        sa[inext] = ret
        self._inext = inext
        self._inextp = inextp
        return ret

    def next(self) -> int:
        """`Random.Next()`（无参重载）= InternalSample()，范围 [0, int.MaxValue-1]。"""
        return self._internal_sample()


def nntrain_split(gids, split_seed: int):
    """复刻 NNTrain 的按局切分。

    :param gids: 每条样本的对局号（float32 也可，内部转 int）
    :param split_seed: `--split-seed N`；N < 0 时 C# 走 Random.Shared（不可复现），这里拒绝
    :return: (val_mask, train_mask, val_games_ordered)
    """
    if split_seed < 0:
        raise ValueError("split_seed < 0 时 C# 用 Random.Shared（不可复现），无法复刻")

    gl = [int(g) for g in gids]
    uniq = []
    seen = set()
    for g in gl:                       # gids.Distinct() 保持首次出现顺序
        if g not in seen:
            seen.add(g)
            uniq.append(g)

    rng = DotNetRandom(split_seed)
    # LINQ OrderBy 是**稳定**排序：键相同时保持原顺序（Random.Next() 有极小概率撞键）
    keyed = [(rng.next(), i, g) for i, g in enumerate(uniq)]
    keyed.sort(key=lambda t: t[0])
    ordered = [g for _, _, g in keyed]

    n_val_games = max(1, int(len(ordered) * 0.15))
    val_games = set(ordered[:n_val_games])

    import numpy as np
    garr = np.asarray(gl, dtype=np.int64)
    val_mask = np.isin(garr, np.fromiter(val_games, dtype=np.int64, count=len(val_games)))
    return val_mask, ~val_mask, ordered[:n_val_games]


# ==================== 模型文件（NnModel 的格式，见 src/KLink.Bot/NN/NnModel.cs）====================

def load_model(path: str):
    """读 `NnModel.Save` 写出的文件，返回 dict（含 mean/std/W1/B1/W2/B2 与元数据）。"""
    import json
    import numpy as np

    buf = open(path, "rb").read()
    magic, ver, dim, hidden, card_dim, zones, per_side, activation = struct.unpack_from("<8i", buf, 0)
    if magic != 0x314D4C4B:
        raise ValueError(f"不是模型文件（magic={magic:#x}）: {path}")
    if ver != 1:
        raise ValueError(f"不认识的模型版本 {ver}")
    o = 32
    spec_len, = struct.unpack_from("<i", buf, o)
    o += 4
    spec = buf[o:o + spec_len].decode("utf-8")
    o += spec_len
    meta_len, = struct.unpack_from("<i", buf, o)
    o += 4
    meta = json.loads(buf[o:o + meta_len].decode("utf-8"))
    o += meta_len

    def take(cnt):
        nonlocal o
        a = np.frombuffer(buf, dtype="<f4", count=cnt, offset=o).astype(np.float32)
        o += 4 * cnt
        return a

    mean = take(dim)
    std = take(dim)
    w1 = take(hidden * dim).reshape(hidden, dim)
    b1 = take(hidden)
    w2 = take(hidden)
    b2, = struct.unpack_from("<f", buf, o)
    return dict(path=path, dim=dim, hidden=hidden, cardDim=card_dim, zones=zones,
                perSide=per_side, activation=activation, spec=spec, meta=meta,
                mean=mean, std=std, W1=w1, B1=b1, W2=w2, B2=b2)


def forward_logit(model, X):
    """与 `NnModel.ForwardLogitNormalized` 同算式：z-score → ReLU(W1 z + B1) → W2·h + B2。"""
    import numpy as np
    z = (X - model["mean"]) / model["std"]
    h = np.maximum(z @ model["W1"].T + model["B1"], 0.0)
    return h @ model["W2"] + model["B2"]


# ==================== 数据文件（NNTrain dump 的格式）====================

MAGIC = 0x314C4B41        # 'AKL1'：v1 行格式 = v[dim] + label + gid（8 字节头）
MAGIC_V2 = 0x324C4B41     # 'AKL2'：v2 行格式 = v[dim] + outcome + gid + pairId（12 字节头）


def open_data(path: str):
    """打开 dump 文件，返回 (memmap(n, dim+2), dim, n)。memmap 是只读视图，不占内存。"""
    import os
    import struct

    import numpy as np

    size = os.path.getsize(path)
    with open(path, "rb") as f:
        magic, dim = struct.unpack("<ii", f.read(8))
    if magic != MAGIC:
        raise ValueError(f"不是 v1 NNTrain dump 文件: {path}（magic={magic:#x}）"
                         " —— v2 数据请用 open_data2()")
    rec_f = dim + 2
    if (size - 8) % (rec_f * 4) != 0:
        raise ValueError("文件长度除不尽，格式不对")
    n = (size - 8) // (rec_f * 4)
    rec = np.memmap(path, dtype=np.float32, mode="r", offset=8, shape=(n, rec_f))
    return rec, dim, n


def open_data2(path: str):
    """打开 **v2** dump 文件（magic 'AKL2'，第四轮）。

    格式：``int32 magic, int32 dim, int32 deckCount`` + 每条
    ``float v[dim], float outcome, float gid, float pairId``。

    返回 ``(memmap(n, dim+3), dim, n, deck_count)``。列含义：
      ``rec[:, :dim]`` 编码；``rec[:, dim]`` 最终胜负（左方胜 = 1）；
      ``rec[:, dim+1]`` 对局号；``rec[:, dim+2]`` 对位编号（= 左卡组下标 × deckCount + 右下标）。
    """
    import os
    import struct

    import numpy as np

    size = os.path.getsize(path)
    with open(path, "rb") as f:
        magic, dim, decks = struct.unpack("<iii", f.read(12))
    if magic == MAGIC:
        raise ValueError(f"这是 v1 数据（magic 'AKL1'）: {path} —— 请用 open_data()")
    if magic != MAGIC_V2:
        raise ValueError(f"不是 v2 NNTrain dump 文件: {path}（magic={magic:#x}）")
    rec_f = dim + 3
    if (size - 12) % (rec_f * 4) != 0:
        raise ValueError("文件长度除不尽，格式不对")
    n = (size - 12) // (rec_f * 4)
    rec = np.memmap(path, dtype=np.float32, mode="r", offset=12, shape=(n, rec_f))
    return rec, dim, n, decks


def sigmoid(x):
    import numpy as np
    return 1.0 / (1.0 + np.exp(-np.clip(x, -30, 30)))
