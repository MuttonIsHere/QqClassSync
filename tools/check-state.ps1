# check-state.ps1 —— 测试 QQ 调课同步插件时的「现场检查器」
#
# 用法：
#   .\check-state.ps1              # 打一份快照：进程 / 插件 / 设置 / 最近日志 / 临时层状态
#   .\check-state.ps1 -Follow      # 实时跟踪日志里和调课相关的行（Ctrl+C 退出）
#
# 说明：ClassIsland 安装目录默认取本机路径，可用 -ClassIslandRoot 覆盖。

[CmdletBinding()]
param(
    [string] $ClassIslandRoot = 'C:\Users\36896\Desktop\old\ClassIsland_windows_x64_selfContained_folder_2.0.0.2_2',
    [switch] $Follow
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not (Test-Path -LiteralPath $ClassIslandRoot)) {
    Write-Host "找不到 ClassIsland 安装目录：$ClassIslandRoot" -ForegroundColor Red
    exit 1
}

$logDir = Join-Path $ClassIslandRoot 'data\Logs'

if ($Follow) {
    $log = Get-ChildItem $logDir -Filter '*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
    Write-Host "跟踪 $($log.Name)（Ctrl+C 退出）" -ForegroundColor Cyan
    Get-Content -LiteralPath $log.FullName -Wait -Tail 20 |
        Select-String -Pattern 'QqClassSync|调课|临时层|保存档案' |
        ForEach-Object { "{0:HH:mm:ss} {1}" -f (Get-Date), $_.Line }
    exit 0
}

function Write-Section($text) {
    Write-Host ''
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

Write-Section 'ClassIsland 进程'
$proc = Get-Process -Name ClassIsland.Desktop -ErrorAction SilentlyContinue
if ($proc) { $proc | Select-Object Id, StartTime | Format-Table -AutoSize }
else { Write-Host '未运行（启动 ClassIsland.exe 后插件才会开始监听）' -ForegroundColor Yellow }

Write-Section '插件文件'
$pluginDir = Join-Path $ClassIslandRoot 'data\Plugins\QqClassSync'
if (Test-Path $pluginDir) {
    Get-ChildItem $pluginDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
} else {
    Write-Host '插件未部署' -ForegroundColor Red
}

Write-Section '插件设置'
$cfg = Join-Path $ClassIslandRoot 'data\Config\Plugins\qq.classsync\Settings.json'
if (Test-Path $cfg) {
    [System.IO.File]::ReadAllText($cfg, [System.Text.Encoding]::UTF8)
} else {
    Write-Host '设置文件不存在（首次启动插件后自动生成）' -ForegroundColor Yellow
}

Write-Section '最近 15 条插件日志'
$log = Get-ChildItem $logDir -Filter '*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) {
    Write-Host "日志：$($log.Name)（最后写入 $($log.LastWriteTime)）"
    Select-String -Path $log.FullName -Pattern 'QqClassSync|调课' |
        Select-Object -Last 15 |
        ForEach-Object { '  ' + $_.Line }
} else {
    Write-Host '还没有日志' -ForegroundColor Yellow
}

Write-Section '今天是否已有临时层课表'
$profilesDir = Join-Path $ClassIslandRoot 'data\Profiles'
$today = (Get-Date).ToString('yyyy-MM-dd')
Get-ChildItem $profilesDir -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $json = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $overlayId = $json.OverlayClassPlanId
        $hit = $json.OrderedSchedules.PSObject.Properties | Where-Object { $_.Name -like "$today*" }
        if ($overlayId -or $hit) {
            Write-Host "  $($_.Name)：OverlayClassPlanId=$overlayId，今天有排课=$([bool]$hit)"
            if ($hit) {
                $planId = $hit.Value.ClassPlanId
                $plan = $json.ClassPlans.$planId
                if ($plan) {
                    Write-Host "    今天课表：$($plan.Name)（IsOverlay=$($plan.IsOverlay)）"
                }
            }
        }
    } catch {
        Write-Host "  $($_.Name)：解析失败 $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host '提示：-Follow 可实时跟踪日志；设置改动会自动生效，无需重启。' -ForegroundColor DarkGray
exit 0
