# Stanje razvoja

| Faza | Stanje | Dokaz |
|---|---|---|
| M0 Temelj | završeno 13.11.2026. | GitHub Actions (Windows Server 2025, .NET 10): build, 31 test (5 Core, 26 arhitektonskih), smoke pokretanje svih 5 programa. Nije pokrenuto na mini PC-ju. |
| M1 Audio jezgro | u toku | vidi ispod |
| M2–M12 | nije započeto | – |

Napomena: programi su u M0 samo kosturi bez funkcija.

## M1 po koracima

| Korak | Stanje | Dokaz |
|---|---|---|
| 1. Deck, Standard Crossfade, limiter, izlaz u memoriju/WAV, offline renderer | završeno 13.11.2026. | GitHub Actions: 13 testova prošlo (T-AUD-01, 02, 03, 05 na veštačkim signalima; rad bez zauzimanja memorije; nezavisnost od veličine bloka). Provera testova: namerno pokvaren kod → 4 testa pala. |
| 2. Dekoderi i matrica formata (T-AUD-04, T-AUD-06) | završeno 13.11.2026., osim Opus i libFLAC | GitHub Actions (Windows Server 2025): 60 audio testova prošlo. Nije pokrenuto na Windows 11 ni na mini PC-ju. |
| 3. Glasnoća po pesmi, ograničeno pojačanje (T-AUD-07) | završeno 13.11.2026. | GitHub Actions: testovi prošli. Merenje LUFS-a same pesme dolazi u fazi M6; do tada se koristi podrazumevano pojačanje. |
| 4a. Kružni bafer, nit za pripremu zvuka | završeno 13.11.2026. | GitHub Actions: put kroz nit daje isti zvuk kao offline renderer; test sa dve niti (2 miliona uzoraka) bez gubitka. |
| 4b. WASAPI izlaz (osnovni) | **prošao na računaru vlasnika, 13.11.2026. 20:06** | Bench, slušalice HyperX (48 kHz), Windows 11: 20,1 s, 0 prekida, ocena slušanjem „d“. Samo veštački tonovi; prava muzika i duži rad još nisu probani. |
| 4c. Praćenje promene uređaja, ponovno povezivanje, Bluetooth (T-AUD-09..11, T-BT) | nije započeto | – |
| 5a. Bench.exe + paket za preuzimanje | završeno 13.11.2026. (bez stvarnog zvuka) | GitHub Actions: Bench se pokreće, meri i piše izveštaj. |
| 5b. Režim „slab računar“ | nije započeto | – |
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
