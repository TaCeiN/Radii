using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using YamlDotNet.Serialization;

namespace Radii.Converters;

/// <summary>JSON, YAML, XML, TOML, CSV and TSV into each other, all in-process; JSON is the common middle step.</summary>
internal sealed class DataConverter : IConverter
{
    private static readonly string[] Known = ["json", "yaml", "yml", "xml", "toml", "csv", "tsv"];

    private static readonly Format[] All =
    [
        new("json", "JSON", "json"),
        new("yaml", "YAML", "yaml"),
        new("xml", "XML", "xml"),
        new("toml", "TOML", "toml"),
        new("csv", "CSV", "csv"),
        new("tsv", "TSV", "tsv"),
    ];

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string ExtOf(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext == "yml" ? "yaml" : ext;
    }

    public IReadOnlyList<Format> Outputs(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !paths.All(p => File.Exists(p) && Known.Contains(Path.GetExtension(p).TrimStart('.').ToLowerInvariant())))
            return [];
        var exts = paths.Select(ExtOf).Distinct().ToList();
        return All.Where(f => !(exts.Count == 1 && exts[0] == f.Ext)).ToList();
    }

    public Task ConvertAsync(IReadOnlyList<string> paths, Format to, ConvertContext context) => Task.Run(() =>
    {
        for (var i = 0; i < paths.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var input = paths[i];
            context.StartFile(i, paths.Count, Path.GetFileName(input));
            try
            {
                var text = File.ReadAllText(input, Encoding.UTF8);
                var node = Parse(ExtOf(input), text);
                var result = Write(to.Ext, node);
                var output = ConvertContext.NewPath(input, to.Ext);
                File.WriteAllText(output, result, new UTF8Encoding(false));
                context.Add(output);
            }
            catch (ConvertException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or YamlDotNet.Core.YamlException or System.Xml.XmlException or InvalidOperationException or FormatException or ArgumentException or NotSupportedException)
            {
                throw new ConvertException($"{Path.GetFileName(input)}: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex.GetType().Namespace?.StartsWith("Tomlyn", StringComparison.Ordinal) == true)
            {
                throw new ConvertException($"{Path.GetFileName(input)}: {ex.Message}", ex);
            }
        }
    }, context.Cancel);

    // --- reading ---------------------------------------------------------------------------------------------

    private static JsonNode? Parse(string ext, string text) => ext switch
    {
        "json" => JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }),
        "yaml" => FromObject(new DeserializerBuilder().Build().Deserialize<object?>(text)),
        "xml" => FromXml(XDocument.Parse(text).Root!),
        "toml" => FromToml(text),
        "csv" => FromCsv(text, ','),
        "tsv" => FromCsv(text, '\t'),
        _ => throw new ConvertException("?"),
    };

    /// <summary>YamlDotNet hands scalars over as strings: give them back their types.</summary>
    private static JsonNode? FromObject(object? value) => value switch
    {
        null => null,
        IDictionary<object, object> map => new JsonObject(map.Select(p => KeyValuePair.Create(Convert.ToString(p.Key, CultureInfo.InvariantCulture) ?? "", FromObject(p.Value)))),
        IList<object> list => new JsonArray(list.Select(FromObject).ToArray()),
        string s => Scalar(s),
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonNode? Scalar(string s)
    {
        switch (s)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }
        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) && (s == "0" || !s.TrimStart('-').StartsWith('0')))
            return JsonValue.Create(l);
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^-?(0|[1-9]\d*)\.\d+([eE][+-]?\d+)?$") && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        return JsonValue.Create(s);
    }

    private static JsonNode? FromToml(string text)
    {
        var table = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(text) ?? [];
        return FromTomlValue(table);
    }

    private static JsonNode? FromTomlValue(object? value) => value switch
    {
        null => null,
        Tomlyn.Model.TomlTable table => new JsonObject(table.Select(p => KeyValuePair.Create(p.Key, FromTomlValue(p.Value)))),
        Tomlyn.Model.TomlArray array => new JsonArray(array.Select(FromTomlValue).ToArray()),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int n => JsonValue.Create(n),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>Attributes become "@name", text next to children "#text", repeated elements an array.</summary>
    private static JsonNode? FromXml(XElement element)
    {
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        if (!element.HasElements && attributes.Count == 0)
            return Scalar(element.Value);
        var result = new JsonObject();
        foreach (var attribute in attributes)
            result["@" + attribute.Name.LocalName] = attribute.Value;
        foreach (var group in element.Elements().GroupBy(e => e.Name.LocalName))
        {
            var items = group.ToList();
            result[group.Key] = items.Count == 1 ? FromXml(items[0]) : new JsonArray(items.Select(FromXml).ToArray());
        }
        if (!element.HasElements && element.Value.Length > 0)
            result["#text"] = element.Value;
        return result;
    }

    private static JsonNode FromCsv(string text, char separator)
    {
        var rows = ParseCsv(text, separator);
        var result = new JsonArray();
        if (rows.Count == 0)
            return result;
        var header = rows[0];
        for (var r = 1; r < rows.Count; r++)
        {
            if (rows[r].Count == 1 && rows[r][0].Length == 0)
                continue;
            var row = new JsonObject();
            for (var c = 0; c < header.Count; c++)
                row[header[c].Length > 0 ? header[c] : "column" + (c + 1)] = c < rows[r].Count ? Scalar(rows[r][c]) ?? (rows[r][c].Length == 0 ? JsonValue.Create("") : null) : null;
            result.Add(row);
        }
        return result;
    }

    private static List<List<string>> ParseCsv(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == separator)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(ch);
            }
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        if (rows.Count > 0 && rows[0].Count > 0)
            rows[0][0] = rows[0][0].TrimStart('﻿');
        return rows;
    }

    // --- writing ---------------------------------------------------------------------------------------------

    private static string Write(string ext, JsonNode? node) => ext switch
    {
        "json" => (node?.ToJsonString(Pretty) ?? "null") + Environment.NewLine,
        "yaml" => new SerializerBuilder().Build().Serialize(ToObject(node)),
        "xml" => ToXml(node),
        "toml" => ToToml(node),
        "csv" => ToCsv(node, ','),
        "tsv" => ToCsv(node, '\t'),
        _ => throw new ConvertException("?"),
    };

    private static object? ToObject(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => o.ToDictionary(p => p.Key, p => ToObject(p.Value)),
        JsonArray a => a.Select(ToObject).ToList(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v => v.ToString(),
        _ => node.ToString(),
    };

    private static string ToToml(JsonNode? node)
    {
        if (node is not JsonObject)
            throw new ConvertException(Menus.T("TOML needs an object at the top level", "В TOML на верхнем уровне должен быть объект"));
        return Tomlyn.TomlSerializer.Serialize(ToTomlValue(node));
    }

    private static object? ToTomlValue(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "";
            case JsonObject o:
                var table = new Tomlyn.Model.TomlTable();
                foreach (var pair in o.Where(p => p.Value is not null))
                    table[pair.Key] = ToTomlValue(pair.Value)!;
                return table;
            case JsonArray a:
                var array = new Tomlyn.Model.TomlArray();
                foreach (var item in a.Where(x => x is not null))
                    array.Add(ToTomlValue(item)!);
                return array;
            default:
                return ToObject(node);
        }
    }

    private static string ToXml(JsonNode? node)
    {
        XElement root;
        if (node is JsonObject o && o.Count == 1 && o.First().Value is JsonObject && !o.First().Key.StartsWith('@'))
            root = ToElement(o.First().Key, o.First().Value);
        else
            root = ToElement("root", node);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }

    private static XElement ToElement(string name, JsonNode? node)
    {
        var element = new XElement(XmlName(name));
        switch (node)
        {
            case JsonObject o:
                foreach (var pair in o)
                {
                    if (pair.Key.StartsWith('@'))
                        element.SetAttributeValue(XmlName(pair.Key[1..]), pair.Value?.ToString());
                    else if (pair.Key == "#text")
                        element.Add(pair.Value?.ToString());
                    else if (pair.Value is JsonArray list)
                        element.Add(list.Select(item => ToElement(pair.Key, item)));
                    else
                        element.Add(ToElement(pair.Key, pair.Value));
                }
                break;
            case JsonArray a:
                element.Add(a.Select(item => ToElement("item", item)));
                break;
            case null:
                break;
            default:
                element.Value = node.ToString();
                break;
        }
        return element;
    }

    private static string XmlName(string name)
    {
        var clean = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray());
        return clean.Length == 0 || !(char.IsLetter(clean[0]) || clean[0] == '_') ? "_" + clean : clean;
    }

    private static string ToCsv(JsonNode? node, char separator)
    {
        var rows = node switch
        {
            JsonArray a => a.ToList(),
            JsonObject => [node],
            _ => throw new ConvertException(Menus.T("The data is not a table", "Данные не похожи на таблицу")),
        };
        if (rows.Any(r => r is not JsonObject))
            throw new ConvertException(Menus.T("CSV needs a list of objects with the same fields", "Для CSV нужен список объектов с одинаковыми полями"));
        var columns = rows.Cast<JsonObject>().SelectMany(r => r.Select(p => p.Key)).Distinct().ToList();
        var text = new StringBuilder();
        text.Append(string.Join(separator, columns.Select(c => Quote(c, separator)))).Append("\r\n");
        foreach (var row in rows.Cast<JsonObject>())
        {
            text.Append(string.Join(separator, columns.Select(c => Quote(row[c] switch
            {
                null => "",
                JsonValue v => v.ToString(),
                var other => other.ToJsonString(),
            }, separator)))).Append("\r\n");
        }
        return text.ToString();
    }

    private static string Quote(string value, char separator) =>
        value.IndexOfAny([separator, '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

/// <summary>Archives and folders into ZIP, 7z, TAR and TAR.GZ with the bsdtar (tar.exe) that ships with Windows; it reads RAR and 7z too.</summary>
internal sealed class ArchiveConverter : IConverter
{
    private static readonly string[] Known = [".zip", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".rar", ".zst", ".cab", ".iso"];

    private static readonly Format[] All =
    [
        new("zip", "ZIP", "zip"),
        new("7z", "7z", "7z"),
        new("tar.gz", "TAR.GZ", "tar.gz"),
        new("tar", "TAR", "tar"),
    ];

    private static string Tar => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");

    private static string Kind(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
            return "tar.gz";
        return Path.GetExtension(lower).TrimStart('.');
    }

    public IReadOnlyList<Format> Outputs(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !paths.All(p => Directory.Exists(p) || (File.Exists(p) && Known.Contains(Path.GetExtension(p).ToLowerInvariant()))))
            return [];
        var kinds = paths.Select(p => Directory.Exists(p) ? "" : Kind(p)).Distinct().ToList();
        return All.Where(f => !(kinds.Count == 1 && kinds[0] == f.Ext)).ToList();
    }

    public async Task ConvertAsync(IReadOnlyList<string> paths, Format to, ConvertContext context)
    {
        for (var i = 0; i < paths.Count; i++)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var input = paths[i];
            context.StartFile(i, paths.Count, Path.GetFileName(input.TrimEnd('\\')));
            context.Progress(null);
            if (Directory.Exists(input))
            {
                var trimmed = input.TrimEnd('\\');
                var output = ConvertContext.NewPath(trimmed, to.Ext);
                await Pack(output, Path.GetDirectoryName(trimmed)!, Path.GetFileName(trimmed), context);
                context.Add(output);
                continue;
            }

            var work = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                await Unpack(input, work, context);
                var target = ConvertContext.NewPath(input, to.Ext);
                await Pack(target, work, ".", context);
                context.Add(target);
            }
            finally
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static async Task Unpack(string archive, string folder, ConvertContext context)
    {
        // A lone .gz/.bz2/.xz is one compressed file, not a tar: tar.exe would reject it.
        var kind = Kind(archive);
        if (kind is "gz" or "bz2" or "xz" && !archive.Contains(".tar.", StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(archive);
            if (kind == "gz")
            {
                await using var source = File.OpenRead(archive);
                await using var gzip = new System.IO.Compression.GZipStream(source, System.IO.Compression.CompressionMode.Decompress);
                await using var target = File.Create(Path.Combine(folder, name));
                await gzip.CopyToAsync(target, context.Cancel);
                return;
            }
        }
        var (code, errors) = await ProcessRunner.RunAsync(Tar, ["-xf", archive, "-C", folder], context.Cancel);
        if (code != 0)
            throw new ConvertException(Menus.T("Could not unpack: ", "Не удалось распаковать: ") + ProcessRunner.Reason(errors));
    }

    private static async Task Pack(string output, string from, string what, ConvertContext context)
    {
        var (code, errors) = await ProcessRunner.RunAsync(Tar, ["-a", "-cf", output, "-C", from, what], context.Cancel);
        if (code != 0 || !File.Exists(output))
        {
            if (File.Exists(output))
                File.Delete(output);
            throw new ConvertException(Menus.T("Could not pack: ", "Не удалось упаковать: ") + ProcessRunner.Reason(errors));
        }
    }
}
