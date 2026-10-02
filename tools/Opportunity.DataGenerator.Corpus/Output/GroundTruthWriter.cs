using System.Text.Json;

using Opportunity.DataGenerator.Corpus.Model;

namespace Opportunity.DataGenerator.Corpus.Output;

/// <summary>
/// Streams known answers for planted terms: one JSON line per (document, needle) and per (document, proximity pair).
/// Needles are planted only in a document's own body (never in quoted email text), so these are exact.
/// Aggregate counts (docs with hits, occurrences, family-expanded docs) are in the manifest.
/// </summary>
public sealed class GroundTruthWriter : ICorpusSink
{
    private readonly string _relativePath;
    private readonly HashingStream _stream;
    private readonly Utf8JsonWriter _json;

    public GroundTruthWriter(string outputDirectory, string relativePath)
    {
        _relativePath = relativePath;
        _stream = new HashingStream(File.Create(Path.Combine(outputDirectory, relativePath), 1 << 16));
        _json = new Utf8JsonWriter(_stream, JsonlCorpusWriter.WriterOptions);
    }

    public void Write(GeneratedChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        foreach (GeneratedFamily family in chunk.Families)
        {
            foreach (GeneratedDocument doc in family.Documents)
            {
                foreach (NeedleHit hit in doc.Content.Needles)
                {
                    _json.WriteStartObject();
                    _json.WriteString("type", "needle");
                    _json.WriteString("term", hit.Term);
                    _json.WriteNumber("occurrences", hit.Occurrences);
                    WriteDoc(doc);
                    EndLine();
                }

                foreach (ProximityHit hit in doc.Content.ProximityHits)
                {
                    _json.WriteStartObject();
                    _json.WriteString("type", "proximity");
                    _json.WriteString("first", hit.First);
                    _json.WriteString("second", hit.Second);
                    _json.WriteNumber("distance", hit.Distance);
                    _json.WriteBoolean("near", hit.IsNear);
                    WriteDoc(doc);
                    EndLine();
                }
            }
        }
    }

    public IReadOnlyList<CorpusOutputFile> Complete()
    {
        _json.Flush();
        _stream.Flush();
        return [new CorpusOutputFile(_relativePath, _stream.BytesWritten, _stream.HashHex())];
    }

    public void Dispose()
    {
        _json.Dispose();
        _stream.Dispose();
    }

    private void WriteDoc(GeneratedDocument doc)
    {
        _json.WriteString("controlNumber", doc.ControlNumber);
        _json.WriteString("familyId", doc.FamilyId);
        _json.WriteNumber("attachmentDepth", doc.AttachmentDepth);
        _json.WriteEndObject();
    }

    private void EndLine()
    {
        _json.Flush();
        _stream.WriteByte((byte)'\n');
        _json.Reset();
    }
}