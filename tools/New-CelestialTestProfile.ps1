<# Creates a NEW isolated game data directory. Never deletes or overwrites any profile, package or save. #>
[CmdletBinding()]
param(
    [string] $PackagePath,
    [switch] $AstraTerra,
    [switch] $Geography,
    [switch] $Wmap,
    [switch] $Ship,
    [switch] $Baseline,
    [switch] $SelfTest
)
$ErrorActionPreference = 'Stop'
$seaRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$installedMods = Join-Path $env:APPDATA 'VintagestoryData\Mods'
if (-not $PackagePath) {
    $version = (Get-Content -LiteralPath (Join-Path $seaRoot 'modinfo.json') -Raw | ConvertFrom-Json).version
    $PackagePath = Join-Path $seaRoot "release\sea-$version.zip"
}
$package = Get-Item -LiteralPath $PackagePath
$names = @()
if ($AstraTerra) { $names += 'AstraTerra-0.15.0.zip' }
if ($Geography) { $names += 'cylinderworld-0.11.5.zip'; $names += 'earthmap-0.14.1-pre.5.zip' }
if ($Wmap) { $names += 'wmap_0.4.1.zip' }
if ($Ship) { $names += 'magelan-0.3.29.zip' }
foreach ($name in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $installedMods $name))) { throw "Exact comparison package unavailable: $name" }
}
$profile = Join-Path $seaRoot ('testserver\celestial-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
if (Test-Path -LiteralPath $profile) { throw "Refusing an existing profile: $profile" }
New-Item -ItemType Directory -Path $profile | Out-Null
New-Item -ItemType Directory -Path (Join-Path $profile 'Mods'), (Join-Path $profile 'ModConfig'), (Join-Path $profile 'Saves') | Out-Null
if ($package.PSIsContainer) {
    Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $profile 'Mods\Sea') -Recurse
} else {
    Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $profile ('Mods\' + $package.Name))
}
foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $installedMods $name) -Destination (Join-Path $profile 'Mods') }
if ($AstraTerra) {
    $astraconfig = Join-Path $env:APPDATA 'VintagestoryData\ModConfig\astraterra.json'
    if (Test-Path -LiteralPath $astraconfig) { Copy-Item -LiteralPath $astraconfig -Destination (Join-Path $profile 'ModConfig\astraterra.json') }
}
$config = Get-Content -LiteralPath (Join-Path $seaRoot 'testserver\serverconfig.json') -Raw | ConvertFrom-Json
$config.Port = 42429
$config.AdvertiseServer = $false
$config.Upnp = $false
$config.ModPaths = @('Mods', (Join-Path $profile 'Mods'))
$config.MapSizeX = 8192; $config.MapSizeZ = 8192; $config.MapSizeY = 256
$config.WorldConfig.Seed = '4242'
$config.WorldConfig.WorldName = 'Disposable Sea celestial comparison'
$config.WorldConfig.SaveFileLocation = Join-Path $profile 'Saves\comparison.vcdbs'
$config.WorldConfig.MapSizeY = 256
$config.WorldConfig.WorldConfiguration = @{worldWidth='8192'; worldLength='8192'; worldEdge='blocked'; gameMode='creative'}
$config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $profile 'serverconfig.json') -Encoding utf8
$effects = if ($Baseline) { 0 } else { 1 }
@{CelestialReflections=$effects; SkyReflections=$effects; NightVisibility=$effects} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'ModConfig\sea.json') -Encoding utf8
@{Enabled=[bool]$SelfTest; Shutdown=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'ModConfig\sea-selftest.json') -Encoding utf8
@{Baseline=[bool]$Baseline; SelfTest=[bool]$SelfTest; Packages=@($package.Name) + $names; Seed=4242} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'comparison-profile.json') -Encoding utf8
Write-Output $profile
