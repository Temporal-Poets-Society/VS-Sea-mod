[CmdletBinding()]
param([switch]$WithShips, [string]$ShipsZip, [string]$SeaZip, [int]$Port = 42781, [switch]$Wait,
    [string]$GamePath = "$env:APPDATA\Vintagestory")
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$run = Join-Path $repo ('.local\boats-' + $(if ($WithShips) { 'ships-' } else { 'sea-alone-' }) + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force "$run\Mods\probe","$run\ModConfig","$run\Saves" | Out-Null
if (-not $SeaZip) {
    $seaVersion = (Get-Content -LiteralPath (Join-Path $repo 'modinfo.json') -Raw | ConvertFrom-Json).version
    $SeaZip = Join-Path $repo "release\sea-$seaVersion.zip"
}
Copy-Item -LiteralPath $SeaZip -Destination "$run\Mods"
Copy-Item -LiteralPath (Join-Path $repo 'tests\BoatProbe\bin\Debug\mod\BoatProbe.dll'),(Join-Path $repo 'tests\BoatProbe\bin\Debug\mod\BoatProbe.pdb'),(Join-Path $repo 'tests\BoatProbe\modinfo.json') -Destination "$run\Mods\probe"
if ($WithShips) {
    if (-not $ShipsZip) { throw 'Pass the candidate ShipsZip for the Ships case.' }
    Copy-Item -LiteralPath $ShipsZip -Destination "$run\Mods"
}
@{Sea=@{Path=$SeaZip;SHA256=(Get-FileHash -LiteralPath $SeaZip -Algorithm SHA256).Hash};
    Ships=$(if($WithShips){@{Path=$ShipsZip;SHA256=(Get-FileHash -LiteralPath $ShipsZip -Algorithm SHA256).Hash}}else{$null})} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$run\inputs.json"
@{WithShips=[bool]$WithShips} | ConvertTo-Json | Set-Content -LiteralPath "$run\ModConfig\boat-probe.json"
@{ServerName='Sea boat acceptance';Ip='127.0.0.1';Port=$Port;Upnp=$false;AdvertiseServer=$false;MaxClients=1;PassTimeWhenEmpty=$true;
    DefaultRoleCode='crplayer';Roles=@(@{Code='crplayer';Name='Test';PrivilegeLevel=100;DefaultGameMode=2;Color='Green';Privileges=@('chat','build','useblock','controlserver')});
    ModPaths=@('Mods',"$run\Mods");MapSizeX=8192;MapSizeY=256;MapSizeZ=8192;
    WorldConfig=@{WorldName='Disposable Sea boat acceptance';Seed='4242';SaveFileLocation="$run\Saves\boats.vcdbs";MapSizeY=256;
        WorldType='standard';PlayStyle='creativebuilding';WorldConfiguration=@{worldWidth='8192';worldLength='8192';gameMode='creative'}}} |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$run\serverconfig.json"
$process = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @('VintagestoryServer.dll','--dataPath',('"'+$run+'"')) -WorkingDirectory $GamePath -WindowStyle Hidden -PassThru -RedirectStandardOutput "$run\console.log" -RedirectStandardError "$run\stderr.log"
[pscustomobject]@{RunPath=$run;ProcessId=$process.Id;WithShips=[bool]$WithShips} | ConvertTo-Json
if ($Wait) {
    if (-not $process.WaitForExit(180000)) { Stop-Process -Id $process.Id; throw "Boat test exceeded 180 seconds: $run" }
    $log = Get-Content -LiteralPath "$run\Logs\server-main.log" -Raw
    $log -split "`n" | Where-Object { $_ -match 'BOATCHECK|Could not patch' } | Write-Output
    if ($log -notmatch 'BOATCHECK RESULT PASS') { throw "Boat test failed: $run" }
}
