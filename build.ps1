# Publishes Housecast as a self-contained app and builds its installer into dist\.
# The release workflow runs this too. Needs Inno Setup 6 (winget install JRSoftware.InnoSetup).
#   .\build.ps1                      x64 installer, version 0.0.0
#   .\build.ps1 -Version 1.2.0 -Arch arm64
param(
    [string]$Version = '0.0.0',
    [ValidateSet('x64', 'arm64')] [string]$Arch = 'x64'
)
$ErrorActionPreference = 'Stop'

$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup' }

$publish = Join-Path $PSScriptRoot "publish\win-$Arch"
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$PSScriptRoot\src\Housecast\Housecast.csproj" -c Release -r "win-$Arch" --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

& $iscc /Q "/DAppVersion=$Version" "/DArch=$Arch" "$PSScriptRoot\installer\Housecast.iss"
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
Get-Item "$PSScriptRoot\dist\Housecast-$Version-$Arch-setup.exe"
