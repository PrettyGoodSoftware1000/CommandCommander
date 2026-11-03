# Command Commander

A small Windows program for the commands you keep copying and pasting into
Command Prompt. Save them once, then click **Run** to run them in an
**Administrator** Command Prompt.

Nothing to install: it uses PowerShell and Windows Forms, which come with Windows 10 and 11.

## Getting started

1. Put `CommandCommander.bat` and `CommandCommander.ps1` in the same folder, for
   example `C:\Tools\CommandCommander`.
2. Double-click **`CommandCommander.bat`**.
3. Click **Yes** when Windows asks for Administrator permission (UAC). You see
   that prompt once each time you start the program, not once per command.

Tip: right-click the `.bat` file and choose **Send to > Desktop (create shortcut)**
for a desktop icon.

## Using it

| To...                      | Do this                                                                   |
|----------------------------|---------------------------------------------------------------------------|
| Add a command              | Click **New**, type a name and the command, click **Save** (or Ctrl+S)    |
| Run a saved command        | Double-click it in the list, or select it and click **Run** (or F5)       |
| Run something once         | Type it in the command box and click **Run**; you don't have to save it   |
| Change a command           | Select it, edit it, click **Save**                                        |
| Delete or reorder          | Select it and use **Delete**, **Move up**, **Move down**                  |
| Get an empty admin prompt  | Click **Open admin prompt**                                               |

- **Several commands at once:** put each command on its own line. They run one
  after another in a single Command Prompt window, as if you had pasted them in
  one at a time.
- **Keep the Command Prompt window open after running:** leave this ticked to
  read the output. Untick it for commands where you don't need to see the result;
  the window then closes as soon as they finish.

Your commands are saved in `commands.json` next to the script. Copy that file to
back up your list or move it to another PC.

## How it works

`CommandCommander.bat` starts `CommandCommander.ps1` with PowerShell. The script
restarts itself as Administrator. Every Command Prompt it opens is therefore also an
Administrator prompt. Each **Run** opens a new `cmd.exe` window with
your command. The program doesn't type into a Command Prompt that is already
open. Windows blocks that for admin windows, and it would be unreliable anyway.

## Troubleshooting

- **Nothing happens / window flashes and closes:** make sure both files are in
  the same folder and that you clicked **Yes** on the UAC prompt.
- **"running scripts is disabled on this system":** start the program with the
  `.bat` file, not by double-clicking the `.ps1`. The `.bat` file allows this one
  script to run without changing your system settings.
- **Windows SmartScreen / "Unblock":** if you downloaded the files, right-click
  `CommandCommander.ps1`, choose **Properties**, tick **Unblock** and click **OK**.
