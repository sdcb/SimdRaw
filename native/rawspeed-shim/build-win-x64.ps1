<#
.SYNOPSIS
  Builds rawspeed.dll (win-x64): RawSpeed + C ABI shim, static CRT, only KERNEL32.dll as dependency.

.DESCRIPTION
  RawSpeed does not build with cl.exe (GCC/Clang only), so this uses clang++ targeting the MSVC ABI
  inside a VS developer shell (MSVC headers/libs + static CRT), with pugixml/zlib/libjpeg-turbo from
  vcpkg's x64-windows-static triplet. OpenMP is off: LLVM for Windows only ships a DLL runtime.
#>
param(
    [string]$RawSpeedSrc = "$env:USERPROFILE\source\repos\rawspeed",
    [string]$LlvmRoot = "C:\_\3rd\llvm-23.1.2",
    [string]$VcpkgRoot = "C:\_\3rd\vcpkg",
    [string]$VsDevShell = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\Tools\Launch-VsDevShell.ps1",
    [string]$BuildDir = "$PSScriptRoot\..\..\artifacts\build\rawspeed-win-x64"
)
$ErrorActionPreference = 'Stop'

& $VsDevShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
& "$VcpkgRoot\vcpkg.exe" install pugixml zlib libjpeg-turbo --triplet x64-windows-static
if ($LASTEXITCODE) { throw "vcpkg install failed" }

cmake -G Ninja -S $PSScriptRoot -B $BuildDir `
    -DCMAKE_BUILD_TYPE=Release `
    "-DCMAKE_CXX_COMPILER=$LlvmRoot/bin/clang++.exe" `
    -DCMAKE_LINKER_TYPE=LLD `
    "-DCMAKE_TOOLCHAIN_FILE=$VcpkgRoot/scripts/buildsystems/vcpkg.cmake" `
    -DVCPKG_TARGET_TRIPLET=x64-windows-static `
    "-DRAWSPEED_SRC=$RawSpeedSrc"
if ($LASTEXITCODE) { throw "cmake configure failed" }
cmake --build $BuildDir
if ($LASTEXITCODE) { throw "cmake build failed" }

$dll = Join-Path $BuildDir 'out\rawspeed.dll'
dumpbin /nologo /dependents $dll | Select-String '\.dll' | Where-Object { $_ -notmatch 'Dump of file' }
Get-FileHash $dll, (Join-Path $BuildDir 'out\cameras.xml') -Algorithm SHA256 |
    ForEach-Object { "{0} {1,9} {2}" -f $_.Hash.ToLower(), (Get-Item $_.Path).Length, $_.Path }
