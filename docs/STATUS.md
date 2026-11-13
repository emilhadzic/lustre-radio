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
| 2. Dekoderi (MP3, FLAC, WAV, M4A, AAC, OGG), matrica formata, T-AUD-04, 06 | nije započeto | – |
| 3. Glasnoća po pesmi (ograničeno pojačanje), T-AUD-07 | nije započeto | – |
| 4. WASAPI izlaz, nit za dekodiranje, ponovno povezivanje uređaja, T-AUD-08..11, T-BT | nije započeto | traži pravi računar sa zvukom |
| 5. Bench.exe + režim „slab računar“ | nije započeto | – |

Nijedan test još nije pokrenut na stvarnom audio uređaju ni na mini PC-ju.
