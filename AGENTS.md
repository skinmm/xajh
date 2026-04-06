## Cursor Cloud specific instructions

### Project Overview

This is a .NET 8.0 C# console application (`xajh`) that targets **x86 Windows** (Win32 P/Invoke for process memory manipulation). It uses the `.slnx` solution format, which requires **.NET 9+ SDK** to parse.

### Build & Restore

- **Restore**: `dotnet restore xajh.slnx`
- **Build**: `dotnet build xajh.slnx`
- You can also target the project directly: `dotnet build xajh/xajh.csproj`

### Runtime Limitation

The application **cannot run on Linux**. It depends on Windows kernel32.dll P/Invoke calls (`OpenProcess`, `ReadProcessMemory`, `WriteProcessMemory`, `VirtualAllocEx`, `CreateRemoteThread`) and requires a running game process (`vrchat1`) to attach to. Running on Linux produces `System.IO.FileLoadException` because the x86 target platform is Windows-only. **Build verification is the maximum testable scope on Linux.**

### SDK Setup

The .NET SDK is installed to `~/.dotnet` via the official `dotnet-install.sh` script. The update script handles SDK installation automatically. Environment variables (`DOTNET_ROOT`, `PATH`) are configured in `~/.bashrc`.

### No Lint/Test Infrastructure

There are no linter configurations, test projects, or CI pipelines in this repository. Build success (0 errors) is the primary quality check. The codebase has nullable reference warnings (CS86xx) which are expected and present in the existing code.

### Key Files

| File | Purpose |
|------|---------|
| `xajh.slnx` | Solution file (requires .NET 9+ SDK) |
| `xajh/xajh.csproj` | Project file targeting `net8.0` / `x86` |
| `xajh/Program.cs` | Entry point |
| `xajh/XajhSmileDll.dll` | Pre-compiled native Windows DLL (checked into repo) |
