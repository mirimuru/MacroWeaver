# MacroWeaver

MacroWeaver is a Windows desktop macro automation tool focused on a clear visual editor and highly customizable action sequences.

## Goals

- Build macros visually without requiring users to write scripts.
- Support keyboard input, mouse clicks, mouse movement, scrolling, and delays.
- Support nested repeat blocks, including fixed counts and infinite loops.
- Make actions easy to add, edit, duplicate, reorder, and remove.
- Save and load macros as JSON.
- Provide safe start, pause, resume, and stop controls.
- Investigate input modes that allow manual interaction while a macro is running; behavior depends on Windows and the target application.

## Technology

- C#
- WPF
- .NET 8 (Windows)
- JSON serialization with `System.Text.Json`

## Project status

Early development. The initial milestone is a usable visual sequence editor and a structured macro document model. OS-level input injection and recording will be added after the editor and execution model are established.

## Build

On Windows with the .NET 8 SDK installed:

```powershell
dotnet restore .\MacroWeaver.sln
dotnet build .\MacroWeaver.sln -c Debug
```

## Safety notes

- Stop execution with the in-app Stop button or configured emergency hotkey.
- Avoid running macros against applications where automation is prohibited.
- Background input is application-dependent and is not guaranteed to work with every game or elevated window.
