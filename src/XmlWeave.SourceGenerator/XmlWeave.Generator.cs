using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        public string HintName => "XmlWeave_" + Encode(root.ToDisplayString()) + ".g.cs";
        private static string Encode(string value) => string.Concat(value.Select(c => ((int)c).ToString("x4", CultureInfo.InvariantCulture)));
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
            code.AppendLine("using System;").AppendLine("using System.Linq;").AppendLine("using System.Xml.Linq;");
            if (!root.ContainingNamespace.IsGlobalNamespace) code.Append("namespace ").Append(root.ContainingNamespace.ToDisplayString()).AppendLine(";");
            code.AppendLine("/// <summary>编译期生成的强类型 XML 序列化器。</summary>");
            code.Append(root.DeclaredAccessibility == Accessibility.Public ? "public" : "internal").Append(" static class ").Append(root.Name).AppendLine("XmlSerializer {");
            var t = Type(rootType); var rn = Literal(rootName);
            code.AppendLine("/// <summary>将模型序列化为 XML 字符串。</summary>");
            code.Append("public static string Serialize(").Append(t).AppendLine(" value, global::XmlWeave.XmlWeaveOptions? options = null) {");
            code.AppendLine("options ??= new global::XmlWeave.XmlWeaveOptions(); options.Validate();");
            code.Append("return global::XmlWeave.XmlWeaveDocument.Write(W0(value, ").Append(rn).AppendLine(", new global::System.Collections.Generic.HashSet<object>(global::System.Collections.Generic.ReferenceEqualityComparer.Instance), 0, options), options); }");
            code.AppendLine("/// <summary>将模型以 UTF8 无 BOM 写入流，不关闭流。</summary>");
            code.Append("public static void Serialize(global::System.IO.Stream stream, ").Append(t).AppendLine(" value, global::XmlWeave.XmlWeaveOptions? options = null) {");
            code.AppendLine("global::System.ArgumentNullException.ThrowIfNull(stream); var bytes = new global::System.Text.UTF8Encoding(false, true).GetBytes(Serialize(value, options)); stream.Write(bytes, 0, bytes.Length); }");
            code.AppendLine("/// <summary>从 XML 字符串读取模型。</summary>");
            code.Append("public static ").Append(t).AppendLine(" Deserialize(string xml, global::XmlWeave.XmlWeaveOptions? options = null) => ReadRoot(global::XmlWeave.XmlWeaveDocument.Read(xml, options));");
            code.AppendLine("/// <summary>从流读取模型，不关闭流。</summary>");
            code.Append("public static ").Append(t).AppendLine(" Deserialize(global::System.IO.Stream stream, global::XmlWeave.XmlWeaveOptions? options = null) => ReadRoot(global::XmlWeave.XmlWeaveDocument.Read(stream, options));");
            code.Append("private static ").Append(t).Append(" ReadRoot(XElement e) { if (e.Name != ").Append(rn).AppendLine(") throw global::XmlWeave.XmlWeaveDocument.Error(e, \"根元素名称不匹配。\"); return R0(e); }");
            for (var i = 0; i < types.Count; i++) GenerateType(i, types[i]);
            return code.AppendLine("}").ToString();
        }

        private string Format(ITypeSymbol type, string value)
        {
            type = Underlying(type);
            if (Converter(type) is INamedTypeSymbol converter) return "((global::XmlWeave.IXmlWeaveConverter<" + Type(type) + ">)new " + Type(converter) + "()).Format(" + value + ")";
            if (type.TypeKind == TypeKind.Enum) return Format(((INamedTypeSymbol)type).EnumUnderlyingType!, "(" + Type(((INamedTypeSymbol)type).EnumUnderlyingType!) + ")" + value);
            if (type.SpecialType == SpecialType.System_String) return value;
            if (type.SpecialType == SpecialType.System_Char) return "global::System.Xml.XmlConvert.ToString((ushort)" + value + ")";
            if (type.SpecialType == SpecialType.System_DateTime) return "global::System.Xml.XmlConvert.ToString(" + value + ", global::System.Xml.XmlDateTimeSerializationMode.RoundtripKind)";
            if (Identity(type) == "System.DateOnly" || Identity(type) == "System.TimeOnly") return value + ".ToString(\"O\", global::System.Globalization.CultureInfo.InvariantCulture)";
            return "global::System.Xml.XmlConvert.ToString(" + value + ")";
        }

        private string Parse(ITypeSymbol type, string value)
        {
            type = Underlying(type);
            if (Converter(type) is INamedTypeSymbol converter) return "((global::XmlWeave.IXmlWeaveConverter<" + Type(type) + ">)new " + Type(converter) + "()).Parse(" + value + ")";
            if (type.TypeKind == TypeKind.Enum) return "(" + Type(type) + ")" + Parse(((INamedTypeSymbol)type).EnumUnderlyingType!, value);
            if (type.SpecialType == SpecialType.System_String) return value;
            if (type.SpecialType == SpecialType.System_Char) return "(char)global::System.Xml.XmlConvert.ToUInt16(" + value + ")";
            if (type.SpecialType == SpecialType.System_DateTime) return "global::System.Xml.XmlConvert.ToDateTime(" + value + ", global::System.Xml.XmlDateTimeSerializationMode.RoundtripKind)";
            if (Identity(type) == "System.DateOnly" || Identity(type) == "System.TimeOnly") return Type(type) + ".ParseExact(" + value + ", \"O\", global::System.Globalization.CultureInfo.InvariantCulture)";
            var method = type.SpecialType switch
            {
                SpecialType.System_Boolean => "Boolean", SpecialType.System_Byte => "Byte", SpecialType.System_SByte => "SByte",
                SpecialType.System_Int16 => "Int16", SpecialType.System_UInt16 => "UInt16", SpecialType.System_Int32 => "Int32",
                SpecialType.System_UInt32 => "UInt32", SpecialType.System_Int64 => "Int64", SpecialType.System_UInt64 => "UInt64",
                SpecialType.System_Single => "Single", SpecialType.System_Double => "Double", SpecialType.System_Decimal => "Decimal",
                _ => type.Name
            };
            return "global::System.Xml.XmlConvert.To" + method + "(" + value + ")";
        }

        private void GenerateType(int id, ITypeSymbol type)
        {
            var t = Type(type); var kind = Kind(type);
            code.Append("private static XElement W").Append(id).Append('(').Append(t).AppendLine(" v, string name, global::System.Collections.Generic.HashSet<object> active, int depth, global::XmlWeave.XmlWeaveOptions options) {");
            if (CanNull(type)) code.AppendLine("if (v is null) return global::XmlWeave.XmlWeaveDocument.Null(name);");
            else if (type.IsReferenceType) code.AppendLine("if (v is null) throw new global::XmlWeave.XmlWeaveException(\"非空成员不能为 null。\", name, 0, 0);");
            if (NullableValue(type))
                code.Append("return W").Append(ids[Underlying(type)]).AppendLine("(v.Value, name, active, depth, options);");
            else if (Scalar(type))
                code.Append("return new XElement(name, ").Append(Format(type, "v")).AppendLine(");");
            else
            {
                code.Append("if (v.GetType() != typeof(").Append(t.TrimEnd('?')).AppendLine(")) throw new global::XmlWeave.XmlWeaveException(\"不支持多态运行时类型。\", name, 0, 0);");
                code.AppendLine("global::XmlWeave.XmlWeaveDocument.Enter(v, active, depth, options); try { var e = new XElement(name);");
                if (kind == 1 || kind == 2)
                    code.Append("foreach (var item in v) e.Add(W").Append(ids[Item(type)]).AppendLine("(item, \"Item\", active, depth + 1, options));");
                else if (kind == 3)
                {
                    var named = (INamedTypeSymbol)type;
                    code.Append("if (!v.Comparer.Equals(global::System.Collections.Generic.EqualityComparer<").Append(Type(Item(type))).AppendLine(">.Default)) throw global::XmlWeave.XmlWeaveDocument.Error(e, \"不支持自定义字典比较器。\");");
                    code.Append("foreach (var item in v) e.Add(new XElement(\"Entry\", W").Append(ids[Item(type)]).Append("(item.Key, \"Key\", active, depth + 1, options), W").Append(ids[named.TypeArguments[1]]).AppendLine("(item.Value, \"Value\", active, depth + 1, options)));");
                }
                else foreach (var member in members[type])
                {
                    var access = "v.@" + member.Symbol.Name;
                    if (member.Attribute)
                    {
                        if (CanNull(member.Type)) code.Append("if (").Append(access).Append(" is not null) ");
                        var expr = NullableValue(member.Type) ? access + ".Value" : access;
                        if (member.Type.IsReferenceType && !CanNull(member.Type))
                            code.Append("if (").Append(access).Append(" is null) throw global::XmlWeave.XmlWeaveDocument.Error(e, ").Append(Literal("非空属性不能为 null：" + member.Name)).AppendLine(");");
                        code.Append("e.Add(new XAttribute(").Append(Literal(member.Name)).Append(", ").Append(Format(member.Type, expr)).AppendLine("));");
                    }
                    else code.Append("e.Add(W").Append(ids[member.Type]).Append('(').Append(access).Append(", ").Append(Literal(member.Name)).AppendLine(", active, depth + 1, options));");
                }
                code.AppendLine("return e; } finally { active.Remove(v); }");
            }
            code.AppendLine("}");
            code.Append("private static ").Append(t).Append(" R").Append(id).AppendLine("(XElement e) { try {");
            if (CanNull(type)) code.AppendLine("if (global::XmlWeave.XmlWeaveDocument.IsNull(e)) return default;");
            else code.AppendLine("if (global::XmlWeave.XmlWeaveDocument.IsNull(e)) throw global::XmlWeave.XmlWeaveDocument.Error(e, \"非空成员不能为 null。\");");
            if (NullableValue(type)) code.Append("return R").Append(ids[Underlying(type)]).AppendLine("(e);");
            else if (Scalar(type)) code.Append("return ").Append(Parse(type, "global::XmlWeave.XmlWeaveDocument.Text(e)")).AppendLine(";");
            else if (kind == 1 || kind == 2)
            {
                code.AppendLine("global::XmlWeave.XmlWeaveDocument.CheckCollection(e, \"Item\");");
                code.Append("var result = new global::System.Collections.Generic.List<").Append(Type(Item(type))).AppendLine(">();");
                code.Append("foreach (var item in e.Elements()) result.Add(R").Append(ids[Item(type)]).AppendLine("(item));");
                code.AppendLine(kind == 1 ? "return result.ToArray();" : "return result;");
            }
            else if (kind == 3)
            {
                var named = (INamedTypeSymbol)type;
                code.AppendLine("global::XmlWeave.XmlWeaveDocument.CheckCollection(e, \"Entry\");");
                code.Append("var result = new ").Append(t.TrimEnd('?')).AppendLine("();");
                code.AppendLine("foreach (var item in e.Elements()) { global::XmlWeave.XmlWeaveDocument.CheckObject(item, \"\", \"Key|Value\");");
                code.Append("var key = R").Append(ids[Item(type)]).AppendLine("(global::XmlWeave.XmlWeaveDocument.Required(item, \"Key\"));");
                code.Append("var value = R").Append(ids[named.TypeArguments[1]]).AppendLine("(global::XmlWeave.XmlWeaveDocument.Required(item, \"Value\"));");
                code.AppendLine("if (!result.TryAdd(key, value)) throw global::XmlWeave.XmlWeaveDocument.Error(item, \"重复字典键。\"); } return result;");
            }
            else
            {
                var list = members[type];
                code.Append("global::XmlWeave.XmlWeaveDocument.CheckObject(e, ").Append(Literal(string.Join("|", list.Where(m => m.Attribute).Select(m => m.Name)))).Append(", ").Append(Literal(string.Join("|", list.Where(m => !m.Attribute).Select(m => m.Name)))).AppendLine(");");
                code.Append("var result = new ").Append(t.TrimEnd('?')).AppendLine("();");
                foreach (var member in list)
                {
                    var access = "result.@" + member.Symbol.Name;
                    if (member.Attribute)
                    {
                        code.Append("{ var a = e.Attribute(").Append(Literal(member.Name)).AppendLine(");");
                        if (CanNull(member.Type)) code.Append(access).AppendLine(" = default;");
                        else code.Append("if (a is null) throw global::XmlWeave.XmlWeaveDocument.Error(e, ").Append(Literal("缺少属性：" + member.Name)).AppendLine(");");
                        code.Append("if (a is not null) ").Append(access).Append(" = ").Append(Parse(member.Type, "a.Value")).AppendLine("; }");
                    }
                    else code.Append(access).Append(" = R").Append(ids[member.Type]).Append("(global::XmlWeave.XmlWeaveDocument.Required(e, ").Append(Literal(member.Name)).AppendLine("));");
                }
                code.AppendLine("return result;");
            }
            code.AppendLine("} catch (global::XmlWeave.XmlWeaveException) { throw; } catch (global::System.Exception ex) when (ex is global::System.FormatException || ex is global::System.OverflowException || ex is global::System.ArgumentException) { throw global::XmlWeave.XmlWeaveDocument.Error(e, \"成员格式无效。\", ex); } }");
        }
    }
}
