namespace KLink.Bot.Cards;

/// <summary>
/// 内置的目标卡组（用户提供的环境卡组）。
/// 规则内核的验收标准是：**这些卡组能 100% 跑对**，
/// 所以它们同时是测试集和待办清单的来源（见 tools/analyze-decks.py）。
/// </summary>
public static class MetaDecks
{
    public static readonly IReadOnlyList<(string Name, string Code)> All = new (string, string)[]
    {
        ("德芬车",      "%%19|303NE1gVwCxr;2Z3f3X4AoUrKwE;E4nlxoxpza;ux"),
        ("德澳老兵",    "%%2a|0m19DJDSgvoosQsTtXw2y7yJyO;0leFqYwbyc;1VDTy9yd;DR"),
        ("德芬车2",     "%%19|2S2Z303N4AE3iawCxryQ;3XrK;3f4vE4nloUxoza;ux"),
        ("德澳老兵2",   "%%1a|3NgUu0xrxVz9zf;4pE3E4y1y9zdzx;3fz8;4wxXzc"),
        ("英苏中立",    "%%24|0mDDgvooq0sQtVtXyEyJyU;DQE0qYxmyGyZzB;DRzC;tTyH"),
        ("米色团",      "%%25|0m19oow7;bqEbohsDtVw0wa;vSwbyw;DRpUsU"),
        ("日波炸槽",    "%%38|5CjQp2pezq;5B5Z6w6xtFztzw;p1p6zpzs;pczi"),
        ("日澳快攻",    "%%3a|5C6B6yhHrktGwXznzqzvzx;DJE7tFy2;7ltKy9zs;7axX"),
        ("Sid日波情报", "%%38|5C6BE7hHmarktCuyx3zmznzqzv;iDphtGtywXzs;7etK;E9pg"),
        ("日波情报",    "%%38|5C6BhHjomarktCuyx3ziznzqzv;E7iDphtywXzs;tGtK;E9pg"),
        ("苏英爆破",    "%%42|8C9bfrjWppqTt3tmvewqwwyUyXz2z5;DQE0tVxmyZz0z1;DRzC;yH"),
        ("苏澳中速",    "%%4a|8C9bfrjWppqTt3tmvewqwwxYyXz5;8NDJE0xWxXz0z1zx;xmy9zC;"),
        ("自残苏",      "%%48|7M8Cwy;9mthu5;8I8UE5gbxmzC;7Hz0zi"),
        ("美澳跳",      "%%5a|bCbEbibmcPDBDCdkfGmPtYv6vYy7ycyv;DJv7zx;bPy9yd;bKgg"),
        ("美英跳",      "%%52|bCbEbmcPDBDCdkfGr4rctYu8v6v7vUvXvYw2yv;qYw8;bKbPlgyH;DR"),
        ("美澳极限快",  "%%5a|bBDBtYv6vYy3;bqDHjyoh;bOxcyw;DGglvSxX"),
        ("德美",        "%%15|2L2Q2Z31343b3j3m3N3O3YbPgggUlUnDnsoTtdtgu0xrz6;j1nwofoPvh;bKmI;"),
        ("英日",        "%%23|0m0z195NgvoosTwbyEyJ;qYw8;0d1V78oxp3tTyH;5B"),
        ("英美2",       "%%25|0j0m0w0z191xgFgvjzkplroosQsTw2w7w8yJ;08bPj1mIofqYwb;mU;bK"),
        ("日美",        "%%35|5C5R5x636B6Y6Z7b7s7xlfnPrk;5z666CbPjOoeohp4p9x3;bKgL;"),
        ("日法",        "%%36|5C636Bzn;2h2l5z6XEagJnNnQp9tKwh;7a7eg3;2p"),
        ("英芬",        "%%29|0B0D0d0meFgvjen8ngoopUq0rKsQw7;0l1VnjqYrLrPsOsRtWw8wbwc;;"),
    };
}
