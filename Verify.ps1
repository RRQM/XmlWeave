param([switch]$Aot, [string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet 失败：$args" }
}
Invoke-DotNet test (Join-Path $PSScriptRoot 'src\XmlWeave.slnx') -c Release
Invoke-DotNet pack (Join-Path $PSScriptRoot 'src\XmlWeave\XmlWeave.csproj') -c Release -o artifacts
$feed = Join-Path $PSScriptRoot 'artifacts'
$version = Invoke-DotNet msbuild (Join-Path $PSScriptRoot 'src\XmlWeave\XmlWeave.csproj') '-getProperty:Version'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $feed "XmlWeave.$version.nupkg"))
try {
    if (-not $package.GetEntry('analyzers/dotnet/cs/XmlWeave.SourceGenerator.dll')) { throw '主包缺少源生成器。' }
    if (-not $package.GetEntry('lib/net8.0/XmlWeave.dll')) { throw '主包缺少运行时。' }
    $reader = [System.IO.StreamReader]::new($package.GetEntry('XmlWeave.nuspec').Open())
    try { [xml]$nuspec = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    if ($nuspec.SelectNodes("//*[local-name()='dependency' and (@id='XmlWeave.SourceGenerator' or starts-with(@id, 'Microsoft.CodeAnalysis'))]").Count -ne 0) {
        throw '主包不应声明源生成器或 Roslyn 包依赖。'
    }
}
finally { $package.Dispose() }
$consumerProject = Join-Path $PSScriptRoot 'src\XmlWeave.PackageTests\XmlWeave.PackageTests.csproj'
# 使用隔离包缓存，避免同版本的旧本地包影响验证。
$previousPackages = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = Join-Path $feed ('consumer-cache-' + [Guid]::NewGuid().ToString('N'))
    Invoke-DotNet restore $consumerProject --force "-p:RestoreAdditionalProjectSources=$feed"
    Invoke-DotNet run --project $consumerProject -c Release --no-restore
    $trimmed = Join-Path $feed 'trimmed'
    Invoke-DotNet publish $consumerProject -c Release -r $Runtime --self-contained true '-p:PublishTrimmed=true' '-p:ILLinkTreatWarningsAsErrors=true' -o $trimmed
    $exe = Join-Path $trimmed 'XmlWeave.PackageTests'
    if ($Runtime.StartsWith('win-')) { $exe += '.exe' }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw '裁剪验证失败。' }
    if ($Aot) {
        $aotOutput = Join-Path $feed 'aot'
        Invoke-DotNet publish $consumerProject -c Release -r $Runtime --self-contained true '-p:PublishAot=true' '-p:ILLinkTreatWarningsAsErrors=true' '-p:IlcTreatWarningsAsErrors=true' -o $aotOutput
        $exe = Join-Path $aotOutput 'XmlWeave.PackageTests'
        if ($Runtime.StartsWith('win-')) { $exe += '.exe' }
        & $exe
        if ($LASTEXITCODE -ne 0) { throw 'AOT 验证失败。' }
    }
}
finally {
    $cache = $env:NUGET_PACKAGES
    $env:NUGET_PACKAGES = $previousPackages
    if ($cache -and (Test-Path $cache)) { Remove-Item -LiteralPath $cache -Recurse -Force }
}
