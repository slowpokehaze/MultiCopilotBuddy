# Сборка MCB.Agent.dll (headless-хост движка CopilotBuddy, integration-cb-into-mcb.md
# Этап B) без SDK — по рецепту build-manager.ps1.
#
# Отличия от менеджера:
#  - x86 строго (движок бота x86, инъекция в 32-битный Wow.exe);
#  - /target:exe — консольная подсистема (ручной прогон DoD смотрит в stdout);
#  - дополнительно референсится CopilotBuddy.dll из корня бота;
#  - результат копируется в КОРЕНЬ БОТА (рядом с CopilotBuddy.dll) — там его
#    ищет AgentLauncher, и оттуда же резолвятся все зависимости движка.
#
# Использование: powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-agent.ps1

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot   # MultiCopilotBuddy\
$botRoot = Split-Path -Parent $root           # Copilotbuddy 1.6.7.6\ (корень бота)
$scratch = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $scratch | Out-Null

$copilotDll = Join-Path $botRoot 'CopilotBuddy.dll'
if (-not (Test-Path $copilotDll)) { throw "CopilotBuddy.dll не найден: $copilotDll" }

$cscDll = @(
    (Join-Path $PSScriptRoot 'toolset\ext\tasks\netcore\bincore\csc.dll'),
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

# CopilotBuddy.dll первее всего: его типы (Styx/GreenMagic) могут конфликтовать
# с фасадами фреймворка — как WPF-фолдер в build-manager.ps1.
$refNames = @{ 'CopilotBuddy.dll' = $true }
$refs = @($copilotDll)
foreach ($dir in @($wpf, $netcore)) {
    foreach ($f in (Get-ChildItem $dir -Filter *.dll)) {
        if (-not $refNames.ContainsKey($f.Name) -and (Test-Managed $f.FullName)) {
            $refNames[$f.Name] = $true
            $refs += $f.FullName
        }
    }
}

$sources = Get-ChildItem (Join-Path $root 'src\MCB.Agent') -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$sources += Join-Path $PSScriptRoot 'GlobalUsings.cs'   # шим ImplicitUsings (как в build-manager.ps1)

$out = Join-Path $scratch 'MCB.Agent.dll'
if (Test-Path $out) { Remove-Item $out -Force }

$rsp = New-Object System.Collections.Generic.List[string]
$rsp.Add('-noconfig')
$rsp.Add('-target:exe')
$rsp.Add('-platform:x86')
$rsp.Add('-langversion:12')
$rsp.Add('-nullable:enable')
$rsp.Add('-out:"' + $out + '"')
$rsp.Add('-nologo')
foreach ($r in $refs)    { $rsp.Add('-reference:"' + $r + '"') }
foreach ($s in $sources) { $rsp.Add('"' + $s + '"') }
$rspPath = Join-Path $scratch 'csc-agent.rsp'
Set-Content -Path $rspPath -Value $rsp -Encoding UTF8

Write-Output ("refs: {0}, sources: {1}" -f $refs.Count, $sources.Count)
& $dotnet $cscDll "@$rspPath" 2>&1 | Where-Object { $_ -notmatch 'CS8632' } | Select-Object -First 60
if ($LASTEXITCODE -ne 0) { Write-Output "FAILED (exit $LASTEXITCODE)"; exit 1 }

# runtimeconfig — как у менеджера (WindowsDesktop, runtime машины 10.0.15).
$rc = Join-Path $scratch 'MCB.Agent.runtimeconfig.json'
@'
{
  "runtimeOptions": {
    "tfm": "net10.0-windows",
    "framework": {
      "name": "Microsoft.WindowsDesktop.App",
      "version": "10.0.15"
    }
  }
}
'@ | Set-Content -Path $rc -Encoding UTF8

# Деплой: агент обязан лежать в корне бота рядом с CopilotBuddy.dll.
Copy-Item $out $botRoot -Force
Copy-Item $rc  $botRoot -Force
Write-Output "OK: $out -> $botRoot"
