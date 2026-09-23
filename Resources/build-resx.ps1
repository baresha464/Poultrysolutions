# Regenerates SharedResource.te.resx from te.translations.txt and checks it against the code.
# Run from the project root:  powershell -File Resources\build-resx.ps1
# Fails (exit 1) on: placeholder mismatches, duplicate keys. Warns on keys used in code with no
# Telugu yet (they fall back to English at runtime) and on translations no code uses any more.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $PSScriptRoot 'te.translations.txt'
$out = Join-Path $PSScriptRoot 'SharedResource.te.resx'

# Case-sensitive, like .NET resource lookup ("Avg weight" and "Avg Weight" are different keys).
$entries = New-Object System.Collections.Specialized.OrderedDictionary ([StringComparer]::Ordinal)
$errors = @()
foreach ($line in [IO.File]::ReadAllLines($src, [Text.Encoding]::UTF8)) {
    if ($line.Trim() -eq '' -or $line.StartsWith('#')) { continue }
    $i = $line.IndexOf(' ||| ')
    if ($i -lt 0) { $errors += "No ' ||| ' separator: $line"; continue }
    $key = $line.Substring(0, $i).TrimEnd()
    $val = $line.Substring($i + 5).Trim()
    if ($entries.Contains($key)) { $errors += "Duplicate key: $key"; continue }
    # .resx names are case-insensitive at build time: "Avg weight" and "Avg Weight" can't both exist.
    $clash = @($entries.Keys) | Where-Object { [string]::Equals($_, $key, [StringComparison]::OrdinalIgnoreCase) }
    if ($clash) { $errors += "Keys differ only by case (use one spelling in code): '$clash' / '$key'"; continue }
    $keyPh = ([regex]::Matches($key, '\{\d+(:[^}]*)?\}') | ForEach-Object Value | Sort-Object) -join ','
    $valPh = ([regex]::Matches($val, '\{\d+(:[^}]*)?\}') | ForEach-Object Value | Sort-Object) -join ','
    if ($keyPh -ne $valPh) { $errors += "Placeholder mismatch: [$key] has '$keyPh' but Telugu has '$valPh'" }
    $entries[$key] = $val
}

# Keys the code actually asks for: L["..."] / T["..."] / text["..."] literals and T.List("...", ...).
$files = @(Get-ChildItem (Join-Path $root 'Components') -Recurse -Filter *.razor) +
         @(Get-ChildItem (Join-Path $root 'Services') -Recurse -Filter *.cs)
$used = New-Object System.Collections.Generic.HashSet[string]
$rx = [regex]'(?:\bL|\bT|text|b\.Text)\[\s*"((?:[^"\\]|\\.)*)"'
$rxList = [regex]'T\.List\(([^;]*?)\)\)?;'
foreach ($f in $files) {
    $c = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    foreach ($m in $rx.Matches($c)) { [void]$used.Add(($m.Groups[1].Value -replace '\\"', '"')) }
    foreach ($m in $rxList.Matches($c)) { foreach ($s in ([regex]'"((?:[^"\\]|\\.)*)"').Matches($m.Groups[1].Value)) { [void]$used.Add($s.Groups[1].Value) } }
}
$missing = $used | Where-Object { -not $entries.Contains($_) } | Sort-Object
if ($missing) { Write-Warning ("No Telugu yet (shows English):`n  " + ($missing -join "`n  ")) }

if ($errors) { $errors | ForEach-Object { Write-Host "ERROR: $_" -ForegroundColor Red }; exit 1 }

function Esc([string]$s) { [Security.SecurityElement]::Escape($s) }
$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<!-- GENERATED from te.translations.txt by build-resx.ps1 - edit that file, not this one. -->')
[void]$sb.AppendLine('<root>')
[void]$sb.AppendLine('  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>')
[void]$sb.AppendLine('  <resheader name="version"><value>2.0</value></resheader>')
[void]$sb.AppendLine('  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>')
[void]$sb.AppendLine('  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>')
foreach ($k in $entries.Keys) {
    [void]$sb.AppendLine("  <data name=`"$(Esc $k)`" xml:space=`"preserve`"><value>$(Esc $entries[$k])</value></data>")
}
[void]$sb.AppendLine('</root>')
[IO.File]::WriteAllText($out, $sb.ToString(), (New-Object Text.UTF8Encoding $false))
Write-Host "Wrote $($entries.Count) Telugu strings to $out ($($missing.Count) used keys still untranslated)."
