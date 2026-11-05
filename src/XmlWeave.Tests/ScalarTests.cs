using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Xml;
using Xunit;

namespace XmlWeave.Tests;

public class ScalarTests
{
    [Fact]
    public void BuiltInScalarsPreserveXmlConvertWireFormats()
    {
        Check(sbyte.MinValue, XmlConvert.ToString(sbyte.MinValue));
        Check(byte.MaxValue, XmlConvert.ToString(byte.MaxValue));
        Check(short.MinValue, XmlConvert.ToString(short.MinValue));
        Check(ushort.MaxValue, XmlConvert.ToString(ushort.MaxValue));
        Check(int.MinValue, XmlConvert.ToString(int.MinValue));
        Check(uint.MaxValue, XmlConvert.ToString(uint.MaxValue));
        Check(long.MinValue, XmlConvert.ToString(long.MinValue));
        Check(ulong.MaxValue, XmlConvert.ToString(ulong.MaxValue));
        Check(decimal.MaxValue, XmlConvert.ToString(decimal.MaxValue));
        Check(1.2300m, XmlConvert.ToString(1.2300m));
        foreach (float value in new[] { 0f, -0f, 1e-9f, float.Epsilon, float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Check(value, XmlConvert.ToString(value));
        foreach (double value in new[] { 0d, -0d, 1e-9d, double.Epsilon, double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check(value, XmlConvert.ToString(value));
        foreach (DateTime value in new[] { DateTime.MinValue, DateTime.MaxValue, DateTime.UnixEpoch, new DateTime(2024, 2, 29, 12, 34, 56, DateTimeKind.Local).AddTicks(1234567), new DateTime(2024, 2, 29, 12, 34, 56, DateTimeKind.Unspecified) })
        {
            Check(value, XmlConvert.ToString(value, XmlDateTimeSerializationMode.RoundtripKind));
            DateTime parsed = XmlWeaveScalar.Parse<DateTime>(XmlConvert.ToString(value, XmlDateTimeSerializationMode.RoundtripKind));
            Assert.Equal(value.Kind, parsed.Kind);
        }
        foreach (DateTimeOffset value in new[] { DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.UnixEpoch, new DateTimeOffset(2024, 2, 29, 12, 34, 56, TimeSpan.FromHours(5.5)).AddTicks(1234567) })
            Check(value, XmlConvert.ToString(value));
        Check(Guid.Empty, XmlConvert.ToString(Guid.Empty));
        Guid guid = new("01234567-89ab-cdef-0123-456789abcdef");
        Check(guid, XmlConvert.ToString(guid));
        // DateOnly and TimeOnly have no XmlConvert overload; their established wire format is O.
        Check(new DateOnly(2024, 2, 29), "2024-02-29");
        Check(new TimeOnly(12, 34, 56).Add(TimeSpan.FromTicks(1234567)), "12:34:56.1234567");
        Check(DateOnly.MinValue, "0001-01-01");
        Check(TimeOnly.MaxValue, "23:59:59.9999999");
        Span<char> destination = stackalloc char[5];
        Assert.Equal(XmlConvert.ToString(true), XmlWeaveScalar.Format(true, destination).ToString());
        Assert.Equal(XmlConvert.ToString(false), XmlWeaveScalar.Format(false, destination).ToString());
        Assert.True(XmlWeaveScalar.Parse<bool>(" true "));
        Assert.True(XmlWeaveScalar.Parse<bool>("1"));
        Assert.False(XmlWeaveScalar.Parse<bool>("0"));
        Assert.False(XmlWeaveScalar.Parse<bool>("false"));
        Assert.Throws<FormatException>(() => XmlWeaveScalar.Parse<bool>("True"));
        // Generated chars and enums use their numeric representation, never char/enum formatters.
        Check((ushort)'\uFFFF', XmlConvert.ToString((ushort)'\uFFFF'));
        Check((short)ExampleEnum.Value, XmlConvert.ToString((short)ExampleEnum.Value));
    }

    [Fact]
    public void SeededDurationsAndExtremaMatchXmlConvert()
    {
        foreach (TimeSpan value in new[] { TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromTicks(1), TimeSpan.FromDays(1), TimeSpan.FromHours(1), TimeSpan.FromMinutes(1) })
            Check(value, XmlConvert.ToString(value));
        var random = new Random(123);
        for (int i = 0; i < 100; i++)
        {
            long ticks = random.NextInt64();
            if ((i & 1) != 0) ticks = -ticks;
            TimeSpan value = new(ticks);
            Check(value, XmlConvert.ToString(value));
        }
        foreach (string text in new[] { "P1Y2M3DT4H5M6.123456789S", "PT.5S", "PT1.S", "-P13M", "P1Y", " P0D ", "-PT0.0000001S" })
            Assert.Equal(XmlConvert.ToTimeSpan(text), XmlWeaveScalar.Parse<TimeSpan>(text));
        foreach (string text in new[] { "P", "PT", "P1DT", "PT1H1H", "P1S", "P1.5D", "P-1D", "PT.S", "P1D1Y", "P1Q" })
            Assert.Throws<FormatException>(() => XmlWeaveScalar.Parse<TimeSpan>(text));
        Assert.Throws<OverflowException>(() => XmlWeaveScalar.Parse<TimeSpan>("P999999999999999D"));
    }

    [Fact]
    public void EscapingAndSmallSegmentSizeHintsPreserveXmlCharacters()
    {
        var output = new ExactBufferWriter();
        using (var writer = new XmlWeaveWriter(output))
        {
            writer.StartElement("root");
            writer.Attribute("a", "\r\n\t<&\"😀é中");
            writer.EndStartElement();
            writer.Text("\r\n\t<&>😀é中");
            writer.NullElement("null");
            writer.EndElement("root");
        }
        Assert.Equal("<root a=\"&#xD;&#xA;&#x9;&lt;&amp;&quot;😀é中\">&#xD;\n\t&lt;&amp;&gt;😀é中<null xmlns:p=\"urn:xmlweave:null\" p:nil=\"true\"></null></root>", output.Text);
        Assert.InRange(output.MaximumHint, 4, 128);
        Assert.True(output.Advances > 10);
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\u0001")]
    [InlineData("\uFFFF")]
    [InlineData("\uFFFE")]
    public void InvalidXmlCharactersAreRejected(string text)
    {
        Assert.Throws<XmlWeaveException>(() => WriteText(text, false));
        Assert.Throws<XmlWeaveException>(() => WriteText(text, true));
        Assert.Throws<XmlWeaveException>(() => WriteText(new string((char)0xD800, 1), false));
        Assert.Throws<XmlWeaveException>(() => WriteText(new string((char)0xDC00, 1), true));
    }

    [Fact]
    public void LimitsCountSerializedUtf16AndElementNesting()
    {
        WriteLimited("&", 12);
        Assert.Throws<XmlWeaveException>(() => WriteLimited("&", 11));
        WriteLimited("😀", 9);
        Assert.Throws<XmlWeaveException>(() => WriteLimited("😀", 8));
        Assert.Throws<XmlWeaveException>(() =>
        {
            using var writer = new XmlWeaveWriter(new ArrayBufferWriter<byte>(), new XmlWeaveOptions { MaxDepth = 1 });
            writer.StartElement("root");
            writer.NullElement("Entry");
        });
        using var valid = new XmlWeaveWriter(new ArrayBufferWriter<byte>(), new XmlWeaveOptions { MaxDepth = 1 });
        valid.StartElement("a"); valid.EndElement("a");
        valid.StartElement("b"); valid.EndElement("b");
    }

    [Fact]
    public void ActiveReferencesUseIdentityAllowSharingAndEnforceDepth()
    {
        object first = new EqualObject(), second = new EqualObject();
        using (var writer = new XmlWeaveWriter(new ArrayBufferWriter<byte>()))
        {
            writer.Enter(first); writer.Enter(second); writer.Exit();
            writer.Enter(second); writer.Exit(); writer.Exit();
        }
        Assert.Throws<XmlWeaveException>(() =>
        {
            using var writer = new XmlWeaveWriter(new ArrayBufferWriter<byte>());
            writer.Enter(first); writer.Enter(first);
        });
        Assert.Throws<XmlWeaveException>(() =>
        {
            using var writer = new XmlWeaveWriter(new ArrayBufferWriter<byte>(), new XmlWeaveOptions { MaxDepth = 1 });
            writer.Enter(first); writer.Enter(second);
        });
        // Exercise growth beyond the initial 64-reference rental, and return it on disposal.
        using var deep = new XmlWeaveWriter(new ArrayBufferWriter<byte>(), new XmlWeaveOptions { MaxDepth = 130 });
        for (int i = 0; i < 130; i++) deep.Enter(new object());
        for (int i = 0; i < 130; i++) deep.Exit();
    }

    [Fact]
    public void StreamAdapterWritesOnAdvanceAndLeavesStreamOpen()
    {
        using var stream = new MemoryStream();
        var adapter = new XmlWeaveStreamBufferWriter(stream);
        Assert.False(adapter.GetMemory().IsEmpty);
        Assert.True(adapter.GetSpan(8193).Length >= 8193);
        adapter.GetSpan(1)[0] = 42;
        Assert.Equal(0, stream.Length);
        adapter.Advance(1);
        Assert.Equal(1, stream.Length);
        Assert.Equal(42, stream.GetBuffer()[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.GetMemory(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.GetSpan(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.Advance(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.Advance(int.MaxValue));
        using (var writer = new XmlWeaveWriter(adapter)) writer.NullElement("x");
        adapter.Dispose(); adapter.Dispose();
        Assert.True(stream.CanWrite);
        Assert.EndsWith("<x xmlns:p=\"urn:xmlweave:null\" p:nil=\"true\"></x>", Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
        Assert.Throws<ObjectDisposedException>(() => adapter.GetMemory());
        Assert.Throws<ObjectDisposedException>(() => adapter.GetSpan());
        Assert.Throws<ObjectDisposedException>(() => adapter.Advance(0));
    }

    [Fact]
    public void EverySupportedScalarFormatsParsesAndWritesWithoutWarmAllocations()
    {
        var output = new ArrayBufferWriter<byte>(4096);
        object active = new();
        for (int i = 0; i < 500; i++) ExerciseScalars(output, active);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) ExerciseScalars(output, active);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void ExerciseScalars(ArrayBufferWriter<byte> output, object active)
    {
        output.Clear();
        var writer = new XmlWeaveWriter(output);
        writer.Enter(active); writer.StartElement("a");
        Emit(ref writer, sbyte.MinValue); Emit(ref writer, byte.MaxValue);
        Emit(ref writer, short.MinValue); Emit(ref writer, ushort.MaxValue);
        Emit(ref writer, int.MinValue); Emit(ref writer, uint.MaxValue);
        Emit(ref writer, long.MinValue); Emit(ref writer, ulong.MaxValue);
        Emit(ref writer, decimal.MaxValue); Emit(ref writer, float.Epsilon);
        Emit(ref writer, double.Epsilon); Emit(ref writer, float.PositiveInfinity);
        Emit(ref writer, double.NegativeInfinity); Emit(ref writer, double.NaN);
        Emit(ref writer, DateTime.UnixEpoch); Emit(ref writer, DateTimeOffset.UnixEpoch);
        Emit(ref writer, DateOnly.MinValue); Emit(ref writer, TimeOnly.MaxValue);
        Emit(ref writer, Guid.Empty); Emit(ref writer, TimeSpan.MinValue);
        Span<char> destination = stackalloc char[5];
        writer.Text(XmlWeaveScalar.Format(true, destination));
        writer.Text(XmlWeaveScalar.Format(false, destination));
        _ = XmlWeaveScalar.Parse<bool>("true");
        writer.Text("a<&\r\n\t😀"); writer.EndElement("a"); writer.Exit(); writer.Dispose();
    }

    private static void Emit<T>(ref XmlWeaveWriter writer, T value) where T : ISpanFormattable, ISpanParsable<T>
    {
        Span<char> destination = stackalloc char[128];
        ReadOnlySpan<char> text = XmlWeaveScalar.Format(value, destination);
        writer.Text(text);
        _ = XmlWeaveScalar.Parse<T>(text);
    }

    private static void Check<T>(T value, string expected) where T : ISpanFormattable, ISpanParsable<T>
    {
        Span<char> destination = stackalloc char[128];
        Assert.Equal(expected, XmlWeaveScalar.Format(value, destination).ToString());
        Assert.Equal(value, XmlWeaveScalar.Parse<T>(expected));
    }

    private static void WriteText(string text, bool attribute)
    {
        using var writer = new XmlWeaveWriter(new ArrayBufferWriter<byte>());
        writer.StartElement("a");
        if (attribute) writer.Attribute("v", text); else writer.Text(text);
    }

    private static void WriteLimited(string text, long limit)
    {
        using var writer = new XmlWeaveWriter(new ArrayBufferWriter<byte>(), new XmlWeaveOptions { MaxCharacters = limit });
        writer.StartElement("a"); writer.Text(text); writer.EndElement("a");
    }

    private enum ExampleEnum : short { Value = -123 }

    private sealed class EqualObject
    {
        public override bool Equals(object? obj) => obj is EqualObject;
        public override int GetHashCode() => 0;
    }

    private sealed class ExactBufferWriter : IBufferWriter<byte>
    {
        private readonly MemoryStream _output = new();
        private byte[] _segment = Array.Empty<byte>();
        public int MaximumHint { get; private set; }
        public int Advances { get; private set; }
        public string Text => Encoding.UTF8.GetString(_output.ToArray());
        public void Advance(int count)
        {
            Assert.InRange(count, 0, _segment.Length);
            _output.Write(_segment.AsSpan(0, count));
            Advances++;
            _segment = Array.Empty<byte>();
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Assert.True(sizeHint >= 0);
            MaximumHint = Math.Max(MaximumHint, sizeHint);
            _segment = new byte[Math.Max(1, sizeHint)];
            return _segment;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
