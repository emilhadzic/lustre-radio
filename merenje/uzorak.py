#!/usr/bin/env python3
"""LUSTRE MUSIC COLLECTOR - uzorak od 50 pesama, biran po zvuku.

Cita kandidati.json (izlaz merenje.py), bira raznovrstan skup od oko 300 kandidata
(najvise 1 po izdanju i izvodjacu), privremeno ih preuzima SAMO na GitHub runner radi
analize zvuka, meri tempo, glasnocu, ostrinu, gornju granicu frekvencija, klipovanje i
tisine unutar pesme, pa bira 50 pesama koje najbolje odgovaraju profilu Lustre.
Fajlovi se brisu odmah posle analize; nista se ne cuva kao biblioteka.
"""
import csv
import json
import os
import random
import re
import subprocess
import sys
import tempfile
import time
import urllib.request

import numpy as np
import librosa

sys.path.insert(0, os.path.dirname(__file__))
from merenje import UA, norm  # noqa: E402

OUT = os.environ.get("OUT", "izlaz")
POOL_PER_GENRE = int(os.environ.get("POOL_PER_GENRE", "50"))
random.seed(20261120)

# Profili zvuka po zanru: opseg tempa (BPM), najveca dozvoljena ostrina (spektralni centroid, Hz)
PROFILE = {
    "lounge":         {"bpm": (80, 122), "centroid": 2800, "kvota": 10},
    "deep house":     {"bpm": (115, 126), "centroid": 3000, "kvota": 10},
    "melodic house":  {"bpm": (115, 126), "centroid": 3200, "kvota": 2},
    "chillout":       {"bpm": (70, 115), "centroid": 2800, "kvota": 9},
    "downtempo":      {"bpm": (70, 112), "centroid": 2800, "kvota": 9},
    "jazz / nu jazz": {"bpm": (60, 130), "centroid": 3000, "kvota": 10},
}
EXPERIMENTAL = ["noise", "harsh", "drone", "glitch", "industrial", "dark ambient", "experimental", "metal",
                "punk", "breakcore", "gabber", "hardcore", "psytrance", "dubstep", "speedcore", "field recording"]


def mojibake(s):
    letters = [c for c in s if c.isalpha()]
    if not letters:
        return True
    bad = sum(1 for c in letters if "\u00c0" <= c <= "\u00ff")
    return bad / len(letters) > 0.3


def download(url, path):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=120) as r, open(path, "wb") as fh:
        while True:
            b = r.read(1 << 16)
            if not b:
                break
            fh.write(b)


def ffmpeg_stats(path):
    """Integrisana glasnoca (LUFS) i tisine duze od 2 s unutar pesme (ne na pocetku/kraju)."""
    p = subprocess.run(["ffmpeg", "-hide_banner", "-nostats", "-i", path, "-af",
                        "silencedetect=noise=-50dB:d=2,ebur128", "-f", "null", "-"],
                       capture_output=True, text=True, timeout=600)
    err = p.stderr
    m = re.findall(r"I:\s+(-?[\d.]+) LUFS", err)
    lufs = float(m[-1]) if m else None
    starts = [float(x) for x in re.findall(r"silence_start: (-?[\d.]+)", err)]
    return lufs, starts


def analyze(path, duration):
    lufs, sil = ffmpeg_stats(path)
    inner = [s for s in sil if 10 < s < duration - 15]
    # 150 s iz sredine pesme za tempo i boju zvuka
    off = max(0.0, duration / 2 - 75)
    y, sr = librosa.load(path, sr=22050, mono=True, offset=off, duration=150)
    if len(y) < sr * 30:
        return None
    tempo, beats = librosa.beat.beat_track(y=y, sr=sr)
    tempo = float(np.atleast_1d(tempo)[0])
    bt = librosa.frames_to_time(beats, sr=sr)
    ibi = np.diff(bt)
    regular = float(np.std(ibi) / np.mean(ibi)) if len(ibi) > 8 else 1.0
    centroid = float(np.mean(librosa.feature.spectral_centroid(y=y, sr=sr)))
    rms = librosa.feature.rms(y=y)[0]
    dyn = float(20 * np.log10((np.percentile(rms, 95) + 1e-9) / (np.percentile(rms, 20) + 1e-9)))
    # gornja granica frekvencija iz punog uzorka (losi transkodovi seku ispod 15 kHz)
    y2, sr2 = librosa.load(path, sr=None, mono=True, offset=off, duration=30)
    S = np.abs(librosa.stft(y2, n_fft=4096)).mean(axis=1)
    freqs = librosa.fft_frequencies(sr=sr2, n_fft=4096)
    thr = S.max() * 10 ** (-60 / 20)
    cutoff = float(freqs[np.nonzero(S > thr)[0][-1]]) if np.any(S > thr) else 0.0
    clip = float(np.mean(np.abs(y2) > 0.999))
    return {"bpm": round(tempo, 1), "ravnomernost": round(regular, 3), "centroid": round(centroid),
            "dinamika_db": round(dyn, 1), "lufs": lufs, "granica_hz": round(cutoff), "klip": clip,
            "tisine_unutra": len(inner)}


def fit(genre, a):
    """Ocena 0-100 i razlozi. Nize = manje odgovara Lustre."""
    prof = PROFILE[genre]
    score, why = 100.0, []
    lo, hi = prof["bpm"]
    bpm = a["bpm"]
    # dopusti polovinu/duplo za spore zanrove
    cands = [bpm, bpm / 2, bpm * 2]
    best = min(cands, key=lambda b: 0 if lo <= b <= hi else min(abs(b - lo), abs(b - hi)))
    dist = 0 if lo <= best <= hi else min(abs(best - lo), abs(best - hi))
    a["bpm_ocena"] = round(best, 1)
    if dist:
        score -= min(40, dist * 2)
        why.append(f"tempo {best:.0f} van {lo}-{hi}")
    if a["centroid"] > prof["centroid"]:
        score -= min(30, (a["centroid"] - prof["centroid"]) / 40)
        why.append("ostriji zvuk")
    if a["lufs"] is not None:
        if a["lufs"] < -22:
            score -= 15
            why.append("vrlo tih snimak")
        if a["lufs"] > -6:
            score -= 15
            why.append("preglasno/zgusnuto")
    if a["granica_hz"] and a["granica_hz"] < 15000:
        score -= 25
        why.append(f"zvuk sece na {a['granica_hz'] / 1000:.1f} kHz")
    if a["klip"] > 0.001:
        score -= 20
        why.append("klipovanje")
    if a["tisine_unutra"]:
        score -= 30
        why.append("tisina usred pesme")
    if genre in ("deep house", "melodic house") and a["ravnomernost"] > 0.08:
        score -= 20
        why.append("neravnomeran ritam")
    if a["dinamika_db"] > 25:
        score -= 10
        why.append("velike promene jacine")
    return max(0, round(score)), why


def main():
    with open(os.path.join(OUT, "kandidati.json")) as fh:
        cands = json.load(fh)
    print(f"kandidata: {len(cands)}", flush=True)
    excluded = {"izvodjac bez imena/pokvareno ime": 0, "eksperimentalne oznake": 0}
    clean = []
    for t in cands:
        a = norm(t["izvodjac"])
        if not a or a in ("unknown artist", "unknown", "various artists", "va") or mojibake(t["izvodjac"] + t["naslov"]):
            excluded["izvodjac bez imena/pokvareno ime"] += 1
            continue
        tags = (t["oznake"] + " " + t["naslov"]).lower()
        if any(k in tags for k in EXPERIMENTAL):
            excluded["eksperimentalne oznake"] += 1
            continue
        clean.append(t)
    print(f"posle ciscenja: {len(clean)} {excluded}", flush=True)

    # raznovrstan skup: po zanru, najvise 1 po izdanju i izvodjacu, nasumicno (ne samo popularno)
    pool, used_rel, used_art = [], set(), set()
    for g in PROFILE:
        cg = [t for t in clean if g in t["zanrovi"]]
        random.shuffle(cg)
        # pola iz popularnijih, pola nasumicno
        top = sorted(cg, key=lambda t: -t["popularnost"])[: POOL_PER_GENRE]
        mix = top[: POOL_PER_GENRE // 2] + cg
        k = 0
        for t in mix:
            if t["izdanje_id"] in used_rel or norm(t["izvodjac"]) in used_art:
                continue
            used_rel.add(t["izdanje_id"])
            used_art.add(norm(t["izvodjac"]))
            pool.append((g, t))
            k += 1
            if k >= POOL_PER_GENRE:
                break
    print(f"za analizu zvuka: {len(pool)}", flush=True)

    rows = []
    tmp = tempfile.mkdtemp()
    for i, (g, t) in enumerate(pool, 1):
        url = t["fajl"] or t.get("fajl_lossless")
        path = os.path.join(tmp, "a" + os.path.splitext(url.split("?")[0])[1][:5])
        try:
            download(url, path)
            a = analyze(path, t["trajanje"])
        except Exception as e:  # noqa: BLE001
            print(f"  greska {t['izvodjac']} - {t['naslov']}: {e}", flush=True)
            a = None
        finally:
            if os.path.exists(path):
                os.remove(path)
        if a:
            sc, why = fit(g, a)
            rows.append({"zanr": g, "ocena": sc, "razlozi": "; ".join(why) or "odgovara profilu", **a, **t})
        if i % 20 == 0:
            print(f"  analizirano {i}/{len(pool)}", flush=True)
        time.sleep(0.5)

    # izbor 50: po kvoti zanra, najbolja ocena, najvise 1 po izvodjacu; ocena najmanje 60
    pick = []
    for g, prof in PROFILE.items():
        rg = sorted([r for r in rows if r["zanr"] == g and r["ocena"] >= 60], key=lambda r: -r["ocena"])
        pick += rg[: prof["kvota"]]
    if len(pick) < 50:  # dopuni najboljima iz ostalih zanrova
        rest = sorted([r for r in rows if r not in pick and r["ocena"] >= 60], key=lambda r: -r["ocena"])
        pick += rest[: 50 - len(pick)]

    cols = ["zanr", "ocena", "razlozi", "izvodjac", "naslov", "trajanje", "bpm_ocena", "lufs", "centroid",
            "granica_hz", "licenca", "izvor", "slusaj", "fajl", "fajl_lossless", "lossless"]
    for name, data in (("uzorak50.csv", pick), ("analiza_svih.csv", rows)):
        with open(os.path.join(OUT, name), "w", newline="") as fh:
            w = csv.DictWriter(fh, fieldnames=cols, extrasaction="ignore")
            w.writeheader()
            w.writerows(data)

    hist = {g: [r["ocena"] for r in rows if r["zanr"] == g] for g in PROFILE}
    L = ["# Uzorak 50 - izbor po zvuku", "", f"kandidata posle merenja: {len(cands)}",
         f"iskljuceno pre analize: {excluded}", f"analizirano zvukom: {len(rows)} od {len(pool)}", "",
         "## Koliko analiziranih odgovara profilu (ocena >= 60)", ""]
    for g, v in hist.items():
        ok = sum(1 for x in v if x >= 60)
        L.append(f"- {g}: {ok} od {len(v)}")
    reasons = {}
    for r in rows:
        for x in r["razlozi"].split("; "):
            reasons[re.sub(r"\d.*", "", x).strip()] = reasons.get(re.sub(r"\d.*", "", x).strip(), 0) + 1
    L += ["", f"## Razlozi nizih ocena (od {len(rows)})", ""] + [f"- {k}: {v}" for k, v in sorted(reasons.items(), key=lambda x: -x[1])]
    L += ["", f"## Izabrano: {len(pick)}", ""]
    for r in pick:
        L.append(f"- [{r['zanr']}] {r['ocena']} | {r['izvodjac']} - {r['naslov']} | {int(r['trajanje'] // 60)}:{int(r['trajanje'] % 60):02d} | "
                 f"{r['bpm_ocena']} BPM | {r['lufs']} LUFS | {r['licenca']} | {r['izvor']} | {r['slusaj']} | {r['fajl']}")
    rep = "\n".join(L) + "\n"
    with open(os.path.join(OUT, "uzorak50.md"), "w") as fh:
        fh.write(rep)
    print(rep)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as fh:
            fh.write(rep)


if __name__ == "__main__":
    main()
