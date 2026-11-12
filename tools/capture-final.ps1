Add-Type -AssemblyName System.Drawing,UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class W8 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@
$outDir = $args[0]
$p = Get-Process KemonoDownloader -ErrorAction Stop | Select-Object -First 1
$h = $p.MainWindowHandle
[W8]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 600

$r0 = New-Object W8+RECT
[W8]::GetWindowRect($h, [ref]$r0) | Out-Null
$origW = $r0.R - $r0.L
$origH = $r0.B - $r0.T

[W8]::MoveWindow($h, 120, 40, 1400, 900, $true) | Out-Null
Start-Sleep -Milliseconds 1200

$r = New-Object W8+RECT
[W8]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L
$hh = $r.B - $r.T
Write-Output ("capture size " + $w + "x" + $hh)

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$listCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)
$list = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)

$order = @(4, 3, 2, 0)

foreach ($i in $order) {
  $sel = $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $sel.Select()
  Start-Sleep -Milliseconds 1500
  $bmp = New-Object System.Drawing.Bitmap $w, $hh
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $hh))
  $path = Join-Path $outDir ("page-" + $i + ".png")
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Output ("tab " + $i + " saved")
}

[W8]::MoveWindow($h, $r0.L, $r0.T, $origW, $origH, $true) | Out-Null
Write-Output "restored window size"
