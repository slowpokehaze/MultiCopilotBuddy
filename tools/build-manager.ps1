# Сборка MCB.Manager.dll без SDK (Roslyn-тулсет microsoft.net.compilers.toolset,
# csc.dll под dotnet-хост). Рецепт: план.md §Этап 0, README §«Сборка (без SDK)».
#
# Использование:  powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-manager.ps1
# Результат:      tools\out\MCB.Manager.dll → вручную (или скриптом) копировать в build\.
#
# Нюансы, найденные в бою:
#  - ссылаться нужно на RUNTIME-сборки shared-фреймворков (ref-паков на машине нет),
#    включая System.Private.CoreLib (.NET 10 WPF форвардит базовые типы в него);
#  - native-DLL (wpfgfx_cor3, coreclr, clrjit, hostpolicy, *Native* …) фильтруются
#    по PE-заголовку (нет CLR-каталога) — иначе CS0009;
#  - WindowsBase.dll лежит в ОБОИХ фреймворках: копия из NETCore.App — пустой фасад
#    (CS7069 «DependencyObject … не найден»), поэтому WPF-фолдер в refs идёт первым;
#  - ImplicitUsings не работает без SDK → шим tools\GlobalUsings.cs.

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot   # MultiCopilotBuddy\
$scratch = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $scratch | Out-Null

$cscDll = @(
    # локально распакованный тулсет (если есть)
    (Join-Path $PSScriptRoot 'toolset\ext\tasks\netcore\bincore\csc.dll'),
    # temp-кэш прошлой сборки (живёт, пока не чистили temp)
    'C:\Users\User\AppData\Local\Temp\opencode\mcb-toolset\ext\tasks\netcore\bincore\csc.dll'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cscDll) { throw 'Roslyn-тулсет (microsoft.net.compilers.toolset, tasks/netcore/bincore/csc.dll) не найден — распакуй .nupkg в tools\toolset\' }
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
$sources += Get-ChildItem (Join-Path $root 'src\MCB.Manager') -Filter *.cs | ForEach-Object { $_.FullName }
$sources += Join-Path $PSScriptRoot 'GlobalUsings.cs'

$out = Join-Path $scratch 'MCB.Manager.dll'
if (Test-Path $out) { Remove-Item $out -Force }

$rsp = New-Object System.Collections.Generic.List[string]
$rsp.Add('-target:winexe')
$rsp.Add('-langversion:12')
$rsp.Add('-out:"' + $out + '"')
$rsp.Add('-nologo')
foreach ($r in $refs)    { $rsp.Add('-reference:"' + $r + '"') }
foreach ($s in $sources) { $rsp.Add('"' + $s + '"') }
$rspPath = Join-Path $scratch 'csc.rsp'
Set-Content -Path $rspPath -Value $rsp -Encoding UTF8

Write-Output ("refs: {0}, sources: {1}" -f $refs.Count, $sources.Count)
& $dotnet $cscDll "@$rspPath" 2>&1 | Where-Object { $_ -notmatch 'CS8632' } | Select-Object -First 40
if ($LASTEXITCODE -eq 0) { Write-Output "OK: $out" } else { Write-Output "FAILED (exit $LASTEXITCODE)" }
