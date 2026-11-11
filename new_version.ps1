# Create a new version folder from an existing one.
#   Usage: new_version.bat V0.1 V0.2
# Copies <From> to <To>, renames files containing <From>, and replaces the
# version string inside .cs/.py/.bat/.md/.txt files (keeps UTF-8 BOM state).
param(
    [Parameter(Mandatory = $true)][string]$From,
    [Parameter(Mandatory = $true)][string]$To
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root $From
$dst = Join-Path $root $To
if (-not (Test-Path $src -PathType Container)) { throw "Source folder not found: $src" }
if (Test-Path $dst) { throw "Target folder already exists: $dst" }

Copy-Item $src $dst -Recurse
Get-ChildItem $dst -Recurse -Include *.exe, *.pdb, *.pyc -File | Remove-Item -Force
Get-ChildItem $dst -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force

Get-ChildItem $dst -Recurse -File | Where-Object { $_.Name -like "*$From*" } | ForEach-Object {
    Rename-Item $_.FullName ($_.Name.Replace($From, $To))
}

$exts = @('.cs', '.py', '.bat', '.md', '.txt')
Get-ChildItem $dst -Recurse -File | Where-Object { $exts -contains $_.Extension.ToLower() } | ForEach-Object {
    $bytes = [IO.File]::ReadAllBytes($_.FullName)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
    $new = $text.Replace($From, $To)
    if ($new -ne $text) {
        [IO.File]::WriteAllText($_.FullName, $new, (New-Object Text.UTF8Encoding($bom)))
    }
}
Write-Host "[OK] $To created from $From"
Write-Host "Next: edit files in $To, add an entry to CHANGELOG.md, then build with $To\build.bat"
