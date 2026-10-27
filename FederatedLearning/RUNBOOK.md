# MIDSS Federated Learning Runbook (E2E)

> **Model:** ResNet-18 antrenat federat (fine-tuning ImageNet), 5 clase APTOS, 512×512,
> aceeași arhitectură ca modelul de inferință. Export ONNX cu **logits** (softmax aplicat o
> singură dată pe partea .NET). Schimb de ponderi: tot `state_dict`-ul ResNet-18 prin Flower
> (FedAvg). BatchNorm: agregat ca orice parametru (varianta simplă; FedBN = direcție viitoare).

## 1. Prerequisites
- 3 instituții active în MIDSS (cu `InstitutionId` GUID real).
- Orthanc per instituție disponibil și accesibil din containerele FL.
- Dataset APTOS 2019 local pentru seeding: `data/raw/train.csv` + `data/raw/train_images/*.png`.
- Labels CSV populate per instituție (regenerate de seeder — vezi §1.1):
  - `labels/inst1-labels.csv`, `labels/inst2-labels.csv`, `labels/inst3-labels.csv`
- Fiecare CSV are header `orthanc_study_id,label`, cu `label` pe **5 clase (0..4)**.

## 1.1 Seed Orthanc cu partiție non-IID 5-class (înlocuiește orice seed binar vechi)
Sursa de antrenare este DOAR Orthanc. Partiția non-IID între instituții se materializează
fizic la seeding (Dirichlet pe etichete). Rulează:
```powershell
# opțional, parametrizează partiția:
$env:FL_NONIID_ALPHA = "0.5"     # mic ⇒ puternic non-IID; mare ⇒ aproape IID
$env:FL_SEED_MAX_PER_CLASS = "40" # cap/clasă (CPU 512×512 e lent)
python D:\University\MIDSS\scripts\seed_orthanc_fl.py
```
Seederul: curăță Orthanc-urile, alege imagini din toate cele 5 clase, le împarte non-IID pe
cele 3 instituții (fără overlap), reîncarcă DICOM-uri RGB 512×512, regenerează CSV-urile și
**loghează histograma pe clase per instituție** (dovada non-IID pentru articol).

## 2. Configure environment
Setează în shell variabilele reale înainte de pornire:
- `FL_INST1_INSTITUTION_ID`, `FL_INST2_INSTITUTION_ID`, `FL_INST3_INSTITUTION_ID`
- `FL_INST1_ORTHANC_BASE_URL`, `FL_INST2_ORTHANC_BASE_URL`, `FL_INST3_ORTHANC_BASE_URL`

Optional:
- `FL_SERVER_PUBLISHED_PORT` (default `18080`)
- `FL_CONTROL_API_PUBLISHED_PORT` (default `18081`)

## 3. Start FL runtime
Imaginile includ acum PyTorch + torchvision (mult mai mari; primul build durează). Contextul de
build este rădăcina `FederatedLearning/` (ca modulul comun `fl_common/` să ajungă în ambele imagini).
```powershell
docker compose -f D:\University\MIDSS\FederatedLearning\docker-compose.fl.yml up --build
```

Variabile noi / modificate (vezi `docker-compose.fl.yml`):
- `FL_PREPROCESSING_IMAGE_SIZE=512` (era 64) — aliniat cu inferența.
- `FL_AUGMENT=true|false` — augmentări ușoare DOAR pe train (flip/rotație/jitter).
- `FL_BATCH_SIZE=8`, `FL_LOCAL_EPOCHS=2` — ajustate pentru 512×512 (memorie/timp CPU).
- `FL_LEARNING_RATE=0.01` — SGD(momentum=0.9) pentru fine-tuning ResNet.
- `FL_ONNX_OPSET=17` (server) — compatibil cu `Microsoft.ML.OnnxRuntime` din .NET.
- `FL_DATALOADER_WORKERS=0` — crește dacă mediul permite.

### 3.1 Rulare pe GPU NVIDIA (opțional, recomandat)
Codul folosește automat GPU dacă e disponibil. Implicit imaginile se construiesc cu torch CPU;
pentru GPU adaugă fișierul de override `docker-compose.gpu.yml` (instalează torch CUDA cu12.1 și
atașează GPU-ul la clienți; serverul rămâne pe CPU — face doar export ONNX):

```powershell
# (o singură dată) verifică accesul Docker la GPU:
docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi

# pornire pe GPU:
docker compose -f D:\University\MIDSS\FederatedLearning\docker-compose.fl.yml `
               -f D:\University\MIDSS\FederatedLearning\docker-compose.gpu.yml up --build
```

Necesită driver NVIDIA + NVIDIA Container Toolkit (runtime `nvidia` în Docker).

**VRAM partajat:** în FedAvg cei 3 clienți se antrenează simultan per rundă pe ACELAȘI GPU. Pe
8 GB (RTX 4060 Laptop) ține batch-ul mic ca să eviți CUDA OOM:
```powershell
$env:FL_BATCH_SIZE = "6"   # trimis clienților prin fit_config; scade la 4 dacă tot dă OOM
```
Confirmă că rulează pe GPU în loguri: `nvidia-smi` arată 3 procese python, iar containerele NU
loghează `device=cpu`.

## 4. Start MIDSS API/Web
Pornește aplicația MIDSS normal (API + Web).  
API rulează monitorizare FL automată prin Hangfire (`fl-session-monitor`).

## 5. Start and complete session from UI
1. Mergi la `Federated Learning`.
2. Verifică lista `Clienți FL conectați`.
3. Creează sesiune FL nouă cu instituțiile online.
4. Urmărește progresul în `Detalii sesiune FL`.
5. După `Completed`, verifică:
   - `Model output` non-empty
   - card `Model publicat în registry`

## 6. Activate FL model for inference
În `Detalii sesiune FL`, apasă `Activează modelul FL`.  
Acțiunea:
- activează modelul publicat pentru task-ul sesiunii;
- dezactivează celelalte modele active pe același task.

## 7. Validate inference bridge
1. Deschide un studiu compatibil.
2. Deschide dialogul de selectare model (`models-for-study`).
3. Confirmă că modelul FL activ este listat și selectabil.
4. Rulează analiza AI și verifică rezultat `Completed`.

## 8. Operational checks
- API endpoint clienți FL:
  - `GET /api/fl/clients`
- Sesiuni FL:
  - `GET /api/fl/sessions`
  - `GET /api/fl/sessions/{id}`
- Model publicat per sesiune:
  - `GET /api/fl/sessions/{id}/published-model`
- Activare model:
  - `POST /api/fl/sessions/{id}/activate-model`

## 9. Troubleshooting
- **Missing online FL clients**
  - verifică `FL_INSTITUTION_ID` (GUID corect) și heartbeat către control-plane.
- **Session fails with missing studies/labels**
  - verifică CSV-ul local și existența study-urilor în Orthanc.
- **Artifact mismatch (hash/size)**
  - rerulează sesiunea; verifică stabilitatea rețelei între API și control-plane.
- **Session stuck running**
  - monitorul Hangfire actualizează periodic statusul;
  - după timeout, sesiunea se marchează `Failed` automat.
