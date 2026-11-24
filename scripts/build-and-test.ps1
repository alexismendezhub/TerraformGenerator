# Terraform Generator - builds and runs ALL tests on Windows (PowerShell 7).
# The whole gate: the screen builds, the .NET solution builds with no warnings, every test passes.
# Usage (from the repo root): scripts\build-and-test.cmd
# With terraform installed, the tests also run terraform validate and fmt -check, the Bicep compiler (az bicep install)
# and the PowerShell parser on generated drawings.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$client = Join-Path $root 'src\terraformgenerator.client'

Push-Location $client
try {
    if (-not (Test-Path 'node_modules')) { npm ci --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' } }
    npm audit --audit-level=low; if ($LASTEXITCODE -ne 0) { throw 'npm audit found vulnerabilities' }
    npm run build; if ($LASTEXITCODE -ne 0) { throw 'The screen did not build' }
} finally { Pop-Location }

if (Get-Command terraform -ErrorAction SilentlyContinue) {
    $env:TERRAFORMGENERATOR_TERRAFORM = '1'
} else {
    Write-Warning 'Terraform is not installed: the terraform validate tests do nothing.'
}

dotnet test (Join-Path $root 'TerraformGenerator.slnx') -nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
Write-Host 'All checks passed.'
