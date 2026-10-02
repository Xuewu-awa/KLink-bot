# audit-4metrics.ps1 —— 对全部回放跑审计，按**四条判据**汇总（2026-10-02 建）
#
# 为什么需要它：`audit-all-replays.ps1` 只报「人类失败」和「未实现种」。
# 而修 bug 时真正可靠的判据有**四条**，其中前两条比"人类失败数"稳得多：
#   1. 首个漂开点（⑤b）是否后移/消失     ← 最稳
#   2. 未实现原语（⑥）是否减少
#   3. HQ 对不上（④ 的【人类】那一栏）是否减少
#   4. 人类失败总数                       ← 只在无随机效果参与时可靠
# 这个脚本把四条一起打出来，省得每次人工从长文本里抠。
#
# ⚠️ ④ 的「全部」那一栏会因 bot 侧旧动作引用旧号段而变差，那是**预期副作用**，
#    所以这里只取【人类 left】那一栏。
[CmdletBinding()]
param(
    [string]$Dir = 'out\_server-replays',
    [string]$Proj = 'tools\ServerBridgeTest'
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
while ($root -and -not (Test-Path (Join-Path $root 'KLink.slnx'))) {
    $parent = Split-Path -Parent $root
    if ($parent -eq $root) { break }
    $root = $parent
}
if (-not $root) { throw '向上找不到含 KLink.slnx 的仓库根' }
Push-Location $root
try {
    $files = Get-ChildItem $Dir -File -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -like 'replay-*.actions.json' } |
             Sort-Object Name
    if (-not $files) { throw "在 $Dir 里没找到 replay-*.actions.json" }

    $rows = @()
    foreach ($f in $files) {
        $base = $f.FullName -replace '\.actions\.json$', ''
        $mid = ($f.Name -replace '^replay-', '') -replace '\.actions\.json$', ''

        $text = (& dotnet run --project $Proj -c Release --no-build -- --audit-replay $base 2>&1) -join "`n"

        $applied = '?'
        if ($text -match '应用\s+(\d+)/(\d+)\s+条') { $applied = "$($Matches[1])/$($Matches[2])" }

        # ⑤ 未应用（含 bot 侧，仅作参考）
        $unapplied = 0
        if ($text -match '=== ⑤ 未应用的动作：(\d+) 条 ===') { $unapplied = [int]$Matches[1] }

        # ⑤ 人类失败（= 保真度信号）
        $humanFail = 0
        if ($text -match '=== ⑤ 未应用的动作：\d+ 条 ===') {
            $sec5 = $text.Substring($Matches[0].Length)
            $humanFail = ([regex]::Matches($sec5, '（left）：')).Count
        }

        # ④ HQ 对不上 —— 只取【人类 left】那一栏
        $hqHuman = -1
        if ($text -match '其中人类 left (\d+) 条') { $hqHuman = [int]$Matches[1] }

        # ⑤b 首个人类失败点
        $firstHuman = '—'
        if ($text -match '✅ \*\*没有人类动作失败\*\*') { $firstHuman = '无（完全对齐）' }
        elseif ($text -match '=== ⑤b [^\r\n]*===\r?\n\s*#(\d+) t(\d+) (\S+)：([^\r\n]*)') {
            $firstHuman = "#$($Matches[1]) t$($Matches[2]) $($Matches[3])"
        }

        # ⑥ 未实现原语种类
        $unimpl = -1
        if ($text -match '=== ⑥ 撞到但\*\*没实现\*\*的原语：(\d+) 种 ===') { $unimpl = [int]$Matches[1] }

        # ⑦ RNG 游标（客户端 cardsRandomStream 的消费数；漏/多消费点会变）
        $rng = -1
        if ($text -match 'RNG 游标：本局共消耗\s+(\d+)\s+个随机数') { $rng = [int]$Matches[1] }

        $rows += [PSCustomObject]@{
            回放        = $mid
            应用        = $applied
            人类失败    = $humanFail
            '④HQ差(人)' = $hqHuman
            '⑤b首漂开'  = $firstHuman
            '⑥未实现种' = $unimpl
            RNG         = $rng
            未应用总    = $unapplied
        }
    }

    $rows | Format-Table -AutoSize

    $sumA = ($rows | Where-Object { $_.应用 -ne '?' } | ForEach-Object { [int]($_.应用 -split '/')[0] } | Measure-Object -Sum).Sum
    $sumT = ($rows | Where-Object { $_.应用 -ne '?' } | ForEach-Object { [int]($_.应用 -split '/')[1] } | Measure-Object -Sum).Sum
    $sumL = ($rows | Measure-Object '人类失败' -Sum).Sum
    $sumH = ($rows | Where-Object { $_.'④HQ差(人)' -ge 0 } | Measure-Object '④HQ差(人)' -Sum).Sum
    Write-Host ""
    Write-Host ("合计：应用 {0}/{1}（{2:N1}%）；人类失败 {3} 条；④ 人类 HQ 差 {4} 条" -f `
        $sumA, $sumT, (100.0 * $sumA / [Math]::Max(1, $sumT)), $sumL, $sumH) -ForegroundColor Cyan
    Write-Host "判据可靠性：⑤b 首漂开 > ⑥ 未实现种 > ④ 人类HQ差 > 人类失败总数" -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
