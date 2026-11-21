# Сборка MCB.Cli.dll (Core + Cli) без SDK — рецепт build-manager.ps1.
# Результат сразу в build\ (CLI — рабочий инструмент, его пересобирают чаще всего).
#
# Использование: powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-cli.ps1

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'build'
New-Item -ItemType Directory -Force $build | Out-Null

$cscDll = @(
    (Join-Path $PSScriptRoot 'toolset\ext\tasks\netcore\bincore\csc.dll'),
    'C:\Users\User\AppData\Local\Temp\opencode\mcb-toolset\ext\tasks\netcore\bincore\csc.dll'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cscDll) { throw 'Roslyn-тулсет не найден — распакуй .nupkg в tools\toolset\' }
$dotnet = 'C:\Program Files (x86)\dotnet\dotnet.exe'

$netcore = 'C:\Program Files (x86)\dotnet\shared\Microsoft.NETCore.App\10.0.15'
$wpf     = 'C:\Program Files (x86)\dotnet\shared\Microsoft.WindowsDesktop.App\10.0.15'

function Test-Managed([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Position = 0x3C
        $peOff = $br.ReadInt32()
        if ($peOff -le 0 -or $peOff -gt $fs.Length - 6) { return $false }
        $fs.Position = $peOff
        if ($br.ReadUInt32() -ne 0x00004550) { return $false }
        $fs.Position = $peOff + 24
        $magic = $br.ReadUInt16()
        $ddOff = if ($magic -eq 0x20B) { $peOff + 24 + 112 } else { $peOff + 24 + 96 }
        $fs.Position = $ddOff + 14 * 8
        return ($br.ReadUInt32() -ne 0)
    } catch { return $false } finally { $fs.Dispose() }
}

$refNames = @{}
$refs = @()
foreach ($dir in @($wpf, $netcore)) {
    foreach ($f in (Get-ChildItem $dir -Filter *.dll)) {
        if (-not $refNames.ContainsKey($f.Name) -and (Test-Managed $f.FullName)) {
            $refNames[$f.Name] = $true
            $refs += $f.FullName
        }
    }
}

$sources = @()
$sources += Get-ChildItem (Join-Path $root 'src\MCB.Core') -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$sources += Get-ChildItem (Join-Path $root 'src\MCB.Cli') -Filter *.cs | ForEach-Object { $_.FullName }
$sources += Join-Path $PSScriptRoot 'GlobalUsings.cs'

$out = Join-Path $build 'MCB.Cli.dll'
if (Test-Path $out) { Remove-Item $out -Force }

$rsp = New-Object System.Collections.Generic.List[string]
$rsp.Add('-target:exe')
$rsp.Add('-langversion:12')
$rsp.Add('-out:"' + $out + '"')
$rsp.Add('-nologo')
foreach ($r in $refs)    { $rsp.Add('-reference:"' + $r + '"') }
foreach ($s in $sources) { $rsp.Add('"' + $s + '"') }
$rspPath = Join-Path $PSScriptRoot 'out\csc-cli.rsp'
New-Item -ItemType Directory -Force (Split-Path -Parent $rspPath) | Out-Null
Set-Content -Path $rspPath -Value $rsp -Encoding UTF8

Write-Output ("refs: {0}, sources: {1}" -f $refs.Count, $sources.Count)
& $dotnet $cscDll "@$rspPath" 2>&1 | Where-Object { $_ -notmatch 'CS8632' } | Select-Object -First 40
if ($LASTEXITCODE -eq 0) { Write-Output "OK: $out" } else { Write-Output "FAILED (exit $LASTEXITCODE)" }
