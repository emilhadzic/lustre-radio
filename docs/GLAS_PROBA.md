# LUSTRE RADIO – poređenje ElevenLabs i Azure (proba glasa)

Stanje 17.11.2026.: **slušna proba NEPROVERENA** (odložena odlukom vlasnika). Plaćeni plan se ne kupuje dok vlasnik ne presluša i ne izabere servis.

## Iste probne rečenice za oba servisa

| # | Jezik | Vrsta | Tekst |
|---|---|---|---|
| 1 | srpski | kratka (ident) | Slušate LUSTRE RADIO. Dobro došli u Caffe Lustre. |
| 2 | srpski | duža | Dobro veče i dobro došli u Caffe Lustre. Uživajte u kafi, dobroj muzici i dobrom društvu. Hvala što ste sa nama. |
| 3 | engleski | kratka (ident) | You're listening to LUSTRE RADIO. Welcome to Caffe Lustre. |
| 4 | engleski | duža | Good evening and welcome to Caffe Lustre. Enjoy your coffee, good music and good company. Thank you for being with us. |

Izgovor imena: probati „Lustre“, „Laster“ i „Lustr“ u rečenici 1 i izabrati po sluhu (rečnik izgovora, spec. 15).

Za svaku rečenicu: ženski i muški glas → 4 × 2 = 8 snimaka po servisu.

## Kako bez troškova

| | ElevenLabs | Azure AI Speech |
|---|---|---|
| Besplatno | plan Free: 10.000 kredita mesečno (Eleven v3 ≈ 1 kredit po znaku), dovoljno za probu | nivo F0: 0,5 miliona znakova mesečno za neuralne glasove |
| Srpski | model Eleven v3 (Multilingual v2 i Flash v2.5 ne podržavaju srpski) | gotovi srpski glasovi Sophie (ženski) i Nicholas (muški) |
| Isti glas na oba jezika | da (jedan dizajnirani glas) | ne: srpski i engleski glas su različiti |
| Sme li proba u eter | **ne**: Free je samo nekomercijalan i traži navođenje ElevenLabs-a; za eter najmanje plaćeni plan Starter | proveriti uslove pre odluke (Product Terms; obaveštenje da je glas sintetički) |
| Napomena | – | pri otvaranju Azure naloga traži se kartica radi provere identiteta; F0 se ne naplaćuje, ali nalog treba pažljivo podesiti da ne pređe na plaćeni nivo |

Snimci sa besplatnih planova služe **samo za slušnu probu**. U program se uvoze tek najave napravljene na izabranom plaćenom planu, uz popunjenu licencu (servis, model i glas, plan, dokaz plaćanja, datum, snimak uslova). Bez toga program najavu ne pušta (D10).

## Šta je u programu (FINAL, korak 3)

- Admin → Najave: ručni uvoz fajla (WAV, MP3, FLAC, OGG, M4A) sa tekstom, vrstom, jezikom, glasom i podacima licence; odobravanje (samo uz potpunu licencu); uključivanje, isključivanje, arhiva.
- Engine meri trajanje i glasnoću i pušta najavu 1,5 LU glasnije od muzike, samo između pesama (pesma se završi prirodno), po pravilima iz specifikacije: ident na 45–60 min, duža na 2–3 h, najviše 2 na sat, ne u prvih i poslednjih 10 min, nikad dve zaredom.
- Najava se beleži u dnevniku emitovanja; u istoriju pesama i rotaciju ne ulazi.
- **Generisanje iz programa preko API ključa nije urađeno**: ključ i servis zavise od vašeg izbora. Uvoz fajla generisanog na sajtu servisa je u specifikaciji predviđen kao ravnopravan način.
