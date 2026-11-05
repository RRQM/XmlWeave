namespace XmlWeave;

/// <summary>将自定义类型显式映射为字符串，不需要反射。</summary>
public interface IXmlWeaveConverter<T>
{
    /// <summary>生成文化无关的字符串。</summary>
    string Format(T value);
    /// <summary>解析字符串，格式错误时抛出异常。</summary>
    T Parse(string value);
}
