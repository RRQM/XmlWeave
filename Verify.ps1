param([switch]$Aot, [string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet 失败：$args" }
}
Invoke-DotNet test (Join-Path $PSScriptRoot 'src\XmlWeave.slnx') -c Release
Invoke-DotNet pack (Join-Path $PSScriptRoot 'src\XmlWeave\XmlWeave.csproj') -c Release -o artifacts
Invoke-DotNet pack (Join-Path $PSScriptRoot 'src\XmlWeave.SourceGenerator\XmlWeave.SourceGenerator.csproj') -c Release -o artifacts
$feed = Join-Path $PSScriptRoot 'artifacts'
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
