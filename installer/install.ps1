# Barracuda Tray kurulumu: uygulamayı bu kullanıcı için kurar ve eksik olanı resmi kaynaklarından
# indirir. Kur.cmd üzerinden çalıştırın.
#
# Yönetici izni yalnızca makine geneli parçalar (.NET çalışma zamanı, VB-CABLE sürücüsü) eksikse,
# ayrı bir pencerede istenir (-MachineOnly). Kullanıcıya ait her şey bu pencerede, normal oturumda
# yapılır; yetki yükseltmesi başka bir hesaba geçse bile uygulama doğru kullanıcıya kurulur.
# -Unattended soru sormadan çalışır. Her çalışma %TEMP%\BarracudaTray-kurulum.log dosyasına yazılır.
param([switch]$Unattended, [switch]$MachineOnly)

$ErrorActionPreference = 'Stop'

$DotNetRuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'
$VbCableUrl = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip'
$HeSuViUrl = 'https://downloads.sourceforge.net/project/hesuvi/HeSuVi_2.0.0.1.exe'

$source = Join-Path $PSScriptRoot 'app'
$target = Join-Path $env:LOCALAPPDATA 'Programs\BarracudaTray'
$exe = Join-Path $target 'BarracudaTray.exe'
$hrir = Join-Path $env:APPDATA 'BarracudaTray\hrir'
$log = Join-Path $env:TEMP $(if ($MachineOnly) { 'BarracudaTray-kurulum-yonetici.log' } else { 'BarracudaTray-kurulum.log' })
$temp = Join-Path $env:TEMP "BarracudaTray-kurulum-$([guid]::NewGuid().ToString('N'))"

function Step([string]$text) { Write-Host "`n> $text" -ForegroundColor Cyan }
function Done([string]$text) { Write-Host "  $text" -ForegroundColor Green }
function Ask([string]$question) { -not $Unattended -and ((Read-Host $question) -match '^[EeYy]') }
function Pause { if (-not $Unattended) { Read-Host 'Kapatmak için Enter' | Out-Null } }

function HasRuntime {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    [bool]($dotnet -and ((& $dotnet.Source --list-runtimes) -match '^Microsoft\.WindowsDesktop\.App 10\.'))
}

function HasCable {
    [bool](Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'VB-Audio Virtual Cable' })
}

# Windows' own curl: SourceForge hands browser-like clients (PowerShell's web cmdlets included) an
# HTML "your download will start" page instead of the file.
function Download([string]$url, [string]$file, [string]$magic) {
    & "$env:SystemRoot\System32\curl.exe" --silent --show-error --fail --location --retry 2 --output $file $url
    if ($LASTEXITCODE -ne 0) { throw "İndirilemedi: $url (curl $LASTEXITCODE)" }
    $head = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($file), 0, 2)
    if ($head -ne $magic) { throw "İndirilen dosya beklenen türde değil: $url" }
}

function Install-Machine {
    Step '.NET 10 Masaüstü Çalışma Zamanı'
    if (HasRuntime) {
        Done 'Zaten kurulu.'
    } else {
        Write-Host '  Microsoft''tan indiriliyor (~60 MB)...'
        $installer = Join-Path $temp 'windowsdesktop-runtime.exe'
        Download $DotNetRuntimeUrl $installer 'MZ'
        $p = Start-Process $installer -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw ".NET kurulumu başarısız oldu (kod $($p.ExitCode))." }
        Done 'Kuruldu.'
    }

    Step 'VB-CABLE sanal ses kablosu (7.1 için)'
    if (HasCable) {
        Done 'Zaten kurulu.'
    } else {
        Write-Host '  vb-audio.com''dan indiriliyor...'
        $zip = Join-Path $temp 'vbcable.zip'
        Download $VbCableUrl $zip 'PK'
        Expand-Archive $zip -DestinationPath (Join-Path $temp 'vbcable')
        # VB-CABLE's license does not allow folding its setup into another installer, so its own
        # window is shown and the user starts the driver install there.
        Write-Host '  VB-Audio''nun kurulum penceresinde "Install Driver" düğmesine basın.' -ForegroundColor Yellow
        Start-Process (Join-Path $temp 'vbcable\VBCABLE_Setup_x64.exe') -Wait
        Done 'Kurulum penceresi kapandı.'
    }
}

function Install-User {
    Step '7.1 profilleri (HeSuVi)'
    if (Test-Path (Join-Path $hrir 'razer.wav')) {
        Done 'Zaten var.'
    } else {
        Write-Host '  SourceForge''dan indiriliyor (~27 MB)...'
        $package = Join-Path $temp 'hesuvi.exe'
        Download $HeSuViUrl $package 'MZ'
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
    # A per-user logon task rather than the Run key: Explorer skipped the Run entry at logon while
    # starting its neighbours. Priority 4 is normal (tasks default to below normal); no time limit
    # (the default stops tasks after 72 h) and no AC-power requirement.
    Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name BarracudaTray -ErrorAction SilentlyContinue
    $user = "$env:USERDOMAIN\$env:USERNAME"
    Register-ScheduledTask -TaskName 'Barracuda Tray' -Force `
        -Action (New-ScheduledTaskAction -Execute $exe) `
        -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $user) `
        -Principal (New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited) `
        -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Priority 4) | Out-Null
    Done "$target klasörüne kuruldu; oturum açılınca kendiliğinden başlayacak."

    # Piped so PowerShell waits for the GUI-subsystem exe. Fails harmlessly right after a fresh
    # VB-CABLE install; the app switches CABLE Input to 7.1 itself once 7.1 is turned on.
    & $exe --configure-cable | Out-Null

    Step 'Razer yazılımları'
    $razer = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -match 'Razer' }
    if (-not $razer) {
        Done 'Kurulu Razer yazılımı yok.'
    } else {
        Write-Host "  Kurulu: $(($razer.DisplayName | Sort-Object) -join ', ')"
        Write-Host '  Synapse çalışırken ışıkları ele geçirir; kaldırılması önerilir.' -ForegroundColor Yellow
        if (Ask '  Razer kaldırıcısı açılsın mı? (E/H)') {
            $razerInstaller = 'C:\Windows\Installer\Razer\Installer2\App\RazerInstaller.exe'
            if (Test-Path $razerInstaller) { Start-Process $razerInstaller -ArgumentList '/uninstall', 'true' -Verb RunAs -Wait }
            else { Start-Process 'ms-settings:appsfeatures' }
        }
    }
}

Start-Transcript -Path $log -Force | Out-Null
New-Item -ItemType Directory -Force $temp | Out-Null
$cableBefore = HasCable
try {
    if ($MachineOnly) {
        Install-Machine
    } else {
        Step 'Sistem bileşenleri (.NET 10, VB-CABLE)'
        if ((HasRuntime) -and $cableBefore) {
            Done 'Zaten kurulu; yönetici izni gerekmiyor.'
        } else {
            Write-Host 'Eksik sistem bileşenleri için yönetici izni isteniyor...' -ForegroundColor Yellow
            $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -MachineOnly" + $(if ($Unattended) { ' -Unattended' })
            $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
            if ($p.ExitCode -ne 0 -or -not (HasRuntime)) {
                throw "Sistem bileşenleri kurulamadı. Ayrıntılar: $env:TEMP\BarracudaTray-kurulum-yonetici.log"
            }
        }
        Install-User
    }
}
catch {
    Write-Host "`nKurulum durdu: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Ayrıntılar: $log"
    Stop-Transcript | Out-Null
    Pause
    exit 1
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($MachineOnly) {
    Stop-Transcript | Out-Null
    exit 0
}

if (-not $cableBefore) {
    Write-Host "`nKurulum bitti. VB-CABLE sürücüsünün devreye girmesi için bilgisayarı yeniden başlatın;" -ForegroundColor Green
    Write-Host 'Barracuda Tray açılışta kendiliğinden başlayacak.' -ForegroundColor Green
    Stop-Transcript | Out-Null
    if (Ask 'Şimdi yeniden başlatılsın mı? (E/H)') { Restart-Computer }
} else {
    Start-Process $exe
    Write-Host "`nKurulum bitti. Barracuda Tray tray'de çalışıyor." -ForegroundColor Green
    Stop-Transcript | Out-Null
    Pause
}
