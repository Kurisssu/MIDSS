# MIDSS — Ghid de instalare și pornire pe un PC nou

**MIDSS** (*Medical Imaging Diagnosis Support System*) — platformă web medicală multi-tenant pentru gestionarea, vizualizarea și analiza studiilor DICOM. Teză de licență, FCIM UTM, filiera francofonă.

> Proiect derivat din **SITIM** (*Système d'Information pour le Traitement des Images Médicales*), aplicația tezei de licență; MIDSS este numele platformei din articolul ICCP 2026. Repository-ul servește ca referință și bază de lucru pentru cursurile *Tehnici de proiectare a produselor software* și *Arhitectura sistemelor software*.

Ghidul acesta te duce de la **Windows proaspăt instalat** până la **aplicația funcțională în dev**, inclusiv modulele opționale (AI, Federated Learning).

> 📌 **Convenție de cale.** În exemple repository-ul este clonat în `D:\University\MIDSS`. Poți folosi orice cale — înlocuiește-o peste tot. Comenzile `dotnet` și `docker compose` din ghid se rulează din **rădăcina repository-ului**, dacă nu e indicat altfel.

---

## Cuprins

1. [Ce conține repository-ul](#1-ce-conține-repository-ul)
2. [Structura proiectului](#2-structura-proiectului)
3. [Prerechizite pe Windows-ul nou](#3-prerechizite-pe-windows-ul-nou)
4. [Pornire rapidă (TL;DR)](#4-pornire-rapidă-tldr)
5. [Pas cu pas: aplicația de bază](#5-pas-cu-pas-aplicația-de-bază)
6. [Populare cu date DICOM (opțional)](#6-populare-cu-date-dicom-opțional)
7. [Modulul AI — încărcarea modelului (opțional)](#7-modulul-ai--încărcarea-modelului-opțional)
8. [Federated Learning (opțional)](#8-federated-learning-opțional)
9. [Adrese, porturi, credențiale](#9-adrese-porturi-credențiale)
10. [Comenzi utile](#10-comenzi-utile)
11. [Troubleshooting](#11-troubleshooting)

---

## 1. Ce conține repository-ul

Tot proiectul stă într-un singur repository Git (monorepo): soluția .NET, infrastructura Docker, stack-ul Federated Learning, scripturile Python și documentația.

```powershell
git clone https://github.com/Kurisssu/MIDSS.git D:\University\MIDSS
```

### Ce NU este în Git (și de unde îl iei)

| Ce | Unde trebuie pus | Cum îl obții |
|----|------------------|--------------|
| Secretele (parole, chei JWT/MinIO) | user-secrets + fișiere `.env` | le setezi tu, vezi [§5.1](#51-clonează-repository-ul-și-setează-secretele) |
| Dataset APTOS 2019 (~9,7 GB) | `data\raw\` | Kaggle, vezi [`data/README.md`](data/README.md) |
| Modelele `.pth` / `.onnx` | `models\` | antrenare sau sesiune FL, vezi [`models/README.md`](models/README.md) |
| Etichetele FL `labels\*.csv` | `FederatedLearning\labels\` | generate de `scripts\seed_orthanc_fl.py` ([§6](#6-populare-cu-date-dicom-opțional)) |
| `scripts\venv\` | `scripts\venv\` | îl creezi în [§6](#6-populare-cu-date-dicom-opțional) |

**Datele din Docker** (DB Postgres, studiile din Orthanc, modelele din MinIO) trăiesc în volume Docker, nu în repository. Le recreezi cu migrațiile + seed.

---

## 2. Structura proiectului

```
D:\University\MIDSS\
├── src\Midss\            # Soluția .NET
│   ├── Midss.Core            # entități domeniu + interfețe servicii
│   ├── Midss.Infrastructure  # EF Core, Orthanc, ONNX, MinIO, orchestrare FL
│   ├── Midss.Api             # Web API + Hangfire + seeding
│   ├── Midss.Web             # Blazor Server UI (Radzen)
│   └── Midss.slnx            # fișierul de soluție
├── infra\                # 🐳 Docker: Postgres, Orthanc ×3, OHIF, MinIO, gateway (+ .env.example)
├── FederatedLearning\    # 🤝 Stack Flower (server + 3 clienți) (+ .env.example)
├── scripts\              # 🐍 Seed DICOM, training, test ONNX
├── models\               # 🎯 Artefacte AI — în Git doar README + training_report.json
├── data\                 # 📂 Dataset APTOS — în Git doar README
├── docs\                 # 📁 Documentație (arhitectură)
├── global.json           # fixează SDK-ul .NET 10
└── .config\dotnet-tools.json  # dotnet-ef local (dotnet tool restore)
```

**Arhitectură (Clean Architecture, layering strict):** `Core` (fără dependențe) → `Infrastructure` (implementează Core) → `Api` (DI + endpoints) / `Web` (UI, vorbește doar cu API-ul prin `MidssApiClient`).

---

## 3. Prerechizite pe Windows-ul nou

### 3.1 Software

| Software | Versiune | Note |
|----------|----------|------|
| **.NET SDK** | **10.0** | obligatoriu (`net10.0`). [Download](https://dotnet.microsoft.com/download) |
| **Docker Desktop** | recent | obligatoriu; include WSL2 |
| **Git** | recent | pentru clonare |
| **dotnet-ef** | 10.0.12 | local, din `.config/dotnet-tools.json` → `dotnet tool restore` (nu e nevoie de instalare globală) |
| **Python** | 3.11 | doar pentru seed DICOM / training |
| Visual Studio 2022 / Rider | opțional | totul merge și din terminal |

```powershell
# verificare (din rădăcina repository-ului)
dotnet --version    # trebuie să înceapă cu 10. (global.json cere 10.0.401 sau mai nou)
docker --version
dotnet tool restore
dotnet ef --version

# certificat HTTPS de dezvoltare (o singură dată)
dotnet dev-certs https --trust
```

### 3.2 ⚠️ Pregătire Windows — 2 setări critice

Ambele apar **exact pe un Windows proaspăt** și blochează proiectul. Rezolvă-le acum, nu când te blochezi.

#### a) Dezactivează Smart App Control

Smart App Control (activ implicit **doar pe instalări curate** de Windows 11) blochează DLL-urile nesemnate compilate local. Efect: **`dotnet ef` nu poate rula migrațiile**, cu eroarea:

```
Could not load assembly 'Midss.Infrastructure'.
System.IO.FileLoadException: An Application Control policy has blocked this file. (0x800711C7)
```

**Fix:** *Windows Security → App & browser control → Smart App Control settings → **Off***.

> ⚠️ Odată dezactivat, nu mai poate fi reactivat fără reinstalarea Windows-ului — de aceea decide-o **acum**, la început.

#### b) Rezervă porturile MIDSS (PowerShell ca Administrator)

Windows (Hyper-V/WSL, serviciul `winnat`) rezervă la **fiecare boot** un bloc **aleatoriu** de porturi TCP. Dacă blocul nimerește peste porturile MIDSS, containerele Orthanc/Adminer nu pornesc:

```
Error response from daemon: ports are not available ... bind: An attempt was made to
access a socket in a way forbidden by its access permissions.
```

…iar gateway-ul nginx cade în lanț (`host not found in upstream "orthanc-mci"`) → **erori 500 în OHIF**.

**Fix permanent** — deschide PowerShell **ca Administrator** și rulează:

```powershell
net stop winnat
netsh int ipv4 add excludedportrange protocol=tcp startport=8042 numberofports=3 store=persistent
netsh int ipv4 add excludedportrange protocol=tcp startport=8080 numberofports=1 store=persistent
net start winnat
```

Asta rezervă permanent 8042–8044 (Orthanc) și 8080 (Adminer) pentru tine; `winnat` le va ocoli la orice boot viitor.

```powershell
# verificare — intervalele excluse NU trebuie să acopere 8042-8044 / 8080
netsh int ipv4 show excludedportrange protocol=tcp
```

#### c) (Doar dacă folosești Federated Learning) memorie pentru Docker

Cei 3 clienți FL + serverul au nevoie de ~7 GB RAM. Implicit WSL2 ia ~50% din RAM-ul fizic, ceea ce pe 16 GB **nu ajunge** → clienții sunt uciși de OOM (`Exit 137`) în mijlocul antrenării.

Creează `C:\Users\<utilizator>\.wslconfig`:

```ini
[wsl2]
memory=10GB
swap=4GB
```

Apoi aplică: `wsl --shutdown`, repornește Docker Desktop.

---

## 4. Pornire rapidă (TL;DR)

Cu prerechizitele făcute, repository-ul clonat și **secretele setate** ([§5.1](#51-clonează-repository-ul-și-setează-secretele)):

```powershell
cd D:\University\MIDSS
dotnet tool restore

# 1. Infrastructura Docker (Postgres, Orthanc ×3, OHIF, MinIO, gateway) — citește infra\.env
docker compose -f infra\docker-compose.yml up -d

# 2. Schema bazei de date
dotnet ef database update --project src\Midss\Midss.Infrastructure --startup-project src\Midss\Midss.Api

# 3. API — terminal 1  (ATENȚIE la -lp https)
dotnet run --project src\Midss\Midss.Api -lp https

# 4. Web — terminal 2
dotnet run --project src\Midss\Midss.Web
```

Deschide **https://localhost:5001** → login `superadmin@midss.local` / parola pe care ai pus-o în `Seed:SuperAdminPassword`.

Apoi, obligatoriu o singură dată: creează instituțiile și **setează-le `OrthancBaseUrl` pe `localhost`** ([§5.6](#56-creează-instituțiile-și-leagă-le-de-orthanc)).

---

## 5. Pas cu pas: aplicația de bază

### 5.1 Clonează repository-ul și setează secretele

```powershell
git clone https://github.com/Kurisssu/MIDSS.git D:\University\MIDSS
cd D:\University\MIDSS
dotnet tool restore
```

Repository-ul nu conține parole sau chei: fișierele versionate au doar `CHANGE_ME...`. Valorile reale le alegi tu și le pui în locurile de mai jos, care **nu** sunt urmărite de Git.

#### a) Docker — `infra\.env`

```powershell
Copy-Item infra\.env.example infra\.env
notepad infra\.env      # completează POSTGRES_PASSWORD și MINIO_ROOT_PASSWORD (MinIO: minim 8 caractere)
```

> Postgres preia parola doar la **prima** creare a volumului `pgdata`. Dacă o schimbi ulterior, fie o schimbi și în DB, fie resetezi volumul (`docker compose -f infra\docker-compose.yml down -v`).

#### b) API — .NET user-secrets

`src\Midss\Midss.Api\appsettings.Development.json` conține doar `CHANGE_ME_VIA_USER_SECRETS`. Valorile reale stau în user-secrets (`%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json`, în afara repository-ului). Rulează din folderul proiectului API:

```powershell
cd src\Midss\Midss.Api

# aceeași parolă ca POSTGRES_PASSWORD din infra\.env
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=midss;Username=midss;Password=<POSTGRES_PASSWORD>"

# cheie HS256, minim 32 de caractere (generator mai jos)
dotnet user-secrets set "Jwt:SigningKey" "<cheie aleatorie de minim 32 de caractere>"

# parola contului superadmin@midss.local, creat la prima pornire (minim 8 caractere)
dotnet user-secrets set "Seed:SuperAdminPassword" "<parola super-admin>"

# MinIO: utilizatorul root din docker-compose.yml și MINIO_ROOT_PASSWORD din infra\.env
dotnet user-secrets set "MinIO:AccessKey" "midss"
dotnet user-secrets set "MinIO:SecretKey" "<MINIO_ROOT_PASSWORD>"

dotnet user-secrets list      # verificare
cd ..\..\..
```

Cheie JWT aleatorie (PowerShell):

```powershell
$b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

`ConnectionStrings:Default` din user-secrets e folosit și de `dotnet ef` (prin `AppDbContextFactory`), deci setează-l înainte de migrații. Alternativ, orice cheie se poate da prin variabile de mediu (`ConnectionStrings__Default`, `Jwt__SigningKey`, …).

#### c) Federated Learning — `FederatedLearning\.env` (doar dacă folosești FL)

```powershell
Copy-Item FederatedLearning\.env.example FederatedLearning\.env
```

Completează `FL_ORTHANC_USERNAME` / `FL_ORTHANC_PASSWORD` și GUID-urile instituțiilor ([§8.1](#81--sincronizează-guid-urile-instituțiilor)). Restul valorilor (`FL_BATCH_SIZE=4` etc.) sunt parametrii experimentului din articol.

#### d) SMTP (opțional) — vezi [§5.8](#58-opțional-smtp--necesar-pentru-invitarea-utilizatorilor).

### 5.2 Pornește infrastructura Docker

Asigură-te că **Docker Desktop rulează** și că există `infra\.env` ([§5.1a](#a-docker--infraenv)), apoi:

```powershell
cd D:\University\MIDSS
docker compose -f infra\docker-compose.yml up -d
docker compose -f infra\docker-compose.yml ps        # toate trebuie Up / healthy
```

| Container | Rol | Port host |
|-----------|-----|-----------|
| `midss-postgres` | PostgreSQL | 5432 |
| `midss-adminer` | UI administrare DB | 8080 |
| `midss-orthanc-mci` | PACS instituția MCI | 8042 · 4242 |
| `midss-orthanc-mikof` | PACS instituția Mikof | 8043 · 4243 |
| `midss-orthanc-ovisus` | PACS instituția Ovisus | 8044 · 4244 |
| `midss-ohif` | viewer OHIF | prin gateway |
| `midss-gateway` | reverse proxy Nginx | 3000 |
| `midss-minio` | stocare `.onnx` | 9000 · 9001 |

> `midss-minio-setup` creează bucket-ul `ai-models` și se oprește — e normal să-l vezi `Exited (0)`.

### 5.3 Aplică migrațiile

Schema **nu** se creează automat la pornirea API-ului:

```powershell
cd D:\University\MIDSS
dotnet ef database update --project src\Midss\Midss.Infrastructure --startup-project src\Midss\Midss.Api
```

> `dotnet ef` vine din manifestul local (`dotnet tool restore`). Connection string-ul e citit din user-secrets-ul API ([§5.1b](#b-api--net-user-secrets)).

> Dacă primești `An Application Control policy has blocked this file` → Smart App Control e activ, vezi [§3.2a](#a-dezactivează-smart-app-control).

### 5.4 Pornește API-ul

```powershell
dotnet run --project src\Midss\Midss.Api -lp https
```

> 🔴 **`-lp https` e obligatoriu.** Fără el, `dotnet run` folosește primul profil din `launchSettings.json` (`http`) și leagă **doar** `http://localhost:5080`. Dar UI-ul Blazor și gateway-ul OHIF apelează **`https://localhost:7006`** — fără acest flag primești erori de conexiune și viewer-ul nu încarcă imagini.

La pornire, API-ul: înregistrează fo-dicom pentru rendering DICOM, face **seed idempotent** (roluri + super-admin) și pornește Hangfire (inclusiv monitorul FL).

API: **https://localhost:7006** · Swagger: `/swagger` · Hangfire: `/hangfire`.

### 5.5 Pornește interfața Web

În **al doilea terminal**:

```powershell
cd D:\University\MIDSS
dotnet run --project src\Midss\Midss.Web
```

UI: **https://localhost:5001**. Loghează-te cu `superadmin@midss.local` / parola din `Seed:SuperAdminPassword` ([§5.1b](#b-api--net-user-secrets)).

### 5.6 Creează instituțiile și leagă-le de Orthanc

Arhitectura e multi-tenant: **fiecare instituție are propriul Orthanc**.

1. În UI → **Administrare instituții** → creează 3 instituții cu slug-urile `mci`, `mikof`, `ovisus`.
2. Setează-le `OrthancBaseUrl`. Deschide Adminer (http://localhost:8080 · sistem `PostgreSQL` · server `postgres` · user `midss` · parolă = `POSTGRES_PASSWORD` din `infra\.env` · db `midss`) și rulează:

```sql
UPDATE institutions SET "OrthancBaseUrl" = 'http://localhost:8042' WHERE "Slug" = 'mci';
UPDATE institutions SET "OrthancBaseUrl" = 'http://localhost:8043' WHERE "Slug" = 'mikof';
UPDATE institutions SET "OrthancBaseUrl" = 'http://localhost:8044' WHERE "Slug" = 'ovisus';
```

> 🔴 **Folosește `localhost:8042/8043/8044`, NU numele de containere** (`http://orthanc-mci:8042`). API-ul rulează **pe host** (`dotnet run`), nu în Docker, deci nu poate rezolva hostname-urile din rețeaua Docker. Simptom tipic: apeși „Sincronizare Orthanc" și **nu apare niciun studiu, fără nicio eroare** (excepția e înghițită tăcut per instituție).
>
> Dacă vreodată containerizezi API-ul în aceeași rețea Docker, atunci numele de containere devin varianta corectă.

3. Verifică din UI: **Studii → Sincronizare Orthanc**.

### 5.7 Verifică `Storage:BasePath`

În `src\Midss\Midss.Api\appsettings.json`:

```json
"Storage": { "BasePath": "D:\\University\\TEZA\\midss\\docs" }
```

E singura cale absolută din configurare (arhive DICOM locale). Dacă ai instalat proiectul în altă locație, **actualizeaz-o** (sau suprascrie prin user-secrets / variabilă de mediu) și asigură-te că folderul există. În versiunea actuală `StorageOptions` e doar înregistrat în DI și nu e citit de niciun serviciu, deci o cale greșită nu blochează pornirea.

### 5.8 (Opțional) SMTP — necesar pentru invitarea utilizatorilor

Fără SMTP configurat, crearea unui utilizator nou **eșuează și contul e șters automat** (rollback intenționat, ca să nu rămână conturi orfane). În UI vei vedea „Email indisponibil / Contul nou a fost retras automat".

Configurează cu un **Gmail App Password** (necesită 2FA activ pe cont):

```powershell
cd D:\University\MIDSS\src\Midss
dotnet user-secrets set "Smtp:Username"    "adresa_ta@gmail.com" --project Midss.Api
dotnet user-secrets set "Smtp:Password"    "aaaabbbbccccdddd"    --project Midss.Api
dotnet user-secrets set "Smtp:FromAddress" "adresa_ta@gmail.com" --project Midss.Api
```

⚠️ App Password-ul se afișează cu spații (`aaaa bbbb cccc dddd`) — introdu-l **fără spații**. `Username` și `FromAddress` trebuie să fie **aceeași adresă Gmail reală** (nu un nume ca „MIDSS"). Repornește API-ul după setare.

```powershell
dotnet user-secrets list --project Midss.Api      # verificare
```

---

## 6. Populare cu date DICOM (opțional)

Scriptul `scripts\seed_orthanc_fl.py` convertește imaginile fundus APTOS în DICOM și le distribuie **non-IID** (Dirichlet) între cele 3 Orthanc-uri, apoi scrie etichetele pentru FL.

**Necesită:** `data\raw\train.csv` + `data\raw\train_images\` (APTOS 2019, de pe Kaggle).

```powershell
# 1. venv nou (venv-ul nu e în Git și nu e portabil între mașini)
py -3.11 -m venv D:\University\MIDSS\scripts\venv

# 2. dependențe
& D:\University\MIDSS\scripts\venv\Scripts\python.exe -m pip install numpy pandas pydicom Pillow

# 3. rulează seed-ul (implicit: TOATE cele ~3662 imagini APTOS)
& D:\University\MIDSS\scripts\venv\Scripts\python.exe D:\University\MIDSS\scripts\seed_orthanc_fl.py
```

> 💡 Folosește **`py -3.11`**, nu `python` — pe un Windows curat `python` deschide alias-ul Microsoft Store, nu interpretorul real.

⚠️ Scriptul **șterge toate studiile existente** din cele 3 Orthanc-uri înainte de re-populare, și regenerează `FederatedLearning\labels\inst{1,2,3}-labels.csv`.

Parametrizare prin variabile de mediu (opțional):

| Variabilă | Implicit | Efect |
|-----------|---------|-------|
| `FL_SEED_MAX_PER_CLASS` | `0` | limită imagini/clasă; `0` = toate |
| `FL_NONIID_ALPHA` | `0.5` | α mic = puternic non-IID |
| `FL_SEED_UPLOAD_WORKERS` | `8` | paralelism upload |

După seed: în UI → **Studii → Sincronizare Orthanc**.

---

## 7. Modulul AI — încărcarea modelului (opțional)

Inferența rulează **embedded în procesul .NET** (ONNX Runtime), nu ca microserviciu. Model: ResNet-18 / APTOS 2019, 5 clase, input 512×512.

1. UI → **Modele AI → Încarcă Model Nou**, alege `models\retinopathy_pretrained_v1.onnx` (nu e în Git — vezi [`models/README.md`](models/README.md)) și completează:

   | Câmp | Valoare |
   |------|---------|
   | Tipul analizei | `Retinopatie Diabetică` |
   | Versiune | `v1.0` |
   | Număr clase | `5` |
   | Dimensiune input | `1,3,512,512` |

2. **Completează interpretarea claselor** (în același dialog). Fără ea, rezultatele apar cu severitate „unknown" și fără recomandări:

   | # | Nume clasă | Severitate | Recomandări |
   |:-:|-----------|-----------|-------------|
   | 0 | No DR | `none` | Control anual de rutină |
   | 1 | Mild DR | `mild` | Reexaminare la 6–12 luni |
   | 2 | Moderate DR | `moderate` | Consult oftalmologic în 3–6 luni |
   | 3 | Severe DR | `severe` | Trimitere urgentă la retinolog |
   | 4 | Proliferative DR | `proliferative` | Tratament urgent (laser/anti-VEGF) |

   > Severitățile trebuie scrise exact așa (`none`/`mild`/`moderate`/`severe`/`proliferative`) — după ele se colorează badge-urile din UI.

3. 🔴 **Setează modalitatea țintă:** dialogul de încărcare **nu** are acest câmp. După upload apasă **✏️ Editează** pe model și pune **Modalitate țintă = `OP`**. Fără ea, modelul nu apare în lista de modele compatibile pentru studiile fundus.

Poți edita oricând clasele/severitățile/recomandările cu același buton ✏️.

Pentru re-antrenare vezi [`scripts/QUICK_START.md`](scripts/QUICK_START.md) și [`scripts/TRAINING_GUIDE.md`](scripts/TRAINING_GUIDE.md).

---

## 8. Federated Learning (opțional)

Stack Flower separat: `fl-server` (control plane) + 3 clienți (unul per instituție). Ghid detaliat: [`FederatedLearning/RUNBOOK.md`](FederatedLearning/RUNBOOK.md).

**Precondiții:** cele 3 instituții există în DB, Orthanc-urile sunt populate ([§6](#6-populare-cu-date-dicom-opțional)), `.wslconfig` are ≥10 GB ([§3.2c](#c-doar-dacă-folosești-federated-learning-memorie-pentru-docker)).

### 8.1 🔴 Sincronizează GUID-urile instituțiilor

Clienții FL se leagă de instituții prin **GUID exact**. Instituțiile create pe noul PC au GUID-uri **noi**, deci valorile implicite din compose nu se mai potrivesc.

Simptom: contorul arată „Conectate: 3/3", dar fiecare instituție apare **„Client FL deconectat"** și nu poți porni sesiunea.

```sql
-- ia GUID-urile reale
SELECT "Slug", "Id" FROM institutions ORDER BY "Slug";
```

Pune-le în `FederatedLearning\.env` (creat din `.env.example`, [§5.1c](#c-federated-learning--federatedlearningenv-doar-dacă-folosești-fl)):

```ini
FL_INST1_INSTITUTION_ID=<GUID mci>
FL_INST2_INSTITUTION_ID=<GUID mikof>
FL_INST3_INSTITUTION_ID=<GUID ovisus>
```

(mapare după port: inst1→8042 `mci`, inst2→8043 `mikof`, inst3→8044 `ovisus`)

⚠️ După orice modificare în `.env`, containerele trebuie **recreate** — variabilele de mediu se aplică doar la creare, un simplu `restart` nu e suficient:

```powershell
docker compose -f docker-compose.fl.yml -f docker-compose.gpu.yml --env-file .env up -d --force-recreate
```

### 8.2 Pornește stack-ul FL

```powershell
cd D:\University\MIDSS\FederatedLearning

# CPU
docker compose -f docker-compose.fl.yml --env-file .env up -d --build

# GPU NVIDIA (recomandat — de ~10× mai rapid)
docker compose -f docker-compose.fl.yml -f docker-compose.gpu.yml --env-file .env up -d --build
```

Pentru GPU verifică întâi accesul Docker:

```powershell
docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi
```

> ⚠️ **Primul build descarcă ~2.5 GB** (PyTorch CUDA). Dacă build-ul e ucis cu `exit 137` (OOM), construiește **secvențial** — primul client populează cache-ul de layere, restul îl refolosesc:
>
> ```powershell
> docker compose -f docker-compose.fl.yml -f docker-compose.gpu.yml --env-file .env build fl-client-inst1
> docker compose -f docker-compose.fl.yml -f docker-compose.gpu.yml --env-file .env build fl-client-inst2 fl-client-inst3
> ```

> ⏱️ La pornire fiecare client descarcă **tot setul lui** din Orthanc (~1000+ imagini) — durează câteva minute, **fără output în log** până termină. E normal.

Verifică apoi:

```powershell
curl http://localhost:18081/clients      # toți 3 trebuie is_online: true
```

### 8.3 Reglaje importante în `.env`

| Variabilă | Recomandat | De ce |
|-----------|:---:|-------|
| `FL_BATCH_SIZE` | `4` | pe GPU 8 GB partajat de 3 clienți @512×512; peste 4 → risc `CUDA out of memory` |
| `FL_MIN_*_CLIENTS` | `3` | sesiunea cere toți 3 clienții; dacă unul cade, sesiunea se **blochează** |

### 8.4 Rulează o sesiune

UI → **Federated Learning → Sesiune nouă** → selectează cele 3 instituții → *Pornește sesiunea*.

Pagina de detalii afișează cele 4 metrici: **acuratețe** și **F1 macro** pe runde, **costul de comunicare** (total + mediu/client/rundă) și **distribuția claselor** pe instituții.

După finalizare poți **activa modelul FL** pentru inferență, direct din pagina sesiunii.

---

## 9. Adrese, porturi, credențiale

| Serviciu | URL |
|----------|-----|
| **Web UI (Blazor)** | https://localhost:5001 · http://localhost:5000 |
| **API** | https://localhost:7006 · http://localhost:5080 |
| Swagger | https://localhost:7006/swagger |
| Hangfire | https://localhost:7006/hangfire |
| Adminer (DB) | http://localhost:8080 |
| Gateway (OHIF + Orthanc) | http://localhost:3000 |
| OHIF viewer | http://localhost:3000/ohif/ |
| Orthanc (UI direct) | http://localhost:8042 · `:8043` · `:8044` |
| MinIO consolă | http://localhost:9001 |
| FL control plane | http://localhost:18081 (Flower gRPC: 18080) |

| Credențiale (dev) | User | Parola se setează în |
|------|-----------|--------|
| MIDSS super-admin | `superadmin@midss.local` | user-secrets API: `Seed:SuperAdminPassword` |
| PostgreSQL | `midss` | `infra\.env`: `POSTGRES_PASSWORD` (+ user-secrets: `ConnectionStrings:Default`) |
| MinIO | `midss` | `infra\.env`: `MINIO_ROOT_PASSWORD` (+ user-secrets: `MinIO:SecretKey`) |

Vezi [§5.1](#51-clonează-repository-ul-și-setează-secretele). Orthanc are autentificarea **dezactivată** în dev.

> CORS: API-ul acceptă doar `http://localhost:5000` și `https://localhost:5001`. Dacă schimbi portul UI-ului, actualizează politica `"MidssWeb"` din `Midss.Api/Program.cs`.

> ⚠️ **Porturi comune cu SITIM.** MIDSS folosește exact porturile proiectului SITIM din care derivă (5000/5001, 7006, 5432, 8042–8044, 3000, 9000/9001, 18080/18081). Pe aceeași mașină nu pot rula simultan: oprește întâi stack-ul SITIM (`docker compose -p sitim stop` și procesele `dotnet run`). Volumele Docker sunt separate (proiectul compose se numește `midss`), deci datele nu se amestecă.

---

## 10. Comenzi utile

```powershell
# --- Unelte locale (din rădăcina repository-ului, o dată per clonare) ---
dotnet tool restore

# --- Build ---
dotnet build src\Midss\Midss.slnx

# --- Migrații EF Core ---
dotnet ef database update       --project src\Midss\Midss.Infrastructure --startup-project src\Midss\Midss.Api
dotnet ef migrations add <Nume> --project src\Midss\Midss.Infrastructure --startup-project src\Midss\Midss.Api
dotnet ef database update <MigrațieAnterioară> --project src\Midss\Midss.Infrastructure --startup-project src\Midss\Midss.Api  # revert

# --- Rulare ---
dotnet run --project src\Midss\Midss.Api -lp https      # API pe 7006
dotnet run --project src\Midss\Midss.Web                # UI pe 5001

# --- Docker infra (din infra\) ---
docker compose up -d ; docker compose ps ; docker compose logs -f
docker compose down          # oprește, păstrează datele
docker compose down -v       # oprește ȘI șterge volumele (reset total)

# --- Reset complet al bazei de date ---
cd D:\University\MIDSS\infra
docker compose down -v ; docker compose up -d
cd D:\University\MIDSS
dotnet ef database update --project src\Midss\Midss.Infrastructure --startup-project src\Midss\Midss.Api
# apoi: recreează instituțiile (§5.6), re-rulează seed-ul (§6), re-încarcă modelul (§7)

# --- Federated Learning (din FederatedLearning\) ---
docker compose -f docker-compose.fl.yml -f docker-compose.gpu.yml --env-file .env up -d
docker compose -f docker-compose.fl.yml down
curl http://localhost:18081/clients
```

---

## 11. Troubleshooting

### Infrastructură / Docker

**Containere Orthanc/Adminer nu pornesc; OHIF dă erori 500**
`ports are not available ... forbidden by its access permissions` + `host not found in upstream "orthanc-mci"`.
→ Windows a rezervat porturile. Aplică fix-ul din [§3.2b](#b-rezervă-porturile-midss-powershell-ca-administrator). Verifică cu `netsh int ipv4 show excludedportrange protocol=tcp`.

**Gateway-ul (nginx) cade cu exit 1** → nginx rezolvă upstream-urile **la pornire**; dacă Orthanc-urile sunt jos, moare. Pornește întâi Orthanc-urile (`docker compose up -d` le ordonează corect), apoi repornește gateway-ul.

**Clienți FL uciși cu `Exit 137` / `OOMKilled=true`** → RAM insuficient în WSL2. Vezi [§3.2c](#c-doar-dacă-folosești-federated-learning-memorie-pentru-docker).

### Bază de date / migrații

**`Could not load assembly 'Midss.Infrastructure'` / `Application Control policy has blocked this file`** → Smart App Control, vezi [§3.2a](#a-dezactivează-smart-app-control).

**`dotnet ef` nu e recunoscut** → rulează `dotnet tool restore` din rădăcina repository-ului (unealta e în `.config/dotnet-tools.json`).

**`Missing ConnectionStrings:Default`** la `dotnet ef` sau la pornirea API-ului → user-secrets nesetate ([§5.1b](#b-api--net-user-secrets)).

**`Jwt:SigningKey must be at least 32 characters`** → `Jwt:SigningKey` nu e setat în user-secrets; în `appsettings.Development.json` e doar `CHANGE_ME_VIA_USER_SECRETS`.

**`password authentication failed` / conexiune DB refuzată** → Postgres nu e pornit sau nu e `healthy` (`docker compose ps`, `docker compose logs postgres`), sau parola din `ConnectionStrings:Default` diferă de `POSTGRES_PASSWORD` din `infra\.env`.

### Aplicație

**UI-ul nu poate apela API-ul (connection refused)** → API-ul rulează pe portul greșit. Folosește **`-lp https`** ([§5.4](#54-pornește-api-ul)) și verifică `Midss.Web/appsettings.json → Api:BaseUrl = https://localhost:7006`.

**Avertisment de certificat HTTPS** → `dotnet dev-certs https --trust` (o singură dată).

**„Sincronizare Orthanc" nu aduce niciun studiu, fără eroare** → `OrthancBaseUrl` din DB folosește hostname-uri Docker. Setează-le pe `localhost:8042/8043/8044` ([§5.6](#56-creează-instituțiile-și-leagă-le-de-orthanc)).

**Crearea unui utilizator eșuează / „Contul nou a fost retras automat"** → SMTP neconfigurat sau credențiale greșite ([§5.8](#58-opțional-smtp--necesar-pentru-invitarea-utilizatorilor)). În consola API vezi `SMTP not configured...` sau eroarea reală de la Gmail.

**OHIF: pagina se încarcă dar nu apare nicio imagine** → verifică în DevTools dacă bundle-urile `.js` vin cu `Content-Type: text/html` (semn că nginx servește fallback-ul). Ruta `/ohif/` din `infra/gateway/nginx.conf` trebuie să folosească `proxy_pass http://ohif:80;` **fără slash final** (imaginea OHIF servește aplicația sub `/ohif/`). Golește cache-ul cu `Ctrl+F5` și dezînregistrează service worker-ul.

### AI / Federated Learning

**Rezultatul analizei arată severitate „unknown" și fără recomandări** → modelul nu are metadatele de clase. Editează-l (✏️) și completează tabelul din [§7](#7-modulul-ai--încărcarea-modelului-opțional).

**Modelul nu apare în lista de modele compatibile pentru un studiu** → lipsește **Modalitate țintă = `OP`** pe model ([§7](#7-modulul-ai--încărcarea-modelului-opțional), pasul 3).

**Inferența dă „No valid DICOM frames…"** → transfer syntax encapsulat fără codec, sau DICOM non-RGB. Vezi [`docs/README.md`](docs/README.md#probleme-cunoscute).

**FL: instituțiile apar „Client FL deconectat" deși sunt 3/3 conectate** → GUID-uri nesincronizate ([§8.1](#81--sincronizează-guid-urile-instituțiilor)).

**FL: „Missing online FL clients"** → clienții încă își încarcă datasetul (câteva minute) sau GUID-urile nu corespund. Verifică `curl http://localhost:18081/clients`.

**Sesiunea FL se blochează la o rundă; apoi `External FL session ... not found after grace window`** → un client a căzut (de obicei OOM). Sesiunea cere toți 3 clienții și nu are `round_timeout`, deci se blochează. Verifică `docker inspect <client> --format '{{.State.OOMKilled}}'`, scade `FL_BATCH_SIZE`, mărește RAM-ul WSL, apoi pornește o sesiune nouă.

**CUDA out of memory în timpul antrenării** → scade `FL_BATCH_SIZE` în `.env` (4 e valoarea sigură pe 8 GB VRAM cu 3 clienți) și recreează containerele.

---

*Detalii arhitecturale (multi-tenancy, query filters, JWT, roluri, configurare secrete): [`docs/README.md`](docs/README.md).
Ghid FL end-to-end: [`FederatedLearning/RUNBOOK.md`](FederatedLearning/RUNBOOK.md).
Multi-Orthanc: [`infra/README_MULTI_ORTHANC.md`](infra/README_MULTI_ORTHANC.md).*
