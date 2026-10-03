param([string]$Iscc = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root 'src\WslDock\WslDock.csproj'))).Project.PropertyGroup.Version
$candidates = @($Iscc, "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
$compiler = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $compiler) { throw 'Install Inno Setup 6 or pass -Iscc with the path to ISCC.exe.' }
& (Join-Path $PSScriptRoot 'build.ps1') -Publish
$publish = Join-Path $root 'artifacts\WslDock'
$output = Join-Path $root 'artifacts\release'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md') -Destination $publish -Force
New-Item -ItemType Directory -Path "$publish/docs/screenshots", "$publish/src/WslDock/Assets" -Force | Out-Null
Copy-Item "$root/docs/screenshots/*.png" "$publish/docs/screenshots"
Copy-Item "$root/src/WslDock/Assets/WslDock.png" "$publish/src/WslDock/Assets"
Copy-Item "$root/AGENTS.md" $publish
$zip = Join-Path $output "WslDock-$version-win-x64.zip"
Compress-Archive -Path "$publish\*" -DestinationPath $zip -CompressionLevel Optimal -Force
& $compiler /Q "/DMyAppVersion=$version" (Join-Path $root 'installer\WslDock.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
Get-Item $zip, (Join-Path $output "WslDock-Setup-v$version.exe") | Select-Object Name,Length
