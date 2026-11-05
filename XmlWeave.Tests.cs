using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XmlWeave.SourceGenerator;
using Xunit;

namespace XmlWeave.Tests;

public class BaseModel
{
    public int Inherited { get; set; }
}

[XmlWeaveRoot(Name = "Document")]
public class DocumentModel : BaseModel
{
    public string Script { get; set; } = "";
    public string? Optional { get; set; }
    public decimal Amount { get; set; }
    public int? Count { get; set; }
    public Mode Mode { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid Id { get; set; }
    [XmlWeaveMember(Name = "Body", Representation = XmlWeaveRepresentation.Element)]
    public string Body { get; set; } = "";
    public List<string?> Names { get; set; } = new();
    public int?[] Values { get; set; } = Array.Empty<int?>();
    public Dictionary<string, Child?> Lookup { get; set; } = new();
    public Child? Child { get; set; }
    public Converted Converted { get; set; } = new();
    [XmlWeaveIgnore]
    public object? Ignored { get; set; }
}

public enum Mode : ulong { Huge = ulong.MaxValue }
public class Child
{
    public int Value { get; set; }
    public Child? Next { get; set; }
}

[XmlWeaveConverter(typeof(ConvertedConverter))]
public class Converted { public string Value { get; set; } = ""; }
public class ConvertedConverter : IXmlWeaveConverter<Converted>
{
    public string Format(Converted value) => value.Value;
    public Converted Parse(string value) => new() { Value = value };
}

public class DerivedChild : Child { public string Extra { get; set; } = ""; }

[XmlWeaveRoot]
public class ScalarsModel
{
    public bool Boolean { get; set; }
    public byte Byte { get; set; }
    public sbyte SByte { get; set; }
    public short Int16 { get; set; }
    public ushort UInt16 { get; set; }
    public int Int32 { get; set; }
    public uint UInt32 { get; set; }
    public long Int64 { get; set; }
    public ulong UInt64 { get; set; }
    public float Single { get; set; }
    public double Double { get; set; }
    public char Character { get; set; }
    public DateTimeOffset Offset { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
    public TimeSpan Duration { get; set; }
}

public class SerializationTests
{
    [Fact]
    public void AllScalarTypesRoundTrip()
    {
        var model = new ScalarsModel
        {
            Boolean = true, Byte = byte.MaxValue, SByte = sbyte.MinValue,
            Int16 = short.MinValue, UInt16 = ushort.MaxValue, Int32 = int.MinValue,
            UInt32 = uint.MaxValue, Int64 = long.MinValue, UInt64 = ulong.MaxValue,
            Single = float.NaN, Double = double.PositiveInfinity, Character = '\0',
            Offset = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)),
            Date = new DateOnly(2026, 1, 2), Time = new TimeOnly(3, 4, 5).Add(TimeSpan.FromTicks(123)),
            Duration = TimeSpan.FromTicks(-1234567)
        };
        var xml = ScalarsModelXmlSerializer.Serialize(model);
        var copy = ScalarsModelXmlSerializer.Deserialize(xml);
        Assert.Equal(xml, ScalarsModelXmlSerializer.Serialize(copy));
        Assert.True(float.IsNaN(copy.Single)); Assert.Equal('\0', copy.Character);
        Assert.Equal(model.Offset, copy.Offset); Assert.Equal(model.Offset.Offset, copy.Offset.Offset);
        Assert.Equal(model.Date, copy.Date); Assert.Equal(model.Time, copy.Time); Assert.Equal(model.Duration, copy.Duration);
    }

    [Fact]
    public void PolymorphismCannotSilentlyLoseMembers()
    {
        var model = Make(); model.Child = new DerivedChild { Extra = "must not disappear" };
        Assert.Throws<XmlWeaveException>(() => DocumentModelXmlSerializer.Serialize(model));
    }

    private static DocumentModel Make() => new()
    {
        Script = "第一行\r\n第二行\r第三行\n\t<&>\"' 😀",
        Body = "\r\n\r\t\n<&>", Optional = null, Amount = 123.456m,
        Count = null, Mode = Mode.Huge, Timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        Id = Guid.Parse("4a067c17-e38a-4b3a-964f-d84106186265"),
        Names = new List<string?> { null, "", "\r\n\t" }, Values = new int?[] { null, 0, int.MaxValue },
        Lookup = new Dictionary<string, Child?> { ["\r\n\t<&>"] = new Child { Value = 42 }, [""] = null },
        Inherited = 7, Converted = new Converted { Value = "custom\r\n\t<&>" }
    };

    [Fact]
    public void RoundTripPreservesAllRepresentations()
    {
        var original = Make();
        var xml = DocumentModelXmlSerializer.Serialize(original);
        var copy = DocumentModelXmlSerializer.Deserialize(xml);
        Assert.Equal(original.Script, copy.Script);
        Assert.Equal(original.Body, copy.Body);
        Assert.Null(copy.Optional); Assert.Null(copy.Count); Assert.Null(copy.Child);
        Assert.Equal(original.Amount, copy.Amount); Assert.Equal(original.Mode, copy.Mode);
        Assert.Equal(original.Timestamp, copy.Timestamp); Assert.Equal(original.Timestamp.Kind, copy.Timestamp.Kind);
        Assert.Equal(original.Id, copy.Id); Assert.Equal(original.Inherited, copy.Inherited);
        Assert.Equal(original.Names, copy.Names); Assert.Equal(original.Values, copy.Values);
        Assert.Equal(42, copy.Lookup["\r\n\t<&>"]!.Value); Assert.Null(copy.Lookup[""]);
        Assert.Equal(original.Converted.Value, copy.Converted.Value);
        Assert.Null(XElement.Parse(xml).Attribute("Optional"));
        original.Optional = "";
        Assert.Equal("", DocumentModelXmlSerializer.Deserialize(DocumentModelXmlSerializer.Serialize(original)).Optional);
    }

    [Fact]
    public void StreamsStayOpenAndUseUtf8WithoutBom()
    {
        using var stream = new MemoryStream();
        DocumentModelXmlSerializer.Serialize(stream, Make());
        Assert.True(stream.CanWrite);
        Assert.Equal((byte)'<', stream.ToArray()[0]);
        stream.Position = 0;
        Assert.Equal(Make().Script, DocumentModelXmlSerializer.Deserialize(stream).Script);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void CultureDoesNotAffectWireFormat()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var xml = DocumentModelXmlSerializer.Serialize(Make());
            Assert.Contains("123.456", xml);
            Assert.Equal(123.456m, DocumentModelXmlSerializer.Deserialize(xml).Amount);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("unknown-attribute")]
    [InlineData("unknown-element")]
    [InlineData("missing-attribute")]
    [InlineData("missing-element")]
    [InlineData("duplicate")]
    [InlineData("dictionary")]
    [InlineData("null-content")]
    [InlineData("format")]
    [InlineData("namespace")]
    public void StrictStructureIsEnforced(string mutation)
    {
        var e = XElement.Parse(DocumentModelXmlSerializer.Serialize(Make()));
        switch (mutation)
        {
            case "unknown-attribute": e.SetAttributeValue("Unknown", "x"); break;
            case "unknown-element": e.Add(new XElement("Unknown")); break;
            case "missing-attribute": e.Attribute("Script")!.Remove(); break;
            case "missing-element": e.Element("Names")!.Remove(); break;
            case "duplicate": e.Add(new XElement(e.Element("Names")!)); break;
            case "dictionary": e.Element("Lookup")!.Add(new XElement(e.Element("Lookup")!.Elements().First())); break;
            case "null-content": e.Element("Child")!.Value = "illegal"; break;
            case "format": e.SetAttributeValue("Amount", "bad"); break;
            case "namespace": e.Name = XName.Get("Document", "unknown"); break;
        }
        var error = Assert.Throws<XmlWeaveException>(() => DocumentModelXmlSerializer.Deserialize(e.ToString()));
        Assert.True(error.Line > 0); Assert.StartsWith("/", error.Path);
    }

    [Fact]
    public void DtdAndResourceLimitsAreEnforced()
    {
        Assert.Throws<XmlWeaveException>(() => DocumentModelXmlSerializer.Deserialize("<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///never-read'>]><Document>&x;</Document>"));
        var xml = DocumentModelXmlSerializer.Serialize(Make());
        Assert.Throws<XmlWeaveException>(() => DocumentModelXmlSerializer.Deserialize(xml, new XmlWeaveOptions { MaxCharacters = 10 }));
        Assert.Throws<XmlWeaveException>(() => DocumentModelXmlSerializer.Deserialize(xml, new XmlWeaveOptions { MaxDepth = 2 }));
        Assert.Throws<XmlWeaveException>(() => DocumentModelXmlSerializer.Serialize(Make(), new XmlWeaveOptions { MaxDepth = 2 }));
        Assert.Throws<XmlWeaveException>(() => DocumentModelXmlSerializer.Serialize(Make(), new XmlWeaveOptions { MaxCharacters = 10 }));
    }

    [Fact]
    public void CyclesAreRejectedButSharedNodesAreAllowed()
    {
        var model = Make(); var child = new Child(); child.Next = child; model.Child = child;
        Assert.Throws<XmlWeaveException>(() => DocumentModelXmlSerializer.Serialize(model));
        child.Next = null; model.Lookup["same"] = child;
        var copy = DocumentModelXmlSerializer.Deserialize(DocumentModelXmlSerializer.Serialize(model));
        Assert.NotSame(copy.Child, copy.Lookup["same"]);
    }

    [Fact]
    public void CustomDictionaryComparersAreRejected()
    {
        var model = Make(); model.Lookup = new Dictionary<string, Child?>(StringComparer.OrdinalIgnoreCase);
        Assert.Throws<XmlWeaveException>(() => DocumentModelXmlSerializer.Serialize(model));
    }

    [Fact]
    public void NonNullableReferencesAreEnforced()
    {
        var model = Make(); model.Script = null!;
        Assert.Throws<XmlWeaveException>(() => DocumentModelXmlSerializer.Serialize(model));
    }
}

public class GeneratorTests
{
    [Theory]
    [InlineData("public object Value { get; set; }", "已知标量")]
    [InlineData("public int Value { get; }", "getter/setter")]
    [InlineData("public int Value { get; init; }", "getter/setter")]
    [InlineData("public readonly int Value;", "readonly")]
    [InlineData("[XmlWeaveMember(Representation = XmlWeaveRepresentation.Attribute)] public Model Value { get; set; }", "复杂成员")]
    [InlineData("[XmlWeaveMember(Name = \"bad:name\")] public int Value { get; set; }", "XML 名称")]
    [InlineData("public int[,] Value { get; set; }", "一维数组")]
    public void UnsupportedMembersProduceActionableDiagnostics(string member, string message)
    {
        var result = Run("#nullable enable\nusing XmlWeave; [XmlWeaveRoot] public class Model { " + member + " }");
        var diagnostic = Assert.Single(result.Diagnostics.Where(d => d.Id == "XW001"));
        Assert.Contains(message, diagnostic.GetMessage()); Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void RecursionAndIdenticalNamesInDifferentNamespacesCompile()
    {
        var source = "#nullable enable\nusing XmlWeave; namespace A { [XmlWeaveRoot] public class Model { public Model? Next {get;set;} } } namespace B { [XmlWeaveRoot] public class Model { public int Value {get;set;} } }";
        var compilation = Compilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new XmlWeaveGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var errors);
        Assert.Empty(errors.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(2, driver.GetRunResult().GeneratedTrees.Length);
    }

    [Theory]
    [InlineData("public class Base { public virtual int Value {get;set;} } [XmlWeaveRoot] public class Model : Base { public override int Value {get;set;} }")]
    [InlineData("[XmlWeaveRoot] internal class Model { public int Value {get;set;} }")]
    public void SupportedAccessibilityAndOverridesCompile(string source)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new XmlWeaveGenerator().AsSourceGenerator());
        driver.RunGeneratorsAndUpdateCompilation(Compilation("using XmlWeave; " + source), out var output, out var errors);
        Assert.Empty(errors.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void SerializerNameCollisionIsDiagnosed()
    {
        var result = Run("using XmlWeave; [XmlWeaveRoot] public class Model {} public class ModelXmlSerializer {}");
        Assert.Contains(result.Diagnostics, d => d.Id == "XW001" && d.GetMessage().Contains("名称与现有类型冲突"));
    }

    [Fact]
    public void UnmarkedModelsGenerateNothing() => Assert.Empty(Run("public class Model { public int Value {get;set;} }").GeneratedTrees);

    private static GeneratorDriverRunResult Run(string source)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new XmlWeaveGenerator().AsSourceGenerator());
        return driver.RunGenerators(Compilation(source)).GetRunResult();
    }

    private static CSharpCompilation Compilation(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(XmlWeaveRootAttribute).Assembly.Location));
        return CSharpCompilation.Create("GeneratedTest", new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
