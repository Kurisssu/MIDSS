from __future__ import annotations

import base64
import csv
import hashlib
import io
import json
import os
import time
import urllib.error
import urllib.request
from collections import Counter
from dataclasses import dataclass, field
from typing import Dict, List, Tuple

import numpy as np
import torch
from PIL import Image
from torch.utils.data import Dataset
from torchvision import transforms

# NOTĂ (decizie SITIM): sursa de date este DOAR Orthanc. Fiecare instituție are propriul
# Orthanc populat cu subsetul ei non-IID (partiția se materializează la seeding prin
# scripts/seed_orthanc_fl.py). Clientul descarcă aici imaginile full-resolution ale
# studiilor proprii și le aduce la 512×512 normalizate ImageNet — identic cu inferența.


def _stable_seed(value: str) -> int:
    digest = hashlib.sha256(value.encode("utf-8")).hexdigest()
    return int(digest[:8], 16)


# Numărul de reîncercări pe erori tranzitorii (timeout/connection reset). La încărcarea
# întregului APTOS (mii de imagini /rendered) Orthanc poate răspunde ocazional lent;
# un singur timeout NU trebuie să dărâme toată încărcarea dataset-ului.
_HTTP_MAX_RETRIES = max(1, int(os.getenv("FL_ORTHANC_MAX_RETRIES", "5")))


def _http_request_with_retries(
    url: str, headers: Dict[str, str], timeout_seconds: int, *, binary: bool
):
    last_exc: Exception | None = None
    for attempt in range(1, _HTTP_MAX_RETRIES + 1):
        try:
            req = urllib.request.Request(url=url, headers=headers, method="GET")
            with urllib.request.urlopen(req, timeout=timeout_seconds) as response:
                data = response.read()
            return data if binary else json.loads(data.decode("utf-8"))
        except urllib.error.HTTPError:
            # 404 etc. — eroare definitivă, decizia aparține apelantului. Nu reîncerca.
            raise
        except (urllib.error.URLError, TimeoutError) as exc:
            last_exc = exc
            if attempt < _HTTP_MAX_RETRIES:
                time.sleep(min(10.0, 1.5 * attempt))  # backoff liniar plafonat
    assert last_exc is not None
    raise last_exc


def _http_get_json(url: str, headers: Dict[str, str], timeout_seconds: int) -> dict:
    return _http_request_with_retries(url, headers, timeout_seconds, binary=False)


def _http_get_bytes(url: str, headers: Dict[str, str], timeout_seconds: int) -> bytes:
    return _http_request_with_retries(url, headers, timeout_seconds, binary=True)


def _build_headers(username: str, password: str) -> Dict[str, str]:
    headers: Dict[str, str] = {}
    if username and password:
        token = base64.b64encode(f"{username}:{password}".encode("utf-8")).decode("ascii")
        headers["Authorization"] = f"Basic {token}"
    return headers


@dataclass
class LabeledStudy:
    orthanc_study_id: str
    label: int


def _read_labels_csv(labels_csv_path: str) -> List[LabeledStudy]:
    if not os.path.exists(labels_csv_path):
        raise RuntimeError(f"Labels CSV not found: {labels_csv_path}")

    studies: List[LabeledStudy] = []
    with open(labels_csv_path, "r", encoding="utf-8") as fp:
        reader = csv.DictReader(fp)
        if reader.fieldnames is None:
            raise RuntimeError("Labels CSV has no header.")

        normalized_headers = {h.lower().strip(): h for h in reader.fieldnames}
        if "orthanc_study_id" not in normalized_headers:
            raise RuntimeError(
                "Labels CSV must contain 'orthanc_study_id' column. "
                "Example: orthanc_study_id,label"
            )
        if "label" not in normalized_headers:
            raise RuntimeError("Labels CSV must contain 'label' column.")

        study_id_header = normalized_headers["orthanc_study_id"]
        label_header = normalized_headers["label"]

        for row in reader:
            orthanc_study_id = (row.get(study_id_header) or "").strip()
            label_raw = (row.get(label_header) or "").strip()
            if not orthanc_study_id or not label_raw:
                continue

            try:
                label_value = int(float(label_raw))
            except ValueError as ex:
                raise RuntimeError(
                    f"Invalid label '{label_raw}' for study '{orthanc_study_id}'. Must be numeric."
                ) from ex
            if label_value < 0:
                raise RuntimeError(
                    f"Invalid label '{label_raw}' for study '{orthanc_study_id}'. Must be >= 0."
                )

            studies.append(LabeledStudy(orthanc_study_id=orthanc_study_id, label=label_value))

    if not studies:
        raise RuntimeError("No valid labeled rows found in labels CSV.")

    return studies


def _pick_instance_id(study_details: dict, series_details: List[dict]) -> str:
    instances: List[str] = []
    for series in series_details:
        for instance_id in series.get("Instances", []):
            if isinstance(instance_id, str) and instance_id:
                instances.append(instance_id)

    if not instances:
        for instance_id in study_details.get("Instances", []):
            if isinstance(instance_id, str) and instance_id:
                instances.append(instance_id)

    if not instances:
        raise RuntimeError("No instances found in labeled study.")

    mid_index = len(instances) // 2
    return instances[mid_index]


def _parse_preprocessing_vector(raw_value: str, variable_name: str) -> np.ndarray:
    try:
        values = json.loads(raw_value)
    except json.JSONDecodeError as ex:
        raise RuntimeError(f"{variable_name} must be valid JSON array.") from ex

    if not isinstance(values, list) or len(values) != 3:
        raise RuntimeError(f"{variable_name} must contain exactly 3 values.")

    try:
        vector = np.asarray([float(v) for v in values], dtype=np.float32)
    except (TypeError, ValueError) as ex:
        raise RuntimeError(f"{variable_name} must contain numeric values.") from ex

    if np.any(np.isclose(vector, 0.0)):
        raise RuntimeError(f"{variable_name} values must be non-zero.")

    return vector


# ── Dataset PyTorch (lazy) ──────────────────────────────────────────────────────
class FundusDataset(Dataset):
    """Dataset lazy peste perechi (PNG bytes, label).

    Decodarea PIL + transformările (resize 512, augmentări opționale, normalizare
    ImageNet) se aplică în `__getitem__`, ca să nu ținem toate imaginile decomprimate
    în memorie simultan (512×512×3 ≈ 3 MB/imagine).
    """

    def __init__(
        self,
        records: List[Tuple[bytes, int]],
        image_size: int,
        mean: np.ndarray,
        std: np.ndarray,
        train: bool,
        augment: bool,
    ) -> None:
        self.records = records
        self.transform = _build_transform(
            image_size=image_size,
            mean=mean,
            std=std,
            augment=train and augment,
        )

    def __len__(self) -> int:
        return len(self.records)

    def __getitem__(self, index: int) -> Tuple[torch.Tensor, int]:
        png_bytes, label = self.records[index]
        with Image.open(io.BytesIO(png_bytes)) as image:
            rgb = image.convert("RGB")
            tensor = self.transform(rgb)
        return tensor, int(label)


def _build_transform(image_size: int, mean: np.ndarray, std: np.ndarray, augment: bool):
    steps = [transforms.Resize((image_size, image_size))]
    if augment:
        # Augmentări ușoare, sigure pentru fundus DR (NU pe val/test).
        steps.extend(
            [
                transforms.RandomHorizontalFlip(p=0.5),
                transforms.RandomRotation(degrees=15),
                transforms.ColorJitter(brightness=0.1, contrast=0.1),
            ]
        )
    steps.extend(
        [
            transforms.ToTensor(),  # [0,1], CHW — identic cu /255 din pipeline-ul de inferință
            transforms.Normalize(mean=mean.tolist(), std=std.tolist()),
        ]
    )
    return transforms.Compose(steps)


@dataclass
class LoadedInstitutionData:
    train_records: List[Tuple[bytes, int]]
    test_records: List[Tuple[bytes, int]]
    image_size: int
    mean: np.ndarray
    std: np.ndarray
    augment: bool
    num_classes: int
    # Distribuția claselor pe întreg setul instituției (train+test), clasă->număr.
    # Sursă pentru metrica „distribuția claselor pe instituții" (raportată la server).
    class_histogram: Dict[int, int] = field(default_factory=dict)


def _stratified_split(
    records: List[Tuple[bytes, int]],
    seed: int,
    test_ratio: float,
) -> Tuple[List[Tuple[bytes, int]], List[Tuple[bytes, int]]]:
    rng = np.random.default_rng(seed)
    by_label: Dict[int, List[int]] = {}
    for idx, (_, label) in enumerate(records):
        by_label.setdefault(int(label), []).append(idx)

    train_idx: List[int] = []
    test_idx: List[int] = []
    for label, indices in by_label.items():
        order = np.asarray(indices)
        rng.shuffle(order)
        per_class_test = int(round(len(order) * test_ratio))
        per_class_test = min(per_class_test, len(order) - 1)
        if per_class_test <= 0:
            # Clasă cu un singur exemplu — merge integral în train.
            train_idx.extend(order.tolist())
            continue
        test_idx.extend(order[:per_class_test].tolist())
        train_idx.extend(order[per_class_test:].tolist())

    rng.shuffle(train_idx)
    rng.shuffle(test_idx)
    return [records[i] for i in train_idx], [records[i] for i in test_idx]


def _log_class_histogram(institution_key: str, labels: List[int]) -> None:
    histogram = dict(sorted(Counter(int(v) for v in labels).items()))
    # Acest log este dovada partiției non-IID, raportat în articol.
    print(f"[FL-DATA:{institution_key}] class histogram: {histogram} (total={len(labels)})")


def load_institution_dataset(institution_key: str) -> LoadedInstitutionData:
    orthanc_base_url = os.getenv("FL_ORTHANC_BASE_URL", "").strip().rstrip("/")
    labels_csv_path = os.getenv("FL_LABELS_CSV_PATH", "").strip()
    orthanc_user = os.getenv("FL_ORTHANC_USERNAME", "").strip()
    orthanc_password = os.getenv("FL_ORTHANC_PASSWORD", "").strip()
    timeout_seconds = int(os.getenv("FL_ORTHANC_TIMEOUT_SECONDS", "30"))
    max_samples = int(os.getenv("FL_MAX_TRAINING_SAMPLES", "512"))
    min_samples_per_class = max(1, int(os.getenv("FL_MIN_SAMPLES_PER_CLASS", "2")))
    min_labeled = max(1, int(os.getenv("FL_MIN_LABELED_SAMPLES", "6")))
    test_ratio = float(os.getenv("FL_TEST_RATIO", "0.2"))
    num_classes = int(os.getenv("FL_NUM_CLASSES", "5"))
    image_size = int(os.getenv("FL_PREPROCESSING_IMAGE_SIZE", "512"))
    augment = os.getenv("FL_AUGMENT", "true").strip().lower() == "true"
    mean = _parse_preprocessing_vector(
        os.getenv("FL_PREPROCESSING_MEAN", "[0.485, 0.456, 0.406]"),
        "FL_PREPROCESSING_MEAN",
    )
    std = _parse_preprocessing_vector(
        os.getenv("FL_PREPROCESSING_STD", "[0.229, 0.224, 0.225]"),
        "FL_PREPROCESSING_STD",
    )

    if not orthanc_base_url:
        raise RuntimeError("FL_ORTHANC_BASE_URL is required.")
    if not labels_csv_path:
        raise RuntimeError("FL_LABELS_CSV_PATH is required.")
    if image_size <= 0:
        raise RuntimeError("FL_PREPROCESSING_IMAGE_SIZE must be > 0.")
    if num_classes < 2:
        raise RuntimeError("FL_NUM_CLASSES must be at least 2.")

    headers = _build_headers(orthanc_user, orthanc_password)
    labeled_studies = _read_labels_csv(labels_csv_path)[: max(1, max_samples)]

    records: List[Tuple[bytes, int]] = []
    for labeled in labeled_studies:
        study_url = f"{orthanc_base_url}/studies/{labeled.orthanc_study_id}"
        try:
            study_details = _http_get_json(study_url, headers=headers, timeout_seconds=timeout_seconds)
        except urllib.error.HTTPError as ex:
            if ex.code == 404:
                print(f"[FL-DATA:{institution_key}] Study not found in Orthanc: {labeled.orthanc_study_id}")
                continue
            raise
        except urllib.error.URLError as ex:
            print(
                f"[FL-DATA:{institution_key}] Skipping study after repeated timeouts: "
                f"{labeled.orthanc_study_id} ({ex})"
            )
            continue

        series_ids = [s for s in study_details.get("Series", []) if isinstance(s, str)]
        series_details: List[dict] = []
        for series_id in series_ids[:8]:
            series_url = f"{orthanc_base_url}/series/{series_id}"
            try:
                series_details.append(_http_get_json(series_url, headers=headers, timeout_seconds=timeout_seconds))
            except urllib.error.URLError:
                continue

        try:
            instance_id = _pick_instance_id(study_details, series_details)
        except RuntimeError:
            print(f"[FL-DATA:{institution_key}] No valid instances for study: {labeled.orthanc_study_id}")
            continue

        # Full-resolution rendered image (NU /preview, care e redus). Resize-ul la
        # `image_size` se face oricum în transformare.
        rendered_url = f"{orthanc_base_url}/instances/{instance_id}/rendered"
        try:
            image_bytes = _http_get_bytes(rendered_url, headers=headers, timeout_seconds=timeout_seconds)
        except urllib.error.URLError:
            print(f"[FL-DATA:{institution_key}] Failed rendered download for instance: {instance_id}")
            continue

        if labeled.label >= num_classes:
            raise RuntimeError(
                f"Label {labeled.label} exceeds FL_NUM_CLASSES={num_classes} for study {labeled.orthanc_study_id}."
            )

        records.append((image_bytes, labeled.label))

    if len(records) < min_labeled:
        raise RuntimeError(
            f"Insufficient labeled samples loaded from Orthanc ({len(records)} < {min_labeled}). "
            "Check FL_LABELS_CSV_PATH and study IDs availability in Orthanc. "
            "Override threshold with FL_MIN_LABELED_SAMPLES env var."
        )

    all_labels = [label for _, label in records]
    _log_class_histogram(institution_key, all_labels)
    class_histogram = {int(k): int(v) for k, v in sorted(Counter(all_labels).items())}

    unique_labels, label_counts = np.unique(np.asarray(all_labels), return_counts=True)
    if unique_labels.size < 2:
        raise RuntimeError("Need at least two distinct label classes in loaded institution data.")
    sparse_classes = unique_labels[label_counts < min_samples_per_class]
    if sparse_classes.size > 0:
        raise RuntimeError(
            "Not enough samples per class for FL training. "
            f"Classes with < {min_samples_per_class} samples: "
            + ", ".join(str(int(v)) for v in sparse_classes)
            + ". Sub non-IID agresiv, scade FL_MIN_SAMPLES_PER_CLASS sau crește FL_NONIID_ALPHA la seeding."
        )

    train_records, test_records = _stratified_split(
        records,
        seed=_stable_seed(institution_key),
        test_ratio=test_ratio,
    )

    return LoadedInstitutionData(
        train_records=train_records,
        test_records=test_records,
        image_size=image_size,
        mean=mean,
        std=std,
        augment=augment,
        num_classes=num_classes,
        class_histogram=class_histogram,
    )
