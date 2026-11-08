# Usage: check-icon.ps1 <exe>  -> exit 0 if the exe has an embedded application icon, 1 otherwise.
Add-Type 'using System;using System.Runtime.InteropServices;public class XI{[DllImport("shell32.dll",CharSet=CharSet.Unicode)]public static extern uint ExtractIconEx(string f,int i,IntPtr[] l,IntPtr[] s,uint n);}'
$n = [XI]::ExtractIconEx((Resolve-Path $args[0]).Path,-1,$null,$null,0)
if ($n -ge 1) { "icon OK ($n)"; exit 0 } else { "BUILD FAILED: $($args[0]) has no embedded icon (rule: always embed the logo)"; exit 1 }
