"""Definiția comună a modelului pentru Federated Learning MIDSS.

Acest modul este importat ATÂT de clientul Flower (`fl-client`) CÂT și de planul de
control al serverului (`fl-server`), ca să existe o singură sursă de adevăr pentru:
  - arhitectura modelului (`build_model`),
  - (de)serializarea parametrilor pentru Flower (`get_weights` / `set_weights`),
  - bucla de antrenare / evaluare locală (`train_one_epoch` / `evaluate`),
  - ponderarea pe clase pentru date dezechilibrate (`compute_class_weights`).

Decizia arhitecturală: ResNet pre-antrenat ImageNet, fine-tuning federat complet (toate
straturile), cap `fc` înlocuit cu un `Linear(in_features, num_classes)` pentru cele 5
clase APTOS (No DR / Mild / Moderate / Severe / Proliferative). Varianta concretă se
alege prin `FL_ARCHITECTURE` (`resnet18` implicit, `resnet50` pentru comparație), ca
studiul comparativ să nu ceară modificări de cod.

Decizia BatchNorm (FedAvg): varianta (a) — statisticile BatchNorm (`running_mean`,
`running_var`, `num_batches_tracked`) sunt incluse în `state_dict` și agregate ca orice
alt parametru. Mai simplu și produce un model global exportabil determinist. Alternativa
FedBN (păstrarea statisticilor BN local) e menționată ca limitare/direcție viitoare în
documentația tezei; NU este implementată aici.

IMPORTANT (capcană dublu-softmax): modelul scoate **logits** (fără softmax în forward).
Softmax-ul se aplică DOAR la evaluare/`predict_proba`. Partea .NET aplică deja softmax
o singură dată pe ieșirea ONNX, deci graful exportat trebuie să rămână pe logits.
"""

from __future__ import annotations

import os
from typing import Dict, List, Tuple

import numpy as np
import torch
import torch.nn as nn
from torch.utils.data import DataLoader
from torchvision import models
from torchvision.models import ResNet18_Weights, ResNet50_Weights

# Arhitecturile suportate, selectabile prin FL_ARCHITECTURE. Capul `fc` e înlocuit
# identic în ambele cazuri; singura diferență este `in_features` (512 vs 2048), citit
# dinamic mai jos, deci adăugarea unei variante nu cere alte modificări.
#
# ATENȚIE: clienții ȘI fl-server trebuie să aibă ACEEAȘI valoare. `set_weights` compară
# numărul de tensori din `state_dict`, iar exportul ONNX de pe server reconstruiește
# modelul prin `build_model`. O nepotrivire nu se vede la pornire — abia la export,
# după ce s-au consumat toate rundele.
_ARCHITECTURES = {
    "resnet18": (models.resnet18, ResNet18_Weights.IMAGENET1K_V1),
    "resnet50": (models.resnet50, ResNet50_Weights.IMAGENET1K_V2),
}


def supported_architectures() -> list[str]:
    """Arhitecturile pe care le poate oferi UI-ul, în ordine stabilă."""
    return sorted(_ARCHITECTURES)


def resolve_architecture(architecture: str | None = None) -> str:
    """Validează arhitectura cerută; `None` cade pe `FL_ARCHITECTURE`, apoi pe resnet18."""
    arch = (architecture or os.getenv("FL_ARCHITECTURE", "resnet18")).strip().lower()
    if arch not in _ARCHITECTURES:
        raise RuntimeError(
            f"Arhitectura '{arch}' nu este suportată. "
            f"Valori valide: {', '.join(supported_architectures())}."
        )
    return arch


def build_model(
    num_classes: int = 5,
    pretrained: bool = True,
    architecture: str | None = None,
) -> nn.Module:
    """Construiește arhitectura cerută, cu capul final adaptat la `num_classes`.

    `architecture` explicit are prioritate peste `FL_ARCHITECTURE`: serverul îl primește
    per sesiune (din UI) și îl trimite clienților prin `fit_config`, astfel încât o
    comparație între arhitecturi să nu mai ceară repornirea containerelor. Mediul rămâne
    doar ca valoare implicită.

    `pretrained=True` pornește din ponderile ImageNet (fine-tuning federat).
    """
    if num_classes < 2:
        raise RuntimeError("num_classes trebuie să fie cel puțin 2 pentru clasificare.")

    arch = resolve_architecture(architecture)
    factory, weights_enum = _ARCHITECTURES[arch]
    model = factory(weights=weights_enum if pretrained else None)
    in_features = model.fc.in_features  # 512 la ResNet-18, 2048 la ResNet-50
    model.fc = nn.Linear(in_features, num_classes)
    return model


def get_weights(model: nn.Module) -> List[np.ndarray]:
    """Serializează parametrii modelului ca listă de ndarray, în ordinea stabilă a
    `state_dict` (include statisticile BatchNorm — vezi decizia BN din docstring-ul modulului).
    """
    return [tensor.detach().cpu().numpy() for tensor in model.state_dict().values()]


def set_weights(model: nn.Module, params: List[np.ndarray]) -> None:
    """Încarcă parametrii în model, în aceeași ordine ca `get_weights`.

    Ordinea cheilor din `state_dict` este deterministă pentru aceeași arhitectură, deci
    împerecherea poziție-cu-poziție între `get_weights` și `set_weights` este sigură.
    """
    state_dict = model.state_dict()
    keys = list(state_dict.keys())
    if len(params) != len(keys):
        raise RuntimeError(
            f"Număr de tensori nepotrivit la set_weights: aștept {len(keys)}, am primit {len(params)}."
        )

    new_state = {}
    for key, value in zip(keys, params):
        reference = state_dict[key]
        tensor = torch.as_tensor(np.asarray(value), dtype=reference.dtype)
        new_state[key] = tensor.reshape(reference.shape)
    model.load_state_dict(new_state, strict=True)


def parameters_num_bytes(params: List[np.ndarray]) -> int:
    """Dimensiunea totală în octeți a parametrilor (metrică de cost de comunicare per rundă)."""
    return int(sum(np.asarray(p).nbytes for p in params))


def compute_class_weights(labels: np.ndarray, num_classes: int) -> np.ndarray:
    """Ponderi pe clase invers proporționale cu frecvența (esențial pentru non-IID dezechilibrat).

    Clasele absente primesc pondere 1.0 (nu vor apărea oricum în loss).
    """
    counts = np.bincount(np.asarray(labels, dtype=np.int64), minlength=num_classes).astype(np.float32)
    non_zero = counts > 0
    weights = np.ones((num_classes,), dtype=np.float32)
    if not np.any(non_zero):
        return weights
    median_count = float(np.median(counts[non_zero]))
    weights[non_zero] = median_count / counts[non_zero]
    return weights


def train_one_epoch(
    model: nn.Module,
    loader: DataLoader,
    optimizer: torch.optim.Optimizer,
    criterion: nn.Module,
    device: torch.device,
    grad_clip_norm: float = 0.0,
) -> Dict[str, float]:
    """O epocă de antrenare standard PyTorch. Întoarce `train_loss` și `train_accuracy`."""
    model.train()
    running_loss = 0.0
    running_correct = 0
    running_total = 0

    for images, targets in loader:
        images = images.to(device)
        targets = targets.to(device)

        optimizer.zero_grad()
        logits = model(images)
        loss = criterion(logits, targets)
        loss.backward()
        if grad_clip_norm and grad_clip_norm > 0.0:
            nn.utils.clip_grad_norm_(model.parameters(), grad_clip_norm)
        optimizer.step()

        batch_size = targets.size(0)
        running_loss += float(loss.item()) * batch_size
        running_correct += int((logits.argmax(dim=1) == targets).sum().item())
        running_total += batch_size

    if running_total == 0:
        return {"train_loss": 0.0, "train_accuracy": 0.0}
    return {
        "train_loss": running_loss / running_total,
        "train_accuracy": running_correct / running_total,
    }


@torch.no_grad()
def evaluate(
    model: nn.Module,
    loader: DataLoader,
    device: torch.device,
    criterion: nn.Module | None = None,
) -> Tuple[float, float, float]:
    """Evaluează modelul. Întoarce `(loss, accuracy, macro_f1)`.

    macro-F1 este reținut intenționat (util pentru date dezechilibrate / non-IID).
    """
    model.eval()
    if criterion is None:
        criterion = nn.CrossEntropyLoss()

    total_loss = 0.0
    total = 0
    all_preds: List[int] = []
    all_targets: List[int] = []

    for images, targets in loader:
        images = images.to(device)
        targets = targets.to(device)
        logits = model(images)
        loss = criterion(logits, targets)

        batch_size = targets.size(0)
        total_loss += float(loss.item()) * batch_size
        total += batch_size
        all_preds.extend(logits.argmax(dim=1).cpu().tolist())
        all_targets.extend(targets.cpu().tolist())

    if total == 0:
        return 0.0, 0.0, 0.0

    loss_value = total_loss / total
    preds = np.asarray(all_preds, dtype=np.int64)
    targets_np = np.asarray(all_targets, dtype=np.int64)
    accuracy = float(np.mean(preds == targets_np))
    macro_f1 = _macro_f1(targets_np, preds)
    return loss_value, accuracy, macro_f1


def _macro_f1(y_true: np.ndarray, y_pred: np.ndarray) -> float:
    """macro-F1 fără dependență obligatorie de sklearn (folosit ca fallback robust).

    Dacă scikit-learn este disponibil, îl folosim (consecvent cu literatura); altfel
    reimplementăm identic.
    """
    try:
        from sklearn.metrics import f1_score

        return float(f1_score(y_true, y_pred, average="macro", zero_division=0))
    except Exception:
        classes = np.unique(np.concatenate([y_true, y_pred]))
        f1_scores: List[float] = []
        for cls in classes:
            true_pos = int(np.sum((y_true == cls) & (y_pred == cls)))
            false_pos = int(np.sum((y_true != cls) & (y_pred == cls)))
            false_neg = int(np.sum((y_true == cls) & (y_pred != cls)))
            precision_den = true_pos + false_pos
            recall_den = true_pos + false_neg
            precision = true_pos / precision_den if precision_den > 0 else 0.0
            recall = true_pos / recall_den if recall_den > 0 else 0.0
            if precision + recall == 0.0:
                f1_scores.append(0.0)
            else:
                f1_scores.append(2.0 * precision * recall / (precision + recall))
        return float(np.mean(f1_scores)) if f1_scores else 0.0


@torch.no_grad()
def predict_proba(model: nn.Module, images: torch.Tensor, device: torch.device) -> np.ndarray:
    """Probabilități softmax pentru un batch de imagini (DOAR pentru evaluare/diagnostic)."""
    model.eval()
    logits = model(images.to(device))
    return torch.softmax(logits, dim=1).cpu().numpy()
