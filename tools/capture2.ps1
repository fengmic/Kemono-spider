Add-Type -AssemblyName System.Drawing,UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class W4 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@
$outDir = $args[0]
$index = [int]$args[1]
$p = Get-Process KemonoDownloader -ErrorAction Stop | Select-Object -First 1
$h = $p.MainWindowHandle
[W4]::SetForegroundWindow($h) | Out-Null
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$listCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)
$list = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
$sel = $items[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$sel.Select()
Start-Sleep -Milliseconds 1500
# 强制重绘：轻微改尺寸再复原
$r = New-Object W4+RECT
[W4]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $hh = $r.B - $r.T
[W4]::MoveWindow($h, $r.L, $r.T, $w + 1, $hh, $true) | Out-Null
Start-Sleep -Milliseconds 300
[W4]::MoveWindow($h, $r.L, $r.T, $w, $hh, $true) | Out-Null
Start-Sleep -Milliseconds 700
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[W4]::PrintWindow($h, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc)
$path = Join-Path $outDir ("ui-" + $index + ".png")
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("saved " + $index + " (" + $w + "x" + $hh + ")")
