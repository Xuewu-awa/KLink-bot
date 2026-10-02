# audit-identity-mismatch.ps1 —— 量化「效果随机/复制出来的卡，内核选中的与客户端不一致」这一类 bug
#
# ## 判据（为什么是**卡组码**而不是卡名）
#
# 每条 `PC`/`ML`/`AC` 动作的 `action_data` 都带**被引用卡的卡组码**
# （`PC`→槽4、`ML`→槽2、`AC` 攻方→槽2 / 守方→槽3；见 `WireAction.CardCodes`）。
# 客户端在后续动作里引用某张卡时，那个码就是**客户端认为那张卡是什么**的权威声明。
#
# 内核把随机/复制效果抽到的那张卡建成什么，由它自己的 RNG 决定
# （`ReplayRunner` 的 `seed:(ulong)replay.MatchId`），与客户端无关 —— 所以
# **「动作自带的码 ≠ 内核里那张卡的码」就是这一类 bug 的充要证据**。
#
# ⚠️ 不能用卡名比：审计里那些「当前 HandLeft」之类的假象有一部分就是**卡号撞车**
#    造成的（`desert_dust 7002/7003`、`atlantic_convoy 9002`）。
#
# ## 用法
#   & "out\audit\audit-identity-mismatch.ps1"                 # 默认（身份校正关，RNG 复刻生效）
#   & "out\audit\audit-identity-mismatch.ps1" -Fix on         # 额外打开「身份校正」兜底
#   & "out\audit\audit-identity-mismatch.ps1" -Only card_event_atlantic_convoy   # 单卡门控
#
# ⚠️ 2026-10-02 方向变更后，「主修复」不再是身份校正，而是**让内核复刻客户端的
#    `cardsRandomStream`**（见 `src/KLink.Bot/Engine/UeRandomStream.cs`）。
#    所以默认这一列是「修复后」的数；`-Fix on` 才是额外叠加兜底。

[CmdletBinding()]
param(
    [string]$Dir = 'out\_server-replays',
    [string]$Proj = 'tools\ServerBridgeTest',
    [ValidateSet('off', 'on')]
    [string]$Fix = 'off',
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
    if (-not $files) { throw "在 $Dir 里没找到 replay-*.actions.json" }

    $extra = @()
    # 身份校正**默认关**（主修复是 RNG 复刻）；`-Fix on` 才打开。
    if ($Fix -eq 'on') { $extra += '--identity-fix' }
    if ($Only)         { $extra += @('--identity-only', $Only) }

    $rows = @()
    $detail = @()
    foreach ($f in $files) {
        $base = $f.FullName -replace '\.actions\.json$', ''
        $mid = ($f.Name -replace '^replay-', '') -replace '\.actions\.json$', ''

        $text = (& dotnet run --project $Proj -c Release --no-build -- --audit-replay $base @extra 2>&1) -join "`n"

        # 机器可读行：⑤c 汇总：条数=N 张数=M 已校正=K 内核码未知=U
        $n = 0; $cards = 0; $fixed = 0; $unk = 0
        if ($text -match '⑤c 汇总：条数=(\d+) 张数=(\d+) 已校正=(\d+) 内核码未知=(\d+)') {
            $n = [int]$Matches[1]; $cards = [int]$Matches[2]
            $fixed = [int]$Matches[3]; $unk = [int]$Matches[4]
        }

        $rows += [PSCustomObject]@{
            回放     = $mid
            不一致条 = $n
            涉及卡数 = $cards
            已校正   = $fixed
            内核码未知 = $unk
        }

        # 逐条明细：只取 ⑤c 段里形如 "    #44 t11 ML cardID=..." 的行
        # ⚠️ 不能用 `^\s*#` —— 后面 ③ 段的 "#26 card_unit_..." 也长这样（第一版串了 7 行）。
        $inSec = $false
        foreach ($line in ($text -split "`n")) {
            if ($line -match '=== ⑤c') { $inSec = $true; continue }
            if ($inSec -and $line -match '^\s*#\d+ t\d+ (PC|AC|ML|CS|HT) ') {
                $detail += "[$mid] " + $line.Trim()
            }
            elseif ($inSec -and $line -match '⑤c 汇总') { $inSec = $false }
        }
    }

    $rows | Format-Table -AutoSize

    $sumN = ($rows | Measure-Object 不一致条 -Sum).Sum
    $sumF = ($rows | Measure-Object 已校正 -Sum).Sum
    Write-Host ""
    Write-Host ("合计：**身份不一致 {0} 条动作**；本次校正 {1} 条（Fix={2}{3}）" -f `
        $sumN, $sumF, $Fix, $(if ($Only) { ", Only=$Only" } else { '' })) -ForegroundColor Cyan
    Write-Host ""

    if ($detail.Count -gt 0) {
        Write-Host "---- 逐条明细 ----" -ForegroundColor DarkGray
        $detail | ForEach-Object { Write-Host $_ }
        Write-Host ""

        # 按「客户端说的那张卡」聚合 —— 这才是"涉及哪些卡"
        Write-Host "---- 按客户端声明的卡名聚合（内核选错的那些）----" -ForegroundColor DarkGray
        $byCard = @{}
        foreach ($d in $detail) {
            if ($d -match '动作码 \S+ = (\S+)') {
                $k = $Matches[1]
                if (-not $byCard.ContainsKey($k)) { $byCard[$k] = 0 }
                $byCard[$k]++
            }
        }
        $byCard.GetEnumerator() | Sort-Object Value -Descending |
            ForEach-Object { Write-Host ("  {0,4}  {1}" -f $_.Value, $_.Key) }
    }
}
finally {
    Pop-Location
}
