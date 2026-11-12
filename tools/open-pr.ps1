# open-pr.ps1 —— 用 GitHub REST API 推送分支并创建 PR（git push 不通时的备用通道）
#
# 用法：
#   .\open-pr.ps1 -HeadBranch feature/per-lesson-undo -Title "feat: ..." -BodyPath .\docs\pr-body.md
#
# 原理：以远端 base 分支 HEAD 为父提交，把本地 `base..head` 的改动上传成新提交，
# 在远端创建 head 分支引用，然后调用 Pull Requests API 建 PR。
#
# 凭据：复用 git 凭据管理器里 github.com 的 token（不会打印明文）。

[CmdletBinding()]
param(
    [string] $Repo = 'MuttonIsHere/QqClassSync',
    [string] $BaseBranch = 'main',
    [string] $HeadBranch = '',
    [string] $Title = '',
    [string] $BodyPath = '',
    [string] $Workspace = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($Workspace)) {
    $Workspace = Split-Path $PSScriptRoot -Parent
}
if ([string]::IsNullOrWhiteSpace($HeadBranch)) { throw '必须指定 -HeadBranch' }

$git = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe'
$credLines = @("protocol=https`nhost=github.com`n`n" | & $git credential fill 2>$null)
$token = $null
foreach ($l in $credLines) { if ($l -match '^password=(.*)$') { $token = $Matches[1] } }
if (-not $token) { throw '没有可用的 GitHub 凭据' }

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
    $curlArgs = @('-sS', '--ssl-no-revoke', '-m', '120', '--config', '-', '-X', $Method)
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

Write-Host "== 计算 $BaseBranch..$HeadBranch 的改动 =="
$files = @(& $git -C $Workspace diff --name-only "$BaseBranch..$HeadBranch")
if ($files.Count -eq 0) { throw "两个分支之间没有差异" }
$files | ForEach-Object { Write-Host "  $_" }

Write-Host "== 取远端 $BaseBranch HEAD =="
$r = Invoke-GH -Method GET -Path "/repos/$Repo/git/ref/heads/$BaseBranch"
if ($r.Status -ne '200') { throw "读取 base 分支失败：$($r.Status) $($r.Body)" }
$baseSha = ($r.Body | ConvertFrom-Json).object.sha

$r = Invoke-GH -Method GET -Path "/repos/$Repo/git/commits/$baseSha"
if ($r.Status -ne '200') { throw "读取 base 提交失败：$($r.Status)" }
$baseTree = ($r.Body | ConvertFrom-Json).tree.sha
Write-Host "base=$baseSha"

Write-Host '== 上传文件为 blob =='
$entries = @()
foreach ($rel in $files) {
    $full = Join-Path $Workspace ($rel -replace '/', '\')
    if (-not (Test-Path -LiteralPath $full)) { throw "本地文件不存在：$full" }
    $b64 = [System.Convert]::ToBase64String([System.IO.File]::ReadAllBytes($full))
    $r = Invoke-GH -Method POST -Path "/repos/$Repo/git/blobs" -Json (@{ content = $b64; encoding = 'base64' } | ConvertTo-Json -Compress)
    if ($r.Status -ne '201') { throw "上传 blob 失败（$rel）：$($r.Status) $($r.Body)" }
    $entries += [ordered]@{ path = $rel; mode = '100644'; type = 'blob'; sha = ($r.Body | ConvertFrom-Json).sha }
    Write-Host "  $rel"
}

Write-Host '== 建 tree / commit =='
$r = Invoke-GH -Method POST -Path "/repos/$Repo/git/trees" -Json (@{ base_tree = $baseTree; tree = $entries } | ConvertTo-Json -Depth 6 -Compress)
if ($r.Status -ne '201') { throw "建 tree 失败：$($r.Status) $($r.Body)" }
$newTree = ($r.Body | ConvertFrom-Json).sha

$commitMessage = if ($Title) { $Title } else { "chore: 更新 $HeadBranch" }
$r = Invoke-GH -Method POST -Path "/repos/$Repo/git/commits" -Json (@{ message = $commitMessage; tree = $newTree; parents = @($baseSha) } | ConvertTo-Json -Compress)
if ($r.Status -ne '201') { throw "建 commit 失败：$($r.Status) $($r.Body)" }
$newCommit = ($r.Body | ConvertFrom-Json).sha
Write-Host "commit=$newCommit"

Write-Host "== 创建分支 $HeadBranch =="
$r = Invoke-GH -Method POST -Path "/repos/$Repo/git/refs" -Json (@{ ref = "refs/heads/$HeadBranch"; sha = $newCommit } | ConvertTo-Json -Compress)
if ($r.Status -eq '422') {
    $r = Invoke-GH -Method PATCH -Path "/repos/$Repo/git/refs/heads/$HeadBranch" -Json (@{ sha = $newCommit; force = $true } | ConvertTo-Json -Compress)
}
if ($r.Status -notin @('200', '201')) { throw "创建/更新分支失败：$($r.Status) $($r.Body)" }

Write-Host '== 创建 Pull Request =='
$body = if ($BodyPath -and (Test-Path -LiteralPath $BodyPath)) {
    [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $BodyPath), [System.Text.Encoding]::UTF8)
} else {
    $Title
}
$payload = [ordered]@{
    title = $Title
    head = $HeadBranch
    base = $BaseBranch
    body = $body
    draft = $false
} | ConvertTo-Json -Compress
$r = Invoke-GH -Method POST -Path "/repos/$Repo/pulls" -Json $payload
if ($r.Status -notin @('200', '201')) { throw "创建 PR 失败：$($r.Status) $($r.Body)" }
$pr = $r.Body | ConvertFrom-Json

Write-Host ''
Write-Host "PR 已创建：#$($pr.number) $($pr.html_url)" -ForegroundColor Green
