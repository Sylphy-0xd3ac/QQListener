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
& $exe (Join-Path $output 'upstream.dll') 2>&1 | Tee-Object -FilePath (Join-Path $output 'result.txt')
if ($LASTEXITCODE -ne 0) { throw "Official upstream DLL loader probe failed: $LASTEXITCODE" }
$legacy = & $exe (Join-Path $output 'legacy.dll') 2>&1
$legacy | Tee-Object -Append -FilePath (Join-Path $output 'result.txt')
if ($LASTEXITCODE -ne 4 -or ($legacy -join ' ') -notmatch 'LoadLibraryW failed=193') { throw 'Expected the legacy PE layout to fail with Win32 193' }
& $exe (Join-Path $output 'pmhq.dll') 2>&1 | Tee-Object -Append -FilePath (Join-Path $output 'result.txt')
if ($LASTEXITCODE -ne 0) { throw "PMHQ Windows loader/TLS probe failed: $LASTEXITCODE" }
$env:QQLISTENER_PMHQ_FIXTURE = Join-Path $output 'pmhq.dll'
dotnet test (Join-Path $repoRoot 'Tests/QQListener.Protocol.Tests') --configuration Release --no-build --no-restore --filter FullyQualifiedName~WindowsLoadsPinnedPmhqThroughTheProductionRemoteCallPath --logger 'console;verbosity=detailed' 2>&1 | Tee-Object -Append -FilePath (Join-Path $output 'result.txt')
if ($LASTEXITCODE -ne 0) { throw "PMHQ production remote loader probe failed: $LASTEXITCODE" }
