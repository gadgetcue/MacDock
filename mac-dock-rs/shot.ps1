# usage: shot.ps1 <name> [trigger] [waitms] [mx my] -> launches TEST copy (--test --lift), optional trigger/hover, screenshot incl. layered windows, kills it
param($name="t", $trig="", [int]$wait=2500, [int]$mx=-1, [int]$my=-1, [int]$post=1800)
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type @'
using System;using System.Runtime.InteropServices;
public class MS{
[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);
[DllImport("user32.dll")]public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")]public static extern void mouse_event(int f,int dx,int dy,int d,int e);
[DllImport("user32.dll")]public static extern IntPtr GetDC(IntPtr h);
[DllImport("user32.dll")]public static extern int ReleaseDC(IntPtr h,IntPtr dc);
[DllImport("user32.dll")]public static extern int GetSystemMetrics(int i);
[DllImport("gdi32.dll")]public static extern bool BitBlt(IntPtr d,int x,int y,int w,int h,IntPtr s,int sx,int sy,int rop);
}
'@
[MS]::SetProcessDPIAware()|Out-Null
$d=$PSScriptRoot
Get-Process MacDock-rs -ea 0 | Stop-Process -Force
$p = Start-Process "$d\MacDock-rs.exe" -ArgumentList '--test','--lift','170' -PassThru
Start-Sleep -m $wait
if ($trig) { Set-Content "$d\scratch\click.trigger" $trig; Start-Sleep -m $post }
if ($mx -ge 0) { $sw=[MS]::GetSystemMetrics(0); $sh=[MS]::GetSystemMetrics(1); foreach($k in 0..6){ $x=$mx-30+$k*5; [MS]::mouse_event(0x8001,[int]($x*65535/$sw),[int]($my*65535/$sh),0,0); Start-Sleep -m 60 }; Start-Sleep -m 900 }
$w=[MS]::GetSystemMetrics(0); $h=[MS]::GetSystemMetrics(1)
$bm=New-Object Drawing.Bitmap $w,$h
$g=[Drawing.Graphics]::FromImage($bm); $dc=$g.GetHdc(); $sdc=[MS]::GetDC([IntPtr]::Zero)
[MS]::BitBlt($dc,0,0,$w,$h,$sdc,0,0,0x40CC0020)|Out-Null
[MS]::ReleaseDC([IntPtr]::Zero,$sdc)|Out-Null; $g.ReleaseHdc($dc); $g.Dispose()
$bm.Save("$d\shots\$name.png"); $bm.Dispose()
$pr = Get-Process MacDock-rs -ea 0; if ($pr) { "{0} WS={1:N1}MB CPU={2:N1}s" -f $pr.Id,($pr.WorkingSet64/1MB),$pr.CPU }
if ($name -ne 'keep') { Stop-Process $p -Force -ea 0 }
