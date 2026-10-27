# MIDSS AI Training - Quick Start

## ⚡ TL;DR (Comenzi Rapide)

```powershell
# 1. Setup Python environment
cd D:\University\MIDSS\scripts
python -m venv venv
.\venv\Scripts\Activate.ps1

# 2. Install dependencies
pip install torch torchvision --index-url https://download.pytorch.org/whl/cpu
pip install onnx onnxruntime pandas pillow scikit-learn matplotlib seaborn tqdm

# 3. Download APTOS dataset (manual)
# - Go to: https://www.kaggle.com/competitions/aptos2019-blindness-detection/data
# - Download "aptos2019-blindness-detection.zip" (4 GB)
# - Extract to: D:\University\MIDSS\data\raw\

# 4. Verify dataset
cd D:\University\MIDSS\data\raw
(Get-ChildItem -Path "train_images" -Filter *.png).Count  # Should be 3662

# 5. Run training
cd D:\University\MIDSS\scripts
python train_retinopathy.py

# 6. Wait for completion (30min-6h depending on CPU/GPU)

# 7. Test model
python test_onnx_model.py

# 8. Copy to MIDSS
Copy-Item `
  -Path "D:\University\MIDSS\models\retinopathy_pretrained_v1.onnx" `
  -Destination "D:\University\MIDSS\src\Midss\Midss.Api\models\"

# ✅ DONE! Anunță agentul pentru backend integration
```

---

## 📁 Expected File Structure După Training

```
D:\University\MIDSS\
├── data/
│   └── raw/
│       ├── train_images/              # 3662 PNG files
│       ├── test_images/               # 1928 PNG files
│       └── train.csv                  # Labels
│
├── models/
│   ├── retinopathy_pretrained_v1.onnx # 🎯 Main output (44.85 MB)
│   ├── retinopathy_best.pth           # PyTorch checkpoint (backup)
│   └── training_report.json           # Metrics & config
│
├── scripts/
│   ├── venv/                          # Python virtual environment
│   ├── train_retinopathy.py           # Training script
│   ├── test_onnx_model.py             # Testing script
│   ├── TRAINING_GUIDE.md              # Detailed guide
│   └── QUICK_START.md                 # This file
│
└── src/Midss/Midss.Api/
    └── models/
        └── retinopathy_pretrained_v1.onnx  # Copy here after training
```

---

## 🎯 Success Criteria

După training, ar trebui să vezi:

```
✅ models/retinopathy_pretrained_v1.onnx (~44-46 MB)
✅ Training accuracy: >80% (ideal: 85-90%)
✅ Validation accuracy: >75% (ideal: 85-90%)
✅ Test inference works on 5 sample images
✅ ONNX model loadable with onnxruntime
```

---

## ⏱️ Timeline

| Task | Duration | Notes |
|------|----------|-------|
| Python setup | 5-10 min | One-time only |
| Download APTOS | 15-30 min | Depends on internet |
| Extract dataset | 2-5 min | |
| Install packages | 5-10 min | One-time only |
| **Training** | **30min-6h** | **GPU: 30-60min, CPU: 4-6h** |
| Test model | 1 min | |
| Copy to API | 30 sec | |

**Total**: 1-7 ore (mostly waiting for download + training)

---

## 🆘 Fast Help

### Training prea lent?
Edit `train_retinopathy.py`:
```python
NUM_EPOCHS = 5  # Instead of 10 (faster, slightly lower accuracy)
BATCH_SIZE = 8  # Instead of 16 (if out of memory)
```

### Out of memory?
```python
BATCH_SIZE = 4  # Smallest batch size
```

### Nu ai GPU?
Normal! Training pe CPU funcționează perfect (doar mai lent). PyTorch automat detectează și folosește CPU.

### Training crashed?
Model parțial salvat în `retinopathy_best.pth`. Poți relua sau reduce parametrii și restart.

---

## 📞 When Ready

După ce ai `retinopathy_pretrained_v1.onnx` gata, anunță cu:
```
✅ Training done!
Accuracy: XX.XX%
Model size: XX.XX MB
Ready for backend integration!
```

Then we'll implement:
1. Database schema (AIModel, AIAnalysisResult)
2. AIInferenceService (ONNX inference)
3. API Controller (POST /api/analysis/retinopathy)
4. UI Component (AIAnalysis.razor)
5. Testing with real DICOM studies

---

**Good luck! 🚀**
