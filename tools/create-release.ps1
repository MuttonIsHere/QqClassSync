# create-release.ps1 —— 用 GitHub REST API 创建 Release 并上传插件包（git push 不通时的备用通道）
#
# 用法：
#   .\create-release.ps1 -Tag v1.0.0 -Name "v1.0.0" -Asset "..\plugin\QqClassSync\cipx\QqClassSync.cipx"
#
# 凭据：复用 git 凭据管理器里 github.com 的 token（不会打印明文）。

[CmdletBinding()]
param(
    [string] $Repo = 'MuttonIsHere/QqClassSync',
    [string] $Tag = 'v1.0.0',
    [string] $Name = 'v1.0.0',
    [string] $TargetCommitish = 'main',
    [string] $AssetPath = '',
    [string] $BodyPath = '',
    [switch] $Prerelease
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$git = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe'
$credLines = @("protocol=https`nhost=github.com`n`n" | & $git credential fill 2>$null)
$token = $null
foreach ($l in $credLines) { if ($l -match '^password=(.*)$') { $token = $Matches[1] } }
if (-not $token) { throw '没有可用的 GitHub 凭据' }

function Invoke-GH {
    param([string] $Method, [string] $Path, [string] $Json = '', [string] $Url = '')
    $target = if ($Url) { $Url } else { "https://api.github.com$Path" }
    $cfg = @(
        "url = `"$target`"",
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

$body = if ($BodyPath -and (Test-Path -LiteralPath $BodyPath)) {
    [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $BodyPath), [System.Text.Encoding]::UTF8)
} else {
    "## $Name`n`nQQ 群聊/私聊「#调课」→ ClassIsland 临时层课表。"
}

Write-Host "== 创建 Release $Tag =="
$payload = [ordered]@{
    tag_name = $Tag
    target_commitish = $TargetCommitish
    name = $Name
    body = $body
    draft = $false
    prerelease = [bool]$Prerelease
} | ConvertTo-Json -Compress

$r = Invoke-GH -Method POST -Path "/repos/$Repo/releases" -Json $payload
if ($r.Status -eq '422') {
    Write-Host '该 tag 的 Release 已存在，改为查询现有 Release…'
    $r = Invoke-GH -Method GET -Path "/repos/$Repo/releases/tags/$Tag"
}
if ($r.Status -notin @('200', '201')) { throw "创建 Release 失败：$($r.Status) $($r.Body)" }

$release = $r.Body | ConvertFrom-Json
Write-Host "Release id=$($release.id) html_url=$($release.html_url)"

if ($AssetPath) {
    if (-not (Test-Path -LiteralPath $AssetPath)) { throw "插件包不存在：$AssetPath" }
    $assetFile = Get-Item -LiteralPath $AssetPath
    $uploadUrl = "https://uploads.github.com/repos/$Repo/releases/$($release.id)/assets?name=$($assetFile.Name)"
    Write-Host "== 上传 $($assetFile.Name)（$([math]::Round($assetFile.Length/1KB,1)) KB） =="
    $cfg = @(
        "url = `"$uploadUrl`"",
        "header = `"Authorization: Bearer $token`"",
        "header = `"Accept: application/vnd.github+json`"",
        "header = `"User-Agent: DSH-Agent`"",
        'header = "Content-Type: application/octet-stream"'
    ) -join "`n"
    $raw = ($cfg | & curl.exe -sS --ssl-no-revoke -m 300 --config - -X POST --data-binary "@$($assetFile.FullName)" -w "`n__STATUS__%{http_code}" 2>&1 | Out-String)
    $status = ''
    if ($raw -match '__STATUS__(\d+)') { $status = $Matches[1] }
    if ($status -notin @('200', '201')) { throw "上传插件包失败：HTTP $status`n$raw" }
    $asset = ($raw -replace "`n?__STATUS__\d+\s*$", '') | ConvertFrom-Json
    Write-Host "资产已上传：$($asset.browser_download_url)"
}

Write-Host ''
Write-Host "发布完成：$($release.html_url)" -ForegroundColor Green
