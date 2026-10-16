# Disturn

A Windows desktop audio mixer built with WinUI 3 and .NET 8.

Open `Disturn.slnx` in Visual Studio, or build the x64 configuration:

```powershell
dotnet restore Disturn.slnx -p:Platform=x64
dotnet build Disturn.slnx -p:Platform=x64
```

Existing user data remains at `%LOCALAPPDATA%\Project Phantom\groups.json` for compatibility. The legacy startup-task and single-instance IDs are intentionally retained. The MSIX package identity and publisher are unchanged.

## Licensing

Disturn is distributed under the [MIT License](LICENSE).
Copyright (c) 2026 RixZC.

Bundled third-party components retain their respective licenses. See
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and the original documents in
[ThirdPartyLicenses](ThirdPartyLicenses). These are included in published builds
and the installer; they are not replaced by Disturn's MIT License.

The notices identify pending redistribution review for Microsoft binary
components, including downstream-agreement requirements. License integration and
a successful build are not confirmation that those release obligations are met.
