using System.Text;
using System.Text.Json;

namespace KLink.Bot.NN;

/// <summary>
/// 训练出来的 MLP 模型：<c>Dim → hidden → 1</c>，ReLU + sigmoid。
///
/// **自包含**：权重之外还存了
/// <list type="bullet">
/// <item>维度（<see cref="Dim"/> / <see cref="Hidden"/> / 编码器的 3 个常量）</item>
/// <item>编码规格字符串（<see cref="EncoderSpec"/>）—— 只存权重不存这个，加载时对不上是**静默错误**</item>
/// <item>标准化参数 <see cref="Mean"/> / <see cref="Std"/> —— 训练时对特征做了 z-score，推理不带上就全错</item>
/// <item>一段自由 JSON 元数据（<see cref="MetaJson"/>，含验证准确率等，仅供人看）</item>
/// </list>
///
/// 二进制布局（小端）：
/// <code>
///   int32  magic = 0x314D4C4B  ('KLM1')
///   int32  version = 1
///   int32  dim, hidden, cardDim, zones, perSide, activation(0=ReLU)
///   int32  specLen ; utf8 spec
///   int32  metaLen ; utf8 meta
///   float  mean[dim]
///   float  std[dim]
///   float  W1[hidden*dim]     （行主序，第 j 行是隐藏单元 j 的输入权重）
///   float  B1[hidden]
///   float  W2[hidden]
///   float  B2               （输出偏置；本版训练不带，恒 0，但格式里留着）
/// </code>
/// </summary>
public sealed class NnModel
{
    public const int Magic = 0x314D4C4B;   // 'KLM1'
    public const int Version = 1;

    public int Dim { get; init; }
    public int Hidden { get; init; }
    public int CardDim { get; init; }
    public int Zones { get; init; }
    public int PerSide { get; init; }
    public int Activation { get; init; }      // 0 = ReLU

    public string EncoderSpec { get; init; } = "";
    public string MetaJson { get; init; } = "";

    public required float[] Mean { get; init; }
    public required float[] Std { get; init; }
    public required float[] W1 { get; init; }   // hidden × dim
    public required float[] B1 { get; init; }   // hidden
    public required float[] W2 { get; init; }   // hidden
    public float B2 { get; init; }              // 输出偏置

    // ==================== 推理 ====================

    /// <summary>
    /// 从**原始（未归一化）**编码向量算「视角方获胜」的概率。
    /// 归一化用模型自带的 mean/std —— 调用方不需要知道这件事。
    /// </summary>
    public float Predict(ReadOnlySpan<float> rawFeatures)
    {
        CheckDim(rawFeatures.Length);

        var z = new float[Dim];
        NormalizeInto(rawFeatures, z);
        return ForwardNormalized(z);
    }

    /// <summary>原始特征 → z-score（复用缓冲，热路径用）。</summary>
    public void NormalizeInto(ReadOnlySpan<float> raw, Span<float> normalized)
    {
        CheckDim(raw.Length);
        for (int d = 0; d < Dim; d++)
        {
            normalized[d] = (raw[d] - Mean[d]) / Std[d];
        }
    }

    /// <summary>
    /// 前向传播（输入**已经**归一化），返回 **sigmoid 之前的 logit**。
    ///
    /// ⚠️ 这段算式必须和 <c>NNTrain train</c> 里的前向**逐字一致**，
    /// 否则「训练报的准确率」和「实际推理」会对不上。
    /// 校验办法：<c>NNTrain verify --model ... --data ...</c> 用这个函数
    /// 重算验证集准确率，和训练时报的数字比。
    ///
    /// 单独暴露 logit 是因为训练里的判正负用的是 <c>o &gt; 0</c>，
    /// 而 <c>sigmoid(o) &gt; 0.5</c> 在 o 极小时会因浮点舍入判成 false ——
    /// 想逐样本对齐就必须比 logit。
    /// </summary>
    public float ForwardLogitNormalized(ReadOnlySpan<float> z)
    {
        var h = new float[Hidden];
        ForwardHidden(z, h);

        float o = 0;
        for (int j = 0; j < Hidden; j++) o += W2[j] * h[j];
        o += B2;
        return o;
    }

    /// <summary>原始特征 → logit（未过 sigmoid）。</summary>
    public float PredictLogit(ReadOnlySpan<float> rawFeatures)
    {
        CheckDim(rawFeatures.Length);

        var z = new float[Dim];
        NormalizeInto(rawFeatures, z);
        return ForwardLogitNormalized(z);
    }

    public float ForwardNormalized(ReadOnlySpan<float> z)
        => 1f / (1f + MathF.Exp(-ForwardLogitNormalized(z)));                  // sigmoid

    /// <summary>把隐藏层激活写进 <paramref name="h"/>（省一次分配）。</summary>
    public void ForwardHidden(ReadOnlySpan<float> z, Span<float> h)
    {
        for (int j = 0; j < Hidden; j++)
        {
            float s = B1[j];
            int off = j * Dim;
            for (int d = 0; d < Dim; d++) s += W1[off + d] * z[d];
            h[j] = s > 0 ? s : 0;                          // ReLU
        }
    }

    private void CheckDim(int length)
    {
        if (length != Dim)
        {
            throw new ArgumentException($"编码维度 {length} ≠ 模型维度 {Dim}");
        }
    }

    // ==================== 存取 ====================

    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs, Encoding.UTF8);

        bw.Write(Magic);
        bw.Write(Version);
        bw.Write(Dim);
        bw.Write(Hidden);
        bw.Write(CardDim);
        bw.Write(Zones);
        bw.Write(PerSide);
        bw.Write(Activation);

        WriteString(bw, EncoderSpec);
        WriteString(bw, MetaJson);

        foreach (float x in Mean) bw.Write(x);
        foreach (float x in Std) bw.Write(x);
        foreach (float x in W1) bw.Write(x);
        foreach (float x in B1) bw.Write(x);
        foreach (float x in W2) bw.Write(x);
        bw.Write(B2);
    }

    public static NnModel Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs, Encoding.UTF8);

        if (br.ReadInt32() != Magic)
        {
            throw new FormatException($"不是模型文件（magic 不符）: {path}");
        }

        int version = br.ReadInt32();
        if (version != Version)
        {
            throw new FormatException($"模型版本 {version} 本程序不认识（只认 {Version}）");
        }

        int dim = br.ReadInt32();
        int hidden = br.ReadInt32();
        int cardDim = br.ReadInt32();
        int zones = br.ReadInt32();
        int perSide = br.ReadInt32();
        int activation = br.ReadInt32();

        // 编码器常量必须完全对上 —— 对不上说明模型是另一套编码训的，绝不能凑合用
        if (cardDim != StateEncoder.CardDim || zones != StateEncoder.Zones
            || perSide != StateEncoder.PerSide || dim != StateEncoder.Dim)
        {
            throw new FormatException(
                $"模型编码维度与当前编码器不符：模型 (dim={dim},card={cardDim},zones={zones},perSide={perSide}) "
                + $"vs 程序 (dim={StateEncoder.Dim},card={StateEncoder.CardDim},zones={StateEncoder.Zones},perSide={StateEncoder.PerSide})");
        }

        string spec = ReadString(br);
        string meta = ReadString(br);

        var mean = new float[dim];
        var std = new float[dim];
        var w1 = new float[hidden * dim];
        var b1 = new float[hidden];
        var w2 = new float[hidden];
        for (int i = 0; i < dim; i++) mean[i] = br.ReadSingle();
        for (int i = 0; i < dim; i++) std[i] = br.ReadSingle();
        for (int i = 0; i < w1.Length; i++) w1[i] = br.ReadSingle();
        for (int i = 0; i < hidden; i++) b1[i] = br.ReadSingle();
        for (int i = 0; i < hidden; i++) w2[i] = br.ReadSingle();
        float b2 = br.ReadSingle();

        return new NnModel
        {
            Dim = dim, Hidden = hidden, CardDim = cardDim, Zones = zones,
            PerSide = perSide, Activation = activation,
            EncoderSpec = spec, MetaJson = meta,
            Mean = mean, Std = std, W1 = w1, B1 = b1, W2 = w2, B2 = b2,
        };
    }

    /// <summary>把元数据 JSON 解析成可读摘要（给人看的）。</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append($"MLP {Dim}→{Hidden}→1  (ReLU+sigmoid, activation={Activation})");
        if (!string.IsNullOrEmpty(MetaJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(MetaJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("valAccuracy", out var acc)) sb.Append($"  验证准确率 {acc.GetDouble():P1}");
                if (root.TryGetProperty("epochs", out var ep)) sb.Append($"  epochs {ep.GetInt32()}");
                if (root.TryGetProperty("data", out var data)) sb.Append($"  数据 {data.GetString()}");
                if (root.TryGetProperty("trainedAt", out var at)) sb.Append($"  训练于 {at.GetString()}");
            }
            catch (JsonException)
            {
                sb.Append("  （元数据 JSON 解析失败）");
            }
        }
        return sb.ToString();
    }

    private static void WriteString(BinaryWriter bw, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static string ReadString(BinaryReader br)
    {
        int len = br.ReadInt32();
        if (len < 0 || len > 1 << 20) throw new FormatException($"字符串长度异常: {len}");
        return Encoding.UTF8.GetString(br.ReadBytes(len));
    }
}
