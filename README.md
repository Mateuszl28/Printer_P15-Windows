# P15 Printer — sterownik .NET

Sterownik i aplikacja CLI w **.NET 8 (Windows)** dla drukarki etykiet
**Marklife / Pristar „P15 Mini"** komunikującej się przez **Bluetooth LE**.

Protokół odtworzony na podstawie reverse-engineeringu
(`tomLadder/thermoprint` → `REVERSE_ENGINEERING.md`,
`Defaezer/Label-printer-P15`). To drukarka klasy ESC/POS przez BLE
z kredytowym sterowaniem przepływem.

## Parametry BLE

| Element | UUID |
|---|---|
| Serwis | `0000ff00-0000-1000-8000-00805f9b34fb` |
| RX (notify, status) | `0000ff01-…` |
| TX (write, dane) | `0000ff02-…` |
| CX (kredyty przepływu) | `0000ff03-…` |

Pakiety on-air max **95 B**. Drukarka przydziela kredyty zapisu przez CX
(`[0x01, n]`), a status raportuje przez RX (`[0xFF, kod]` = błąd,
`0xAA/0x4F/0x4B` = OK).

## Sekwencja druku

```
wakeup     00×15
enable     10 FF F1 02
density    1F 70 02 <0-15>
raster     1D 76 30 00 xL xH yL yH <dane 1bpp MSB-first>
feed       1B 4A 64
stop       10 FF F1 45
```

## Budowanie

```powershell
dotnet build -c Release
```

## Użycie

```powershell
# tekst
dotnet run -- text "Etykieta nr 42"

# obraz (skalowany do szerokości głowicy, dithering Floyd–Steinberg)
dotnet run -- image logo.png

# wysuń etykietę
dotnet run -- feed
```

Opcje: `--width <dots>` (domyślnie 384), `--density <0-15>` (domyślnie 8),
`--no-dither`, `--name <filtr_nazwy_BLE>` (domyślnie `P15`).

> **Wymagania:** Windows 10/11 z Bluetooth LE; drukarka sparowana lub
> w zasięgu. Szerokość głowicy domyślnie 384 dots (≈48 mm, 203 dpi) —
> jeśli wydruk jest zwężony/rozciągnięty, dostrój `--width`.

## Struktura

| Plik | Rola |
|---|---|
| `P15Driver.cs` | sterownik BLE (połączenie, kredyty, komendy ESC/POS) |
| `ImageEncoder.cs` | obraz/tekst → raster 1-bit (próg + dithering) |
| `Program.cs` | aplikacja CLI |

## Użycie jako biblioteka

```csharp
await using var printer = await P15Driver.ConnectAsync("P15");
printer.StatusReported += s => Console.WriteLine(s.Describe());

var r = ImageEncoder.FromFile("etykieta.png", widthDots: 384);
await printer.PrintRasterAsync(r.Data, r.WidthBytes, r.Height, density: 8);
```
