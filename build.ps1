# Builds dist\UsbShareMonitor.exe
# Target: .NET Framework 4.8 (built into Windows 10/11, nothing extra to install on the PC that runs it).
# Needs the Roslyn C# compiler, which ships with Visual Studio / "Build Tools for Visual Studio".
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    foreach ($vs in (& $vswhere -all -products * -property installationPath)) {
        $candidate = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe'
        if (Test-Path $candidate) { $csc = $candidate; break }
    }
}
if (-not $csc) { throw 'Roslyn csc.exe not found. Install "Build Tools for Visual Studio" (.NET desktop build tools).' }

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll' |
    ForEach-Object { '/reference:' + (Join-Path $fw $_) }
$common = '/nologo', '/noconfig', '/langversion:9.0', '/codepage:65001', '/utf8output', '/optimize+'

$obj = Join-Path $root 'obj'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $obj, $dist | Out-Null

# 1) Render the application icon
& $csc @common /target:exe "/out:$obj\IconGen.exe" @refs "$root\tools\IconGen.cs" "$root\src\AppIcon.cs"
if ($LASTEXITCODE) { throw 'IconGen build failed' }
& "$obj\IconGen.exe" "$obj\app.ico"
if ($LASTEXITCODE) { throw 'Icon generation failed' }

# 2) Build the app
& $csc @common /target:winexe /platform:anycpu "/win32icon:$obj\app.ico" "/win32manifest:$root\src\app.manifest" `
    "/out:$dist\UsbShareMonitor.exe" @refs "$root\src\*.cs"
if ($LASTEXITCODE) { throw 'Build failed' }

Write-Host "OK: $dist\UsbShareMonitor.exe"
