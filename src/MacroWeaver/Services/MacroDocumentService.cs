using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MacroWeaver.Models;

namespace MacroWeaver.Services;

public sealed class MacroDocument
{
    public string FormatVersion { get; set; } = "1.0";
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
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken);
    }

    public static async Task<MacroDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<MacroDocument>(stream, Options, cancellationToken);
        return document ?? throw new InvalidDataException("マクロファイルの内容を読み取れませんでした。");
    }
}