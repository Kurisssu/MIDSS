# SITIM - AI Module Training Environment

## Required Python Packages

```bash
pip install torch torchvision
pip install onnx onnxruntime
pip install pandas pillow
pip install scikit-learn
pip install matplotlib seaborn  # for visualization
pip install tqdm  # for progress bars
```

Or install all at once:
```bash
pip install torch torchvision onnx onnxruntime pandas pillow scikit-learn matplotlib seaborn tqdm
```

## Versiuni recomandate:
- Python: 3.9-3.11
- PyTorch: 2.0+ (CPU sau GPU)
- ONNX Runtime: 1.16+

## Check GPU availability:
```python
import torch
print(f"CUDA available: {torch.cuda.is_available()}")
print(f"CUDA device: {torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'None'}")
```

## Dataset structure (după download APTOS):
```
data/
└── raw/
    ├── train_images/          # 3662 PNG images
    │   ├── 000c1434d8d7.png
    │   ├── 001639a390f0.png
    │   └── ...
    ├── test_images/           # 1928 PNG images (fără labels)
    ├── train.csv              # Labels (id_code, diagnosis: 0-4)
    ├── test.csv
    └── sample_submission.csv
```

## Expected model output:
```
models/
└── retinopathy_pretrained_v1.onnx  (~45 MB)
```

## Training duration:
- **CPU only**: ~4-6 ore (10 epochs)
- **GPU (CUDA)**: ~30-60 minute (10 epochs)
- **Apple Silicon (MPS)**: ~1-2 ore (10 epochs)
