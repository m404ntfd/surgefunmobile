$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host 'Surge Guest Information Kiosk - Uninstall' -ForegroundColor Cyan
Write-Host ''

$answer = Read-Host 'Remove the kiosk application, shortcuts, settings, and local browsing data? (Y/N)'
if ($answer -notmatch '^[Yy]') {
    Write-Host 'No changes were made.'
    exit 0
}

$processes = Get-Process -Name 'SurgeMobileEventKiosk' -ErrorAction SilentlyContinue
if ($processes) {
    throw 'The kiosk is running. Exit it with Ctrl + Alt + Shift + F12, then run this uninstaller again.'
}

$velopackRoot = Join-Path $env:LOCALAPPDATA 'SurgeMobile.EventKiosk'
$velopackUpdater = Join-Path $velopackRoot 'Update.exe'
$legacyInstallRoot = Join-Path $env:LOCALAPPDATA 'SurgeMobileEventKiosk'
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Surge Guest Information Kiosk.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Surge Guest Information Kiosk.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Surge Guest Information Kiosk.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Surge Mobile Event Kiosk.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Surge Mobile Event Kiosk.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Surge Mobile Event Kiosk.lnk')
)

foreach ($shortcutPath in $shortcutPaths) {
    if (Test-Path $shortcutPath) {
        Remove-Item $shortcutPath -Force
    }
}

if (Test-Path -LiteralPath $velopackUpdater) {
    $uninstaller = Start-Process -FilePath $velopackUpdater -ArgumentList @('uninstall', '--silent') -Wait -PassThru
    if ($uninstaller.ExitCode -ne 0) {
        throw "The Velopack uninstaller ended with exit code $($uninstaller.ExitCode)."
    }
}

if (Test-Path -LiteralPath $legacyInstallRoot) {
    Remove-Item -LiteralPath $legacyInstallRoot -Recurse -Force
}

Write-Host 'The guest information kiosk was removed from this Windows account.' -ForegroundColor Green
