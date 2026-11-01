<#
.SYNOPSIS
    Prints the section of CHANGELOG.md for one version (without its heading), for release notes.
.EXAMPLE
    ./build/Get-ChangelogSection.ps1 -Version 0.9.0
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Path = (Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md')
)

$lines = Get-Content $Path
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match ('^## \[' + [regex]::Escape($Version) + '\]')) { $start = $i + 1; break }
}
if ($start -lt 0) { return }
$end = $lines.Count
for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## \[' -or $lines[$i] -match '^\[[^\]]+\]: ') { $end = $i; break }
}
($lines[$start..($end - 1)] -join "`n").Trim()
