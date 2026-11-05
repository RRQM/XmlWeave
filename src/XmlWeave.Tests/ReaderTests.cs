using System;
using System.IO;
using System.Text;
using System.Xml;
using Xunit;

namespace XmlWeave.Tests;

public class ReaderTests
{
    [Theory]
    [InlineData("<r xmlns:n='urn:xmlweave:null' n:nil='true'/>")]
    [InlineData("<r n:nil='true' xmlns:n='urn:xmlweave:null'/>")]
    [InlineData("<r xmlns:n='urn:xmlweave:null'><v n:nil='true'/></r>")]
    [InlineData("<r xmlns:n='urn:xmlweave:nu&#108;l' n:nil='true'/>")]
    public void NilResolvesDeclarationsRegardlessOfOrderAndScope(string xml)
    {
        var reader = new XmlWeaveReader(Encoding.UTF8.GetBytes(xml));
        try
        {
            int nils = 0, declarations = 0;
            while (reader.Read())
            {
                if (reader.IsNamespaceDeclaration) declarations++;
                if (reader.IsNilAttribute) { nils++; Assert.Equal("true", reader.GetString()); }
            }
            Assert.Equal(1, nils);
            Assert.Equal(1, declarations);
        }
        finally { reader.Dispose(); }
    }

    [Fact]
    public void NamespaceShadowingAndEmptyTagScopeAreCorrect()
    {
        var reader = new XmlWeaveReader("<r xmlns:n='urn:xmlweave:null'><a xmlns:n='other' n:nil='false'/><b n:nil='true'/></r>"u8);
        try
        {
            int nils = 0;
            while (reader.Read()) if (reader.IsNilAttribute) { nils++; Assert.Equal("true", reader.GetString()); }
            Assert.Equal(1, nils);
        }
        finally { reader.Dispose(); }
    }

    [Fact]
    public void NameEqualsDoesNotMatchNamespacedNames()
    {
        var reader = new XmlWeaveReader("<r xmlns='urn:other'><r xmlns=''/><p:r xmlns:p='urn:other'/></r>"u8);
        try
        {
            Assert.True(reader.Read()); Assert.False(reader.NameEquals("r"));
            reader.Read(); Assert.True(reader.IsNamespaceDeclaration);
            reader.Read(); Assert.True(reader.NameEquals("r"));
            reader.Read(); reader.Read(); Assert.True(reader.NameEquals("r"));
            reader.Read(); Assert.False(reader.NameEquals("r"));
        }
        finally { reader.Dispose(); }
    }

    [Theory]
    [InlineData("<r a='a\tb\r\nc\rd&#9;&#10;&#13;&amp;&lt;&gt;&quot;&apos;😀'>x\r\ny\rz&#13;😀</r>")]
    [InlineData("<?xml version='1.0' encoding='UTF-8'?><!--before--><r><![CDATA[a\r\nb\rc]]>&#x1F600;<!--after--><?p data?></r><!--tail-->")]
    public void ValuesMatchBclNormalization(string xml)
    {
        var ours = new XmlWeaveReader(Encoding.UTF8.GetBytes(xml));
        using var bcl = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { IgnoreWhitespace = false });
        try
        {
            while (bcl.Read())
            {
                if (bcl.NodeType == XmlNodeType.Element)
                {
                    Assert.True(ours.Read()); Assert.Equal(XmlWeaveToken.StartElement, ours.Token);
                    if (bcl.MoveToFirstAttribute())
                    {
                        do { Assert.True(ours.Read()); Assert.Equal(bcl.Value, ours.GetString()); } while (bcl.MoveToNextAttribute());
                        bcl.MoveToElement();
                    }
                }
                else if (bcl.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    Assert.True(ours.Read()); Assert.Equal(XmlWeaveToken.Text, ours.Token);
                    Assert.Equal(bcl.Value, ours.GetString());
                }
                else if (bcl.NodeType == XmlNodeType.EndElement)
                {
                    Assert.True(ours.Read()); Assert.Equal(XmlWeaveToken.EndElement, ours.Token);
                }
            }
            ours.Finish(); Assert.Equal(XmlWeaveToken.EndDocument, ours.Token);
        }
        finally { ours.Dispose(); }
    }

    [Fact]
    public void ScalarCombinesTextCdataAndIgnoredMarkup()
    {
        var reader = new XmlWeaveReader("<r>a<![CDATA[b]]><!--ignored--><?p?>c&#x1F600;</r>"u8);
        try
        {
            reader.Read(); reader.Read();
            Assert.Equal("abc😀", ReadScalar(ref reader));
            Assert.Equal(XmlWeaveToken.EndElement, reader.Token);
            reader.Finish(); Assert.False(reader.Read());
        }
        finally { reader.Dispose(); }
    }

    private static string ReadScalar(ref XmlWeaveReader reader)
    {
        Span<char> scratch = stackalloc char[256];
        return reader.ReadElementValue(scratch).ToString();
    }

    [Fact]
    public void StringsAndWhitespaceAreNotLimitedToScalarScratchSize()
    {
        string large = new string('中', 5000) + "😀";
        var reader = new XmlWeaveReader(Encoding.UTF8.GetBytes("<r>" + large + "<![CDATA[tail]]></r>"));
        try { reader.Read(); reader.Read(); Assert.Equal(large + "tail", reader.ReadElementString()); reader.Finish(); }
        finally { reader.Dispose(); }
        reader = new XmlWeaveReader(Encoding.UTF8.GetBytes("<r>" + new string(' ', 5000) + "&#9;&#10;&#13;</r>"));
        try { reader.Read(); reader.Read(); Assert.True(reader.IsWhitespace()); reader.Read(); reader.Finish(); }
        finally { reader.Dispose(); }
    }

    [Fact]
    public void ScalarBufferOverflowIsFormatException()
    {
        Assert.Throws<FormatException>(() => ParseShortScalar("<r>" + new string('1', 300) + "</r>"));
    }

    private static void ParseShortScalar(string xml)
    {
        var reader = new XmlWeaveReader(Encoding.UTF8.GetBytes(xml));
        try { reader.Read(); reader.Read(); ReadScalar(ref reader); }
        finally { reader.Dispose(); }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("<r/><s/>")]
    [InlineData("<r><s></r>")]
    [InlineData("<r>")]
    [InlineData("<r a='1' a='2'/>")]
    [InlineData("<r xmlns:p='same' xmlns:q='same' p:a='1' q:a='2'/>")]
    [InlineData("<r xmlns:p='same' xmlns:q='s&#97;me' p:a='1' q:a='2'/>")]
    [InlineData("<p:r/>")]
    [InlineData("<r p:a='1'/>")]
    [InlineData("<r xmlns:p=''/>")]
    [InlineData("<r xmlns:xml='wrong'/>")]
    [InlineData("<r xmlns:p='http://www.w3.org/XML/1998/namespace'/>")]
    [InlineData("<r xmlns:xmlns='urn:other'/>")]
    [InlineData("<r xmlns='http://www.w3.org/2000/xmlns/'/>")]
    [InlineData("<r>&unknown;</r>")]
    [InlineData("<r>&amp</r>")]
    [InlineData("<r>&#;</r>")]
    [InlineData("<r>&#x;</r>")]
    [InlineData("<r>&#0;</r>")]
    [InlineData("<r>&#xD800;</r>")]
    [InlineData("<r>&#x110000;</r>")]
    [InlineData("<r>&#99999999999999999999;</r>")]
    [InlineData("<r>]]></r>")]
    [InlineData("<r a='<'/>")]
    [InlineData("<r a=unquoted/>")]
    [InlineData("<r a='1'b='2'/>")]
    [InlineData("<r a='unterminated/>")]
    [InlineData("<r / >")]
    [InlineData("<r></r extra>")]
    [InlineData("<r><!--bad--comment--></r>")]
    [InlineData("<r><!--bad---></r>")]
    [InlineData("<r><![CDATA[unterminated</r>")]
    [InlineData("<!DOCTYPE r><r/>")]
    [InlineData("<r/><?xml version='1.0'?>")]
    [InlineData("<?XML version='1.0'?><r/>")]
    [InlineData("<?xml encoding='UTF-8'?><r/>")]
    [InlineData("outside<r/>")]
    [InlineData("<r/>outside")]
    [InlineData("<![CDATA[outside]]><r/>")]
    [InlineData("<:r/>")]
    [InlineData("<r:/>")]
    [InlineData("<a:b:c/>")]
    [InlineData("<r>\u0001</r>")]
    public void RejectsMalformedXml(string xml)
        => Assert.Throws<XmlWeaveException>(() => Consume(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void RejectsInvalidUtf8AndHonorsLimits()
    {
        foreach (byte[] bytes in new[] { new byte[] { 0xff }, new byte[] { 0xc0, 0xaf }, new byte[] { 0xed, 0xa0, 0x80 }, new byte[] { 0xf0, 0x9f } })
            Assert.Throws<XmlWeaveException>(() => Consume(bytes));
        Assert.Throws<XmlWeaveException>(() => Consume("<r><s/></r>"u8.ToArray(), new XmlWeaveOptions { MaxDepth = 1 }));
        Consume("<r>😀</r>"u8.ToArray(), new XmlWeaveOptions { MaxCharacters = 9 });
        Assert.Throws<XmlWeaveException>(() => Consume("<r>😀</r>"u8.ToArray(), new XmlWeaveOptions { MaxCharacters = 8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Consume("<r/>"u8.ToArray(), new XmlWeaveOptions { MaxDepth = 0 }));
    }

    [Fact]
    public void ErrorCarriesCurrentPathAndNormalizedLinePosition()
    {
        var reader = new XmlWeaveReader("<r>\r\n<s a='x'/></r>"u8);
        try
        {
            reader.Read(); reader.Read(); reader.Read(); reader.Read();
            var error = reader.Error("test");
            Assert.Equal("/r/s", error.Path); Assert.Equal(2, error.Line); Assert.Equal(4, error.Column);
        }
        finally { reader.Dispose(); }
    }

    [Fact]
    public void ScalarRejectsNestedContentAndFinishRejectsIncompleteRoot()
    {
        Assert.Throws<XmlWeaveException>(() => ParseShortScalar("<r><s/></r>"));
        var reader = new XmlWeaveReader("<r><s/></r>"u8);
        try
        {
            reader.Read(); reader.Read(); reader.Read();
            bool rejected = false;
            try { reader.Finish(); } catch (XmlWeaveException) { rejected = true; }
            Assert.True(rejected);
        }
        finally { reader.Dispose(); }
    }

    [Fact]
    public void WarmTokenizationAllocatesNothingAndGetStringOnlyAllocatesResult()
    {
        byte[] xml = "<r xmlns:n='urn:xmlweave:null'><v n:nil='true' a='value'/>text</r>"u8.ToArray();
        for (int i = 0; i < 200; i++) Consume(xml);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Consume(xml);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var reader = new XmlWeaveReader("<r>hello</r>"u8);
        try
        {
            reader.Read(); reader.Read();
            for (int i = 0; i < 200; i++) GC.KeepAlive(reader.GetString());
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) GC.KeepAlive(new string('x', 5));
            long strings = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) GC.KeepAlive(reader.GetString());
            Assert.Equal(strings, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally { reader.Dispose(); }
    }

    [Theory]
    [InlineData("<?xml version='1.0' encoding='UtF-8'?><r/>")]
    [InlineData("<?a:b:c data?><r/>")]
    [InlineData("<?: data?><r/>")]
    [InlineData("<r xml:lang='en' xmlns:xml='http://www.w3.org/XML/1998/namespace'/>")]
    public void AcceptsXmlDeclarationsAndProcessingInstructionNames(string xml)
        => Consume(Encoding.UTF8.GetBytes(xml));

    private static void Consume(byte[] utf8, XmlWeaveOptions? options = null)
    {
        var reader = new XmlWeaveReader(utf8, options);
        try { while (reader.Read()) { } }
        finally { reader.Dispose(); }
    }
}
