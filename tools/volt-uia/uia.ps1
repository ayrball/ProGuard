# UI Automation helpers for driving ProGuard.UI without mouse or keyboard input. See README.md.
# Dot-source on the desktop the UI runs on: . .\uia.ps1
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
if (-not ("PgNative2" -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing @"
using System; using System.Runtime.InteropServices; using System.Drawing;
public static class PgNative2 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static Bitmap Capture(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    var bmp = new Bitmap(r.R - r.L, r.B - r.T);
    using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc); }
    return bmp;
  }
}
"@
[PgNative2]::SetProcessDPIAware() | Out-Null
}
$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]
$script:Work = if ($env:PROGUARD_UIA_WORK) { $env:PROGUARD_UIA_WORK } else { Join-Path $PSScriptRoot 'work' }
$script:Shots = Join-Path $script:Work 'shots'
New-Item -ItemType Directory -Force $script:Shots | Out-Null

function Get-PgWindow([int]$ProcessId = 0) {
  $pidFile = Join-Path $script:Work 'ui.pid'; if (-not $ProcessId -and (Test-Path $pidFile)) { $ProcessId = [int](Get-Content $pidFile) }; $p = Get-Process -Id $ProcessId
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
  $AE::RootElement.FindAll($TS::Children, $cond) | Where-Object { $_.Current.Name -eq 'ProGuard' } | Select-Object -First 1
}
function Get-All($root) { $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }
function Find-Name($root, [string]$name, [string]$type) {
  Get-All $root | Where-Object { $_.Current.Name -eq $name -and (-not $type -or $_.Current.ControlType.ProgrammaticName -eq "ControlType.$type") } | Select-Object -First 1
}
function Find-Like($root, [string]$pattern, [string]$type) {
  Get-All $root | Where-Object { $_.Current.Name -like $pattern -and (-not $type -or $_.Current.ControlType.ProgrammaticName -eq "ControlType.$type") }
}
function Dump($root, [string]$file) {
  $lines = foreach ($e in (Get-All $root)) {
    $c = $e.Current; $r = $c.BoundingRectangle
    "{0,-14} {1,-30} name='{2}' cls={3} rect={4:0},{5:0},{6:0}x{7:0} off={8} en={9}" -f $c.ControlType.ProgrammaticName.Replace('ControlType.',''), $c.AutomationId, ($c.Name -replace "`n",'\n'), $c.ClassName, $r.X, $r.Y, $r.Width, $r.Height, $c.IsOffscreen, $c.IsEnabled
  }
  if ($file) { $lines | Set-Content -Encoding utf8 $file } else { $lines }
}
function Texts($root) { Get-All $root | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' -and -not $_.Current.IsOffscreen } | ForEach-Object { $_.Current.Name } }
function Invoke-El($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-El($e) { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Toggle-El($e) { $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
function ToggleState($e) { $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
function Set-Val($e, [string]$v) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function Get-Val($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Expand-El($e) { $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() }
function Patterns($e) { $e.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName } }
function Go-Page($win, [string]$title) {
  $item = Get-All $win | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and ($_.Current.Name -eq $title -or $_.Current.Name -like "*Title = $title,*") } | Select-Object -First 1
  if (-not $item) { throw "page $title not found" }
  Select-El $item; Start-Sleep -Milliseconds 700
}
function Shot($win, [string]$name) {
  $h = [IntPtr]$win.Current.NativeWindowHandle
  $bmp = [PgNative2]::Capture($h); $path = Join-Path $script:Shots "$name.png"; $bmp.Save($path); $bmp.Dispose(); $path
}
function Center($e) { $r = $e.Current.BoundingRectangle; @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
function Toasts($win) { Get-All $win | Where-Object { $_.Current.Name -like 'Toast {*' } | ForEach-Object { $_.Current.Name } }
function Buttons($root, [string]$name) {
  Get-All $root | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Current.Name -eq $name -and -not [double]::IsInfinity($_.Current.BoundingRectangle.Y) } |
    Sort-Object { $_.Current.BoundingRectangle.Y }, { $_.Current.BoundingRectangle.X }
}
function Press($root, [string]$name, [int]$index = 0) {
  $b = @(Buttons $root $name)
  if ($b.Count -le $index) { throw "button '$name' #$index not found ($($b.Count))" }
  Invoke-El $b[$index]
}
# Wait for a toast and return all toasts seen within $sec seconds.
function Wait-Toast($win, [double]$sec = 4) {
  $seen = [ordered]@{}; $end = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $end) { foreach ($t in (Toasts $win)) { $seen[$t] = 1 }; if ($seen.Count) { Start-Sleep -Milliseconds 400; foreach ($t in (Toasts $win)) { $seen[$t] = 1 }; break }; Start-Sleep -Milliseconds 150 }
  $seen.Keys
}
function Clear-Toasts { Start-Sleep -Milliseconds 7200 }
function Probe([int]$procId) { & (Join-Path $script:Work 'bin\probe.exe') $procId }
function Select-Proc($win, [string]$query, [int]$procId = 0) {
  $search = Get-All $win | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Edit' } | Select-Object -First 1
  Set-Val $search $query; Start-Sleep 1.5
  $rows = @(Get-All $win | Where-Object { $_.Current.ClassName -eq 'ListBoxItem' -and $_.Current.Name -like '*ProcessRow*' })
  $row = if ($procId) { $rows | Where-Object { (Get-All $_ | ForEach-Object { $_.Current.Name }) -contains "$procId" } | Select-Object -First 1 } else { $rows[0] }
  if (-not $row) { throw "row for $query/$procId not found ($($rows.Count) rows)" }
  Select-El $row; Start-Sleep 1; $row
}
function CpuOver([int]$procId, [double]$sec = 5) {
  $p = Get-Process -Id $procId; $a = $p.TotalProcessorTime.TotalSeconds; Start-Sleep $sec; $p.Refresh(); $b = $p.TotalProcessorTime.TotalSeconds
  [math]::Round(($b - $a) / $sec / [Environment]::ProcessorCount * 100, 1)
}
function ByName($root, [string]$name, [string]$type) {
  Get-All $root | Where-Object { $_.Current.Name -eq $name -and (-not $type -or $_.Current.ControlType.ProgrammaticName -eq "ControlType.$type") } | Select-Object -First 1
}
function ById($root, [string]$id) { Get-All $root | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1 }
function Choose($root, [string]$list, [string]$label) {
  $l = ByName $root $list 'List'; if (-not $l) { throw "list '$list' not found" }
  $i = Get-All $l | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and $_.Current.Name -eq $label } | Select-Object -First 1
  if (-not $i) { throw "choice '$label' not in '$list'" }
  Select-El $i
}
function Chosen($root, [string]$list) {
  $l = ByName $root $list 'List'
  Get-All $l | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } | ForEach-Object { $_.Current.Name }
}
function Type-In($root, [string]$name, [string]$value) { $e = ByName $root $name 'Edit'; if (-not $e) { throw "edit '$name' not found" }; Set-Val $e $value }
function Switch-To($root, [string]$name, [bool]$on) {
  $c = ByName $root $name 'CheckBox'
  if (-not $c) { $c = Get-All $root | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.CheckBox' -and ((Get-All $_ | ForEach-Object { $_.Current.Name }) -contains $name) } | Select-Object -First 1 }
  if (-not $c) { throw "switch '$name' not found" }
  if (((ToggleState $c) -eq 'On') -ne $on) { Toggle-El $c }
}
function Open-Section($root, [string]$name) { $x = Get-All $root | Where-Object { $_.Current.Name -eq $name -and $_.Current.ClassName -eq 'Expander' } | Select-Object -First 1; Expand-El $x; Start-Sleep -Milliseconds 300 }
function Click-Id($root, [string]$id) { $e = ById $root $id; if (-not $e) { throw "id '$id' not found" }; Invoke-El $e }
function Click-Name($root, [string]$name) { $e = ByName $root $name 'Button'; if (-not $e) { throw "button '$name' not found" }; Invoke-El $e }
