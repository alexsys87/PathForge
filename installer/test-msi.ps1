#Requires -Version 7.0
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Msi,
    [Parameter(Mandatory)] [string] $BaselineMsi,
    [switch] $AllowMachineChanges
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or -not $AllowMachineChanges) {
    throw 'Run on a disposable Windows machine with -AllowMachineChanges.'
}
$repoRoot = Split-Path $PSScriptRoot -Parent
$Msi = [IO.Path]::GetFullPath($Msi, $repoRoot)
$BaselineMsi = [IO.Path]::GetFullPath($BaselineMsi, $repoRoot)
$registryPath = 'HKLM:\SOFTWARE\alexsys87\PathForge'
if (Test-Path $registryPath) { throw 'PathForge is already installed; refusing to modify it.' }
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'PathForge.lnk'
if (Test-Path $shortcut) { throw 'A PathForge shortcut already exists; refusing to modify it.' }
$installDir = Join-Path $env:ProgramFiles ("PathForge MSI Verification " + [guid]::NewGuid().ToString('N'))
$logs = Join-Path $repoRoot 'artifacts/msi-logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$userDataDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'PathForge'
$hadUserDataDir = Test-Path $userDataDir
New-Item -ItemType Directory -Force $userDataDir | Out-Null
$sentinel = Join-Path $userDataDir ("msi-test-" + [guid]::NewGuid().ToString('N') + '.txt')
Set-Content $sentinel 'User data must survive MSI removal.'

function Invoke-Msi {
    param([string] $Arguments, [string] $LogName, [int[]] $AllowedCodes = @(0, 3010))
    $log = Join-Path $logs "$LogName.log"
    $process = Start-Process msiexec.exe -ArgumentList "$Arguments /qn /norestart /L*v `"$log`"" -Wait -PassThru
    if ($process.ExitCode -notin $AllowedCodes) {
        throw "msiexec $LogName failed: $($process.ExitCode); see $log"
    }
    return $process.ExitCode
}
function Assert-Installed {
    if (-not (Test-Path $registryPath)) { throw 'Install-location registry value is missing.' }
    $actual = (Get-ItemProperty $registryPath).InstallDir.TrimEnd('\')
    if ($actual -ine $installDir.TrimEnd('\')) { throw "Installation path changed: $actual" }
    if (-not (Test-Path $shortcut -PathType Leaf)) { throw 'Start menu shortcut is missing.' }
    $publishDir = Join-Path $repoRoot 'artifacts/publish/win-x64'
    foreach ($source in (Get-ChildItem $publishDir -Recurse -File)) {
        $relative = [IO.Path]::GetRelativePath($publishDir, $source.FullName)
        $installed = Join-Path $installDir $relative
        if (-not (Test-Path $installed -PathType Leaf)) { throw "Missing installed file: $relative" }
        if ((Get-FileHash $source.FullName).Hash -ne (Get-FileHash $installed).Hash) {
            throw "Installed file differs: $relative"
        }
    }
    if (-not (Test-Path $sentinel)) { throw 'User data was removed.' }
}
try {
    [void] (Invoke-Msi "/i `"$BaselineMsi`" INSTALLFOLDER=`"$installDir`"" 'install-baseline')
    Assert-Installed
    # Deliberately omit INSTALLFOLDER: the upgrade must retain the custom path.
    [void] (Invoke-Msi "/i `"$Msi`"" 'upgrade')
    Assert-Installed
    [void] (Invoke-Msi "/i `"$BaselineMsi`"" 'blocked-downgrade' @(1603))
    Assert-Installed
    Remove-Item (Join-Path $installDir 'PathForge.dll') -Force
    [void] (Invoke-Msi "/fa `"$Msi`"" 'repair')
    Assert-Installed
    [void] (Invoke-Msi "/x `"$Msi`"" 'uninstall')
    if (Test-Path $shortcut) { throw 'Shortcut was not removed.' }
    if (Test-Path $registryPath) { throw 'Install-location registration was not removed.' }
    if (Test-Path (Join-Path $installDir 'PathForge.exe')) { throw 'Application was not removed.' }
    if (-not (Test-Path $sentinel)) { throw 'Uninstall removed user data.' }
    # Fresh install, independently of the upgrade path.
    [void] (Invoke-Msi "/i `"$Msi`" INSTALLFOLDER=`"$installDir`"" 'fresh-install')
    Assert-Installed
    [void] (Invoke-Msi "/x `"$Msi`"" 'fresh-uninstall')
    Write-Host 'MSI installation, repair, upgrade, downgrade blocking and uninstall passed.'
} finally {
    # /x on an absent product returns 1605; do not mask the original test failure.
    foreach ($package in @($Msi, $BaselineMsi)) {
        try { [void] (Invoke-Msi "/x `"$package`"" ('cleanup-' + [IO.Path]::GetFileNameWithoutExtension($package)) @(0, 3010, 1605)) }
        catch { Write-Warning $_ }
    }
    Remove-Item $sentinel -Force -ErrorAction SilentlyContinue
    if (-not $hadUserDataDir -and (Test-Path $userDataDir) -and @(Get-ChildItem $userDataDir -Force).Count -eq 0) {
        Remove-Item $userDataDir -Force
    }
}
