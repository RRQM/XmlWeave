# XmlWeave

通用、强类型、无运行时反射的 XML 序列化库。MIT 许可证。

> 当前版本 **0.1.0**，运行时仅支持 .NET 10。源码包含 Linux CI；Linux 验证以 GitHub Actions 实际运行结果为准。尚未发布到 NuGet。

## 包与平台

- `XmlWeave`：运行时，目标 .NET 10。
- `XmlWeave.SourceGenerator`：增量源生成器，目标 netstandard2.0，编译器要求 Roslyn 4.8+。通过 NuGet 的 `analyzers/dotnet/cs` 自动加载，不向消费者添加运行时依赖。
- 两个包均为 `0.1.0`；安装时需要同时引用。NuGet 名称是否可注册以实际发布结果为准。

## 公共 API

根模型使用 `XmlWeaveRootAttribute` 显式标记，生成同命名空间下的 `类型名XmlSerializer` 静态类。无需根类型声明为 partial。

生成的 `Serialize(IBufferWriter<byte>, T, XmlWeaveOptions?)` 直接输出 UTF-8；`Deserialize(ReadOnlySpan<byte>, XmlWeaveOptions?)` 直接读取 UTF-8。支持 `Deserialize(in ReadOnlySequence<byte>, XmlWeaveOptions?)`；单段直接读取，多段使用 ArrayPool 合并后解析，不支持暂停/续读。

便捷入口 `Serialize(T, XmlWeaveOptions?)` 返回字符串；`Serialize(Stream, T, XmlWeaveOptions?)` 写入 UTF8 无 BOM。`Deserialize(string, XmlWeaveOptions?)` 和 `Deserialize(Stream, XmlWeaveOptions?)` 返回强类型模型。所有流 API 均保留调用者的流，流写入不是原子操作。缓冲区和流读取入口要求 UTF-8（允许 UTF-8 BOM）；不再自动识别 UTF-16 等其他编码。

`XmlWeaveMemberAttribute.Name` 覆盖 XML 名称，`Representation` 可指定 Auto、Attribute、Element。`XmlWeaveIgnoreAttribute` 显式排除成员。`XmlWeaveConverterAttribute` 指定实现 `IXmlWeaveConverter<T>` 的无状态转换器；其 Format/Parse 负责文化无关表示。

## 分配边界

- 参考 System.Text.Json 的源生成快速路径：编译期生成成员分派和标量处理，无运行时反射、DOM 或标量字符串中转。
- `IBufferWriter<byte>` 写入在缓冲区容量足够、共享池及类型初始化预热后，内置类型路径零额外托管分配；池首次扩容、调用者缓冲区增长不在此承诺内。写入失败可能留下部分输出。
- Span 读取只分配返回模型、字符串和集合等结果数据；读取器栈使用可归还缓冲区。数组和集合可能需要容量增长/构建暂存，不承诺整个反序列化过程绝对 0 GC。
- string、Stream 便捷入口和传统字符串转换器不承诺零分配。转换器实例按生成器类型缓存，必须无状态且线程安全。
- `BufferTests` 通过 `GC.GetAllocatedBytesForCurrentThread` 验证预热后的标量模型缓冲区写入为零、读取与仅创建模型的分配相等。

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

## 源码与解决方案

`src/XmlWeave.slnx` 为构建和测试入口，需要 .NET SDK 10.0.400 或更新的 10.0 补丁版本（由根目录 `global.json` 固定特性带）。运行时、测试及包消费者目标 net10.0；源生成器保留 netstandard2.0，以兼容编译器分析器加载环境，不代表运行时支持 .NET 8。

解决方案和公共构建配置独立置于 `src`，各项目及其源码放在各自子目录：

- `src/XmlWeave.slnx`、`src/Directory.Build.props`：解决方案及公共构建配置，配置文件显示在 `.Solutions` 解决方案文件夹中。
- `src/XmlWeave/`：运行时项目及源码。
- `src/XmlWeave.SourceGenerator/`：源生成器项目及源码。
- `src/XmlWeave.Tests/`：单元测试项目及源码。
- `src/XmlWeave.PackageTests/`：包消费者测试项目及源码，不加入解决方案，以便先打包再从本地源安装验证。
- 根目录保留验证脚本、SDK 配置、文档与许可证。

可在根目录执行 `dotnet build src/XmlWeave.slnx -c Release`。

## 验证与打包

运行 `dotnet test src/XmlWeave.slnx -c Release`。测试包含生成器诊断、生成代码编译、往返边界、继承、可空、集合、字典、文本精确保留、严格结构、安全读取及对象图循环。

`./Verify.ps1` 执行测试、两个包的打包、隔离缓存的真实 NuGet 安装消费者运行及裁剪发布运行。Windows 使用 `./Verify.ps1 -Aot -Runtime win-x64` 额外验证 NativeAOT；Linux 可使用 PowerShell 7 执行 `./Verify.ps1 -Aot -Runtime linux-x64`。AOT 要求对应平台原生工具链。

本地构建包路径：`artifacts/XmlWeave.0.1.0.nupkg` 和 `artifacts/XmlWeave.SourceGenerator.0.1.0.nupkg`。消费者验证项目仅使用 PackageReference，不使用 ProjectReference；它是自动化测试而非使用示例。

## 发布与 CI 状态

验证脚本使用 .NET 10 执行测试、实际包消费、裁剪及可选 NativeAOT 验证。GitHub Actions 工作流位于 `.github/workflows/ci.yml`，推送和 PR 时在 Linux 执行完整验证并上传 nupkg；不自动发布到 NuGet。

实际发布需要维护者明确授权并配置 NuGet Trusted Publishing 或提供受控发布凭据；本实现不读取或搜索敏感凭据。原子文件保存、业务格式版本及消费者迁移策略不属于本库职责。
