param([ValidateSet("Start","Quit","Status","Click","Shot")][string]$Action="Status",[string]$Save="Grok Test",[int]$X=0,[int]$Y=0,[string]$Name="shot")
$ErrorActionPreference="Stop"
$K=@($env:KSPDir,"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program","C:\Steam\steamapps\common\Kerbal Space Program") | Where-Object { $_ -and (Test-Path "$_\KSP_x64.exe") } | Select-Object -First 1; $D="$env:TEMP\kspauto"; $Log="$K\KSP.log"; $Sum="$D\summary.log"
function Note($m){ $l="$(Get-Date -f 'MM-dd HH:mm:ss') $m"; Add-Content $Sum $l; $l }
if((Test-Path $Sum) -and (Get-Item $Sum).Length -gt 200KB){ Get-Content $Sum -Tail 300 | Set-Content $Sum }
$free=(Get-PSDrive C).Free/1GB; if($free -lt 15){ Note "ABORT low disk: $([int]$free) GB free on C:"; exit 2 }
Add-Type @"
using System;using System.Runtime.InteropServices;
public class KW{[DllImport("user32.dll")]public static extern bool GetClientRect(IntPtr h,out R r);[DllImport("user32.dll")]public static extern bool ClientToScreen(IntPtr h,ref P p);[DllImport("user32.dll")]public static extern bool SetProcessDPIAware();[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(int f,int x,int y,int d,int e);[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
public struct R{public int L,T,Rt,B;} public struct P{public int X,Y;}}
"@
[KW]::SetProcessDPIAware()|Out-Null
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
function Win{ $p=Get-Process KSP_x64 -ErrorAction SilentlyContinue; if(!$p){return $null}; $h=$p.MainWindowHandle; $o=New-Object KW+P;[KW]::ClientToScreen($h,[ref]$o)|Out-Null; $c=New-Object KW+R;[KW]::GetClientRect($h,[ref]$c)|Out-Null; @{h=$h;x=$o.X;y=$o.Y;w=$c.Rt;ht=$c.B} }
function Click($x,$y){ $w=Win; [KW]::SetForegroundWindow($w.h)|Out-Null; Start-Sleep -m 300
  [KW]::SetCursorPos($w.x+$x+3,$w.y+$y+2)|Out-Null; Start-Sleep -m 150; [KW]::SetCursorPos($w.x+$x,$w.y+$y)|Out-Null; Start-Sleep -m 300
  [KW]::mouse_event(2,0,0,0,0); Start-Sleep -m 150; [KW]::mouse_event(4,0,0,0,0) }
function Shot($n){ $w=Win; if(!$w){return}; $b=New-Object System.Drawing.Bitmap $w.w,$w.ht; $g=[System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($w.x,$w.y,0,0,$b.Size)
  $s=New-Object System.Drawing.Bitmap $b,([int]($w.w/2)),([int]($w.ht/2)); $s.Save("$D\$n.jpg",[System.Drawing.Imaging.ImageFormat]::Jpeg); "screenshot: $D\$n.jpg" }
function Px($x,$y){ $w=Win; $b=New-Object System.Drawing.Bitmap 1,1; $g=[System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($w.x+$x,$w.y+$y,0,0,$b.Size); $b.GetPixel(0,0) }
function Near($c,$r,$g,$b){ [math]::Abs($c.R-$r) -lt 20 -and [math]::Abs($c.G-$g) -lt 20 -and [math]::Abs($c.B-$b) -lt 20 }
function KopPopup{ (Near (Px 740 580) 72 75 84) -and (Near (Px 1180 580) 72 75 84) -and (Near (Px 740 632) 52 56 65) -and (Near (Px 1180 632) 52 56 65) }
function WaitScene($to,$since,$secs){ $end=(Get-Date).AddSeconds($secs)
  while((Get-Date) -lt $end){ if(!(Get-Process KSP_x64 -ErrorAction SilentlyContinue)){ return $false }
    if((Test-Path $Log) -and (Get-Item $Log).LastWriteTime -gt $since){ $hit=Select-String -Path $Log -Pattern "Scene Change : From \w+ to $to" | Select-Object -Last 1
      if($hit){ $t=[datetime]::ParseExact(($hit.Line -replace '^\[LOG (\d\d:\d\d:\d\d).*','$1'),'HH:mm:ss',$null); if($t -ge $since.AddSeconds(-2)){ return $true } } }
    Start-Sleep 5 }; return $false }
if($Action -eq "Click"){ Click $X $Y; Start-Sleep 2; Shot $Name; exit 0 }
if($Action -eq "Shot"){ Shot $Name; exit 0 }
Get-ChildItem $D -Filter *.jpg -ErrorAction SilentlyContinue | Remove-Item -Force
if($Action -eq "Status"){ if(Win){ $last=(Select-String -Path $Log -Pattern "Scene Change" | Select-Object -Last 1).Line; Note "running; last scene: $last" } else { Note "KSP not running" }; exit 0 }
if($Action -eq "Quit"){ $p=Get-Process KSP_x64 -ErrorAction SilentlyContinue; if(!$p){ Note "quit: not running"; exit 0 }
  $p.CloseMainWindow()|Out-Null; if(!$p.WaitForExit(90000)){ Note "quit: no clean exit in 90s, forcing"; $p.Kill() } else { Note "quit: clean exit" }; exit 0 }
if($Action -eq "Start"){
  if(Get-Process KSP_x64 -ErrorAction SilentlyContinue){ Note "start: KSP already running"; exit 3 }
  $t0=Get-Date; Note "start: launching KSP, save '$Save'"
  Start-Process "$K\KSP_x64.exe" -WorkingDirectory $K -ArgumentList "-single-instance -popupwindow -screen-fullscreen 0"
  if(!(WaitScene "MAINMENU" $t0 900)){ Note "FAIL: no main menu within 15 min"; Shot "fail_menu"; exit 4 }
  Note "main menu after $([int]((Get-Date)-$t0).TotalSeconds)s"; Start-Sleep 8
  Click 622 514; Start-Sleep 3
  Click 772 378; Start-Sleep 3
  Click 960 188; Start-Sleep 1
  [System.Windows.Forms.SendKeys]::SendWait($Save); Start-Sleep 2
  Click 952 266; Start-Sleep 1
  $t1=Get-Date; Click 1196 926
  if(!(WaitScene "SPACECENTER" $t1 240)){ Note "FAIL: save did not load"; Shot "fail_load"; exit 5 }
  Note "loaded '$Save' after $([int]((Get-Date)-$t1).TotalSeconds)s"; Start-Sleep 15; for($i=0;$i -lt 4;$i++){ if(KopPopup){ Click 960 604; Note "dismissed Kopernicus popup"; Start-Sleep 2 } else { Start-Sleep 3 } }; Shot "loaded"; exit 0 }
