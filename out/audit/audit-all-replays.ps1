# audit-all-replays.ps1 —— 对全部服务端回放跑一次审计，汇总成一张表
#
# 为什么需要它：单局审计只能看出"这一局怎么样"。要判断一个修复
# **整体是变好还是变坏**（尤其是有得有失的改动），必须一次看全部回放。
#
# ⚠️ 一个重要的度量口径：回放里 **bot 自己的动作是旧内核生成的**，
#    用新内核重放自然会被拒 —— **那不是保真度信号**。
#    **只有人类的动作才是 ground truth。** 所以下表把 left/right 分开统计。

[CmdletBinding()]
param(
    [string]$Dir = 'out\_server-replays',
    [string]$Proj = 'tools\ServerBridgeTest'
)

$ErrorActionPreference = 'Stop'

# 向上找仓库根（含 KLink.slnx）。
# ⚠️ 不能用 `Split-Path -Parent $PSScriptRoot` —— 本脚本在 `out/audit/` 下，
#    只上一层是 `out/`，那里没有 KLink.slnx，于是根会退化成脚本自己的目录，
#    相对路径 `out\_server-replays` 就找不到了（我第一版正是这么错的）。
$root = $PSScriptRoot
while ($root -and -not (Test-Path (Join-Path $root 'KLink.slnx'))) {
    $parent = Split-Path -Parent $root
    if ($parent -eq $root) { break }
    $root = $parent
}
if (-not $root) { throw '向上找不到含 KLink.slnx 的仓库根' }
Push-Location $root
try {
    # ⚠️ 不用 `-Filter 'replay-*.actions.json'` —— Windows 的 DOS 通配符
    #    对"两个点"的文件名匹配不可靠（实测返回空）。用 Where-Object 更稳。
    $files = Get-ChildItem $Dir -File -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -like 'replay-*.actions.json' } |
             Sort-Object Name
    if (-not $files) { throw "在 $Dir 里没找到 replay-*.actions.json" }

    $rows = @()
    foreach ($f in $files) {
        $base = $f.FullName -replace '\.actions\.json$', ''
        $mid = ($f.Name -replace '^replay-', '') -replace '\.actions\.json$', ''

        $out = & dotnet run --project $Proj -c Release --no-build -- --audit-replay $base 2>&1
        $text = $out -join "`n"

        # "应用 121/141 条（⚠ 20 条没应用）"
        $applied = $null; $total = $null
        if ($text -match '应用\s+(\d+)/(\d+)\s+条') { $applied = [int]$Matches[1]; $total = [int]$Matches[2] }

        $leftFail  = ([regex]::Matches($text, '（left）：')).Count
        $rightFail = ([regex]::Matches($text, '（right）：')).Count

        # 撞到但没实现的原语种类数
        $unimpl = 0
        if ($text -match '=== ⑥ 撞到但\*\*没实现\*\*的原语：(\d+) 种 ===') { $unimpl = [int]$Matches[1] }

        # 死亡单位仍被移动/攻击
        $deadMoves = 0
        if ($text -match '=== ② 死亡单位仍被移动/攻击：(\d+) 次 ===') { $deadMoves = [int]$Matches[1] }

        $rows += [PSCustomObject]@{
            回放      = $mid
            应用      = if ($applied -ne $null) { "$applied/$total" } else { '?' }
            应用率    = if ($applied -ne $null -and $total -gt 0) { '{0,5:N1}%' -f (100.0 * $applied / $total) } else { '?' }
            '人类失败' = $leftFail
            'bot失败'  = $rightFail
            死亡仍动  = $deadMoves
            未实现种  = $unimpl
        }
    }

    $rows | Format-Table -AutoSize

    $sumA = ($rows | Where-Object { $_.应用 -ne '?' } | ForEach-Object { [int]($_.应用 -split '/')[0] } | Measure-Object -Sum).Sum
    $sumT = ($rows | Where-Object { $_.应用 -ne '?' } | ForEach-Object { [int]($_.应用 -split '/')[1] } | Measure-Object -Sum).Sum
    $sumL = ($rows | Measure-Object '人类失败' -Sum).Sum
    Write-Host ""
    Write-Host ("合计：应用 {0}/{1}（{2:N1}%）；**人类动作失败 {3} 条**" -f `
        $sumA, $sumT, (100.0 * $sumA / [Math]::Max(1, $sumT)), $sumL) -ForegroundColor Cyan
    Write-Host "⚠️ 只有「人类失败」是保真度信号 —— bot 的动作是旧内核生成的，被拒属正常。" -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
