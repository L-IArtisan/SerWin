<#
.SYNOPSIS
  Generates Strings.resx (French, neutral language) and Strings.en.resx from a TSV table
  "key <TAB> french <TAB> english". Guarantees both languages have exactly the same keys.
  (ASCII-only script: Windows PowerShell 5.1 reads BOM-less files as ANSI.)
.EXAMPLE
  powershell -File tools\Build-Resx.ps1
#>
param(
    [string] $Tsv = '',
    [string] $OutDir = ''
)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Tsv) { $Tsv = Join-Path $root '..\src\SerWin.App\Resources\Strings.tsv' }
if (-not $OutDir) { $OutDir = Split-Path -Parent (Resolve-Path $Tsv) }

$header = @'
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
'@

function Escape([string] $s) { [System.Security.SecurityElement]::Escape($s.Replace('\n', "`n")) }

$fr = New-Object System.Text.StringBuilder; [void]$fr.AppendLine($header)
$en = New-Object System.Text.StringBuilder; [void]$en.AppendLine($header)
$seen = @{}
foreach ($line in [System.IO.File]::ReadAllLines((Resolve-Path $Tsv), [System.Text.Encoding]::UTF8)) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
    $parts = $line.Split("`t")
    if ($parts.Count -ne 3) { throw "Invalid line (3 columns expected): $line" }
    $key = $parts[0].Trim()
    if ($seen.ContainsKey($key)) { throw "Duplicate key: $key" }
    $seen[$key] = $true
    [void]$fr.AppendLine("  <data name=`"$key`" xml:space=`"preserve`"><value>$(Escape $parts[1])</value></data>")
    [void]$en.AppendLine("  <data name=`"$key`" xml:space=`"preserve`"><value>$(Escape $parts[2])</value></data>")
}
[void]$fr.AppendLine('</root>'); [void]$en.AppendLine('</root>')

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $OutDir 'Strings.resx'), $fr.ToString(), $utf8)
[System.IO.File]::WriteAllText((Join-Path $OutDir 'Strings.en.resx'), $en.ToString(), $utf8)
Write-Host "$($seen.Count) strings written to $OutDir"
