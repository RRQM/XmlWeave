using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XmlWeave;

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
