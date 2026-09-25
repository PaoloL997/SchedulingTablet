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

## Starting and closing an activity

An activity **starts** only when every assigned component has been confirmed on the
way out, and **closes** only when every component has been confirmed back in: there
are no partial exits or returns. The direction comes from the server (`actual_start`
empty = uscita/avvio, otherwise rientro/chiusura).

The activity list has two levels. First come the **commesse**, showing only their
number, in ascending order. Tapping one shows its activities, ordered by ascending
planned start date (`start`, the "data di preventivo"; undated activities go last).
Each activity card shows only `{disegno}-{item}-{attività}`, with "Da avviare" /
"Da chiudere" on the same line. The back button returns to the commesse, and after
a scan session the app lands back in the same commessa.

Opening an activity goes straight to the scanning screen:

- **Top left**: back button.
- **Top right**: one chip per category with its own counter (`TESTA 1/1`,
  `MANDRINO 0/2`, …), amber once the category is done. Tapping a chip changes what to
  look for. "Elenco completo" opens a dropdown with every piece, its model or measure
  and its check mark (read-only).
- **Above the viewfinder**: the piece to scan now — category, `ID T144` and the
  planning requirement (`Ø min · puntuale · max` for mandrini and tastatori, `Passo`
  for maschere, `Modello` for teste, from `bore_min` / `bore_mid` / `bore_max` /
  `tube_pitch` / `modello_testa`). With several pieces in the category the title reads
  "1 di 2"; the active category moves on by itself once a category is complete.
- **Below the viewfinder**: the pieces already confirmed in that category.
- **Bottom**: "Annulla ultimo" (clears the last confirmation), the shutter and
  "Inserisci manualmente". Manual entry is hidden while the active category is testa or
  generatore, since those must be framed with the camera. A testa or generatore code
  typed while another category is active is also refused.

When the last piece is confirmed the screen asks "Tutti i N accessori sono usciti.
Avviare l'attività?" on the spot: confirming writes the local tracking file, pushes
`actual_start` / `actual_finish` to the bridge and returns to the activity list.
Declining keeps the camera open; leaving it then lands on the session summary page,
which still carries the "Avvia attività (6/6)" button and allows fixing a
confirmation by tapping a card. Leaving with an incomplete list drops the session and
goes back to the activity list.

### Scanning a compatible accessory

On the way out, a code that is not one of the assigned components but exists in the
inventory is checked with the bridge (`POST /api/bridge/attivita/<pk>/scan/`,
scheduling app):

- **Teste and generatori** always require the exact assigned piece.
- **Mandrini, tastatori and maschere** may be replaced by a piece with a compatible
  measure. Scanning only **asks** the bridge (`conferma: false`, no write) and shows the
  answer (`"Stai usando MND-117 al posto di MND-102, va bene?"`); the planned piece it
  replaces is the one the screen is currently asking for, so no extra question is
  needed. On confirm the swap is kept **on the tablet only** and the piece takes the
  planned one's place in the list, already confirmed. Refusals (out of tolerance, in use
  elsewhere, missing requirements) appear in the camera banner with the server's reason.
  "Annulla ultimo" undoes a pending swap completely, putting the planned piece back.

The planning is written **only when the activity is actually started**: on confirm the
queued substitutions are sent (`conferma: true`, one call each) before `actual_start`.
Any activity left without a spare is reported then. If the server refuses one at that
point (someone else took the piece in the meantime) the activity does not start, the
reason is shown and the session page stays open so the operator can fix it.

On the way back no substitution is offered: what returns is what went out, and an
unknown code is reported as not part of the activity.

Logic that does not touch the UI lives in `ActivityChecklistPolicy.cs` and
`ScanSubstitutionPolicy.cs` and is covered by `tools/Brembana.Tests`:

```
dotnet run --project tools/Brembana.Tests/Brembana.Tests.csproj
```

## Publish

```
dotnet publish "Brembana\Brembana\Brembana.csproj" -c Release -r win-x64 --self-contained true -o ".\publish\BrembanaTablet"
```
