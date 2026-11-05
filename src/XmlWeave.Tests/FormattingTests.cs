using System;
using System.Buffers;
using System.IO;
using System.Text;
using Xunit;

namespace XmlWeave.Tests;

[XmlWeaveRoot]
public class FormattingModel
{
    public string Label { get; set; } = "";
    public int Number { get; set; }
    public FormattingModel? Child { get; set; }
    [XmlWeaveMember(Representation = XmlWeaveRepresentation.Element)]
    public string Text { get; set; } = "";
}

public class FormattingTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void FormattingSwitchesAreIndependent(bool elements, bool indent, bool attributes)
    {
        var options = new XmlWeaveOptions
        {
            NewLineOnElements = elements, Indent = indent, NewLineOnAttributes = attributes,
            IndentChars = "\t", NewLineChars = "\r\n"
        };
        string Line(int depth) => "\r\n" + (indent ? new string('\t', depth) : "");
        string Attribute(int depth) => attributes ? Line(depth) : " ";
        string Element(int depth) => elements ? Line(depth) : "";
        var model = new FormattingModel { Label = "a\r\n\t<&😀", Number = 42, Text = "\r\n \t<&>😀" };
        string expected = "<FormattingModel" + Attribute(1) + "Label=\"a&#xD;&#xA;&#x9;&lt;&amp;😀\""
            + Attribute(1) + "Number=\"42\">"
            + Element(1) + "<Child" + Attribute(2) + "xmlns:p=\"urn:xmlweave:null\""
            + Attribute(2) + "p:nil=\"true\"></Child>"
            + Element(1) + "<Text>&#xD;\n \t&lt;&amp;&gt;😀</Text>"
            + Element(0) + "</FormattingModel>";
        string xml = FormattingModelXmlSerializer.Serialize(model, options);
        Assert.Equal(expected, xml);
        var buffer = new ArrayBufferWriter<byte>();
        FormattingModelXmlSerializer.Serialize(buffer, model, options);
        Assert.Equal(expected, Encoding.UTF8.GetString(buffer.WrittenSpan));
        using var stream = new MemoryStream();
        FormattingModelXmlSerializer.Serialize(stream, model, options);
        Assert.True(stream.CanWrite);
        Assert.Equal(buffer.WrittenSpan.ToArray(), stream.ToArray());
        var copy = FormattingModelXmlSerializer.Deserialize(xml);
        Assert.Equal(model.Label, copy.Label);
        Assert.Equal(model.Text, copy.Text);
        Assert.Equal(42, copy.Number);
        Assert.Null(copy.Child);
    }

    [Fact]
    public void NestedElementsAndClosingTagsUseTheirOwnDepth()
    {
        var model = new FormattingModel { Child = new FormattingModel { Text = "x" } };
        var options = new XmlWeaveOptions { NewLineOnElements = true, Indent = true };
        string xml = FormattingModelXmlSerializer.Serialize(model, options);
        Assert.Contains("\n  <Child Label=\"\" Number=\"0\">\n    <Child", xml);
        Assert.Contains("\n    <Text>x</Text>\n  </Child>\n  <Text></Text>\n</FormattingModel>", xml);
        Assert.Equal("x", FormattingModelXmlSerializer.Deserialize(xml).Child!.Text);
        Assert.Equal(FormattingModelXmlSerializer.Serialize(model), FormattingModelXmlSerializer.Serialize(model, new XmlWeaveOptions()));
    }

    [Fact]
    public void FormattingCountsTowardCharacterLimit()
    {
        var model = new FormattingModel();
        var options = new XmlWeaveOptions { NewLineOnElements = true, Indent = true, NewLineOnAttributes = true };
        string xml = FormattingModelXmlSerializer.Serialize(model, options);
        Assert.Equal(xml, FormattingModelXmlSerializer.Serialize(model, new XmlWeaveOptions
        {
            NewLineOnElements = true, Indent = true, NewLineOnAttributes = true, MaxCharacters = xml.Length
        }));
        Assert.Throws<XmlWeaveException>(() => FormattingModelXmlSerializer.Serialize(model, new XmlWeaveOptions
        {
            NewLineOnElements = true, Indent = true, NewLineOnAttributes = true, MaxCharacters = xml.Length - 1
        }));
    }

    [Theory]
    [InlineData("x", "\n")]
    [InlineData("\n", "\n")]
    [InlineData(null, "\n")]
    [InlineData("  ", null)]
    [InlineData("  ", "")]
    [InlineData("  ", "<")]
    public void InvalidFormattingCharactersAreRejected(string? indent, string? newline)
        => Assert.ThrowsAny<ArgumentException>(() => new XmlWeaveOptions { IndentChars = indent!, NewLineChars = newline! }.Validate());

    [Fact]
    public void TextAndEmptyElementsDoNotAcquireFormattingWhitespace()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new XmlWeaveWriter(buffer, new XmlWeaveOptions { NewLineOnElements = true, Indent = true }))
        {
            writer.StartElement("root");
            writer.Text("before");
            writer.StartElement("empty"); writer.EndElement("empty");
            writer.Text("after");
            writer.EndElement("root");
        }
        Assert.Equal("<root>before<empty></empty>after</root>", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void FormattingStackGrowsBeyondInitialRental()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new XmlWeaveWriter(buffer, new XmlWeaveOptions { NewLineOnElements = true, MaxDepth = 130 }))
        {
            for (int i = 0; i < 130; i++) writer.StartElement("n");
            for (int i = 0; i < 130; i++) writer.EndElement("n");
        }
        Assert.Equal(258, Encoding.UTF8.GetString(buffer.WrittenSpan).Split('\n').Length - 1);
    }
}
