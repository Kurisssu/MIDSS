# models/

Fișierele de model (`*.pth`, `*.onnx`) **nu** sunt în Git: sunt mari (checkpoint-ul `.pth` ~131 MB, exportul `.onnx` ~43 MB) și sunt ignorate prin `.gitignore`.

În repository rămâne doar `training_report.json`: raportul antrenării centralizate (epoci, acuratețe de validare, loss, matrice de confuzie, istoric pe epoci).

## Cum obții modelul

### Modelul centralizat (ResNet-18, APTOS 2019, 5 clase)

1. Descarcă datasetul conform `data/README.md`.
2. Antrenează și exportă cu `scripts/train_retinopathy.py` — pașii detaliați sunt în `scripts/TRAINING_GUIDE.md` și `scripts/QUICK_START.md`.
3. Rezultatul: `models/retinopathy_best.pth` (checkpoint PyTorch) și `models/retinopathy_pretrained_v1.onnx`.
4. Încarcă `.onnx` din aplicație: **Modele AI → Încarcă Model Nou** (vezi §7 din `README.md`). Aplicația îl stochează în MinIO, nu în acest folder.

Verificare locală, în afara aplicației: `scripts/test_onnx_model.py`.

### Modelele federate

Rezultă dintr-o sesiune de Federated Learning (vezi §8 din `README.md` și `FederatedLearning/RUNBOOK.md`). La final, modelul agregat este exportat în ONNX și poate fi activat pentru inferență direct din pagina sesiunii.

### Varianta pre-antrenată

Dacă exportul `.onnx` folosit în articol va fi publicat, va fi atașat ca **GitHub Release asset**, nu în Git.
