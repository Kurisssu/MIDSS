from __future__ import annotations

import copy
import hashlib
import json
import os
import threading
import time
import uuid
import urllib.error
import urllib.request
from typing import List, Tuple

import flwr as fl
import numpy as np
import torch
import torch.nn as nn
from torch.utils.data import DataLoader

from dataset import FundusDataset, LoadedInstitutionData, load_institution_dataset
from model import (
    build_model,
    resolve_architecture,
    compute_class_weights,
    evaluate,
    get_weights,
    parameters_num_bytes,
    set_weights,
    train_one_epoch,
)


def _stable_seed(value: str) -> int:
    digest = hashlib.sha256(value.encode("utf-8")).hexdigest()
    return int(digest[:8], 16)


class ClientStatusState:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._status = "Online"

    def set_status(self, status: str) -> None:
        with self._lock:
            self._status = status

    def get_status(self) -> str:
        with self._lock:
            return self._status


def _post_json(url: str, payload: dict, timeout_seconds: int = 5) -> dict:
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url=url,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=timeout_seconds) as response:
        return json.loads(response.read().decode("utf-8"))


def _heartbeat_loop(
    control_api_base_url: str,
    institution_id: str,
    client_id: str,
    status_state: ClientStatusState,
    stop_event: threading.Event,
    interval_seconds: int,
) -> None:
    endpoint = f"{control_api_base_url}/clients/register"
    last_error = ""
    while not stop_event.is_set():
        payload = {
            "institution_id": institution_id,
            "client_id": client_id,
            "status": status_state.get_status(),
        }
        try:
            _post_json(endpoint, payload, timeout_seconds=5)
            if last_error:
                print(f"[FL-CLIENT:{client_id}] Registration heartbeat recovered.")
            last_error = ""
        except Exception as ex:
            current_error = str(ex)
            if current_error != last_error:
                print(f"[FL-CLIENT:{client_id}] Registration heartbeat failed: {current_error}")
            last_error = current_error

        stop_event.wait(max(1, interval_seconds))


def _split_train_validation(
    records: List[Tuple[bytes, int]],
    seed: int,
    val_ratio: float,
) -> Tuple[List[Tuple[bytes, int]], List[Tuple[bytes, int]]]:
    """Split intern stratificat train/val (păstrat din contractul original — valoros
    pentru early stopping). Sub date foarte puține, val = train (fallback sigur).
    """
    if len(records) < 8:
        return records, records

    rng = np.random.default_rng(seed)
    by_label: dict[int, List[int]] = {}
    for idx, (_, label) in enumerate(records):
        by_label.setdefault(int(label), []).append(idx)

    train_idx: List[int] = []
    val_idx: List[int] = []
    for indices in by_label.values():
        order = np.asarray(indices)
        rng.shuffle(order)
        val_count = int(round(len(order) * val_ratio))
        val_count = max(1, min(val_count, len(order) - 1))
        val_idx.extend(order[:val_count].tolist())
        train_idx.extend(order[val_count:].tolist())

    if len(train_idx) < 4 or len(val_idx) < 2:
        return records, records

    rng.shuffle(train_idx)
    rng.shuffle(val_idx)
    return [records[i] for i in train_idx], [records[i] for i in val_idx]


class ResNetFederatedClient(fl.client.NumPyClient):
    """Client Flower care antrenează federat un ResNet complet (fine-tuning ImageNet).

    Arhitectura concretă vine din FL_ARCHITECTURE, prin fl_common.model_def.build_model.

    Înlocuiește vechiul MulticlassLinearClient (MLP NumPy). Schimbă DOAR modelul și
    features-urile; contractul de orchestrare (heartbeat, status, reconnect) e neatins.
    """

    def __init__(
        self,
        client_id: str,
        institution_id: str,
        data: LoadedInstitutionData,
        learning_rate: float,
        momentum: float,
        weight_decay: float,
        default_local_epochs: int,
        default_batch_size: int,
        grad_clip_norm: float,
        validation_ratio: float,
        early_stopping_patience: int,
        dataloader_workers: int,
        status_state: ClientStatusState,
    ) -> None:
        self.client_id = client_id
        self.institution_id = institution_id
        # Histograma claselor serializată o singură dată; raportată în metricile fit
        # pentru metrica „distribuția claselor pe instituții".
        self.class_histogram_json = json.dumps(
            {str(k): int(v) for k, v in (data.class_histogram or {}).items()}
        )
        self.data = data
        self.learning_rate = learning_rate
        self.momentum = momentum
        self.weight_decay = max(0.0, weight_decay)
        self.default_local_epochs = max(1, default_local_epochs)
        self.default_batch_size = max(1, default_batch_size)
        self.grad_clip_norm = max(0.0, grad_clip_norm)
        self.validation_ratio = min(0.4, max(0.05, validation_ratio))
        self.early_stopping_patience = max(1, early_stopping_patience)
        self.dataloader_workers = max(0, dataloader_workers)
        self.status_state = status_state

        self.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        torch.manual_seed(_stable_seed(client_id))

        # Arhitectura de pornire vine din mediu, dar sesiunea o poate schimba: serverul o
        # trimite în fit_config, iar `_ensure_architecture` reconstruiește modelul la nevoie.
        self.architecture = resolve_architecture()
        self.model = build_model(
            num_classes=data.num_classes,
            pretrained=True,
            architecture=self.architecture,
        ).to(self.device)

        # Split intern train/val pentru early stopping.
        self.fit_records, self.val_records = _split_train_validation(
            data.train_records,
            _stable_seed(f"{client_id}-split"),
            self.validation_ratio,
        )

        fit_labels = [label for _, label in self.fit_records]
        class_weights = compute_class_weights(np.asarray(fit_labels), data.num_classes)
        self.class_weights = torch.tensor(class_weights, dtype=torch.float32, device=self.device)

    # ── Dataset / loader helpers ────────────────────────────────────────────────
    def _make_loader(self, records, train: bool, batch_size: int) -> DataLoader:
        dataset = FundusDataset(
            records=records,
            image_size=self.data.image_size,
            mean=self.data.mean,
            std=self.data.std,
            train=train,
            augment=self.data.augment,
        )
        return DataLoader(
            dataset,
            batch_size=max(1, batch_size),
            shuffle=train,
            num_workers=self.dataloader_workers,
            drop_last=False,
        )

    # ── Flower API ──────────────────────────────────────────────────────────────
    def get_parameters(self, config):  # type: ignore[override]
        return get_weights(self.model)

    def _ensure_architecture(self, config) -> None:
        """Aliniază modelul local la arhitectura cerută de sesiune.

        Clientul se conectează înainte ca sesiunea să existe, deci pornește pe arhitectura
        din mediu. Serverul anunță alegerea reală prin `fit_config` / `evaluate_config`; la
        prima nepotrivire modelul se reconstruiește, altfel `set_weights` ar respinge
        parametrii primiți (număr diferit de tensori în state_dict).

        Ponderile ImageNet se descarcă o singură dată și rămân în cache-ul torch, deci
        comutarea între sesiuni costă doar reconstrucția grafului.
        """
        requested = str(config.get("architecture", "") or "").strip().lower()
        if not requested or requested == self.architecture:
            return

        print(
            f"[FL-CLIENT:{self.client_id}] Switching architecture "
            f"{self.architecture} -> {requested} for this session."
        )
        self.model = build_model(
            num_classes=self.data.num_classes,
            pretrained=True,
            architecture=requested,
        ).to(self.device)
        self.architecture = requested

    def set_parameters(self, parameters) -> None:
        # NOTĂ: NU mai există validarea „4 tensori" — ResNet-18 are zeci de tensori în
        # state_dict (inclusiv statistici BatchNorm). set_weights validează numărul corect.
        set_weights(self.model, [np.asarray(p) for p in parameters])

    def fit(self, parameters, config):  # type: ignore[override]
        self.status_state.set_status("Training")
        try:
            self._ensure_architecture(config)
            self.set_parameters(parameters)
            epochs = int(config.get("local_epochs", self.default_local_epochs))
            batch_size = int(config.get("batch_size", self.default_batch_size))
            learning_rate = float(config.get("learning_rate", self.learning_rate))

            fit_loader = self._make_loader(self.fit_records, train=True, batch_size=batch_size)
            val_loader = self._make_loader(self.val_records, train=False, batch_size=batch_size)

            optimizer = torch.optim.SGD(
                self.model.parameters(),
                lr=learning_rate,
                momentum=self.momentum,
                weight_decay=self.weight_decay,
            )
            criterion = nn.CrossEntropyLoss(weight=self.class_weights)

            best_val_loss = float("inf")
            best_epoch = 0
            best_state = copy.deepcopy(self.model.state_dict())
            last_train_metrics = {"train_loss": 0.0, "train_accuracy": 0.0}

            for epoch in range(1, max(1, epochs) + 1):
                last_train_metrics = train_one_epoch(
                    self.model,
                    fit_loader,
                    optimizer,
                    criterion,
                    self.device,
                    grad_clip_norm=self.grad_clip_norm,
                )
                val_loss, val_accuracy, val_macro_f1 = evaluate(
                    self.model, val_loader, self.device, criterion
                )

                if val_loss + 1e-5 < best_val_loss:
                    best_val_loss = val_loss
                    best_epoch = epoch
                    best_state = copy.deepcopy(self.model.state_dict())
                elif epoch - best_epoch >= self.early_stopping_patience:
                    break

            # Restaurează cel mai bun model pe validare.
            self.model.load_state_dict(best_state)
            val_loss, val_accuracy, val_macro_f1 = evaluate(
                self.model, val_loader, self.device, criterion
            )

            weights = get_weights(self.model)
            payload_bytes = parameters_num_bytes(weights)
            payload_mb = payload_bytes / (1024.0 * 1024.0)
            # Metrică nouă (§8.4): cost de comunicare per rundă, raportat în articol.
            print(
                f"[FL-CLIENT:{self.client_id}] round payload: {payload_mb:.2f} MB "
                f"({len(weights)} tensors); best_epoch={best_epoch}"
            )

            metrics = {
                "train_loss": float(last_train_metrics["train_loss"]),
                "train_accuracy": float(last_train_metrics["train_accuracy"]),
                "val_loss": float(val_loss),
                "val_accuracy": float(val_accuracy),
                "val_macro_f1": float(val_macro_f1),
                "best_epoch": float(best_epoch),
                "payload_bytes": float(payload_bytes),
                # Identitate + distribuția claselor, ca serverul să atribuie metricile
                # per-instituție (cost de comunicare, histogramă) — Flower nu expune altfel
                # maparea ClientProxy->instituție.
                "institution_id": self.institution_id,
                "class_histogram": self.class_histogram_json,
            }
            return weights, len(self.fit_records), metrics
        finally:
            self.status_state.set_status("Online")

    def evaluate(self, parameters, config):  # type: ignore[override]
        self.status_state.set_status("Evaluating")
        try:
            self._ensure_architecture(config)
            self.set_parameters(parameters)
            test_loader = self._make_loader(
                self.data.test_records, train=False, batch_size=self.default_batch_size
            )
            criterion = nn.CrossEntropyLoss(weight=self.class_weights)
            loss, accuracy, macro_f1 = evaluate(self.model, test_loader, self.device, criterion)
            return (
                float(loss),
                len(self.data.test_records),
                {"accuracy": float(accuracy), "macro_f1": float(macro_f1)},
            )
        finally:
            self.status_state.set_status("Online")


def main() -> None:
    server_address = os.getenv("FL_SERVER_ADDRESS", "fl-server:8080")
    client_id = os.getenv("FL_CLIENT_ID", "institution-1")
    control_api_base_url = os.getenv("FL_CONTROL_API_URL", "http://fl-server:8081").rstrip("/")
    institution_id_raw = os.getenv("FL_INSTITUTION_ID", "").strip()
    learning_rate = float(os.getenv("FL_LEARNING_RATE", "0.01"))
    momentum = float(os.getenv("FL_MOMENTUM", "0.9"))
    weight_decay = float(os.getenv("FL_WEIGHT_DECAY", "0.0001"))
    local_epochs = int(os.getenv("FL_LOCAL_EPOCHS", "2"))
    batch_size = int(os.getenv("FL_BATCH_SIZE", "8"))
    grad_clip_norm = float(os.getenv("FL_GRAD_CLIP_NORM", "5.0"))
    validation_ratio = float(os.getenv("FL_VALIDATION_RATIO", "0.15"))
    early_stopping_patience = int(os.getenv("FL_EARLY_STOPPING_PATIENCE", "2"))
    dataloader_workers = int(os.getenv("FL_DATALOADER_WORKERS", "0"))
    heartbeat_seconds = int(os.getenv("FL_HEARTBEAT_SECONDS", "5"))
    run_once = os.getenv("FL_RUN_ONCE", "false").lower() == "true"
    reconnect_delay_seconds = int(os.getenv("FL_RECONNECT_DELAY_SECONDS", "5"))

    if not institution_id_raw:
        raise RuntimeError("FL_INSTITUTION_ID is required and must be set to InstitutionId GUID from MIDSS.")

    try:
        institution_id = str(uuid.UUID(institution_id_raw))
    except ValueError as ex:
        raise RuntimeError(f"Invalid FL_INSTITUTION_ID '{institution_id_raw}'. Must be a GUID.") from ex

    data = load_institution_dataset(institution_key=client_id)

    print(
        f"[FL-CLIENT:{client_id}] Connecting to {server_address} "
        f"(institution_id={institution_id}, train={len(data.train_records)}, "
        f"test={len(data.test_records)}, image_size={data.image_size})"
    )

    status_state = ClientStatusState()
    stop_event = threading.Event()
    heartbeat_thread = threading.Thread(
        target=_heartbeat_loop,
        args=(
            control_api_base_url,
            institution_id,
            client_id,
            status_state,
            stop_event,
            heartbeat_seconds,
        ),
        daemon=True,
    )
    heartbeat_thread.start()

    while True:
        client = ResNetFederatedClient(
            client_id=client_id,
            institution_id=institution_id,
            data=data,
            learning_rate=learning_rate,
            momentum=momentum,
            weight_decay=weight_decay,
            default_local_epochs=local_epochs,
            default_batch_size=batch_size,
            grad_clip_norm=grad_clip_norm,
            validation_ratio=validation_ratio,
            early_stopping_patience=early_stopping_patience,
            dataloader_workers=dataloader_workers,
            status_state=status_state,
        )
        try:
            fl.client.start_numpy_client(server_address=server_address, client=client)
        except Exception as ex:
            print(f"[FL-CLIENT:{client_id}] Connection/training error: {ex}")

        if run_once:
            break

        time.sleep(max(1, reconnect_delay_seconds))

    stop_event.set()
    heartbeat_thread.join(timeout=2)


if __name__ == "__main__":
    main()
