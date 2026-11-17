#!/usr/bin/env python3
"""LUSTRE MUSIC COLLECTOR - Faza 1: merenje kataloga.

Samo meri katalog i pravi listu uzorka za preslusavanje. Ne preuzima muziku.
Izvori: Internet Archive (zbirka netlabels) i ccMixter (javni Query API).
Licence: prihvata CC0, javno dobro, CC BY, CC BY-SA. NC, ND i bez licence se odbacuju.
Samo standardna Python biblioteka.
"""
import csv
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

UA = "LustreRadio-Faza1-merenje/1.0 (katalog; github.com/emilhadzic/lustre-radio)"
OUT = os.environ.get("OUT", "izlaz")
MAX_IA_ITEMS = int(os.environ.get("MAX_IA_ITEMS", "6000"))
MIN_SEC, MAX_SEC = 150, 600          # osnovni filter: 2:30 - 10:00
MIN_KBPS = 192                       # osnovni filter za MP3; lossless uvek prolazi

# Zanrovi koncepta -> kljucne reci (male slova, trazi se u oznakama/nazivu)
GENRES = {
    "lounge": ["lounge", "bossa", "lounge music", "cafe"],
    "deep house": ["deep house", "deephouse", "deep-house"],
    "organic house": ["organic house", "organic", "afro house", "ethnic house"],
    "melodic house": ["melodic house", "melodic techno", "progressive house"],
    "chillout": ["chillout", "chill out", "chill-out", "chill", "chillwave", "balearic"],
    "downtempo": ["downtempo", "down tempo", "trip hop", "trip-hop", "triphop", "lo-fi", "lofi"],
    "jazz / nu jazz": ["jazz", "nu jazz", "nu-jazz", "jazzy", "acid jazz", "electro swing"],
    "soft electronic": ["electronica", "idm", "ambient pop", "minimal", "dub"],
    "instrumental": ["instrumental", "piano", "guitar"],
    "house (ostalo)": ["house"],
}
CONCEPT = list(GENRES)


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
    if "house (ostalo)" in found and any(g in found for g in ("deep house", "organic house", "melodic house")):
        found.remove("house (ostalo)")
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
        kbps = round(size * 8 / length / 1000) if length and size and fmt == "MP3" else None
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
        })
    return tracks


# ---------------------------------------------------------------- ccMixter
CC_TAGS = ["chill", "chillout", "downtempo", "lounge", "jazz", "nu_jazz", "house", "deep_house",
           "ambient", "instrumental", "trip_hop", "electronic", "electronica", "piano", "bossa_nova"]


def cc_search():
    seen, raw = set(), []
    sample_dumped = False
    for tag in CC_TAGS:
        offset = 0
        while True:
            url = ("https://ccmixter.org/api/query?" + urllib.parse.urlencode(
                {"f": "json", "tags": tag, "limit": 200, "offset": offset, "type": "any"}))
            d = http_json(url)
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
            if len(d) < 200:
                break
            offset += 200
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
                     if genres_of(as_text(i.get("subject")) + " " + as_text(i.get("title")))]
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
        t["zanrovi"] = genres_of(t["oznake"] + " " + t["naslov"] + " " + t["izdanje"])
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
        lossless = any(x in t["formati"] for x in ("FLAC", "WAV", "AIFF"))
        if not lossless and t["kbps"] and t["kbps"] < MIN_KBPS - 8:
            rejected[f"kvalitet ispod {MIN_KBPS} kbps"] += 1
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

    by_src = Counter(t["izvor"] for t in uniq)
    by_genre = Counter(g for t in uniq for g in t["zanrovi"])
    durs = [t["trajanje"] for t in uniq]
    fmt = Counter(f for t in uniq for f in t["formati"].split("+"))
    lic = Counter(t["licenca"] for t in uniq)
    artists = len({norm(t["izvodjac"]) for t in uniq})

    # --- uzorak: po zanru najpopularniji, najvise 1 pesma po izvodjacu
    sample, used_art, used_id = [], set(), set()
    for g in CONCEPT:
        cand = sorted([t for t in uniq if g in t["zanrovi"]], key=lambda t: -t["popularnost"])
        k = 0
        for t in cand:
            a = norm(t["izvodjac"])
            if a in used_art or t["id"] in used_id:
                continue
            sample.append((g, t))
            used_art.add(a)
            used_id.add(t["id"])
            k += 1
            if k >= 5:
                break

    cols = ["izvor", "izvodjac", "naslov", "izdanje", "zanrovi", "licenca", "trajanje", "formati", "kbps",
            "popularnost", "slusaj", "fajl", "licenca_url"]
    with open(os.path.join(OUT, "upotrebljive_pesme.csv"), "w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(cols)
        for t in sorted(uniq, key=lambda t: -t["popularnost"]):
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
         f"- od toga sa dozvoljenom licencom: {stats['ia_izdanja_dozvoljena_licenca']}",
         f"- od toga sa zanrom koncepta: {stats['ia_izdanja_dozvoljena_i_zanr']}",
         f"- audio snimaka u tim izdanjima: {stats['ia_audio_snimaka_u_tim_izdanjima']}",
         f"- {stats.get('ia_napomena', 'pregledana sva takva izdanja')}",
         f"- IA izdanja po licenci: {stats['ia_izdanja_po_licenci']}",
         f"- ccMixter jedinstvenih unosa za oznake {', '.join(CC_TAGS)}: {stats['cc_rezultati_pretrage']}",
         f"- ccMixter po licenci: {stats['cc_po_licenci']}", "",
         "## Upotrebljive pesme (prosle filtere, bez duplikata)", "",
         f"- UKUPNO: **{len(uniq)}** (izvodjaca: {artists})",
         f"- po izvoru: {dict(by_src)}",
         f"- odbijeno: {stats['odbijeno_po_razlogu']}",
         f"- duplikata uklonjeno: {stats['duplikata_uklonjeno']}",
         f"- po licenci: {dict(lic)}", "",
         "## Po zanru (pesma moze imati vise zanrova)", ""]
    for g in CONCEPT:
        L.append(f"- {g}: {by_genre.get(g, 0)}")
    if durs:
        durs.sort()
        L += ["", "## Trajanje i formati", "",
              f"- prosek {mmss(sum(durs) / len(durs))}, medijana {mmss(durs[len(durs) // 2])}",
              f"- formati: {dict(fmt)}"]
    L += ["", "## Uzorak za preslusavanje", ""]
    for g, t in sample:
        L.append(f"- [{g}] {t['izvodjac']} - {t['naslov']} ({mmss(t['trajanje'])}, {t['licenca']}, {t['izvor']}) {t['slusaj']}")
    rep = "\n".join(L) + "\n"
    with open(os.path.join(OUT, "izvestaj.md"), "w") as fh:
        fh.write(rep)
    with open(os.path.join(OUT, "brojevi.json"), "w") as fh:
        json.dump({**stats, "upotrebljivo": len(uniq), "po_izvoru": by_src, "po_zanru": by_genre,
                   "formati": fmt, "po_licenci": lic, "izvodjaca": artists}, fh, indent=1, default=dict)
    print(rep)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as fh:
            fh.write(rep)


if __name__ == "__main__":
    main()
