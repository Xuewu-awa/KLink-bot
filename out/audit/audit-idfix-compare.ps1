# audit-idfix-compare.ps1 —— 身份校正的**前后对比**（四条判据）+ 单卡门控归因
#
# ## 四条判据（按可靠性排序，见任务书）
#   1. ⑤b 首个「人类动作」失败点 —— 最稳（位置是否后移/消失）
#   2. ⑥ 撞到但没实现的原语种数 —— 是否减少
#   3. ④ HQ 对不上的动作条数 —— 是否减少
#   4. ⑤/人类失败总数 —— **只在无随机效果参与时可靠**；本修复正好作用在随机效果上，
#      所以这一列**会变**，不能单独拿它下结论（必须配合 -Only 门控实验）
#
# ## 用法
#   & "out\audit\audit-idfix-compare.ps1"
#   & "out\audit\audit-idfix-compare.ps1" -Only card_event_atlantic_convoy

[CmdletBinding()]
param(
    [string]$Dir = 'out\_server-replays',
    [string]$Proj = 'tools\ServerBridgeTest',
    [string]$Only = ''
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
    $files = Get-ChildItem $Dir -File |
             Where-Object { $_.Name -like 'replay-*.actions.json' } |
             Sort-Object Name

    function Measure-One([string]$base, [bool]$fix) {
        $extra = @()
        # 身份校正**默认关**（RNG 复刻才是主修复）；打开要显式 `--identity-fix`。
        if ($fix)      { $extra += '--identity-fix' }
        if ($Only)     { $extra += @('--identity-only', $Only) }
        $text = (& dotnet run --project $Proj -c Release --no-build -- --audit-replay $base @extra 2>&1) -join "`n"

        $m = [ordered]@{
            应用 = '?'; 人类失败 = 0; HQ不匹配 = 0; 人类HQ不匹配 = 0; 未实现种 = 0
            首个人类失败 = '—'
        }

        if ($text -match '应用 (\d+)/(\d+) 条') { $m.应用 = "$($Matches[1])/$($Matches[2])" }

        # ⑤b 段：**必须在段内判定**。
        # ⚠️ 第一版用 `'=== ⑤b[\s\S]*?\n\s*✅'` —— 那个 ✅ 会一路匹配到文末
        #    「判读」段里的 ✅（"内核杀过单位…"），于是**所有**回放都被误报成"无失败"。
        $i5b = $text.IndexOf('=== ⑤b')
        if ($i5b -ge 0) {
            $blk = $text.Substring($i5b)
            $nxt = $blk.IndexOf("`n=== ", 4)
            if ($nxt -gt 0) { $blk = $blk.Substring(0, $nxt) }
            if ($blk -match '#(\d+) t(\d+) (\S+)：([^\r\n]+)') {
                $m.首个人类失败 = "#$($Matches[1]) t$($Matches[2]) $($Matches[3])：$($Matches[4].Trim())"
            }
            elseif ($blk -match '✅') {
                $m.首个人类失败 = '✅ 无'
            }
        }

        # ④ 段的人类/bot 拆分
        if ($text -match '其中人类 left (\d+) 条、bot right (\d+) 条') {
            $m.人类HQ不匹配 = [int]$Matches[1]
        }

        # ⑤ 段里按 side 数人类失败（left）
        if ($text -match '=== ⑤ 未应用的动作：(\d+) 条') { $m.人类失败 = [int]$Matches[1] }
        # 人类失败数只数 left —— 用 ⑤ 段逐行统计
        $inSec = $false; $left = 0; $right = 0
        foreach ($line in ($text -split "`n")) {
            if ($line -match '=== ⑤ 未应用的动作') { $inSec = $true; continue }
            if ($inSec -and $line -match '^\s*#\d+ t\d+ \S+（left）') { $left++ }
            elseif ($inSec -and $line -match '^\s*#\d+ t\d+ \S+（right）') { $right++ }
            elseif ($inSec -and $line -match '=== ') { $inSec = $false }
        }
        $m.人类失败 = $left
        $m.bot失败 = $right

        if ($text -match '=== ④ HQ 对不上的动作：(\d+) 条') { $m.HQ不匹配 = [int]$Matches[1] }
        if ($text -match '=== ⑥ 撞到但\*\*没实现\*\*的原语：(\d+) 种') { $m.未实现种 = [int]$Matches[1] }

        return $m
    }

    $rows = @()
    foreach ($f in $files) {
        $base = $f.FullName -replace '\.actions\.json$', ''
        $mid = ($f.Name -replace '^replay-', '') -replace '\.actions\.json$', ''
        $before = Measure-One $base $false
        $after = Measure-One $base $true
        $rows += [PSCustomObject]@{
            回放     = $mid
            '前·应用' = $before.应用
            '后·应用' = $after.应用
            '前·人类失败' = $before.人类失败
            '后·人类失败' = $after.人类失败
            '前·④HQ' = $before.HQ不匹配
            '后·④HQ' = $after.HQ不匹配
            '前·④人类' = $before.人类HQ不匹配
            '后·④人类' = $after.人类HQ不匹配
            '前·⑥种' = $before.未实现种
            '后·⑥种' = $after.未实现种
        }
        Write-Host "[$mid] ⑤b 前：$($before.首个人类失败)"
        Write-Host "[$mid] ⑤b 后：$($after.首个人类失败)" -ForegroundColor Cyan
    }

    Write-Host ""
    $rows | Format-Table -AutoSize
    Write-Host ("合计：人类失败 前 {0} → 后 {1}；④HQ(全部) 前 {2} → 后 {3}；④HQ(人类) 前 {4} → 后 {5}；⑥种 前 {6} → 后 {7}" -f `
        ($rows | Measure-Object '前·人类失败' -Sum).Sum, ($rows | Measure-Object '后·人类失败' -Sum).Sum,
        ($rows | Measure-Object '前·④HQ' -Sum).Sum, ($rows | Measure-Object '后·④HQ' -Sum).Sum,
        ($rows | Measure-Object '前·④人类' -Sum).Sum, ($rows | Measure-Object '后·④人类' -Sum).Sum,
        ($rows | Measure-Object '前·⑥种' -Sum).Sum, ($rows | Measure-Object '后·⑥种' -Sum).Sum) -ForegroundColor Cyan
    if ($Only) { Write-Host "（单卡门控：只对 $Only 生效）" -ForegroundColor Yellow }
}
finally {
    Pop-Location
}
