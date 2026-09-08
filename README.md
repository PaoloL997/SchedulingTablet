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

## Scanning a compatible accessory

During an activity session a scanned code is first matched against the assigned
components. When it is not one of them but exists in the inventory, the tablet asks
the bridge (`POST /api/bridge/attivita/<pk>/scan/`, scheduling app) whether it can be
used instead:

- **Teste and generatori** always require the exact assigned piece.
- **Mandrini, tastatori and maschere** may be replaced by a piece with a compatible
  measure. The server answers with the confirmation text
  (`"Stai usando MND-117 al posto di MND-102, va bene?"`); on confirm the planning
  assignment is updated, the piece appears selected in the session and, if it was
  assigned to another planned activity, that activity is re-equipped automatically or
  flagged in planning. Refusals (out of tolerance, in use elsewhere, missing
  requirements) are shown in the camera banner with the server's reason.

Logic that does not touch the UI lives in `ScanSubstitutionPolicy.cs` and is covered by
`tools/Brembana.Tests`:

```
dotnet run --project tools/Brembana.Tests/Brembana.Tests.csproj
```

## Publish

```
dotnet publish "Brembana\Brembana\Brembana.csproj" -c Release -r win-x64 --self-contained true -o ".\publish\BrembanaTablet"
```
