# 逐 epoch 计时采样：每 20 秒记一次 (epoch, 时刻)，用来算「每 epoch 多久」。
# 训练本身不输出时间戳（保持与第四轮日志格式一致），所以从外部测。
param(
  [string]$Log = "out\_r5-train-win-100k-ep150.txt",
  [string]$Out = "out\_r5-timing-100k.csv",
  [int]$Minutes = 300
)
$deadline = (Get-Date).AddMinutes($Minutes)
"epoch,time" | Out-File -Encoding utf8 $Out
$seen = @{}
while ((Get-Date) -lt $deadline) {
  if (Test-Path $Log) {
    foreach ($line in (Get-Content $Log -Encoding UTF8)) {
      if ($line -match '^\s+epoch\s+(\d+)\s+') {
        $e = [int]$Matches[1]
        if (-not $seen.ContainsKey($e)) {
          $seen[$e] = (Get-Date).ToString('HH:mm:ss.fff')
          "$e,$($seen[$e])" | Out-File -Append -Encoding utf8 $Out
        }
      }
    }
  }
  Start-Sleep -Seconds 20
}
"done: 记录 $($seen.Count) 个 epoch 的时刻 → $Out"
