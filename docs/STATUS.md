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
| 3. Glasnoća po pesmi (ograničeno pojačanje), T-AUD-07 | nije započeto | – |
| 4. WASAPI izlaz, nit za dekodiranje, ponovno povezivanje uređaja, T-AUD-08..11, T-BT | nije započeto | traži pravi računar sa zvukom |
| 5. Bench.exe + režim „slab računar“ | nije započeto | – |

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
