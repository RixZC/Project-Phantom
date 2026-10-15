[CmdletBinding()]
param([string] $IsccPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $IsccPath = $command.Source }
    else {
        $IsccPath = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe"
        ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $IsccPath) { throw 'Inno Setup is unavailable; the shared-code registry fixture tests could not run.' }
& $IsccPath (Join-Path $PSScriptRoot 'StartupCleanup.Tests.iss')
if ($LASTEXITCODE -ne 0) { throw "Fixture compilation failed (exit $LASTEXITCODE)." }
$exe = Join-Path $repoRoot 'artifacts/installer-tests/StartupCleanup.Tests.exe'
$results = "$exe.results.txt"
if (Test-Path -LiteralPath $results) { Remove-Item -LiteralPath $results }
$fixtureId = [Guid]::NewGuid().ToString('N')
$log = Join-Path $repoRoot 'artifacts/installer-tests/StartupCleanup.Tests.log'
# InitializeSetup returns False deliberately, so inspect its explicit result rather
# than treating Setup's cancellation exit code as a failed test.
Start-Process -FilePath $exe -WindowStyle Hidden -Wait -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', "/FixtureId=$fixtureId", "/LOG=`"$log`"")
if (-not (Test-Path -LiteralPath $results)) { throw 'Fixture executable did not produce a result. Inspect the test log.' }
$report = Get-Content -LiteralPath $results -Raw
if (-not $report.StartsWith('PASS:')) { throw $report }
Write-Host $report
