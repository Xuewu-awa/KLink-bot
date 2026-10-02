<#
.SYNOPSIS
    从 BP_OnlineMatch 的反编译产物生成《对局协议参考》。

.DESCRIPTION
    输入：klink bot/decompiled/BP_OnlineMatch.all-functions.json
          （由 UAssetCLI dump-blueprint 从 kards-Windows.pak 里的 BP_OnlineMatch 反编译得到）

    输出：klink bot/docs/对局协议参考.md

    为什么要这么做：KARDS 对局协议的动作名/子动作名/参数键名全部是 Blueprint 里的字符串常量，
    反编译后可以直接统计出来 —— 不需要抓包，也不需要运行时注入。

.EXAMPLE
    pwsh -File "gen-protocol-doc.ps1"
#>
[CmdletBinding()]
param(
    [string]$DecompiledJson = (Join-Path $PSScriptRoot '..\decompiled\BP_OnlineMatch.live.all-functions.json'),
    [string]$OutputMd       = (Join-Path $PSScriptRoot '..\docs\对局协议参考.md')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $DecompiledJson)) {
    throw "找不到反编译产物：$DecompiledJson`n请先运行：UAssetCLI dump-blueprint --asset <BP_OnlineMatch.uasset> --usmap <xxx.jmap> --output <该文件>"
}

Write-Host "读取 $DecompiledJson ..."
$functions = Get-Content $DecompiledJson -Raw | ConvertFrom-Json
$names = $functions.PSObject.Properties.Name
Write-Host "  函数数：$($names.Count)"

# ---- 1) 子动作：ZAction 名 -> 构造它的函数 + 参数键 ----
$subMap   = [ordered]@{}
$subNames = [System.Collections.Generic.HashSet[string]]::new()
$keySet   = [System.Collections.Generic.HashSet[string]]::new()
$ctorCount = @{}

foreach ($prop in $functions.PSObject.Properties) {
    $fnName = $prop.Name
    $flat = $prop.Value | ConvertTo-Json -Depth 40 -Compress

    # 该函数构造了哪些子动作
    $made = [regex]::Matches($flat,
        '"FunctionName":"CreateAction_AddSubAction","Parameters":\[\{"Inst":"StringConst","Value":"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value }

    # 该函数用了哪些 ActionValue 键（键 -> 类型）
    $kv = [ordered]@{}
    foreach ($m in [regex]::Matches($flat,
        '"FunctionName":"(ActionValue[A-Za-z]+)","Parameters":\[\{"Inst":"StringConst","Value":"([^"]+)"')) {
        $ctor = $m.Groups[1].Value -replace '^ActionValue', ''
        $key  = $m.Groups[2].Value
        $kv[$key] = $ctor
        [void]$keySet.Add($key)
        $ctorCount[$ctor] = 1 + ($ctorCount[$ctor] | ForEach-Object { $_ })  # 计数在下面统一重算
    }

    foreach ($n in $made) {
        [void]$subNames.Add($n)
        if (-not $subMap.Contains($n)) { $subMap[$n] = [ordered]@{ builders = @(); keys = [ordered]@{} } }
        $subMap[$n].builders += $fnName
        foreach ($k in $kv.Keys) { $subMap[$n].keys[$k] = $kv[$k] }
    }
}

# 构造器出现次数（全量重算，避免上面的增量写法出错）
$ctorCount = @{}
foreach ($m in [regex]::Matches((Get-Content $DecompiledJson -Raw), '"FunctionName":"(ActionValue[A-Za-z]+)"')) {
    $c = $m.Groups[1].Value
    $ctorCount[$c] = 1 + [int]$ctorCount[$c]
}

# ---- 2) 顶层动作类型 ----
# 这些是 BP_OnlineMatch 自己「定义」的接收函数，所以出现在属性名里而不是被调用名里
$topActions = $names | Where-Object { $_ -match '^ReceiveAction' } | Sort-Object -Unique

# ---- 3) 生成 markdown ----
$md = [System.Collections.Generic.List[string]]::new()
function Add-Line([string]$s = '') { $md.Add($s) }

Add-Line '# KARDS 对局协议参考（从游戏本体反编译提取）'
Add-Line ''
Add-Line '> 来源：`kards-Windows.pak` → `Content/Blueprints/Logic/BP_OnlineMatch.uasset` → Kismet 字节码反序列化。'
Add-Line '> 生成工具：`UAssetCLI dump-blueprint`，由 `tools/gen-protocol-doc.ps1` 汇总。'
Add-Line '> **本文件全部内容不依赖抓包、不依赖运行时注入。**'
Add-Line ''
Add-Line '## 1. 动作信封'
Add-Line ''
Add-Line '```'
Add-Line 'action = {'
Add-Line '  action_id:      int,'
Add-Line '  action_type:    string,        -- "XActionPlayCardFromHand" / "XActionAttackCard" / ...'
Add-Line '  player_id:      int,'
Add-Line '  action_data:    ActionValue2[],  -- 意图参数'
Add-Line '  sub_actions:    SubAction[],     -- 效果（本 build 由各客户端本地计算）'
Add-Line '  turn_number:    int,'
Add-Line '  action:         string,          -- "end-match" / "lvl-loaded"'
Add-Line '  value:          map,'
Add-Line '  send_action_id: int'
Add-Line '}'
Add-Line ''
Add-Line 'ActionValue2 = { Name: FString, Value: int32, Text: FString }   // size 40'
Add-Line 'SubAction    = { Name: FString, Values: ActionValue2[] }        // size 32'
Add-Line '```'
Add-Line ''
Add-Line "## 2. 子动作全集（$($subMap.Count) 个）"
Add-Line ''
Add-Line '`ResolveSubAction` 的分发键是**去掉 `Z` 前缀**的紧凑形式（92 路 switch）。'
Add-Line '`XActionNamesFullToCompact` 与 `feature_compact_action_values_only_enabled` 控制这个压缩。'
Add-Line ''
Add-Line '| # | 全名 | 紧凑名 | 构造于 | 参数键（类型） |'
Add-Line '|---|---|---|---|---|'
$i = 0
foreach ($n in ($subMap.Keys | Sort-Object)) {
    $i++
    $compact  = $n -replace '^Z', ''
    $builders = (($subMap[$n].builders | Select-Object -Unique) -join ', ')
    $kstr     = (($subMap[$n].keys.Keys | ForEach-Object { "$_($($subMap[$n].keys[$_]))" }) -join ', ')
    Add-Line "| $i | ``$n`` | ``$compact`` | $builders | $kstr |"
}
Add-Line ''
Add-Line "## 3. ActionValue2 键名全集（$($keySet.Count) 个）"
Add-Line ''
Add-Line '```'
Add-Line (($keySet | Sort-Object) -join '  ')
Add-Line '```'
Add-Line ''
Add-Line '## 4. 类型化构造器'
Add-Line ''
Add-Line '| 构造器 | 次数 | 含义 |'
Add-Line '|---|---|---|'
$ctorMeaning = @{
    'Bool'          = 'bool 值'
    'BoolArray'     = 'bool 数组'
    'CardID'        = '卡牌 ID（无键名，值即 id）'
    'Int'           = 'int 值'
    'IntArray'      = 'int 数组'
    'Location'      = '位置枚举（ECardLocationEnum）'
    'LocationArray' = '位置数组'
    'Name'          = 'FName'
    'NameArray'     = 'FName 数组'
    'Side'          = '阵营 left/right'
    'String'        = '字符串'
}
foreach ($c in ($ctorCount.Keys | Sort-Object)) {
    Add-Line "| ``ActionValue$c`` | $($ctorCount[$c]) | $($ctorMeaning[$c]) |"
}
Add-Line ''
Add-Line '## 5. 顶层动作接收器（`ReceiveAction*`）'
Add-Line ''
Add-Line "这些是 ``BP_OnlineMatch`` 内部定义的接收函数（$($topActions.Count) 个）。"
Add-Line '`XAction*` 前缀的那几个就是网络上的 `action_type` 取值。'
Add-Line ''
Add-Line '```'
foreach ($a in $topActions) { Add-Line $a }
Add-Line '```'
Add-Line ''
Add-Line '## 6. 出牌结算流程（`ReceiveActionXActionPlayCardFromHand` 原文）'
Add-Line ''
Add-Line '```'
Add-Line '1. AddActionEnd(subActions)'
Add-Line '2. DoSubActions(subActions, receiveFromLocal, out visualActions)'
Add-Line '3. AddActionToVisualQueue("XActionPlayCardFromHand", actionData, visualActions)'
Add-Line '```'
Add-Line ''
Add-Line '`receiveFromLocal` 对应 CDO 字段 `ResolveSubActionsFromLocal`；配合 `server_options.skip_sending_subactions = 1`，'
Add-Line '本 build 下效果由双方客户端各自本地计算（**确定性锁步**）。'
Add-Line ''
Add-Line '## 7. 已知的状态字段（`BP_OnlineMatch_C` CDO，节选）'
Add-Line ''
Add-Line '```'
Add-Line 'AllMatchActions  myMatchActions  actionsQueue  subActions  CurrentSubActionID'
Add-Line 'currentActionName  currentActionValues  CurrentSnapshot  passiveEffects'
Add-Line 'endOfTurnCleanupActions  endOfTurnInstigators  cardMarkers  syncErrorCheckCards'
Add-Line 'mySide  mySideString  Match_ID_AsTwoDigits  isResolvingAction'
Add-Line 'isValidatingActionSent  missingActionFound  actionsCreated'
Add-Line 'ResolveSubActionsFromLocal  XActionNamesFullToCompact  XActionNamesCompactToFull'
Add-Line 'AITimer  AITurnChange  lastAIAction  lastAITurnNumber'
Add-Line '```'
Add-Line ''

# ---- 8) 线上实测校正 ----
# 由 tools/extract-live-actions.py 从 fyserver 控制台日志抽出的**真实客户端动作**。
# 这是唯一能证明前面所有离线推断是否正确的东西。
$livePath = Join-Path $PSScriptRoot '..\docs\live-actions.json'
if (Test-Path $livePath) {
    $live = Get-Content $livePath -Raw | ConvertFrom-Json
    Add-Line '## 8. ⭐ 线上实测校正（真实客户端动作）'
    Add-Line ''
    Add-Line "数据来源：``tools/extract-live-actions.py`` 从 fyserver 控制台日志抽出的 $($live.action_count) 条解密动作，存于 ``docs/live-actions.json``。"
    Add-Line ''
    Add-Line '### 8.1 `action_type` 在网络上走**紧凑名**'
    Add-Line ''
    Add-Line '前几节从蓝图字符串常量推出的名字（`XActionPlayCardFromHand` 等）是**全名**，'
    Add-Line '但真实客户端发的是紧凑形式 —— 对应 CDO 里的 `XActionNamesFullToCompact`'
    Add-Line '与 `server_options.feature_compact_action_values_only_enabled = 1`。'
    Add-Line ''
    Add-Line '| 全名（离线推断） | 线上实际（实测） |'
    Add-Line '|---|---|'
    Add-Line '| `XActionPlayCardFromHand` | **`PC`** |'
    Add-Line '| `XActionMoveCardToLine` | **`ML`** |'
    Add-Line '| `XActionAttackCard` | **`AC`** |'
    Add-Line '| `XActionStartOfTurn` | `XActionStartOfTurn`（不压缩） |'
    Add-Line '| `XActionEndOfTurn` | `XActionEndOfTurn`（不压缩） |'
    Add-Line ''
    Add-Line '⚠️ 只有**主动操作**被压缩，回合边界动作保持全名。'
    Add-Line ''
    Add-Line '### 8.2 `action_data` 用**数字下标**，不是名字'
    Add-Line ''
    Add-Line '```'
    Add-Line '{"action_type":"PC","player_id":900009,"action_id":49,"local_subactions":1,'
    Add-Line ' "action_data":{"0":"64","1":"3","2":"0","3":"0","4":"jJ","84":"11"}}'
    Add-Line ''
    Add-Line '{"action_type":"AC","player_id":900008,"action_id":39,"local_subactions":1,'
    Add-Line ' "action_data":{"0":"7","1":"59","2":"4H","3":"d3","84":"3"}}'
    Add-Line ''
    Add-Line '{"action_type":"ML","player_id":900009,"action_id":50,"local_subactions":1,'
    Add-Line ' "action_data":{"0":"74","1":"1","2":"yD","84":"11"}}'
    Add-Line '```'
    Add-Line ''
    Add-Line '前几节里 `ActionValue2.Name` 在线路上是**下标字符串**，'
    Add-Line '而且**含义随动作类型而变**（每种动作有自己的参数表，压缩后下标自然不同）：'
    Add-Line ''
    Add-Line '| 动作 | `0` | `1` | `2` | `3` | `4` | `84` |'
    Add-Line '|---|---|---|---|---|---|---|'
    Add-Line '| `PC` 出牌 | 卡牌 ID | **部署槽位** | 目标卡 ID | 0 | **卡组码** | HQ 防御 |'
    Add-Line '| `AC` 攻击 | 攻击者 ID | **防御者 ID** | **攻击者码** | **防御者码** | — | HQ 防御 |'
    Add-Line '| `ML` 移动 | 卡牌 ID | **目标槽位** | **卡组码** | — | — | HQ 防御 |'
    Add-Line '| `XActionStartOfTurn` / `XActionEndOfTurn` | — | — | — | — | — | HQ 防御 + `side` |'
    Add-Line ''
    Add-Line '### 8.2.1 ⭐ `84` 是**行动方自己的 HQ 当前防御力**'
    Add-Line ''
    Add-Line '这条是靠 21 回合 / 95 条动作的完整回放确认的（`docs/live-replays/`）：'
    Add-Line ''
    Add-Line '```'
    Add-Line '左 900008 的 84:  20 → 17 → 15 → 12 → 10 → 8 → 5 → 3     终局 3（存活，最终获胜）'
    Add-Line '右 900009 的 84:  20 → 18 → 15 → 11                     T14 后停止发动作，HQ 被摧毁'
    Add-Line '最后一条动作: ActionEndMatch{ reason: Victory_DestroyHQ, winner_side: left }'
    Add-Line '```'
    Add-Line ''
    Add-Line '两个推论：'
    Add-Line '1. **HQ 初始防御 = 20** —— 与 fyserver 注入的 bot 动作 `{"75":"20"}` 相互印证'
    Add-Line '2. **动作流里带着一个关键状态量**，所以「喂动作流进内核、逐帧比对 HQ 防御」'
    Add-Line '   就是一条现成的验证路径 —— 不需要额外的状态导出'
    Add-Line ''
    Add-Line '### 8.2.2 实测值对照'
    Add-Line ''
    Add-Line '```'
    Add-Line 'PC  900009  {0:80, 1:1, 2:0,  3:0, 4:ux, 84:20}   → 把 sd_kfz_10_38 部署到槽位 1'
    Add-Line 'PC  900009  {0:48, 1:0, 2:0,  3:0, 4:4B, 84:20}   → 把 stug_iii 部署到槽位 0（前线）'
    Add-Line 'ML  900009  {0:48, 1:0, 2:4B,            84:20}   → 移动 stug_iii 到槽位 0'
    Add-Line 'AC  900009  {0:48, 1:1, 2:4B, 3:3v,      84:20}   → stug_iii 攻击 cardID 1（敌方 HQ）'
    Add-Line 'AC  900008  {0:36, 1:80, 2:za, 3:ux,     84:20}   → 3_panzergrenadier 攻击 sd_kfz_10_38'
    Add-Line 'PC  900008  {0:32, 1:4, 2:41, 3:0, 4:wF, 84:3 }   → 打出 ace_of_spades，目标 41（敌方 HQ）'
    Add-Line '```'
    Add-Line ''
    Add-Line '⚠️ **一个容易踩的坑**：`0` 的取值（64 / 60 / 32…）**有些恰好是合法的 2 字符码**，'
    Add-Line '拿它去查 `deckCodeIDsTable2` 会得到**看起来合理但完全无关**的卡名（假阳性）。'
    Add-Line '真正引用卡的是 `2`/`3`/`4` 这些槽位。'
    Add-Line ''
    Add-Line '### 8.2.3 数据集'
    Add-Line ''
    Add-Line '`docs/live-replays/` 下是从 fyserver 只读接口导出的 5 局真实对局：'
    Add-Line ''
    Add-Line '| 对局 | 类型 | 回合 | 动作数 | 胜方 |'
    Add-Line '|---|---|---|---|---|'
    Add-Line '| 310284 | classic | 21 | **95** | left |'
    Add-Line '| 955337 | classic | 5 | 20 | left |'
    Add-Line '| 165924 | classic | 4 | 19 | right |'
    Add-Line '| 130691 | classic | 1 | 3 | left |'
    Add-Line '| 563868 | pw（打人机） | 1 | 3 | left |'
    Add-Line ''
    Add-Line '每局都包含**完整开局快照**（双方手牌 4/5 张 + 牌库 34/35 张，带 cardID）'
    Add-Line '与**全部动作**。\`tools/decode-replay.py\` 能把它解码成完全可读的形式。'
    Add-Line ''
    Add-Line '### 8.3 `sub_actions` 确实是空的 —— 锁步模型得到确证'
    Add-Line ''
    Add-Line '日志里每条动作的 `local_subactions` 都是 `1`，服务端侧的 `sub_actions` 是空数组。'
    Add-Line '**效果由双方客户端各自本地计算，不上网络** —— 与第 6 节的推断一致。'
    Add-Line ''
    Add-Line '这也是为什么规则内核必须自己实现全部效果：服务端从头到尾看不到效果数据。'
    Add-Line ''
    Add-Line '### 8.4 还没拿到的'
    Add-Line ''
    Add-Line '- **棋盘状态**：这份日志只有动作，没有局面。逐帧状态对拍还需要客户端侧的状态导出'
    Add-Line '- **完整对局**：目前只覆盖约一个回合（8 条动作），键的含义需要更多样本复核'
    Add-Line '- **子动作的形状**：既然不上网络，只能从客户端内部取（或靠内核自己算）'
}

# ---- 4) 写出 ----
$outDir = Split-Path -Parent $OutputMd
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
Set-Content -Path $OutputMd -Value ($md -join "`n") -Encoding UTF8

Write-Host ""
Write-Host "子动作：$($subMap.Count)"
Write-Host "ActionValue2 键：$($keySet.Count)"
Write-Host "顶层接收器：$($topActions.Count)"
Write-Host "已写出：$OutputMd  ($((Get-Item $OutputMd).Length) 字节)"
