#requires -Version 5.1
<#
    Command Commander launcher (started by CommandCommander.bat).

    Builds CommandCommander.exe from CommandCommander.cs with the C# compiler
    that is built into Windows - only the first time, and again whenever the
    .cs file changes - and then starts it. The program asks for Administrator
    rights itself (one UAC prompt) and saves your commands to commands.json in
    this folder when you close it.

    After the first start you can also run CommandCommander.exe directly or pin
    it to the taskbar.
#>

Add-Type -AssemblyName System.Windows.Forms

function Show-Error([string]$Message) {
    [System.Windows.Forms.MessageBox]::Show($Message, 'Command Commander', 'OK', 'Error') | Out-Null
}

$source = Join-Path $PSScriptRoot 'CommandCommander.cs'
$exe    = Join-Path $PSScriptRoot 'CommandCommander.exe'

if (-not (Test-Path -LiteralPath $source)) {
    Show-Error "CommandCommander.cs was not found. Keep it in the same folder as this script:`n$PSScriptRoot"
    exit 1
}

$needsBuild = -not (Test-Path -LiteralPath $exe)
if (-not $needsBuild) {
    $needsBuild = (Get-Item -LiteralPath $exe).LastWriteTimeUtc -lt (Get-Item -LiteralPath $source).LastWriteTimeUtc
}

if ($needsBuild) {
    try {
        if (Test-Path -LiteralPath $exe) { Remove-Item -LiteralPath $exe -Force -ErrorAction Stop }
    } catch {
        Show-Error "Command Commander has been updated, but the old version is still open.`nClose Command Commander and start it again."
        exit 1
    }
    try {
        $code = Get-Content -LiteralPath $source -Raw -Encoding UTF8
        Add-Type -TypeDefinition $code -Language CSharp -IgnoreWarnings `
            -OutputAssembly $exe -OutputType WindowsApplication `
            -ReferencedAssemblies System.Windows.Forms, System.Drawing
    } catch {
        Show-Error "Command Commander could not be built.`n`n$($_ | Out-String)"
        exit 1
    }
}

try {
    Start-Process -FilePath $exe -Verb RunAs
} catch {
    # Most likely the UAC prompt was declined; nothing else to do.
}
