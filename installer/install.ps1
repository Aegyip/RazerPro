# Barracuda Tray kurulumu: uygulamayı bu kullanıcı için kurar ve gereken her şeyi resmi
# kaynaklarından indirir. Kur.cmd üzerinden çalıştırın; yönetici iznini bir kez ister
# (.NET çalışma zamanı ve VB-CABLE sürücüsü için).
param([switch]$Elevated)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is many times slower with the progress bar

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated"
    exit
}

$DotNetRuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'
$VbCableUrl = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip'
$HeSuViUrl = 'https://downloads.sourceforge.net/project/hesuvi/HeSuVi_2.0.0.1.exe'

$source = Join-Path $PSScriptRoot 'app'
$target = Join-Path $env:LOCALAPPDATA 'Programs\BarracudaTray'
$exe = Join-Path $target 'BarracudaTray.exe'
$hrir = Join-Path $env:APPDATA 'BarracudaTray\hrir'
$temp = Join-Path $env:TEMP "BarracudaTray-kurulum-$([guid]::NewGuid().ToString('N'))"
$needsReboot = $false

function Step([string]$text) { Write-Host "`n> $text" -ForegroundColor Cyan }
function Done([string]$text) { Write-Host "  $text" -ForegroundColor Green }

function Download([string]$url, [string]$file) {
    Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
}

New-Item -ItemType Directory -Force $temp | Out-Null
try {
    Step '.NET 10 Masaüstü Çalışma Zamanı'
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $hasRuntime = $dotnet -and ((& $dotnet.Source --list-runtimes) -match '^Microsoft\.WindowsDesktop\.App 10\.')
    if ($hasRuntime) {
        Done 'Zaten kurulu.'
    } else {
        Write-Host '  Microsoft''tan indiriliyor (~60 MB)...'
        $installer = Join-Path $temp 'windowsdesktop-runtime.exe'
        Download $DotNetRuntimeUrl $installer
        $p = Start-Process $installer -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw ".NET kurulumu başarısız oldu (kod $($p.ExitCode))." }
        if ($p.ExitCode -eq 3010) { $needsReboot = $true }
        Done 'Kuruldu.'
    }

    Step 'VB-CABLE sanal ses kablosu (7.1 için)'
    $hasCable = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -match 'VB-Audio Virtual Cable' }
    if ($hasCable) {
        Done 'Zaten kurulu.'
    } else {
        Write-Host '  vb-audio.com''dan indiriliyor...'
        $zip = Join-Path $temp 'vbcable.zip'
        Download $VbCableUrl $zip
        Expand-Archive $zip -DestinationPath (Join-Path $temp 'vbcable')
        # VB-CABLE's license does not allow folding its setup into another installer, so its own
        # window is shown and the user starts the driver install there.
        Write-Host '  VB-Audio''nun kurulum penceresinde "Install Driver" düğmesine basın.' -ForegroundColor Yellow
        Start-Process (Join-Path $temp 'vbcable\VBCABLE_Setup_x64.exe') -Wait
        $needsReboot = $true
        Done 'Kurulum penceresi kapandı.'
    }

    Step '7.1 profilleri (HeSuVi)'
    if (Test-Path (Join-Path $hrir 'razer.wav')) {
        Done 'Zaten var.'
    } else {
        Write-Host '  SourceForge''dan indiriliyor (~27 MB)...'
        $package = Join-Path $temp 'hesuvi.exe'
        Download $HeSuViUrl $package
        # The package is a 7-Zip archive; Windows' own tar reads it, so nothing else is needed.
        & "$env:SystemRoot\System32\tar.exe" -xf $package -C $temp 'HeSuVi/hrir'
        if ($LASTEXITCODE -ne 0) { throw 'HeSuVi paketi açılamadı.' }
        New-Item -ItemType Directory -Force $hrir | Out-Null
        Copy-Item (Join-Path $temp 'HeSuVi\hrir\*.wav'), (Join-Path $temp 'HeSuVi\hrir\info.csv') $hrir -Force
        Done "$((Get-ChildItem $hrir -Filter *.wav).Count) profil kopyalandı."
    }

    Step 'Barracuda Tray'
    # An update: let the running copy exit cleanly so it hands the default playback device back.
    if (Test-Path $exe) { & $exe --exit | Out-Null }
    Get-Process BarracudaTray -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $source '*') $target -Recurse -Force

    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Barracuda Tray.lnk'))
    $shortcut.TargetPath = $exe
    $shortcut.Save()
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name BarracudaTray -Value "`"$exe`""
    Done "$target klasörüne kuruldu; Windows ile başlayacak."

    if (-not $needsReboot) {
        # Piped so PowerShell waits for the GUI-subsystem exe to finish.
        & $exe --configure-cable | Out-Null
    }

    Step 'Razer yazılımları'
    $razer = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -match 'Razer' }
    if (-not $razer) {
        Done 'Kurulu Razer yazılımı yok.'
    } else {
        Write-Host "  Kurulu: $(($razer.DisplayName | Sort-Object) -join ', ')"
        Write-Host '  Synapse çalışırken ışıkları ele geçirir; kaldırılması önerilir.' -ForegroundColor Yellow
        if ((Read-Host '  Razer kaldırıcısı açılsın mı? (E/H)') -match '^[EeYy]') {
            $razerInstaller = 'C:\Windows\Installer\Razer\Installer2\App\RazerInstaller.exe'
            if (Test-Path $razerInstaller) { Start-Process $razerInstaller -ArgumentList '/uninstall', 'true' -Wait }
            else { Start-Process 'ms-settings:appsfeatures' }
        }
    }
}
catch {
    Write-Host "`nKurulum durdu: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host 'Kapatmak için Enter'
    exit 1
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($needsReboot) {
    Write-Host "`nKurulum bitti. VB-CABLE sürücüsünün devreye girmesi için bilgisayarı yeniden başlatın;" -ForegroundColor Green
    Write-Host 'Barracuda Tray açılışta kendiliğinden başlayacak.' -ForegroundColor Green
    if ((Read-Host 'Şimdi yeniden başlatılsın mı? (E/H)') -match '^[EeYy]') { Restart-Computer }
} else {
    # Started through Explorer so the app runs as the normal user, not elevated like this script.
    Start-Process explorer.exe $exe
    Write-Host "`nKurulum bitti. Barracuda Tray tray'de çalışıyor." -ForegroundColor Green
    Read-Host 'Kapatmak için Enter'
}
