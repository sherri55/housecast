# Builds the tray app and installs it for the current user, with a Start menu shortcut.
# Run again after changing the code to update the installed copy.
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\AirPlayTray'
$exe = Join-Path $installDir 'AirPlayTray.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'AirPlay Tray.lnk'

# The running copy locks its files and would quit with its speakers left playing
Get-Process AirPlayTray -ErrorAction SilentlyContinue | Stop-Process -Confirm:$false
Start-Sleep -Seconds 1

dotnet publish "$PSScriptRoot\AirPlayTray.csproj" -c Release -o $installDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $installDir
$link.Description = 'Play this PC''s sound on AirPlay devices'
$link.Save()

Start-Process $exe
Write-Host "Installed to $installDir and started. Start menu: AirPlay Tray."
