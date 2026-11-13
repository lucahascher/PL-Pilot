<#
.SYNOPSIS
  Builds PL/Pilot (technical name PlsqlDevAgent) into .\dist and creates .\dist\PLPilot.zip

  Requirements (build machine only): .NET SDK 6+ and Visual Studio / Build Tools with the C++ workload.
  The work PC only needs Windows 10/11 (.NET Framework 4.8 and WebView2 are part of Windows).
#>
param(
    [string]$Configuration = "Release",
    # Also copy the result into a local PL/SQL Developer PlugIns folder (for testing on this PC).
    [string]$DeployTo = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$pluginDir = Join-Path $dist "PlugIns"
$managedOut = Join-Path $pluginDir "PlsqlDevAgent"
$obj = Join-Path $root "obj\shim"

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $managedOut, $obj | Out-Null

# ---------------------------------------------------------------- 0. syntax check of the chat page script
# One syntax error (e.g. an unescaped ' in a translation) breaks the whole chat window; checked if node or bun exists.
$checker = Get-Command node, bun -ErrorAction SilentlyContinue | Select-Object -First 1
if ($checker) {
    Write-Host "== Checking the chat page script ($($checker.Name))" -ForegroundColor Cyan
    $html = [IO.File]::ReadAllText((Join-Path $root "src\PlsqlDevAgent.Core\UI\Web\index.html"), [Text.Encoding]::UTF8)
    $js = ([regex]::Matches($html, '(?s)<script>(.*?)</script>') | ForEach-Object { $_.Groups[1].Value }) -join "`n"
    $jsFile = Join-Path $obj "page-check.js"
    [IO.File]::WriteAllText($jsFile, $js, (New-Object Text.UTF8Encoding($false)))
    $ErrorActionPreference = "Continue"
    if ($checker.Name -like "node*") { & $checker.Source --check $jsFile 2>&1 | Write-Host }
    else { & $checker.Source build $jsFile --outfile (Join-Path $obj "page-check.out.js") 2>&1 | Out-Null }
    $ok = $LASTEXITCODE -eq 0
    $ErrorActionPreference = "Stop"
    if (-not $ok) { throw "JavaScript syntax error in src\PlsqlDevAgent.Core\UI\Web\index.html (check with: $($checker.Name) $jsFile)" }
} else {
    Write-Host "== (node/bun not found: chat page script not syntax-checked)" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- 1. managed part (.NET Framework 4.8)
Write-Host "== Building managed part" -ForegroundColor Cyan
$proj = Join-Path $root "src\PlsqlDevAgent.Core\PlsqlDevAgent.Core.csproj"
dotnet build $proj -c $Configuration -nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

$bin = Join-Path $root "src\PlsqlDevAgent.Core\bin\$Configuration\net48"
foreach ($f in "PlsqlDevAgent.Core.dll", "PlsqlDevAgent.Core.pdb", "Newtonsoft.Json.dll",
               "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll") {
    Copy-Item (Join-Path $bin $f) $managedOut
}
Copy-Item (Join-Path $bin "runtimes\win-x64\native\WebView2Loader.dll") $managedOut

# Company rules shipped with the plug-in: every *.md file in this folder is added to the system prompt.
$rulesDir = Join-Path $managedOut "rules"
New-Item -ItemType Directory -Force $rulesDir | Out-Null
@"
Company rules for PL/Pilot
==================================

Every *.md file in this folder is added to the agent's system prompt as mandatory rules
(coding and naming conventions, forbidden patterns, deployment rules ...).

Put your team's rules here before you distribute the plug-in, or point each user to a shared
file / folder with PL/Pilot Settings -> Company Rules (e.g. a network share).
Settings -> Company Rules -> Edit... creates a file from a template you can adapt.
"@ | Set-Content (Join-Path $rulesDir "README.txt") -Encoding UTF8

# ---------------------------------------------------------------- 2. native shim (x64, static CRT)
Write-Host "== Building native shim" -ForegroundColor Cyan
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found - install Visual Studio or the Build Tools with the C++ workload." }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "No Visual Studio installation with the C++ x64 tools found." }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"

$src = Join-Path $root "src\Shim\PlsqlDevAgentShim.cpp"
$rc = Join-Path $root "src\Shim\PlsqlDevAgentShim.rc"
$res = Join-Path $obj "PlsqlDevAgentShim.res"
$out = Join-Path $pluginDir "PlsqlDevAgent.dll"
# Linker flags as recommended by the PL/SQL Developer plug-in documentation.
$cl = "rc /nologo /fo `"$res`" `"$rc`" && cl /nologo /O2 /MT /LD /EHsc /W3 /DUNICODE /D_UNICODE `"$src`" `"$res`" /Fo`"$obj\\`" /Fe`"$out`" " +
      "/link /DYNAMICBASE:NO /NXCOMPAT:NO /FIXED:NO /MACHINE:X64"
$env:PATH = "$(Split-Path $vswhere);$env:PATH"
$ErrorActionPreference = "Continue"
cmd /c "`"$vcvars`" >nul 2>&1 && $cl 2>&1"
$ErrorActionPreference = "Stop"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $out)) { throw "Native shim build failed" }
Remove-Item (Join-Path $pluginDir "PlsqlDevAgent.exp"), (Join-Path $pluginDir "PlsqlDevAgent.lib") -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- 3. package
# install.ps1 contains German text: Windows PowerShell 5.1 needs a UTF-8 BOM to read it correctly
$installText = [IO.File]::ReadAllText((Join-Path $root "install.ps1"), [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $dist "install.ps1"), $installText, (New-Object Text.UTF8Encoding($true)))
Copy-Item (Join-Path $root "README.md"), (Join-Path $root "ANLEITUNG.md") $dist
$zip = Join-Path $dist "PLPilot.zip"
Compress-Archive -Path (Join-Path $dist "PlugIns"), (Join-Path $dist "install.ps1"), (Join-Path $dist "README.md"), (Join-Path $dist "ANLEITUNG.md") -DestinationPath $zip
Write-Host "== Done: $zip" -ForegroundColor Green

if ($DeployTo) {
    Write-Host "== Deploying to $DeployTo" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force (Join-Path $DeployTo "PlsqlDevAgent") | Out-Null
    Copy-Item $out $DeployTo -Force
    Copy-Item (Join-Path $managedOut "*") (Join-Path $DeployTo "PlsqlDevAgent") -Force -Recurse
}
