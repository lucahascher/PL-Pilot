<#
.SYNOPSIS
  Installs PL/Pilot for the current user. No administrator rights required.
  Installiert PL/Pilot für den aktuellen Benutzer. Keine Administratorrechte nötig.

  Copies the plug-in to %LOCALAPPDATA%\PlsqlDevAgent\PlugIns. PL/SQL Developer then has to be pointed
  to that folder once: Configure > Preferences > Options > "Plug-Ins" directory (preference PlugInDir).
  If the PL/SQL Developer PlugIns folder itself is writable, you can use -Target to install there instead.

  Run (from the extracted zip folder):
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param(
    [string]$Target = (Join-Path $env:LOCALAPPDATA "PlsqlDevAgent\PlugIns")
)

$ErrorActionPreference = "Stop"
$de = (Get-UICulture).TwoLetterISOLanguageName -eq "de"
function Say([string]$en, [string]$german) { if ($de) { $german } else { $en } }

$source = Join-Path $PSScriptRoot "PlugIns"
if (-not (Test-Path (Join-Path $source "PlsqlDevAgent.dll"))) {
    throw (Say "PlugIns\PlsqlDevAgent.dll not found next to install.ps1" "PlugIns\PlsqlDevAgent.dll wurde neben install.ps1 nicht gefunden")
}

$running = Get-Process plsqldev -ErrorAction SilentlyContinue
if ($running) {
    Write-Warning (Say "PL/SQL Developer is running. Close it first, otherwise the files are locked." `
                       "PL/SQL Developer läuft. Bitte zuerst schließen, sonst sind die Dateien gesperrt.")
}

New-Item -ItemType Directory -Force (Join-Path $Target "PlsqlDevAgent") | Out-Null
Copy-Item (Join-Path $source "PlsqlDevAgent.dll") $Target -Force
Copy-Item (Join-Path $source "PlsqlDevAgent\*") (Join-Path $Target "PlsqlDevAgent") -Force -Recurse

# Files downloaded from the internet / mail are marked as "blocked"; .NET refuses to load blocked DLLs.
Get-ChildItem $Target -Recurse -File | Unblock-File

Write-Host ""
Write-Host ((Say "Installed to: " "Installiert nach: ") + $Target) -ForegroundColor Green
Write-Host ""
if ($de) {
    Write-Host "Nächste Schritte:"
    Write-Host "  1. PL/SQL Developer starten."
    Write-Host "  2. Konfigurieren > Einstellungen > Optionen (bzw. 'Verzeichnisse'): Plug-Ins-Verzeichnis setzen auf:"
    Write-Host "       $Target"
    Write-Host "     (entfällt, wenn direkt in den PlugIns-Ordner von PL/SQL Developer installiert wurde)"
    Write-Host "  3. PL/SQL Developer neu starten. Unter Konfigurieren > Plug-Ins erscheint 'PL/Pilot'."
    Write-Host "  4. Register Plug-Ins > PL-Pilot > Einstellungen: URL des LLM-Servers eintragen."
    Write-Host ""
    Write-Host "Ausführliche Anleitung: ANLEITUNG.md"
} else {
    Write-Host "Next steps:"
    Write-Host "  1. Start PL/SQL Developer."
    Write-Host "  2. Configure > Preferences > Options (or 'Directories'): set the Plug-Ins directory to:"
    Write-Host "       $Target"
    Write-Host "     (skip this if you installed directly into PL/SQL Developer's own PlugIns folder)"
    Write-Host "  3. Restart PL/SQL Developer. Configure > Plug-Ins should list 'PL/Pilot'."
    Write-Host "  4. Plug-Ins tab > PL-Pilot > Settings: enter the URL of your LLM server."
}
