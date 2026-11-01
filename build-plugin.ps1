<#
.SYNOPSIS
    Packages Viswright as a Compile Pal plugin.

.DESCRIPTION
    Produces artifacts/Viswright/, a folder a user drops into their Compile Pal/Plugins directory.
    Compile Pal discovers it from the meta.json alone.

    Built the same way as Meshwright, for the same reasons: self-contained and single-file so it runs
    on a machine with no .NET installed, trimmed (nothing here or in SourceKit resolves a type by name,
    and the status JSON is written with Utf8JsonWriter rather than reflection-based serialisation), and
    compressed, which is what lets a single-file bundle run under Wine.

.PARAMETER Zip
    Also write artifacts/Viswright-plugin.zip, which is the form to attach to a release.

.PARAMETER Version
    Stamps the executable with a version.
#>
[CmdletBinding()]
param(
    [switch]$Zip,

    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$staging = Join-Path $root 'artifacts/publish'
$out = Join-Path $root 'artifacts/Viswright'

# The folder name is load-bearing: Compile Pal matches it against meta.json's "Name" to decide whether
# a step is already registered, and a mismatch loads the plugin under a name its parameters do not
# belong to.
$source = Join-Path $root 'CompilePalPlugin/Viswright'

Write-Host 'Publishing viswright...' -ForegroundColor Cyan

# Built as one array rather than a backtick-continued line so the optional -p:Version can be added
# or left out without the call having two shapes. It has to be typed [string[]]: PowerShell unrolls a
# one-element array back to a bare string, and splatting a string passes it one character at a time.
[string[]]$publishArgs = @(
    'publish'
    (Join-Path $root 'ViswrightCli/ViswrightCli.csproj')
    '--configuration', 'Release'
    '--runtime', 'win-x64'
    '--self-contained', 'true'
    '-p:PublishSingleFile=true'
    '-p:PublishTrimmed=true'
    '-p:TrimMode=full'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    # Needed to run under Wine at all - see the header.
    '-p:EnableCompressionInSingleFile=true'
    '-warnaserror'
    '--output', $staging
)

if ($Version) { $publishArgs += "-p:Version=$Version" }

dotnet @publishArgs

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

# Emptied in place rather than deleted and recreated.
#
# The folder is often not just build output: pointing Compile Pal's Plugins/Viswright at it with a
# junction is the obvious way to test a build, and Compile Pal builds that predate keeping step state
# in their own settings save whether the step is ticked, and where it sits in the order, into the
# plugin's own meta.json. Deleting the folder
# and copying the repo's meta.json back threw that away on every build, so the step unticked itself.
#
# So the files are cleared but the folder stays, and those two answers are carried over from the
# meta.json being replaced. Text substitution rather than a JSON round trip so the shipped file keeps
# its own formatting.
$kept = @{}
$installedMeta = Join-Path $out 'meta.json'

if (Test-Path $installedMeta) {
    try {
        $previous = Get-Content $installedMeta -Raw | ConvertFrom-Json
        foreach ($field in 'DoRun', 'Order') {
            if ($null -ne $previous.$field) { $kept[$field] = $previous.$field }
        }
    }
    catch {
        Write-Warning "Could not read the existing meta.json, so its DoRun/Order are not carried over: $_"
    }
}

if (Test-Path $out) { Get-ChildItem $out -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

Copy-Item (Join-Path $staging 'viswright.exe') $out

# Not the .pdb: it is larger than the executable and a plugin folder is something people copy around.
$meta = Get-Content (Join-Path $source 'meta.json') -Raw

foreach ($field in $kept.Keys) {
    $value = if ($kept[$field] -is [bool]) { "$($kept[$field])".ToLowerInvariant() }
             else { [string]::Format([cultureinfo]::InvariantCulture, '{0}', $kept[$field]) }
    $meta = $meta -replace "(`"$field`"\s*:\s*)[^,\r\n}]+", "`${1}$value"
}

Set-Content -Path $installedMeta -Value $meta -NoNewline -Encoding utf8
Copy-Item (Join-Path $source 'parameters.json') $out
Copy-Item (Join-Path $root 'LICENSE') $out
Copy-Item (Join-Path $source 'README.md') $out -ErrorAction SilentlyContinue

# The report is a second compile step, after VVIS, that runs the same executable. It is its own
# plugin folder because a step is a folder, and it holds only its step definition: its meta.json
# points at Plugins\Viswrightiswright.exe.
$reportSource = Join-Path $root 'CompilePalPlugin/Viswright Report'
$reportOut = Join-Path $root 'artifacts/Viswright Report'
if (Test-Path $reportOut) { Get-ChildItem $reportOut -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Path $reportOut -Force | Out-Null
Copy-Item (Join-Path $reportSource 'meta.json') $reportOut
Copy-Item (Join-Path $reportSource 'parameters.json') $reportOut

$size = (Get-ChildItem $out -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB

Write-Host ''
Write-Host "Plugin written to $out ($([math]::Round($size, 1)) MB)" -ForegroundColor Green
Get-ChildItem $out | ForEach-Object { Write-Host "  $($_.Name)" }

if ($Zip) {
    $archive = Join-Path $root 'artifacts/Viswright-plugin.zip'
    if (Test-Path $archive) { Remove-Item $archive -Force }

    # Compressing the folder itself, not its contents, so the zip contains a Viswright/ directory -
    # extracting it straight into Plugins/ then lands in the right place.
    Compress-Archive -Path $out, $reportOut -DestinationPath $archive
    Write-Host "Archive written to $archive" -ForegroundColor Green
}

Write-Host ''
Write-Host 'To install: copy the Viswright and Viswright Report folders into your Compile Pal "Plugins" directory,'
Write-Host 'then restart Compile Pal and add the Viswright step to a preset.'
