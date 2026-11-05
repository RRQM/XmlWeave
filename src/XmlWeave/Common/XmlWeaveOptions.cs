using System;

namespace XmlWeave;

/// <summary>读取和写入的资源限制。</summary>
public sealed class XmlWeaveOptions
{
    /// <summary>最大 XML 字符数，默认一千万。</summary>
    public long MaxCharacters { get; init; } = 10_000_000;
    /// <summary>最大元素深度，默认六十四。</summary>
    public int MaxDepth { get; init; } = 64;
    /// <summary>验证限制参数。</summary>
    public void Validate()
    {
        if (MaxCharacters <= 0 || MaxDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(XmlWeaveOptions));
    }
}
