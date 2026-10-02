# P0 每族回归脚本（用法：pwsh -File out\audit\p0-regress.ps1 -Tag fam1）
param([string]$Tag = "run")
$ErrorActionPreference = "Continue"
Set-Location "<repo-root>"
$log = "out\_p0-$Tag.log"
"=== $(Get-Date -Format o) $Tag START ===" | Out-File $log -Encoding utf8

& {
  "--- 1) dotnet build KLink.slnx -c Release ---"
  $b1 = dotnet build KLink.slnx -c Release 2>&1
  $b1 | Select-String -Pattern '个警告|个错误|error [A-Z]|已成功生成|生成失败' | ForEach-Object { $_.Line }
  "KLink.Bot 自身的警告/错误："
  ($b1 | Select-String -Pattern 'KLink\.Bot.*(warning|error)').Count

  "--- 2) dotnet build tools\BotSim ---"
  dotnet build tools\BotSim\BotSim.csproj -c Release 2>&1 | Select-String -Pattern '个警告|个错误|已成功生成|生成失败' | ForEach-Object { $_.Line }

  "--- 3) dotnet build tools\BoardCompare ---"
  dotnet build tools\BoardCompare\BoardCompare.csproj -c Release 2>&1 | Select-String -Pattern '个警告|个错误|已成功生成|生成失败' | ForEach-Object { $_.Line }

  "--- 4) 哨兵：三处 KLink.Bot.dll 的哈希必须一致（证明跑的是新 dll） ---"
  Get-FileHash 'src\KLink.Bot\bin\Release\net10.0\KLink.Bot.dll',
               'tools\BotSim\bin\Release\net10.0\KLink.Bot.dll',
               'tools\BoardCompare\bin\Release\net10.0\KLink.Bot.dll' -ErrorAction SilentlyContinue |
    ForEach-Object { "$($_.Hash.Substring(0,16))  $($_.Path)" }

  "--- 5) selftest（基线 24/24） ---"
  dotnet run --project tools\BotSim -c Release --no-build -- selftest 2>&1 | Select-Object -Last 3

  "--- 6) BoardCompare（基线 攻击力98.6% / 区域53.5% / 战线92.7% / 动作应用82.2% / HQ24.6%） ---"
  dotnet run --project tools\BoardCompare -c Release --no-build -- `
    --cap "out\_handoff\klink-bot\klink bot\docs\capture" --rep "klink bot\docs\fresh-replays" 2>&1 |
    Select-String -Pattern '可对拍快照|卡名 |攻击力|防御力|区域 |战线|槽位|Blitz|HQ 防御|动作应用' | ForEach-Object { $_.Line }

  "--- 7) play --games 20 --seed 12345（基线 左胜8 / 右胜12 / 平均67.7回合） ---"
  dotnet run --project tools\BotSim -c Release --no-build -- play --games 20 --seed 12345 2>&1 |
    Select-String -Pattern '完成:|左胜|平均回合|蓝图程序执行|Kismet 步数|程序异常|未实现的调用|pop-unresolved' | ForEach-Object { $_.Line }
} *>&1 | Tee-Object -FilePath $log -Append | Out-Null

"=== $(Get-Date -Format o) $Tag DONE ===" | Out-File $log -Append -Encoding utf8
