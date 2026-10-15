[CmdletBinding()]
param(
    [string] $IsccPath,
    [switch] $PublishOnly
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts/publish/Disturn-win-x64'

Push-Location $repoRoot
try {
    & dotnet publish 'Disturn/Disturn.csproj' -c Release -r win-x64 -p:Platform=x64 -p:PublishProfile=Unpackaged-win-x64
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

    $required = @('Disturn.exe', 'Disturn.dll', 'Disturn.deps.json', 'Disturn.runtimeconfig.json',
        'Disturn.pri', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
        'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'NAudio.Wasapi.dll', 'Assets/Disturn.ico')
    foreach ($file in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir $file) -PathType Leaf)) {
            throw "Publish output is incomplete: $file"
        }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $publishDir 'Disturn.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (-not $runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.frameworks) {
        throw 'Expected a self-contained .NET runtime configuration.'
    }
    foreach ($name in @('System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault', 'System.Runtime.InteropServices.BuiltInComInterop.IsSupported')) {
        if ($runtime.runtimeOptions.configProperties.$name -ne $true) { throw "Required runtime switch is not enabled: $name" }
    }
    $files = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File)
    $unexpected = @($files | Where-Object { $_.Extension -in @('.cs', '.csproj', '.xaml', '.sln', '.slnx', '.msix', '.appx', '.msixbundle', '.appxbundle') -or $_.Name -eq 'groups.json' })
    if ($unexpected.Count) { throw 'Unexpected source, MSIX, or user-data files in publish output. Inspect the publish directory before packaging.' }
    $payload = @($files | Where-Object { $_.Extension -notin @('.pdb', '.ilk', '.ipdb', '.iobj', '.exp', '.lib') })
    Write-Host ('Installer payload: {0} files, {1:N1} MiB (before compression).' -f $payload.Count, (($payload | Measure-Object Length -Sum).Sum / 1MB))
    if ($PublishOnly) { return }

    if (-not $IsccPath) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $IsccPath = $command.Source }
        else {
            $candidates = @(
                "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
                "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
                "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe")
            $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        }
    }
    if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
        throw 'Inno Setup ISCC.exe was not found. Install Inno Setup 6.6+ or supply -IsccPath. Publish validation succeeded; no installer was compiled.'
    }
    Write-Host "Inno Setup compiler: $IsccPath"
    & $IsccPath (Join-Path $PSScriptRoot 'Disturn.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed (exit $LASTEXITCODE)." }
    Get-Item -LiteralPath (Join-Path $repoRoot 'artifacts/installer/Disturn-1.0.0-Setup-x64.exe') | Select-Object FullName, Length
}
finally { Pop-Location }
