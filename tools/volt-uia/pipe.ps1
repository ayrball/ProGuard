# pipe.ps1 : send one IpcMessage (or a Command) to the engine pipe as the current user and print the reply.
#   pipe.ps1 -Type GetLogs -Payload '{"After":0}'
#   pipe.ps1 -Command GetSettings [-Pid n] [-Argument '...']
param([string]$Type = 'Command', [string]$Payload = '', [string]$Command, [Nullable[int]]$TargetPid, [string]$Argument)
$types = @{ Ping=0; GetStatus=4; SetSetting=10; GetLogs=11; Command=17 }
$m = [ordered]@{ Type = $types[$Type]; Payload = $Payload; Timestamp = (Get-Date).ToUniversalTime().ToString('o') }
if ($Command) { $m.Type = 17; $m.CommandType = $Command; $m.TargetPid = $TargetPid; $m.Argument = $(if ($Argument) { $Argument } else { $null }) }
$msg = $m | ConvertTo-Json -Compress
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ProGuardEngine', [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(3000)
$enc = New-Object System.Text.UTF8Encoding($false)
$w = New-Object System.IO.StreamWriter($pipe, $enc); $w.NewLine = "`n"; $w.AutoFlush = $true
$r = New-Object System.IO.StreamReader($pipe, $enc)
$w.WriteLine($msg)
$reply = $r.ReadLine() | ConvertFrom-Json
$pipe.Dispose()
if ($reply.Type -eq 18) { $res = $reply.Payload | ConvertFrom-Json; "Success=$($res.Success) Error=$($res.Error)"; $res.Data } else { "Type=$($reply.Type)"; $reply.Payload }
