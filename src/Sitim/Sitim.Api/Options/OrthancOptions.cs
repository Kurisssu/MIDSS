namespace Sitim.Api.Options;

public sealed class OrthancOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8042";
    public string DicomWebRoot { get; set; } = "/dicom-web";
}
