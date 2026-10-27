# Documentație

Ghidul de instalare și pornire este în [`README.md`](../README.md) de la rădăcină. Aici: o privire de ansamblu asupra arhitecturii și problemele cunoscute.

## Arhitectură

Soluția .NET 10 (`src/Sitim/`) are patru proiecte, cu dependențe strict pe straturi (Clean Architecture):

- **Sitim.Core** — entități de domeniu și interfețe de servicii; fără dependențe de infrastructură.
- **Sitim.Infrastructure** — implementează interfețele din Core: EF Core (`AppDbContext`, PostgreSQL), clientul HTTP Orthanc, inferența ONNX, stocarea modelelor în MinIO, orchestrarea Federated Learning.
- **Sitim.Api** — ASP.NET Core Web API: înregistrează DI, găzduiește dashboard-ul Hangfire, face seed-ul de roluri și super-admin la pornire.
- **Sitim.Web** — Blazor Server (componente Radzen); vorbește cu API-ul doar prin `SitimApiClient`.

### Multi-tenancy

Fiecare entitate care aparține unei instituții are `InstitutionId`. `AppDbContext` aplică filtre globale EF Core după `ITenantContext.InstitutionId`, luat din claim-ul JWT `institution_id` (`HttpContextTenantContext`). Când `InstitutionId` e `null` (SuperAdmin sau job-uri în fundal), filtrele nu se aplică.

Job-urile Hangfire (`AIAnalysisHangfireJob`, `FLSessionMonitorHangfireJob`) rulează în afara pipeline-ului HTTP, deci își creează propriul `IServiceScope` pentru serviciile dependente de tenant.

### Orthanc per instituție

Fiecare instituție are propriul `OrthancBaseUrl`. `OrthancClientFactory` creează un `OrthancClient` per instituție; nu există o instanță Orthanc comună.

### Inferență AI (embedded, ONNX Runtime)

`AIInferenceService`:

1. descarcă seria DICOM din Orthanc (`IOrthancClient`);
2. alege un `AIModel` compatibil (`IAIModelSelectorService`, după `TargetModality`);
3. descarcă artefactul ONNX din MinIO (`IModelStorageService`);
4. preprocesează pixelii DICOM și rulează inferența (`OnnxInferenceEngine`);
5. salvează rezultatul (`AIAnalysisJob`) în PostgreSQL.

Modelul de referință: ResNet-18 antrenat pe APTOS 2019, 5 clase (No DR / Mild / Moderate / Severe / Proliferative), intrare `[1, 3, 512, 512]`, normalizare ImageNet (mean `[0.485, 0.456, 0.406]`, std `[0.229, 0.224, 0.225]`).

### Federated Learning

`FLOrchestrationService` leagă aplicația de control plane-ul extern bazat pe Flower (`FederatedLearning/`):

- sesiunile, rundele, participanții și actualizările de model sunt în PostgreSQL (`FLSession`, `FLRound`, `FLParticipant`, `FLModelUpdate`);
- `FLSessionMonitorHangfireJob` rulează la `MonitorIntervalMinutes` (implicit 1 minut) și reconciliază starea cu API-ul REST al control plane-ului (`ControlPlaneBaseUrl`);
- o sesiune devine orfană dacă sesiunea externă dispare mai mult de `MissingExternalSessionGraceMinutes`.

## Configurare

| Cheie | Rol |
|-------|-----|
| `ConnectionStrings:Default` | PostgreSQL (obligatoriu; user-secrets) |
| `Jwt:SigningKey` | cheie HS256, minim 32 de caractere (obligatoriu; user-secrets) |
| `Seed:SuperAdminPassword` | parola contului super-admin creat la pornire (user-secrets) |
| `MinIO:Endpoint` / `AccessKey` / `SecretKey` | stocarea artefactelor ONNX (cheile în user-secrets) |
| `FederatedLearning:ControlPlaneBaseUrl` | URL-ul control plane-ului Flower (implicit `http://localhost:18081`) |
| `Orthanc:BaseUrl` | Orthanc implicit (URL-ul per instituție are prioritate) |
| `Smtp:*` | trimiterea invitațiilor (opțional; user-secrets) |

Secretele nu se pun în fișiere versionate; vezi §5.1 din `README.md`.

**CORS:** politica `"SitimWeb"` permite doar `http://localhost:5000` și `https://localhost:5001`. Pentru alt origin, actualizează `Sitim.Api/Program.cs`.

**Roluri:** `SuperAdmin` (toate instituțiile) și `InstitutionAdmin` (o singură instituție), create la pornire; verificate cu `[Authorize(Roles = "...")]`.

## Probleme cunoscute

### Inferența: „No valid DICOM frames could be extracted from study archive”

Apare când niciun frame din arhiva studiului nu poate fi extras. Cauze tipice:

- **transfer syntax encapsulat** (de exemplu JPEG Baseline) fără codec-uri native înregistrate în fo-dicom (pachetul `fo-dicom.Codecs` și `NativeTranscoderManager`);
- **imagini RGB tratate ca grayscale:** extracția trebuie să respecte `PhotometricInterpretation` (inclusiv YBR → RGB), altfel modelul primește R = G = B și prezice aproape mereu *No DR*. Randarea prin `DicomImage.RenderImage()` tratează corect aceste cazuri.

Pentru diagnostic: ridică temporar la `LogError` excepțiile prinse la deschiderea fișierelor DICOM și la extragerea frame-urilor în `AIInferenceService`, apoi compară predicția cu `scripts/test_onnx_model.py` pe aceeași imagine.
