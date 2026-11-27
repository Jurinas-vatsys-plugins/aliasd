# aliasd

A [vatSys](https://virtualairtrafficsystem.com/) plugin that brings CRC / VRC style **text aliases** to vatSys message boxes, plus a **callsign box** that prefixes the selected aircraft's callsign to everything you send.

## Features

- **Alias expansion on Enter** – type `.rbc` and press Enter: it is replaced with the full text (not sent). Press Enter again to send.
- **Aliases anywhere in a message** – `AAL27 .rbc` → `AAL27 Readback correct.`
- **Unknown alias protection** – if any `.word` in the message is not a known alias, the message is **not sent**; the box turns red and an error is shown in the vatSys Error Window.
- **Arguments** – `.climb FL350` fills `$1` with `FL350`.
- **Live variables** – `$squawk`, `$arr`, `$dep`, `$route`, `$cruise`, `$alt`, `$calt`, `$callsign`, `$com1`, `$freq(XX)`, `$radioname(XX)`, `$uc()`, … are filled from vatSys.
- **Quick variable filling** – variables that cannot be filled are selected after expansion; type to replace, **Tab / Shift+Tab** to move to the next / previous one, **← / →** to jump between them.
- **Callsign box** – a small box left of the VSCS / chat message box:
  - follows the aircraft selected in vatSys;
  - with nothing selected, type a callsign in the message box and press **Tab** to move it into the box (a later selection overrides it);
  - every message you send is prefixed with it (`{cs}, {msg}` by default).
- Ships with the **ZLA** alias file as `Alias.txt`, and a documented `Alias.example.txt`.

## Installation

1. Download `aliasd-vX.Y.Z.zip` from [Releases](https://github.com/Jurinas-vatsys-plugins/aliasd/releases).
2. Extract the `aliasd` folder into **one** of:
   - `C:\Program Files (x86)\vatSys\bin\Plugins\`
   - `Documents\vatSys Files\Profiles\<your profile>\Plugins\`
3. Start vatSys. The plugin adds **Tools → Aliases** (alias count, *Reload Aliases*, *Open Plugin Folder*).

## Alias files

By default only `Alias.txt` in the plugin folder is read (file name is case-insensitive), so `.txt` files belonging to other plugins are never picked up. Replace it with your own file (CRC alias files from `%LOCALAPPDATA%\CRC\Aliases\` work as-is), or point to other files in `AliasPlugin.ini`:

```ini
AliasFile=%LOCALAPPDATA%\CRC\Aliases\ZLA.txt
AliasFolder=D:\MyAliases   ; reads Alias.txt in that folder
```

See [`Alias.example.txt`](Alias.example.txt) for the full syntax and the list of supported variables.

## Configuration (`AliasPlugin.ini`)

| Key | Default | Description |
|---|---|---|
| `AliasFile` | – | A specific alias file (repeatable) |
| `AliasFolder` | plugin folder | Folder containing `Alias.txt` (repeatable) |
| `TransitionAltitude` | `11000` | Altitudes at/above this are shown as `FLxxx` |
| `ChatWindowOnly` | `false` | `true` = only expand in message windows |
| `CallsignBox` | `true` | Show the callsign box |
| `CallsignBoxWindows` | `VSCSWindow,ChatWindow` | Windows that get a callsign box |
| `CallsignFormat` | `{cs}, {msg}` | How the callsign is prefixed |
| `Debug` | `false` | Log key handling to `Documents\vatSys Files\AliasPlugin_log.txt` |

## Building

Requires the .NET SDK and the .NET Framework 4.7.2 targeting pack.

```
dotnet build -c Release
```

The project references `vatSys.exe` from `C:\Program Files (x86)\vatSys\bin` when vatSys is installed, otherwise the API-only reference assemblies in `lib/` (generated with [Refasmer](https://github.com/JetBrains/Refasmer); they contain no vatSys code). Override with `-p:VatSysBin=<folder>`.

To regenerate `lib/` after a vatSys update:

```
dotnet tool install -g JetBrains.Refasmer.CliTool
refasmer -p --omit-non-api-members=true -O lib <copy of vatSys.exe> <copy of RossCarlson.Vatsim.Network.dll>
```

Every push to `main` builds the plugin and publishes a GitHub release, bumping the patch version (starting at `1.0.1`).

## License

GPL-3.0 – see [LICENSE](LICENSE).
