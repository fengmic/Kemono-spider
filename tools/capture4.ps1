Add-Type -AssemblyName System.Drawing,UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class W6 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
}
'@
$outDir = $args[0]
$p = Get-Process KemonoDownloader -ErrorAction Stop | Select-Object -First 1
$h = $p.MainWindowHandle
if ([W6]::IsIconic($h)) { [W6]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Milliseconds 500 }
[W6]::ShowWindow($h, 5) | Out-Null
[W6]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 900

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$listCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)
$list = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)

for ($i = $items.Count - 1; $i -ge 0; $i--) {
  $sel = $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $sel.Select()
  Start-Sleep -Milliseconds 1300
  $rect = $win.Current.BoundingRectangle
  $w = [int]$rect.Width; $hh = [int]$rect.Height
  if ($w -lt 100) { Write-Output ("tab " + $i + " bad rect " + $w + "x" + $hh); continue }
  $bmp = New-Object System.Drawing.Bitmap $w, $hh
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, (New-Object System.Drawing.Size $w, $hh))
  $path = Join-Path $outDir ("tab-" + $i + ".png")
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Output ("tab " + $i + " -> " + $w + "x" + $hh)
}
