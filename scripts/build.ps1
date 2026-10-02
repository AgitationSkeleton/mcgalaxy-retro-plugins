param(
    [Parameter(Mandatory = $true)]
    [string]$MCGalaxyDll
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $MCGalaxyDll)) { throw "MCGalaxy assembly not found: $MCGalaxyDll" }
if (-not (Test-Path -LiteralPath $csc)) { throw "C# compiler not found: $csc" }

$plugins = @(
    @{ Name = 'AmbientMobs'; Extra = @() },
    @{ Name = 'DynmapClassic'; Extra = @('/reference:System.Drawing.dll') },
    @{ Name = 'IndevWorldGen'; Extra = @() },
    @{ Name = 'HerobrineGhostMCG'; Extra = @() }
)

foreach ($plugin in $plugins) {
    $name = $plugin.Name
    $dir = Join-Path $repo "plugins\$name"
    $args = @('/nologo', '/target:library', '/optimize+', "/out:$dir\$name.dll", "/reference:$MCGalaxyDll")
    $args += $plugin.Extra
    $args += "$dir\$name.cs"
    & $csc @args
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile $name" }
}

$bridge = Join-Path $repo 'plugins\IndevWorldGen'
$classes = Join-Path $bridge 'bridge-bin'
New-Item -ItemType Directory -Force $classes | Out-Null
& javac -d $classes (Join-Path $bridge 'IndevGeneratorBridge.java')
if ($LASTEXITCODE -ne 0) { throw 'Failed to compile IndevGeneratorBridge.java' }
& jar --create --file (Join-Path $bridge 'IndevGeneratorBridge.jar') -C $classes .
if ($LASTEXITCODE -ne 0) { throw 'Failed to package IndevGeneratorBridge.jar' }

Write-Host 'Built all plugins successfully.'
