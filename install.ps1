# AIO Lite installer
# usage (in PowerShell):  irm https://raw.githubusercontent.com/Quiiks/AioLite/main/install.ps1 | iex
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\AioLite'
$zip = Join-Path $env:TEMP 'AioLite-win-x64.zip'

Write-Host 'Downloading the latest AIO Lite...'
Invoke-WebRequest 'https://github.com/Quiiks/AioLite/releases/latest/download/AioLite-win-x64.zip' -OutFile $zip -UseBasicParsing

# close the running one so the files can be replaced
Get-Process AioLite -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

New-Item -ItemType Directory -Force $dir | Out-Null
Expand-Archive $zip -DestinationPath $dir -Force
Remove-Item $zip -Force

# first launch adds it to Windows startup by itself
Start-Process (Join-Path $dir 'AioLite.exe')
Write-Host "AIO Lite installed in $dir" -ForegroundColor Green
