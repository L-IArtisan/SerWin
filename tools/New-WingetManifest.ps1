# Remplit les modeles winget de packaging/winget pour une version publiee.
# Usage : New-WingetManifest.ps1 -Version 1.0.0 [-Owner L-IArtisan] [-Sums SHA256SUMS.txt | -Exe chemin\SerWin.exe]
# Aucun acces reseau : l'empreinte vient de SHA256SUMS.txt (release) ou d'un exe local.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Owner = 'L-IArtisan',
    [string]$Sums,
    [string]$Exe
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$src = Join-Path $root 'packaging\winget'
$out = Join-Path $src "out\$Version"

if ($Exe) {
    $sha = (Get-FileHash $Exe -Algorithm SHA256).Hash
} elseif ($Sums) {
    $line = Get-Content $Sums | Where-Object { $_ -match "SerWin-v$([regex]::Escape($Version))-win-x64\.exe$" } | Select-Object -First 1
    if (-not $line) { throw "Aucune empreinte pour SerWin-v$Version-win-x64.exe dans $Sums" }
    $sha = ($line -split '\s+')[0]
} else {
    throw 'Indiquer -Sums (SHA256SUMS.txt de la release) ou -Exe (SerWin.exe publie).'
}

New-Item -ItemType Directory -Force $out | Out-Null
foreach ($f in Get-ChildItem $src -Filter 'LIArtisan.SerWin*.yaml') {
    $text = [IO.File]::ReadAllText($f.FullName)
    $text = $text.Replace('{VERSION}', $Version).Replace('L-IArtisan', $Owner).Replace('{SHA256}', $sha.ToUpperInvariant())
    [IO.File]::WriteAllText((Join-Path $out $f.Name), $text, (New-Object Text.UTF8Encoding $false))
}
Write-Output "Manifeste ecrit dans $out (SHA256 $sha)"
