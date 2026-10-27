using System.Text.Json;

namespace Sitim.Web.Models;

/// <summary>
/// One editable output-class row in the model interpretation editor:
/// the class label, its clinical severity, and free-text recommendations
/// (one per line). Used by the upload and edit-metadata dialogs.
/// </summary>
public sealed class ClassInterpretationRow
{
    public string Name { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;

    /// <summary>Recommendations, one per line; blank lines are ignored on save.</summary>
    public string RecommendationsText { get; set; } = string.Empty;
}

/// <summary>
/// Conversions between the editor rows and the JSON shapes the API stores
/// (ClassNames: string[], ClassSeverities: string[], ClassRecommendations: string[][]).
/// </summary>
public static class ClassInterpretation
{
    public static string[] Names(IEnumerable<ClassInterpretationRow> rows) =>
        rows.Select(r => r.Name?.Trim() ?? string.Empty).ToArray();

    public static string[] Severities(IEnumerable<ClassInterpretationRow> rows) =>
        rows.Select(r => r.Severity?.Trim() ?? string.Empty).ToArray();

    public static string[][] Recommendations(IEnumerable<ClassInterpretationRow> rows) =>
        rows.Select(r => SplitLines(r.RecommendationsText)).ToArray();

    /// <summary>True when at least one row has a non-empty class name.</summary>
    public static bool HasAny(IEnumerable<ClassInterpretationRow> rows) =>
        rows.Any(r => !string.IsNullOrWhiteSpace(r.Name));

    private static string[] SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n")
                  .Split('\n')
                  .Select(l => l.Trim())
                  .Where(l => l.Length > 0)
                  .ToArray();

    /// <summary>
    /// Rebuilds editor rows from a model's stored JSON metadata. Rows are aligned
    /// by class index; missing severities/recommendations simply leave blanks.
    /// </summary>
    public static List<ClassInterpretationRow> FromJson(
        string? classNamesJson, string? severitiesJson, string? recommendationsJson)
    {
        var names = DeserializeOrEmpty<string[]>(classNamesJson) ?? Array.Empty<string>();
        var severities = DeserializeOrEmpty<string[]>(severitiesJson) ?? Array.Empty<string>();
        var recommendations = DeserializeOrEmpty<string[][]>(recommendationsJson) ?? Array.Empty<string[]>();

        var rows = new List<ClassInterpretationRow>();
        for (var i = 0; i < names.Length; i++)
        {
            rows.Add(new ClassInterpretationRow
            {
                Name = names[i] ?? string.Empty,
                Severity = i < severities.Length ? severities[i] ?? string.Empty : string.Empty,
                RecommendationsText = i < recommendations.Length && recommendations[i] != null
                    ? string.Join("\n", recommendations[i])
                    : string.Empty
            });
        }
        return rows;
    }

    private static T? DeserializeOrEmpty<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }
}
