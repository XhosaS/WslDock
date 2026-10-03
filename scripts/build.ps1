param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localSdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetExe = if (Test-Path $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
$project = Join-Path $projectRoot 'src\WslDock\WslDock.csproj'
$restoreConfig = '-p:RestoreConfigFile=' + (Join-Path $projectRoot 'NuGet.Config')
if ($Publish) {
    $output = Join-Path $projectRoot 'artifacts\WslDock'
    if (Test-Path $output) {
        $resolved = (Resolve-Path $output).Path
        if ($resolved -ne [IO.Path]::GetFullPath($output)) { throw 'Unexpected publish path' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    & $dotnetExe publish $project $restoreConfig -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $output
} else {
    & $dotnetExe build $project $restoreConfig -c Release
}
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
