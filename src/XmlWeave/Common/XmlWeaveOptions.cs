using System;

namespace XmlWeave;

/// <summary>读取和写入的资源限制及序列化格式。</summary>
public sealed class XmlWeaveOptions
{
    /// <summary>最大 XML 字符数，默认一千万。</summary>
    public long MaxCharacters { get; init; } = 10_000_000;
    /// <summary>最大元素深度，默认六十四。</summary>
    public int MaxDepth { get; init; } = 64;
    /// <summary>元素之间换行，默认关闭；不在标量文本内添加空白。</summary>
    public bool NewLineOnElements { get; init; }
    /// <summary>对新增的格式化行按元素深度缩进，默认关闭；不单独启用换行。</summary>
    public bool Indent { get; init; }
    /// <summary>每个属性独占一行，默认关闭，可独立于元素换行使用。</summary>
    public bool NewLineOnAttributes { get; init; }
    /// <summary>每级缩进字符，默认两个空格；仅允许空格和制表符。</summary>
    public string IndentChars { get; init; } = "  ";
    /// <summary>格式化换行符，默认 LF；支持 LF、CRLF 和 CR。</summary>
    public string NewLineChars { get; init; } = "\n";
    /// <summary>验证限制及格式参数。</summary>
    public void Validate()
    {
        if (MaxCharacters <= 0 || MaxDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(XmlWeaveOptions));
        ArgumentNullException.ThrowIfNull(IndentChars);
        foreach (char c in IndentChars)
            if (c is not (' ' or '\t')) throw new ArgumentException("缩进仅允许空格和制表符。", nameof(IndentChars));
        if (NewLineChars is not ("\n" or "\r\n" or "\r"))
            throw new ArgumentException("换行符必须为 LF、CRLF 或 CR。", nameof(NewLineChars));
    }
}
