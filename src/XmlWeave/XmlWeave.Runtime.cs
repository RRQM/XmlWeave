using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XmlWeave;

/// <summary>标记需要生成 XML 序列化器的根类型。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class XmlWeaveRootAttribute : Attribute
{
    /// <summary>根元素名称，默认使用类型名称。</summary>
    public string? Name { get; set; }
}

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

/// <summary>覆盖成员名称和表示形式。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class XmlWeaveMemberAttribute : Attribute
{
    /// <summary>XML 名称，默认使用成员名称。</summary>
    public string? Name { get; set; }
    /// <summary>XML 表示形式。</summary>
    public XmlWeaveRepresentation Representation { get; set; }
}

/// <summary>显式忽略成员。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class XmlWeaveIgnoreAttribute : Attribute { }

/// <summary>为类型指定无状态、公开且具有无参构造函数的转换器。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum)]
public sealed class XmlWeaveConverterAttribute : Attribute
{
    /// <summary>指定转换器类型。</summary>
    public XmlWeaveConverterAttribute(Type converterType) => ConverterType = converterType;
    /// <summary>转换器类型。</summary>
    public Type ConverterType { get; }
}

/// <summary>将自定义类型显式映射为字符串，不需要反射。</summary>
public interface IXmlWeaveConverter<T>
{
    /// <summary>生成文化无关的字符串。</summary>
    string Format(T value);
    /// <summary>解析字符串，格式错误时抛出异常。</summary>
    T Parse(string value);
}

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

/// <summary>包含 XML 路径、行号及列号的格式错误。</summary>
public sealed class XmlWeaveException : FormatException
{
    /// <summary>创建 XML 格式错误。</summary>
    public XmlWeaveException(string message, string path, int line, int column, Exception? inner = null)
        : base($"{message} ({path}, 行 {line}, 列 {column})", inner)
    { Path = path; Line = line; Column = column; }
    /// <summary>错误节点路径。</summary>
    public string Path { get; }
    /// <summary>行号。</summary>
    public int Line { get; }
    /// <summary>列号。</summary>
    public int Column { get; }
}

/// <summary>生成代码使用的安全 XML 基础设施。</summary>
public static class XmlWeaveDocument
{
    /// <summary>空值标记名称。</summary>
    public static readonly XName NilName = XName.Get("nil", "urn:xmlweave:null");

    /// <summary>从字符串读取 XML，禁止 DTD、外部资源和过深内容。</summary>
    public static XElement Read(string xml, XmlWeaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var input = new StringReader(xml);
        return ReadCore(input, null, options);
    }

    /// <summary>从流读取 XML，不关闭调用者的流。</summary>
    public static XElement Read(Stream stream, XmlWeaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ReadCore(null, stream, options);
    }

    private static XElement ReadCore(TextReader? text, Stream? stream, XmlWeaveOptions? options)
    {
        options ??= new XmlWeaveOptions();
        options.Validate();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = options.MaxCharacters,
            IgnoreWhitespace = false,
            CloseInput = false
        };
        try
        {
            using var reader = text is null ? XmlReader.Create(stream!, settings) : XmlReader.Create(text, settings);
            var document = new XDocument();
            var stack = new Stack<XElement>();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (stack.Count >= options.MaxDepth) throw new XmlException("超过最大 XML 深度。");
                    var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
                    var line = (IXmlLineInfo)reader;
                    element.AddAnnotation(new Position(line.LineNumber, line.LinePosition));
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            XName name = reader.Prefix == "xmlns" ? XNamespace.Xmlns + reader.LocalName
                                : reader.Name == "xmlns" ? XName.Get("xmlns") : XName.Get(reader.LocalName, reader.NamespaceURI);
                            element.Add(new XAttribute(name, reader.Value));
                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                    if (stack.Count == 0) document.Add(element); else stack.Peek().Add(element);
                    if (!reader.IsEmptyElement) stack.Push(element);
                }
                else if (reader.NodeType == XmlNodeType.EndElement) stack.Pop();
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
                {
                    if (stack.Count > 0) stack.Peek().Add(new XText(reader.Value));
                }
            }
            return document.Root ?? throw new XmlException("缺少根元素。");
        }
        catch (XmlException ex) { throw new XmlWeaveException(ex.Message, "/", ex.LineNumber, ex.LinePosition, ex); }
    }

    /// <summary>写入字符串，精确保留换行、制表符和特殊字符。</summary>
    public static string Write(XElement element, XmlWeaveOptions? options = null)
    {
        ValidateTree(element, options);
        var result = new StringBuilder();
        using (var writer = XmlWriter.Create(result, WriterSettings())) element.WriteTo(writer);
        return result.ToString();
    }

    /// <summary>以 UTF8 无 BOM 写入流，不关闭调用者的流。</summary>
    public static void Write(Stream stream, XElement element, XmlWeaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var xml = Write(element, options);
        var bytes = new UTF8Encoding(false, true).GetBytes(xml);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static XmlWriterSettings WriterSettings() => new()
    {
        OmitXmlDeclaration = true, Indent = false, NewLineHandling = NewLineHandling.Entitize,
        CheckCharacters = true, Encoding = new UTF8Encoding(false), CloseOutput = false
    };

    private static void ValidateTree(XElement element, XmlWeaveOptions? options)
    {
        options ??= new XmlWeaveOptions(); options.Validate();
        foreach (var child in element.DescendantsAndSelf())
            if (child.Ancestors().Count() >= options.MaxDepth) throw Error(child, "超过最大 XML 深度。");
        // 通过受限读取器检查转义后的实际字符数。
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, WriterSettings())) element.WriteTo(writer);
        if (text.Length > options.MaxCharacters) throw Error(element, "超过最大 XML 字符数。");
    }

    /// <summary>生成包含位置和路径的异常。</summary>
    public static XmlWeaveException Error(XElement element, string message, Exception? inner = null)
    {
        var position = element.Annotation<Position>();
        var path = "/" + string.Join("/", element.AncestorsAndSelf().Reverse().Select(x => x.Name.LocalName));
        return new XmlWeaveException(message, path, position?.Line ?? 0, position?.Column ?? 0, inner);
    }

    /// <summary>检查元素是否是合法空值。</summary>
    public static bool IsNull(XElement element)
    {
        var nil = element.Attribute(NilName);
        if (nil is null) return false;
        if (nil.Value != "true" || element.Nodes().Any() || element.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name != NilName))
            throw Error(element, "空值标记不能与其他内容组合。");
        return true;
    }

    /// <summary>创建空值元素。</summary>
    public static XElement Null(string name) => new(name, new XAttribute(NilName, "true"));

    /// <summary>检查对象节点的未知、重复字段以及非空白文本。</summary>
    public static void CheckObject(XElement element, string attributes, string children)
    {
        var allowedAttributes = new HashSet<string>(attributes.Split('|'), StringComparer.Ordinal);
        var allowedChildren = new HashSet<string>(children.Split('|'), StringComparer.Ordinal);
        var seen = new HashSet<XName>();
        foreach (var attribute in element.Attributes())
            if (!attribute.IsNamespaceDeclaration && (attribute.Name.NamespaceName != "" || !allowedAttributes.Contains(attribute.Name.LocalName)))
                throw Error(element, "未知属性：" + attribute.Name);
        foreach (var child in element.Elements())
            if (child.Name.NamespaceName != "" || !allowedChildren.Contains(child.Name.LocalName) || !seen.Add(child.Name))
                throw Error(child, "未知或重复元素：" + child.Name);
        CheckText(element);
    }

    /// <summary>检查集合节点结构。</summary>
    public static void CheckCollection(XElement element, string childName)
    {
        if (element.Attributes().Any(a => !a.IsNamespaceDeclaration)) throw Error(element, "集合不能包含属性。");
        foreach (var child in element.Elements())
            if (child.Name != childName) throw Error(child, "未知集合元素。");
        CheckText(element);
    }

    private static void CheckText(XElement element)
    {
        if (element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            throw Error(element, "对象或集合不能包含文本。");
    }

    /// <summary>读取严格标量文本。</summary>
    public static string Text(XElement element)
    {
        if (element.HasElements || element.Attributes().Any(a => !a.IsNamespaceDeclaration)) throw Error(element, "标量节点包含未知内容。");
        return element.Value;
    }

    /// <summary>读取必需的子节点。</summary>
    public static XElement Required(XElement element, string name) => element.Element(name) ?? throw Error(element, "缺少元素：" + name);

    /// <summary>保护生成代码免于无限对象图递归。</summary>
    public static void Enter(object value, HashSet<object> active, int depth, XmlWeaveOptions options)
    {
        if (depth >= options.MaxDepth) throw new XmlWeaveException("超过最大对象深度。", "/", 0, 0);
        if (!active.Add(value)) throw new XmlWeaveException("对象图存在循环。", "/", 0, 0);
    }

    private sealed class Position
    {
        public Position(int line, int column) { Line = line; Column = column; }
        public int Line { get; }
        public int Column { get; }
    }
}
