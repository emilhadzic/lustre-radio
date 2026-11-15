# LUSTRE RADIO – Konačna tehnička specifikacija v4.1

**Projekat:** LUSTRE RADIO · **Namena:** Caffe Lustre, Novi Pazar · **Platforma:** Windows 11 (10 podržan) · **Režim rada:** 100% offline tokom emitovanja
**Datum:** 13.11.2026. · **Status:** **odobrena od vlasnika 13.11.2026.**; dopune posle odobrenja su u delu 27

Ovaj dokument zamenjuje specifikaciju v3.1, arhitektonsku reviziju i v4. Izmene u odnosu na v4 su u delu 24, provera kontradikcija u delu 25, a spisak provera pre početka rada u delu 26. Objedinjuje: master prompt (15 zahteva), dopune v2, v3 (jezici, LUSTRE VOICE), v3.1 (ciljni računar), reviziju Claude Opus 5.5, analizu Claude Fable 5.2 i odluke vlasnika od 13.11.2026.

**Oznake pouzdanosti tvrdnji u dokumentu:**
- **[Č]** činjenica proverena u dokumentaciji ili računicom
- **[P]** procena, mora se izmeriti
- **[?]** pretpostavka ili otvoreno pitanje

**Stanje provere:** nijedan red koda još nije napisan ni pokrenut. Sve brojke o potrošnji su [P]. Test se vodi kao „prošao“ samo ako je stvarno pokrenut i rezultat viđen.

**Šta ne obećavamo:** apsolutno neprekidan rad u svakom kvaru hardvera (pokvaren SSD, napajanje, zvučna kartica, pojačalo) nije moguće garantovati softverom. Cilj je da softver sam preživi sve kvarove koje može da preživi i da se posle ostalih sam vrati u rad.

---

## 1. Odluke

### 1.1 Odobrene odluke (vlasnik, 13.11.2026.)

| # | Odluka |
|---|---|
| D1 | C# / .NET 10 (LTS) + WPF + NAudio (WASAPI) + SQLite |
| D2 | **Dva nezavisna programa:** audio proces (Engine: običan program bez prozora u prijavljenoj korisničkoj sesiji, **nije Windows servis**) i grafički interfejs (UI). Muzika nastavlja kad se UI zatvori, zamrzne ili ponovo pokrene |
| D3 | Jednosmeran lanac nadzora: Task Scheduler → Watchdog → Engine i UI |
| D4 | Analiza i reprodukcija koriste isti dekoder |
| D5 | Sve zahtevne analize rade se unapred, van emitovanja, sa ograničenjem resursa |
| D6 | **DJ Mix po tri slučaja** (deo 5.3): do 0,3% pravi beatmatching; 0,3% do 2–3% unapred pripremljeno usklađivanje tempa uz očuvanje visine tona; preko toga Smart. Nestabilan tempo ili nesigurna analiza: uvek Smart |
| D7 | Glasnoća: početni cilj −18 do −20 LUFS, pojačanje po pesmi ograničeno, limiter samo kao zaštita |
| D8 | Glasnoću zaposlenih kontroliše aplikacija; sistemska glasnoća je fiksna |
| D9 | Prilagodljiva rotacija; 7 dana ostaje cilj; ekran „Zdravlje biblioteke“ |
| D10 | Ništa bez potvrđenih prava ne ide u eter (muzika i AI glas), ni ručnim izborom ni iz rezervnih lista |
| D11 | Windows sistemski zvuci i obaveštenja se isključuju |
| D12 | Engine je tehnički odsečen od mreže pravilom Windows Firewall-a |
| D13 | Interfejs je u potpunosti za ekran na dodir, bez tastature i miša u svakodnevnom radu; admin pod PIN-om |
| D14 | Srpski (latinica) i engleski interfejs |
| D15 | LUSTRE VOICE: dva originalna sintetička glasa, bez kloniranja; prvo se testira ElevenLabs, Azure je rezerva |
| D16 | Aplikacija se ne prodaje i ne distribuira; koristi je samo Caffe Lustre |
| D17 | Testiranje: automatsko u fajl + stvarna reprodukcija na uređaju + testovi na računaru u kafiću |
| D18 | Privatni GitHub repozitorijum sa udaljenim Windows build-om; ništa od toga se ne instalira na mini PC |
| D19 | Nijedna funkcionalnost iz v3.1 se ne izbacuje bez odobrenja |

### 1.2 Uslovne odluke (važe tek kad se ispuni uslov)

| # | Odluka | Uslov | Ako uslov ne prođe |
|---|---|---|---|
| U1 | Dva programa ostaju konačna arhitektura | Izmerena **zajednička** potrošnja Engine + UI + Watchdog na Celeronu je u budžetu (deo 3.2) | UI se drži zatvoren van upotrebe ili se pojednostavljuje; Engine ostaje odvojen |
| U2 | Dva decka uživo | Bench na Celeronu: 0 prekida, CPU u budžetu | Lakša varijanta iz dela 3.5 (prelazi pripremljeni noću) |
| U3 | Usklađivanje tempa uz očuvanje visine tona (D6, srednji slučaj) | (a) kvalitet prolazi slušni test, (b) licenca pesme dozvoljava izmene i obrađene kopije | Za tu pesmu ili u celini: Smart Crossfade |
| U4 | Sopstveni algoritmi za BPM, tonalitet, LUFS | Prolaze pragove merenja (deo 6) | Zamenske biblioteke (deo 6.4) |
| U5 | ElevenLabs kao servis za glas | Srpski izgovor prolazi vaš slušni test; uslovi korišćenja pročitani i sačuvani | Azure; ili kombinacija |
| U6 | Automatski build na GitHub-u | GitHub nalog povezan sa Claude-om; Windows runner uspešno prevede projekat | Build i testovi se pokreću ručno na vašem Windows računaru |
| U7 | Automatsko paljenje posle povratka struje | BIOS mini PC-ja ima opciju „Restore on AC Power Loss“ / „AC Power Recovery“ | Ručno paljenje dugmetom; sve ostalo je automatsko |
| U8 | Media Foundation kao glavni dekoder | Windows nije „N“ izdanje ili ima Media Feature Pack | Instalacija Media Feature Pack-a; rezervni dekoderi |
| U9 | Rad sa 4 GB RAM-a | Soak test 16,5 h prolazi bez prekida | Preporuka proširenja na 8 GB postaje uslov |
| U10 | Cilj glasnoće | Slušni test u lokalu između −18 i −20 LUFS | Podešava se; vrednost je parametar |
| U11 | Način crtanja interfejsa (softverski ili preko grafičke kartice) | Izmeren odziv na dodir na mini PC-ju u oba načina | Bira se brži i stabilniji |
| U12 | Analiza i priprema prelaza posle 23:30 | Mini PC ostaje uključen noću | Analiza na drugom računaru (Analyzer); priprema prelaza koliko stigne; ostalo Smart |
| U13 | Preslušavanje u radno vreme | Postoji zaseban audio izlaz za preslušavanje (npr. slušalice) | Preslušavanje samo van radnog vremena |
| U14 | Poseban standardni Windows nalog za radio | Moguće ga je napraviti na mini PC-ju | Koristi se postojeći nalog; rizik slučajnih izmena je veći |

---

## 2. Arhitektura

### 2.1 Procesi, sesija i pokretanje

**Terminologija:** „audio proces“ ili Engine je običan program bez prozora koji radi u **prijavljenoj (interaktivnoj) korisničkoj sesiji**. **Nije Windows servis i ne radi u Session 0.** Windows servisi rade u izolovanoj sesiji 0, gde pristup zvučnim uređajima korisnika nije pouzdan [Č]. Zato se Windows servis ne koristi ni za jedan deo sistema.

```
Automatska prijava naloga „LustreRadio“ (standardni, ne administratorski)
   └─► Task Scheduler: jedan zadatak, interaktivno, u sesiji tog korisnika
          └─► LustreRadio.Watchdog.exe     nadzor; bez prozora; bez mreže
                 ├─► LustreRadio.Engine.exe  zvuk (WASAPI Shared), raspored, rotacija,
                 │                           baza (jedini upisuje); bez prozora; bez mreže
                 └─► LustreRadio.UI.exe      ekran na dodir: zaposleni, admin, Voice Studio
                          ▲ lokalni imenovani kanal (named pipe), deo 2.6 ▼
```

| Program | Sesija | Prozor | Mreža | Prava | Pokreće ga |
|---|---|---|---|---|---|
| Watchdog | korisnička | ne | blokirana | standardna | Task Scheduler |
| Engine | korisnička | ne | blokirana | standardna | samo Watchdog |
| UI | korisnička | da | samo Voice Studio, na zahtev admina | standardna | samo Watchdog |

**Zadatak u Task Scheduler-u** (pravi ga instaler; instaler jedini traži administratorska prava, jednom):

| Podešavanje | Vrednost | Zašto |
|---|---|---|
| Okidač | prijava korisnika „LustreRadio“, odlaganje 10 s | sistem zvuka stigne da se podigne |
| Način | **„Run only when user is logged on“** | opcija „Run whether user is logged on or not“ pokreće program van interaktivne sesije, gde zvuk ne radi [Č]; zabranjena |
| Prava | najmanja (bez „highest privileges“) | programu ne trebaju administratorska prava |
| Prioritet | 4 (normalan) | podrazumevano je 7, sniženo [Č] |
| Vremensko ograničenje | isključeno | podrazumevano gasi zadatak posle 72 h [Č] |
| Ponavljanje | na 5 min, neograničeno; „Do not start a new instance“ | vraća Watchdog ako nestane |
| Ponovni pokušaj | posle 1 min, 3 puta | ako start ne uspe |
| Uslovi baterije i mirovanja | isključeni | ne smeju da zaustave zadatak |

- **Jedan izvor pokretanja:** ne koriste se Startup folder, Run ključ ni servis.
- **Provera sesije:** Engine i Watchdog pri startu proveravaju da nisu u sesiji 0. Ako jesu, upišu grešku i izađu. Ekran „Provera sistema“ proverava podešavanja zadatka.
- Engine sam podiže svoj prioritet i prioritet audio niti.
- Ako audio uređaj još nije spreman pri startu, Engine čeka i pokušava na 5 s.

### 2.2 Nadzor i oporavak

- **Jedna instanca:** tri imenovana mutex-a (`Global\LustreRadio.Watchdog`, `.Engine`, `.UI`). Windows ih sam oslobađa kad proces umre. Drugi start UI-ja samo pošalje postojećem „prikaži se“ i zatvori se.
- **Watchdog ne vezuje život Engine-a za svoj.** Ako Watchdog padne, Engine nastavlja da svira. Novi Watchdog **preuzima** postojeće programe (prepoznaje ih po mutex-u i vezi) i ne pokreće nove.
- **Signal života:** Engine svakih 5 s šalje Watchdogu stanje (svira / pauzirano / mirovanje) i brojač puštenih uzoraka. Kvar je: nema signala 30 s, ili stanje „svira“ a brojač stoji 15 s. Pauza i mirovanje nisu kvar. Sva vremena mere se monotonim satom.
- **Oporavak Engine-a:** prekid procesa → čekanje da se mutex oslobodi (do 10 s) → pauza 3 s → start. Engine nastavlja iz snimka stanja (ista pesma, pozicija, fade-in 3 s).
- 3 pada Engine-a za 10 min → **sigurni režim** (samo rezervna reprodukcija, bez analize i skeniranja); pokušaj punog režima na 60 s.
- **UI:** pad → novi start posle 5 s; ne odgovara 30 s → prekid i novi start; 5 padova za 10 min → pokušaj na 5 min. Muzika se pri tome ne dira.
- **Watchdog:** vraća ga Task Scheduler (najkasnije za 5 min). Za to vreme muzika svira, ali niko ne nadzire Engine.
- **Namerno zaustavljanje:** admin komanda „Zaustavi LUSTRE RADIO“ upisuje oznaku; Watchdog tada ne pokreće ništa do komande „Pokreni“ ili sledeće prijave. Isto koristi i ažuriranje programa.
- **Gašenje ili odjava Windowsa:** Engine hvata kraj sesije, uradi kratak fade i upiše stanje. I bez toga snimak stanja nije stariji od 5 s.
- **Sesija:**
  - zaključan ekran ne bi trebalo da prekida zvuk [P, test T-SES-02];
  - Remote Desktop preuzima sesiju i zvuk [Č kao poznato ponašanje], pa se u radno vreme ne koristi; za daljinsku pomoć alat koji ne preuzima sesiju;
  - prebacivanje na drugi Windows nalog prekida zvuk, pa računar radi sa jednim nalogom.
- **Granice zaštite:** PIN i standardni nalog sprečavaju slučajne izmene. Ne štite od osobe sa fizičkim pristupom koja namerno želi štetu. Zaključavanje Windowsa u kiosk režim je moguće kasnije, van ove specifikacije.

### 2.3 Audio engine
- Dva decka za muziku + treći ulaz za glasovne poruke → mixer → limiter → izlaz.
- Izlaz je apstrakcija (`IAudioSink`): **WASAPI** (stvarni uređaj) ili **WAV fajl** (offline renderer za testove, brže od realnog vremena).
- Interni format float 32-bit stereo; sample rate prati uređaj. Preporuka: uređaj na 44,1 kHz, jer je većina muzike takva, pa nema resamplinga [P].
- Pesme se čitaju u malim delovima (ring buffer ~2 s po decku). **Cela pesma se nikad ne učitava u memoriju** [Č: 5 min dekodiranog zvuka ≈ 100+ MB].
- Dekodiranje radi posebna nit, nikad audio callback. Bez alokacija memorije u audio putanji.
- Početak sledeće pesme (~20 s) dekodira se unapred u mali bafer.
- WASAPI shared, bafer 200 ms. Audio nit sa MMCSS prioritetom „Pro Audio“.
- Čuvar tišine: ako izlaz ćuti duže od 3 s dok bi muzika trebalo da svira → sledeća pesma → rezervni nivoi (deo 10).
- **Audio uređaj:** Engine prati nestanak uređaja, promenu podrazumevanog uređaja i promenu formata; ponovo se povezuje na 5 s. Admin bira uređaj „zaključano na ovaj“ ili „prati podrazumevani“.
- **Glasnoća (D8):** dugme za zaposlene menja pojačanje u aplikaciji u opsegu koji odredi admin. Engine drži sistemsku glasnoću uređaja na fiksnoj vrednosti i poništava „mute“.
- **Vreme:** raspored koristi zidni sat (`IClock`); sva trajanja (fade, pauze, nadzor) koriste monotoni sat; pozicija u pesmi se računa iz broja uzoraka.

### 2.4 Glasnoća pesama (D7)
- Cilj je parametar, početno **−18 LUFS** (testiramo −18 do −20).
- Pojačanje po pesmi = cilj − izmerena glasnoća, ali **ograničeno**: (a) najviše +6 dB [P, podesivo], (b) tako da vrh pesme ostane ispod −1 dBFS.
- Tihe pesme koje ne mogu da stignu do cilja ostaju malo tiše, umesto da ih limiter gnječi.
- Limiter je osigurač tokom prelaza (zbir dva kanala) i za glasovne poruke.

### 2.5 Projekti i zavisnosti

```
LustreRadio/
├─ src/
│  ├─ LustreRadio.Core/       čista logika bez I/O: IClock, raspored, rotacija, kapija licenci,
│  │                          planer prelaza, raspored glasa, pravila rezervnih režima
│  ├─ LustreRadio.Audio/      dekoderi, Deck, Mixer, Limiter, IAudioSink (WASAPI | WAV), glasovni ulaz
│  ├─ LustreRadio.Analysis/   LUFS, tišina, intro/outro, BPM, beat grid, stabilnost tempa, tonalitet, vokal
│  ├─ LustreRadio.Data/       SQLite, migracije, backup, izvoz/uvoz
│  ├─ LustreRadio.Library/    skener, probno dekodiranje, heš, duplikati
│  ├─ LustreRadio.Ipc/        protokol između Engine-a i UI-ja
│  ├─ LustreRadio.Engine/     proces bez prozora
│  ├─ LustreRadio.UI/         WPF: Staff, Admin, Voice Studio, Provera sistema; SR/EN
│  ├─ LustreRadio.Voice/      JEDINI deo sa mrežom: TTS (ElevenLabs, Azure); koristi ga samo UI
│  ├─ LustreRadio.Watchdog/   nadzor (cilj: Native AOT radi male memorije [P])
│  ├─ LustreRadio.Analyzer/   prenosni alat: analiza na drugom računaru
│  └─ LustreRadio.Bench/      prenosni merni alat za računar u kafiću
├─ tests/   Core · Audio · Analysis.Benchmark · Resilience · Ipc · Architecture · L10n · UI.Smoke
├─ testdata/  installer/  docs/  .github/workflows/
```

**Pravilo:** Core ne zavisi ni od čega. Engine ne referencira Voice. UI zna samo za Ipc, Voice i read-only Data. Proverava se automatskim arhitektonskim testom.

### 2.6 Komunikacija između interfejsa i audio procesa

- **Mehanizam:** imenovani kanal (named pipe) na istom računaru. Ne koristi mrežu ni TCP, pa ga pravilo zaštitnog zida ne dotiče. Pristup ima samo isti Windows nalog; pristup preko mreže je zabranjen.
- Engine je server, UI klijent. Poseban kanal služi za signal života ka Watchdogu.
- **Poruke:** JSON sa prefiksom dužine. Pri povezivanju se razmenjuje verzija protokola; ako se ne poklapa, UI prikaže poruku i ne šalje komande.
- **Tri vrste poruka:** komanda → odgovor (sa ID-jem), događaj (sa rednim brojem), snimak stanja.
- **Engine je jedini izvor istine; UI ne čuva stanje.** Pri povezivanju UI dobija ceo snimak: pesma, izvođač, album art, sledeća pesma, pozicija, režim, miks, status rasporeda, glasnoća, vreme do gašenja, stanje sistema. Zatim stižu samo promene. Poziciju u pesmi UI računa sam, uz usklađivanje na 5 s.
- **Komande zaposlenih** (bez PIN-a): Play/Pause, Next, glasnoća kao apsolutna vrednost, Mirnije/Življe.
- **Admin komande** traže važeću admin sesiju. **PIN proverava Engine**, ne UI, i izdaje privremenu sesiju koja ističe posle 3 min neaktivnosti.
- **Engine ne veruje UI-ju:** sam primenjuje opseg glasnoće, kapiju licenci i sva pravila.
- **Ponovljene komande:** svaka nosi ID i izvršava se jednom. „Next“ nosi oznaku trenutne pesme, pa dvostruki dodir ne preskače dve pesme.

**Kako pad interfejsa ne može da zaustavi muziku:**
- audio nit nikad ne dodiruje komunikaciju;
- komande idu u red koji čita posebna kontrolna nit;
- događaji ka UI-ju idu u ograničen red (256 poruka); ako UI ne čita ili je red pun, Engine prekida vezu i nastavlja rad;
- nijedan poziv ka UI-ju ne čeka odgovor;
- neispravna ili prevelika poruka se odbija i beleži; učestalost komandi je ograničena.

**Kad veza pukne:**
- UI zadržava poslednji prikaz zasivljen, uz „Povezivanje…“, onemogućava kontrole i pokušava ponovo na 1 s, zatim ređe do 5 s. Po povezivanju dobija nov snimak stanja. UI nikad ne pokreće Engine.
- Engine ne menja ništa: raspored, pauza i override žive u njemu.

**Fajlovi i baza:**
- UI ne upisuje u bazu ni u foldere podataka. Nov fajl (glasovna poruka, dokaz licence) UI ostavi u `incoming\` i pošalje komandu; Engine ga proveri, obradi, izračuna heš, premesti i upiše.
- UI čita bazu read-only vezom, kratkim upitima.
- Engine drži sledeće tri pesme u memoriji, pa mu baza ne treba u trenutku prelaza.

**Podaci:** `C:\ProgramData\LustreRadio\` → `incoming\`, `lustre.db`, `logs\`, `backups\`, `licenses\`, `voice\`, `fallback\`, `prepared\` (pripremljeni prelazi). Muzika ostaje na svom mestu.

---

## 3. Ciljni računar

### 3.1 Konfiguracija

Namenski računar Caffe Lustre, postavljen u lokalu, za emitovanje muzike. **Nije privatni računar vlasnika.** Razvoj i build se rade na drugom računaru ili udaljeno; na mini PC se instalira samo gotov program.

| Stavka | Vrednost |
|---|---|
| Tip | Mini PC |
| Procesor | Intel Celeron N4000, 1,10 GHz, 2 jezgra / 2 niti |
| Memorija | 4 GB RAM |
| Disk | SATA SSD |
| Grafika | Intel UHD Graphics 600 |
| Ekran | monitor na dodir (touchscreen) |
| Sistem | Windows 11 |

Još nepoznato [?]: vidi deo 26.

### 3.2 Budžet (sve [P] dok se ne izmeri; meri se **zbir sva tri procesa**)

| Merilo | Cilj tokom emitovanja |
|---|---|
| CPU ukupno, prosek | ≤ 10% |
| CPU ukupno, vrh tokom prelaza | ≤ 25% |
| RAM Engine | ≤ 80 MB |
| RAM UI | ≤ 130 MB |
| RAM Watchdog | ≤ 30 MB (≤ 10 MB ako Native AOT uspe) |
| **RAM zbir** | **≤ 240 MB**, bez rasta većeg od 10 MB tokom 16,5 h |
| Prekidi zvuka (underrun) | 0 |
| Odziv na dodir | ≤ 200 ms |

Budžet se posle prvog merenja koriguje stvarnim brojevima; ako zbir ne prolazi, primenjuje se U1.

### 3.3 Ocena [P]
- Dekodiranje i mešanje dve pesme je lak posao za ovaj procesor, jer se sve odluke računaju unapred.
- Glavni rizici: 4 GB memorije uz Windows 11, pozadinska aktivnost Windowsa (ažuriranja, Defender), HDMI zvuk koji nestaje kad se monitor ugasi.

### 3.4 Optimizacije
- **Analiza samo van emitovanja** (posle fade-out-a u 23:30 dok je računar uključen, i pre 07:00; U12), najniži prioritet procesora i diska, jedna pesma u isto vreme, pauze između (procesor je pasivno hlađen). Automatski staje ako počne emitovanje ili ako audio bafer padne ispod granice. Ručno pokretanje tokom dana samo uz upozorenje.
- **Poslovi na kraju dana** (odmah posle 23:30, kratki): backup baze sa proverom, osvežavanje rezervne liste, plan sutrašnjeg dana. Ako su propušteni jer je računar ugašen, rade se pri sledećem startu, sa najnižim prioritetom. Analiza i priprema prelaza su prekidive: gašenje računara ih ne kvari, nastavljaju se sledeći put. Ekran posle 23:30 prikazuje „Može se bezbedno ugasiti“ ili „Analiza u toku (sme se ugasiti)“.
- **Analiza na drugom računaru** (`Analyzer.exe`): rezultati se prenose fajlom, pesme se prepoznaju po hešu. Uslov iz D4: isti dekoder i ista verzija; inače se DJ poravnanje za te pesme ne koristi dok se ne potvrdi na mini PC-ju.
- **Interfejs:** način crtanja se bira merenjem (U11): softversko crtanje uklanja zavisnost od grafičkog drajvera, ali može usporiti listanje dugih spiskova prstom [P], bez animacija, senki, blura i vizualizatora; osvežavanje jednom u sekundi; „mirni prikaz“ posle 2 min; admin ekrani se učitavaju po potrebi.
- **.NET:** Workstation GC bez concurrent režima, ReadyToRun; jedan zajednički self-contained runtime za sva tri programa.
- **SQLite:** mali keš, jedan upis istorije po pesmi, snimak stanja na 5 s.

### 3.5 Lakša varijanta ako merenja ne prođu (redom, bez izbacivanja funkcija)
1. Veći bafer (300–500 ms).
2. Kraj trenutne i početak sledeće pesme potpuno unapred dekodirani za prelaz.
3. Sve pesme i uređaj na 44,1 kHz, bez resamplinga.
4. **Prelazi pripremljeni noću** kao mali fajlovi; danju se samo puštaju.
5. UI zatvoren kad se ne koristi (U1).

### 3.6 Podešavanje Windowsa (instaler + ekran „Provera sistema“)
Automatska prijava (Sysinternals Autologon) · bez spavanja i hibernacije · aktivni sati za ažuriranja 06:30–00:00 · **zvučna šema „Bez zvukova“ i isključena obaveštenja (D11)** · uklonjeni nepotrebni programi iz pokretanja · isključeno indeksiranje foldera sa muzikom · format uređaja 44,1 kHz · pravilo zaštitnog zida za Engine (D12) · provera Media Foundation · slobodan disk · tačnost sata. BIOS opcija za struju ide u štampano uputstvo (program je ne može proveriti).

### 3.7 Monitor na dodir
- Sve kontrole najmanje 56 px sa razmakom 12 px [P, potvrđuje se na vašem monitoru]; bez funkcija koje zahtevaju prelazak mišem, desni klik ili tastaturu.
- PIN se unosi sopstvenom tastaturom na ekranu (ne Windows tastaturom).
- Gašenje i paljenje monitora: UI mora da se vrati u ispravan prikaz; Engine ne sme da primeti ništa osim eventualnog nestanka HDMI zvuka.
- **Preporuka:** zvuk preko 3,5 mm izlaza ili USB zvučne kartice, ne preko HDMI-ja, jer HDMI audio uređaj često nestaje kad se monitor ugasi [Č kao poznato ponašanje; na vašem hardveru [?]].

### 3.8 Sistemski zahtevi (privremeno)

| | Minimalno (ciljno) | Preporučeno |
|---|---|---|
| CPU | 2 jezgra, klasa Celeron N4000 | 4 jezgra (npr. Intel N100) |
| RAM | 4 GB | 8 GB |
| Disk | SSD, 2 GB + muzika (1000 pesama ≈ 10 GB MP3 / 30 GB FLAC) | SSD 128 GB+ |
| OS | Windows 10 22H2 / 11, 64-bit, sa Media Foundation | Windows 11 Pro |
| Ekran | 1366×768, dodir ili miš | 1920×1080 na dodir |
| Zvuk | bilo koji WASAPI uređaj | 3,5 mm ili USB zvučna kartica |

---

## 4. Audio formati i dekoderi

| Format | Glavni dekoder | Rezervni | Licenca |
|---|---|---|---|
| WAV | NAudio | Media Foundation | MIT |
| MP3 | Media Foundation (MF) | NLayer | MIT |
| FLAC | MF (Windows 10+) [Č] | libFLAC | BSD-3 |
| M4A (AAC/ALAC) | MF [Č] | nema (FAAD2 je GPL; može se razmotriti po D16) | – |
| AAC (ADTS) | MF [Č] | nema | – |
| OGG Vorbis | NAudio.Vorbis + NVorbis | – | MIT |
| OGG Opus | Concentus | – | BSD-3 / MIT |

- Ekstenziji se ne veruje: pri skeniranju se svaki fajl probno dekodira i dobija status.
- **Dekoder se bira jednom, pri skeniranju, i upisuje uz pesmu** (`decoder_id` + verzija). Analiza i reprodukcija koriste taj isti (D4). Ako reprodukcija mora na rezervni, DJ poravnanje za tu pesmu se gasi.
- Za tačke prelaza koristi se dekodiranje od početka u bafer, ne premotavanje, jer premotavanje u VBR MP3 ume da bude neprecizno [P].
- Windows „N“ izdanja nemaju MF [Č] → U8.

---

## 5. Prelazi

### 5.1 Režimi

| Režim | Opis | Ako podaci fale |
|---|---|---|
| Standard | fiksni crossfade 3–20 s, equal-power | – |
| Smart | odsecanje tišine; outro/intro iz krive energije; dužina prema energiji obe pesme | Standard |
| DJ Mix | po delu 5.3 | Smart |
| Break | za glasovne poruke: pesma se završi prirodno → poruka → sledeća pesma od početka; opciono glas preko kraja pesme uz muziku utišanu ~10 dB | Break bez preklapanja |

Podesivo: tri režima, trajanje, po miksu i po slotu.

### 5.2 Smart Crossfade – prioriteti

| # | Signal | Ako ga nema |
|---|---|---|
| 1 | Tišina na krajevima | uvek dostupno |
| 2 | Outro/intro | fiksne vrednosti po žanru |
| 3 | Usklađivanje energije | standardna kriva |
| 4 | **Procena vokala** | težina 0 |

Procena vokala je približna, sa stepenom pouzdanosti; samo pomera tačku prelaza kad je pouzdanost visoka; može se isključiti. Smart mora proći iste testove i bez nje.

### 5.3 DJ Mix (D6)

Računica [Č]: pri razlici tempa od 4% tokom prelaza od 16 s udarci se raziđu ~640 ms. Zato:

| Razlika tempa | Postupak |
|---|---|
| ≤ 0,3% | **Pravi beatmatching:** poravnanje udaraca i fraza (8/16 taktova), prelaz uživo; start sledeće pesme tačno u uzorak |
| 0,3% – 2% (gornja granica podesiva do 3%) | **Unapred pripremljen prelaz:** van emitovanja se segment prelaza izračuna sa blagim usklađivanjem tempa uz očuvanje visine tona i sačuva kao fajl; danju se samo pušta (U3) |
| preko toga | Smart Crossfade |

Dodatna pravila:
- DJ Mix se koristi samo ako obe pesme imaju **stabilan tempo** (mera stabilnosti iz analize) i pouzdanost BPM-a i mreže udaraca iznad praga. Jazz, bossa nova, akustika i slično: Smart, osim ako analiza pouzdano potvrdi stabilan ritam.
- Tonalitet (Camelot ±1) utiče na **izbor sledeće pesme**, ne blokira prelaz. Ako tonalitet nije poznat, reprodukcija ide normalno.
- Svaka nesigurnost → Smart.
- **Usklađivanje tempa:** biblioteka se bira merenjem kvaliteta (SoundTouch, LGPL; Rubber Band, GPL, dozvoljeno po D16). Menja se samo deo unutar prelaza, najviše do polovine razlike po pesmi [P]. Original se nikad ne menja.
- **Licence (U3):** pripremljeni prelaz je obrađena kopija. Pravi se samo ako obe pesme imaju potvrđeno „Dozvoljene izmene: Da“. Fajlovi u `prepared\` vezani su za heševe obe pesme i brišu se čim se heš ili status licence promeni. Nikad se ne izvoze van programa.
- **Plan dana:** pripremljen prelaz traži da se par pesama zna unapred. Zato se redosled za sutra planira na kraju dana, uz sva pravila rotacije. Svako odstupanje (Next, override, Mirnije/Življe, pomerena glasovna poruka, preskočen fajl) znači da se za taj spoj koristi beatmatching uživo ako je moguć, inače Smart.
- Ako pripremljeni fajl ne postoji ili je neispravan → Smart.

---

## 6. Analiza zvuka: prvo merenje

### 6.1 Redosled razvoja (po koristi)
LUFS → tišina na krajevima → outro/intro → BPM → mreža udaraca i stabilnost tempa → tonalitet → procena vokala.

### 6.2 Testni skup
Veštački signali (klik-trake 60–140 BPM, sekvence u 24 tonaliteta, šum poznatog nivoa) · EBU Tech 3341/3342 · ~50 stvarnih pesama sa referencama (Mixxx i FFmpeg samo kao merni alati).

### 6.3 Pragovi (predlog; konačno posle prvog merenja)
LUFS: ±0,1 LU na EBU signalima, ±0,5 LU na muzici · BPM: ≥ 90% unutar ±4% · za beatmatching dodatno: greška tempa ≤ 0,1% i mreža udaraca ±10 ms na veštačkim signalima · tonalitet: ponderisani skor ≥ 70%.
Rezultat ispod praga ili niske pouzdanosti se ne koristi za DJ odluke.

### 6.4 Zamene ako sopstveni algoritmi ne prođu (U4)
libebur128 (MIT) · SoundTouch BPM (LGPL) · aubio, Queen Mary, libKeyFinder (GPL) · Essentia (AGPL). GPL/AGPL su prihvatljivi jer se program ne distribuira (D16); ako se to ikad promeni, ovo se mora ponovo razmotriti. Ovo nije pravni savet.

---

## 7. Raspored

- Radno vreme 07:00–23:30, podesivo. Raspored se računa iz sata pri svakoj proveri: start u 10:00 → svira; u 06:00 → čeka; posle 23:30 → ćuti.
- 23:30: fade-out 15–30 s, pa mirovanje.
- Tri perioda (Morning 07–11, Afternoon 11–17, Evening 17–23:30) × 7 dana = **21 slot**; za svaki: žanrovi, BPM raspon, intenzitet, miks, režim prelaza. Kopiranje dana. Praznici i posebni događaji. Weekly Calendar u adminu.
- Podrazumevani sadržaj slotova prema master promptu (delovi 2 i 11).
- Prioritet: **Manual Override > praznik/događaj > nedeljni slot > podrazumevano.** Override ima trajanje i sam se vraća. Privremeno isključenje rasporeda takođe ima rok.
- Snimak stanja na 5 s; posle restarta nastavak iste pesme sa pozicije uz fade-in 3 s.
- Ako sat „ode unazad“: upozorenje na ekranu.

---

## 8. Biblioteka, miksevi, rotacija

- **Biblioteka:** dodavanje foldera, skeniranje, automatsko otkrivanje novih pesama, ponovno skeniranje, izmena podataka, uklanjanje, favorit, blokada, duplikati (heš i akustički otisak), raspoloženje, istorija.
- **Miksevi:** neograničeno; naziv, žanrovi, BPM, energija, izbor pesama ili pravila, crossfade, pravila ponavljanja, trajanje; automatsko generisanje.
- **Rotacija (D9):**
  - Cilj: ista pesma ne pre 7 dana (podesivo); razmak za izvođača; izbegavanje istih redosleda.
  - Računica [Č]: ~250 pesama dnevno → ~1750 proverenih pesama za punih 7 dana.
  - Kad pesama nema dovoljno: prvo kompatibilni žanrovi, zatim se razmak **postepeno** smanjuje na najveći ostvarivi. Uvek se bira pesma koja najduže nije puštana među odgovarajućim. Nikad ista pesma dvaput zaredom ako postoje bar dve.
  - **Ekran „Zdravlje biblioteke“:** za svaki od 21 slota broj proverenih pesama, ostvareni razmak i koliko pesama fali do cilja.

---

## 9. Licence (muzika i AI glas)

**Statusi:** Uvezeno (neprovereno) → Na proveri → **Provereno** | Odbijeno; Provereno → Ponovna provera (promenjen heš, istekao rok, izmenjen izvor).

- **Kapija (D10):** jedna funkcija u Core-u odlučuje da li stavka sme u eter, i **svi putevi prolaze kroz nju**: raspored, ručni izbor, override, preslušavanje u eteru, rezervna lista, folder „Rezerva“, glasovne poruke, pripremljeni prelazi.
- Status „Provereno“ dodeljuje samo admin, uz sva polja i dokaz.
- **Preslušavanje neproverenih stavki** (pesma na proveri, nova glasovna poruka) radi UI, nikad Engine, na zasebnom uređaju za preslušavanje (npr. slušalice). Bez takvog uređaja, preslušavanje na zvučnicima lokala dozvoljeno je samo van radnog vremena (U13).
- **Polja:** izvor i URL, autor, tip i verzija licence, datum preuzimanja, komercijalna upotreba, javno izvođenje u ugostiteljstvu, **dozvoljene izmene (crossfade, normalizacija, promena tempa, obrađene kopije)**, obaveza navođenja autora, dokaz (PDF ili snimak ekrana), SHA-256, ko i kada.
- **Pravila:** „Ne“ ili „Nejasno“ u ključnim poljima blokira status; NC i ND se odbijaju osim uz pisanu dozvolu; promena heša vraća na proveru; šabloni po izvoru; spisak autora za CC BY; tip „Pokriveno SOKOJ/OFPS“ traži dokaz, ništa se ne pretpostavlja.
- **FREE MUSIC LIBRARY ekran:** evidencija, filteri, dokazi, status, izvoz u CSV/PDF.
- Izvori za prikupljanje (Pixabay Music, Free Music Archive i drugi) proveravaju se pojedinačno u fazi M9; uslovi se menjaju.
- Nisam pravnik; nedoumice rešava SOKOJ/OFPS ili advokat.

---

## 10. Rezervni režimi

| Nivo | Problem | Reakcija |
|---|---|---|
| 1 | Pesma neanalizirana ili analiza nepouzdana | Standard crossfade, podrazumevana glasnoća |
| 2 | Fajl neispravan ili pukne usred pesme | prelaz ≤ 2 s na pripremljenu sledeću; posle 3 greške pesma izlazi iz rotacije |
| 3 | Greška u izboru sledeće pesme | najduže nepuštana pesma iz trenutnog miksa |
| 4 | Baza nedostupna ili oštećena | keširana lista proverenih pesama po periodu (osvežava se na kraju svakog dana) |
| 5 | Audio uređaj nestao | ponovno povezivanje; zatim podrazumevani uređaj |
| 6 | Sve gore otkazalo | folder „Rezerva“ sa spiskom heševa proverenih pesama |
| V | Greška glasovne poruke | preskače se, muzika ide dalje |
| P | Greška pripremljenog prelaza | Smart |

Svi rezervni putevi sadrže samo proverene stavke (D10): lista i folder nose heševe upisane dok je baza bila ispravna, a fajl koji ne odgovara hešu se ne pušta. Pre svakog puštanja radi se brza provera (veličina i datum izmene fajla); pun heš se računa pri skeniranju i na kraju dana.

---

## 11. Struja, restart, podaci

| Situacija | Ponašanje |
|---|---|
| Uključivanje | automatska prijava → Task Scheduler → Watchdog → Engine → raspored iz sata |
| Povratak struje | ako BIOS ima opciju (U7), računar se sam pali; dalje isto |
| Windows restart | noću zbog aktivnih sati; ako se desi danju, muzika nazad za ~1–2 min [P] |

- SQLite WAL; `synchronous=FULL` za važne upise; atomski upis fajla stanja; provera baze pri startu; automatsko vraćanje iz poslednjeg ispravnog backupa ako je oštećena.
- Preporuka van softvera: mali UPS štiti i računar i SSD od naglih nestanaka struje.

---

## 12. Interfejs

- **Ekran za zaposlene:** LUSTRE RADIO logo, pesma, izvođač, album art, progress bar, Play/Pause (pauza se sama prekida posle 15 min), Next, glasnoća (u opsegu), režim, miks, dan i datum, vreme do gašenja, status rasporeda, sledeća pesma, „Mirnije/Življe“ na 1 h, pokazatelj stanja.
- **Admin (PIN):** biblioteka, miksevi, dnevni i nedeljni raspored, vremena paljenja i gašenja, crossfade, glasnoća, ručna promena režima, privremeno isključenje rasporeda, Manual Override, istorija, pregled analize, FREE MUSIC LIBRARY, Zdravlje biblioteke, Voice Studio, backup, Provera sistema.
- **PIN:** 4–8 cifara, čuva se kao heš u bazi i **proverava ga Engine**; 5 grešaka → 5 min; samozaključavanje posle 3 min; kod za oporavak; dnevnik izmena.
- **Dizajn:** crna i antracit, zlatni detalji, minimalistički, statičan (elegancija kroz tipografiju i raspored, ne kroz efekte).
- **Jezici:** srpski (latinica) i engleski; promena bez restarta; posebno za ekran zaposlenih i admin.
- Pravilo: nema dugmeta bez funkcije i nema prikaza funkcije koja nije implementirana.

---

## 13. Potpuno offline

- Self-contained instalacija; bez telemetrije i provere ažuriranja; ažuriranje novim instalacionim fajlom.
- **Engine nema mrežni kod** (arhitektonski test) i **zaštitni zid mu blokira izlaz** (D12). Isto važi za Watchdog. Lokalna veza između programa ne koristi mrežu, pa je pravilo ne dotiče. Instaler dodaje pravilo; uputstvo opisuje kako se proverava i uklanja.
- Jedini mrežni deo je generisanje glasa u Voice Studio-u (UI, admin, ručno). Bez interneta sve ostalo radi, a postoji i ručni uvoz audio fajla.
- Test prihvatanja: 24 h sa isključenim mrežnim adapterom.

---

## 14. Backup, izvoz, uvoz

- Paket `.lustrebackup`: baza (konzistentan snimak dok muzika svira), podešavanja, dokazi o licencama, glasovne poruke; muzika opciono. API ključ se nikad ne izvozi.
- Delimičan izvoz: raspored i miksevi; ili samo glasovne poruke.
- Uvoz: remapiranje putanja, pronalaženje po hešu, automatski backup pre uvoza, nadogradnja šeme.
- Backup na kraju svakog dana (poslednjih 14) + opciono na USB. **Svaki backup se posle pravljenja proverava** (otvaranje i provera ispravnosti); neispravan se ne računa.
- Pripremljeni prelazi i keš se ne izvoze; prave se ponovo.

---

## 15. LUSTRE VOICE – AI Radio Hosts

- **Glasovi:** ženski (elegantan, topao, smiren) i muški (dubok, kultivisan, smiren). Originalni sintetički glasovi; bez kloniranja ičijeg glasa i bez glasova iz deljenih biblioteka zajednice.
- **Jezici:** srpski i engleski.
- **Kategorije:** Ident 2–5 s · Welcome 10–20 s · Long 20–40 s · Seasonal · Holiday · Event. Stil: luksuzan italijansko-francuski kafe, bez agresivnog reklamiranja.
- **Poruka ima:** tekst, jezik, glas, kategoriju, dozvoljene periode dana, važenje od–do, uključeno/isključeno, status (Nacrt → Odobreno → Arhivirano), verzije.
- **Voice Studio (admin):** pisanje teksta, izbor glasa i jezika, generisanje, preslušavanje, odobravanje, čuvanje, brisanje (u arhivu), izmena, ponovno generisanje, uključivanje/isključivanje, intervali, istorija emitovanja, ručni uvoz fajla, rečnik izgovora (za „Lustre“ se pravi nekoliko varijanti i bira po sluhu).
- **Obrada:** fajl se čuva lokalno (FLAC + original), odsecanje tišine, normalizacija (1–2 LU iznad muzike, podesivo), heš.
- **Emitovanje (početno, podesivo po periodu dana):** Ident na 45–60 min; duža poruka na 2–3 h; nasumično između odobrenih poruka i glasova; najviše 2 na sat.
- **Pravila umetanja:**
  - Poruka nikad ne prekida pesmu; čeka granicu između pesama i ulazi kao **planirana stavka u redu** sa Break prelazom.
  - Ne razbija dobar DJ par: pomera se na sledeću granicu, najviše 2 puta.
  - Ne u prvih 10 min posle starta, ne u poslednjih 10 min pre gašenja, ne dve zaredom; ista poruka ne pre 6 h (ident) odnosno 24 h (duža).
  - Sezonske, praznične i događajne imaju prednost u svom periodu.
- **Servis [Č, provereno 13.11.2026.]:** ElevenLabs podržava srpski u modelu Eleven v3; Multilingual v2 i Flash v2.5 ne. Komercijalna upotreba važi samo za sadržaj generisan tokom plaćene pretplate i ostaje trajno. Azure ima srpske glasove Sophie i Nicholas. Kvalitet srpskog izgovora [?] → U5.
- **Licenca poruke (tip „AI TTS“):** servis, model, glas i način nastanka, plan, dokaz plaćenog plana na dan generisanja, datum, ID zahteva, snimak uslova korišćenja, heš, ko je odobrio. Bez toga poruka ne ide u eter (D10).
- **API ključ:** šifrovan (Windows DPAPI); čuva ga UI u svom fajlu podešavanja, ne u bazi; Engine ga nikad ne vidi; nikad u backupu.
- **Podela posla:** UI generiše i preslušava; Engine obrađuje, čuva i emituje (deo 2.6).
- Preporuka: na vidnom mestu ili na zahtev navesti da su glasovi sintetički.

---

## 16. Baza podataka

| Grupa | Tabele |
|---|---|
| Biblioteka | `tracks`, `track_decode_status`, `track_analysis`, `analysis_confidence`, `genres`, `track_genres` |
| Miksevi | `mixes`, `mix_rules`, `mix_tracks` |
| Raspored | `schedule_slots`, `schedule_exceptions`, `overrides` |
| Rotacija | `play_history` |
| Prelazi | `prepared_transitions` |
| Licence | `licenses`, `license_proofs`, `license_audit` |
| Glas | `voices`, `voice_messages`, `voice_message_versions`, `voice_licenses`, `voice_rules`, `voice_play_history`, `pronunciation_dictionary` |
| Sistem | `settings`, `app_state`, `admin_audit`, `fallback_playlist`, `schema_version` |

---

## 17. Alati i biblioteke

**Razvoj:** .NET 10 SDK · Visual Studio Community · Git · Inno Setup 6 · GitHub (privatni repozitorijum). Merni alati (ne idu u aplikaciju): FFmpeg, Mixxx.

| Biblioteka | Namena | Licenca |
|---|---|---|
| NAudio | zvuk, WASAPI, MF | MIT |
| NLayer | rezervni MP3 | MIT |
| NAudio.Vorbis + NVorbis | OGG Vorbis | MIT |
| Concentus | OGG Opus | BSD-3 / MIT |
| libFLAC | rezervni FLAC | BSD-3 |
| TagLibSharp | tagovi | LGPL |
| Microsoft.Data.Sqlite + Dapper | baza | MIT / Apache |
| CommunityToolkit.Mvvm | MVVM | MIT |
| Serilog | logovi | Apache |
| TaskScheduler | Windows zadatak | MIT |
| MathNet.Numerics | FFT | MIT |
| SoundTouch ili Rubber Band | usklađivanje tempa (U3) | LGPL / GPL |
| libebur128, xUnit, NetArchTest, FlaUI | samo testovi | MIT / Apache |

Verzije i licence se potvrđuju pri dodavanju svakog paketa.

---

## 18. Plan razvoja

**Princip:** prvo najmanja verzija koja svira po rasporedu na pravom računaru i preživi restart; zatim nadogradnja. Svaka faza ima kriterijum prihvatanja; faza nije gotova dok ga niste videli.

| Faza | Sadržaj | Kriterijum prihvatanja |
|---|---|---|
| **M0** Temelj | repozitorijum, projekti, pravila zavisnosti, logovanje, skripte za build i test, automatski build | Build bez grešaka na Windowsu; arhitektonski testovi prolaze |
| **M1** Audio jezgro | dekoderi, deck, mixer, limiter, glasnoća, WASAPI i WAV izlaz, Standard crossfade, offline renderer, **Bench.exe** | T-AUD-01..08 prolaze; Bench na mini PC-ju: 0 prekida i izmeren CPU/RAM |
| **M2** Logika | sat, raspored (21 slot, praznici, override), rotacija, kapija licenci, raspored glasa, pravila rezervnih režima | T-SCH, T-ROT, T-LIC, T-VOI-03 prolaze |
| **M3** Engine i baza | SQLite, skener, probno dekodiranje, heš, snimak stanja, IPC, minimalan unos licence | Engine svira po rasporedu iz baze; T-RES, T-IPC prolaze |
| **M4** Nadzor | Watchdog, mutex-i, Task Scheduler, sigurni režim, zaštitni zid | T-WD, T-PWR prolaze; **soak 16,5 h na mini PC-ju**; izmeren zbir tri procesa (U1) |
| **M5** Interfejs | ekran za zaposlene, osnova admina, PIN, SR/EN, Provera sistema | T-UI, T-L10N, T-TCH prolaze na monitoru na dodir |
| **M6** Analiza | benchmark → LUFS → tišina/outro/intro → BPM/mreža/stabilnost → tonalitet → vokal; Analyzer.exe | Izveštaj merenja prema pragovima; odluka U4 |
| **M7** Smart i DJ Mix | planer prelaza, pripremljeni prelazi, zaštita DJ para | T-TRN prolaze; slušni test 20 prelaza po režimu; odluka U3 |
| **M8** Admin: raspored i miksevi | Weekly Calendar, praznici, override, miksevi | svi scenariji iz dela 7 kroz interfejs |
| **M9** Licence | FREE MUSIC LIBRARY, dokazi, Zdravlje biblioteke, provera izvora | ceo tok od uvoza do „Provereno“; T-LIC kroz interfejs |
| **M10** LUSTRE VOICE | emitovanje → Voice Studio i TTS → licence i istorija | T-VOI-01..06 prolaze |
| **M11** Otpornost i backup | svi rezervni nivoi, izvoz/uvoz, backup na kraju dana | T-RES, T-BAK prolaze, uključujući drugi računar |
| **M12** Završno | ceo plan testova, offline 24 h, struja ×10, 3 dana rada, instaler, uputstva SR/EN | svi testovi sa stvarnim rezultatima; instalacija na čist računar |

Obim [P]: 25–35 radnih sesija, realno nekoliko nedelja, zavisno od brzine testiranja na vašoj strani.

**Paralelno (vi):** prikupljanje legalne muzike sa evidencijom od prve pesme · probni test ElevenLabs · podaci o mini PC-ju.
**Paralelno (ja):** ~30 tekstova poruka SR/EN · varijante izgovora „Lustre“.

---

## 19. Plan testiranja

Tip: **A** automatski · **R** offline renderer · **U** stvarni audio uređaj · **M** ručno ili slušno · **C** na mini PC-ju u kafiću.

### Audio
| ID | Test | Tip | Kriterijum |
|---|---|---|---|
| T-AUD-01 | 50 **veštačkih** pesama (svaka svoj ton), Standard | R | u svakom trenutku se čuje tačno ono što plan predviđa; nema neplanirane tišine > 20 ms |
| T-AUD-02 | Klipovanje | R | nijedan uzorak iznad −0,3 dBFS |
| T-AUD-03 | Kriva prelaza na veštačkim signalima | R | zbir snage tokom prelaza unutar ±1 dB |
| T-AUD-04 | Matrica formata | A | dekodiranje i prelaz bez greške |
| T-AUD-05 | Fajl pukne usred pesme | R | prelaz ≤ 2 s; pesma označena |
| T-AUD-06 | **Dekodiranje od početka naspram posle premotavanja** | A | isti uzorci, ili izmeren pomak upisan uz dekoder |
| T-AUD-07 | Glasnoća: pojačanje ograničeno | A | nijedna pesma preko granice; vrh ≤ −1 dBFS pre limitera |
| T-AUD-08 | Stvarna reprodukcija sa snimanjem izlaza (loopback) | U | snimak odgovara izlazu offline renderera; 0 prekida |
| T-AUD-09 | Nestanak HDMI zvuka / gašenje monitora | C | zvuk se vraća ≤ 10 s po povratku uređaja; bez pada |
| T-AUD-10 | Promena podrazumevanog izlaza; izvlačenje USB kartice | U/C | po podešavanju: ostaje na zaključanom ili prati; bez pada |
| T-AUD-11 | Sistemska glasnoća i mute promenjeni spolja | U | Engine vraća fiksnu vrednost ≤ 5 s |

### Raspored (lažni sat)
T-SCH-01 start 06:00 → čeka do 07:00 · 02 start 10:00 → svira · 03 start 23:45 → ne svira · 04 23:30 → fade-out pa mirovanje · 05 svih 21 slotova · 06 praznik ima prednost · 07 override se vraća na isteku · 08 letnje/zimsko vreme bez dvostrukog ili propuštenog starta · 09 sat unazad → upozorenje · 10 trajanja ne zavise od promene sistemskog sata.

### Rotacija i licence
T-ROT-01 velika biblioteka, 30 dana: nema ponavljanja pre 7 dana · 02 mala biblioteka: razmak se smanjuje postepeno, uvek najduže nepuštana, nikad dvaput zaredom · 03 razmak za izvođača · 04 Zdravlje biblioteke tačno računa manjak.
T-LIC-01 **neproverena stavka nikad ne izlazi ni kroz jedan put** (raspored, ručni izbor, override, rezervna lista, folder, glas, pripremljeni prelaz) · 02 promena heša → van rotacije · 03 NC/ND → odbijeno · 04 pripremljeni prelaz se ne pravi bez „Dozvoljene izmene: Da“ i briše se pri promeni statusa.

### Prelazi i analiza
T-TRN-01 Smart: bez tišine na spoju (veštački signali sa poznatom tišinom) · 02 Smart bez procene vokala prolazi isto; slušno ≥ 4/5 na 20 prelaza · 03 beatmatching: udarci ±10 ms tokom celog prelaza na klik-trakama · 04 srednji slučaj: pripremljeni prelaz poravnat ±10 ms; visina tona nepromenjena (±5 centi) · 05 razlika preko granice, nestabilan tempo ili niska pouzdanost → Smart · 06 nedostaju podaci → pad na niži režim bez tišine.
T-ANA: merenje BPM, tonaliteta, LUFS prema pragovima, sa izveštajem.

### LUSTRE VOICE
T-VOI-01 poruka nikad usred pesme (R) · 02 DJ par netaknut (R) · 03 intervali i nasumičnost kroz 30 simuliranih dana (A) · 04 neispravan fajl → preskočeno bez tišine (R) · 05 generisanje → preslušavanje → odobrenje (M) · 06 emitovanje bez interneta (C).

### Stabilnost i oporavak
| ID | Test | Tip | Kriterijum |
|---|---|---|---|
| T-WD-01 | Prekid Engine-a | C | muzika nazad ≤ 15 s |
| T-WD-02 | Zamrzavanje Engine-a | A/C | otkriveno ≤ 30 s; tačno jedan novi proces |
| T-WD-03 | 20 brzih pokretanja | A/C | uvek po jedan Engine, UI, Watchdog |
| T-WD-04 | Pad, zamrzavanje i zatvaranje UI-ja | C | **muzika ne staje**; UI se vraća |
| T-PWR-01 | Izvlačenje struje ×10 | C | muzika nazad svaki put (uz U7 ili ručno paljenje); baza ispravna |
| T-PWR-02 | Windows restart u radno vreme | C | muzika nazad bez dodira |
| T-RES-01..06 | Svaki rezervni nivo | A/R | tišina ≤ 3 s |
| T-OFF-01 | Mrežni kod samo u Voice | A | prolazi |
| T-OFF-02 | Zaštitni zid blokira Engine | C | pokušaj veze iz Engine-a odbijen |
| T-OFF-03 | 24 h bez mrežnog adaptera | C | sve radi |
| T-PERF-01 | Bench.exe | C | budžet iz 3.2 |
| T-PERF-02 | Zbir tri procesa tokom 16,5 h | C | budžet; rast RAM-a ≤ 10 MB |
| T-SOAK-01 | 3 dana zaredom | C | 0 prekida |
| T-BG-01 | Analiza van emitovanja i emitovanje | C | analiza staje na početku emitovanja; 0 prekida |

### Sesija i komunikacija
| ID | Test | Tip | Kriterijum |
|---|---|---|---|
| T-SES-01 | Engine pokrenut u sesiji 0 (simulirano) | A | odbija rad i beleži grešku |
| T-SES-02 | Zaključan ekran tokom reprodukcije | C | zvuk ne staje |
| T-SES-03 | Podešavanja zadatka (interaktivno, prioritet, bez ograničenja) | A/C | Provera sistema ih potvrđuje |
| T-SES-04 | Pad Watchdoga | C | muzika svira; novi Watchdog preuzima postojeći Engine; nema drugog Engine-a |
| T-SES-05 | Namerno zaustavljanje | C | ništa se ne pokreće do komande ili nove prijave |
| T-SES-06 | Gašenje Windowsa usred pesme | C | stanje sačuvano; nastavak posle starta |
| T-IPC-01 | UI ne čita poruke 10 min | A | Engine svira; memorija ne raste |
| T-IPC-02 | 1000 neispravnih i prevelikih poruka | A | odbijene; 0 prekida zvuka |
| T-IPC-03 | Prekid i povezivanje ×100 | A | ispravan snimak stanja svaki put |
| T-IPC-04 | Ponovljena komanda sa istim ID-jem | A | izvršena jednom |
| T-IPC-05 | Admin komanda bez važeće sesije | A | odbijena |
| T-IPC-06 | Povezivanje drugog naloga ili preko mreže | A/C | odbijeno |
| T-IPC-07 | Različite verzije protokola | A | jasna poruka; komande se ne šalju |
| T-PRE-01 | Preslušavanje neproverene stavke u radno vreme | A | nikad na uređaju za emitovanje |
| T-EOD-01 | Računar ugašen odmah posle 23:30 | C | propušteni poslovi urađeni pri sledećem startu; muzika u 07:00 |

### Interfejs, jezik, backup
T-UI-01 svako dugme ima funkciju · 02 PIN · T-TCH-01 sve kontrole upotrebljive prstom, bez tastature i miša · T-TCH-02 monitor ugašen pa upaljen: ispravan prikaz · T-L10N-01 svi tekstovi na oba jezika · T-BAK-01 izvoz pa uvoz na drugom računaru · 02 vraćanje oštećene baze · 03 svaki backup proveren posle pravljenja.

**Pravilo:** u izveštajima postoje samo tri stanja: „prošao (pokrenuto, datum, gde)“, „pao“, „napisano, nepokrenuto“.

---

## 20. Preostali rizici

| # | Rizik | Ocena | Mera |
|---|---|---|---|
| R1 | Premalo legalne proverene muzike | **visok** | D9; prikupljanje odmah; pitati SOKOJ/OFPS da li postojeće naknade pokrivaju i legalno kupljenu muziku, što bi olakšalo izbor |
| R2 | 4 GB RAM-a uz Windows 11 | **visok** | mali Engine, merenje, U1, U9 |
| R3 | Kod se ne može prevesti u mom okruženju | sigurno | U6; inače ručni build kod vas |
| R4 | Sopstvena analiza ne prođe pragove | srednji | U4 |
| R5 | Kvalitet usklađivanja tempa | srednji | U3 |
| R6 | HDMI zvuk nestaje sa monitorom | srednji | 3,5 mm ili USB; ponovno povezivanje |
| R7 | Windows ažuriranja i Defender tokom dana | srednji | aktivni sati; podešavanja |
| R8 | Srpski izgovor AI glasa | srednji | U5 |
| R9 | BIOS nema opciju za povratak struje | [?] | U7; UPS |
| R10 | Windows „N“ izdanje | [?] | U8 |
| R11 | Nagli nestanci struje oštete SSD ili bazu | nizak | WAL, backup, UPS |
| R12 | Promena uslova TTS servisa | nizak | dokaz uz svaku poruku |
| R13 | Obim projekta | visok | faze sa kriterijumima; prvo radna najmanja verzija |
| R15 | Računar se gasi noću, pa analiza i priprema prelaza ne stižu | [?] | U12 |
| R16 | Daljinski pristup ili drugi nalog preuzme sesiju i zvuk | nizak | pravila iz dela 2.2 |
| R14 | Kvar hardvera | – | softver ne može da ga spreči; backup i selidba (deo 14) |

## 21. Otvorena pitanja

Spisak je premešten u deo 26.

---

## 22. Tri dana sa Fable 5.2 (13–16.11.2026.)

Cilj: zatvoriti delove gde su greške u dizajnu najskuplje, i izmeriti mini PC.

| Dan | Posao | Šta zavisi od vas |
|---|---|---|
| 1 | Odobrenje v4.1 · M0 temelj · automatski build · početak M1 (dekoderi, deck, mixer, WAV izlaz) | odobrenje; GitHub ili Visual Studio; podaci iz dela 26 |
| 2 | M1: Standard crossfade, glasnoća, limiter, WASAPI, offline renderer, testovi T-AUD · Bench.exe | pokretanje Bench-a na mini PC-ju (~15 min) |
| 3 | M2: raspored, rotacija, kapija licenci, raspored glasa, sa testovima · ispravke po rezultatima Bench-a · nacrt pravila DJ Mix-a kao čist kod sa testovima | pokretanje testova ako nema automatskog build-a |

**Realno ostvarivo [P]:** M0, M1 i M2 napisani; prošli onoliko testova koliko je stvarno pokrenuto; prvi izmereni brojevi sa mini PC-ja.
**Nije realno za tri dana:** interfejs, analiza, DJ Mix uživo, LUSTRE VOICE, instaler.
Ako build ne bude dostupan (ni GitHub ni Visual Studio), kod će biti napisan ali **nepokrenut**, i tako će biti i označen.

---

## 23. Izmene u odnosu na v3.1

| # | Oblast | v3.1 | v4 |
|---|---|---|---|
| 1 | Procesi | jedan WPF program + Watchdog | Engine bez prozora + UI + Watchdog |
| 2 | Nadzor | uzajamni, tri nivoa | jednosmeran lanac |
| 3 | Task Scheduler | podrazumevana podešavanja | podignut prioritet, bez limita od 72 h |
| 4 | Baza | jedan proces | upisuje samo Engine; UI čita |
| 5 | Dekoder | glavni i rezervni | izbor pri skeniranju, isti za analizu i reprodukciju |
| 6 | DJ Mix | ±4% bez promene tempa | tri slučaja (0,3% / 2–3% / Smart) + stabilnost tempa |
| 7 | Time-stretch | opciono ±2% uživo, isključeno | samo unapred pripremljen, uz očuvanje visine tona, uslovno |
| 8 | Glasnoća | −16 LUFS | −18 do −20 LUFS, ograničeno pojačanje |
| 9 | Glasnoća sistema | nije definisano | aplikacija kontroliše; sistemska fiksna |
| 10 | Rotacija | 7 dana fiksno + kompatibilni žanrovi | prilagodljivo + Zdravlje biblioteke |
| 11 | Licence | polje „izmene“ | prošireno na tempo i obrađene kopije; jedna kapija za sve puteve |
| 12 | Offline | bez mrežnih poziva | + arhitektonski test + zaštitni zid |
| 13 | Windows zvuci | nije pomenuto | isključeni |
| 14 | Interfejs | miš i dodir, 44 px | prvenstveno dodir, 56 px, sopstvena tastatura za PIN, softversko crtanje |
| 15 | Budžet | jedan proces ≤ 150 MB | zbir tri procesa ≤ 240 MB, meri se |
| 16 | Vreme | zidni sat | zidni sat za raspored, monotoni za trajanja |
| 17 | Testovi | po fazama | offline renderer, veštački signali, stvarni uređaj, ~70 testova sa kriterijumima |
| 18 | Redosled analize | LUFS, BPM, Key… | po koristi; dodata stabilnost tempa |
| 19 | Plan | 15 faza | 13 faza, prvo najmanja radna verzija |
| 20 | Analiza na drugom računaru | opciono | opciono, uz uslov istog dekodera |
| 21 | Backup | noćni | + provera svakog backupa |
| 22 | Računar | nepoznat tip | mini PC, monitor na dodir, BIOS provera, preporuka UPS |
| 23 | Odluke | otvorena pitanja | odobrene i uslovne odluke razdvojene |
| 24 | Alati | – | GitHub (uslovno), NetArchTest, FlaUI, Rubber Band kao opcija |

**Ništa nije izbačeno.** Jedina funkcija sa promenjenim ponašanjem je DJ Mix (stavke 6 i 7), uz vaše odobrenje.

---

## 24. Izmene u odnosu na v4

| # | Oblast | v4 | v4.1 |
|---|---|---|---|
| 1 | Naziv | „audio servis“ | „audio proces“; izričito nije Windows servis, ne radi u Session 0 |
| 2 | Pokretanje | Task Scheduler, opšte | tačna podešavanja zadatka; obavezno „Run only when user is logged on“; jedan izvor pokretanja |
| 3 | Windows nalog | nije definisano | poseban standardni nalog sa automatskom prijavom (U14) |
| 4 | Provera sesije | nema | Engine i Watchdog odbijaju rad u sesiji 0 |
| 5 | Watchdog | pokreće programe | i preuzima postojeće; njegov pad ne gasi Engine |
| 6 | Signal života | „nema signala 30 s“ | stanje + brojač uzoraka; pauza i mirovanje nisu kvar |
| 7 | Namerno zaustavljanje | nije definisano | oznaka koju Watchdog poštuje |
| 8 | Sesija | nije definisano | pravila za zaključan ekran, Remote Desktop, drugi nalog |
| 9 | Komunikacija | „named pipe, JSON“ | ceo deo 2.6: protokol, snimak stanja, redovi, prekid veze |
| 10 | PIN | u interfejsu | proverava Engine; admin sesija sa rokom |
| 11 | Novi fajlovi | nije definisano | UI ostavlja u `incoming\`, Engine obrađuje i upisuje |
| 12 | API ključ | „u adminu“ | u fajlu UI-ja, nikad u bazi ni kod Engine-a |
| 13 | Preslušavanje | „kroz isti audio lanac“ | radi UI, na zasebnom uređaju ili van radnog vremena |
| 14 | Noćni poslovi | 23:35–06:45, pretpostavka da računar radi noću | poslovi na kraju dana + nadoknada pri startu; analiza uslovno (U12) |
| 15 | Pripremljeni prelazi | po paru | plan dana unapred; odstupanje → uživo ili Smart |
| 16 | Provera heša | „pri promeni“ | brza provera pre puštanja; pun heš pri skeniranju i na kraju dana |
| 17 | Crtanje interfejsa | softversko | bira se merenjem (U11) |
| 18 | Mini PC | lista | tabela; namenski računar lokala, nije privatni |
| 19 | Zaštitni zid | Engine | Engine i Watchdog |
| 20 | Testovi | ~70 | + 15 (sesija, komunikacija, preslušavanje, kraj dana) |
| 21 | Uslovne odluke | U1–U10 | + U11–U14 |

Ništa nije izbačeno.

---

## 25. Provera kontradikcija

| # | Kontradikcija u v4 | Rešenje u v4.1 |
|---|---|---|
| K1 | „Audio servis“ je mogao značiti Windows servis, koji nema pouzdan pristup zvuku | Običan proces u korisničkoj sesiji; provera sesije; test T-SES-01 |
| K2 | Preslušavanje nove poruke „kroz isti audio lanac“ kršilo bi pravilo da neproverena stavka ne ide u eter, a gosti bi je čuli | Preslušava UI, na zasebnom uređaju ili van radnog vremena |
| K3 | Samo Engine upisuje u bazu, a UI generiše poruke, dodaje dokaze i čuva API ključ | Fajlovi idu preko `incoming\` i komande; ključ je u fajlu UI-ja |
| K4 | PIN u interfejsu, a pravila u Engine-u: svaki lokalni program mogao bi da pošalje admin komandu | PIN proverava Engine |
| K5 | „Noćni“ backup, analiza i rezervna lista, a zaposleni „samo uključe računar“, pa je on noću verovatno ugašen | Poslovi na kraju dana + nadoknada pri startu; U12 |
| K6 | Pripremljeni prelazi traže poznat par, a izbor pesama je nasumičan i zaposleni imaju Next | Plan dana; odstupanje → uživo ili Smart |
| K7 | Rezervni režim bez baze, a kapija licenci se oslanja na bazu | Rezervna lista nosi heševe i podatke za brzu proveru |
| K8 | Zaštitni zid blokira Engine, a Engine razgovara sa UI-jem | Lokalni kanal ne koristi mrežu; pravilo ga ne dotiče |
| K9 | Watchdog pokreće Engine: pad Watchdoga mogao bi povući Engine, a novi Watchdog pokrenuti drugi | Bez vezivanja života; preuzimanje; mutex |
| K10 | Softversko crtanje radi stabilnosti, a monitor na dodir traži brz odziv | Odluka merenjem (U11) |
| K11 | „Potpuno offline“, a tačan sat i Windows ažuriranja zavise od interneta | Ako računar nema internet: ručna provera sata, ažuriranja po dogovoru; ako ima: aktivni sati i zaštitni zid |
| K12 | Automatska prijava bez lozinke, a admin pod PIN-om | Standardni nalog; jasno navedene granice zaštite |
| K13 | Analiza na drugom računaru, a pravilo istog dekodera | Već uslovljeno u 3.4; DJ poravnanje tek posle potvrde |

Nisam našao kontradikciju koja bi tražila izbacivanje neke funkcije. K2 i K5 menjaju način upotrebe, pa zavise od vaših odgovora (deo 26).

---

## 26. Šta mora da se proveri pre početka rada

**Na mini PC-ju:**
1. Izdanje Windowsa (da li piše „N“), 64-bit, verzija.
2. Memorija zauzeta u mirovanju; polje „Slots used“.
3. Slobodan prostor na SSD-u.
4. Kako je povezan zvuk: 3,5 mm, HDMI preko monitora ili USB.
5. Da li na njemu radi još nešto (kasa, pretraživač).
6. BIOS: opcija „Restore on AC Power Loss“ / „AC Power Recovery“.
7. Model i rezolucija monitora na dodir.
8. Da li mini PC ima internet (K11).
9. Da li ostaje uključen noću ili se gasi (U12).
10. Postoji li drugi audio izlaz za preslušavanje, npr. slušalice (U13).
11. Može li se napraviti poseban standardni Windows nalog (U14).

**Za razvoj:**
12. GitHub nalog povezan sa Claude-om i prazan privatni repozitorijum, ili Windows računar sa Visual Studiom za ručni build. Stanje 13.11.2026.: GitHub nije povezan.

**Odluka:**
13. Odobrenje v4.1.

Stavke 1, 4, 8, 9 i 12 utiču na prve faze. Ostale mogu da stignu do faze M4.

---

## 27. Potvrđeno posle odobrenja (13.11.2026.)

| Stavka | Stanje | Posledica |
|---|---|---|
| Specifikacija v4.1 | odobrena | sledi faza M0 |
| Mini PC radi 24/7 | potvrđeno | U12 ispunjena: analiza, priprema prelaza, backup i plan dana rade noću. Dodato: planirani nedeljni restart programa noću (npr. 04:00); test dugog rada produžen na 7 dana |
| Mini PC ima internet | potvrđeno | sat se sam usklađuje; Windows ažuriranja samo noću; pravilo zaštitnog zida za Engine i Watchdog je obavezno |
| Zvuk | **Bluetooth zvučnik sa vuferom; ne gasi se sam** | vidi ispod |
| Razvoj | na drugom računaru; na mini PC ide samo instalacija | – |
| GitHub | `emilhadzic/lustre-radio`, privatni, povezan | U6 ispunjena za build i automatske testove (vidi ispod) |

**Bluetooth zvuk (novi rizik R17, srednji):**
- Prekide Bluetooth veze i pucketanje softver ne može da otkrije ni popravi; program tada misli da muzika svira.
- Engine mora da preživi nestanak i povratak Bluetooth uređaja i da sam nastavi.
- Tokom radnog vremena veza se drži otvorenom i u pauzama.
- U Windowsu se za zvučnik isključuje „Hands-Free“ režim; koristi se samo „Stereo“.
- Ekran za zaposlene prikazuje „Zvučnik nije povezan“.
- Preporuka ostaje kabl (AUX), ako zvučnik ima ulaz [?].
- Novi testovi: T-BT-01 zvučnik ugašen pa upaljen → muzika se vraća sama; T-BT-02 zvučnik van dometa pa nazad; T-BT-03 jutarnji start posle noći bez dodira; T-BT-04 „Hands-Free“ se ne aktivira.

**Proba automatskog build-a (pokrenuto 13.11.2026., GitHub Actions, prošlo):**
- Windows Server 2025, .NET SDK 10.0.404; build i 2 probna testa prošli za ~1 min.
- Media Foundation biblioteke postoje na tom računaru [Č]; da li dekodiranje MP3/AAC/FLAC tamo stvarno radi, još nije isprobano [?].
- Audio uređaja nema (0) [Č]: stvarna reprodukcija se tamo ne može testirati. Testovi tipa U i C ostaju za pravi računar.

**Dopuna 13.11.2026. uveče:**
- Windows na mini PC-ju: **Windows 11 Home, nije „N“** → U8 ispunjena.
- **Na mini PC-ju radi i POS program (kasa).** Posledice:
  - POS ima prednost: radio ne sme da ga uspori. Audio proces troši malo, a analiza radi samo noću, najnižim prioritetom [P, mora se izmeriti uz POS].
  - Memorija od 4 GB se deli sa POS-om → rizik R2 raste; preporuka za 8 GB je jača.
  - Monitor na dodir verovatno ceo dan prikazuje POS, pa interfejs radija ne može biti stalno na ekranu [?]. Otvara se po potrebi, a muzika ne zavisi od njega (to arhitektura već obezbeđuje).
  - Poseban Windows nalog (U14) verovatno nije moguć, jer POS određuje nalog [?].
  - Isključivanje Windows zvukova (D11) ne sme da ugasi zvuke koje POS možda koristi [?].
- **Testiranje:** mini PC nije dostupan za duže testove tokom dana. Testovi se rade na računaru za razvoj, uz režim „slab računar“ (ograničen broj jezgara, brzina i memorija). Taj režim je procena, ne zamena: završni testovi na mini PC-ju rade se noću (23:30–07:00), kad lokal ne radi, i ne traže ničije prisustvo.

**Odluka vlasnika 14.11.2026. (menja deo 9 i D10):**
- Vlasnik je izjavio da ima potrebna prava i da plaća SOKOJ i OFPS, i tražio da pesme sviraju na tom osnovu.
- Pesma sme u eter i na osnovu **izjave vlasnika o kolektivnoj licenci**. Program beleži organizacije, ko je i kada dao izjavu. Odgovornost za tačnost izjave je na vlasniku.
- Nove pesme u folderima sa muzikom automatski dobijaju status „Provereno“ po toj izjavi.
- Ostaje isto: bez zapisa o osnovu ništa ne svira; odbijena pesma ostaje odbijena; zamenjen fajl gubi status; unapred pripremljeni prelazi (obrađene kopije) traže posebnu potvrdu da su izmene dozvoljene.
- Ranije pravilo „nikad se ne pretpostavlja pokrivenost SOKOJ/OFPS“ zamenjeno je pravilom „pokrivenost se prihvata na osnovu izričite izjave vlasnika“.

**Još otvoreno:** koji POS program, da li je preko celog ekrana i da li koristi zvuk; AUX ulaz na zvučniku; memorija u mirovanju i „Slots used“; slobodan disk; drugi programi na mini PC-ju; BIOS opcija za struju; model monitora; izlaz za preslušavanje; poseban Windows nalog.

---

## Prilog A – Primeri poruka (nacrt)
- Ident: „Lustre Caffe Radio.“ · „Slušate Lustre Caffe Radio.“ · „You're listening to Lustre Caffe Radio.“
- Welcome: „Dobro došli u Caffe Lustre. Vaš trenutak elegancije.“ · „Take a moment. Enjoy the atmosphere. You're listening to Lustre Caffe Radio.“
- Long: „Dobro jutro i dobro došli u Caffe Lustre. Neka vaša kafa bude topla, razgovor lagan, a ovaj trenutak samo vaš. Opustite se i uživajte. Lustre Caffe Radio.“

## Izvori (provereno 13.11.2026.)
- Media Foundation formati: https://learn.microsoft.com/en-us/windows/win32/medfound/supported-media-formats-in-media-foundation
- FLAC/ALAC/Opus od Windows 10: https://learn.microsoft.com/en-us/Windows/Win32/medfound/audio-subtype-guids
- NAudio.Vorbis: https://github.com/NAudio/Vorbis
- ElevenLabs jezici: https://elevenlabs.io/docs/help-center/other/what-languages-do-you-support
- ElevenLabs komercijalna upotreba: https://elevenlabs.io/docs/help-center/legal/can-i-publish-the-content-i-generate-on-the-platform
- Azure srpski glasovi: https://techcommunity.microsoft.com/t5/azure-ai/azure-cognitive-services-releases-new-languages-and-voices-for/ba-p/3177458
