# Stanje razvoja

| Faza | Stanje | Dokaz |
|---|---|---|
| M0 Temelj | završeno 13.11.2026. | GitHub Actions (Windows Server 2025, .NET 10): build, 31 test (5 Core, 26 arhitektonskih), smoke pokretanje svih 5 programa. Nije pokrenuto na mini PC-ju. |
| M1 Audio jezgro | **zatvorena 13.11.2026. po odluci vlasnika, sa otvorenim stavkama** | Automatski testovi prošli; stvarni zvuk prošao na računaru vlasnika. Otvoreno (prenosi se dalje): ponovno povezivanje na pravom uređaju i Bluetooth (T-BT) nepokrenuti; fiksna sistemska glasnoća; OGG Opus; rezervni FLAC dekoder; ništa nije pokrenuto na mini PC-ju; duža proba nije rađena; režim „slab računar“ preskočen. |
| M2 Logika | **završena 13.11.2026.** | GitHub Actions: 93 testa logike prošla. Kriterijum iz specifikacije (T-SCH, T-ROT, T-LIC, T-VOI-03) ispunjen na nivou čiste logike; povezivanje sa bazom i engine-om dolazi u M3. |
| M3 Engine i baza | **završena 14.11.2026. (logika)**, sa otvorenim stavkama | GitHub Actions: 279 testova prošlo. Radio svira po rasporedu iz baze (proba vlasnika 14.11.). Otvoreno: veza sa interfejsom nije probana sa pravim interfejsom (ne postoji do M5); nastavak posle restarta i pauza nisu probani slušanjem; rezervni nivoi 4 i 6 (keširana lista, folder „Rezerva“) dolaze u M11; biranje pesama unapred. |
| M4 Nadzor i pokretanje | **automatski deo završen 14.11.2026.**; proba na pravom računaru nije rađena | GitHub Actions: 297 testova + integracioni test sa pravim procesima. Otvoreno: pokretanje posle prijave/paljenja računara, odbijanje sesije 0 kod pravog servisa, „zvuk stoji“ sa pravim zvukom, sigurni režim u radu, rad 72 h na mini PC-ju. |
| M5 Interfejs | **prvi deo završen; vlasnik probao 15.11.2026. na svom računaru** | GitHub Actions: 412 testova, ekrani nacrtani i provereni. Proba vlasnika (miš, slušalice): radi. Otvoreno: monitor na dodir (T-TCH), mini PC, kod za oporavak PIN-a, dnevnik izmena, omot albuma, sledeća pesma, „Mirnije/Življe“. |
| M6 Analiza | **u toku: glasnoća (LUFS) gotova 15.11.2026.** | GitHub Actions: 433 testa. Sledeće: tišina na krajevima, outro/intro, BPM, tonalitet, vokal. |
| M7–M12 | nije započeto | – |

Napomena: programi su u M0 samo kosturi bez funkcija.

## M1 po koracima

| Korak | Stanje | Dokaz |
|---|---|---|
| 1. Deck, Standard Crossfade, limiter, izlaz u memoriju/WAV, offline renderer | završeno 13.11.2026. | GitHub Actions: 13 testova prošlo (T-AUD-01, 02, 03, 05 na veštačkim signalima; rad bez zauzimanja memorije; nezavisnost od veličine bloka). Provera testova: namerno pokvaren kod → 4 testa pala. |
| 2. Dekoderi i matrica formata (T-AUD-04, T-AUD-06) | završeno 13.11.2026., osim Opus i libFLAC | GitHub Actions (Windows Server 2025): 60 audio testova prošlo. Nije pokrenuto na Windows 11 ni na mini PC-ju. |
| 3. Glasnoća po pesmi, ograničeno pojačanje (T-AUD-07) | završeno 13.11.2026. | GitHub Actions: testovi prošli. Merenje LUFS-a same pesme dolazi u fazi M6; do tada se koristi podrazumevano pojačanje. |
| 4a. Kružni bafer, nit za pripremu zvuka | završeno 13.11.2026. | GitHub Actions: put kroz nit daje isti zvuk kao offline renderer; test sa dve niti (2 miliona uzoraka) bez gubitka. |
| 4b. WASAPI izlaz (osnovni) | **prošao na računaru vlasnika, 13.11.2026. 20:06** | Bench, slušalice HyperX (48 kHz), Windows 11: 20,1 s, 0 prekida, ocena slušanjem „d“. Samo veštački tonovi; prava muzika i duži rad još nisu probani. |
| 4c. Ponovno povezivanje uređaja, promena podrazumevanog uređaja (T-AUD-09, 10) | logika **prošla na lažnom uređaju** (GitHub Actions, 13.11.2026.); **na pravom uređaju nepokrenuto** | 5 testova: nestanak i povratak, promena podrazumevanog, bez uređaja na startu, neuspela otvaranja. Muzika nastavlja bez gubitka i ponavljanja. Čeka probu izvlačenjem slušalica (Bench) i Bluetooth (T-BT) na mini PC-ju. |
| 4d. Fiksna sistemska glasnoća i mute (T-AUD-11) | nije započeto | – |
| 5a. Bench.exe + paket za preuzimanje | završeno 13.11.2026. | vidi rezultate ispod |
| 5b. Režim „slab računar“ | **preskočeno po odluci vlasnika (13.11.2026.)** | Posledica: nema procene za Celeron pre testova na mini PC-ju. |
| Duža proba (1 sat) na računaru vlasnika | **nije rađena, po odluci vlasnika (13.11.2026.)** | Nije označena kao prošla. Dug rad se prvi put proverava noćnim testom na mini PC-ju (M4). |
| – OGG Opus, rezervni FLAC dekoder | nije započeto | – |

Nijedan test još nije pokrenut na stvarnom audio uređaju ni na mini PC-ju.

## Izmereno u koraku 2 (GitHub Actions, Windows Server 2025, 13.11.2026.)

Probni fajlovi: poznat signal od 4 s (testdata/audio), po jedan ili više za svaki format.

| Format | Dekoder | Dekodiranje | Kašnjenje početka | Nastavak pesme (ResumeAt) |
|---|---|---|---|---|
| WAV 16-bit / float | NAudio | tačno u uzorak | 0 | – |
| FLAC 44,1 kHz | Media Foundation | tačno u uzorak | 0 | tačno (dekodiranjem; brzo premotavanje promaši > 1 s) |
| FLAC 96 kHz → 44,1 | MF + resampler | sličnost 0,9995 | 1 frejm | tačno |
| MP3 CBR / sa slikom albuma | MF | sličnost 1,0000 | 1729 frejmova (39 ms) | −15 ms |
| MP3 VBR | MF | 0,99997 | 577 frejmova | −145 ms |
| MP3 48 kHz → 44,1 | MF + resampler | 0,998 | 1589 frejmova | −8 ms |
| MP3 mono | MF | 1,0000 | 1729 frejmova | −15 ms |
| MP3 (rezervni dekoder) | NLayer | 1,0000 | 1105 frejmova (25 ms) | – |
| M4A (AAC) / AAC ADTS | MF | 0,9999 | 1024 frejma (23 ms) | +20 ms |
| OGG Vorbis | NVorbis | 0,9999 | 0 | 0 |
| OGG Opus | – | još nije podržan; javlja jasnu grešku | – | – |

Zaključci:
- Tačno preskakanje (SkipFrames) daje iste uzorke kao dekodiranje od početka za sve formate (razlika 0 ili 3e-8).
- MF i NLayer za isti MP3 počinju 624 frejma (14 ms) različito: potvrđeno pravilo D4 (isti dekoder za analizu i reprodukciju).
- Dekoderi najavljuju dužinu različitu od stvarne (MP3: +12 ms; M4A: −40 ms). Tačna dužina mora doći iz probnog dekodiranja pri skeniranju (faza M3).
- Media Foundation dekodira MP3, AAC i FLAC na Windows Server 2025 runner-u. Na Windows 11 Home još nije pokrenuto.

## Odluke vlasnika posle odobrenja specifikacije

- POS radi preko celog ekrana i ne koristi zvuk. **Ništa se ne prikazuje preko kase**; interfejs radija se otvara posebno, po potrebi.
- Windows zvuci i obaveštenja se isključuju (POS ih ne koristi).
- Vlasnik nema razvojni računar. Build radi GitHub; vlasnik preuzima gotov folder. Testovi stvarnog zvuka: njegov Windows računar sa slušalicama. Mini PC: samo kratko merenje i noćni završni testovi.

## Bench na GitHub runner-u (AMD EPYC, 2 jezgra, 8 GB; 13.11.2026.)

Ovo NIJE mini PC; brojevi služe samo kao polazna tačka za poređenje.

- Indeks brzine procesora: 220 ms.
- Dekodiranje: MP3 135–381×, AAC/M4A 138–239×, FLAC 92–147×, OGG 87× brže od slušanja.
- Priprema prelaza: 703 s zvuka za 2,03 s (346×); zauzeto 29 KB memorije tokom pripreme.
- Memorija programa: radni skup 66 MB, privatno 34 MB.

## Bench na računaru vlasnika (13.11.2026. 20:06)

Windows 11 (10.0.26300), AMD Ryzen 9 5900X (24 logička jezgra), 64 GB. Ovo NIJE mini PC.

- Indeks brzine procesora: 119 ms (GitHub runner: 220 ms; mini PC: još nije izmereno).
- Dekodiranje preko Media Foundation radi na Windows 11: MP3 232–751×, AAC/M4A 266–535×, FLAC 213–391×, OGG 143×.
- Priprema prelaza: 714×; 29 KB zauzeto; memorija programa 69–71 MB radni skup, 35 MB privatno.
- Stvarna reprodukcija (WASAPI Shared, uređaj na 48 kHz): 6 tonova, 5 prelaza, 0 prekida, završeno samo, prelazi glatki po oceni vlasnika.
- Merenje procesora tokom reprodukcije pokazalo 0,0 %: na 24 jezgra opterećenje je ispod rezolucije merenja.

## Proba sa pravom muzikom na računaru vlasnika (13.11.2026. 20:16)

- 5 MP3 pesama sa Pixabay-a (jedna 48 kHz, četiri 44,1 kHz pretvorene u 48 kHz), po 25 s, prelazi 5 s.
- Pesme su krenule na 0, 20, 40, 60 i 80 s, tačno po planu; ukupno 105,1 s (plan 105 s); završeno samo.
- 0 prekida zvuka; memorija 68 MB radni skup, 34 MB privatno; ocena slušanjem „d“.
- Nije probano slušanjem: FLAC, M4A, AAC, OGG, WAV; rad duži od 2 minuta; glasnoća po pesmi (sve su puštene sa istim podrazumevanim utišanjem).

## M2 po koracima

| Korak | Stanje | Dokaz |
|---|---|---|
| 1. Satovi, raspored (21 slot, izuzeci, ručni izbor), kapija licenci, rotacija, Zdravlje biblioteke | završeno 13.11.2026. | GitHub Actions: 62 testa logike prošla (T-SCH-01..10, T-LIC-01..04 na nivou kapije, T-ROT-01..04). Provera testova: namerno pokvaren raspored, kapija i rotacija → 9 testova palo. |
| 2. Raspored glasovnih poruka (T-VOI-03), izbor prelaza po D6 (T-TRN-05, 06 na nivou odluke), pravila rezervnih režima | završeno 13.11.2026. | GitHub Actions: 31 novi test. Dva testa su pala u prvom pokušaju: jedan je otkrio pravu grešku (ista kratka poruka mogla se ponoviti preko granice dana, ispravljeno), drugi je imao pogrešno očekivanje (ispravljen test). |

Napomene uz korak 1:
- Raspored podržava radno vreme unutar jednog dana (zatvaranje pre ponoći).
- Ručni izbor programa ne otvara radio van radnog vremena; za to služi izuzetak (događaj).
- Kad pesama nema dovoljno, bira se nasumično među četvrtinom najduže nepuštanih (ne strogo najstarija), da se redosledi ne ponavljaju.
- „Zdravlje biblioteke“ računa svaki slot za sebe; ako iste pesme odgovaraju većem broju slotova, stvarni razmak je manji od procene.
- T-LIC-01 („kroz sve puteve“) je ovde dokazan za kapiju i rotaciju; za ručni izbor, rezervnu listu i glas dokazuje se kad ti delovi nastanu (M3, M10, M11).
- Željeni razmak ponavljanja glasovnih poruka (6 h kratke, 24 h duže) poštuje se kad poruka ima dovoljno; inače se bira najduže nepuštana, ali nikad ista dvaput zaredom. Za pun razmak treba oko 8 kratkih i 7 dužih poruka.
- Sezonska, praznična ili događajna poruka ima prednost jednom u svom razmaku od 24 h (klizno), ne nužno kao prva duža poruka svakog dana.
- Posle duže poruke kratka identifikacija ne ide bar 45 minuta.

## M3 po koracima

| Korak | Stanje | Dokaz |
|---|---|---|
| 1. Baza (SQLite, WAL), skladišta, kopija i provera; skener biblioteke | završeno 13.11.2026. | GitHub Actions: 12 testova baze + 14 testova skenera prošlo (ukupno 217). |
| 2. Radio koji sam svira: indeksiranje foldera, reditelj (raspored + rotacija + kapija licenci), stišavanje na zatvaranju, snimak stanja i nastavak, dugme Next | **logika završena 13.11.2026.; sa pravim uređajem nepokrenuto** | GitHub Actions: 14 testova celog lanca bez uređaja (vreme teče sa zvukom). Čeka probu vlasnika (LustreRadio-Proba). |
| 3. Veza interfejsa i audio procesa (lokalni kanal), komande, PIN i admin sesije, pauza, glasnoća | završeno 14.11.2026. | GitHub Actions: 11 testova kanala (T-IPC-01, 02, 03, 04, 07), 9 testova komandi (T-IPC-05), 5 testova PIN-a, 6 testova pauze i glasnoće. T-IPC-06 (drugi nalog, mreža) nije automatizovan: oslanja se na Windows (CurrentUserOnly). |

Napomene uz korak 1:
- Raspored i pravila se čuvaju kao JSON u tabeli `settings`, ne u posebnim tabelama (jednostavnije; ponašanje je isto). Provereno: raspored posle čuvanja daje iste odgovore.
- Baza ne koristi Dapper iz specifikacije, već direktne upite (manje zavisnosti, isti rezultat).
- Status „Provereno“ se ne može upisati mimo pravila ni direktno kroz skladište (test).
- Skener meri tačnu dužinu i vrh dekodiranjem celog fajla. Izmereno: AAC vraća vrhove do −2,8 dBFS za signal od −6 dBFS, pa se vrh ne sme pretpostaviti.
- Duplikati se prepoznaju po istom sadržaju fajla (heš); prepoznavanje iste pesme u različitim formatima (akustički otisak) nije urađeno.

Napomene uz M3 korak 2:
- Nova biblioteka `LustreRadio.Playout` sadrži logiku radija; `Engine` (proces bez prozora) i `Proba` (probna konzola) je samo pokreću. Razlog: samostalni program ne može da koristi drugi program kao biblioteku.
- Testovi su u prvom pokušaju otkrili pravu grešku: posle 23:30 nije birana sledeća pesma, pa bi muzika stala čim se tekuća završi, usred stišavanja. Ispravljeno: tokom stišavanja se biraju pesme iz poslednjeg programa.
- Pre svakog puštanja proverava se da fajl nije menjan posle skeniranja (veličina i datum); izmenjen fajl se ne pušta dok se ponovo ne skenira i ne proveri licenca.
- Upis istorije u bazu radi se na niti koja priprema zvuk (zaštićeno baferom od ~0,7 s). Biranje nekoliko pesama unapred, kako piše u specifikaciji 2.6, još nije urađeno.
- `ProbeLicensing` i „uvek otvoreno“ postoje samo za probu; pravi Engine ih ne koristi.
- Engine još nema nadzor (M4), vezu sa interfejsom (M3 korak 3) ni podešavanje foldera sa muzikom kroz interfejs.

## Odluka vlasnika 14.11.2026.: izjava o kolektivnoj licenci

Vlasnik je izjavio da ima potrebna prava i da plaća SOKOJ i OFPS, i tražio da pesme sviraju na tom osnovu.

- Pesma sme u eter i na osnovu **izjave vlasnika o kolektivnoj licenci** (tip „Kolektivna licenca (izjava vlasnika)“). Program beleži organizacije, ko je i kada dao izjavu; odgovornost za tačnost izjave je na vlasniku.
- Uz izjavu sa automatskom primenom, nove pesme u folderima sa muzikom same dobijaju status „Provereno“.
- Kapija licenci ostaje jedina i ista: bez zapisa o osnovu ništa ne svira. Pesma koju je admin odbio ostaje odbijena. Zamenjen fajl i dalje gubi status.
- Unapred pripremljeni prelazi (obrađene kopije) i dalje traže posebnu potvrdu da su izmene dozvoljene; bez nje se koristi Smart.
- Ovo menja deo 9 i odluku D10 specifikacije v4.1 u tački „nikad se ne pretpostavlja pokrivenost SOKOJ/OFPS“: sada se pokrivenost prihvata na osnovu izričite izjave vlasnika.
- Privremeno označavanje „za probu“ je uklonjeno; Proba koristi istu izjavu.
- Dokaz: GitHub Actions, 14.11.2026., 238 testova prošlo (4 nova za izjavu).

## Proba radija na računaru vlasnika (14.11.2026. 07:33–07:41)

Windows 11, slušalice HyperX (48 kHz), 5 MP3 pesama, verzija sa izjavom o kolektivnoj licenci.

- Skeniranje 5 pesama: 2 s. Izjava data → 5 pesama potvrđeno. Emitovanje krenulo samo.
- Program „Subota - jutro“ u 07:34 (subota): raspored je izabrao ispravan slot.
- Pesme su se smenjivale na 07:36:16, 07:38:15, 07:40:16 (cele pesme, prelaz 6 s).
- Taster N u 07:41:13: sledeća pesma je krenula iste sekunde.
- **Nije probano:** nastavak iste pesme posle ponovnog pokretanja (u dnevniku je samo jedno pokretanje); izvlačenje slušalica.
- **Nađena greška:** pesme su išle abecednim redom. Uzrok: pesme bez žanra u oznakama su na nivou „bilo koja“, a tamo izbor nije bio nasumičan. Ispravljeno 14.11. (dva nova testa; GitHub Actions, 241 test prošao). Ispravka još nije potvrđena slušanjem.
- Dodato: kad fajl nema žanr u oznakama, žanr je naziv podfoldera (npr. `muzika\Deep House\...`).

Napomene uz M3 korak 3:
- Svaka poruka stanja nosi celo stanje (nema delimičnih izmena): jednostavnije i otporno na izgubljenu poruku.
- Klijent koji ne čita poruke isključuje se kad mu se red napuni (256 poruka); slanje nikad ne čeka (test: 20.000 poruka za manje od 5 s).
- Ista komanda (isti ID) izvršava se jednom; najviše 50 komandi u sekundi po klijentu.
- PIN proverava audio proces; prvo postavljanje PIN-a je slobodno, svaka promena traži admin sesiju. Kod za oporavak PIN-a još nije urađen.
- Pauza se sama prekida posle 15 minuta; ako radno vreme istekne tokom pauze, emitovanje se završava.
- Test pauze je otkrio pravu grešku: zbog zaokruživanja izvor je čitan još jednom posle stišavanja, pa bi muzika pri nastavku bila pomerena za jedan blok. Ispravljeno.
- Komande za raspored, licence, mikseve i glas dodaju se kad nastanu odgovarajući ekrani.

## M4: nadzor i pokretanje (14.11.2026.)

Dokaz za sve stavke „prošao“: GitHub Actions, Windows Server 2025, 14.11.2026., pokretanje 43588860869; 297 testova + integracioni test `tests/integration/nadzor.ps1` sa pravim procesima (bez zvučnog uređaja).

| Stavka | Stanje |
|---|---|
| Pravila nadzora (pad, zamrzavanje, zvuk stoji, sigurni režim posle više padova, namerno zaustavljanje) | prošao (GitHub, jedinični testovi) |
| Tačno jedan audio proces i jedan nadzor; 10 dvostrukih pokretanja (T-WD-03) | prošao (GitHub, pravi procesi) |
| Ubijen audio proces se vraća (T-WD-01) | prošao (GitHub): vraćen za 4,6 s |
| Zamrznut audio proces se otkriva i menja (T-WD-02) | prošao (GitHub): zamenjen za 38 s (granica 30 s bez odgovora + provera na 5 s) |
| Pad nadzora ne gasi muziku; novi nadzor preuzima postojeći audio proces (T-SES-04) | prošao (GitHub) |
| Namerno zaustavljanje i ponovno pokretanje (`--stop`, `--start`) (T-SES-05) | prošao (GitHub) |
| Zadatak u Task Scheduler-u: pravi se, podešavanja se proveravaju, briše se (T-SES-03) | prošao (GitHub) — samo pravljenje i provera podešavanja |
| Zadatak stvarno pokreće radio posle prijave korisnika / paljenja računara | **nije probano** (traži pravi računar i ponovno pokretanje) |
| Audio proces odbija rad u sesiji 0 | pravilo: prošao (jedinični test). Na GitHub-u se proces namerno pušta u probnom režimu jer runner nema običnu sesiju; odbijanje pravog servisa **nije probano** |
| Otkrivanje „zvuk stoji iako treba da svira“ | pravilo: prošao (jedinični test); sa pravim zvukom **nije probano** (runner nema zvučni uređaj) |
| Sigurni režim (`--safe`: bez skeniranja biblioteke) | napisano; prelazak u režim: prošao (jedinični test); stvarno pokretanje u tom režimu **nepokrenuto** |
| Rad 72 h, potrošnja memorije i procesora na mini PC-u (T-SOAK) | **nije rađeno** |
| Pravilo zaštitnog zida za audio proces | ide sa instalacijom (M12) |

Greška koju je našao integracioni test: kad se audio proces zamrzne, stao je i nadzor (slanje poruke je čekalo bez roka). Ispravljeno: rok važi i za slanje, a nadzor ima i tvrdi rok od 12 s za celu proveru. Dodat poseban test (`FrozenPeerTests`).

Ostalo iz ovog koraka:
- Skeniranje biblioteke sada ide u pozadini, da program odmah odgovara nadzoru.
- Stanje koje audio proces šalje sadrži brojač zvuka, ID procesa i oznaku sigurnog režima.
- Probni folder sa podacima: promenljiva `LUSTRE_DATA_ROOT` (koriste je testovi).

## M5: interfejs, prvi deo (14.11.2026.)

Dokaz za „prošao“: GitHub Actions, Windows Server 2025, 14.11.2026., pokretanje 43621207630, 412 testova.

| Stavka | Stanje |
|---|---|
| Tekstovi na srpskom i engleskom, isti broj mesta za podatke (T-L10N-01) | prošao (GitHub; 91 tekst) |
| Prikaz ekrana za zaposlene iz stanja audio procesa (svira, pauza, zatvoreno, nema veze, nema zvučnika, nema pesama) | prošao (GitHub, jedinični testovi) |
| Veza koja se sama obnavlja (1 s → 5 s), zadržava poslednji prikaz, admin sesija se briše posle prekida | prošao (GitHub, pravi kanal) |
| PIN: samo cifre, 4–8, novi PIN se unosi dvaput | prošao (GitHub) |
| Admin komande bez važeće sesije se odbijaju, uključujući nove (jezik, izjava o licenci) (T-IPC-05) | prošao (GitHub) |
| Folder sa muzikom mora da postoji | prošao (GitHub) |
| Svaki ekran nacrtan u 1024×600 i 1366×768; svaka kontrola ≥ 56×56 px i cela na ekranu (deo T-TCH-01) | prošao (GitHub, automatska provera; slike pregledane) |
| Dugme bez funkcije ne može da se napravi (T-UI-01) | obezbeđeno načinom pisanja koda; nema posebnog testa |
| Klik/dodir na pravom prozoru: Pauza, Sledeća, glasnoća, PIN, dodavanje foldera, izjava, jezik | **napisano, nepokrenuto rukom** |
| Monitor na dodir (T-TCH-01 prstom, T-TCH-02 gašenje monitora) | **nije probano** |
| Zatvaranje interfejsa ne prekida muziku | po konstrukciji (odvojeni procesi); **nije probano slušanjem** |
| Zaključavanje posle 3 min neaktivnosti; 5 pogrešnih PIN-ova → 5 min | pravila u audio procesu: prošao (ranije); ponašanje ekrana **nepokrenuto rukom** |

Šta ekran za zaposlene još NEMA (ne prikazuje se dok ne radi): omot albuma, sledeća pesma, „Mirnije/Življe“ (traži analizu, M6), režim prelaza i miks (M7/M8).
Šta admin još NEMA: raspored, miksevi, istorija, analiza, Voice Studio, backup, kod za oporavak PIN-a, dnevnik izmena, povlačenje izjave o licenci.

Izmene u audio procesu uz ovaj korak: stanje sada nosi vreme gašenja/otvaranja, foldere, broj pesama, jezike i izjavu o licenci; skeniranje posle komande ide u pozadini (komanda se odmah vraća); izjava o kolektivnoj licenci daje se iz admin dela (komanda `declare-collective-license`, samo uz PIN).

Odstupanje: ekrani su pisani u C# kodu, ne u XAML-u (bez razvojnog računara greške u XAML-u bi se videle tek pri pokretanju).

Paket za probu: artefakt `LustreRadio` (audio proces + nadzor + interfejs, `POKRENI.cmd`, `ZAUSTAVI.cmd`, `UPUTSTVO.txt`).

## Proba vlasnika 15.11.2026. (njegov računar, Windows 11, slušalice HyperX 48 kHz, miš)

Paket `LustreRadio` sa GitHub-a (M5). Izvor: dnevnici `engine-20261114.log`, `engine-20261115.log`, `watchdog-20261115.log` i izjava vlasnika.

| Stavka | Stanje | Dokaz |
|---|---|---|
| Postavljanje PIN-a, dodavanje foldera, izjava o licenci | prošao | dnevnik 14.11. 23:53: 5 pesama dodato, 5 potvrđeno po izjavi |
| Van radnog vremena muzika ne kreće | prošao | 14.11. pokrenuto u 23:50, nema emitovanja |
| Nadzor pali audio proces; muzika kreće sama u radno vreme | prošao | 15.11. 16:39:50 pokretanje, 16:39:54 „Počinje emitovanje“, termin „Nedelja – popodne“ |
| Namerno zaustavljanje (`ZAUSTAVI.cmd`) | prošao | dnevnik nadzora 16:44:52 |
| Pauza, Sledeća, glasnoća | prošao po izjavi vlasnika („sve radi kako treba“) | komande tada nisu beležene u dnevnik |
| Zatvaranje prozora ne prekida muziku | prošao po izjavi vlasnika | – |
| Monitor na dodir, mini PC, Bluetooth zvučnik | **nije probano** | – |

Posle ove probe audio proces beleži komande sa ekrana u dnevnik (bez PIN-a), da sledeće probe budu proverljive iz dnevnika.

## M6: analiza, korak 1 – glasnoća (15.11.2026.)

Dokaz: GitHub Actions, Windows Server 2025, 15.11.2026., pokretanje 43732101731, 433 testa.

| Stavka | Stanje |
|---|---|
| Merenje glasnoće po ITU-R BS.1770-4 (K-filter, blokovi 400 ms, pragovi −70 LUFS i −10 LU), bilo koji sample rate | prošao (GitHub) |
| Signali po opisu EBU Tech 3341, slučajevi 1–5 (napravljeni u testu; nisu zvanični EBU fajlovi) | prošao: svi unutar ±0,1 LU |
| Koeficijenti filtera na 48 kHz jednaki onima iz standarda | prošao (9 decimala) |
| Poznat signal kroz pravi dekoder (WAV, sinus −20 dBFS) | prošao: izmereno −19,99 LUFS na 44,1 i 48 kHz |
| Isti zvuk u raznim formatima (FLAC, OGG, AAC, M4A, MP3 VBR) | izmereno −5,95 do −6,02 LUFS; MP3 CBR −6,47 |
| Prag „±0,5 LU na muzici“ (spec. 6.3) | **nije mereno** – nema referentnih muzičkih fajlova sa poznatom vrednošću |
| Glasnoća se meri pri skeniranju (isti prolaz kao vrh); ranije skenirane pesme se mere pri sledećem skeniranju | prošao (GitHub) |
| Pojačanje po pesmi koristi izmerenu glasnoću (cilj −18 LUFS, najviše +6 dB, vrh do −1 dBFS) | pravilo: prošao ranije (M2); sa izmerenim vrednostima **nije preslušano** |
| Analyzer.exe meri glasnoću i vrh fajlova | prošao (GitHub, 10 fajlova) |

Odstupanje od plana: merenje glasnoće ide u prolazu skeniranja (tokom rada), a ne posle 23:30. Razlog: fajl se pri skeniranju ionako ceo dekodira, a merenje dodaje samo dva mala filtera po uzorku. Koliko to opterećuje mini PC **nije mereno**.

Poznato ograničenje: fajl koji je sav tišina ostaje bez glasnoće i ponovo se proverava pri svakom skeniranju.

## Faza A pre M7 (15.11.2026.) – ČEKA PREGLED VLASNIKA; nije spojena u `main`

Grana `faza-a`. Plan: `claude/LUSTRE_RADIO_Pre-M7_tehnicka_revizija.md`. Izveštaj: `claude/LUSTRE_RADIO_Faza_A_izvestaj.md`.

Gde je šta provereno: **G** = GitHub Actions (Windows Server 2025, 4 procesora, bez zvučnog uređaja); **R** = glavni računar vlasnika; **M** = mini PC u lokalu.

| Stavka | G | R | M |
|---|---|---|---|
| `Microsoft.Data.Sqlite` 10.0.15 → `SQLitePCLRaw.lib.e_sqlite3` 2.1.13 (lanac ispisan komandom) | prošao | – | – |
| SQLite u objavljenom fajlu `e_sqlite3.dll`: 3.53.3 (ranjive su < 3.50.2) | prošao | nije rađeno | nije rađeno |
| SQLite koji učita objavljeni Engine: 3.53.3 | prošao | nije rađeno | nije rađeno |
| Ranjivosti NU1901–NU1904 obaraju build (proba sa starim paketom: build pao) | prošao | – | – |
| Baza iz prethodne verzije (SQLite 3.46.1) otvara se nepromenjena i upisiva je | prošao (probna baza) | **nije rađeno** (prava baza vlasnika) | nije rađeno |
| Zamrznut Engine: otkrivanje ≤ 30,0 s, tri puta zaredom | prošao: 19,8–20,2 s | nije rađeno | nije rađeno |
| Zamrznut Engine: novi proces ≤ 35,0 s | prošao: 19,9–20,4 s | nije rađeno | nije rađeno |
| Isti test sa starim vrednostima (5 s / 30 s) pada | prošao (pao kako treba: 34,2 s) | – | – |
| Zamrzavanje od 10 s nije kvar | prošao | nije rađeno | nije rađeno |
| Opterećen računar 45 s (12 petlji na 4 procesora) nije kvar | prošao | nije rađeno | **nije rađeno** (ovde je najvažnije) |
| Buđenje računara / zastoj samog nadzora nije kvar | prošao **samo kao simulacija** (lažni sat) | **nije rađeno** | nije rađeno |
| Ubijen Engine se vraća ≤ 15 s (T-WD-01) | prošao: 0,9 s (novi proces; bez zvuka) | nije rađeno ovom verzijom | nije rađeno |
| 20 pokretanja × Engine, nadzor, interfejs: uvek 1+1+1 (T-WD-03) | prošao | nije rađeno | nije rađeno |
| Ubijen interfejs ne dira Engine (deo T-WD-04) | prošao | zatvaranje prozora: prošao 15.11. (starija verzija) | nije rađeno |
| Sigurni režim: Engine stvarno pokrenut sa `--safe` posle 3 prekida | prošao | nije rađeno | nije rađeno |
| `LUSTRE_TEST_ALLOW_SESSION0` i `LUSTRE_DATA_ROOT` uklonjene; postavljene promenljive nemaju dejstva | prošao | – | – |
| U objavljenom paketu nema teksta `LUSTRE_TEST_`, `LUSTRE_DATA_ROOT`, `ALLOW_SESSION0` | prošao (14 naših fajlova) | – | – |
| Nijedan naš program ne čita promenljive okruženja (arhitektonski test) | prošao (15 projekata) | – | – |

Dokaz: pokretanje 43738170393 (izmena `4520661`), 518 testova, svi prošli. Probe sa namernim kvarom, grana `proba-pravila`: 43738844042 (stari paket → `error NU1903`), 43738937377 (stare vrednosti nadzora → „T-WD-02 PAO: 34,2 s“).

Nalazi tokom rada:
- Red `WarningsNotAsErrors NU1901–NU1904` u `Directory.Build.props` dodat je još u M0 (13.11.). Zbog njega upozorenje o ranjivosti nije obaralo build kad je SQLite dodat u M3. Uklonjen.
- Prethodna verzija je stvarno nosila SQLite 3.46.1 (izmereno, pokretanje 43736748161).
- GitHub računar radi u sesiji 2, ne 0; prekidač za sesiju 0 nije bio potreban.
- Kad se isti program pokrene dok već radi, drugi primerak piše u poseban fajl dnevnika (`engine-…_001.log`). Nije greška, ali dnevnik jednog dana može biti u više fajlova.

Čeka odobrenje (nije u odluci O1): prvi odgovor Engine-a posle pokretanja čeka se do 60 s pre nego što se proglasi kvar (umesto 20 s), jer pokretanje na sporom računaru može da traje. U kodu je na grani `faza-a`; u specifikaciju nije upisano.
