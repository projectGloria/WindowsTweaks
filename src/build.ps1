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

$args = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/platform:anycpu',
    "/out:$outExe",
    "/win32icon:$icoPath",
    "/win32manifest:$manifest",
    "/resource:$icoPath,AppIcon.ico",
    "/reference:$wpfDir\PresentationFramework.dll",
    "/reference:$wpfDir\PresentationCore.dll",
    "/reference:$wpfDir\WindowsBase.dll",
    "/reference:$fxDir\System.Xaml.dll",
    "/reference:$fxDir\System.Xml.dll",
    "/reference:$fxDir\System.Core.dll",
    "/reference:$fxDir\System.dll",
    "/reference:$fxDir\System.Web.Extensions.dll",
    $srcFile
)

Write-Host "Compiling $outExe ..."
& $csc @args
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }

$fi = Get-Item $outExe
Write-Host ("Built successfully: {0} ({1:N1} KB)" -f $fi.FullName, ($fi.Length / 1KB))
