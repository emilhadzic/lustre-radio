# LUSTRE RADIO – pravila rada u ovom repozitorijumu

- Specifikacija: `docs/SPECIFIKACIJA_v4.1.md` (odobrena 13.11.2026.). Nijedna funkcija se ne izbacuje bez odobrenja vlasnika.
- Sa vlasnikom se razgovara na srpskom (latinica), jednostavnim jezikom; on nije programer.
- Nikad ne tvrditi da je nešto testirano ako nije stvarno pokrenuto. Stanja: „prošao (gde, kada)“, „pao“, „napisano, nepokrenuto“.
- Build i automatski testovi: GitHub Actions (`.github/workflows/build.yml`), Windows. Na GitHub runner-u nema audio uređaja; testovi stvarnog zvuka idu na pravi računar.
- Pravila zavisnosti proverava `tests/LustreRadio.Architecture.Tests`. Mrežni kod sme samo u `LustreRadio.Voice`. Engine i Watchdog nikad ne koriste Voice.
- Engine je običan proces u korisničkoj sesiji, nikad Windows servis.
- Ciljni računar: mini PC, Celeron N4000, 4 GB RAM, Windows 11 Home, monitor na dodir, Bluetooth zvučnik, radi 24/7. Na njega ide samo instalacija.
- Stanje faza: vidi `docs/STATUS.md`.
