# PolyLingMcpServer を HTTP モードで起動する（Unity Editor 用）
# 終了は Ctrl+C
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'PolyLingMcpServer\bin\Debug\net8.0\PolyLingMcpServer.exe'
if (-not (Test-Path $exe)) { Write-Error "exe が見つかりません: $exe" }

$port = 8771
$serverArgs = @(
    '--project',  'D:\UNITY保存の品\TEO3.0開発中\PolyLing',
    '--editable', 'Packages/com.haglib.polyling;Packages/com.haglib.net_duplexchannel;SandBox;Assets;@settings',
    '--root',     'settings=C:\Users\dhag\AppData\LocalLow\HagiharaLab',
    '--http',     "$port"
)

# サーバの待ち受け開始を待ってからブラウザを開く
Start-Job -ScriptBlock { param($u) Start-Sleep -Seconds 2; Start-Process $u } -ArgumentList "http://127.0.0.1:$port/" | Out-Null

& $exe @serverArgs
