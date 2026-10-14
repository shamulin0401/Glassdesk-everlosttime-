<#
.SYNOPSIS
    把当前工作区提交并推送到远端的 GitHub 仓库。

.DESCRIPTION
    做四件事：找到（必要时安装）git → 以仓库级身份提交全部源码 → 指向远端 → 推送。

    为什么这条命令要你自己在终端里跑：
    推送需要 GitHub 凭据。脚本不会、也不该替你保存 token——运行时由 git 自己弹出
    登录/浏览器授权（凭据交给 Windows 凭据管理器保管，不进仓库）。

    推送前会做一次体积体检：.gitignore 已排除 bin/obj/artifacts/Data 等，脚本再核对
    一次「暂存文件里有没有超过 GitHub 100MB 上限的」，有就中止，免得白推一次。

.PARAMETER Repository
    远端地址，例如 https://github.com/shamulin0401/Glassdesk-everlosttime-.git

.EXAMPLE
    pwsh build\upload-to-github.ps1 -Repository https://github.com/shamulin0401/Glassdesk-everlosttime-.git

.EXAMPLE
    已经推过一次、只想更新：
    pwsh build\upload-to-github.ps1 -Repository https://github.com/shamulin0401/Glassdesk-everlosttime-.git -Force
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Repository,

    [string] $CommitMessage = 'GlassDesk 1.0',

    [string] $UserName = 'shamulin0401',

    [string] $UserEmail = 'shamulin0401@users.noreply.github.com',

    [string] $Branch = 'main',

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

function Find-Git {
    $command = Get-Command git -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    foreach ($candidate in @(
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe",
        "$env:ProgramFiles\Git\cmd\git.exe",
        "${env:ProgramFiles(x86)}\Git\cmd\git.exe")) {
        if (Test-Path $candidate) { return $candidate }
    }

    return $null
}

try {
    $git = Find-Git

    if (-not $git) {
        Write-Host '未找到 git，尝试用户级安装（不弹 UAC）…'
        & winget install --id Git.Git -e --scope user --accept-package-agreements --accept-source-agreements
        $git = Find-Git
    }

    if (-not $git) {
        throw '仍然找不到 git。请手动安装：winget install --id Git.Git -e --scope user'
    }

    Write-Host "使用 git：$git"
    & $git --version

    if (-not (Test-Path (Join-Path $repoRoot '.git'))) {
        Write-Host '初始化仓库…'
        & $git init -b $Branch
    }

    # 仓库级身份，不动你的全局配置。
    & $git config user.name $UserName
    & $git config user.email $UserEmail
    & $git config core.autocrlf false

    Write-Host '暂存文件…'
    & $git add -A

    $staged = @(& $git diff --cached --name-only)
    Write-Host "暂存 $($staged.Count) 个文件。"

    Write-Host '体积体检：'
    $oversized = @()

    foreach ($relative in $staged) {
        $full = Join-Path $repoRoot $relative
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            $size = (Get-Item -LiteralPath $full).Length
            if ($size -gt 100MB) { $oversized += [pscustomobject]@{ File = $relative; MB = [math]::Round($size / 1MB, 1) } }
        }
    }

    if ($oversized.Count -gt 0) {
        $oversized | ForEach-Object { Write-Warning "$($_.File) 有 $($_.MB) MB，超过 GitHub 单文件 100MB 上限。" }
        throw '请把这些文件加入 .gitignore（或改用 Git LFS / GitHub Releases 分发），再重新运行。'
    }

    Write-Host '最大的三个暂存文件：'
    $staged |
        ForEach-Object { Get-Item -LiteralPath (Join-Path $repoRoot $_) -ErrorAction SilentlyContinue } |
        Where-Object { $_ } |
        Sort-Object Length -Descending |
        Select-Object -First 3 |
        ForEach-Object { Write-Host ("  {0}  {1} KB" -f $_.Name, [math]::Round($_.Length / 1KB)) }

    if ($staged.Count -gt 0) {
        & $git commit -q -m $CommitMessage
        Write-Host '提交完成。'
    }
    else {
        Write-Host '没有需要提交的改动。'
    }

    $existing = & $git remote
    if ($existing -contains 'origin') {
        & $git remote set-url origin $Repository
    }
    else {
        & $git remote add origin $Repository
    }

    Write-Host "推送到 $Repository …（如需登录，git 会在这里提示你）"

    if ($Force) {
        & $git push -u origin $Branch
    }
    else {
        & $git push -u origin $Branch
    }

    if ($LASTEXITCODE -ne 0) { throw "推送失败（退出码 $LASTEXITCODE）。" }

    Write-Host '完成。' -ForegroundColor Green
}
finally {
    Pop-Location
}
