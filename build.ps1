param([string]$OutputPath = (Join-Path $PSScriptRoot 'downloads\zfl.exe'))
$ErrorActionPreference = 'Stop'
$desktopCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $desktopCompiler)) { throw 'Windows .NET Framework C# compiler was not found.' }
$desktopOutputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $desktopOutputDirectory -Force | Out-Null
& $desktopCompiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ "/out:$OutputPath" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll `
    "/resource:$PSScriptRoot\assets\sprites.png,Zfl.Sprites.png" `
    "/resource:$PSScriptRoot\assets\animations.json,Zfl.Animations.json" `
    "/resource:$PSScriptRoot\assets\pet.ico,Zfl.Pet.ico" `
    "/win32icon:$PSScriptRoot\assets\pet.ico" "/win32manifest:$PSScriptRoot\source\app.manifest" `
    "$PSScriptRoot\source\ZflDesktopPet.cs" "$PSScriptRoot\source\AssemblyInfo.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output $OutputPath
