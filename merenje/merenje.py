#!/usr/bin/env python3
"""LUSTRE MUSIC COLLECTOR - Faza 1: merenje kataloga.

Samo meri katalog i pravi listu uzorka za preslusavanje. Ne preuzima muziku.
Izvori: Internet Archive (zbirka netlabels) i ccMixter (javni Query API).
Licence: prihvata CC0, javno dobro, CC BY, CC BY-SA. NC, ND i bez licence se odbacuju.
Samo standardna Python biblioteka.
"""
import csv
import http.client
import json
import os
import re
import sys
import time
import unicodedata
import urllib.parse
import urllib.request
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor

http.client._MAXLINE = 10_000_000  # ccMixter salje vrlo dugacka zaglavlja
UA = "LustreRadio-Faza1-merenje/1.0 (katalog; github.com/emilhadzic/lustre-radio)"
OUT = os.environ.get("OUT", "izlaz")
MAX_IA_ITEMS = int(os.environ.get("MAX_IA_ITEMS", "6000"))
MIN_SEC, MAX_SEC = 150, 600          # osnovni filter: 2:30 - 10:00
MIN_KBPS = 192                       # samo za kompresovane formate (MP3, OGG); lossless (FLAC/WAV/AIFF) nema ovo pravilo
LOSSLESS = ("FLAC", "WAV", "AIFF")
# delovi pesama, a ne kompletne kompozicije
PARTIAL = ["acapella", "a cappella", "acappella", "a capella", "pells", "stem", "stems", "sample", "samples",
           "sample pack", "loop", "loops", "intro", "outro", "interlude", "skit", "jingle", "snippet", "preview",
           "sketch", "rough", "demo", "drum track", "vocals only", "vocal only", "beat only"]

# Zanrovi koncepta -> kljucne reci (male slova, trazi se u oznakama/nazivu)
GENRES = {
    "lounge": ["lounge", "bossa nova", "bossa"],
    "deep house": ["deep house", "deephouse", "deep-house"],
    "organic house": ["organic house", "afro house"],
    "melodic house": ["melodic house", "progressive house"],
    "chillout": ["chillout", "chill out", "chill-out", "chill", "balearic"],
    "downtempo": ["downtempo", "down tempo", "trip hop", "trip-hop", "triphop"],
    "jazz / nu jazz": ["jazz", "nu jazz", "nu-jazz", "jazzy", "acid jazz"],
    # sire grupe: prijavljuju se odvojeno, NE ulaze u glavni zbir
    "soft electronic (sire)": ["electronica", "ambient pop"],
    "instrumental (sire)": ["instrumental"],
    "house ostalo (sire)": ["house"],
}
CONCEPT = list(GENRES)
CORE = [g for g in GENRES if "(sire)" not in g]


def http_json(url, tries=4):
    for i in range(tries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.loads(r.read().decode("utf-8", "replace"))
        except Exception as e:  # noqa: BLE001
            if i == tries - 1:
                print(f"GRESKA {url[:120]}: {e}", file=sys.stderr)
                return None
            time.sleep(2 * (i + 1))


def is_partial(text):
    t = " " + re.sub(r"[_/,;|()\[\]\-]+", " ", str(text).lower()) + " "
    return any(re.search(r"(?<![a-z])" + re.escape(k) + r"(?![a-z])", t) for k in PARTIAL)


def license_class(url):
    u = (url or "").lower()
    if not u:
        return "bez licence"
    if "publicdomain/zero" in u or "/zero/" in u:
        return "CC0"
    if "publicdomain" in u:
        return "javno dobro"
    if "creativecommons.org" not in u:
        return "drugo"
    m = re.search(r"licenses/([a-z\-+]+)/", u)
    code = m.group(1) if m else ""
    if "nc" in code.split("-") or "sampling" in code:
        return "NC/drugo (odbacuje se)"
    if "nd" in code.split("-"):
        return "ND (odbacuje se)"
    if code == "by":
        return "CC BY"
    if code == "by-sa":
        return "CC BY-SA"
    return "drugo"


ALLOWED = {"CC0", "javno dobro", "CC BY", "CC BY-SA"}


def norm(s):
    s = unicodedata.normalize("NFKD", str(s or "")).encode("ascii", "ignore").decode().lower()
    s = re.sub(r"\(.*?\)|\[.*?\]", " ", s)
    s = re.sub(r"^\d+\s*[-_.]\s*", "", s.strip())
    return re.sub(r"[^a-z0-9]+", " ", s).strip()


def genres_of(text):
    t = " " + re.sub(r"[_/,;|]+", " ", text.lower()) + " "
    found = []
    for g, keys in GENRES.items():
        for k in keys:
            if re.search(r"(?<![a-z])" + re.escape(k) + r"(?![a-z])", t):
                found.append(g)
                break
    if "house ostalo (sire)" in found and any(g in found for g in ("deep house", "organic house", "melodic house")):
        found.remove("house ostalo (sire)")
    return found


def parse_len(v):
    if v is None:
        return None
    s = str(v).strip()
    try:
        if ":" in s:
            parts = [float(p) for p in s.split(":")]
            sec = 0.0
            for p in parts:
                sec = sec * 60 + p
            return sec
        return float(s)
    except ValueError:
        return None


# ---------------------------------------------------------------- Internet Archive
def ia_search():
    base = "https://archive.org/services/search/v1/scrape"
    q = "collection:netlabels AND mediatype:audio"
    fields = "identifier,title,creator,subject,licenseurl,date,downloads,collection"
    items, cursor = [], None
    while True:
        p = {"q": q, "fields": fields, "count": "5000"}
        if cursor:
            p["cursor"] = cursor
        d = http_json(base + "?" + urllib.parse.urlencode(p))
        if not d:
            break
        items.extend(d.get("items", []))
        cursor = d.get("cursor")
        print(f"IA pretraga: {len(items)} / {d.get('total')}", flush=True)
        if not cursor:
            break
    return items


def as_text(v):
    if isinstance(v, list):
        return " ".join(map(str, v))
    return str(v or "")


AUDIO_FORMATS = {"vbr mp3": "MP3", "mp3": "MP3", "320kbps mp3": "MP3", "256kbps mp3": "MP3",
                 "192kbps mp3": "MP3", "160kbps mp3": "MP3", "128kbps mp3": "MP3", "64kbps mp3": "MP3",
                 "flac": "FLAC", "24bit flac": "FLAC", "ogg vorbis": "OGG", "wave": "WAV", "aiff": "AIFF"}


def ia_tracks(item):
    ident = item["identifier"]
    md = http_json(f"https://archive.org/metadata/{urllib.parse.quote(ident)}")
    if not md or "files" not in md:
        return []
    meta = md.get("metadata", {})
    lic = meta.get("licenseurl") or item.get("licenseurl")
    by_base = defaultdict(list)
    for f in md["files"]:
        fmt = AUDIO_FORMATS.get(str(f.get("format", "")).lower())
        if not fmt:
            continue
        base = re.sub(r"\.[a-z0-9]+$", "", f["name"].split("/")[-1].lower())
        by_base[base].append((fmt, f))
    tracks = []
    for base, files in by_base.items():
        # jedan snimak = jedan unos, bez obzira na broj formata
        fmts = sorted({x[0] for x in files})
        best = None
        for fmt, f in files:
            if f.get("source") == "original":
                best = (fmt, f)
                break
        best = best or files[0]
        fmt, f = best
        length = parse_len(f.get("length") or next((parse_len(x[1].get("length")) for x in files if x[1].get("length")), None))
        size = float(f.get("size") or 0)
        kbps = round(size * 8 / length / 1000) if length and size and fmt in ("MP3", "OGG") else None
        lossless_f = next((x[1] for x in files if x[0] in LOSSLESS), None)
        mp3 = next((x[1] for x in files if x[0] == "MP3"), None)
        tracks.append({
            "izvor": "Internet Archive",
            "id": f"{ident}/{f['name']}",
            "izvodjac": as_text(f.get("creator") or f.get("artist") or meta.get("creator") or item.get("creator")),
            "naslov": as_text(f.get("title") or base),
            "izdanje": as_text(meta.get("title")),
            "oznake": as_text(item.get("subject")) + " " + as_text(f.get("genre")),
            "licenca_url": lic or "",
            "trajanje": length,
            "formati": "+".join(fmts),
            "kbps": kbps,
            "popularnost": float(item.get("downloads") or 0),
            "slusaj": f"https://archive.org/details/{ident}",
            "fajl": "https://archive.org/download/" + urllib.parse.quote(f"{ident}/{(mp3 or f)['name']}"),
            "fajl_lossless": ("https://archive.org/download/" + urllib.parse.quote(f"{ident}/{lossless_f['name']}")) if lossless_f else "",
        })
    return tracks


# ---------------------------------------------------------------- ccMixter
CC_TAGS = ["chill", "chillout", "downtempo", "lounge", "jazz", "nu_jazz", "house", "deep_house",
           "ambient", "instrumental", "trip_hop", "electronic", "electronica", "piano", "bossa_nova"]


CC_BAD_PAGES = []


def cc_search():
    seen, raw = set(), []
    sample_dumped = False
    for tag in CC_TAGS:
        offset, fails = 0, 0
        while True:
            url = ("https://ccmixter.org/api/query?" + urllib.parse.urlencode(
                {"f": "json", "tags": tag, "limit": 100, "offset": offset, "type": "any"}))
            d = http_json(url)
            if d is None:  # neispravan odgovor: preskoci tu stranu, ne ceo zanr
                fails += 1
                CC_BAD_PAGES.append(url)
                if fails >= 3:
                    break
                offset += 100
                continue
            fails = 0
            if not d:
                break
            if not sample_dumped:
                os.makedirs(OUT, exist_ok=True)
                with open(os.path.join(OUT, "ccmixter_primer.json"), "w") as fh:
                    json.dump(d[:2], fh, indent=1)
                sample_dumped = True
            for r in d:
                uid = r.get("upload_id")
                if uid in seen:
                    continue
                seen.add(uid)
                raw.append(r)
            print(f"ccMixter '{tag}': offset {offset}, ukupno jedinstvenih {len(raw)}", flush=True)
            if len(d) < 100:
                break
            offset += 100
            time.sleep(0.5)
    return raw


def cc_track(r):
    files = r.get("files") or []
    audio = [f for f in files if str((f.get("file_format_info") or {}).get("media-type", "audio")) == "audio"]
    f = audio[0] if audio else (files[0] if files else {})
    info = f.get("file_format_info") or {}
    length = parse_len(info.get("ps"))
    fmts = sorted({str((x.get("file_format_info") or {}).get("default-ext", x.get("file_extra", {}).get("type", "?"))).upper() for x in audio}) or ["?"]
    br = str(info.get("br", ""))
    m = re.search(r"(\d+)", br)
    kbps = int(m.group(1)) if m else None
    if kbps and kbps > 2000:
        kbps //= 1000
    return {
        "izvor": "ccMixter",
        "id": str(r.get("upload_id")),
        "izvodjac": r.get("user_real_name") or r.get("user_name") or "",
        "naslov": r.get("upload_name") or "",
        "izdanje": "",
        "oznake": str(r.get("upload_tags") or "") + " " + str(r.get("upload_extra", {}).get("usertags", "") if isinstance(r.get("upload_extra"), dict) else ""),
        "licenca_url": r.get("license_url") or "",
        "trajanje": length,
        "formati": "+".join(fmts),
        "kbps": kbps,
        "popularnost": float(r.get("upload_num_scores") or 0),
        "slusaj": r.get("file_page_url") or f"https://ccmixter.org/files/{r.get('user_name')}/{r.get('upload_id')}",
        "fajl": f.get("download_url") or "",
        "fajl_lossless": next((x.get("download_url") for x in audio if str((x.get("file_format_info") or {}).get("default-ext", "")).upper() in LOSSLESS), "") or "",
    }


# ---------------------------------------------------------------- glavni tok
def main():
    os.makedirs(OUT, exist_ok=True)
    stats = {}

    # --- Internet Archive
    ia_items = ia_search()
    stats["ia_rezultati_pretrage"] = len(ia_items)
    lic_items = Counter(license_class(i.get("licenseurl")) for i in ia_items)
    stats["ia_izdanja_po_licenci"] = dict(lic_items)
    ok_items = [i for i in ia_items if license_class(i.get("licenseurl")) in ALLOWED]
    stats["ia_izdanja_dozvoljena_licenca"] = len(ok_items)
    concept_items = [i for i in ok_items
                     if genres_of(as_text(i.get("subject")))]
    stats["ia_izdanja_dozvoljena_i_zanr"] = len(concept_items)
    concept_items.sort(key=lambda i: -float(i.get("downloads") or 0))
    if len(concept_items) > MAX_IA_ITEMS:
        stats["ia_napomena"] = f"pregledano prvih {MAX_IA_ITEMS} izdanja po popularnosti od {len(concept_items)}"
        concept_items = concept_items[:MAX_IA_ITEMS]
    print(f"IA: citam spisak fajlova za {len(concept_items)} izdanja", flush=True)
    ia_all = []
    with ThreadPoolExecutor(max_workers=6) as ex:
        for n, tr in enumerate(ex.map(ia_tracks, concept_items), 1):
            ia_all.extend(tr)
            if n % 250 == 0:
                print(f"IA metapodaci: {n}/{len(concept_items)} izdanja, {len(ia_all)} snimaka", flush=True)
    stats["ia_audio_snimaka_u_tim_izdanjima"] = len(ia_all)

    # --- ccMixter
    cc_raw = cc_search()
    stats["cc_rezultati_pretrage"] = len(cc_raw)
    cc_all = [cc_track(r) for r in cc_raw]
    stats["cc_po_licenci"] = dict(Counter(license_class(t["licenca_url"]) for t in cc_all))

    tracks = ia_all + cc_all
    # --- filteri
    rejected = Counter()
    passed = []
    for t in tracks:
        lc = license_class(t["licenca_url"])
        t["licenca"] = lc
        t["zanrovi"] = genres_of(t["oznake"])
        t["izdanje_id"] = t["id"].split("/")[0] if t["izvor"] == "Internet Archive" else "cc:" + t["id"]
        if lc not in ALLOWED:
            rejected["licenca"] += 1
            continue
        if not t["zanrovi"]:
            rejected["zanr van koncepta"] += 1
            continue
        if t["trajanje"] is None:
            rejected["nepoznato trajanje"] += 1
            continue
        if not (MIN_SEC <= t["trajanje"] <= MAX_SEC):
            rejected["trajanje van 2:30-10:00"] += 1
            continue
        if is_partial(t["naslov"] + " " + t["oznake"]):
            rejected["deo pesme (vokal, semplovi, loop, intro...)"] += 1
            continue
        t["lossless"] = any(x in t["formati"] for x in LOSSLESS)
        # lossless se ne meri po kbps; kompresovani (MP3, OGG) moraju imati najmanje 192 kbps (VBR tolerancija 8)
        if not t["lossless"] and t["kbps"] and t["kbps"] < MIN_KBPS - 8:
            rejected[f"kompresovano ispod {MIN_KBPS} kbps"] += 1
            continue
        passed.append(t)

    # --- duplikati (isti snimak se racuna jednom)
    groups = {}
    for t in passed:
        key = (norm(t["izvodjac"]), norm(t["naslov"]), int((t["trajanje"] or 0) // 4))
        if key in groups:
            if t["popularnost"] > groups[key]["popularnost"]:
                groups[key] = t
        else:
            groups[key] = t
    uniq = list(groups.values())
    stats["duplikata_uklonjeno"] = len(passed) - len(uniq)
    stats["odbijeno_po_razlogu"] = dict(rejected)

    core = [t for t in uniq if any(g in CORE for g in t["zanrovi"])]
    core_ids = {id(t) for t in core}
    wide = [t for t in uniq if id(t) not in core_ids]
    by_src = Counter(t["izvor"] for t in core)
    durs = [t["trajanje"] for t in core]
    fmt = Counter(f for t in core for f in t["formati"].split("+"))
    lic = Counter(t["licenca"] for t in core)
    artists = len({norm(t["izvodjac"]) for t in core})
    rel_count = Counter(t["izdanje_id"] for t in core)
    # procena bez dominacije kompilacija: najvise 5 pesama po izdanju
    capped = sum(min(n, 5) for n in rel_count.values())

    def gstat(g, pool):
        ts = [t for t in pool if g in t["zanrovi"]]
        return len(ts), len({t["izdanje_id"] for t in ts}), len({norm(t["izvodjac"]) for t in ts})

    # --- uzorak: glavni zanrovi; 4 iz Internet Archive + 2 iz ccMixter po zanru;
    # bez velikih zbirnih izdanja (vise od 40 pesama), 1 pesma po izvodjacu i izdanju
    big = {r for r, n in rel_count.items() if n > 40}
    sample, used_art, used_rel = [], set(), set()
    for g in CORE:
        for src, want in (("Internet Archive", 4), ("ccMixter", 2)):
            cand = sorted([t for t in core if g in t["zanrovi"] and t["izvor"] == src
                           and t["izdanje_id"] not in big and norm(t["izvodjac"])],
                          key=lambda t: -t["popularnost"])
            k = 0
            for t in cand:
                a = norm(t["izvodjac"])
                if a in used_art or t["izdanje_id"] in used_rel:
                    continue
                sample.append((g, t))
                used_art.add(a)
                used_rel.add(t["izdanje_id"])
                k += 1
                if k >= want:
                    break
    no_big = sum(min(n, 5) for r, n in rel_count.items() if r not in big)
    rel_info = {}
    for t in core:
        rel_info.setdefault(t["izdanje_id"], (t["izdanje"], t["izvodjac"], t["licenca"]))

    cols = ["izvor", "izvodjac", "naslov", "izdanje", "zanrovi", "licenca", "trajanje", "formati", "kbps",
            "popularnost", "slusaj", "fajl", "licenca_url"]
    with open(os.path.join(OUT, "upotrebljive_pesme.csv"), "w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(cols)
        for t in sorted(core, key=lambda t: -t["popularnost"]):
            w.writerow([("|".join(t[c]) if isinstance(t[c], list) else t[c]) for c in cols])
    with open(os.path.join(OUT, "uzorak.csv"), "w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["zanr"] + cols)
        for g, t in sample:
            w.writerow([g] + [("|".join(t[c]) if isinstance(t[c], list) else t[c]) for c in cols])

    def mmss(s):
        return f"{int(s // 60)}:{int(s % 60):02d}"

    L = ["# Faza 1 - merenje kataloga", "", f"Vreme: {time.strftime('%Y-%m-%d %H:%M UTC', time.gmtime())}", "",
         "## Pretraga (ovo NISU upotrebljive pesme)", "",
         f"- Internet Archive, netlabels, audio izdanja u pretrazi: {stats['ia_rezultati_pretrage']}",
         f"- IA izdanja po licenci: {stats['ia_izdanja_po_licenci']}",
         f"- od toga sa dozvoljenom licencom: {stats['ia_izdanja_dozvoljena_licenca']}",
         f"- od toga sa zanrom u oznakama: {stats['ia_izdanja_dozvoljena_i_zanr']}",
         f"- audio snimaka u tim izdanjima: {stats['ia_audio_snimaka_u_tim_izdanjima']}",
         f"- {stats.get('ia_napomena', 'pregledana sva takva izdanja')}",
         f"- ccMixter jedinstvenih unosa za oznake ({', '.join(CC_TAGS)}): {stats['cc_rezultati_pretrage']}",
         f"- ccMixter po licenci: {stats['cc_po_licenci']}",
         f"- ccMixter strana sa neispravnim odgovorom (preskocene, po 100 unosa): {len(CC_BAD_PAGES)}", "",
         "## Prosli osnovne filtere (licenca, trajanje 2:30-10:00, kvalitet), bez duplikata", "",
         f"- odbijeno: {stats['odbijeno_po_razlogu']}",
         f"- duplikata uklonjeno: {stats['duplikata_uklonjeno']}",
         f"- GLAVNI ZANROVI KONCEPTA: **{len(core)}** pesama, {len(rel_count)} izdanja, {artists} izvodjaca",
         f"- isto, ali najvise 5 pesama po izdanju (bez naduvavanja kompilacijama): **{capped}**",
         f"- isto, bez zbirnih izdanja sa vise od 40 pesama ({len(big)} izdanja): **{no_big}**",
         f"- samo sire grupe (soft electronic / instrumental / house), van glavnog zbira: {len(wide)}",
         f"- po izvoru (glavni): {dict(by_src)}",
         f"- po licenci (glavni): {dict(lic)}",
         f"- lossless dostupan (FLAC/WAV/AIFF): {sum(1 for t in core if t['lossless'])}; samo kompresovano: {sum(1 for t in core if not t['lossless'])}", "",
         "## Po zanru: pesme / izdanja / izvodjaci (pesma moze imati vise zanrova)", ""]
    for g in CONCEPT:
        n, r, ar = gstat(g, uniq)
        L.append(f"- {g}: {n} / {r} / {ar}")
    L += ["", "## Izdanja sa najvise pesama (glavni zanrovi)", ""]
    for rid, n in rel_count.most_common(15):
        L.append(f"- {rid}: {n} pesama | {rel_info[rid][0][:60]} | {rel_info[rid][1][:40]} | {rel_info[rid][2]}")
    if durs:
        durs.sort()
        L += ["", "## Trajanje i formati (glavni)", "",
              f"- prosek {mmss(sum(durs) / len(durs))}, medijana {mmss(durs[len(durs) // 2])}",
              f"- formati (snimak moze imati vise): {dict(fmt)}"]
    L += ["", "## Uzorak za preslusavanje (1 pesma po izvodjacu i izdanju)", ""]
    for g, t in sample:
        L.append(f"- [{g}] {t['izvodjac']} - {t['naslov']} ({mmss(t['trajanje'])}, {t['licenca']}, {t['izvor']}) {t['slusaj']} | {t['fajl']}")
    with open(os.path.join(OUT, "kandidati.json"), "w") as fh:
        json.dump([{k: v for k, v in t.items()} for t in core if t["izdanje_id"] not in big], fh)
    rep = "\n".join(L) + "\n"
    with open(os.path.join(OUT, "izvestaj.md"), "w") as fh:
        fh.write(rep)
    with open(os.path.join(OUT, "brojevi.json"), "w") as fh:
        json.dump({**stats, "glavni": len(core), "glavni_max5_po_izdanju": capped, "po_izvoru": by_src,
                   "formati": fmt, "po_licenci": lic, "izvodjaca": artists}, fh, indent=1, default=dict)
    print(rep)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as fh:
            fh.write(rep)


if __name__ == "__main__":
    main()
