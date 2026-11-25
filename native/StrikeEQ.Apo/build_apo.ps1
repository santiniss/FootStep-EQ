param(
    [string]$OutputPath = "..\..\publish\FootStepEQ.Apo.dll"
)

$ErrorActionPreference = "Stop"

# Locate clang++ / g++ in PATH or WinGet Packages
$compiler = Get-Command "x86_64-w64-mingw32-clang++" -ErrorAction SilentlyContinue
if (-not $compiler) {
    $compiler = Get-Command "clang++" -ErrorAction SilentlyContinue
}
if (-not $compiler) {
    $found = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "x86_64-w64-mingw32-clang++.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $compiler = $found.FullName }
}
if (-not $compiler) {
    throw "No MinGW-w64 / LLVM-MinGW C++ compiler found. Install with: winget install MartinStorsjo.LLVM-MinGW.UCRT"
}

$exe = if ($compiler -is [string]) { $compiler } else { $compiler.Source }
$srcDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outFull = [System.IO.Path]::GetFullPath((Join-Path $srcDir $OutputPath))
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outFull) | Out-Null

Write-Host "Compiling StrikeEQ.Apo.dll with $exe..."
& $exe -std=c++20 -O2 -shared -static -static-libgcc -static-libstdc++ `
    -DUNICODE -D_UNICODE `
    (Join-Path $srcDir "StrikeEqApo.cpp") `
    (Join-Path $srcDir "StrikeEQ.Apo.def") `
    -o $outFull `
    -lole32 -ladvapi32 -luuid

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Get-Item $outFull | Select-Object FullName, Length, LastWriteTime | Format-List
