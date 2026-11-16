# Povratak na stabilnu verziju (faza A)

## Gde je paket

| | |
|---|---|
| Izdanje | GitHub → emilhadzic/lustre-radio → Releases → **„LUSTRE RADIO faza A 0.1.0“** (oznaka `faza-a-0.1.0`) |
| Fajlovi | `LustreRadio-faza-a-0.1.0.zip` (67.440.898 bajtova) i `LustreRadio-faza-a-0.1.0.zip.sha256.txt` |
| SHA-256 | `B3720562D8D462FBE759B28485D39673E7D650FBAFCC71144BF51173FD53E2B9` |
| Kod | izmena `bcb3eba` (grana `stabilno-faza-a`); izdanje je napravljeno iz grane `izdanje-faza-a`, koja dodaje samo postupak izdanja (proverava se u samom postupku) |
| Druga kopija | preporuka: ZIP sačuvati na računaru i na USB-u |

Izdanje je napravljeno i provereno 16.11.2026. na GitHub-u (pokretanje 43912076700): ZIP je skinut nazad, kontrolni zbir se slaže, a Engine i nadzor su pokrenuti iz raspakovanog foldera (u dnevniku „Baza: SQLite 3.53.3, sema 1“). Na računaru vlasnika: NOT TESTED. Na mini PC-ju: NOT TESTED – ACCEPTED RISK.

## Postupak

1. **Provera fajla** (PowerShell, u folderu gde je ZIP): `Get-FileHash .\LustreRadio-faza-a-0.1.0.zip`. Niz mora biti isti kao gore.
2. **Zaustavite novu verziju:** `ZAUSTAVI.cmd` iz foldera nove verzije.
3. **Raspakujte ZIP faze A u nov folder** (npr. `C:\LustreRadio-faza-A`), ne preko nove verzije.
4. **Baza**
   - Ako nova verzija bazu NIJE nadogradila (u njenom dnevniku: „sema 1“ ili „Baza NIJE nadogradjena“): bazu ne dirajte.
   - Ako jeste („sema 2“): pokrenite `VRATI-FAZU-A.cmd` iz foldera NOVE verzije. Skripta:
     - premešta `lustre.db`, `lustre.db-wal` i `lustre.db-shm` u `backups\pre-povratka-<vreme>` (ništa ne briše);
     - kopira najnoviji `backups\pre-sema2-….db` kao `lustre.db`;
     - premešta folder `journal` u `journal.novo-<vreme>`;
     - proverava da pored vraćene baze nije ostao stari `-wal` ili `-shm`.
     Isto može ručno, po `POVRATAK.txt` iz paketa faze A.
5. **Pokrenite fazu A:** `POKRENI.cmd` iz foldera faze A.
6. **Provera da je uspelo:**
   - `VERZIJA.txt` u folderu faze A kaže „faza A“;
   - u `C:\ProgramData\LustreRadio\logs\engine-<datum>.log` posle pokretanja piše „Baza: SQLite 3.53.3, sema 1“;
   - nema reda „Baza je iz novije verzije programa“;
   - pesme, PIN i izjava o licenci su tu; muzika svira.

**Šta se gubi:** istorija puštanja i izmene podešavanja nastale posle nadogradnje baze.

**Ako se preskoči korak 4 posle nadogradnje:** faza A odbija bazu porukom „Baza je iz novije verzije programa“ i ne svira. Ništa se ne kvari; uradite korak 4.
