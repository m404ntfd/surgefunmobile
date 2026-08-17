$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host 'Surge Mobile Event Kiosk Installer' -ForegroundColor Magenta
Write-Host '----------------------------------' -ForegroundColor Cyan
Write-Host ''

$running = Get-Process -Name 'SurgeMobileEventKiosk' -ErrorAction SilentlyContinue
if ($running) {
    throw 'The event kiosk is running. Exit it with Ctrl + Alt + Shift + F12, then run this installer again.'
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

$startupShortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Surge Mobile Event Kiosk.lnk'
$previousStartupPreference = Test-Path -LiteralPath $startupShortcutPath
if ($previousStartupPreference) {
    $startupAnswer = 'Y'
}
else {
    $startupAnswer = Read-Host 'Start the event kiosk automatically when this Windows account signs in? (Y/N)'
}

if ($startupAnswer -match '^[Yy]') {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startupShortcutPath)
    $shortcut.TargetPath = $installedExe
    $shortcut.WorkingDirectory = Split-Path -Parent $installedExe
    $shortcut.Description = 'Automatically start the Surge Mobile event kiosk'
    $shortcut.Save()
}
elseif (Test-Path -LiteralPath $startupShortcutPath) {
    Remove-Item -LiteralPath $startupShortcutPath -Force
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
