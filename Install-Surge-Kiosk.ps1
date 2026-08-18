$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host 'Surge Guest Information Kiosk Installer' -ForegroundColor Magenta
Write-Host '---------------------------------------' -ForegroundColor Cyan
Write-Host ''

$running = Get-Process -Name 'SurgeMobileEventKiosk' -ErrorAction SilentlyContinue
if ($running) {
    throw 'The guest information kiosk is running. Exit it with Ctrl + Alt + Shift + F12, then run this installer again.'
}

$searchFolders = @($PSScriptRoot, (Join-Path $PSScriptRoot 'Releases'))
$setup = $null
foreach ($folder in $searchFolders) {
    if (-not (Test-Path -LiteralPath $folder)) {
        continue
    }

    $setup = Get-ChildItem -LiteralPath $folder -Filter '*-Setup.exe' -File |
        Where-Object { $_.Name -like 'SurgeMobile.EventKiosk*' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($setup) {
        break
    }
}

if (-not $setup) {
    throw 'The Velopack Setup file is missing. Download it from https://github.com/m404ntfd/surgefunmobile/releases/latest, place it beside this script, and try again.'
}

Write-Host 'Starting the updateable kiosk installer...' -ForegroundColor Yellow
$installer = Start-Process -FilePath $setup.FullName -Wait -PassThru
if ($installer.ExitCode -ne 0) {
    throw "The kiosk installer ended with exit code $($installer.ExitCode)."
}

$installedExe = Join-Path $env:LOCALAPPDATA 'SurgeMobile.EventKiosk\current\SurgeMobileEventKiosk.exe'
if (-not (Test-Path -LiteralPath $installedExe)) {
    throw 'Installation completed, but the installed kiosk application could not be found.'
}

$oldStartupShortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Surge Mobile Event Kiosk.lnk'
$startupShortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Surge Guest Information Kiosk.lnk'
$previousStartupPreference = Test-Path -LiteralPath $startupShortcutPath
if (-not $previousStartupPreference) {
    $previousStartupPreference = Test-Path -LiteralPath $oldStartupShortcutPath
}
if ($previousStartupPreference) {
    $startupAnswer = 'Y'
}
else {
    $startupAnswer = Read-Host 'Start the guest information kiosk automatically when this Windows account signs in? (Y/N)'
}

if ($startupAnswer -match '^[Yy]') {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startupShortcutPath)
    $shortcut.TargetPath = $installedExe
    $shortcut.WorkingDirectory = Split-Path -Parent $installedExe
    $shortcut.Description = 'Automatically start the Surge guest information kiosk'
    $shortcut.Save()
    if (Test-Path -LiteralPath $oldStartupShortcutPath) {
        Remove-Item -LiteralPath $oldStartupShortcutPath -Force
    }
}
elseif (Test-Path -LiteralPath $startupShortcutPath) {
    Remove-Item -LiteralPath $startupShortcutPath -Force
}
if ($startupAnswer -notmatch '^[Yy]' -and (Test-Path -LiteralPath $oldStartupShortcutPath)) {
    Remove-Item -LiteralPath $oldStartupShortcutPath -Force
}

Write-Host ''
Write-Host 'Installation complete.' -ForegroundColor Green
Write-Host 'Future updates will be checked automatically whenever the kiosk starts.' -ForegroundColor Green
Write-Host 'Staff can also check for updates from Staff Settings.' -ForegroundColor Green
Write-Host 'Staff settings shortcut: Ctrl + Alt + Shift + F12' -ForegroundColor Green
Write-Host ''

if (-not (Get-Process -Name 'SurgeMobileEventKiosk' -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $installedExe -WorkingDirectory (Split-Path -Parent $installedExe)
}
