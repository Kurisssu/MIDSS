using FellowOakDicom;

namespace Sitim.Infrastructure.Services;

/// <summary>
/// Detects and validates DICOM modality codes from DICOM datasets
/// </summary>
public static class ModalityDetector
{
    /// <summary>
    /// Extracts DICOM Modality code from dataset
    /// DICOM tag (0008,0060) = Modality
    /// </summary>
    /// <returns>Two-letter modality code (e.g., "OT", "CR", "CT") or null if not found</returns>
    public static string? DetectModality(DicomDataset dataset)
    {
        if (dataset == null)
            return null;

        // DICOM Tag (0008,0060) = Modality
        if (dataset.TryGetString(DicomTag.Modality, out var modality))
        {
            var code = modality?.ToUpper().Trim();
            return string.IsNullOrWhiteSpace(code) ? null : code;
        }

        return null;
    }

    /// <summary>
    /// Validates if modality is a standard DICOM code
    /// </summary>
    public static bool IsValidModality(string? modality)
    {
        if (string.IsNullOrWhiteSpace(modality))
            return false;

        var validModalities = new[]
        {
            "OT",  // Ophthalmic Tomography
            "OP",  // Ophthalmic Photography (non-standard variant)
            "OPT", // Ophthalmic Photography (non-standard variant)
            "XC",  // Ophthalmic Photography
            "CR",  // Computed Radiography
            "DX",  // Digital X-Ray
            "RF",  // Radiofluoroscopy
            "CT",  // CT Scan
            "MR",  // Magnetic Resonance Imaging
            "US",  // Ultrasound
            "PT",  // Positron Emission Tomography
            "NM",  // Nuclear Medicine
            "BI",  // Biomagnetic Imaging
            "XA",  // X-Ray Angiography
            "MG",  // Mammography
        };

        return validModalities.Contains(modality.ToUpper());
    }

    /// <summary>
    /// Get human-readable description for modality code
    /// </summary>
    public static string GetModalityDescription(string? modality)
    {
        return modality?.ToUpper() switch
        {
            "OT" => "Ophthalmic Tomography",
            "OP" => "Ophthalmic Photography",
            "OPT" => "Ophthalmic Photography",
            "XC" => "Ophthalmic Photography",
            "CR" => "Computed Radiography",
            "DX" => "Digital X-Ray",
            "RF" => "Radiofluoroscopy",
            "CT" => "CT Scan",
            "MR" => "Magnetic Resonance Imaging",
            "US" => "Ultrasound",
            "PT" => "Positron Emission Tomography",
            "NM" => "Nuclear Medicine",
            "BI" => "Biomagnetic Imaging",
            "XA" => "X-Ray Angiography",
            "MG" => "Mammography",
            _ => "Unknown"
        };
    }
}
