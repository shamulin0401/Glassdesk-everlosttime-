<#
.SYNOPSIS
    生成 GlassDesk 的 exe 安装包（用户级，单文件）。

.DESCRIPTION
    三步：把 artifacts\publish 暂存一份（排除 Data\*，与 packaging\GlassDesk-Capture.iss 一致）
    → 压成 payload.zip → publish 出内嵌载荷的 GlassDesk-Setup.exe。

    之所以不用 packaging\GlassDesk-Capture.iss：那需要 Inno Setup（iscc.exe），本机没有装，
    而这个脚本只依赖已安装的 .NET SDK，产物同样是单个 exe。

.PARAMETER OutputDirectory
    安装包输出目录，默认 artifacts\installer。

.EXAMPLE
    pwsh build\package-setup.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = 'artifacts\installer',
    [string] $PublishDirectory = 'artifacts\publish'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

    if (-not (Test-Path $PublishDirectory)) {
        throw "找不到 $PublishDirectory；请先运行 build\publish.ps1 生成发布件。"
    }

    $version = ([xml](Get-Content 'Directory.Build.props' -Raw)).PropertyGroup.Version
    $version = @($version | Where-Object { $_ })[0]

    Write-Host "暂存载荷（排除 Data\*）…"
    $stage = Join-Path $env:TEMP "glassdesk-stage-$version"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage | Out-Null
    Get-ChildItem $PublishDirectory -Force |
        Where-Object { $_.Name -ne 'Data' } |
        ForEach-Object { Copy-Item $_.FullName -Destination $stage -Recurse -Force }

    $payload = Join-Path $repoRoot 'src\GlassDesk.Setup\payload.zip'
    if (Test-Path $payload) { Remove-Item $payload -Force }
    Write-Host "压缩载荷…"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $payload -CompressionLevel Optimal

    Write-Host "生成安装包…"
    & $dotnet publish 'src\GlassDesk.Setup\GlassDesk.Setup.csproj' -c Release -r win-x64 `
        --self-contained true -o $OutputDirectory

    if ($LASTEXITCODE -ne 0) { throw "publish 失败（退出码 $LASTEXITCODE）。" }

    # 与 .iss 的 OutputBaseFilename={#PackageBaseName}-Setup 对齐。
    $built = Join-Path $OutputDirectory 'GlassDesk-Setup.exe'
    $final = Join-Path $OutputDirectory "GlassDesk-$version-Setup.exe"
    Copy-Item $built $final -Force

    # 载荷只是构建中间物，不留在源码树里。
    Remove-Item $payload -Force
    Remove-Item $stage -Recurse -Force

    $size = [math]::Round((Get-Item $final).Length / 1MB, 1)
    Write-Host "完成：$final（$size MB）"
}
finally {
    Pop-Location
}
