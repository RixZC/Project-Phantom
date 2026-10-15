# Disturn

A Windows desktop audio mixer built with WinUI 3 and .NET 8.

Open `Disturn.slnx` in Visual Studio, or build the x64 configuration:

```powershell
dotnet restore Disturn.slnx -p:Platform=x64
dotnet build Disturn.slnx -p:Platform=x64
```

Existing user data remains at `%LOCALAPPDATA%\Project Phantom\groups.json` for compatibility. The legacy startup-task and single-instance IDs are intentionally retained. The MSIX package identity and publisher are unchanged.
