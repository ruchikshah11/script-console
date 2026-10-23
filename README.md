# Script Console (C#)

Windows desktop app (WPF, .NET 10) to browse, fill in and run PowerShell scripts. It's the C# version
of the PowerShell Script Console (`C:\Ruchik\powershell-scripts\Utilities\DevTooling\ScriptConsole`),
with the same look, behaviour and saved values.

- **Folder page first:** suggested folders (recent, the script repo, QM scripts, ...) with their
  script counts, or type a path / Browse.
- **Left:** the folder as a tree with per-folder counts (a folder holding only a same-named script
  shows as that script), plus instant search.
- **Right:**
  - script card with Edit and Folder buttons
  - comments and help (Show full help adds parameter docs and examples)
  - parameter form: text boxes, `ValidateSet` dropdowns, checkboxes for switches, masked secrets,
    required fields marked and checked before running
  - command bar with the exact command (secrets masked), Extra args, engine and **Run**
- **Output here** streams the script's output into the built-in console: warnings yellow, verbose
  grey, errors red. Stop ends the script and its child processes. The script runs
  non-interactively, so pick **Console window** for scripts that ask for input. **History** lists
  every run; double-click one to see its output again.
- Remembers values per script in `%APPDATA%\ScriptConsole\state.json`, the same file the
  PowerShell version uses. Secrets are never saved. Runs are logged to `runs.log`, and unexpected
  errors to `errors.log`, in the same folder.

Shortcuts: Ctrl+F search, Ctrl+Enter run (or double-click a script), Ctrl+O change folder.

## Build and run
```powershell
dotnet build -c Release
.\bin\Release\net10.0-windows\ScriptConsole.exe                                # folder page
.\bin\Release\net10.0-windows\ScriptConsole.exe C:\Ruchik\powershell-scripts  # open a folder directly
.\bin\Release\net10.0-windows\ScriptConsole.exe --engine powershell           # default to Windows PowerShell 5.1
```
Requires the .NET 10 desktop runtime; no NuGet packages. Opening the 968-script repo takes about
40 ms; each script is read only when you select it.

## How scripts are read
`Core\ScriptParser.cs` reads comment-based help, leading comments, `#Requires -PSEdition` and the
`param()` block without running anything. It's a small lexer that skips strings, here-strings and
comments, and it follows PowerShell's rule that `param` must be the first statement. Checked
against PowerShell's own parser on all 968 scripts in the repo, it matches parameter names,
`Mandatory`, switches, arrays and `ValidateSet` values everywhere. It also shows `.SYNOPSIS` help in
106 scripts where PowerShell's stricter placement rules ignore the help block.

## Layout
```
Program.cs              entry point, --help, hidden --dump / --self-test
MainWindow.xaml(.cs)    the window: folder page, tree, details, parameter form, run + output
MainWindow.SelfTest.cs  --self-test <dir>: drives the window, saves screenshots and results.json
RunSession.cs           one run (process, output, History row), search/suggestion rows, tree node
Themes\Styles.xaml      buttons, inputs, dropdowns, tree, tabs
Core\ScriptParser.cs    help / param() reader
Core\ScriptScanner.cs   folder scan
Core\CommandBuilder.cs  command line + child process script (stream tagging for the output panel)
Core\StateStore.cs      shared state.json
```
