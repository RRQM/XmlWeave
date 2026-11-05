# XmlWeave

![XmlWeave icon](https://raw.githubusercontent.com/RRQM/XmlWeave/main/assets/icon.png)

通用、强类型、无运行时反射的 XML 序列化库。通过增量源生成器在编译期生成专用序列化代码，适用于 .NET 10、裁剪和 NativeAOT 应用。MIT 许可证。

[NuGet 包](https://www.nuget.org/packages/XmlWeave) · [源码与问题反馈](https://github.com/RRQM/XmlWeave)

## 核心特性

- **编译期生成**：标记模型即可获得强类型序列化器，无运行时反射、动态代码生成或模型 DOM。
- **单包即用**：运行时与源生成器一并提供，无需单独安装分析器。
- **多种入口**：支持字符串、Stream、UTF-8 Span、IBufferWriter<byte> 和 ReadOnlySequence<byte>。
- **低分配路径**：UTF-8 缓冲区入口直接读写；预热后的内置标量模型写入可实现零额外托管分配，具体边界见下文。
- **丰富模型支持**：常用标量、可空类型、嵌套对象、继承、一维数组、List 和 Dictionary。
- **可控 XML 表示**：属性或元素映射、成员重命名、忽略成员、自定义转换器，以及缩进和换行配置。
- **严格读取**：拒绝未知或重复成员，异常提供 XML 路径和位置；禁止 DTD 和外部资源，并限制深度与字符数。
- **AOT 友好**：支持裁剪和 NativeAOT，无需运行时动态生成代码。

## 安装与基本用法

项目需要以 `net10.0` 为目标，并使用 .NET SDK 10.0.400 或更新的 10.0 补丁版本。

```shell
dotnet add package XmlWeave
```

在控制台项目的 `Program.cs` 中使用以下完整示例：

```csharp
using System;
using XmlWeave;

var book = new Book { Id = 1, Title = "Hello XML" };
string xml = BookXmlSerializer.Serialize(book);
Console.WriteLine(xml);

Book restored = BookXmlSerializer.Deserialize(xml);
Console.WriteLine(restored.Title);

[XmlWeaveRoot]
public class Book
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}
```

`BookXmlSerializer` 由源生成器自动生成，与 `Book` 位于同一命名空间，不需要手写或声明 `partial`。默认情况下，简单成员写为 XML 属性，复杂成员写为子元素。模型应为顶级、非泛型、非抽象类，具有可访问的无参构造函数和公开可读写成员。

### 自定义成员映射

```csharp
[XmlWeaveRoot]
public class Article
{
    [XmlWeaveMember(Name = "id")]
    public int Id { get; set; }

    [XmlWeaveMember(Representation = XmlWeaveRepresentation.Element)]
    public string Title { get; set; } = "";

    [XmlWeaveIgnore]
    public string LocalNote { get; set; } = "";
}
```

仍使用 `ArticleXmlSerializer.Serialize` / `Deserialize` 读写。输入须符合模型契约，不会静默忽略未知成员；不支持自动多态或任意 XML 命名空间映射。

### UTF-8 缓冲区与流

以下代码接在基本用法中的模型创建之后：

```csharp
var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
BookXmlSerializer.Serialize(buffer, book);
Book fromUtf8 = BookXmlSerializer.Deserialize(buffer.WrittenSpan);

using var stream = new System.IO.MemoryStream();
BookXmlSerializer.Serialize(stream, book);
stream.Position = 0;
Book fromStream = BookXmlSerializer.Deserialize(stream);
```

流写入为 UTF-8 无 BOM，调用结束后不会关闭流；读取前应定位到 XML 起始位置。需要可读格式时，可向 `Serialize` 传入 `XmlWeaveOptions`，详见“序列化格式”。

## 公共 API

根模型使用 `XmlWeaveRootAttribute` 显式标记，生成同命名空间下的 `类型名XmlSerializer` 静态类。无需根类型声明为 `partial`。

生成的 `Serialize(IBufferWriter<byte>, T, XmlWeaveOptions?)` 直接输出 UTF-8；`Deserialize(ReadOnlySpan<byte>, XmlWeaveOptions?)` 直接读取 UTF-8。支持 `Deserialize(in ReadOnlySequence<byte>, XmlWeaveOptions?)`；单段直接读取，多段使用 ArrayPool 合并后解析，不支持暂停/续读。

便捷入口 `Serialize(T, XmlWeaveOptions?)` 返回字符串；`Serialize(Stream, T, XmlWeaveOptions?)` 写入 UTF8 无 BOM。`Deserialize(string, XmlWeaveOptions?)` 和 `Deserialize(Stream, XmlWeaveOptions?)` 返回强类型模型。所有流 API 均保留调用者的流，流写入不是原子操作。缓冲区和流读取入口要求 UTF-8（允许 UTF-8 BOM）；不再自动识别 UTF-16 等其他编码。

`XmlWeaveMemberAttribute.Name` 覆盖 XML 名称，`Representation` 可指定 Auto、Attribute、Element。`XmlWeaveIgnoreAttribute` 显式排除成员。`XmlWeaveConverterAttribute` 指定实现 `IXmlWeaveConverter<T>` 的无状态转换器；其 Format/Parse 负责文化无关表示。

## 序列化格式

生成的序列化器所有写入入口（字符串、Stream、IBufferWriter<byte>）均通过 `XmlWeaveOptions` 控制格式，默认保持原有紧凑输出：

```csharp
var options = new XmlWeaveOptions
{
    NewLineOnElements = true,   // 子元素及包含子元素的结束标签换行
    Indent = true,              // 对新增的格式化行按深度缩进
    NewLineOnAttributes = true, // 每个属性单独换行（包括命名空间和 null 标记）
    IndentChars = "  ",         // 每级两个空格，也可使用 "\t"
    NewLineChars = "\n"         // 支持 "\n"、"\r\n"、"\r"
};
var xml = MyModelXmlSerializer.Serialize(model, options);
```

三个开关独立控制：仅开启 `Indent` 不产生换行；仅开启属性换行时，元素仍可保持同行。属性缩进比所属元素多一级。缩进字符只允许空格或制表符，也可为空。标量文本、空字符串及空节点不会被插入格式化空白；文本和属性中的原始换行仍按原有转义规则保留。格式化增加的空白计入 `MaxCharacters`。这些设置不改变读取规则；传统 `XmlWeaveDocument.Write(XElement)` 辅助入口仍使用紧凑格式。

## 分配边界

- `IBufferWriter<byte>` 写入在缓冲区容量足够、共享池及类型初始化预热后，内置类型路径零额外托管分配；池首次扩容、调用者缓冲区增长不在此承诺内。写入失败可能留下部分输出。
- Span 读取只分配返回模型、字符串和集合等结果数据；读取器栈使用可归还缓冲区。数组和集合可能需要容量增长/构建暂存，不承诺整个反序列化过程绝对 0 GC。
- string、Stream 便捷入口和传统字符串转换器不承诺零分配。转换器实例按生成器类型缓存，必须无状态且线程安全。

```csharp
var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
// 对已标记 [XmlWeaveRoot] 的 MyModel，由生成器产生以下入口：
MyModelXmlSerializer.Serialize(buffer, model);
var copy = MyModelXmlSerializer.Deserialize(buffer.WrittenSpan);
```

## 模型与格式契约

- 简单成员默认属性，复杂成员和集合默认子元素。简单成员可以强制表示为子元素，复杂成员不能表示为属性。
- 支持字符串、布尔、整数、浮点、decimal、char、DateTime、DateTimeOffset、DateOnly、TimeOnly、TimeSpan、Guid、枚举以及 nullable。枚举保存底层整数（包括未命名值），不依赖运行时枚举反射。内置非字符串标量读取使用 256 个字符的栈缓冲区，超过此长度的标量文本会被拒绝；字符串及自定义转换器不受此长度限制。
- char 保存 UTF16 数值，字符串遵守 XML 1.0 可用字符范围；不合法 XML 字符拒绝写入。换行、CR/LF、tab、特殊字符通过 XML 转义精确保留。
- 可空属性缺省代表 null，空字符串代表空值。复杂/元素成员始终存在，null 使用 `urn:xmlweave:null` 命名空间的 nil 属性显式表示。启用可空引用标注时，非空引用严格拒绝 null；未启用标注的引用按可空处理。
- 支持公开可读写字段/属性、继承成员、顶级非泛型非抽象类和可访问无参构造函数。readonly、required、init、索引器等不支持的公开实例成员必须显式忽略，否则产生 XW001 编译错误，不会静默丢弃。
- 支持一维数组、List 和 Dictionary；集合使用 Item，字典使用 Entry/Key/Value。字典键要求非空标量，重复键和自定义比较器拒绝处理。保留字典枚举顺序，不承诺规范化排序。
- 不支持对象标识保留、多态自动扫描、接口集合、动态 object、泛型/嵌套模型及命名空间映射；自定义类型可使用显式转换器。循环模型类型可以生成，循环对象图写入时拒绝；共享对象按值复制。运行时派生类型严格拒绝，避免静默丢失派生成员。
- 严格拒绝未知属性、未知元素、重复成员、缺失非空属性和缺失元素。格式异常提供 XML 路径、行、列；读取器级异常只能提供根路径。XML 注释及处理指令不参与模型数据。
- XML 读取禁止 DTD 和外部资源。默认最大深度 64、字符数 10,000,000，通过 XmlWeaveOptions 可调整。生成代码不构建 XElement 中间树，直接按 token 读取、按成员写入 UTF-8；深度及字符限制在处理时检查。Stream 读取使用有上限的池化输入缓冲区，不是增量网络解析；Stream 写入直接输出，不构建完整文档。转换器是受信任的应用程序代码，不在隔离环境执行。
