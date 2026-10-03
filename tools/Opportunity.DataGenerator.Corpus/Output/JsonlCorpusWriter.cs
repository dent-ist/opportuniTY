using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Text;

namespace Opportunity.DataGenerator.Corpus.Output;

/// <summary>
/// Writes one JSON object per document (load order) to a JSONL file. Text is omitted unless a synthesiser is
/// supplied, in which case it is streamed inline as the "text" property.
/// </summary>
public sealed class JsonlCorpusWriter : ICorpusSink
{
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private readonly string _relativePath;
    private readonly IReadOnlyList<FieldDefinition> _fields;
    private readonly TextSynthesizer? _inlineText;
    private readonly HashingStream _stream;
    private readonly Utf8JsonWriter _json;
    private bool _completed;

    public JsonlCorpusWriter(string outputDirectory, string relativePath, FieldCatalog catalog, TextSynthesizer? inlineText = null)
        : this(File.Create(Path.Combine(outputDirectory, relativePath), 1 << 20), relativePath, catalog, inlineText)
    {
    }

    public JsonlCorpusWriter(Stream destination, string relativePath, FieldCatalog catalog, TextSynthesizer? inlineText = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _relativePath = relativePath;
        _fields = catalog.Fields;
        _inlineText = inlineText;
        _stream = new HashingStream(destination);
        _json = new Utf8JsonWriter(_stream, WriterOptions);
    }

    public void Write(GeneratedChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        foreach (GeneratedFamily family in chunk.Families)
        {
            foreach (GeneratedDocument doc in family.Documents)
            {
                WriteDocument(doc);
                _json.Flush();
                _stream.WriteByte((byte)'\n');
                _json.Reset();
            }
        }
    }

    public IReadOnlyList<CorpusOutputFile> Complete()
    {
        if (!_completed)
        {
            _json.Flush();
            _stream.Flush();
            _completed = true;
        }

        return [new CorpusOutputFile(_relativePath, _stream.BytesWritten, _stream.HashHex())];
    }

    public void Dispose()
    {
        _json.Dispose();
        _stream.Dispose();
    }

    private void WriteDocument(GeneratedDocument d)
    {
        Utf8JsonWriter w = _json;
        w.WriteStartObject();
        w.WriteNumber("docIndex", d.DocIndex);
        w.WriteString("controlNumber", d.ControlNumber);
        w.WriteString("familyId", d.FamilyId);
        if (d.ParentControlNumber != null)
        {
            w.WriteString("parentControlNumber", d.ParentControlNumber);
        }

        w.WriteNumber("familySequence", d.FamilySequence);
        w.WriteNumber("attachmentDepth", d.AttachmentDepth);
        w.WriteString("begAttach", d.BegAttach);
        w.WriteString("endAttach", d.EndAttach);
        w.WriteString("kind", d.Kind == DocumentKind.Email ? "email" : "edoc");
        w.WriteString("fileType", d.Content.FileType);
        w.WriteString("custodian", d.Custodian);
        WriteArray(w, "allCustodians", d.AllCustodians);
        WriteArray(w, "duplicateCustodians", d.DuplicateCustodians);
        w.WriteString("md5", d.Md5);
        w.WriteString("sha256", d.Content.Sha256);
        if (d.DuplicateGroupId != null)
        {
            w.WriteString("duplicateGroupId", d.DuplicateGroupId);
        }

        if (d.FamilyDuplicateGroupId != null)
        {
            w.WriteString("familyDuplicateGroupId", d.FamilyDuplicateGroupId);
        }

        w.WriteString("duplicateType", d.DuplicateType switch
        {
            DuplicateType.ExactMd5 => "exactMd5",
            DuplicateType.CrossCustodian => "crossCustodian",
            DuplicateType.WithinFamily => "withinFamily",
            _ => "none",
        });
        w.WriteBoolean("isDuplicatePrimary", d.IsDuplicatePrimary);
        if (d.EmailThreadId != null)
        {
            w.WriteString("emailThreadId", d.EmailThreadId);
        }

        if (d.Content.NearDuplicateClusterId != null)
        {
            w.WriteString("nearDuplicateClusterId", d.Content.NearDuplicateClusterId);
        }

        w.WriteNumber("textBytes", d.TextBytes);
        w.WriteStartObject("fields");
        for (int i = 0; i < _fields.Count; i++)
        {
            object? value = d.Fields[i];
            if (value != null)
            {
                w.WritePropertyName(_fields[i].Name);
                WriteValue(w, value);
            }
        }

        w.WriteEndObject();
        if (_inlineText != null)
        {
            w.WritePropertyName("text");
            var sink = new JsonSegmentSink(w);
            _inlineText.Write(d.Content.Text, sink);
            w.WriteStringValueSegment(ReadOnlySpan<char>.Empty, isFinalSegment: true);
        }

        w.WriteEndObject();
    }

    public static void WriteValue(Utf8JsonWriter w, object value)
    {
        ArgumentNullException.ThrowIfNull(w);
        switch (value)
        {
            case string s:
                w.WriteStringValue(s);
                break;
            case string[] values:
                w.WriteStartArray();
                foreach (string v in values)
                {
                    w.WriteStringValue(v);
                }

                w.WriteEndArray();
                break;
            case DateTimeOffset dto:
                w.WriteStringValue(dto.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
                break;
            case DateOnly date:
                w.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case decimal m:
                w.WriteNumberValue(m);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            default:
                throw new InvalidOperationException("Unsupported field value type " + value.GetType().Name);
        }
    }

    private static void WriteArray(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (string v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }

    private sealed class JsonSegmentSink(Utf8JsonWriter writer) : ITextSink
    {
        public void Write(ReadOnlySpan<char> chars) => writer.WriteStringValueSegment(chars, isFinalSegment: false);
    }
}
