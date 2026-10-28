# Barracuda Tray'i kaldırır. VB-CABLE ve .NET başka programlarca da kullanılabildiği için yerinde bırakılır.
$ErrorActionPreference = 'Continue'

$target = Join-Path $env:LOCALAPPDATA 'Programs\BarracudaTray'
$data = Join-Path $env:APPDATA 'BarracudaTray'

# A clean exit hands the default playback device back from VB-CABLE to the headset.
$exe = Join-Path $target 'BarracudaTray.exe'
if (Test-Path $exe) { & $exe --exit | Out-Null }
Get-Process BarracudaTray -ErrorAction SilentlyContinue | Stop-Process -Force

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name BarracudaTray -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'Barracuda Tray' -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Barracuda Tray.lnk') -ErrorAction SilentlyContinue
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue

if ((Read-Host 'Ayarlar ve 7.1 profilleri de silinsin mi? (E/H)') -match '^[EeYy]') {
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Barracuda Tray kaldırıldı. VB-CABLE''ı da kaldırmak isterseniz Ayarlar > Uygulamalar''dan kaldırın.' -ForegroundColor Green
Write-Host 'Ses gelmezse görev çubuğundaki ses simgesinden kulaklığı seçin.'
Read-Host 'Kapatmak için Enter'
