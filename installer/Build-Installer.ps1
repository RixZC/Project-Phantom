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
    # Ask MSBuild for evaluated values, including SDK-derived numeric file version.
    # GetAssemblyVersion computes metadata without building or modifying source files.
    $versionOutput = & dotnet msbuild 'Disturn/Disturn.csproj' -nologo -verbosity:quiet `
        -p:Configuration=Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
        -p:PublishProfile=Unpackaged-win-x64 -target:GetAssemblyVersion '-getProperty:Version,FileVersion'
    if ($LASTEXITCODE -ne 0) { throw "Project version query failed (exit $LASTEXITCODE)." }
    $versionProperties = (($versionOutput -join [Environment]::NewLine) | ConvertFrom-Json).Properties
    $appVersion = [string] $versionProperties.Version
    $appFileVersion = [string] $versionProperties.FileVersion
    if ($appVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' -or
        $appFileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw 'Expected a major.minor.patch project Version and a four-part numeric FileVersion.'
    }
    Write-Host "Disturn version: $appVersion (Windows file version $appFileVersion)"

    & dotnet publish 'Disturn/Disturn.csproj' -c Release -r win-x64 -p:Platform=x64 -p:PublishProfile=Unpackaged-win-x64
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

    $required = @('Disturn.exe', 'Disturn.dll', 'Disturn.deps.json', 'Disturn.runtimeconfig.json',
        'LICENSE', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt', 'ThirdPartyLicenses/manifest.json', 'Disturn.pri', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
        'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'NAudio.Wasapi.dll', 'Assets/Disturn.ico')
    foreach ($file in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir $file) -PathType Leaf)) {
            throw "Publish output is incomplete: $file"
        }
    }
    foreach ($binary in @('Disturn.exe', 'Disturn.dll')) {
        $metadata = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDir $binary))
        if ($metadata.FileVersion -ne $appFileVersion -or $metadata.ProductVersion -ne $appVersion) {
            throw "Published $binary version does not match the project. Expected $appVersion / $appFileVersion."
        }
    }
    foreach ($relative in @('LICENSE', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt') + @(
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'ThirdPartyLicenses') -Recurse -File |
            ForEach-Object { $_.FullName.Substring($repoRoot.Length + 1) })) {
        $source = Join-Path $repoRoot $relative
        $published = Join-Path $publishDir $relative
        if (-not (Test-Path -LiteralPath $published -PathType Leaf) -or
            (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $published).Hash) {
            throw "Published license/notice is missing or changed: $relative"
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
    & $IsccPath "/DAppVersion=$appVersion" "/DAppFileVersion=$appFileVersion" (Join-Path $PSScriptRoot 'Disturn.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed (exit $LASTEXITCODE)." }
    $installerPath = Join-Path $repoRoot "artifacts/installer/Disturn-$appVersion-Setup-x64.exe"
    $installerMetadata = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installerPath)
    # Inno pads textual resources with spaces; compare numeric fields independently.
    $installerFileVersion = '{0}.{1}.{2}.{3}' -f $installerMetadata.FileMajorPart,
        $installerMetadata.FileMinorPart, $installerMetadata.FileBuildPart, $installerMetadata.FilePrivatePart
    $installerProductVersion = '{0}.{1}.{2}.{3}' -f $installerMetadata.ProductMajorPart,
        $installerMetadata.ProductMinorPart, $installerMetadata.ProductBuildPart, $installerMetadata.ProductPrivatePart
    if ($installerFileVersion -ne $appFileVersion -or $installerProductVersion -ne $appFileVersion -or
        $installerMetadata.ProductVersion.Trim() -ne $appVersion) {
        throw 'Installer version metadata does not match the project.'
    }
    Get-Item -LiteralPath $installerPath | Select-Object FullName, Length
}
finally { Pop-Location }

