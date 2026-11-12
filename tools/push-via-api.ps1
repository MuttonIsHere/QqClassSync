# push-via-api.ps1 —— 当 `git push` 连不上 github.com 时，用 GitHub REST API 推送本地未推送的提交。
#
# 原理：把 `origin/main..HEAD` 改动的文件逐个上传为 blob，再基于远端 HEAD 的 tree 建新 tree、
# 建 commit、最后更新分支引用。效果等价于 git push（提交历史会多一条，SHA 与本地不同）。
#
# 用法：
#   .\push-via-api.ps1                      # 推送当前仓库未推送的改动
#   .\push-via-api.ps1 -CommitMessage "..." # 自定义提交信息（默认取本地最新提交信息）
#
# 凭据：复用 git 凭据管理器里 github.com 的 token（不会打印明文）。

[CmdletBinding()]
param(
    [string] $Repo = 'MuttonIsHere/QqClassSync',
    [string] $Branch = 'main',
    [string] $Workspace = '',
    [string] $CommitMessage = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($Workspace)) {
    $Workspace = Split-Path $PSScriptRoot -Parent
}

$git = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe'
if (-not (Test-Path $git)) { throw "找不到 git: $git" }

$credLines = @("protocol=https`nhost=github.com`n`n" | & $git credential fill 2>$null)
$token = $null
foreach ($l in $credLines) { if ($l -match '^password=(.*)$') { $token = $Matches[1] } }
if (-not $token) { throw '没有可用的 GitHub 凭据（git credential fill 没返回 password）' }

function Invoke-GH {
    param([string] $Method, [string] $Path, [string] $Json = '')
    $cfg = @(
        "url = `"https://api.github.com$Path`"",
        "header = `"Authorization: Bearer $token`"",
        "header = `"Accept: application/vnd.github+json`"",
        "header = `"User-Agent: DSH-Agent`"",
        "header = `"X-GitHub-Api-Version: 2022-11-28`""
    ) -join "`n"
    $tmp = $null
    $curlArgs = @('-sS', '--ssl-no-revoke', '-m', '90', '--config', '-', '-X', $Method)
    if ($Json) {
        $tmp = [System.IO.Path]::GetTempFileName()
        [System.IO.File]::WriteAllText($tmp, $Json, (New-Object System.Text.UTF8Encoding($false)))
        $curlArgs += @('-H', 'Content-Type: application/json', '--data-binary', "@$tmp")
    }
    $curlArgs += @('-w', "`n__STATUS__%{http_code}")
    $raw = ($cfg | & curl.exe @curlArgs 2>&1 | Out-String)
    if ($tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    $status = ''
    if ($raw -match '__STATUS__(\d+)') {
        $status = $Matches[1]
        $raw = $raw -replace "`n?__STATUS__\d+\s*$", ''
    }
    $raw = $raw -replace 'gh[pousr]_[A-Za-z0-9_]+', '***REDACTED***'
    return [pscustomobject]@{ Status = $status; Body = $raw.Trim() }
}

Write-Host '== 找出未推送的改动 =='
$files = @(& $git -C $Workspace diff --name-only "origin/$Branch..HEAD")
if ($files.Count -eq 0) {
    Write-Host '没有未推送的改动，退出。'
    exit 0
}
$files | ForEach-Object { Write-Host "  $_" }

if ([string]::IsNullOrWhiteSpace($CommitMessage)) {
    $CommitMessage = (& $git -C $Workspace log -1 --pretty=%s)
}

Write-Host '== 取远端 HEAD =='
$r = Invoke-GH -Method GET -Path "/repos/$Repo/git/ref/heads/$Branch"
if ($r.Status -ne '200') { throw "读取分支失败：$($r.Status) $($r.Body)" }
$parentSha = ($r.Body | ConvertFrom-Json).object.sha
Write-Host "远端 HEAD = $parentSha"

$r = Invoke-GH -Method GET -Path "/repos/$Repo/git/commits/$parentSha"
if ($r.Status -ne '200') { throw "读取提交失败：$($r.Status)" }
$baseTree = ($r.Body | ConvertFrom-Json).tree.sha

Write-Host '== 上传文件 =='
$entries = @()
foreach ($rel in $files) {
    $full = Join-Path $Workspace ($rel -replace '/', '\')
    if (-not (Test-Path -LiteralPath $full)) { throw "本地文件不存在：$full" }
    $b64 = [System.Convert]::ToBase64String([System.IO.File]::ReadAllBytes($full))
    $payload = @{ content = $b64; encoding = 'base64' } | ConvertTo-Json -Compress
    $r = Invoke-GH -Method POST -Path "/repos/$Repo/git/blobs" -Json $payload
    if ($r.Status -ne '201') { throw "上传 blob 失败（$rel）：$($r.Status) $($r.Body)" }
    $entries += [ordered]@{
        path = $rel
        mode = '100644'
        type = 'blob'
        sha = ($r.Body | ConvertFrom-Json).sha
    }
    Write-Host "  $rel"
}

Write-Host '== 建 tree / commit =='
$treePayload = @{ base_tree = $baseTree; tree = $entries } | ConvertTo-Json -Depth 6 -Compress
$r = Invoke-GH -Method POST -Path "/repos/$Repo/git/trees" -Json $treePayload
if ($r.Status -ne '201') { throw "建 tree 失败：$($r.Status) $($r.Body)" }
$newTree = ($r.Body | ConvertFrom-Json).sha

$commitPayload = @{ message = $CommitMessage; tree = $newTree; parents = @($parentSha) } | ConvertTo-Json -Compress
$r = Invoke-GH -Method POST -Path "/repos/$Repo/git/commits" -Json $commitPayload
if ($r.Status -ne '201') { throw "建 commit 失败：$($r.Status) $($r.Body)" }
$newCommit = ($r.Body | ConvertFrom-Json).sha

Write-Host '== 更新分支 =='
$r = Invoke-GH -Method PATCH -Path "/repos/$Repo/git/refs/heads/$Branch" -Json (@{ sha = $newCommit; force = $false } | ConvertTo-Json -Compress)
if ($r.Status -notin @('200', '201')) { throw "更新分支失败：$($r.Status) $($r.Body)" }

Write-Host ''
Write-Host "推送完成：$newCommit" -ForegroundColor Green
Write-Host "https://github.com/$Repo/commit/$newCommit"
Write-Host '本地与远端 SHA 会不一致，后续请先 git fetch 再继续提交；或等 github.com 恢复后正常 push。' -ForegroundColor DarkGray
