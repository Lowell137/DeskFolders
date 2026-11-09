# Builds DeskFolders and the installer.
#   1) src\DeskFolders  -> dist\  (WinUI 3, self-contained, x64)
#   2) src\Trigger      -> dist\Tetik.exe (tiny .NET Framework helper used by the desktop "New > App folder" menu)
#   3) installer\DeskFolders.iss -> out\DeskFolders-Setup-<version>.exe (Inno Setup 6)
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

dotnet publish "$root\src\DeskFolders\GrupPopup.csproj" -c Release -p:Platform=x64 -o $dist | Select-Object -Last 2
if ($LASTEXITCODE) { throw 'DeskFolders build failed' }

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /platform:x64 /target:winexe /optimize+ /out:"$dist\Tetik.exe" "$root\src\Trigger\Tetik.cs"
if ($LASTEXITCODE) { throw 'Trigger build failed' }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found' }
& $iscc /Q "$root\installer\DeskFolders.iss"
if ($LASTEXITCODE) { throw 'Installer build failed' }
Get-ChildItem "$root\out\*.exe" | ForEach-Object { "$($_.Name)  $([math]::Round($_.Length / 1MB, 1)) MB" }
