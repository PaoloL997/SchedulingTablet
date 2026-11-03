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
  `MANDRINO 0/2`, …), amber once the category is done. Tapping a chip changes what the
  screen shows. "Elenco completo" opens a dropdown with every piece, its model or
  measure and its check mark; substitutes follow the planned pieces, in amber.
- **Above the viewfinder**: what is still missing in the active category — the category
  with "ne mancano 2", the planned pieces not yet scanned (`ID C330 · C331`) and the
  planning requirement (`Ø min · puntuale · max` for mandrini and tastatori, `Passo`
  for maschere, `Modello` for teste, from `bore_min` / `bore_mid` / `bore_max` /
  `tube_pitch` / `modello_testa`). Pieces are scanned **in any order**; the active
  category moves on by itself once a category is complete.
- **Below the viewfinder**: the pieces already present in that category (planned in
  green, substitutes in amber).
- **Bottom**: "Annulla ultimo" (undoes the last scan; it can be pressed again), the
  shutter and "Inserisci manualmente". Manual entry is hidden while the active category
  is testa or generatore, since those must be framed with the camera. A testa or
  generatore code typed while another category is active is also refused.

Any scan can be undone, not only the last one: tapping a present piece below the
viewfinder, in "Elenco completo" or on the summary page asks "Togliere il pezzo?" and
then puts a planned piece back to unconfirmed or drops a substitute. A misread code
that fits several planned pieces (e.g. `C10` for C102 and C103) is not guessed: the
operator is asked to scan again.

When the last piece is confirmed the screen asks "Tutti i N accessori sono usciti.
Avviare l'attività?" on the spot, listing the substitutions that will be written
("C106 al posto di C330"): confirming pushes the date to the bridge
(`PUT /api/bridge/attivita/<pk>/update/`, body `{"actual_start": "<today>"}` on the
way out, `{"actual_finish": "<today>"}` on the way back, nothing else), then writes the
local tracking file and returns to the activity list. Declining keeps the camera open;
leaving it then lands on the session summary page, which still carries the "Avvia
attività (6/6)" button and allows fixing a confirmation by tapping a card. Leaving with
an incomplete list drops the session and goes back to the activity list.

The server answers the start in three ways:

- **200 with `warning_message`**: the activity started, but some pieces were planned for
  another activity in this period. The server reassigns or reschedules that activity by
  itself; the tablet shows `warning_message` (details from `warnings[]`: `tipo`,
  `risorsa_id`, `attivita_label`, `data_inizio`, `data_fine`) as a notice, not an
  error, and goes on.
- **409 with `conflicts`**: the activity did not start because a piece is in use in an
  activity already in progress. The tablet shows `error` (ready-made text, lines
  separated by `\n`) and asks for another piece; nothing changes locally and the
  session stays open.
- **409 with `missing_resources`**: the minimum kit is missing. The tablet shows `error`.

In every refusal the text is in `error`, never in `message`.

### Scanning on the way out

Every outgoing scan is checked with the bridge (`POST /api/bridge/attivita/<pk>/scan/`,
scheduling app, `conferma: false`), the planned piece included: a 409 with
`motivo: "occupata"` can arrive even for the expected piece when it is in use in an
activity in progress. The refusal is shown in the camera banner with the server's
`error` text plus "Usa un altro pezzo."; the piece stays unconfirmed. A 400 with
`motivo: "atteso"` means the planned piece is no longer assigned on the server: the
planning changed after the session opened, so the tablet reloads it (keeping what was
already scanned) and asks to scan the piece again. Without a connection no outgoing
scan is confirmed.

### Scanning a compatible accessory

On the way out, a code that is not one of the assigned components but exists in the
inventory goes through the same endpoint:

- **Teste and generatori** always require the exact assigned piece.
- **Mandrini, tastatori and maschere** may be replaced by a piece with a compatible
  measure. Each category has as many **slots** as planned pieces; a slot is filled by a
  confirmed planned piece or by a substitute. A substitute is **not tied to a planned
  piece** while scanning: compatibility depends on the activity requirement, not on the
  piece replaced, so the pairing is left for the start.
  - With a free slot, scanning only **asks** the bridge (`conferma: false`, no `atteso`,
    no write) and shows the answer (`"Stai usando MND-117 al posto di uno dei mandrini
    previsti, va bene?"`). On confirm the piece joins the list as a substitute, on the
    tablet only. Refusals (out of tolerance, in use elsewhere, missing requirements)
    appear in the camera banner with the server's reason.
  - With no free slot the substitute is refused on the tablet: "Mandrino: già 4 su 4.
    Togli un pezzo dalla lista per usare C106."
  - **Planned pieces win**: a planned piece scanned when the slots are full (because of
    a substitute) is confirmed and the last substitute leaves the list, with a notice.
    So scanning the original after a substitute never adds a fifth piece.
  - If the server answers `corrispondente` for a code the tablet does not have, the
    planning changed on the server: the tablet reloads it and confirms the piece as
    planned, never as an extra one.
  - An activity without pieces of that category takes the substitute as an addition.

The planning is written **only when the activity is actually started**: each
substitute, in scan order, takes the place of a planned piece of its category left
unconfirmed, in list order, and is sent with that piece as `atteso` (`conferma: true`,
one call each) before `actual_start`. Each written substitution is applied on the
tablet right away, so a refusal halfway never leaves the tablet behind the server. Any
activity left without a spare is reported then. If the server refuses one at that point
(someone else took the piece in the meantime) the activity does not start, the reason
is shown and the session page stays open so the operator can fix it.

On the way back no substitution is offered: what returns is what went out, and an
unknown code is reported as not part of the activity.

Logic that does not touch the UI lives in `ActivityChecklistPolicy.cs`,
`ScanSubstitutionPolicy.cs` and `ActivityScanSession.cs` (slots, substitutes, pairing,
undo, reload) and is covered by `tools/Brembana.Tests`:

```
dotnet run --project tools/Brembana.Tests/Brembana.Tests.csproj
```

## Publish

```
dotnet publish "Brembana\Brembana\Brembana.csproj" -c Release -r win-x64 --self-contained true -o ".\publish\BrembanaTablet"
```
