# FederatedLearning/labels/

Aici stau etichetele clienților FL: `inst1-labels.csv`, `inst2-labels.csv`, `inst3-labels.csv`.

Fișierele **nu** sunt în Git: sunt derivate din datasetul APTOS 2019 și din ID-urile Orthanc ale instalării locale, deci diferă de la o mașină la alta. Sunt ignorate prin `.gitignore`.

Le generează `scripts/seed_orthanc_fl.py`, care convertește imaginile APTOS în DICOM, le distribuie non-IID în cele 3 Orthanc-uri și scrie aceste CSV-uri (vezi §6 din `README.md`).
