using System;

namespace XmlWeave;

/// <summary>为类型指定无状态、公开且具有无参构造函数的转换器。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum)]
public sealed class XmlWeaveConverterAttribute : Attribute
{
    /// <summary>指定转换器类型。</summary>
    public XmlWeaveConverterAttribute(Type converterType) => ConverterType = converterType;
    /// <summary>转换器类型。</summary>
    public Type ConverterType { get; }
}
