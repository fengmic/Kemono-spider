Add-Type -AssemblyName System.Drawing,UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class W3 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@

$outDir = $args[0]
$p = Get-Process KemonoDownloader -ErrorAction Stop | Select-Object -First 1
$h = $p.MainWindowHandle
[W3]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 700

function Get-Rect {
  $r = New-Object W3+RECT
  [W3]::GetWindowRect($h, [ref]$r) | Out-Null
  return $r
}

function Save-Shot($path) {
  $r = Get-Rect
  $w = $r.R - $r.L
  $hh = $r.B - $r.T
  $bmp = New-Object System.Drawing.Bitmap $w, $hh
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [W3]::PrintWindow($h, $hdc, 2) | Out-Null
  $g.ReleaseHdc($hdc)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Output ("saved " + $path + " (" + $w + "x" + $hh + ")")
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$listCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)
$list = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)

for ($i = 0; $i -lt $items.Count; $i++) {
  if ($i -gt 0) {
    $sel = $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sel.Select()
    Start-Sleep -Milliseconds 900
  }
  Save-Shot (Join-Path $outDir ("ui-" + $i + ".png"))
}
