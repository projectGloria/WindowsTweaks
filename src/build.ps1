# Builds standalone WindowsTweaks.exe with embedded Windows Tweaks.ico and UAC manifest
$ErrorActionPreference = 'Stop'

$rootDir  = Split-Path -Parent $PSScriptRoot
$csc      = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpfDir   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$fxDir    = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$icoPath  = Join-Path $rootDir 'Windows Tweaks.ico'
$outExe   = Join-Path $rootDir 'WindowsTweaks.exe'
$srcFile  = Join-Path $PSScriptRoot 'WindowsTweaks.cs'
$manifest = Join-Path $PSScriptRoot 'app.manifest'

if (-not (Test-Path $csc))     { throw "csc.exe not found at $csc" }
if (-not (Test-Path $icoPath)) { throw "Icon not found at $icoPath" }

$common = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/platform:anycpu',
    "/reference:$wpfDir\PresentationFramework.dll",
    "/reference:$wpfDir\PresentationCore.dll",
    "/reference:$wpfDir\WindowsBase.dll",
    "/reference:$fxDir\System.Xaml.dll",
    "/reference:$fxDir\System.Xml.dll",
    "/reference:$fxDir\System.Core.dll",
    "/reference:$fxDir\System.dll",
    "/reference:$fxDir\System.Web.Extensions.dll"
)

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('WindowsTweaks-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $helperExe = Join-Path $scratch 'CustomShortcutCreator.exe'
    $helperManifest = Join-Path $scratch 'CustomShortcutCreator.manifest'
    $helperManifestText = [IO.File]::ReadAllText($manifest).
        Replace('WindowsTweaks.app', 'WindowsTweaks.CustomShortcutCreator').
        Replace('requireAdministrator', 'asInvoker')
    $commonControls = @'
  <dependency>
    <dependentAssembly>
      <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*" />
    </dependentAssembly>
  </dependency>
</assembly>
'@
    $helperManifestText = $helperManifestText.Replace('</assembly>', $commonControls)
    [IO.File]::WriteAllText($helperManifest, $helperManifestText)
    $sharedSource = Join-Path $PSScriptRoot 'ShortcutIntegration.cs'
    $helperArgs = @(
        "/out:$helperExe", "/win32icon:$icoPath", "/win32manifest:$helperManifest",
        "/reference:$fxDir\System.Windows.Forms.dll",
        (Join-Path $PSScriptRoot 'CustomShortcutCreator.cs'), $sharedSource
    )
    & $csc @common @helperArgs
    if ($LASTEXITCODE -ne 0) { throw "Shortcut creator compilation failed with exit code $LASTEXITCODE" }

    $watermarkSource = Join-Path $PSScriptRoot 'WatermarkPatcher.cs'
    $watermarkExe = Join-Path $scratch 'WatermarkPatcher.exe'
    $watermarkManifest = Join-Path $scratch 'WatermarkPatcher.manifest'
    $watermarkManifestText = [IO.File]::ReadAllText($manifest).
        Replace('WindowsTweaks.app', 'WindowsTweaks.WatermarkPatcher').
        Replace('requireAdministrator', 'asInvoker')
    [IO.File]::WriteAllText($watermarkManifest, $watermarkManifestText)
    $watermarkArgs = @(
        '/nologo',
        '/target:winexe',
        '/optimize+',
        '/platform:x64',
        '/define:WATERMARK_CLI',
        "/out:$watermarkExe",
        "/win32manifest:$watermarkManifest",
        "/reference:$fxDir\System.dll",
        "/reference:$fxDir\System.Core.dll",
        $watermarkSource
    )
    & $csc @watermarkArgs
    if ($LASTEXITCODE -ne 0) { throw "WatermarkPatcher compilation failed with exit code $LASTEXITCODE" }

    $folderIconsSource = Join-Path $PSScriptRoot 'FolderIconApplier.cs'
    $mainArgs = @(
        "/out:$outExe", "/win32icon:$icoPath", "/win32manifest:$manifest",
        "/resource:$icoPath,AppIcon.ico", "/resource:$helperExe,CustomShortcutCreator.exe", "/resource:$watermarkExe,WatermarkPatcher.exe",
        $srcFile, $sharedSource, $folderIconsSource, $watermarkSource
    )
    Write-Host "Compiling $outExe ..."
    & $csc @common @mainArgs
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item -LiteralPath $helperExe, $helperManifest, $watermarkExe, $watermarkManifest -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $scratch -ErrorAction SilentlyContinue
}

$fi = Get-Item $outExe
Write-Host ("Built successfully: {0} ({1:N1} KB)" -f $fi.FullName, ($fi.Length / 1KB))
