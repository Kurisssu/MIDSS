# data/

Datasetul **nu** este în repository (~9,7 GB, plus regulile competiției Kaggle). Folderul `data/raw/` este ignorat de Git.

## APTOS 2019 Blindness Detection

1. Creează-ți cont pe Kaggle și acceptă regulile competiției:
   <https://www.kaggle.com/c/aptos2019-blindness-detection>
2. Descarcă arhiva competiției (din pagina *Data*, sau cu Kaggle CLI: `kaggle competitions download -c aptos2019-blindness-detection`).
3. Dezarhivează astfel încât să obții:

```
data/
└── raw/
    ├── train.csv
    ├── train_images/      # ~3662 imagini fundus .png
    ├── test.csv           # opțional
    └── test_images/       # opțional
```

`train.csv` + `train_images/` sunt folosite de:

- `scripts/train_retinopathy.py` — antrenarea modelului centralizat (vezi `scripts/TRAINING_GUIDE.md`);
- `scripts/seed_orthanc_fl.py` — conversia în DICOM și distribuirea non-IID în cele 3 Orthanc-uri (vezi §6 din `README.md`).
