# Multi-Orthanc Architecture - Startup Guide

## 📋 Pregătire

Asigură-te că Docker Desktop este pornit.

## 🚀 Pornire containere

Navighează în folderul infra și pornește toate containerele:

```powershell
cd D:\University\SITIM\infra
docker-compose down -v  # Oprește și șterge volumele vechi (dacă există)
docker-compose up -d
```

## ✅ Verificare containere

Verifică că toate cele 7 containere rulează:

```powershell
docker ps
```

Ar trebui să vezi:
- **sitim-postgres** (PostgreSQL database)
- **sitim-adminer** (DB admin interface)
- **sitim-orthanc-mci** (MCI PACS server)
- **sitim-orthanc-mikof** (Mikof PACS server)
- **sitim-orthanc-ovisus** (Ovisus PACS server)
- **sitim-ohif** (OHIF viewer)
- **sitim-gateway** (Nginx reverse proxy)

## 🔧 Actualizare URL-uri instituții

După ce containerele pornesc, actualizează URL-urile Orthanc din baza de date.

### Opțiunea 1: Via Adminer (UI)

1. Deschide Adminer: http://localhost:8080
2. Login cu:
   - System: **PostgreSQL**
   - Server: **postgres**
   - Username: **sitim**
   - Password: valoarea `POSTGRES_PASSWORD` din `infra/.env` (vezi `infra/.env.example`)
   - Database: **sitim**
3. Deschide SQL command și rulează:

> 🔴 **Alege URL-urile după locul unde rulează API-ul SITIM:**
> - **API pe host** (`dotnet run` — cazul normal în dev) → foloseşte `localhost` + portul publicat (mai jos).
>   Numele de containere (`orthanc-mci`) **nu se rezolvă** de pe host: sincronizarea nu aduce niciun studiu,
>   iar eroarea este înghiţită tăcut per instituţie (nu vezi niciun mesaj în UI).
> - **API containerizat** în aceeaşi reţea Docker → foloseşte numele de servicii (varianta comentată).

```sql
-- API rulează pe host (dev standard)
UPDATE institutions SET "OrthancBaseUrl" = 'http://localhost:8042' WHERE "Slug" = 'mci';
UPDATE institutions SET "OrthancBaseUrl" = 'http://localhost:8043' WHERE "Slug" = 'mikof';
UPDATE institutions SET "OrthancBaseUrl" = 'http://localhost:8044' WHERE "Slug" = 'ovisus';

-- Doar dacă API-ul rulează ÎN Docker, pe reţeaua compose:
-- UPDATE institutions SET "OrthancBaseUrl" = 'http://orthanc-mci:8042'    WHERE "Slug" = 'mci';
-- UPDATE institutions SET "OrthancBaseUrl" = 'http://orthanc-mikof:8042'  WHERE "Slug" = 'mikof';
-- UPDATE institutions SET "OrthancBaseUrl" = 'http://orthanc-ovisus:8042' WHERE "Slug" = 'ovisus';
```

### Opțiunea 2: Via psql (CLI)

```powershell
docker exec -it sitim-postgres psql -U sitim -d sitim
```

Apoi rulează SQL-ul de mai sus.

## 🧪 Testare accese

### Orthanc UIs (pentru debugging manual)
- MCI: http://localhost:3000/orthanc-mci/
- Mikof: http://localhost:3000/orthanc-mikof/
- Ovisus: http://localhost:3000/orthanc-ovisus/

### OHIF Viewer
- http://localhost:3000/ohif/

### Gateway Status
- http://localhost:3000/

## 📊 Arhitectură

```
┌─────────────────────────────────────────────────────┐
│              Nginx Gateway (:3000)                  │
├─────────────────────────────────────────────────────┤
│  /ohif/              → OHIF Viewer                  │
│  /orthanc-mci/       → Orthanc MCI                  │
│  /orthanc-mikof/     → Orthanc Mikof                │
│  /orthanc-ovisus/    → Orthanc Ovisus               │
│  /sitim-dicomweb/    → SITIM API (secure proxy)     │
└─────────────────────────────────────────────────────┘
            ↓              ↓              ↓
    ┌──────────────┬──────────────┬──────────────┐
    │ orthanc-mci  │orthanc-mikof │orthanc-ovisus│
    │   :8042      │    :8042     │    :8042     │
    │  (port 8042) │  (port 8043) │  (port 8044) │
    └──────────────┴──────────────┴──────────────┘
            ↓              ↓              ↓
         [mci_db]      [mikof_db]    [ovisus_db]
       (izolat)        (izolat)      (izolat)
```

## 🛑 Oprire containere

```powershell
docker-compose down
```

Pentru a șterge și volumele (date):
```powershell
docker-compose down -v
```

## 📝 Note importante

1. **Izolare fizică**: Fiecare instituție are propriul server Orthanc și storage separat
2. **Ports externe**: 
   - MCI: 8042 (HTTP), 4242 (DICOM)
   - Mikof: 8043 (HTTP), 4243 (DICOM)
   - Ovisus: 8044 (HTTP), 4244 (DICOM)
3. **Comunicare internă**: Containerele folosesc numele serviciului (ex: `orthanc-mci:8042`)
4. **SITIM API**: Trebuie rulat separat din Visual Studio (nu este în Docker)
