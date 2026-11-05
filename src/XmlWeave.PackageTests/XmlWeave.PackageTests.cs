using System;
using System.Collections.Generic;
using System.IO;
using XmlWeave;

namespace XmlWeave.PackageTests;

[XmlWeaveRoot]
public class PackageModel
{
    public string Text { get; set; } = "";
    public string? Missing { get; set; }
    public decimal Number { get; set; }
    public List<int?> Values { get; set; } = new();
    public Dictionary<string, string?> Map { get; set; } = new();
}

internal static class Program
{
    private static void Main()
    {
        var model = new PackageModel
        {
            Text = "\r\n\t<&>😀", Number = 123.456m,
            Values = new List<int?> { null, 42 },
            Map = new Dictionary<string, string?> { ["\r\n"] = null, [""] = "" }
        };
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
        PackageModelXmlSerializer.Serialize(buffer, model);
        if (PackageModelXmlSerializer.Deserialize(buffer.WrittenSpan).Text != model.Text)
            throw new Exception("UTF8 缓冲区实际包验证失败。");
        var sequence = new System.Buffers.ReadOnlySequence<byte>(buffer.WrittenMemory);
        if (PackageModelXmlSerializer.Deserialize(sequence).Number != model.Number)
            throw new Exception("分段缓冲区实际包验证失败。");
        using var stream = new MemoryStream();
        PackageModelXmlSerializer.Serialize(stream, model);
        if (stream.ToArray()[0] != (byte)'<') throw new Exception("输出包含 BOM。");
        stream.Position = 0;
        var copy = PackageModelXmlSerializer.Deserialize(stream);
        if (copy.Text != model.Text || copy.Number != model.Number || copy.Missing != null ||
            copy.Values.Count != 2 || copy.Values[0] != null || copy.Values[1] != 42 ||
            copy.Map["\r\n"] != null || copy.Map[""] != "") throw new Exception("实际包往返验证失败。");
        try
        {
            PackageModelXmlSerializer.Deserialize("<!DOCTYPE PackageModel><PackageModel/>");
            throw new Exception("接受了 DTD。");
        }
        catch (XmlWeaveException) { }
        Console.WriteLine("XmlWeave 实际包消费者验证通过。");
    }
}
