<#
    allow-firewall-release-folder.ps1

    Windows Firewall exceptions for running SDR-VST3-HL2 straight from the build
    output (Project Files\bin\<platform>\Release\Thetis.exe).

    Why this is needed:
    a firewall rule matches the *path* of the executable, not the product.  The
    installer creates the exceptions for the installed copy
    (C:\Program Files\OpenHPSDR\SDR-VST3-HL2\Thetis.exe), so running the build
    output has no exceptions at all.  The radio is then found and connected, the
    console stays alive (the display digits scroll) but the radio's samples never
    arrive: no audio and no waterfall.

    The script is idempotent - it removes any rule it previously created for the
    same path and recreates them.

    Run it from an elevated PowerShell (it will ask for elevation itself):

        powershell -ExecutionPolicy Bypass -File .\tools\allow-firewall-release-folder.ps1

    Pass -Platform x86 to open the 32 bit build output instead.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'

# ---- elevate if needed ------------------------------------------------------
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    Write-Host "Not running elevated - requesting it."
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $MyInvocation.MyCommand.Path), '-Platform', $Platform)
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args
    Write-Host "An elevated window was opened, the rules are being added there."
    return
}

# ---- work out the path of the build output ---------------------------------
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot ("Project Files\bin\{0}\Release\Thetis.exe" -f $Platform)

if (-not (Test-Path -LiteralPath $exe))
{
    Write-Warning ("Thetis.exe not found at '{0}'." -f $exe)
    Write-Warning "Build the solution first (Release|$Platform) and run this again."
    exit 1
}

Write-Host "Adding Windows Firewall exceptions for:"
Write-Host ("  {0}" -f $exe)

# ---- remove the rules we created earlier for the same path -----------------
$existing = Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {
    $rule = $_
    $app = $rule | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue
    $app -and $app.Program -eq $exe
}
if ($existing)
{
    Write-Host ("Removing {0} existing rule(s) for this path." -f @($existing).Count)
    $existing | Remove-NetFirewallRule -ErrorAction SilentlyContinue
}

# ---- create the rules ------------------------------------------------------
foreach ($protocol in @('TCP', 'UDP'))
{
    foreach ($direction in @('Inbound', 'Outbound'))
    {
        $suffix = if ($direction -eq 'Inbound') { 'In' } else { 'Out' }
        $displayName = "SDR-VST3-HL2 VST ($protocol $suffix)"

        New-NetFirewallRule -DisplayName $displayName `
            -Direction $direction `
            -Program $exe `
            -Protocol $protocol `
            -Action Allow `
            -Profile Any | Out-Null
    }
}

# ---- show what we ended up with --------------------------------------------
Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {
    $rule = $_
    $app = $rule | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue
    $app -and $app.Program -eq $exe
} | ForEach-Object {
    $app = $_ | Get-NetFirewallApplicationFilter
    "{0,-32} {1,-10} {2}" -f $_.DisplayName, $_.Direction, (Split-Path $app.Program -Leaf)
}

Write-Host "Done."
