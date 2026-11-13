# LUSTRE RADIO

Offline radio za Caffe Lustre (Novi Pazar). Windows 10/11, C# / .NET 10.

- Specifikacija: [docs/SPECIFIKACIJA_v4.1.md](docs/SPECIFIKACIJA_v4.1.md)
- Stanje razvoja: [docs/STATUS.md](docs/STATUS.md)

## Build i testovi (Windows, .NET 10 SDK)

    build.cmd
    test.cmd

## Struktura

- `src/LustreRadio.Core` – čista logika (raspored, rotacija, licence, pravila)
- `src/LustreRadio.Audio`, `.Analysis`, `.Data`, `.Library`, `.Ipc`, `.Voice`
- `src/LustreRadio.Engine` – audio proces bez prozora
- `src/LustreRadio.UI` – interfejs (WPF)
- `src/LustreRadio.Watchdog` – nadzor
- `src/LustreRadio.Analyzer`, `.Bench` – prenosni alati
- `tests/` – automatski testovi
