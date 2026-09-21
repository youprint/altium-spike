#requires -Version 5.1
<#
    Deploy.ps1 - build AltiumSpike and install it as an Altium Designer extension.

      .\Deploy.ps1              build, copy, register
      .\Deploy.ps1 -SkipBuild   copy + register the existing bin output
      .\Deploy.ps1 -Force       proceed even if Altium Designer is running

    Modelled on the EasyEDA-Loader extension already installed on this machine.
#>
param(
    [switch]$SkipBuild,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$name = 'AltiumSpike'
$root = $PSScriptRoot
$outDir = Join-Path $root 'bin\Debug\net8.0-windows'

# --- 0. Altium must not be holding the DLL ---------------------------------
$running = Get-Process -Name 'X2' -ErrorAction SilentlyContinue
if ($running -and -not $Force) {
    throw "Altium Designer is running (PID $($running.Id -join ', ')). Close it and re-run, or pass -Force."
}

# --- 1. build ---------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Host "Building $name ..." -ForegroundColor Cyan
    & dotnet build (Join-Path $root "$name.csproj") -c Debug
    if ($LASTEXITCODE -ne 0) { throw "Build failed - nothing deployed." }
}

$dll = Join-Path $outDir "$name.dll"
if (-not (Test-Path $dll)) { throw "Not found: $dll   (re-run without -SkipBuild)" }

# --- 2. locate the Altium extensions folder ---------------------------------
$base = Join-Path $env:ProgramData 'Altium'
$installs = @(Get-ChildItem $base -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'Altium Designer*' -and $_.Name -notlike '*_Security' } |
    Where-Object { Test-Path (Join-Path $_.FullName 'Extensions') })

if ($installs.Count -eq 0) { throw "No Altium Designer installation found under $base" }
if ($installs.Count -gt 1) { Write-Host "Multiple installations found; using '$($installs[0].Name)'" -ForegroundColor Yellow }

$extRoot  = Join-Path $installs[0].FullName 'Extensions'
$deployTo = Join-Path $extRoot $name
$registry = Join-Path $extRoot 'ExtensionsRegistry.xml'
Write-Host "Extensions root: $extRoot"

# --- 3. copy ----------------------------------------------------------------
try {
    New-Item -ItemType Directory -Path $deployTo -Force | Out-Null
    # Copy the WHOLE build output, not a hand-picked list. A .NET 8 library
    # also emits AltiumSpike.deps.json, which a host that loads the assembly
    # through an AssemblyLoadContext may need in order to resolve it at all.
    foreach ($src in Get-ChildItem $outDir -File) {
        Copy-Item $src.FullName $deployTo -Force
        Write-Host "  copied $($src.Name)"
    }
    foreach ($f in @("$name.Ins", "$name.rcs")) {
        Copy-Item (Join-Path $root $f) $deployTo -Force; Write-Host "  copied $f"
    }
}
catch [System.UnauthorizedAccessException] {
    throw "Access denied writing to $deployTo - re-run this script from an elevated PowerShell (Run as Administrator)."
}

# --- 4. register ------------------------------------------------------------
if (-not (Test-Path $registry)) { throw "Registry not found: $registry" }
Copy-Item $registry "$registry.bak" -Force
Write-Host "  backed up ExtensionsRegistry.xml -> ExtensionsRegistry.xml.bak"

[xml]$xml = Get-Content $registry -Raw
$items = @($xml.DocumentElement.SelectNodes('Item'))

$existing = $items | Where-Object { $_.GetAttribute('HRID') -eq $name }
foreach ($e in $existing) {
    $xml.DocumentElement.RemoveChild($e) | Out-Null
    Write-Host "  replaced existing '$name' entry"
}

# inherit the platform build numbers already in the file so the entry is accepted
$dxp = '1.0.16.63'
$edp = '10.0.16.63'
$ref = $items | Where-Object { $_.SelectSingleNode('PlatformVersions/DXP') } | Select-Object -Last 1
if ($ref) {
    $dxp = $ref.SelectSingleNode('PlatformVersions/DXP').GetAttribute('BuildNumber')
    $edp = $ref.SelectSingleNode('PlatformVersions/EDP').GetAttribute('BuildNumber')
}
Write-Host "  platform: DXP $dxp / EDP $edp"

$serial = [string]([datetime]::Now.ToOADate())
$pathEsc = [System.Security.SecurityElement]::Escape($deployTo)

$frag = @"
<Item HRID="$name" Guid="3F6C1A94-7E52-4B0D-9C48-21A5F0D7E6B3">
    <Path>$pathEsc</Path>
    <Status>0</Status>
    <VaultGuid></VaultGuid>
    <CreatedBy>$env:USERNAME</CreatedBy>
    <CategoryGuid>793A1F67-0B22-4E01-A5DE-3176A1E8C60D</CategoryGuid>
    <CategoryName></CategoryName>
    <ReadMe></ReadMe>
    <Help></Help>
    <Requirements></Requirements>
    <Title>$name</Title>
    <ShortDescription>PCB automation feasibility spike</ShortDescription>
    <LongDescription>Three-stage probe of the Altium .NET SDK: PCBServer, current board, component iterator.</LongDescription>
    <SmallImage></SmallImage>
    <LargeImage></LargeImage>
    <Version>1.0.0.0</Version>
    <VersionGuid>8D21C7F0-4A63-4E19-B5C2-9F70E3A1D842</VersionGuid>
    <ReleasedDate>$serial</ReleasedDate>
    <ReleaseNotes></ReleaseNotes>
    <DateInstalled>$serial</DateInstalled>
    <PlatformVersions>
      <DXP BuildNumber="$dxp"/>
      <EDP BuildNumber="$edp"/>
      <MaxDXP BuildNumber="0.0.0.0"/>
      <MaxEDP BuildNumber="0.0.0.0"/>
    </PlatformVersions>
  </Item>
"@

$node = $xml.CreateDocumentFragment()
$node.InnerXml = $frag
$xml.DocumentElement.AppendChild($node) | Out-Null

try { $xml.Save($registry) }
catch [System.UnauthorizedAccessException] {
    throw "Access denied writing $registry - re-run from an elevated PowerShell (Run as Administrator)."
}

Write-Host ""
Write-Host "Deployed: $deployTo" -ForegroundColor Green
Write-Host "Next: start Altium, open a .PcbDoc, then Tools -> Run Altium Spike." -ForegroundColor Green
Write-Host "To undo: delete that folder and restore ExtensionsRegistry.xml.bak"
