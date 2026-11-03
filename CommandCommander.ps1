#requires -Version 5.1
<#
    Command Commander
    -----------------
    A small Windows GUI that stores the commands you use often and runs them
    in an Administrator Command Prompt with one click.

    Saved commands live in "commands.json" next to this script.
    Start it by double-clicking "CommandCommander.bat".
#>

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$AppName = 'Command Commander'

# ---------------------------------------------------------------------------
# Make sure we are running as Administrator. Anything started from an
# elevated process is elevated too, so every Command Prompt we open later
# will be an admin prompt (and you only see one UAC prompt per session).
# ---------------------------------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList (
            "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`"")
    } catch {
        [System.Windows.Forms.MessageBox]::Show(
            "$AppName needs Administrator rights so it can run commands as admin.`n`n$($_.Exception.Message)",
            $AppName, 'OK', 'Warning') | Out-Null
    }
    exit
}

# ---------------------------------------------------------------------------
# Saved commands
# ---------------------------------------------------------------------------
$DataFile = Join-Path $PSScriptRoot 'commands.json'
$Commands = New-Object System.Collections.ArrayList

function New-CommandEntry([string]$Name, [string]$Command) {
    [pscustomobject]@{ Name = $Name; Command = $Command }
}

function Save-Commands {
    $json = ConvertTo-Json -InputObject @($Commands.ToArray()) -Depth 3
    Set-Content -LiteralPath $DataFile -Value $json -Encoding UTF8
}

function Read-Commands {
    $Commands.Clear()

    if (-not (Test-Path -LiteralPath $DataFile)) {
        # First run: add a couple of examples so the list is not empty.
        [void]$Commands.Add((New-CommandEntry 'Flush DNS cache' 'ipconfig /flushdns'))
        [void]$Commands.Add((New-CommandEntry 'Check system files' 'sfc /scannow'))
        [void]$Commands.Add((New-CommandEntry 'Reset network (example of several lines)' (
            "netsh winsock reset`r`nnetsh int ip reset`r`nipconfig /release`r`nipconfig /renew")))
        Save-Commands
        return
    }

    try {
        $data = Get-Content -LiteralPath $DataFile -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($item in $data) {
            [void]$Commands.Add((New-CommandEntry ([string]$item.Name) ([string]$item.Command)))
        }
    } catch {
        # Keep a copy of the unreadable file so nothing is lost when we save again.
        $backup = "$DataFile.bak"
        Copy-Item -LiteralPath $DataFile -Destination $backup -Force
        [System.Windows.Forms.MessageBox]::Show(
            "Could not read $DataFile.`nA copy was saved as $backup.`n`n$($_.Exception.Message)",
            $AppName, 'OK', 'Error') | Out-Null
    }
}

# ---------------------------------------------------------------------------
# Running commands
# ---------------------------------------------------------------------------
function Invoke-InCommandPrompt([string]$Text, [bool]$KeepOpen) {
    # Every non-blank line is run in order, just like pasting them in one by one.
    $lines = @($Text -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
    if ($lines.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show('There is no command to run.', $AppName, 'OK', 'Information') | Out-Null
        return
    }

    $commandLine = $lines -join ' & '
    $mode = if ($KeepOpen) { '/k' } else { '/c' }

    # "/s /k "<text>"" makes cmd.exe strip only the outer quotes and run the
    # rest exactly as typed, so quotes and & inside the command keep working.
    Start-Process -FilePath $env:ComSpec -ArgumentList "/s $mode `"$commandLine`""
}

function Open-AdminPrompt {
    Start-Process -FilePath $env:ComSpec
}

# ---------------------------------------------------------------------------
# Window
# ---------------------------------------------------------------------------
[System.Windows.Forms.Application]::EnableVisualStyles()

$form = New-Object System.Windows.Forms.Form
$form.Text = "$AppName (Administrator)"
$form.ClientSize = New-Object System.Drawing.Size(760, 446)
$form.MinimumSize = New-Object System.Drawing.Size(776, 485)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$form.KeyPreview = $true

$anchorTL   = [System.Windows.Forms.AnchorStyles]'Top, Left'
$anchorTLR  = [System.Windows.Forms.AnchorStyles]'Top, Left, Right'
$anchorTBL  = [System.Windows.Forms.AnchorStyles]'Top, Bottom, Left'
$anchorAll  = [System.Windows.Forms.AnchorStyles]'Top, Bottom, Left, Right'
$anchorBL   = [System.Windows.Forms.AnchorStyles]'Bottom, Left'
$anchorBR   = [System.Windows.Forms.AnchorStyles]'Bottom, Right'

function Add-Control($control, [int]$x, [int]$y, [int]$w, [int]$h, $anchor) {
    $control.Location = New-Object System.Drawing.Point($x, $y)
    if ($w -gt 0) { $control.Size = New-Object System.Drawing.Size($w, $h) }
    $control.Anchor = $anchor
    $form.Controls.Add($control)
    $control
}

function New-Button([string]$text) {
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $text
    $b
}

# Left side: list of saved commands
$lblList = New-Object System.Windows.Forms.Label
$lblList.Text = 'Saved commands (double-click to run)'
$lblList.AutoSize = $true
[void](Add-Control $lblList 12 10 0 0 $anchorTL)

$list = New-Object System.Windows.Forms.ListBox
$list.IntegralHeight = $false
[void](Add-Control $list 12 30 230 336 $anchorTBL)

$btnUp   = Add-Control (New-Button 'Move up')   12  374 112 30 $anchorBL
$btnDown = Add-Control (New-Button 'Move down') 130 374 112 30 $anchorBL

# Right side: editor
$lblName = New-Object System.Windows.Forms.Label
$lblName.Text = 'Name'
$lblName.AutoSize = $true
[void](Add-Control $lblName 256 10 0 0 $anchorTL)

$txtName = New-Object System.Windows.Forms.TextBox
[void](Add-Control $txtName 256 30 492 23 $anchorTLR)

$lblCmd = New-Object System.Windows.Forms.Label
$lblCmd.Text = 'Command(s) - put each command on its own line; they run in order'
$lblCmd.AutoSize = $true
[void](Add-Control $lblCmd 256 62 0 0 $anchorTL)

$txtCmd = New-Object System.Windows.Forms.TextBox
$txtCmd.Multiline = $true
$txtCmd.AcceptsReturn = $true
$txtCmd.WordWrap = $false
$txtCmd.ScrollBars = 'Both'
$txtCmd.Font = New-Object System.Drawing.Font('Consolas', 10)
[void](Add-Control $txtCmd 256 82 492 254 $anchorAll)

$chkKeep = New-Object System.Windows.Forms.CheckBox
$chkKeep.Text = 'Keep the Command Prompt window open after running'
$chkKeep.AutoSize = $true
$chkKeep.Checked = $true
[void](Add-Control $chkKeep 256 344 0 0 $anchorBL)

$btnNew    = Add-Control (New-Button 'New')    256 374 80 30 $anchorBL
$btnSave   = Add-Control (New-Button 'Save')   342 374 80 30 $anchorBL
$btnDelete = Add-Control (New-Button 'Delete') 428 374 80 30 $anchorBL

$btnPrompt = Add-Control (New-Button 'Open admin prompt') 520 374 122 30 $anchorBR
$btnRun    = Add-Control (New-Button 'Run  (F5)')         648 374 100 30 $anchorBR
$btnRun.Font = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)

$status = New-Object System.Windows.Forms.StatusStrip
$statusLabel = New-Object System.Windows.Forms.ToolStripStatusLabel
$statusLabel.Text = "Running as Administrator   |   Saved in: $DataFile"
[void]$status.Items.Add($statusLabel)
$form.Controls.Add($status)

$tips = New-Object System.Windows.Forms.ToolTip
$tips.SetToolTip($btnRun, 'Run the command shown in the box (F5). Ctrl+S saves.')
$tips.SetToolTip($btnPrompt, 'Open an empty Administrator Command Prompt')
$tips.SetToolTip($btnNew, 'Clear the boxes to add a new command')

# ---------------------------------------------------------------------------
# Behaviour
# ---------------------------------------------------------------------------
function Update-List([int]$SelectIndex = -1) {
    $list.BeginUpdate()
    $list.Items.Clear()
    foreach ($c in $Commands) { [void]$list.Items.Add($c.Name) }
    $list.EndUpdate()
    if ($SelectIndex -ge 0 -and $SelectIndex -lt $list.Items.Count) {
        $list.SelectedIndex = $SelectIndex
    }
}

function Clear-Editor {
    $list.ClearSelected()
    $txtName.Text = ''
    $txtCmd.Text = ''
    $txtName.Focus() | Out-Null
}

function Save-Current {
    $name = $txtName.Text.Trim()
    $cmd  = $txtCmd.Text.Trim()
    if ($name -eq '' -or $cmd -eq '') {
        [System.Windows.Forms.MessageBox]::Show('Please enter both a name and a command.', $AppName, 'OK', 'Information') | Out-Null
        return
    }

    $index = $list.SelectedIndex
    $entry = New-CommandEntry $name $cmd
    if ($index -ge 0) {
        $Commands[$index] = $entry
    } else {
        $index = $Commands.Add($entry)
    }
    Save-Commands
    Update-List $index
}

function Remove-Current {
    $index = $list.SelectedIndex
    if ($index -lt 0) { return }
    $answer = [System.Windows.Forms.MessageBox]::Show(
        "Delete `"$($Commands[$index].Name)`"?", $AppName, 'YesNo', 'Question')
    if ($answer -ne 'Yes') { return }

    $Commands.RemoveAt($index)
    Save-Commands
    Update-List ([Math]::Min($index, $Commands.Count - 1))
    if ($Commands.Count -eq 0) { Clear-Editor }
}

function Move-Current([int]$Delta) {
    $i = $list.SelectedIndex
    $j = $i + $Delta
    if ($i -lt 0 -or $j -lt 0 -or $j -ge $Commands.Count) { return }
    $tmp = $Commands[$i]
    $Commands[$i] = $Commands[$j]
    $Commands[$j] = $tmp
    Save-Commands
    Update-List $j
}

function Invoke-Current {
    Invoke-InCommandPrompt $txtCmd.Text $chkKeep.Checked
}

$list.add_SelectedIndexChanged({
    $i = $list.SelectedIndex
    if ($i -ge 0) {
        $txtName.Text = $Commands[$i].Name
        $txtCmd.Text  = $Commands[$i].Command
    }
})
$list.add_DoubleClick({
    if ($list.SelectedIndex -ge 0) { Invoke-Current }
})

$btnNew.add_Click({ Clear-Editor })
$btnSave.add_Click({ Save-Current })
$btnDelete.add_Click({ Remove-Current })
$btnUp.add_Click({ Move-Current -1 })
$btnDown.add_Click({ Move-Current 1 })
$btnRun.add_Click({ Invoke-Current })
$btnPrompt.add_Click({ Open-AdminPrompt })

$form.add_KeyDown({
    param($source, $e)
    if ($e.KeyCode -eq 'F5') {
        Invoke-Current
        $e.Handled = $true
    } elseif ($e.Control -and $e.KeyCode -eq 'S') {
        Save-Current
        $e.Handled = $true
        $e.SuppressKeyPress = $true
    }
})

# ---------------------------------------------------------------------------
# Go
# ---------------------------------------------------------------------------
Read-Commands
Update-List 0
[void]$form.ShowDialog()
