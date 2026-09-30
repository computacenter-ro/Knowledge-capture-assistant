<#
.SYNOPSIS
  Starts Foundry Local, makes sure an NPU model is loaded, then runs the Knowledge Capture app.
.EXAMPLE
  .\run.ps1                 # Debug build, native architecture
  .\run.ps1 -Release        # Release build
  .\run.ps1 -Model phi-4-mini
#>
param(
    [switch]$Release,
    [string]$Model = 'phi-4-mini',
    [string]$Device = 'npu'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Native architecture (x64 PowerShell on ARM64 reports AMD64, so ask the OS)
$os = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$platform = if ($os -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'ARM64' } else { 'x64' }
Write-Host "Platform: $platform ($os)" -ForegroundColor Cyan

if (-not (Get-Command foundry -ErrorAction SilentlyContinue)) {
    throw 'Foundry Local is not installed. Install it with: winget install Microsoft.FoundryLocal'
}

# 1. Start the local model service (Foundry 0.10+: "server"; older: "service")
Write-Host 'Starting Foundry Local...' -ForegroundColor Cyan
$null = foundry server start 2>&1
if ($LASTEXITCODE -ne 0) { $null = foundry service start 2>&1 }

# 2. Make sure an NPU model is loaded (the app refuses non-NPU models when Llm.Device = "npu")
$loaded = @()
try { $loaded = @((foundry model list --loaded -o json 2>$null | ConvertFrom-Json).models) } catch { }
$hit = $loaded | Where-Object { $_.device -eq $Device -and ($_.alias -eq $Model -or $_.id -like "*$Model*") } | Select-Object -First 1
if (-not $hit) {
    $cached = @()
    try { $cached = @((foundry model list --device $Device --cached -o json 2>$null | ConvertFrom-Json).models) } catch { }
    $variant = $cached | Where-Object { $_.alias -eq $Model -or $_.id -like "*$Model*" } | Select-Object -First 1
    if (-not $variant) {
        Write-Host "No cached $Device build of '$Model'. Available $Device models:" -ForegroundColor Yellow
        foundry model list --device $Device --variants
        throw "Download one first, e.g.: foundry model download <variant id>   (then re-run .\run.ps1)"
    }
    Write-Host "Loading $($variant.id) on the $Device (first load can take a minute)..." -ForegroundColor Cyan
    foundry model load $variant.id
    if ($LASTEXITCODE -ne 0) { throw "Foundry could not load $($variant.id) on the $Device. See: foundry server logs" }
} else {
    Write-Host "Model ready: $($hit.id) on $($hit.device)" -ForegroundColor Green
}

# 3. Build + run (the app discovers the dynamic Foundry port itself)
$config = if ($Release) { 'Release' } else { 'Debug' }
dotnet run --project App\App.csproj -c $config -p:Platform=$platform
