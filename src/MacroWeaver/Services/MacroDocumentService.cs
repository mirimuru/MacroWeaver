using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MacroWeaver.Models;

namespace MacroWeaver.Services;

public sealed class MacroDocument
{
    public string FormatVersion { get; set; } = "2.0";
    public string Name { get; set; } = "新しいマクロ";
    public List<MacroStep> Steps { get; set; } = [];
}

public static class MacroDocumentService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task SaveAsync(string path, MacroDocument document, CancellationToken cancellationToken = default)
    {
        document.FormatVersion = "2.0";
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken);
    }

    public static async Task<MacroDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<MacroDocument>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("マクロファイルの内容を読み取れませんでした。");

        if (document.FormatVersion == "1.0")
            MigrateLegacyRepeatMarkers(document);

        document.FormatVersion = "2.0";
        return document;
    }

    // Version 1.0 represented Repeat as a marker that repeated all following root steps.
    // Convert the first marker and its tail into a proper child list.
    private static void MigrateLegacyRepeatMarkers(MacroDocument document)
    {
        var repeatIndex = document.Steps.FindIndex(s => s.Type == MacroStepType.Repeat && s.Children.Count == 0);
        if (repeatIndex < 0 || repeatIndex == document.Steps.Count - 1) return;

        var repeat = document.Steps[repeatIndex];
        repeat.Children = document.Steps.Skip(repeatIndex + 1).ToList();
        document.Steps.RemoveRange(repeatIndex + 1, document.Steps.Count - repeatIndex - 1);
    }
}