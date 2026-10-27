namespace Sitim.Core.Options
{
    public sealed class FederatedLearningOptions
    {
        public const string SectionName = "FederatedLearning";
        public const string ControlPlaneHttpClientName = "fl-control-plane";

        public string ControlPlaneBaseUrl { get; set; } = "http://localhost:18081";
        public int HttpTimeoutSeconds { get; set; } = 30;
        public int MinAvailableClients { get; set; } = 3;
        public int MinFitClients { get; set; } = 3;
        public int MinEvaluateClients { get; set; } = 3;
        public int SessionTimeoutMinutes { get; set; } = 180;
        public int MonitorIntervalMinutes { get; set; } = 1;
        public int OutputNumClasses { get; set; } = 5;
        public int OutputImageSize { get; set; } = 512;
        public string OutputPreprocessingMean { get; set; } = "[0.485, 0.456, 0.406]";
        public string OutputPreprocessingStd { get; set; } = "[0.229, 0.224, 0.225]";
        public int MissingExternalSessionGraceMinutes { get; set; } = 10;
    }
}
