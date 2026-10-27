# MIDSS AI Model Training - Ghid Complet Pas cu Pas

## 📋 Overview
Acest ghid te va ajuta să antrenezi un model AI pentru detectarea retinopatiei diabetice pe dataset-ul APTOS 2019.

---

## PASUL 1: Pregătire Environment Python

### 1.1 Verifică Python instalat
```powershell
python --version
```
**Expected**: Python 3.9, 3.10 sau 3.11

Dacă nu ai Python instalat:
- Download: https://www.python.org/downloads/
- ✅ Check "Add Python to PATH" la instalare

### 1.2 Creează virtual environment (RECOMANDAT)
```powershell
cd D:\University\MIDSS\scripts

# Crează venv
python -m venv venv

# Activează venv
.\venv\Scripts\Activate.ps1

# Verificare
python --version
which python  # Should point to venv
```

### 1.3 Instalează dependencies
```powershell
# Upgrade pip
python -m pip install --upgrade pip

# Install PyTorch (CPU version - funcționează pe orice PC)
pip install torch torchvision --index-url https://download.pytorch.org/whl/cpu

# SAU dacă ai GPU NVIDIA (opțional, mult mai rapid):
pip install torch torchvision --index-url https://download.pytorch.org/whl/cu118

# Install alte pachete
pip install onnx onnxruntime
pip install pandas pillow
pip install scikit-learn
pip install matplotlib seaborn
pip install tqdm
```

**Timp**: ~5-10 minute (depinde de internet)

### 1.4 Verifică instalarea
```powershell
python -c "import torch; print(f'PyTorch: {torch.__version__}')"
python -c "import torch; print(f'CUDA available: {torch.cuda.is_available()}')"
python -c "import onnxruntime; print(f'ONNX Runtime: {onnxruntime.__version__}')"
```

**Expected output**:
```
PyTorch: 2.x.x
CUDA available: True (dacă ai GPU) sau False (CPU)
ONNX Runtime: 1.x.x
```

---

## PASUL 2: Download Dataset APTOS

### 2.1 Crează cont Kaggle
1. Accesează: https://www.kaggle.com/
2. Click "Register" (gratuit)
3. Verifică email

### 2.2 Acceptă regulile competiției
1. Accesează: https://www.kaggle.com/competitions/aptos2019-blindness-detection
2. Click pe tab-ul **"Data"**
3. Scroll jos → Click pe **"I Understand and Accept"**

### 2.3 Download dataset

**Opțiunea A: Manual (SIMPLU)**
1. Pe pagina Data, click pe **"Download All"**
2. Va descărca `aptos2019-blindness-detection.zip` (~4 GB)
3. **Salvează în**: `D:\University\MIDSS\data\raw\`

**Opțiunea B: Kaggle CLI (pentru automatizare)**
```powershell
# Install Kaggle CLI
pip install kaggle

# Setup API key (once)
# 1. Pe Kaggle.com → Account → "Create New API Token"
# 2. Download kaggle.json
# 3. Mută în: C:\Users\<username>\.kaggle\kaggle.json

# Download cu CLI
cd D:\University\MIDSS\data\raw
kaggle competitions download -c aptos2019-blindness-detection
```

**Timp**: 10-30 minute (depinde de viteza internetului)

### 2.4 Extract ZIP
```powershell
cd D:\University\MIDSS\data\raw

# Extract cu PowerShell
Expand-Archive -Path aptos2019-blindness-detection.zip -DestinationPath . -Force

# SAU cu 7-Zip (dacă e instalat)
7z x aptos2019-blindness-detection.zip
```

**Timp**: 2-5 minute

### 2.5 Verifică structura
```powershell
cd D:\University\MIDSS\data\raw

# Count imagini
(Get-ChildItem -Path "train_images" -Filter *.png).Count
# Expected: 3662

(Get-ChildItem -Path "test_images" -Filter *.png).Count
# Expected: 1928

# View labels
Get-Content train.csv -Head 10
```

**Expected structure**:
```
data/
└── raw/
    ├── train_images/          # 3662 PNG files
    ├── test_images/           # 1928 PNG files
    ├── train.csv              # Labels (id_code, diagnosis)
    ├── test.csv
    └── sample_submission.csv
```

---

## PASUL 3: Rulare Training

### 3.1 Pregătire
```powershell
cd D:\University\MIDSS\scripts

# Verifică că venv e activ
# Prompt should show: (venv) PS D:\...

# Verifică că scriptul există
Get-Item train_retinopathy.py
```

### 3.2 Configurare training (OPȚIONAL)

Dacă vrei să modifici parametrii, editează `train_retinopathy.py`:

```python
# În clasa Config:
NUM_EPOCHS = 10          # Reduce la 5 pentru test rapid
BATCH_SIZE = 16          # Reduce la 8 dacă out of memory
IMAGE_SIZE = 512         # Nu modifica (standard APTOS)
LEARNING_RATE = 0.0001   # Nu modifica
```

### 3.3 Start training
```powershell
python train_retinopathy.py
```

**Ce se întâmplă**:
1. ✅ Load dataset (train.csv + imagini)
2. ✅ Split 80/20 (train/validation)
3. ✅ Crează model ResNet18 (pretrained)
4. ✅ Training loop (10 epochs):
   - Epoch 1: Train → Validate → Save best model
   - Epoch 2: Train → Validate → Update if better
   - ... (repeat)
5. ✅ Export la ONNX: `retinopathy_pretrained_v1.onnx`
6. ✅ Salvează raport: `training_report.json`

**Output esperiat**:
```
============================================================
MIDSS AI Module - Diabetic Retinopathy Training
============================================================

🖥️  Device: cuda (sau cpu)
   GPU: NVIDIA GeForce RTX 3060 (sau CPU)

📂 Loading dataset from ..\data\raw\train.csv
   Total images: 3662
   Class distribution:
   0    1805
   1     370
   2     999
   3     193
   4     295

📊 Split:
   Training: 2929 images
   Validation: 733 images

🧠 Creating model: resnet18
   Total parameters: 11,181,637
   Trainable parameters: 11,181,637

🚀 Starting training for 10 epochs
============================================================

Epoch 1/10
------------------------------------------------------------
Training: 100%|██████████| 183/183 [03:45<00:00,  0.81it/s, loss=1.234, acc=45.67%]
Validation: 100%|██████████| 46/46 [00:32<00:00,  1.42it/s, loss=1.123, acc=52.34%]

📈 Epoch 1 Summary:
   Train Loss: 1.2345 | Train Acc: 45.67%
   Val Loss:   1.1234 | Val Acc:   52.34%
   ✅ Best model saved! (Val Acc: 52.34%)

Epoch 2/10
------------------------------------------------------------
...

============================================================
🎉 Training completed!
   Best validation accuracy: 87.23%

📊 Final Validation Metrics:

Classification Report:
              precision    recall  f1-score   support

      No DR       0.91      0.93      0.92       361
    Mild DR       0.78      0.65      0.71        74
Moderate DR       0.88      0.89      0.88       200
  Severe DR       0.72      0.68      0.70        39
Proliferative     0.85      0.83      0.84        59

   accuracy                           0.87       733
  macro avg       0.83      0.80      0.81       733

Confusion Matrix:
[[336  12   9   2   2]
 [ 14  48   8   3   1]
 [  7   5 177   6   5]
 [  1   4   7  26   1]
 [  1   2   5   2  49]]

📦 Exporting model to ONNX...
   ✅ ONNX model saved: ..\models\retinopathy_pretrained_v1.onnx
   File size: 44.85 MB
   ✅ Training report saved: ..\models\training_report.json

============================================================
✅ All done! Model ready for deployment in MIDSS.
============================================================
```

**Timp total**:
- **CPU**: 4-6 ore
- **GPU (NVIDIA)**: 30-60 minute
- **Apple Silicon (M1/M2)**: 1-2 ore

### 3.4 Monitorizare progres

**În timpul training-ului**:
- Fiecare epoch arată progress bar cu loss și accuracy
- Modelul se salvează automat când validation accuracy crește
- Dacă vrei să oprești: `Ctrl+C` (modelul parțial salvat în `retinopathy_best.pth`)

**Dacă training se blochează**:
- Reduce `BATCH_SIZE` la 8 sau 4 (în `train_retinopathy.py`)
- Verifică RAM usage (trebuie minim 8 GB disponibil)

---

## PASUL 4: Testare Model

### 4.1 Verifică output
```powershell
cd D:\University\MIDSS\models

# Verifică că fișierele există
Get-Item retinopathy_pretrained_v1.onnx
Get-Item training_report.json
Get-Item retinopathy_best.pth  # PyTorch checkpoint (backup)

# Check file size
Get-Item retinopathy_pretrained_v1.onnx | Select-Object Length
# Expected: ~45 MB (44,000,000+ bytes)
```

### 4.2 Rulează test script
```powershell
cd D:\University\MIDSS\scripts

python test_onnx_model.py
```

**Expected output**:
```
============================================================
MIDSS AI Module - ONNX Model Testing
============================================================

✅ Model found: ..\models\retinopathy_pretrained_v1.onnx
   Size: 44.85 MB

🧠 Loading ONNX model...
   Input name: input
   Input shape: [1, 3, 512, 512]
   Output name: output
   Output shape: [1, 5]

🔍 Looking for test images...
   Found 5 test images

============================================================
Running inference on sample images...
============================================================

📸 Image 1: 000c1434d8d7.png
   Prediction: Moderate DR
   Confidence: 89.23%
   All probabilities:
      No DR: 2.34%
      Mild DR: 4.56%
      Moderate DR: 89.23%
      Severe DR: 2.11%
      Proliferative DR: 1.76%

📸 Image 2: 001639a390f0.png
   Prediction: No DR
   Confidence: 95.67%
   ...

============================================================
✅ Testing completed!

Next steps:
1. Copy retinopathy_pretrained_v1.onnx to MIDSS API project
2. Implement AIInferenceService in backend
3. Test with real DICOM studies
============================================================
```

---

## PASUL 5: Integrare în MIDSS

### 5.1 Copy model la MIDSS API
```powershell
# Crează folder models în API project
New-Item -ItemType Directory -Path "D:\University\MIDSS\src\Midss\Midss.Api\models" -Force

# Copy ONNX model
Copy-Item `
  -Path "D:\University\MIDSS\models\retinopathy_pretrained_v1.onnx" `
  -Destination "D:\University\MIDSS\src\Midss\Midss.Api\models\"

# Verifică
Get-Item "D:\University\MIDSS\src\Midss\Midss.Api\models\retinopathy_pretrained_v1.onnx"
```

### 5.2 Anunță-mă când e gata
```
✅ Training completed
✅ Model ONNX exportat (44.85 MB)
✅ Test inference OK (5 imagini sample)
✅ Model copiat în Midss.Api/models/

➡️ READY pentru backend integration!
```

---

## 🆘 Troubleshooting

### Problem: Out of Memory (OOM)
**Solution**:
```python
# In train_retinopathy.py, reduce:
BATCH_SIZE = 8  # sau chiar 4
```

### Problem: CUDA out of memory (GPU)
**Solution**:
```python
# Use CPU instead
DEVICE = torch.device('cpu')  # Force CPU
# sau rulează cu CPU flag:
python train_retinopathy.py --device cpu
```

### Problem: Training prea lent (CPU)
**Options**:
1. Reduce epochs: `NUM_EPOCHS = 5`
2. Reduce image size: `IMAGE_SIZE = 256` (mai puțin accuracy)
3. Folosește un PC cu GPU (Google Colab gratuit cu GPU)

### Problem: Dataset not found
**Solution**:
```powershell
# Verifică paths:
Get-Item D:\University\MIDSS\data\raw\train_images
Get-Item D:\University\MIDSS\data\raw\train.csv
```

### Problem: ModuleNotFoundError
**Solution**:
```powershell
# Verifică venv activat:
# Prompt: (venv) PS ...

# Re-install pachetele:
pip install torch torchvision onnx onnxruntime pandas pillow scikit-learn tqdm
```

---

## 📊 Training Report Analysis

După training, verifică `training_report.json`:
```json
{
  "training_date": "2026-04-06T12:00:00",
  "best_val_accuracy": 0.8723,
  "num_epochs": 10,
  "num_train_samples": 2929,
  "num_val_samples": 733,
  "confusion_matrix": [[336, 12, 9, 2, 2], ...],
  ...
}
```

**Ce înseamnă accuracy 87.23%**:
- Model corect pentru ~637 din 733 imagini validation
- **Excelent pentru demo** (state-of-art e ~90-92% pe APTOS)

---

## ✅ Checklist Final

Înainte să treci la backend integration:

- [ ] Python environment setup (venv creat)
- [ ] Dependencies instalate (torch, onnx, etc.)
- [ ] APTOS dataset downloaded (3662 train images)
- [ ] Training completed (10 epochs, ~87% accuracy)
- [ ] ONNX model exportat (~45 MB)
- [ ] Test inference OK (5 sample images)
- [ ] Model copiat în `Midss.Api/models/`
- [ ] Training report salvat (JSON)

**Când toate sunt ✅, anunță-mă și trecem la backend integration!**

---

## 🚀 Alternative: Google Colab (dacă PC-ul e lent)

Dacă training-ul pe PC-ul tău e prea lent (>6h), poți folosi Google Colab gratuit cu GPU:

1. Upload script pe Google Drive
2. Deschide Colab notebook: https://colab.research.google.com/
3. Mount Drive + Run training
4. Download model ONNX după finalizare

**Tutorial Colab în `COLAB_TRAINING.md`** (pot crea dacă vrei)

---

**Good luck! 🎓**
