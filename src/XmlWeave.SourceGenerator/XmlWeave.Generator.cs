using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace XmlWeave.SourceGenerator;

/// <summary>为显式标记的模型生成不使用运行时反射的 XML 读写代码。</summary>
[Generator(LanguageNames.CSharp)]
public sealed class XmlWeaveGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor Unsupported = new DiagnosticDescriptor(
        "XW001", "不支持的 XML 模型", "{0}", "XmlWeave", DiagnosticSeverity.Error, true);
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>注册按根类型增量生成的处理流程。</summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var roots = context.SyntaxProvider.ForAttributeWithMetadataName("XmlWeave.XmlWeaveRootAttribute",
            (node, _) => node is ClassDeclarationSyntax,
            (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol);
        context.RegisterSourceOutput(roots, (production, root) =>
        {
            try
            {
                var builder = new Builder(root);
                var source = builder.Build();
                production.AddSource(builder.HintName, SourceText.From(source, Encoding.UTF8));
            }
            catch (ModelException error)
            {
                production.ReportDiagnostic(Diagnostic.Create(Unsupported,
                    error.Symbol.Locations.FirstOrDefault(), error.Message));
            }
        });
    }

    private sealed class ModelException : Exception
    {
        public ModelException(ISymbol symbol, string message) : base(message) { Symbol = symbol; }
        public ISymbol Symbol { get; }
    }

    private sealed class Member
    {
        public ISymbol Symbol = null!;
        public ITypeSymbol Type = null!;
        public string Name = "";
        public bool Attribute;
    }

    private sealed class Builder
    {
        private readonly INamedTypeSymbol root;
        private readonly List<ITypeSymbol> types = new List<ITypeSymbol>();
        private readonly Dictionary<ITypeSymbol, int> ids = new Dictionary<ITypeSymbol, int>(SymbolEqualityComparer.IncludeNullability);
        private readonly Dictionary<ITypeSymbol, List<Member>> members = new Dictionary<ITypeSymbol, List<Member>>(SymbolEqualityComparer.Default);
        private readonly StringBuilder code = new StringBuilder();
        public Builder(INamedTypeSymbol root) { this.root = root; }
        public string HintName
        {
            get
            {
                var name = root.Name.Length > 32 ? root.Name.Substring(0, 32) : root.Name;
                using (var sha256 = SHA256.Create())
                {
                    var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(root.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
                    var suffix = string.Concat(hash.Take(16).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
                    return name + "." + suffix + ".g.cs";
                }
            }
        }
        private static string Type(ITypeSymbol type) => type.ToDisplayString(TypeFormat);
        private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);
        private static AttributeData? Attribute(ISymbol symbol, string name) => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "XmlWeave." + name);
        private static string? NameOverride(AttributeData? attr) => attr?.NamedArguments.FirstOrDefault(p => p.Key == "Name").Value.Value as string;
        private static void Fail(ISymbol symbol, string message) => throw new ModelException(symbol, symbol.ToDisplayString() + "：" + message);
        private static bool Accessible(ISymbol symbol) => symbol.DeclaredAccessibility == Accessibility.Public || symbol.DeclaredAccessibility == Accessibility.Internal || symbol.DeclaredAccessibility == Accessibility.ProtectedOrInternal;
        private static bool NullableValue(ITypeSymbol type) => type is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        private static bool CanNull(ITypeSymbol type) => NullableValue(type) || type.IsReferenceType && type.NullableAnnotation != NullableAnnotation.NotAnnotated;
        private static ITypeSymbol Underlying(ITypeSymbol type) => NullableValue(type) ? ((INamedTypeSymbol)type).TypeArguments[0] : type;
        private static string Identity(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString();
        private static INamedTypeSymbol? Converter(ITypeSymbol type)
            => Attribute(type, "XmlWeaveConverterAttribute")?.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol;
        private static bool Scalar(ITypeSymbol type)
        {
            type = Underlying(type);
            if (Converter(type) != null || type.TypeKind == TypeKind.Enum) return true;
            switch (type.SpecialType)
            {
                case SpecialType.System_String: case SpecialType.System_Boolean: case SpecialType.System_Byte:
                case SpecialType.System_SByte: case SpecialType.System_Int16: case SpecialType.System_UInt16:
                case SpecialType.System_Int32: case SpecialType.System_UInt32: case SpecialType.System_Int64:
                case SpecialType.System_UInt64: case SpecialType.System_Single: case SpecialType.System_Double:
                case SpecialType.System_Decimal: case SpecialType.System_Char: case SpecialType.System_DateTime: return true;
            }
            return Identity(type) == "System.Guid" || Identity(type) == "System.TimeSpan" || Identity(type) == "System.DateTimeOffset" || Identity(type) == "System.DateOnly" || Identity(type) == "System.TimeOnly";
        }
        private static int Kind(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol) return 1;
            if (type is INamedTypeSymbol n)
            {
                var name = n.OriginalDefinition.ToDisplayString();
                if (name == "System.Collections.Generic.List<T>") return 2;
                if (name == "System.Collections.Generic.Dictionary<TKey, TValue>") return 3;
            }
            return 0;
        }
        private static ITypeSymbol Item(ITypeSymbol type) => type is IArrayTypeSymbol a ? a.ElementType : ((INamedTypeSymbol)type).TypeArguments[0];
        private int Add(ITypeSymbol type)
        {
            if (ids.TryGetValue(type, out var existing)) return existing;
            var index = types.Count; ids.Add(type, index); types.Add(type);
            if (NullableValue(type)) { Add(Underlying(type)); return index; }
            if (Converter(type) is INamedTypeSymbol converter)
            {
                var contract = converter.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "XmlWeave.IXmlWeaveConverter<T>" && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], type));
                if (!contract || converter.IsAbstract || converter.IsGenericType || !Accessible(converter) || !converter.InstanceConstructors.Any(c => c.Parameters.Length == 0 && Accessible(c)))
                    Fail(type, "转换器必须实现 IXmlWeaveConverter<T>，并且可访问、非抽象且具有可访问的无参构造函数。");
                return index;
            }
            if (Scalar(type)) return index;
            var kind = Kind(type);
            if (kind != 0)
            {
                if (type is IArrayTypeSymbol array && array.Rank != 1) Fail(type, "仅支持一维数组。");
                Add(Item(type));
                if (kind == 3)
                {
                    if (!Scalar(Item(type)) || CanNull(Item(type))) Fail(type, "字典键必须是非空标量。");
                    Add(((INamedTypeSymbol)type).TypeArguments[1]);
                }
                return index;
            }
            if (type.SpecialType != SpecialType.None || (type.ContainingNamespace.ToDisplayString() == "System" || type.ContainingNamespace.ToDisplayString().StartsWith("System.", StringComparison.Ordinal)) || type is not INamedTypeSymbol model || model.TypeKind != TypeKind.Class || model.IsAbstract || model.IsGenericType || model.IsStatic || !Accessible(model) || model.ContainingType != null)
                Fail(type, "仅支持可访问的非抽象顶级非泛型类、已知标量、List<T>、一维数组和 Dictionary<TKey,TValue>；其他类型请使用转换器。");
            var named = (INamedTypeSymbol)type;
            if (!named.InstanceConstructors.Any(c => c.Parameters.Length == 0 && Accessible(c))) Fail(type, "模型需要可访问的无参构造函数。");
            var list = new List<Member>();
            var clrNames = new HashSet<string>(StringComparer.Ordinal);
            var overridden = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var xmlNames = new HashSet<string>(StringComparer.Ordinal);
            for (INamedTypeSymbol? current = named; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    if (member.IsStatic || member.IsImplicitlyDeclared || member.DeclaredAccessibility != Accessibility.Public || Attribute(member, "XmlWeaveIgnoreAttribute") != null) continue;
                    ITypeSymbol memberType;
                    if (member is IPropertySymbol property)
                    {
                        if (overridden.Contains(property)) continue;
                        for (var ancestor = property.OverriddenProperty; ancestor != null; ancestor = ancestor.OverriddenProperty) overridden.Add(ancestor);
                        if (property.IsIndexer || property.GetMethod == null || property.SetMethod == null || property.GetMethod.DeclaredAccessibility != Accessibility.Public || property.SetMethod.DeclaredAccessibility != Accessibility.Public || property.SetMethod.IsInitOnly || property.IsRequired)
                            Fail(member, "公开属性必须有公开 getter/setter，不能是索引器、init 或 required；可使用 XmlWeaveIgnore 显式忽略。");
                        memberType = property.Type;
                    }
                    else if (member is IFieldSymbol field)
                    {
                        if (field.IsReadOnly || field.IsRequired) Fail(member, "公开字段不能是 readonly 或 required。");
                        memberType = field.Type;
                    }
                    else continue;
                    if (!clrNames.Add(member.Name)) Fail(member, "不支持隐藏继承成员。");
                    var attr = Attribute(member, "XmlWeaveMemberAttribute");
                    var name = NameOverride(attr) ?? member.Name;
                    CheckName(member, name);
                    if (!xmlNames.Add(name)) Fail(member, "XML 名称冲突：" + name);
                    var representation = attr?.NamedArguments.FirstOrDefault(p => p.Key == "Representation").Value.Value;
                    var value = representation is int v ? v : 0;
                    if (value < 0 || value > 2) Fail(member, "未知表示形式。");
                    var asAttribute = value == 1 || value == 0 && Scalar(memberType);
                    if (asAttribute && !Scalar(memberType)) Fail(member, "复杂成员不能表示为属性。");
                    list.Add(new Member { Symbol = member, Type = memberType, Name = name, Attribute = asAttribute });
                    Add(memberType);
                }
            }
            members[type] = list;
            return index;
        }

        private static void CheckName(ISymbol symbol, string name)
        {
            // 不依赖运行时 XML 程序集，在编译阶段限制到可移植的 NCName 子集。
            if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || name.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) || name == "xmlns")
                Fail(symbol, "XML 名称必须是无命名空间的有效名称（字母/下划线开头，其后可含数字、短横线和点）。");
        }

        public string Build()
        {
            if (root.ContainingType != null || root.IsGenericType) Fail(root, "根类型必须为非泛型顶级类。");
            if (root.ContainingNamespace.GetTypeMembers(root.Name + "XmlSerializer").Length != 0)
                Fail(root, "生成的序列化器名称与现有类型冲突：" + root.Name + "XmlSerializer");
            var rootName = NameOverride(Attribute(root, "XmlWeaveRootAttribute")) ?? root.Name;
            CheckName(root, rootName);
            var rootType = root.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            Add(rootType);
            code.AppendLine("// <auto-generated/>").AppendLine("#nullable enable");
            code.AppendLine("using System;").AppendLine("using System.Buffers;");
            if (!root.ContainingNamespace.IsGlobalNamespace) code.Append("namespace ").Append(root.ContainingNamespace.ToDisplayString()).AppendLine(";");
            code.AppendLine("/// <summary>编译期生成的强类型 XML 序列化器。</summary>");
            code.Append(root.DeclaredAccessibility == Accessibility.Public ? "public" : "internal").Append(" static class ").Append(root.Name).AppendLine("XmlSerializer {");
            var t = Type(rootType); var rn = Literal(rootName);
            code.AppendLine("private static readonly global::System.Text.UTF8Encoding Utf8 = new(false, true);");
            code.AppendLine("/// <summary>将模型序列化为 XML 字符串。</summary>");
            code.Append("public static string Serialize(").Append(t).AppendLine(" value, global::XmlWeave.XmlWeaveOptions? options = null) { var buffer = new ArrayBufferWriter<byte>(); Serialize(buffer, value, options); return global::System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan); }");
            code.AppendLine("/// <summary>直接向调用者缓冲区写入 UTF8 XML。</summary>");
            code.Append("public static void Serialize(IBufferWriter<byte> output, ").Append(t).AppendLine(" value, global::XmlWeave.XmlWeaveOptions? options = null) { var writer = new global::XmlWeave.XmlWeaveWriter(output, options); try { W0(ref writer, value, " + rn + "); } finally { writer.Dispose(); } }");
            code.AppendLine("/// <summary>将模型以 UTF8 无 BOM 写入流，不关闭流。</summary>");
            code.Append("public static void Serialize(global::System.IO.Stream stream, ").Append(t).AppendLine(" value, global::XmlWeave.XmlWeaveOptions? options = null) { using var output = new global::XmlWeave.XmlWeaveStreamBufferWriter(stream); Serialize(output, value, options); }");
            code.AppendLine("/// <summary>从 XML 字符串读取模型。</summary>");
            code.Append("public static ").Append(t).AppendLine(" Deserialize(string xml, global::XmlWeave.XmlWeaveOptions? options = null) { global::System.ArgumentNullException.ThrowIfNull(xml); try { return Deserialize(Utf8.GetBytes(xml), options); } catch (global::System.Text.EncoderFallbackException ex) { throw new global::XmlWeave.XmlWeaveException(\"无效 UTF16 字符。\", \"/\", 0, 0, ex); } }");
            code.AppendLine("/// <summary>直接从 UTF8 缓冲区读取模型。</summary>");
            code.Append("public static ").Append(t).AppendLine(" Deserialize(ReadOnlySpan<byte> utf8, global::XmlWeave.XmlWeaveOptions? options = null) { var reader = new global::XmlWeave.XmlWeaveReader(utf8, options); try { if (!reader.Read() || reader.Token != global::XmlWeave.XmlWeaveToken.StartElement || !reader.NameEquals(" + rn + ")) throw reader.Error(\"根元素名称不匹配。\"); var result = R0(ref reader); reader.Finish(); return result; } finally { reader.Dispose(); } }");
            code.AppendLine("/// <summary>从分段 UTF8 缓冲区读取；跨段输入使用可归还缓冲区。</summary>");
            code.Append("public static ").Append(t).AppendLine(" Deserialize(in ReadOnlySequence<byte> utf8, global::XmlWeave.XmlWeaveOptions? options = null) { options?.Validate(); long limit = options?.MaxCharacters ?? 10000000; if (limit <= int.MaxValue / 4 && utf8.Length > limit * 4L + 3) throw new global::XmlWeave.XmlWeaveException(\"超过最大 XML 字符数。\", \"/\", 0, 0); if (utf8.IsSingleSegment) return Deserialize(utf8.FirstSpan, options); int length = checked((int)utf8.Length); var buffer = ArrayPool<byte>.Shared.Rent(length); try { utf8.CopyTo(buffer); return Deserialize(buffer.AsSpan(0, length), options); } finally { ArrayPool<byte>.Shared.Return(buffer); } }");
            code.AppendLine("/// <summary>从流读取模型，不关闭流；使用有上限的可归还输入缓冲区。</summary>");
            code.Append("public static ").Append(t).AppendLine(" Deserialize(global::System.IO.Stream stream, global::XmlWeave.XmlWeaveOptions? options = null) { global::System.ArgumentNullException.ThrowIfNull(stream); options?.Validate(); long limit = options?.MaxCharacters ?? 10000000; var buffer = ArrayPool<byte>.Shared.Rent(4096); int count = 0; try { while (true) { if (count == buffer.Length) { var next = ArrayPool<byte>.Shared.Rent(checked(buffer.Length * 2)); buffer.AsSpan(0, count).CopyTo(next); ArrayPool<byte>.Shared.Return(buffer); buffer = next; } int read = stream.Read(buffer.AsSpan(count)); if (read == 0) break; count += read; if (limit <= int.MaxValue / 4 && count > limit * 4L) throw new global::XmlWeave.XmlWeaveException(\"超过最大 XML 字符数。\", \"/\", 0, 0); } return Deserialize(buffer.AsSpan(0, count), options); } finally { ArrayPool<byte>.Shared.Return(buffer); } }");
            for (var i = 0; i < types.Count; i++) GenerateType(i, types[i]);
            return code.AppendLine("}").ToString();
        }

        private string Format(ITypeSymbol type, string value)
        {
            type = Underlying(type);
            if (Converter(type) is INamedTypeSymbol converter) return "C" + ids[type] + ".Format(" + value + ")";
            if (type.TypeKind == TypeKind.Enum) return Format(((INamedTypeSymbol)type).EnumUnderlyingType!, "(" + Type(((INamedTypeSymbol)type).EnumUnderlyingType!) + ")" + value);
            if (type.SpecialType == SpecialType.System_String) return value;
            if (type.SpecialType == SpecialType.System_Char) value = "(ushort)" + value;
            return "global::XmlWeave.XmlWeaveScalar.Format(" + value + ", scratch)";
        }

        private string Parse(ITypeSymbol type, string value)
        {
            type = Underlying(type);
            if (Converter(type) != null) return "C" + ids[type] + ".Parse(" + value + ".ToString())";
            if (type.TypeKind == TypeKind.Enum) return "(" + Type(type) + ")" + Parse(((INamedTypeSymbol)type).EnumUnderlyingType!, value);
            if (type.SpecialType == SpecialType.System_String) return value + ".ToString()";
            if (type.SpecialType == SpecialType.System_Char) return "(char)global::XmlWeave.XmlWeaveScalar.Parse<ushort>(" + value + ")";
            return "global::XmlWeave.XmlWeaveScalar.Parse<" + Type(type) + ">(" + value + ")";
        }

        private void GenerateType(int id, ITypeSymbol type)
        {
            var t = Type(type); var kind = Kind(type);
            if (Converter(type) is INamedTypeSymbol converter)
                code.Append("private static readonly global::XmlWeave.IXmlWeaveConverter<").Append(t).Append("> C").Append(id).Append(" = new ").Append(Type(converter)).AppendLine("();");
            code.Append("private static void W").Append(id).Append("(ref global::XmlWeave.XmlWeaveWriter w, ").Append(t).AppendLine(" v, string name) {");
            code.AppendLine("Span<char> scratch = stackalloc char[256];");
            if (CanNull(type)) code.AppendLine("if (v is null) { w.NullElement(name); return; }");
            else if (type.IsReferenceType) code.AppendLine("if (v is null) throw new global::XmlWeave.XmlWeaveException(\"非空成员不能为 null。\", name, 0, 0);");
            if (NullableValue(type))
                code.Append("W").Append(ids[Underlying(type)]).AppendLine("(ref w, v.Value, name);");
            else if (Scalar(type))
                code.Append("w.StartElement(name); w.EndStartElement(); w.Text(").Append(Format(type, "v")).AppendLine("); w.EndElement(name);");
            else
            {
                code.Append("if (v.GetType() != typeof(").Append(t.TrimEnd('?')).AppendLine(")) throw new global::XmlWeave.XmlWeaveException(\"不支持多态运行时类型。\", name, 0, 0);");
                code.AppendLine("w.Enter(v); try { w.StartElement(name);");
                if (kind != 0) code.AppendLine("w.EndStartElement();");
                if (kind == 1 || kind == 2)
                    code.Append("foreach (var item in v) W").Append(ids[Item(type)]).AppendLine("(ref w, item, \"Item\");");
                else if (kind == 3)
                {
                    var named = (INamedTypeSymbol)type;
                    code.Append("if (!v.Comparer.Equals(global::System.Collections.Generic.EqualityComparer<").Append(Type(Item(type))).AppendLine(">.Default)) throw new global::XmlWeave.XmlWeaveException(\"不支持自定义字典比较器。\", name, 0, 0);");
                    code.Append("foreach (var item in v) { w.StartElement(\"Entry\"); w.EndStartElement(); W").Append(ids[Item(type)]).Append("(ref w, item.Key, \"Key\"); W").Append(ids[named.TypeArguments[1]]).AppendLine("(ref w, item.Value, \"Value\"); w.EndElement(\"Entry\"); }");
                }
                else
                {
                    foreach (var member in members[type].Where(m => m.Attribute))
                    {
                        var access = "v.@" + member.Symbol.Name;
                        if (CanNull(member.Type)) code.Append("if (").Append(access).Append(" is not null) ");
                        else if (member.Type.IsReferenceType) code.Append("if (").Append(access).Append(" is null) throw new global::XmlWeave.XmlWeaveException(\"非空属性不能为 null。\", name, 0, 0); ");
                        code.Append("w.Attribute(").Append(Literal(member.Name)).Append(", ").Append(Format(member.Type, NullableValue(member.Type) ? access + ".Value" : access)).AppendLine(");");
                    }
                    code.AppendLine("w.EndStartElement();");
                    foreach (var member in members[type].Where(m => !m.Attribute))
                        code.Append("W").Append(ids[member.Type]).Append("(ref w, v.@").Append(member.Symbol.Name).Append(", ").Append(Literal(member.Name)).AppendLine(");");
                }
                code.AppendLine("w.EndElement(name); } finally { w.Exit(); }");
            }
            code.AppendLine("}");
            code.Append("private static ").Append(t).Append(" R").Append(id).AppendLine("(ref global::XmlWeave.XmlWeaveReader r) { try {");
            code.AppendLine("Span<char> scratch = stackalloc char[256];");
            {
                var list = kind == 0 && !Scalar(type) ? members[type] : new List<Member>();
                code.AppendLine("bool nil = false, contentAttribute = false;");
                for (int i = 0; i < list.Count; i++) code.Append("bool seen").Append(i).AppendLine(" = false;");
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Attribute) code.Append(Type(list[i].Type)).Append(" a").Append(i).AppendLine(" = default!;");
                code.AppendLine("r.Read(); while (r.Token == global::XmlWeave.XmlWeaveToken.Attribute) { if (r.IsNamespaceDeclaration) { r.Read(); continue; } if (r.IsNilAttribute) { if (nil || !r.GetValue(scratch).SequenceEqual(\"true\")) throw r.Error(\"无效空值标记。\"); nil = true; r.Read(); continue; } contentAttribute = true;");
                bool first = true;
                for (int i = 0; i < list.Count; i++)
                {
                    var member = list[i]; if (!member.Attribute) continue;
                    code.Append(first ? "if (" : "else if (").Append("r.NameEquals(").Append(Literal(member.Name)).Append(")) { if (seen").Append(i).Append(") throw r.Error(\"重复属性。\"); seen").Append(i).Append(" = true; a").Append(i).Append(" = ").Append(member.Type.SpecialType == SpecialType.System_String ? "r.GetString()" : Converter(Underlying(member.Type)) != null ? Parse(member.Type, "r.GetString()") : Parse(member.Type, "r.GetValue(scratch)")).AppendLine("; }");
                    first = false;
                }
                code.AppendLine(first ? "throw r.Error(\"未知属性。\"); }" : "else throw r.Error(\"未知属性。\"); r.Read(); }");
                code.AppendLine("if (nil) { if (contentAttribute || r.Token != global::XmlWeave.XmlWeaveToken.EndElement) throw r.Error(\"空值标记不能与其他内容组合。\");");
                code.AppendLine(CanNull(type) ? "return default; }" : "throw r.Error(\"非空成员不能为 null。\"); }");
                if (Scalar(type))
                    code.Append("return ").Append(type.SpecialType == SpecialType.System_String ? "r.ReadElementString()" : Converter(Underlying(type)) != null ? Parse(type, "r.ReadElementString()") : Parse(type, "r.ReadElementValue(scratch)")).AppendLine(";");
                else
                {
                    code.Append("var result = new ").Append(kind == 1 ? "global::System.Collections.Generic.List<" + Type(Item(type)) + ">" : t.TrimEnd('?')).AppendLine("();");
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].Attribute) code.Append("result.@").Append(list[i].Symbol.Name).Append(" = a").Append(i).AppendLine(";");
                    code.AppendLine("while (r.Token != global::XmlWeave.XmlWeaveToken.EndElement) { if (r.Token == global::XmlWeave.XmlWeaveToken.Text) { if (!r.IsWhitespace()) throw r.Error(\"对象或集合不能包含文本。\"); r.Read(); continue; } if (r.Token != global::XmlWeave.XmlWeaveToken.StartElement) throw r.Error(\"缺少结束元素。\");");
                    if (kind == 1 || kind == 2)
                        code.Append("if (!r.NameEquals(\"Item\")) throw r.Error(\"未知集合元素。\"); result.Add(R").Append(ids[Item(type)]).AppendLine("(ref r));");
                    else if (kind == 3)
                    {
                        var named = (INamedTypeSymbol)type;
                        code.AppendLine("if (!r.NameEquals(\"Entry\")) throw r.Error(\"未知集合元素。\"); bool keySeen = false, valueSeen = false;");
                        code.Append(Type(Item(type))).Append(" key = default!; ").Append(Type(named.TypeArguments[1])).AppendLine(" value = default!; r.Read();");
                        code.AppendLine("while (r.Token != global::XmlWeave.XmlWeaveToken.EndElement) { if (r.Token == global::XmlWeave.XmlWeaveToken.Attribute && r.IsNamespaceDeclaration) { r.Read(); continue; } if (r.Token == global::XmlWeave.XmlWeaveToken.Text && r.IsWhitespace()) { r.Read(); continue; }");
                        code.Append("if (r.Token == global::XmlWeave.XmlWeaveToken.StartElement && r.NameEquals(\"Key\") && !keySeen) { key = R").Append(ids[Item(type)]).AppendLine("(ref r); keySeen = true; }");
                        code.Append("else if (r.Token == global::XmlWeave.XmlWeaveToken.StartElement && r.NameEquals(\"Value\") && !valueSeen) { value = R").Append(ids[named.TypeArguments[1]]).AppendLine("(ref r); valueSeen = true; } else throw r.Error(\"未知或重复字典内容。\"); r.Read(); }");
                        code.AppendLine("if (!keySeen || !valueSeen || !result.TryAdd(key, value)) throw r.Error(\"缺失字典成员或重复字典键。\");");
                    }
                    else
                    {
                        first = true;
                        for (int i = 0; i < list.Count; i++)
                        {
                            var m = list[i]; if (m.Attribute) continue;
                            code.Append(first ? "if (" : "else if (").Append("r.NameEquals(").Append(Literal(m.Name)).Append(")) { if (seen").Append(i).Append(") throw r.Error(\"重复元素。\"); seen").Append(i).Append(" = true; result.@").Append(m.Symbol.Name).Append(" = R").Append(ids[m.Type]).AppendLine("(ref r); }"); first = false;
                        }
                        code.AppendLine(first ? "throw r.Error(\"未知元素。\");" : "else throw r.Error(\"未知元素。\");");
                    }
                    code.AppendLine(kind == 0 && list.All(m => m.Attribute) ? "}" : "r.Read(); }");
                    for (int i = 0; i < list.Count; i++)
                        if (!list[i].Attribute || !CanNull(list[i].Type)) code.Append("if (!seen").Append(i).Append(") throw r.Error(").Append(Literal("缺少成员：" + list[i].Name)).AppendLine(");");
                    code.AppendLine(kind == 1 ? "return result.ToArray();" : "return result;");
                }
            }
            code.AppendLine("} catch (global::XmlWeave.XmlWeaveException) { throw; } catch (global::System.Exception ex) when (ex is global::System.FormatException || ex is global::System.OverflowException || ex is global::System.ArgumentException) { throw r.Error(\"成员格式无效。\", ex); } }");
        }
    }
}
