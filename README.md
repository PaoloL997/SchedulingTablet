# SchedulingTablet

WinUI tablet app for warehouse operations: activities, outgoing/incoming registration, inventory, component vision scanning, and Windows kiosk mode.

The published executable is `Brembana.exe`, matching the identity of existing tablet installs.

## Requirements

- Windows 10/11
- .NET 10 SDK
- Visual Studio 2022 (WinUI / Windows App SDK workload)

Open `Brembana.slnx` and run the `Brembana` project.

## Configuration

Copy `Brembana/Brembana/.env.example` to `.env` in the same folder and fill in the values (bridge, vision keys, kiosk PIN).

## Publish

```
dotnet publish "Brembana\Brembana\Brembana.csproj" -c Release -r win-x64 --self-contained true -o ".\publish\BrembanaTablet"
```
