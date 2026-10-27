from __future__ import annotations

import io
import math
import os
import threading
import uuid
import hashlib
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Dict, List, Optional, Tuple

import flwr as fl
import numpy as np
import onnx
import torch
from flwr.common import ndarrays_to_parameters, parameters_to_ndarrays
from flask import Flask, Response, jsonify, request

from fl_common.model_def import build_model, get_weights, resolve_architecture, set_weights


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def weighted_average(metrics: List[Tuple[int, dict]]) -> dict:
    total_examples = sum(num_examples for num_examples, _ in metrics)
    if total_examples == 0:
        return {"accuracy": 0.0, "macro_f1": 0.0}

    aggregated: Dict[str, float] = {}
    for num_examples, metric in metrics:
        for key, value in metric.items():
            if isinstance(value, (int, float)):
                aggregated[key] = aggregated.get(key, 0.0) + (num_examples * float(value))

    if not aggregated:
        return {"accuracy": 0.0, "macro_f1": 0.0}
    return {key: value / total_examples for key, value in aggregated.items()}


def fit_config(server_round: int) -> dict:
    return {
        "round": server_round,
        "local_epochs": int(os.getenv("FL_LOCAL_EPOCHS", "1")),
        "batch_size": int(os.getenv("FL_BATCH_SIZE", "32")),
        "learning_rate": float(os.getenv("FL_LEARNING_RATE", "0.05")),
    }


def _session_config_fn(runtime: "SessionRuntime"):
    """Închide peste sesiune ca să adauge arhitectura la configul trimis clienților.

    Se folosește ȘI pentru evaluate: Flower nu trimite implicit niciun config acolo, iar
    clientul are nevoie de aceeași informație pe ambele căi ca să nu-și reconstruiască
    modelul doar pe jumătate de rundă.
    """

    def _config(server_round: int) -> dict:
        config = fit_config(server_round)
        config["architecture"] = runtime.architecture
        return config

    return _config


def _initial_parameters(runtime: "SessionRuntime"):
    """Ponderile de start ale modelului global, în arhitectura cerută de sesiune.

    Pornim din ImageNet, nu din inițializare aleatoare: fine-tuning-ul federat pe ~1000 de
    imagini per instituție depinde de acest transfer — fără el prima rundă ar porni practic
    de la zero.
    """
    num_classes = int(os.getenv("FL_NUM_CLASSES", "5"))
    model = build_model(
        num_classes=num_classes,
        pretrained=True,
        architecture=runtime.architecture,
    )
    return ndarrays_to_parameters(get_weights(model))


@dataclass
class ClientUpdateRuntime:
    """Metricile per-client raportate în faza fit a unei runde (cost de comunicare etc.)."""
    institution_id: str
    payload_bytes: Optional[int] = None
    training_loss: Optional[float] = None
    validation_accuracy: Optional[float] = None


@dataclass
class RoundRuntime:
    round_number: int
    aggregated_loss: Optional[float] = None
    aggregated_accuracy: Optional[float] = None
    aggregated_macro_f1: Optional[float] = None
    completed_at_utc: Optional[str] = None
    # Update-urile per-client din faza fit (cheie = institution_id).
    client_updates: Dict[str, ClientUpdateRuntime] = field(default_factory=dict)


@dataclass
class ParticipantRuntime:
    institution_id: str
    status: str = "Invited"
    last_heartbeat_utc: Optional[str] = None
    # Distribuția claselor raportată de client (JSON: {"0":n0,...}); setată o singură dată.
    class_histogram_json: Optional[str] = None


@dataclass
class ClientRuntime:
    institution_id: str
    client_id: str
    status: str = "Online"
    last_heartbeat_utc: Optional[str] = None


@dataclass
class SessionRuntime:
    session_id: str
    model_key: str
    total_rounds: int
    architecture: str
    min_available_clients: int
    min_fit_clients: int
    min_evaluate_clients: int
    status: str = "Pending"
    current_round: int = 0
    started_at_utc: Optional[str] = None
    finished_at_utc: Optional[str] = None
    last_error: Optional[str] = None
    output_model_path: Optional[str] = None
    output_model_sha256: Optional[str] = None
    output_model_size_bytes: Optional[int] = None
    output_model_num_classes: Optional[int] = None
    output_model_image_size: Optional[int] = None
    output_model_bytes: Optional[bytes] = None
    latest_parameters: Optional[fl.common.Parameters] = None
    rounds: List[RoundRuntime] = field(default_factory=list)
    participants: Dict[str, ParticipantRuntime] = field(default_factory=dict)
    stop_event: threading.Event = field(default_factory=threading.Event)
    lock: threading.Lock = field(default_factory=threading.Lock)


class SessionAwareFedAvg(fl.server.strategy.FedAvg):
    def __init__(self, runtime: SessionRuntime, **kwargs):
        super().__init__(**kwargs)
        self.runtime = runtime

    def configure_fit(self, server_round, parameters, client_manager):
        if self.runtime.stop_event.is_set():
            raise RuntimeError("Session stop requested")

        now = utc_now_iso()
        with self.runtime.lock:
            for participant in self.runtime.participants.values():
                if participant.status in ("Invited", "Accepted", "Training"):
                    participant.status = "Training"
                    participant.last_heartbeat_utc = now

        return super().configure_fit(server_round, parameters, client_manager)

    def aggregate_evaluate(self, server_round, results, failures):
        loss_agg, metrics_agg = super().aggregate_evaluate(server_round, results, failures)
        now = utc_now_iso()

        with self.runtime.lock:
            self.runtime.current_round = server_round
            accuracy = float(metrics_agg.get("accuracy", 0.0)) if metrics_agg else None
            macro_f1 = float(metrics_agg.get("macro_f1", 0.0)) if metrics_agg else None
            existing = next(
                (round_item for round_item in self.runtime.rounds if round_item.round_number == server_round),
                None,
            )
            if existing is None:
                self.runtime.rounds.append(
                    RoundRuntime(
                        round_number=server_round,
                        aggregated_loss=float(loss_agg) if loss_agg is not None else None,
                        aggregated_accuracy=accuracy,
                        aggregated_macro_f1=macro_f1,
                        completed_at_utc=now,
                    )
                )
            else:
                existing.aggregated_loss = float(loss_agg) if loss_agg is not None else None
                existing.aggregated_accuracy = accuracy
                existing.aggregated_macro_f1 = macro_f1
                existing.completed_at_utc = now

            for participant in self.runtime.participants.values():
                if participant.status == "Training":
                    participant.last_heartbeat_utc = now

        return loss_agg, metrics_agg

    def aggregate_fit(self, server_round, results, failures):
        parameters_agg, metrics_agg = super().aggregate_fit(server_round, results, failures)
        now = utc_now_iso()
        with self.runtime.lock:
            if parameters_agg is not None:
                self.runtime.latest_parameters = parameters_agg

            # Capturează metricile per-client raportate în fit: cost de comunicare
            # (payload_bytes) per (rundă, instituție) și histograma de clase (o dată).
            round_item = next(
                (r for r in self.runtime.rounds if r.round_number == server_round), None
            )
            if round_item is None:
                round_item = RoundRuntime(round_number=server_round)
                self.runtime.rounds.append(round_item)

            for _client_proxy, fit_res in results:
                metric = getattr(fit_res, "metrics", None) or {}
                institution_id = str(metric.get("institution_id", "")).strip()
                if not institution_id:
                    continue

                payload_raw = metric.get("payload_bytes")
                round_item.client_updates[institution_id] = ClientUpdateRuntime(
                    institution_id=institution_id,
                    payload_bytes=int(float(payload_raw)) if payload_raw is not None else None,
                    training_loss=float(metric["train_loss"]) if "train_loss" in metric else None,
                    validation_accuracy=float(metric["val_accuracy"]) if "val_accuracy" in metric else None,
                )

                histogram = metric.get("class_histogram")
                participant = self.runtime.participants.get(institution_id)
                if participant is not None and histogram and not participant.class_histogram_json:
                    participant.class_histogram_json = str(histogram)

        return parameters_agg, metrics_agg


app = Flask(__name__)

_sessions: Dict[str, SessionRuntime] = {}
_active_session_id: Optional[str] = None
_sessions_lock = threading.Lock()
_registered_clients: Dict[str, ClientRuntime] = {}
_clients_lock = threading.Lock()


def _normalize_guid(value: str) -> Optional[str]:
    try:
        return str(uuid.UUID(value.strip()))
    except Exception:
        return None


def _client_is_online(client: ClientRuntime, stale_after_seconds: int) -> bool:
    if not client.last_heartbeat_utc:
        return False
    try:
        heartbeat = datetime.fromisoformat(client.last_heartbeat_utc.replace("Z", "+00:00"))
    except ValueError:
        return False
    age_seconds = (datetime.now(timezone.utc) - heartbeat).total_seconds()
    return age_seconds <= stale_after_seconds


def _client_to_dict(client: ClientRuntime, stale_after_seconds: int) -> dict:
    return {
        "institution_id": client.institution_id,
        "client_id": client.client_id,
        "status": client.status,
        "last_heartbeat_utc": client.last_heartbeat_utc,
        "is_online": _client_is_online(client, stale_after_seconds),
    }


def _onnx_opset_version() -> int:
    # Opset compatibil cu Microsoft.ML.OnnxRuntime folosit în .NET (1.20.x → opset 17 OK).
    return int(os.getenv("FL_ONNX_OPSET", "17"))


def _build_onnx_from_aggregated_parameters(ndarrays: List[np.ndarray], architecture: str) -> tuple[bytes, int, int]:
    """Reconstituie modelul in arhitectura sesiunii din parametrii agregați (state_dict ordonat) și exportă ONNX.

    Nu se mai asamblează graful manual (sute de noduri ResNet). În schimb se reface modelul
    PyTorch și se folosește torch.onnx.export. Ieșirea graful = LOGITS (fără softmax — partea
    .NET aplică softmax o singură dată; vezi capcana dublu-softmax).
    """
    if not ndarrays:
        raise RuntimeError("No aggregated parameters available for ONNX export.")

    image_size = int(os.getenv("FL_PREPROCESSING_IMAGE_SIZE", "512"))
    if image_size <= 0:
        raise RuntimeError("FL_PREPROCESSING_IMAGE_SIZE must be > 0.")

    # num_classes = lungimea ultimului tensor (fc.bias) din state_dict — valabil pentru
    # orice variantă ResNet, capul fiind mereu un Linear final.
    num_classes = int(np.asarray(ndarrays[-1]).reshape(-1).shape[0])
    if num_classes < 2:
        # fallback defensiv pe env dacă inferența din shape eșuează.
        num_classes = int(os.getenv("FL_NUM_CLASSES", "5"))

    model = build_model(num_classes=num_classes, pretrained=False, architecture=architecture)
    set_weights(model, [np.asarray(t, dtype=np.float32) for t in ndarrays])
    model.eval()

    dummy_input = torch.randn(1, 3, image_size, image_size, dtype=torch.float32)
    buffer = io.BytesIO()
    torch.onnx.export(
        model,
        dummy_input,
        buffer,
        input_names=["input"],
        output_names=["logits"],
        dynamic_axes={"input": {0: "batch"}, "logits": {0: "batch"}},
        opset_version=_onnx_opset_version(),
        do_constant_folding=True,
    )
    artifact_bytes = buffer.getvalue()

    onnx_model = onnx.load_from_string(artifact_bytes)
    onnx.checker.check_model(onnx_model)
    return artifact_bytes, num_classes, image_size


def _export_global_model(runtime: SessionRuntime) -> tuple[bytes, str, int, int]:
    if runtime.latest_parameters is None:
        raise RuntimeError("No aggregated model parameters available after FL training.")

    ndarrays = [np.asarray(tensor, dtype=np.float32) for tensor in parameters_to_ndarrays(runtime.latest_parameters)]
    artifact_bytes, num_classes, image_size = _build_onnx_from_aggregated_parameters(ndarrays, runtime.architecture)
    artifact_sha = hashlib.sha256(artifact_bytes).hexdigest()
    return artifact_bytes, artifact_sha, num_classes, image_size


def _json_safe(value):
    """Înlocuiește recursiv float-urile nefinite cu None.

    `json.dumps` scrie implicit tokenurile `NaN` / `Infinity`, care NU sunt JSON valid
    (RFC 8259 admite doar numere finite). `System.Text.Json` din MIDSS le respinge, iar
    răspunsul devine imposibil de deserializat — nu doar metrica afectată, ci întregul
    payload al sesiunii.

    S-a manifestat concret: un client a raportat `training_loss = NaN` într-o rundă, iar
    din acel moment MIDSS nu a mai putut ingera NICIO rundă, deși antrenarea a mers până
    la capăt. O metrică lipsă se reprezintă corect ca `null`.
    """
    if isinstance(value, float):
        return value if math.isfinite(value) else None
    if isinstance(value, dict):
        return {key: _json_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_safe(item) for item in value]
    return value


def _session_to_dict(runtime: SessionRuntime) -> dict:
    with runtime.lock:
        return _json_safe({
            "session_id": runtime.session_id,
            "model_key": runtime.model_key,
            "architecture": runtime.architecture,
            "status": runtime.status,
            "total_rounds": runtime.total_rounds,
            "current_round": runtime.current_round,
            "started_at_utc": runtime.started_at_utc,
            "finished_at_utc": runtime.finished_at_utc,
            "last_error": runtime.last_error,
            "output_model_path": runtime.output_model_path,
            "output_model_sha256": runtime.output_model_sha256,
            "output_model_size_bytes": runtime.output_model_size_bytes,
            "output_model_num_classes": runtime.output_model_num_classes,
            "output_model_image_size": runtime.output_model_image_size,
            "has_output_artifact": runtime.output_model_bytes is not None,
            "rounds": [
                {
                    "round_number": round_item.round_number,
                    "aggregated_loss": round_item.aggregated_loss,
                    "aggregated_accuracy": round_item.aggregated_accuracy,
                    "aggregated_macro_f1": round_item.aggregated_macro_f1,
                    "completed_at_utc": round_item.completed_at_utc,
                    "client_updates": [
                        {
                            "institution_id": update.institution_id,
                            "payload_bytes": update.payload_bytes,
                            "training_loss": update.training_loss,
                            "validation_accuracy": update.validation_accuracy,
                        }
                        for update in sorted(
                            round_item.client_updates.values(), key=lambda u: u.institution_id
                        )
                    ],
                }
                for round_item in sorted(runtime.rounds, key=lambda r: r.round_number)
            ],
            "participants": [
                {
                    "institution_id": participant.institution_id,
                    "status": participant.status,
                    "last_heartbeat_utc": participant.last_heartbeat_utc,
                    "class_histogram": participant.class_histogram_json,
                }
                for participant in sorted(runtime.participants.values(), key=lambda p: p.institution_id)
            ],
        })


def _run_fl_session(runtime: SessionRuntime) -> None:
    global _active_session_id

    server_address = os.getenv("FL_SERVER_ADDRESS", "0.0.0.0:8080")

    with runtime.lock:
        runtime.status = "Running"
        runtime.started_at_utc = utc_now_iso()
        for participant in runtime.participants.values():
            participant.status = "Accepted"
            participant.last_heartbeat_utc = runtime.started_at_utc

    strategy = SessionAwareFedAvg(
        runtime=runtime,
        min_available_clients=runtime.min_available_clients,
        min_fit_clients=runtime.min_fit_clients,
        min_evaluate_clients=runtime.min_evaluate_clients,
        on_fit_config_fn=_session_config_fn(runtime),
        on_evaluate_config_fn=_session_config_fn(runtime),
        evaluate_metrics_aggregation_fn=weighted_average,
        # Serverul furnizează parametrii de start în locul obișnuitului „cere-i unui client
        # la întâmplare". Ordinea impune asta: la INIT niciun client nu a primit încă vreun
        # fit_config, deci nu ar avea cum să știe ce arhitectură vrea sesiunea — ar răspunde
        # cu cea din mediul lui. Construind aici modelul, serverul devine autoritatea, iar
        # clienții se aliniază la prima rundă prin `_ensure_architecture`.
        initial_parameters=_initial_parameters(runtime),
    )

    try:
        fl.server.start_server(
            server_address=server_address,
            config=fl.server.ServerConfig(num_rounds=runtime.total_rounds),
            strategy=strategy,
        )

        with runtime.lock:
            if runtime.stop_event.is_set():
                runtime.status = "Stopped"
                runtime.last_error = "Stopped by user."
                for participant in runtime.participants.values():
                    if participant.status in ("Invited", "Accepted", "Training"):
                        participant.status = "Stopped"
            else:
                artifact_bytes, artifact_sha, output_num_classes, output_image_size = _export_global_model(runtime)
                runtime.status = "Completed"
                runtime.output_model_path = f"fl-models/{runtime.session_id}/global.onnx"
                runtime.output_model_bytes = artifact_bytes
                runtime.output_model_sha256 = artifact_sha
                runtime.output_model_size_bytes = len(artifact_bytes)
                runtime.output_model_num_classes = output_num_classes
                runtime.output_model_image_size = output_image_size
                for participant in runtime.participants.values():
                    participant.status = "Completed"
                    participant.last_heartbeat_utc = utc_now_iso()
    except Exception as ex:
        with runtime.lock:
            runtime.status = "Stopped" if runtime.stop_event.is_set() else "Failed"
            runtime.last_error = str(ex)
            for participant in runtime.participants.values():
                if participant.status in ("Invited", "Accepted", "Training"):
                    participant.status = "Stopped" if runtime.stop_event.is_set() else "Failed"
    finally:
        with runtime.lock:
            runtime.finished_at_utc = utc_now_iso()

        with _sessions_lock:
            if _active_session_id == runtime.session_id:
                _active_session_id = None


@app.get("/health")
def health():
    with _sessions_lock:
        active_id = _active_session_id
    return jsonify({"status": "ok", "active_session_id": active_id})


@app.post("/sessions")
def start_session():
    global _active_session_id

    payload = request.get_json(silent=True) or {}
    model_key = str(payload.get("model_key", "")).strip()
    total_rounds = int(payload.get("total_rounds", 0))
    session_id = str(payload.get("session_id", "")).strip()
    raw_institution_ids = [str(value).strip() for value in payload.get("institution_ids", []) if str(value).strip()]

    if not model_key:
        return jsonify({"error": "model_key is required"}), 400
    if total_rounds <= 0:
        return jsonify({"error": "total_rounds must be greater than zero"}), 400
    if not session_id:
        return jsonify({"error": "session_id is required"}), 400
    if len(raw_institution_ids) == 0:
        return jsonify({"error": "At least one institution_id is required"}), 400

    institution_ids: List[str] = []
    invalid_institution_ids: List[str] = []
    for raw_id in raw_institution_ids:
        normalized_id = _normalize_guid(raw_id)
        if normalized_id is None:
            invalid_institution_ids.append(raw_id)
            continue
        institution_ids.append(normalized_id)

    if invalid_institution_ids:
        return jsonify(
            {
                "error": "institution_ids must contain valid GUID values",
                "invalid_institution_ids": invalid_institution_ids,
            }
        ), 400

    institution_ids = sorted(set(institution_ids))

    min_available_clients = max(1, int(payload.get("min_available_clients", 3)))
    min_fit_clients = max(1, int(payload.get("min_fit_clients", 3)))
    min_evaluate_clients = max(1, int(payload.get("min_evaluate_clients", 3)))

    # Arhitectura e per sesiune: vine din dropdown-ul UI prin MIDSS. Lipsa ei cade pe
    # FL_ARCHITECTURE, deci apelurile vechi raman valide.
    try:
        architecture = resolve_architecture(payload.get("architecture"))
    except RuntimeError as ex:
        return jsonify({"error": str(ex)}), 400

    stale_after_seconds = max(5, int(os.getenv("FL_CLIENT_STALE_SECONDS", "20")))
    with _clients_lock:
        online_client_ids = {
            institution_id
            for institution_id, client in _registered_clients.items()
            if _client_is_online(client, stale_after_seconds)
        }

    missing_institution_ids = [institution_id for institution_id in institution_ids if institution_id not in online_client_ids]
    if missing_institution_ids:
        return jsonify(
            {
                "error": "Missing online FL clients for one or more selected institutions",
                "missing_institution_ids": missing_institution_ids,
                "online_institution_ids": sorted(online_client_ids),
            }
        ), 409

    with _sessions_lock:
        if _active_session_id is not None:
            active_runtime = _sessions.get(_active_session_id)
            if active_runtime and active_runtime.status in ("Pending", "Running"):
                return jsonify({"error": "Another FL session is already running"}), 409

        if session_id in _sessions:
            return jsonify({"error": f"Session '{session_id}' already exists"}), 409

        runtime = SessionRuntime(
            session_id=session_id,
            model_key=model_key,
            total_rounds=total_rounds,
            architecture=architecture,
            min_available_clients=min_available_clients,
            min_fit_clients=min_fit_clients,
            min_evaluate_clients=min_evaluate_clients,
            participants={
                institution_id: ParticipantRuntime(institution_id=institution_id)
                for institution_id in institution_ids
            },
        )
        _sessions[runtime.session_id] = runtime
        _active_session_id = runtime.session_id

    worker = threading.Thread(target=_run_fl_session, args=(runtime,), daemon=True)
    worker.start()

    return jsonify(_session_to_dict(runtime))


@app.get("/sessions/<session_id>")
def get_session_status(session_id: str):
    runtime = _sessions.get(session_id)
    if runtime is None:
        return jsonify({"error": "Session not found"}), 404
    return jsonify(_session_to_dict(runtime))


@app.post("/sessions/<session_id>/stop")
def stop_session(session_id: str):
    runtime = _sessions.get(session_id)
    if runtime is None:
        return jsonify({"error": "Session not found"}), 404

    runtime.stop_event.set()
    with runtime.lock:
        runtime.status = "Stopped"
        runtime.last_error = "Stopped by user."
        for participant in runtime.participants.values():
            if participant.status in ("Invited", "Accepted", "Training"):
                participant.status = "Stopped"
            participant.last_heartbeat_utc = utc_now_iso()
    return jsonify(_session_to_dict(runtime))


@app.get("/sessions/<session_id>/result")
def get_session_result(session_id: str):
    runtime = _sessions.get(session_id)
    if runtime is None:
        return jsonify({"error": "Session not found"}), 404
    return jsonify(_session_to_dict(runtime))


@app.get("/sessions/<session_id>/artifact")
def download_session_artifact(session_id: str):
    runtime = _sessions.get(session_id)
    if runtime is None:
        return jsonify({"error": "Session not found"}), 404

    with runtime.lock:
        artifact_bytes = runtime.output_model_bytes
        artifact_sha = runtime.output_model_sha256 or ""
        artifact_size = runtime.output_model_size_bytes or 0
        model_name = f"{runtime.session_id}-global.onnx"

    if artifact_bytes is None:
        return jsonify({"error": "Model artifact not available"}), 404

    return Response(
        artifact_bytes,
        mimetype="application/octet-stream",
        headers={
            "Content-Disposition": f'attachment; filename="{model_name}"',
            "X-Artifact-Sha256": artifact_sha,
            "X-Artifact-Size": str(artifact_size),
        },
    )


@app.post("/clients/register")
def register_client():
    payload = request.get_json(silent=True) or {}
    raw_institution_id = str(payload.get("institution_id", "")).strip()
    institution_id = _normalize_guid(raw_institution_id)
    if institution_id is None:
        return jsonify({"error": "institution_id must be a valid GUID"}), 400

    client_id = str(payload.get("client_id", "")).strip() or institution_id
    status = str(payload.get("status", "Online")).strip() or "Online"
    heartbeat = utc_now_iso()

    with _clients_lock:
        _registered_clients[institution_id] = ClientRuntime(
            institution_id=institution_id,
            client_id=client_id,
            status=status,
            last_heartbeat_utc=heartbeat,
        )

    stale_after_seconds = max(5, int(os.getenv("FL_CLIENT_STALE_SECONDS", "20")))
    return jsonify(_client_to_dict(_registered_clients[institution_id], stale_after_seconds))


@app.get("/clients")
def list_clients():
    stale_after_seconds = max(5, int(os.getenv("FL_CLIENT_STALE_SECONDS", "20")))
    with _clients_lock:
        clients = [
            _client_to_dict(client, stale_after_seconds)
            for _, client in sorted(_registered_clients.items(), key=lambda item: item[0])
        ]
    return jsonify(
        {
            "stale_after_seconds": stale_after_seconds,
            "clients": clients,
        }
    )


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=int(os.getenv("FL_CONTROL_API_PORT", "8081")))
