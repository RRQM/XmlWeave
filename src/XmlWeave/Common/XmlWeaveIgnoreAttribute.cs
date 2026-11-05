using System;

namespace XmlWeave;

/// <summary>显式忽略成员。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class XmlWeaveIgnoreAttribute : Attribute { }
