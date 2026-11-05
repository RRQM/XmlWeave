using System;
using System.Buffers;
using System.Text;
using Xunit;

namespace XmlWeave.Tests;

[XmlWeaveRoot]
public class BufferModel
{
    public int Number { get; set; }
    public bool Flag { get; set; }
}

public class BufferTests
{
    [Fact]
    public void BufferSpanAndSegmentedSequenceRoundTrip()
    {
        var output = new ArrayBufferWriter<byte>(1024);
        var model = new BufferModel { Number = 123, Flag = true };
        BufferModelXmlSerializer.Serialize(output, model);
        var copy = BufferModelXmlSerializer.Deserialize(output.WrittenSpan);
        Assert.Equal(model.Number, copy.Number);
        Assert.True(copy.Flag);
        var bytes = output.WrittenMemory;
        for (int split = 1; split < bytes.Length; split++)
        {
            var first = new Segment(bytes[..split]);
            var last = first.Add(bytes[split..]);
            var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
            Assert.Equal(123, BufferModelXmlSerializer.Deserialize(sequence).Number);
        }
    }

    [Fact]
    public void WarmBufferWritingAllocatesNothingAndReadingOnlyAllocatesModel()
    {
        var output = new ArrayBufferWriter<byte>(1024);
        var model = new BufferModel { Number = int.MinValue, Flag = true };
        for (int i = 0; i < 200; i++) { output.Clear(); BufferModelXmlSerializer.Serialize(output, model); BufferModelXmlSerializer.Deserialize(output.WrittenSpan); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { output.Clear(); BufferModelXmlSerializer.Serialize(output, model); }
        long written = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, written);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) GC.KeepAlive(new BufferModel());
        long models = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) GC.KeepAlive(BufferModelXmlSerializer.Deserialize(output.WrittenSpan));
        long read = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(models, read);
    }

    [Theory]
    [InlineData("<BufferModel Number=\"1\" Flag=\"true\"/><BufferModel Number=\"2\" Flag=\"false\"/>")]
    [InlineData("<BufferModel Number=\"1\" Number=\"2\" Flag=\"true\"/>")]
    [InlineData("<BufferModel Number=\"1\" Flag=\"true\"></Other>")]
    [InlineData("<BufferModel Number=\"&#0;\" Flag=\"true\"/>")]
    [InlineData("<BufferModel Number=\"1\" Flag=\"true\"><![CDATA[illegal]]></BufferModel>")]
    public void InvalidUtf8XmlIsRejected(string xml)
        => Assert.Throws<XmlWeaveException>(() => BufferModelXmlSerializer.Deserialize(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void LargeStringsConvertersAndFormattingWhitespaceRoundTrip()
    {
        var model = new DocumentModel
        {
            Script = new string('中', 2000) + "😀<&>\r\n\t",
            Body = new string('x', 2000) + "\r\n<&>",
            Converted = new Converted { Value = new string('c', 2000) }
        };
        var buffer = new ArrayBufferWriter<byte>();
        DocumentModelXmlSerializer.Serialize(buffer, model);
        var copy = DocumentModelXmlSerializer.Deserialize(buffer.WrittenSpan);
        Assert.Equal(model.Script, copy.Script);
        Assert.Equal(model.Body, copy.Body);
        Assert.Equal(model.Converted.Value, copy.Converted.Value);
        var formatted = System.Xml.Linq.XElement.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan)).ToString();
        Assert.Equal(model.Script, DocumentModelXmlSerializer.Deserialize(formatted).Script);
    }

    [Fact]
    public void LongObjectFormattingWhitespaceIsAccepted()
    {
        var xml = "<BufferModel Number=\"1\" Flag=\"true\">" + new string(' ', 2048) + "</BufferModel>";
        Assert.Equal(1, BufferModelXmlSerializer.Deserialize(xml).Number);
    }

    [Fact]
    public void InvalidUtf16StringInputIsRejected()
        => Assert.Throws<XmlWeaveException>(() => BufferModelXmlSerializer.Deserialize("<BufferModel Number=\"1\" Flag=\"true\">\ud800</BufferModel>"));

    [Fact]
    public void InvalidUtf8IsRejected()
        => Assert.Throws<XmlWeaveException>(() => BufferModelXmlSerializer.Deserialize(new byte[] { 0xff }));

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        public Segment Add(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
