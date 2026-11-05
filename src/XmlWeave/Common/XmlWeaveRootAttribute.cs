using System;

namespace XmlWeave;

/// <summary>标记需要生成 XML 序列化器的根类型。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class XmlWeaveRootAttribute : Attribute
{
    /// <summary>根元素名称，默认使用类型名称。</summary>
    public string? Name { get; set; }
}
