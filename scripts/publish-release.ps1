param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Message = "release: FootStep EQ"
)

$ErrorActionPreference = "Stop"

# Ensure MinGit and gh are on PATH even in fresh PowerShell windows
$minGitCmd = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "git.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like "*\cmd\git.exe" } | Select-Object -First 1 -ExpandProperty DirectoryName
if ($minGitCmd -and ($env:PATH -notlike "*$minGitCmd*")) {
    $env:PATH = "$minGitCmd;$env:PATH"
}
if ((Test-Path "C:\Program Files\GitHub CLI") -and ($env:PATH -notlike "*GitHub CLI*")) {
    $env:PATH = "C:\Program Files\GitHub CLI;$env:PATH"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# 1. Bump <Version> inside StrikeEQ.csproj
$csprojPath = Join-Path $repoRoot "src\StrikeEQ\StrikeEQ.csproj"
$csproj = Get-Content $csprojPath -Raw
$updated = [regex]::Replace($csproj, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
Set-Content -Path $csprojPath -Value $updated -NoNewline

Write-Host "Versão atualizada para v$Version em StrikeEQ.csproj" -ForegroundColor Cyan

# 2. Commit & Tag
git add .
git commit -m "$Message (v$Version)"
git tag -f "v$Version"

# 3. Push main + tag (triggers GitHub Actions automatic build & GitHub Release!)
git push origin main
git push origin "v$Version"

Write-Host ""
Write-Host "Pronto! Release v$Version disparada no GitHub Actions:" -ForegroundColor Green
Write-Host "https://github.com/santiniss/FootStep-EQ/releases" -ForegroundColor Yellow
