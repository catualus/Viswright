<#
.SYNOPSIS
    Runs the benchmark in docs/benchmark.md: every variant of every map, compiled three times and
    measured at the same standing points.

.DESCRIPTION
    Downloads nothing. -Sources names a folder holding, per map, the source the method lists:
      tf2/<map>.vmf          Valve's VMF (the TF2 SDK sdk_ sources, renamed without the sdk_ prefix)
      gmod/<map>.vmf         the decompile or released source
      gmod/<map>.bsp         the author's shipped BSP, where there is one
    Results go to -Out: one folder per map with every compile, and results.json with every figure.

.EXAMPLE
    ./bench/run.ps1 -Sources D:\bench\sources -Out D:\bench\out
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Sources,
    [Parameter(Mandatory)] [string]$Out,
    [string]$Tools = "$env:USERPROFILE\Documents\Tools++",
    [string]$Tf2 = "C:\Program Files (x86)\Steam\steamapps\common\Team Fortress 2\tf",
    [string]$GarrysMod = "C:\Program Files (x86)\Steam\steamapps\common\GarrysMod\garrysmod",
    [int]$Runs = 3,
    [string[]]$Only
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$vbsp = Join-Path $Tools 'vbsp++.exe'
$vvis = Join-Path $Tools 'vvis++.exe'
$vrad = Join-Path $Tools 'vrad++.exe'

dotnet build (Join-Path $root 'bench/Viswright.Bench/Viswright.Bench.csproj') -c Release -v q | Out-Null
dotnet build (Join-Path $root 'ViswrightCli/ViswrightCli.csproj') -c Release -v q | Out-Null
$bench = Join-Path $root 'bench/Viswright.Bench/bin/Release/net10.0/viswright-bench.exe'
$viswright = Join-Path $root 'ViswrightCli/bin/Release/net10.0/viswright.exe'

New-Item -ItemType Directory -Force $Out | Out-Null
$resultsFile = Join-Path $Out 'results.json'
$results = if (Test-Path $resultsFile) { Get-Content $resultsFile -Raw | ConvertFrom-Json -AsHashtable } else { @{} }

function Save-Results { $results | ConvertTo-Json -Depth 8 | Set-Content $resultsFile -Encoding utf8 }

function Invoke-Timed([string]$exe, [string[]]$arguments, [string]$log) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $exe @arguments *>> $log
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed ($LASTEXITCODE); see $log" }
    return [math]::Round($watch.Elapsed.TotalSeconds, 1)
}

# One variant: VBSP and VVIS $Runs times, each measured at the map's points; VRAD once, for the game.
function Invoke-Variant([string]$map, [string]$variant, [string]$vmf, [string]$game, [string[]]$radFlags, [string]$points) {
    $dir = Join-Path $Out "$map/$variant"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $name = "vwb_${map}_$variant"
    $measured = @()
    for ($i = 1; $i -le $Runs; $i++) {
        $copy = Join-Path $dir "$name.vmf"
        Copy-Item $vmf $copy -Force
        $log = Join-Path $dir "run$i.log"
        Remove-Item $log -ErrorAction SilentlyContinue
        $bspTime = Invoke-Timed $vbsp @('-game', $game, '-cullverts', $copy) $log
        $visTime = Invoke-Timed $vvis @('-game', $game, $copy) $log
        $bsp = [IO.Path]::ChangeExtension($copy, 'bsp')
        if (-not $points) {
            $points = Join-Path (Join-Path $Out $map) 'points.json'
            & $bench points $bsp $vmf $points | Tee-Object -Append $log | Out-Host
        }
        $measure = Join-Path $dir "measure$i.json"
        & $bench measure $bsp $points $game $measure | Tee-Object -Append $log | Out-Host
        $m = Get-Content $measure -Raw | ConvertFrom-Json -AsHashtable
        $m.vbspSeconds = $bspTime
        $m.vvisSeconds = $visTime
        $measured += $m
        if ($i -lt $Runs) { Copy-Item $bsp (Join-Path $dir "$name.run$i.bsp") -Force }
    }
    $radTime = Invoke-Timed $vrad (@('-game', $game) + $radFlags + @([IO.Path]::ChangeExtension((Join-Path $dir "$name.vmf"), 'bsp'))) (Join-Path $dir 'vrad.log')
    $results[$map][$variant] = @{ runs = $measured; vradSeconds = $radTime; bsp = (Join-Path $dir "$name.bsp") }
    Save-Results
    return $points
}

function Invoke-Viswright([string]$map, [string]$vmf, [string]$game) {
    $dir = Join-Path $Out "$map/viswright-search"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $copy = Join-Path $dir "vwb_${map}_viswright.vmf"
    Copy-Item $vmf $copy -Force
    $log = Join-Path $dir 'viswright.log'
    $flags = @('optimize', $copy, '-mode', 'auto', '-areaportals', '-detail', '-nodraw', '-fades', '-slim', '-hints',
        '-vbsp', $vbsp, '-vvis', $vvis, '-game', $game)
    $seconds = Invoke-Timed $viswright $flags $log
    # the optimised copy is handed on the way Compile Pal reads it
    $line = Select-String -Path $log -Pattern 'COMPILE_PAL_SET file "(.+)"' | Select-Object -Last 1
    if (-not $line) { throw "viswright handed no file on; see $log" }
    $optimised = Join-Path $dir 'optimised.vmf'
    Copy-Item $line.Matches[0].Groups[1].Value $optimised -Force
    $results[$map].viswrightSeconds = $seconds
    Save-Results
    return $optimised
}

$tf2Maps = @('ctf_2fort', 'cp_dustbowl', 'cp_badlands', 'cp_granary', 'cp_gravelpit', 'tc_hydro', 'pl_badwater', 'pl_goldrush', 'arena_lumberyard', 'arena_ravine')
$gmodMaps = @('rp_downtown_v4c_v2', 'rp_downtown_tits_v2', 'rp_rockford_v2b', 'rp_evocity_v33x')

foreach ($map in $tf2Maps) {
    if ($Only -and $map -notin $Only) { continue }
    if (-not $results.ContainsKey($map)) { $results[$map] = @{ game = 'tf2' } }
    $valve = Join-Path $Sources "tf2/$map.vmf"
    $stripped = Join-Path $Out "$map/stripped.vmf"
    New-Item -ItemType Directory -Force (Join-Path $Out $map) | Out-Null
    & $bench strip $valve $stripped | Out-Host
    # points come from the stripped map: the plain geometry every variant shares
    $points = Invoke-Variant $map 'stripped' $stripped $Tf2 @('-final', '-hdr') $null
    Invoke-Variant $map 'valve' $valve $Tf2 @('-final', '-hdr') $points | Out-Null
    $optimised = Invoke-Viswright $map $stripped $Tf2
    Invoke-Variant $map 'viswright' $optimised $Tf2 @('-final', '-hdr') $points | Out-Null
}

foreach ($map in $gmodMaps) {
    if ($Only -and $map -notin $Only) { continue }
    if (-not $results.ContainsKey($map)) { $results[$map] = @{ game = 'gmod' } }
    $source = Join-Path $Sources "gmod/$map.vmf"
    $points = Invoke-Variant $map 'plain' $source $GarrysMod @('-final', '-ldr') $null
    $shipped = Join-Path $Sources "gmod/$map.bsp"
    if (Test-Path $shipped) {
        $measure = Join-Path $Out "$map/shipped.json"
        & $bench measure $shipped $points $GarrysMod $measure | Out-Host
        $results[$map].shipped = Get-Content $measure -Raw | ConvertFrom-Json -AsHashtable
        Save-Results
    }
    $optimised = Invoke-Viswright $map $source $GarrysMod
    Invoke-Variant $map 'viswright' $optimised $GarrysMod @('-final', '-ldr') $points | Out-Null
}

Write-Host "Results in $resultsFile"
