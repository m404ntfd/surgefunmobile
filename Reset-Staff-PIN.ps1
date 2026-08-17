$ErrorActionPreference = 'Stop'

$settingsPath = Join-Path $env:LOCALAPPDATA 'SurgeMobileEventKiosk\Data\settings.json'
$exePaths = @(
    (Join-Path $env:LOCALAPPDATA 'SurgeMobile.EventKiosk\current\SurgeMobileEventKiosk.exe'),
    (Join-Path $env:LOCALAPPDATA 'SurgeMobileEventKiosk\App\SurgeMobileEventKiosk.exe')
)

Write-Host ''
Write-Host 'Surge Mobile Event Kiosk - Reset Staff Password' -ForegroundColor Cyan
Write-Host ''

if (Get-Process -Name 'SurgeMobileEventKiosk' -ErrorAction SilentlyContinue) {
    throw 'Exit the kiosk first using Ctrl + Alt + Shift + F12, then run this tool again.'
}

$answer = Read-Host 'Remove the current staff password and create a new one on the next launch? (Y/N)'
if ($answer -notmatch '^[Yy]') {
    Write-Host 'No changes were made.'
    exit 0
}

if (Test-Path $settingsPath) {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settings.StaffPinSalt = ''
    $settings.StaffPinHash = ''
    $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
}

Write-Host 'The staff password was cleared. Advertisement schedules and other kiosk settings were kept.' -ForegroundColor Green
Write-Host 'The kiosk will ask for a new password.' -ForegroundColor Green
$exePath = $exePaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($exePath) {
    Start-Process -FilePath $exePath
}
