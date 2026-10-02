using KLink.Bot.Engine;

namespace KLink.Bot.Cards;

/// <summary>
/// 卡组码解析。格式与服务器端 <c>DeckCodeManager.ParseDeckCode</c> 保持一致：
///
/// <code>
/// %%&lt;主国&gt;&lt;盟国&gt;|&lt;牌组&gt;|&lt;HQ 2 位&gt;
/// </code>
///
/// 牌组部分按 <c>;</c> 拆成**正好 4 组**，组内每 2 字符一个码，
/// 第 i 组的每个码计 i+1 份（倍数 1/2/3/4）。<c>~</c> 之后的内容丢弃。
///
/// 国家码：1Germany 2Britain 3Japan 4Soviet 5USA 6France 7Italy 8Poland 9Finland，
/// 字母是后续新增阵营（a=Anzac 等，见 <see cref="CountryNames"/>）。
/// </summary>
public static class DeckCodeParser
{
    private static readonly int[] Multipliers = { 1, 2, 3, 4 };

    public static readonly IReadOnlyDictionary<char, string> CountryNames = new Dictionary<char, string>
    {
        ['1'] = "Germany",
        ['2'] = "Britain",
        ['3'] = "Japan",
        ['4'] = "Soviet",
        ['5'] = "USA",
        ['6'] = "France",
        ['7'] = "Italy",
        ['8'] = "Poland",
        ['9'] = "Finland",
        ['a'] = "Anzac",
    };

    public sealed record ParsedDeck(
        string Raw,
        char MainCountryCode,
        char AllyCountryCode,
        string MainCountry,
        string AllyCountry,
        string HqCode,
        IReadOnlyDictionary<string, int> ImportIds)
    {
        public int TotalCards => ImportIds.Values.Sum();
        public int UniqueCards => ImportIds.Count;
    }

    public static ParsedDeck Parse(string deckCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deckCode);

        if (!deckCode.StartsWith("%%", StringComparison.Ordinal))
        {
            throw new FormatException("卡组码必须以 %% 开头");
        }

        string body = deckCode[2..];
        string[] parts = body.Split('|');
        if (parts.Length < 2)
        {
            throw new FormatException("卡组码缺少牌组部分");
        }

        string country = parts[0];
        if (country.Length < 2)
        {
            throw new FormatException("卡组码缺少国家位");
        }

        string cards = parts[1];
        int tilde = cards.IndexOf('~');
        if (tilde >= 0)
        {
            cards = cards[..tilde];
        }

        string hq = parts.Length >= 3 ? parts[2] : "0N";

        string[] groups = cards.Split(';');
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int gi = 0; gi < groups.Length && gi < Multipliers.Length; gi++)
        {
            string group = groups[gi];
            for (int j = 0; j + 1 < group.Length; j += 2)
            {
                string importId = group.Substring(j, 2);
                counts[importId] = counts.GetValueOrDefault(importId) + Multipliers[gi];
            }
        }

        char main = country[0];
        char ally = country[1];

        return new ParsedDeck(
            deckCode,
            main,
            ally,
            CountryNames.GetValueOrDefault(main, $"Unknown({main})"),
            CountryNames.GetValueOrDefault(ally, $"Unknown({ally})"),
            hq,
            counts);
    }

    /// <summary>把卡组码展开成卡名列表（含重复）。找不到映射的码会被记录在 <paramref name="unknownIds"/>。</summary>
    public static List<string> Expand(ParsedDeck deck, IReadOnlyDictionary<string, string> deckCodeIds, out List<string> unknownIds)
    {
        var result = new List<string>(deck.TotalCards);
        unknownIds = new List<string>();

        foreach (var (importId, count) in deck.ImportIds)
        {
            if (!deckCodeIds.TryGetValue(importId, out string? cardName))
            {
                unknownIds.Add(importId);
                continue;
            }

            for (int i = 0; i < count; i++)
            {
                result.Add(cardName);
            }
        }

        return result;
    }
}
