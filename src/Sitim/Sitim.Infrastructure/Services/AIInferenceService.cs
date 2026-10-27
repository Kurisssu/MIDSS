using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Sitim.Core.Entities;
using Sitim.Core.Services;
using Sitim.Infrastructure.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using FellowOakDicom;
using System.IO.Compression;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;

namespace Sitim.Infrastructure.Services;

public class AIInferenceService : IAIInferenceService
{
    private const int MaxDicomInstancesToInspect = 12;
    private const int MaxFramesPerInstance = 3;
    private const int MaxTotalFramesForInference = 8;

    private readonly AppDbContext _context;
    private readonly IInferenceEngine _inferenceEngine;
    private readonly IModelStorageService _modelStorage;
    private readonly IOrthancClientFactory _orthancFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AIInferenceService> _logger;

    public AIInferenceService(
        AppDbContext context,
        IInferenceEngine inferenceEngine,
        IModelStorageService modelStorage,
        IOrthancClientFactory orthancFactory,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AIInferenceService> logger)
    {
        _context = context;
        _inferenceEngine = inferenceEngine;
        _modelStorage = modelStorage;
        _orthancFactory = orthancFactory;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<AIAnalysisJob> AnalyzeStudyAsync(
        Guid studyId,
        Guid? modelId = null,
        CancellationToken cancellationToken = default)
    {
        // Doctor must explicitly select model
        // No auto-selection fallback allowed (violates doctor-driven design)
        if (!modelId.HasValue)
            throw new InvalidOperationException(
                "Model selection is required.");

        var sw = Stopwatch.StartNew();

        // Get current user ID from claims
        var userIdClaim = _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
            throw new InvalidOperationException("User not authenticated");

        // Get user name for audit trail
        var userNameClaim = _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.Name);
        var userName = userNameClaim?.Value ?? "Unknown";

        // Get study metadata
        var study = await _context.ImagingStudies
            .IgnoreQueryFilters() // Allow SuperAdmin to access any study
            .FirstOrDefaultAsync(s => s.Id == studyId, cancellationToken);

        if (study == null)
            throw new InvalidOperationException("Study not found");

        if (!study.InstitutionId.HasValue)
            throw new InvalidOperationException("Study has no institution");

        _logger.LogInformation(
            "Starting AI analysis for study {StudyId} from institution {InstitutionId}",
            studyId, study.InstitutionId);

        // Get AI model (modelId is guaranteed non-null by check above)
        var model = await _context.AIModels.FindAsync(modelId.Value)
            ?? throw new InvalidOperationException("Model not found");

        _logger.LogInformation("Using model: {ModelName} (v{Version})", model.Name, model.Version);

        // Use database metadata for logging
        // Get class names from model for audit trail
        var classNamesJson = model.ClassNames ?? "[]";
        var classNames = JsonSerializer.Deserialize<string[]>(classNamesJson) ?? Array.Empty<string>();
        var predictionClassName = classNames.Length > 0 ? string.Join(", ", classNames) : "Unknown";


        var modelBytes = await DownloadModelBytesAsync(model.StorageFileName, cancellationToken);
        var (predictionClass, confidence, probabilities, framesUsed) = await RunStudyInferenceAsync(
            study.InstitutionId.Value,
            study.OrthancStudyId,
            model,
            modelBytes,
            cancellationToken);

        sw.Stop();

        // Save result to database
        var result = new AIAnalysisJob
        {
            Id = Guid.NewGuid(),
            StudyId = studyId,
            ModelId = model.Id,
            PredictionClass = predictionClass,
            Confidence = confidence,
            Probabilities = JsonSerializer.Serialize(probabilities),
            ProcessingTimeMs = (int)sw.ElapsedMilliseconds,
            PerformedByUserId = userId,
            PerformedByUserName = userName,
            CreatedAtUtc = DateTime.UtcNow,
            FinishedAtUtc = DateTime.UtcNow,
            Status = "Completed"
        };

        _context.AIAnalysisJobs.Add(result);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "AI analysis completed for study {StudyId}. Prediction class: {ClassIndex} ({Confidence:P2}), Frames used: {FramesUsed}, Time: {Time}ms. Available classes: {ClassNames}",
            studyId, predictionClass, confidence, framesUsed, sw.ElapsedMilliseconds, predictionClassName);

        return result;
    }

    public async Task<List<AIAnalysisJob>> GetStudyAnalysisHistoryAsync(Guid studyId)
    {
        return await _context.AIAnalysisJobs
            .Where(r => r.StudyId == studyId)
            .Include(r => r.Model)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToListAsync();
    }

    /// <summary>
    /// Execute an AI analysis job (called from Hangfire background worker).
    /// Loads the job record, runs inference, and updates with results/status.
    /// </summary>
    public async Task ExecuteAnalysisJobAsync(Guid analysisJobId, CancellationToken cancellationToken)
    {
        try
        {
            // Load the job record
            var job = await _context.AIAnalysisJobs
                .Include(j => j.Study)
                .Include(j => j.Model)
                .FirstOrDefaultAsync(j => j.Id == analysisJobId, cancellationToken);

            if (job == null)
            {
                _logger.LogError("AI analysis job not found: {JobId}", analysisJobId);
                throw new InvalidOperationException($"Analysis job not found: {analysisJobId}");
            }

            _logger.LogInformation(
                "Executing AI analysis job {JobId} for study {StudyId} with model {ModelId}",
                analysisJobId, job.StudyId, job.ModelId);

            // Update status to Running
            job.Status = "Running";
            job.StartedAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            var sw = Stopwatch.StartNew();

            // Get the study
            var study = job.Study ?? await _context.ImagingStudies
                .FirstOrDefaultAsync(s => s.Id == job.StudyId, cancellationToken);
            if (study == null)
                throw new InvalidOperationException($"Study not found: {job.StudyId}");

            if (!study.InstitutionId.HasValue)
                throw new InvalidOperationException($"Study {job.StudyId} has no institution");

            // Get the model
            var model = job.Model ?? await _context.AIModels.FindAsync(new object[] { job.ModelId }, cancellationToken);
            if (model == null)
                throw new InvalidOperationException($"Model {job.ModelId} not found");

            // Use database metadata for logging
            var classNamesJson = model.ClassNames ?? "[]";
            var execClassNames = JsonSerializer.Deserialize<string[]>(classNamesJson) ?? Array.Empty<string>();
            var execPredictionClassName = execClassNames.Length > 0 ? string.Join(", ", execClassNames) : "Unknown";

            var modelBytes = await DownloadModelBytesAsync(model.StorageFileName, cancellationToken);
            var (predictionClass, confidence, probabilities, framesUsed) = await RunStudyInferenceAsync(
                study.InstitutionId.Value,
                study.OrthancStudyId,
                model,
                modelBytes,
                cancellationToken);

            sw.Stop();

            // Update job with results
            job.Status = "Completed";
            job.PredictionClass = predictionClass;
            job.Confidence = confidence;
            job.Probabilities = JsonSerializer.Serialize(probabilities);
            job.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            job.FinishedAtUtc = DateTime.UtcNow;
            job.ErrorMessage = null;

            _logger.LogInformation(
                "AI analysis completed for job {JobId}. Prediction class: {ClassIndex} ({Confidence:P2}), Frames used: {FramesUsed}, Time: {Time}ms. Available classes: {ClassNames}",
                analysisJobId, predictionClass, confidence, framesUsed, sw.ElapsedMilliseconds, execPredictionClassName);

            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing AI analysis job {JobId}", analysisJobId);

            // Update job with error status
            var job = await _context.AIAnalysisJobs.FirstOrDefaultAsync(
                j => j.Id == analysisJobId, cancellationToken);
            if (job != null)
            {
                job.Status = "Failed";
                job.ErrorMessage = ex.Message;
                job.FinishedAtUtc = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
            }

            throw;
        }
    }

    private async Task<byte[]> DownloadModelBytesAsync(string storageFileName, CancellationToken cancellationToken)
    {
        using var modelStream = await _modelStorage.DownloadModelAsync(storageFileName, cancellationToken);
        using var buffer = new MemoryStream();
        await modelStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private async Task<(int predictionClass, decimal confidence, float[] probabilities, int framesUsed)> RunStudyInferenceAsync(
        Guid institutionId,
        string? orthancStudyId,
        AIModel model,
        byte[] modelBytes,
        CancellationToken cancellationToken)
    {
        var selectedFrames = await ExtractRepresentativeFramesFromStudyAsync(
            institutionId,
            orthancStudyId,
            cancellationToken);

        /*// DEBUG: Save extracted frames to disk for visual inspection
        var debugDir = Path.Combine(Path.GetTempPath(), "sitim-debug-frames");
        Directory.CreateDirectory(debugDir);
        for (int i = 0; i < selectedFrames.Count; i++)
        {
            var path = Path.Combine(debugDir, $"frame_{Guid.NewGuid():N}_{i}.png");
            await File.WriteAllBytesAsync(path, selectedFrames[i], cancellationToken);
            _logger.LogWarning("DEBUG frame saved: {Path}", path);
        }*/

        var outputs = new List<InferenceOutput>(selectedFrames.Count);
        foreach (var frameBytes in selectedFrames)
        {
            var input = PreprocessImage(frameBytes, model);
            await using var modelStream = new MemoryStream(modelBytes, writable: false);
            var output = await _inferenceEngine.RunAsync(modelStream, input, model, cancellationToken);
            outputs.Add(output);
        }

        if (outputs.Count == 0)
            throw new InvalidOperationException("No inference outputs were generated from selected DICOM frames.");

        var classCount = outputs[0].Probabilities.Length;
        if (outputs.Any(o => o.Probabilities.Length != classCount))
            throw new InvalidOperationException($"Inconsistent output dimensions across selected frames for model '{model.Name}'.");

        var averagedProbabilities = new float[classCount];
        foreach (var output in outputs)
        {
            for (var index = 0; index < classCount; index++)
                averagedProbabilities[index] += output.Probabilities[index];
        }

        for (var index = 0; index < classCount; index++)
            averagedProbabilities[index] /= outputs.Count;

        var predictionClass = Array.IndexOf(averagedProbabilities, averagedProbabilities.Max());
        var confidence = (decimal)averagedProbabilities[predictionClass];

        return (predictionClass, confidence, averagedProbabilities, outputs.Count);
    }

    private async Task<List<byte[]>> ExtractRepresentativeFramesFromStudyAsync(
        Guid institutionId,
        string? orthancStudyId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(orthancStudyId))
            throw new InvalidOperationException("Study has no OrthancStudyId");

        var orthancClient = await _orthancFactory.CreateClientAsync(institutionId);

        try
        {
            // Get study details from Orthanc using its internal ID
            var studyDetails = await orthancClient.GetStudyAsync(orthancStudyId, cancellationToken);
            if (studyDetails == null)
                throw new InvalidOperationException($"Study {orthancStudyId} not found in Orthanc");

            // Download study archive (ZIP) from Orthanc
            _logger.LogInformation("Downloading study archive for {OrthancStudyId}", orthancStudyId);
            using var archiveStream = new MemoryStream();
            await orthancClient.DownloadStudyArchiveAsync(orthancStudyId, archiveStream, cancellationToken);
            archiveStream.Position = 0;

            var selectedFrames = ExtractRepresentativeFramesFromArchive(archiveStream);
            if (selectedFrames.Count == 0)
                throw new InvalidOperationException("No valid DICOM frames could be extracted from study archive.");

            _logger.LogInformation(
                "Selected {FrameCount} representative frames from study {OrthancStudyId} for inference.",
                selectedFrames.Count,
                orthancStudyId);

            return selectedFrames;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract representative frames from study {OrthancStudyId}", orthancStudyId);
            throw;
        }
    }

    private List<byte[]> ExtractRepresentativeFramesFromArchive(MemoryStream archiveStream)
    {
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

        var allEntries = archive.Entries.ToList();
        _logger.LogInformation(
            "Archive contains {TotalEntries} entries: {EntryNames}",
            allEntries.Count,
            string.Join("; ", allEntries.Take(30).Select(e => e.FullName)));

        // Orthanc stores DICOM instances without file extension (e.g. StudyUID/SeriesUID/InstanceUID).
        // Accept both .dcm files and extension-less entries so both Orthanc and third-party archives work.
        var dicomEntries = allEntries
            .Where(e => e.Name.EndsWith(".dcm", StringComparison.OrdinalIgnoreCase)
                     || !Path.HasExtension(e.Name))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDicomInstancesToInspect)
            .ToList();

        if (dicomEntries.Count == 0)
        {
            _logger.LogError(
                "No DICOM entries found in archive. {TotalEntries} total entries with names: {EntryNames}",
                allEntries.Count,
                string.Join("; ", allEntries.Select(e => e.FullName)));
            return [];
        }

        _logger.LogInformation("Selected {Count} candidate entries for DICOM parsing.", dicomEntries.Count);

        var candidates = new List<FrameCandidate>();
        foreach (var dicomEntry in dicomEntries)
        {
            using var entryStream = dicomEntry.Open();
            using var memStream = new MemoryStream();
            entryStream.CopyTo(memStream);
            memStream.Position = 0;

            DicomFile? dicomFile = null;
            try
            {
                dicomFile = DicomFile.Open(memStream);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Skipping invalid DICOM entry {EntryName}", dicomEntry.FullName);
            }

            if (dicomFile is null)
                continue;

            DicomPixelData pixelData;
            try
            {
                pixelData = DicomPixelData.Create(dicomFile.Dataset);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DicomPixelData.Create failed for entry {EntryName}, skipping.", dicomEntry.FullName);
                continue;
            }

            var numberOfFrames = Math.Max(1, pixelData.NumberOfFrames);
            foreach (var frameIndex in SelectFrameIndices(numberOfFrames))
            {
                byte[] imageBytes;
                try
                {
                    imageBytes = ExtractPixelDataFromDicomFrame(dicomFile, frameIndex);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Skipping frame {FrameIndex} from entry {EntryName} due to extraction error.",
                        frameIndex,
                        dicomEntry.FullName);
                    continue;
                }

                if (imageBytes.Length == 0)
                    continue;

                var score = ScoreFrameQuality(imageBytes);
                candidates.Add(new FrameCandidate(imageBytes, score));
            }
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .Take(MaxTotalFramesForInference)
            .Select(c => c.ImageBytes)
            .ToList();
    }

    private static IEnumerable<int> SelectFrameIndices(int frameCount)
    {
        if (frameCount <= 0)
            yield break;

        if (frameCount == 1)
        {
            yield return 0;
            yield break;
        }

        var indices = new HashSet<int> { 0, frameCount - 1 };
        if (frameCount > 2)
            indices.Add(frameCount / 2);

        foreach (var index in indices.OrderBy(i => i).Take(MaxFramesPerInstance))
            yield return index;
    }

    private double ScoreFrameQuality(byte[] imageBytes)
    {
        using var ms = new MemoryStream(imageBytes);
        using var image = Image.Load<Rgb24>(ms);

        var stepX = Math.Max(1, image.Width / 128);
        var stepY = Math.Max(1, image.Height / 128);
        var values = new List<double>((image.Width / stepX + 1) * (image.Height / stepY + 1));

        for (var y = 0; y < image.Height; y += stepY)
        {
            for (var x = 0; x < image.Width; x += stepX)
            {
                var pixel = image[x, y];
                var luminance = (0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B);
                values.Add(luminance);
            }
        }

        if (values.Count == 0)
            return 0;

        var mean = values.Average();
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
        var nonBlackRatio = values.Count(v => v > 8) / (double)values.Count;

        return variance * nonBlackRatio;
    }

    private byte[] ExtractPixelDataFromDicomFrame(DicomFile dicomFile, int frameIndex)
    {
        var dataset = dicomFile.Dataset;

        _logger.LogInformation(
            "Extracting frame {FrameIndex}: TransferSyntax={TransferSyntaxName} (UID={TransferSyntaxUid}), IsEncapsulated={IsEncapsulated}",
            frameIndex,
            dataset.InternalTransferSyntax.UID.Name,
            dataset.InternalTransferSyntax.UID.UID,
            dataset.InternalTransferSyntax.IsEncapsulated);

        // Decompress if needed (handles JPEG, JPEG-LS, JPEG2000 transfer syntaxes)
        if (dataset.InternalTransferSyntax.IsEncapsulated)
        {
            var transcoder = new DicomTranscoder(
                dataset.InternalTransferSyntax,
                DicomTransferSyntax.ExplicitVRLittleEndian);
            dicomFile = transcoder.Transcode(dicomFile);
            dataset = dicomFile.Dataset;
        }

        var pixelData = DicomPixelData.Create(dataset);
        var totalFrames = Math.Max(1, pixelData.NumberOfFrames);
        var safeFrameIndex = Math.Clamp(frameIndex, 0, totalFrames - 1);

        var rows = dataset.GetSingleValue<int>(DicomTag.Rows);
        var columns = dataset.GetSingleValue<int>(DicomTag.Columns);
        var samplesPerPixel = dataset.GetSingleValueOrDefault(DicomTag.SamplesPerPixel, (ushort)1);
        var bitsAllocated = dataset.GetSingleValueOrDefault(DicomTag.BitsAllocated, (ushort)8);
        var bitsStored = dataset.GetSingleValueOrDefault(DicomTag.BitsStored, (ushort)8);
        var photometric = dataset.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2").Trim();
        var planarConfig = dataset.GetSingleValueOrDefault(DicomTag.PlanarConfiguration, (ushort)0);

        _logger.LogInformation(
            "DICOM frame info: {Rows}x{Cols}, Photometric={Photo}, SamplesPerPixel={Samples}, BitsAllocated={Bits}, PlanarConfig={Planar}, FrameIndex={FrameIndex}/{TotalFrames}",
            rows, columns, photometric, samplesPerPixel, bitsAllocated, planarConfig, safeFrameIndex, totalFrames);

        var rawBytes = pixelData.GetFrame(safeFrameIndex).Data;

        using var image = BuildRgbImageFromRawPixels(rawBytes, rows, columns, samplesPerPixel, bitsAllocated, bitsStored, photometric, planarConfig);
        using var outputStream = new MemoryStream();
        image.SaveAsPng(outputStream);
        return outputStream.ToArray();
    }

    // Constructs Image<Rgb24> from uncompressed DICOM pixel bytes.
    // Handles RGB, YBR_FULL, MONOCHROME (8/16-bit), pixel-interleaved and band-interleaved.
    private static Image<Rgb24> BuildRgbImageFromRawPixels(
        byte[] raw, int rows, int cols,
        int samplesPerPixel, int bitsAllocated, int bitsStored,
        string photometric, int planarConfig)
    {
        var img = new Image<Rgb24>(cols, rows);
        bool isColor = samplesPerPixel >= 3;
        bool isYbr = photometric.StartsWith("YBR", StringComparison.OrdinalIgnoreCase);
        bool is16bit = bitsAllocated == 16;
        bool invertGray = photometric == "MONOCHROME1";

        if (isColor && !is16bit)
        {
            int planeSize = rows * cols;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    byte c0, c1, c2;
                    if (planarConfig == 0)
                    {
                        int offset = (y * cols + x) * 3;
                        c0 = raw[offset]; c1 = raw[offset + 1]; c2 = raw[offset + 2];
                    }
                    else
                    {
                        int i = y * cols + x;
                        c0 = raw[i]; c1 = raw[planeSize + i]; c2 = raw[planeSize * 2 + i];
                    }

                    if (isYbr)
                    {
                        // YBR_FULL → RGB per DICOM PS3.3 C.7.6.3.1.2
                        double yD = c0, cbD = c1 - 128.0, crD = c2 - 128.0;
                        c0 = DicomClamp(yD + 1.402 * crD);
                        c1 = DicomClamp(yD - 0.344136 * cbD - 0.714136 * crD);
                        c2 = DicomClamp(yD + 1.772 * cbD);
                    }

                    img[x, y] = new Rgb24(c0, c1, c2);
                }
            }
        }
        else
        {
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    byte gray;
                    if (is16bit)
                    {
                        int offset = (y * cols + x) * 2;
                        ushort v = (ushort)(raw[offset] | (raw[offset + 1] << 8));
                        int maxVal = (1 << bitsStored) - 1;
                        gray = maxVal > 0 ? (byte)Math.Min(255, v * 255 / maxVal) : (byte)0;
                    }
                    else
                    {
                        gray = raw[y * cols + x];
                    }
                    if (invertGray)
                        gray = (byte)(255 - gray);
                    img[x, y] = new Rgb24(gray, gray, gray);
                }
            }
        }

        return img;
    }

    private static byte DicomClamp(double value)
        => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private InferenceInput PreprocessImage(byte[] imageBytes, AIModel model)
    {
        var imageSize = model.PreprocessingImageSize ?? 512;
        // Normalize method name: treat null/empty/any "imagenet*" variant as "imagenet_norm"
        var rawMethod = model.PreprocessingMethod?.Trim().ToLowerInvariant() ?? "";
        var method = (rawMethod == "" || rawMethod.StartsWith("imagenet")) ? "imagenet_norm" : rawMethod;

        // Detect expected channel count from OnnxInputSpec shape [batch, C, H, W]
        var inputSpecs = OnnxTensorSpecHandler.ParseSpecFromJson(model.OnnxInputSpec);
        var channels = 3;
        if (inputSpecs.Count > 0 && inputSpecs[0].Shape.Length >= 2 && inputSpecs[0].Shape[1] == 1)
            channels = 1;

        using var ms = new MemoryStream(imageBytes);
        using var image = Image.Load<Rgb24>(ms);
        image.Mutate(x => x.Resize(imageSize, imageSize));

        return channels == 1
            ? BuildGrayscaleTensor(image, imageSize, method)
            : BuildRgbTensor(image, imageSize, method, model);
    }

    private static InferenceInput BuildRgbTensor(Image<Rgb24> image, int size, string method, AIModel model)
    {
        var tensorShape = new[] { 1, 3, size, size };
        var tensor = new DenseTensor<float>(tensorShape);

        if (method is "minmax" or "zscore")
        {
            var r = new float[size * size];
            var g = new float[size * size];
            var b = new float[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var p = image[x, y];
                    int i = y * size + x;
                    r[i] = p.R / 255f; g[i] = p.G / 255f; b[i] = p.B / 255f;
                }
            if (method == "minmax") { NormalizeMinMax(r); NormalizeMinMax(g); NormalizeMinMax(b); }
            else                    { NormalizeZScore(r); NormalizeZScore(g); NormalizeZScore(b); }
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                { int i = y * size + x; tensor[0,0,y,x] = r[i]; tensor[0,1,y,x] = g[i]; tensor[0,2,y,x] = b[i]; }
        }
        else
        {
            float[]? mean = null, std = null;
            if (method == "imagenet_norm")
            {
                mean = JsonSerializer.Deserialize<float[]>(model.PreprocessingMean ?? "[0.485,0.456,0.406]") ?? [0.485f, 0.456f, 0.406f];
                std  = JsonSerializer.Deserialize<float[]>(model.PreprocessingStd  ?? "[0.229,0.224,0.225]") ?? [0.229f, 0.224f, 0.225f];
                if (mean.Length < 3 || std.Length < 3)
                    throw new InvalidOperationException($"Model '{model.Name}': PreprocessingMean/Std must have 3 values.");
            }

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var p = image[x, y];
                    if (method == "imagenet_norm")
                    {
                        tensor[0,0,y,x] = (p.R / 255f - mean![0]) / std![0];
                        tensor[0,1,y,x] = (p.G / 255f - mean![1]) / std![1];
                        tensor[0,2,y,x] = (p.B / 255f - mean![2]) / std![2];
                    }
                    else // "none"
                    {
                        tensor[0,0,y,x] = p.R / 255f;
                        tensor[0,1,y,x] = p.G / 255f;
                        tensor[0,2,y,x] = p.B / 255f;
                    }
                }
        }

        return new InferenceInput(tensor.ToArray(), tensorShape);
    }

    private static InferenceInput BuildGrayscaleTensor(Image<Rgb24> image, int size, string method)
    {
        var tensorShape = new[] { 1, 1, size, size };
        var tensor = new DenseTensor<float>(tensorShape);

        var values = new float[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = image[x, y];
                values[y * size + x] = (0.299f * p.R + 0.587f * p.G + 0.114f * p.B) / 255f;
            }

        switch (method)
        {
            case "minmax":       NormalizeMinMax(values); break;
            case "zscore":       NormalizeZScore(values); break;
            case "imagenet_norm":
                // grayscale ImageNet equivalent: mean≈0.449, std≈0.226
                for (int i = 0; i < values.Length; i++) values[i] = (values[i] - 0.449f) / 0.226f;
                break;
            // "none" or unknown → already [0,1], no further processing
        }

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                tensor[0, 0, y, x] = values[y * size + x];

        return new InferenceInput(tensor.ToArray(), tensorShape);
    }

    private static void NormalizeMinMax(float[] values)
    {
        var min = values.Min(); var max = values.Max();
        var range = max - min;
        if (range < 1e-6f) return;
        for (int i = 0; i < values.Length; i++) values[i] = (values[i] - min) / range;
    }

    private static void NormalizeZScore(float[] values)
    {
        var mean = values.Average();
        var std = MathF.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        if (std < 1e-6f) return;
        for (int i = 0; i < values.Length; i++) values[i] = (values[i] - mean) / std;
    }

    private sealed record FrameCandidate(byte[] ImageBytes, double Score);
}
