namespace XmlWeave;

/// <summary>成员的 XML 表示形式。</summary>
public enum XmlWeaveRepresentation
{
    /// <summary>标量使用属性，其他类型使用子元素。</summary>
    Auto,
    /// <summary>使用 XML 属性，仅适用于标量。</summary>
    Attribute,
    /// <summary>使用 XML 子元素。</summary>
    Element
}
