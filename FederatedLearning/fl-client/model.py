"""Shim de compatibilitate.

Definiția reală a modelului ResNet-18 trăiește în modulul comun `fl_common.model_def`,
partajat între client și server. Acest fișier doar re-exportă API-ul public ca să nu
rupă importurile existente (`from model import ...`).
"""

from __future__ import annotations

from fl_common.model_def import (  # noqa: F401
    build_model,
    resolve_architecture,
    compute_class_weights,
    evaluate,
    get_weights,
    parameters_num_bytes,
    predict_proba,
    set_weights,
    train_one_epoch,
)

__all__ = [
    "build_model",
    "resolve_architecture",
    "compute_class_weights",
    "evaluate",
    "get_weights",
    "parameters_num_bytes",
    "predict_proba",
    "set_weights",
    "train_one_epoch",
]
