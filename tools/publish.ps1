# Build dated executables without replacing the running installation.
param([Parameter(Mandatory = $true)][string]$Tag)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$Version = ($Tag -split '\.')[-1]
$temp = Join-Path $root "tmp/publish-$Version"
$source = Join-Path $temp 'src'
New-Item -ItemType Directory -Force $source, (Join-Path $root 'publish') | Out-Null
& robocopy (Join-Path $root 'src') $source /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Source copy failed' }
Copy-Item (Join-Path $root 'Directory.Build.props') (Join-Path $temp 'Directory.Build.props') -Force
& dotnet publish (Join-Path $source 'PiloSync.Cli/PiloSync.Cli.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true "-p:PiloSyncTag=$Tag" -p:ContinuousIntegrationBuild=true -p:DebugType=none -o (Join-Path $temp 'cli')
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }
& dotnet publish (Join-Path $source 'PiloSync.Launcher/PiloSync.Launcher.csproj') -c Release "-p:PiloSyncTag=$Tag" -p:ContinuousIntegrationBuild=true -p:DebugType=none -o (Join-Path $temp 'launcher')
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed' }
Copy-Item (Join-Path $temp 'launcher/PiloSync.exe') (Join-Path $root "publish/PiloSync-$Version.exe") -Force
Copy-Item (Join-Path $temp 'cli/pilosync-cli.exe') (Join-Path $root "publish/pilosync-cli-$Version.exe") -Force
