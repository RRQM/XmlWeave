using System;

namespace XmlWeave;

/// <summary>覆盖成员名称和表示形式。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class XmlWeaveMemberAttribute : Attribute
{
    /// <summary>XML 名称，默认使用成员名称。</summary>
    public string? Name { get; set; }
    /// <summary>XML 表示形式。</summary>
    public XmlWeaveRepresentation Representation { get; set; }
}
