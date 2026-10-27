using Minio;
using Minio.DataModel.Args;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Sitim.Core.Services;

namespace Sitim.Infrastructure.Storage;

public class MinIOModelStorageService : IModelStorageService
{
    private readonly IMinioClient _minioClient;
    private readonly ILogger<MinIOModelStorageService> _logger;
    private readonly string _bucketName;

    public MinIOModelStorageService(
        IMinioClient minioClient,
        ILogger<MinIOModelStorageService> logger,
        IConfiguration configuration)
    {
        _minioClient = minioClient;
        _logger = logger;
        _bucketName = configuration["MinIO:BucketName"] ?? "ai-models";
    }

    public async Task<string> UploadModelAsync(
        string fileName,
        Stream fileStream,
        string contentType = "application/octet-stream",
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Ensure bucket exists
            await EnsureBucketExistsAsync(cancellationToken);

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(fileName)
                .WithStreamData(fileStream)
                .WithObjectSize(fileStream.Length)
                .WithContentType(contentType);

            await _minioClient.PutObjectAsync(putObjectArgs, cancellationToken);

            var objectUrl = $"minio://{_bucketName}/{fileName}";
            _logger.LogInformation(
                "Model {FileName} uploaded successfully to MinIO. Size: {Size} bytes",
                fileName, fileStream.Length);

            return objectUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload model {FileName} to MinIO", fileName);
            throw;
        }
    }

    public async Task<Stream> DownloadModelAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var memoryStream = new MemoryStream();

            var getObjectArgs = new GetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(fileName)
                .WithCallbackStream(stream =>
                {
                    stream.CopyTo(memoryStream);
                });

            await _minioClient.GetObjectAsync(getObjectArgs, cancellationToken);

            memoryStream.Position = 0;
            _logger.LogInformation("Model {FileName} downloaded from MinIO", fileName);

            return memoryStream;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download model {FileName} from MinIO", fileName);
            throw;
        }
    }

    public async Task<bool> ModelExistsAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var statObjectArgs = new StatObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(fileName);

            await _minioClient.StatObjectAsync(statObjectArgs, cancellationToken);
            return true;
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check if model {FileName} exists in MinIO", fileName);
            throw;
        }
    }

    public async Task DeleteModelAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var removeObjectArgs = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(fileName);

            await _minioClient.RemoveObjectAsync(removeObjectArgs, cancellationToken);
            _logger.LogInformation("Model {FileName} deleted from MinIO", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete model {FileName} from MinIO", fileName);
            throw;
        }
    }

    public async Task<string> GetModelDownloadUrlAsync(
        string fileName,
        int expirySeconds = 3600)
    {
        try
        {
            var presignedGetObjectArgs = new PresignedGetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(fileName)
                .WithExpiry(expirySeconds);

            var url = await _minioClient.PresignedGetObjectAsync(presignedGetObjectArgs);
            _logger.LogInformation(
                "Generated presigned URL for model {FileName}, valid for {Expiry} seconds",
                fileName, expirySeconds);

            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate presigned URL for model {FileName}", fileName);
            throw;
        }
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = new List<string>();

            var listObjectsArgs = new ListObjectsArgs()
                .WithBucket(_bucketName)
                .WithRecursive(true);

            await foreach (var item in _minioClient.ListObjectsEnumAsync(listObjectsArgs, cancellationToken))
            {
                models.Add(item.Key);
            }

            _logger.LogInformation("Listed {Count} models from MinIO", models.Count);
            return models;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list models from MinIO");
            throw;
        }
    }

    private async Task EnsureBucketExistsAsync(CancellationToken cancellationToken)
    {
        var bucketExistsArgs = new BucketExistsArgs()
            .WithBucket(_bucketName);

        bool found = await _minioClient.BucketExistsAsync(bucketExistsArgs, cancellationToken);

        if (!found)
        {
            var makeBucketArgs = new MakeBucketArgs()
                .WithBucket(_bucketName);

            await _minioClient.MakeBucketAsync(makeBucketArgs, cancellationToken);
            _logger.LogInformation("Created MinIO bucket: {BucketName}", _bucketName);
        }
    }
}
