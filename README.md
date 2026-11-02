# MacroWeaver

MacroWeaver is a Windows desktop macro automation tool focused on a clear visual editor and highly customizable action sequences.

## Current features

- Visual action list with nested repeat groups.
- Keyboard key presses, mouse clicks, mouse movement, and delays.
- Fixed-count repeats and infinite repeats (0 = infinite).
- Add actions inside a selected repeat group, or use **最上位に追加** to add outside it.
- Reorder, duplicate, and delete actions within the same level.
- Save and load JSON macro files.
- Start, pause/resume, and stop controls.
- Legacy version 1.0 macro files are migrated on load.

## Current limitations

- Mouse-wheel scrolling and recording physical input are not implemented yet.
- Macro input uses normal Windows input injection. Manual input can conflict with macro mouse/keyboard actions.
- Background input to a specific window is not implemented and will not work reliably for every application or game.
- Hotkeys for start/stop are not implemented yet.
- Empty repeat groups are skipped during execution. Infinite loops should be tested carefully; use Stop to interrupt them.

## Technology

- C#
- WPF
- .NET 8 (Windows)
- JSON serialization with `System.Text.Json`

## Build

On Windows with the .NET 8 SDK installed:

```powershell
git pull origin main
dotnet restore .\MacroWeaver.sln
dotnet build .\MacroWeaver.sln -c Debug
dotnet run --project .\src\MacroWeaver\MacroWeaver.csproj
```

## Basic nested-repeat workflow

1. Select **繰り返し** and add it.
2. Select that repeat row, then add the actions that should run inside it.
3. To create a nested repeat, select a repeat row inside another repeat and add another **繰り返し**.
4. Enable **最上位に追加（繰り返しの外）** to add a root-level action instead.
5. Save the macro as JSON and test it in a safe application such as Notepad before using it elsewhere.

## Safety notes

- Start with a short macro in a safe application.
- Stop execution with the in-app Stop button.
- Avoid running macros against applications where automation is prohibited.
- The current implementation sends input to the active Windows session; it does not isolate manual input from macro input.
