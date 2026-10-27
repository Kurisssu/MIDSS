#!/usr/bin/env python3
"""
Seeds all three Orthanc instances with DICOM studies created from APTOS fundus PNGs.

Goal: each institution receives a DIFFERENT, NON-OVERLAPPING subset of images.
This avoids the SITIM cache constraint where the same Orthanc Study ID cannot
belong to multiple institutions simultaneously.

Workflow:
    1. Clear all studies from each of the 3 Orthanc instances.
    2. Select images from the APTOS training set (all of them by default;
       cap per class with FL_SEED_MAX_PER_CLASS > 0).
    3. Split them across the 3 institutions with NO overlap (non-IID Dirichlet).
    4. For each (institution, image) pair, build a brand-new DICOM with a
       fresh StudyInstanceUID so the resulting Orthanc Study ID is unique
       even if the source PNG were ever shared.
    5. Regenerate the FL labels CSV for each institution.

Usage:
    python seed_orthanc_fl.py

Requires: pydicom, Pillow, numpy, pandas (installed in scripts/venv).
"""
from __future__ import annotations

import base64
import datetime
import io
import json
import os
import random
import sys
import threading
import urllib.request
from collections import Counter
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

# Force UTF-8 stdout/stderr so we can print arrow glyphs etc. on Windows shells.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

import numpy as np
import pandas as pd
import pydicom
from pydicom.dataset import FileDataset, FileMetaDataset
from pydicom.uid import (
    ExplicitVRLittleEndian,
    SecondaryCaptureImageStorage,
    generate_uid,
)
from PIL import Image

# ── Paths ─────────────────────────────────────────────────────────────────────
SITIM_ROOT = Path(__file__).resolve().parent.parent
IMAGES_DIR = SITIM_ROOT / "data" / "raw" / "train_images"
TRAIN_CSV = SITIM_ROOT / "data" / "raw" / "train.csv"
LABELS_DIR = SITIM_ROOT / "FederatedLearning" / "labels"

# ── Orthanc instances ─────────────────────────────────────────────────────────
ORTHS = {
    "inst1": {"name": "MedClinic",            "url": "http://localhost:8042", "user": "",        "pass": ""},
    "inst2": {"name": "Microchirurgia ochiului","url": "http://localhost:8043", "user": "orthanc", "pass": "orthanc"},
    "inst3": {"name": "Ovisus",               "url": "http://localhost:8044", "user": "orthanc", "pass": "orthanc"},
}

# ── Sampling / partition plan ───────────────────────────────────────────────────
# Migrare la ResNet-18 federat: etichete pe 5 CLASE APTOS reale (0=No DR, 1=Mild,
# 2=Moderate, 3=Severe, 4=Proliferative) — NU mai binar.
#
# Partiția între cele 3 instituții este NON-IID controlată prin schema Dirichlet pe
# etichete (standard în literatura FL): pentru fiecare clasă, exemplele alese se împart
# între instituții după proporții extrase dintr-o Dirichlet(alpha).
#   - alpha mic  ⇒ puternic non-IID (instituțiile se specializează pe anumite clase)
#   - alpha mare ⇒ aproape IID
# Reproductibil pentru același (RANDOM_SEED, NONIID_ALPHA, set de imagini).
#
# MAX_PER_CLASS limitează volumul per clasă; 0 (default) = FĂRĂ limită, adică
# întregul set de antrenare APTOS este repartizat instituțiilor.

NUM_CLASSES = 5
INSTITUTION_KEYS = ["inst1", "inst2", "inst3"]

# Parametrizabile prin env (vezi RUNBOOK).
NONIID_ALPHA = float(os.getenv("FL_NONIID_ALPHA", "0.5"))
MAX_PER_CLASS = int(os.getenv("FL_SEED_MAX_PER_CLASS", "0"))  # 0 = toate imaginile
RANDOM_SEED = int(os.getenv("FL_SEED_RANDOM_SEED", "20260517"))  # determinist
UPLOAD_WORKERS = int(os.getenv("FL_SEED_UPLOAD_WORKERS", "8"))

# ── Helpers ───────────────────────────────────────────────────────────────────
def _auth_headers(cfg: dict, *, content_type: str | None = None) -> dict:
    h: dict[str, str] = {}
    if content_type:
        h["Content-Type"] = content_type
    if cfg["user"]:
        tok = base64.b64encode(f"{cfg['user']}:{cfg['pass']}".encode()).decode()
        h["Authorization"] = f"Basic {tok}"
    return h


def orthanc_get_json(cfg: dict, path: str) -> object:
    req = urllib.request.Request(cfg["url"] + path, headers=_auth_headers(cfg))
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.loads(r.read())


def orthanc_post_dicom(cfg: dict, dicom_bytes: bytes) -> dict:
    req = urllib.request.Request(
        cfg["url"] + "/instances",
        data=dicom_bytes,
        headers=_auth_headers(cfg, content_type="application/dicom"),
        method="POST",
    )
    # Timeout generos: la upload paralel Orthanc poate răspunde mai lent.
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read())


def orthanc_delete_all_studies(cfg: dict) -> int:
    studies = orthanc_get_json(cfg, "/studies")
    if not isinstance(studies, list):
        return 0
    count = 0
    for sid in studies:
        req = urllib.request.Request(
            cfg["url"] + f"/studies/{sid}",
            headers=_auth_headers(cfg),
            method="DELETE",
        )
        try:
            urllib.request.urlopen(req, timeout=15)
            count += 1
        except Exception as exc:
            print(f"    WARN failed to delete {sid}: {exc}")
    return count


# ── DICOM builder ─────────────────────────────────────────────────────────────
def create_dicom(
    image_id: str,
    institution_name: str,
    label: int,
) -> bytes:
    """Build a unique RGB fundus DICOM for a given (image, institution) pair.

    `label` este diagnosticul APTOS pe 5 clase (0..4).
    """
    img_path = IMAGES_DIR / f"{image_id}.png"
    with Image.open(img_path) as img:
        rgb = img.convert("RGB").resize((512, 512))
    arr = np.asarray(rgb, dtype=np.uint8)

    file_meta = FileMetaDataset()
    file_meta.MediaStorageSOPClassUID = SecondaryCaptureImageStorage
    sop_uid = generate_uid()
    file_meta.MediaStorageSOPInstanceUID = sop_uid
    file_meta.TransferSyntaxUID = ExplicitVRLittleEndian

    ds = FileDataset(None, {}, file_meta=file_meta, preamble=b"\0" * 128)
    ds.is_implicit_VR = False
    ds.is_little_endian = True

    # Distinct patient per (institution, image) to avoid Orthanc patient collisions
    patient_token = f"{institution_name[:3].upper()}-{image_id[:10]}"
    ds.PatientID = patient_token
    ds.PatientName = f"FL^{patient_token}"
    ds.PatientBirthDate = "19800101"
    ds.PatientSex = "U"

    # Fresh StudyInstanceUID guarantees a unique Orthanc Study ID per upload
    ds.StudyInstanceUID = generate_uid()
    ds.StudyDate = datetime.date.today().strftime("%Y%m%d")
    ds.StudyTime = "120000"
    ds.AccessionNumber = ""
    ds.ReferringPhysicianName = ""
    ds.StudyID = image_id[:16]
    ds.StudyDescription = (
        f"FL Fundus — {institution_name} — APTOS DR class {label}"
    )
    ds.InstitutionName = institution_name

    ds.SeriesInstanceUID = generate_uid()
    ds.SeriesNumber = "1"
    ds.Modality = "OP"
    ds.SeriesDescription = "Color Fundus Photography"

    ds.SOPClassUID = SecondaryCaptureImageStorage
    ds.SOPInstanceUID = sop_uid
    ds.InstanceNumber = "1"

    ds.SamplesPerPixel = 3
    ds.PhotometricInterpretation = "RGB"
    ds.PlanarConfiguration = 0
    ds.Rows = 512
    ds.Columns = 512
    ds.BitsAllocated = 8
    ds.BitsStored = 8
    ds.HighBit = 7
    ds.PixelRepresentation = 0
    ds.PixelData = arr.tobytes()
    # (7FE0,0010) is declared "OB or OW" in the DICOM dictionary and pydicom
    # resolves that ambiguity to OW. PS3.5 A.2 requires OB whenever BitsAllocated
    # is 8, and the VR is carried verbatim into the DICOMweb metadata. Conformance
    # only — Orthanc and cornerstone both decode these files correctly either way.
    ds["PixelData"].VR = "OB"

    buf = io.BytesIO()
    pydicom.dcmwrite(buf, ds, enforce_file_format=True)
    return buf.getvalue()


# ── Sampling — partiție non-IID Dirichlet pe 5 clase ────────────────────────────
def _dirichlet_counts(total: int, proportions: np.ndarray) -> list[int]:
    """Împarte `total` exemple pe instituții după `proportions` (sumă 1), păstrând suma.

    Rotunjire cu corecție de rest, ca să nu pierdem/inventăm exemple.
    """
    raw = proportions * total
    counts = np.floor(raw).astype(int)
    remainder = total - int(counts.sum())
    if remainder > 0:
        # Distribuie resturile către pozițiile cu cea mai mare parte fracționară.
        frac_order = np.argsort(-(raw - counts))
        for i in range(remainder):
            counts[frac_order[i % len(counts)]] += 1
    return counts.tolist()


def pick_image_sets(np_rng: np.random.Generator) -> dict[str, list[tuple[str, int]]]:
    """
    Returns: { institution_key: [(image_id, label), ...] } cu label pe 5 clase (0..4).
    Partiție non-IID Dirichlet(alpha) per clasă; image_ids globally unique.
    """
    df = pd.read_csv(TRAIN_CSV)
    have_files = {p.stem for p in IMAGES_DIR.glob("*.png")}
    df = df[df["id_code"].isin(have_files)].reset_index(drop=True)

    buckets: dict[str, list[tuple[str, int]]] = {k: [] for k in INSTITUTION_KEYS}

    limit_desc = "toate" if MAX_PER_CLASS <= 0 else str(MAX_PER_CLASS)
    print(f"Partiție non-IID Dirichlet(alpha={NONIID_ALPHA}), {limit_desc} imagini/clasă, seed={RANDOM_SEED}")

    for label in range(NUM_CLASSES):
        pool = df[df["diagnosis"] == label]["id_code"].tolist()
        np_rng.shuffle(pool)
        chosen = pool if MAX_PER_CLASS <= 0 else pool[:MAX_PER_CLASS]
        if not chosen:
            print(f"  WARN clasa {label}: 0 imagini disponibile în APTOS local — sărită.")
            continue

        proportions = np_rng.dirichlet([NONIID_ALPHA] * len(INSTITUTION_KEYS))
        counts = _dirichlet_counts(len(chosen), proportions)

        cursor = 0
        for inst_key, count in zip(INSTITUTION_KEYS, counts):
            for image_id in chosen[cursor : cursor + count]:
                buckets[inst_key].append((image_id, label))
            cursor += count

    # Sanity: zero overlap of image_ids across institutions
    all_ids: list[str] = []
    for bucket in buckets.values():
        all_ids.extend(item[0] for item in bucket)
    if len(all_ids) != len(set(all_ids)):
        dupes = sorted({x for x in all_ids if all_ids.count(x) > 1})
        raise RuntimeError(f"Plan contains duplicate image ids across institutions: {dupes}")

    return buckets


# ── Main ──────────────────────────────────────────────────────────────────────
def main() -> None:
    if not TRAIN_CSV.exists():
        print(f"ERROR: {TRAIN_CSV} not found.")
        sys.exit(1)
    if not IMAGES_DIR.exists():
        print(f"ERROR: {IMAGES_DIR} not found.")
        sys.exit(1)
    LABELS_DIR.mkdir(parents=True, exist_ok=True)

    np_rng = np.random.default_rng(RANDOM_SEED)
    plan = pick_image_sets(np_rng)

    # Histograma pe clase per instituție — dovada partiției non-IID (raportată în articol).
    print()
    print("=" * 78)
    print("Non-IID class histograms (per institution)")
    print("=" * 78)
    for inst_key in INSTITUTION_KEYS:
        hist = dict(sorted(Counter(lbl for _, lbl in plan[inst_key]).items()))
        print(f"  [{inst_key}] class histogram: {hist} (total={len(plan[inst_key])})")

    print("=" * 78)
    print("Step 1/3 — Clearing existing studies from all Orthanc instances")
    print("=" * 78)
    for inst_key, cfg in ORTHS.items():
        print(f"  [{inst_key}] {cfg['name']} @ {cfg['url']}")
        removed = orthanc_delete_all_studies(cfg)
        print(f"    Deleted {removed} studies.")

    print()
    print("=" * 78)
    print(f"Step 2/3 — Uploading fresh DICOM studies ({UPLOAD_WORKERS} workers, no overlap)")
    print("=" * 78)

    tasks: list[tuple[str, str, int]] = []
    for inst_key, bucket in plan.items():
        print(f"  [{inst_key}] {ORTHS[inst_key]['name']} → {len(bucket)} studies planned")
        tasks.extend((inst_key, image_id, label) for image_id, label in bucket)

    def _upload_one(inst_key: str, image_id: str, label: int) -> tuple[str, str, int]:
        cfg = ORTHS[inst_key]
        dicom_bytes = create_dicom(image_id, cfg["name"], label)
        resp = orthanc_post_dicom(cfg, dicom_bytes)
        return inst_key, resp["ParentStudy"], label

    results: dict[str, list[tuple[str, int]]] = {k: [] for k in ORTHS}
    total = len(tasks)
    done = 0
    progress_lock = threading.Lock()
    errors: list[str] = []

    with ThreadPoolExecutor(max_workers=UPLOAD_WORKERS) as pool_exec:
        futures = {
            pool_exec.submit(_upload_one, inst_key, image_id, label): (inst_key, image_id)
            for inst_key, image_id, label in tasks
        }
        for fut in as_completed(futures):
            inst_key, image_id = futures[fut]
            try:
                ok_inst, study_id, label = fut.result()
                results[ok_inst].append((study_id, label))
            except Exception as exc:
                errors.append(f"{inst_key}/{image_id}: {exc}")
            with progress_lock:
                done += 1
                if done % 100 == 0 or done == total:
                    print(f"    progress: {done}/{total} uploaded ({len(errors)} errors)")

    if errors:
        print(f"\n  ERR — {len(errors)} uploads failed; first 10:")
        for line in errors[:10]:
            print(f"    {line}")
        sys.exit(1)

    print()
    print("=" * 78)
    print("Step 3/3 — Writing per-institution label CSV files")
    print("=" * 78)
    for inst_key, entries in results.items():
        csv_path = LABELS_DIR / f"{inst_key}-labels.csv"
        with open(csv_path, "w", newline="", encoding="utf-8") as f:
            f.write("orthanc_study_id,label\n")
            for study_id, lbl in entries:
                f.write(f"{study_id},{lbl}\n")
        print(f"  Wrote {csv_path}  ({len(entries)} rows)")

    # Cross-institution overlap check
    print()
    print("=" * 78)
    print("Validation — checking that no Orthanc Study ID appears in more than one institution")
    print("=" * 78)
    seen: dict[str, str] = {}
    overlap = False
    for inst_key, entries in results.items():
        for sid, _ in entries:
            if sid in seen:
                print(f"  CONFLICT: {sid} present in both {seen[sid]} and {inst_key}")
                overlap = True
            else:
                seen[sid] = inst_key
    if not overlap:
        total = sum(len(e) for e in results.values())
        print(f"  OK — {total} unique Orthanc Study IDs across {len(results)} institutions.")

    print()
    print("Done. Recommended next steps:")
    print("  1. Restart the FL client containers so they pick up the new labels:")
    print("     docker compose -f FederatedLearning\\docker-compose.fl.yml restart fl-client-inst1 fl-client-inst2 fl-client-inst3")
    print("  2. In SITIM UI press 'Sincronizare' on the studies page (or just refresh).")


if __name__ == "__main__":
    main()
