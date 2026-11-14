# Собирает FolderSizePanel.exe компилятором из .NET Framework 4.x (есть в любой Windows 10/11).
# -Install: копирует в %LOCALAPPDATA%\Programs\FolderSizePanel и запускает (автозапуск программа включит сама).
# -Version: версия в свойствах exe и в «Установленных приложениях» (CI берёт её из тега windows-vX.Y).
param([switch]$Install, [string]$Version = '0.1')
$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$refs = '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:Microsoft.CSharp.dll'
$info = Join-Path $out 'AssemblyInfo.cs'
$v = [version]$Version
$v4 = '{0}.{1}.{2}.{3}' -f $v.Major, $v.Minor, [math]::Max($v.Build, 0), [math]::Max($v.Revision, 0)
@"
[assembly: System.Reflection.AssemblyTitle("Folder Size Panel")]
[assembly: System.Reflection.AssemblyProduct("Folder Size Panel")]
[assembly: System.Reflection.AssemblyVersion("$v4")]
[assembly: System.Reflection.AssemblyFileVersion("$v4")]
[assembly: System.Reflection.AssemblyInformationalVersion("$Version")]
"@ | Set-Content -Encoding UTF8 $info
$src = (Join-Path $PSScriptRoot 'src\Program.cs'), $info
& $csc /nologo /codepage:65001 /unsafe /target:winexe /optimize+ /platform:anycpu "/out:$out\FolderSizePanel.exe" $refs $src
if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась" }
# Консольная версия того же кода — для замеров: bin\bench.exe --bench <папка>
& $csc /nologo /codepage:65001 /unsafe /target:exe /optimize+ /platform:anycpu "/out:$out\bench.exe" $refs $src
if ($LASTEXITCODE -ne 0) { throw "Сборка bench не удалась" }
Write-Host "Собрано: $out\FolderSizePanel.exe"
# Установщик: FolderSizePanel.exe вшит ресурсом в FolderSizePanel-Setup.exe
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu "/out:$out\FolderSizePanel-Setup.exe" $refs "/resource:$out\FolderSizePanel.exe,FolderSizePanel.exe" (Join-Path $PSScriptRoot 'installer\Setup.cs') $info
if ($LASTEXITCODE -ne 0) { throw "Сборка установщика не удалась" }
Write-Host "Установщик: $out\FolderSizePanel-Setup.exe"

if ($Install) {
    $dest = Join-Path $env:LOCALAPPDATA 'Programs\FolderSizePanel'
    New-Item -ItemType Directory -Force $dest | Out-Null
    Get-Process FolderSizePanel -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    Copy-Item "$out\FolderSizePanel.exe" $dest -Force
    Start-Process (Join-Path $dest 'FolderSizePanel.exe')
    Write-Host "Установлено и запущено: $dest"
}
