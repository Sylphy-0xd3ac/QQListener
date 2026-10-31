$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repoRoot 'artifacts/native-probe'
New-Item -ItemType Directory -Force -Path $output | Out-Null
dotnet run --project (Join-Path $PSScriptRoot 'QQListener.PackageBuilder') --configuration Release --no-build --no-restore -- --pmhq-fixture $repoRoot (Join-Path $output 'pmhq.dll')
if ($LASTEXITCODE -ne 0) { throw 'Failed to prepare the pinned native fixture' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC x64 tools were not found' }
$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$source = Join-Path $PSScriptRoot 'PMHQ.LocalSign/tests/tls_loader_probe.c'
$compile = Join-Path $output 'compile.cmd'
$exe = Join-Path $output 'tls-loader-probe.exe'
@"
@echo off
call "$vcvars"
if errorlevel 1 exit /b 1
cl /nologo /TC /std:c17 /Od /W3 "$source" /Fe:"$exe" /Fo:"$output/tls-loader-probe.obj"
"@ | Set-Content -Path $compile -Encoding ascii
& $env:ComSpec /c $compile
if ($LASTEXITCODE -ne 0) { throw 'Native fixture compilation failed' }
# The probe explicitly sets PMHQ_SRDI_TEST. It never attaches to QQ or installs live hooks.
& $exe (Join-Path $output 'pmhq.dll') 2>&1 | Tee-Object -FilePath (Join-Path $output 'result.txt')
if ($LASTEXITCODE -ne 0) { throw "PMHQ Windows loader/TLS probe failed: $LASTEXITCODE" }
