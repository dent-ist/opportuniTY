using System.Globalization;
using System.Text;
using System.Text.Json;

using Opportunity.DataGenerator.Corpus.Output;

namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>Counters and load-file hashes of one written volume (part of <c>volume-manifest.json</c>).</summary>
public sealed class VolumeSummary(string name)
{
    public string Name { get; } = name;

    public long Documents { get; set; }

    public int DatRows { get; set; }

    public long OptRows { get; set; }

    public long Pages { get; set; }

    public long ImageFiles { get; set; }

    public long Natives { get; set; }

    public long PlaceholderNatives { get; set; }

    public long TextFiles { get; set; }

    public int OverlayRows { get; set; }

    public List<CorpusOutputFile> LoadFiles { get; } = [];

    public void WriteTo(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartObject();
        w.WriteString("name", Name);
        w.WriteNumber("documents", Documents);
        w.WriteNumber("datRows", DatRows);
        w.WriteNumber("optRows", OptRows);
        w.WriteNumber("pages", Pages);
        w.WriteNumber("imageFiles", ImageFiles);
        w.WriteNumber("natives", Natives);
        w.WriteNumber("placeholderNatives", PlaceholderNatives);
        w.WriteNumber("textFiles", TextFiles);
        w.WriteNumber("overlayRows", OverlayRows);
        w.WriteStartArray("loadFiles");
        foreach (CorpusOutputFile f in LoadFiles)
        {
            w.WriteStartObject();
            w.WriteString("path", f.Path);
            w.WriteNumber("bytes", f.Bytes);
            w.WriteString("sha256", f.Sha256);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}

/// <summary>One open volume: its DAT/OPT/overlay streams and folder allocators.</summary>
internal sealed class Volume : IDisposable
{
    private readonly string _directory;
    private readonly VolumeOptions _options;
    private readonly HashingStream _dat;
    private readonly HashingStream _opt;
    private readonly HashingStream? _overlay;
    private readonly string _datPath;
    private readonly string _optPath;
    private readonly string _overlayPath;

    public Volume(string directory, string name, VolumeOptions options, IReadOnlyList<string> header)
    {
        _directory = directory;
        _options = options;
        Summary = new VolumeSummary(name);
        Directory.CreateDirectory(Path.Combine(directory, "DATA"));
        _datPath = "DATA/" + name + ".dat";
        _optPath = "DATA/" + name + ".opt";
        _overlayPath = "DATA/" + name + "_OVERLAY.dat";
        _dat = Open(_datPath);
        _dat.Write(LoadFileEncodings.Preamble(options.DatEncoding));
        _opt = Open(_optPath);
        var row = new StringBuilder();
        AppendRow(row, header);
        LoadFileEncodings.Write(options.DatEncoding, row.ToString(), _dat);
        if (options.OverlayRate > 0)
        {
            _overlay = Open(_overlayPath);
            _overlay.Write(LoadFileEncodings.Preamble(options.DatEncoding));
            row.Clear();
            AppendRow(row, VolumeWriter.OverlayColumns);
            LoadFileEncodings.Write(options.DatEncoding, row.ToString(), _overlay);
        }

        Images = new FolderAllocator(directory, "IMAGES", "IMG", options.FilesPerFolder);
        Natives = new FolderAllocator(directory, "NATIVES", "NATIVE", options.FilesPerFolder);
        Text = new FolderAllocator(directory, "TEXT", "TEXT", options.FilesPerFolder);
    }

    public VolumeSummary Summary { get; }

    public FolderAllocator Images { get; }

    public FolderAllocator Natives { get; }

    public FolderAllocator Text { get; }

    public bool HasOverlay => _overlay != null;

    /// <summary>Creates a file from a volume-relative path written with backslashes (as it appears in the load files).</summary>
    public FileStream CreateFile(string relativePath) =>
        new(Path.Combine(_directory, relativePath.Replace('\\', Path.DirectorySeparatorChar)), FileMode.Create, FileAccess.Write, FileShare.None, 4096);

    public void WriteDat(StringBuilder row, LoadFileEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(row);
        LoadFileEncodings.Write(encoding, row.ToString(), _dat);
    }

    public void WriteOpt(string imageKey, string path, bool documentBreak, int? pageCount)
    {
        string line = imageKey + "," + Summary.Name + "," + path + "," + (documentBreak ? "Y" : "") + ",,,"
            + (pageCount?.ToString(CultureInfo.InvariantCulture) ?? "") + "\r\n";
        LoadFileEncodings.Write(LoadFileEncoding.Windows1252, line, _opt);
        Summary.OptRows++;
    }

    public void WriteOverlayRow(IReadOnlyList<string> values, StringBuilder scratch)
    {
        scratch.Clear();
        AppendRow(scratch, values);
        LoadFileEncodings.Write(_options.DatEncoding, scratch.ToString(), _overlay!);
    }

    public IReadOnlyList<CorpusOutputFile> Close()
    {
        var files = new List<CorpusOutputFile>();
        Add(files, _datPath, _dat);
        Add(files, _optPath, _opt);
        if (_overlay != null)
        {
            Add(files, _overlayPath, _overlay);
        }

        Summary.LoadFiles.AddRange(files);
        return files;
    }

    public void Dispose()
    {
        _dat.Dispose();
        _opt.Dispose();
        _overlay?.Dispose();
    }

    private void Add(List<CorpusOutputFile> files, string path, HashingStream stream)
    {
        stream.Flush();
        files.Add(new CorpusOutputFile(Summary.Name + "/" + path, stream.BytesWritten, stream.HashHex()));
    }

    private HashingStream Open(string relativePath) =>
        new(new FileStream(Path.Combine(_directory, relativePath), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16));

    private void AppendRow(StringBuilder row, IReadOnlyList<string> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                row.Append(_options.Delimiters.Column);
            }

            _options.Delimiters.AppendQualified(row, values[i]);
        }

        row.Append("\r\n");
    }
}

/// <summary>Assigns files to <c>PREFIX0001</c>-style subfolders holding at most <c>limit</c> files each.</summary>
internal sealed class FolderAllocator(string volumeDirectory, string parent, string prefix, int limit)
{
    private int _index;
    private int _count;

    /// <summary>Returns the subfolder (e.g. <c>IMG0001</c>) for a group of files that must stay together.</summary>
    public string Allocate(int files)
    {
        if (_index == 0 || (_count > 0 && _count + files > limit))
        {
            _index++;
            _count = 0;
            Directory.CreateDirectory(Path.Combine(volumeDirectory, parent, Name));
        }

        _count += files;
        return Name;
    }

    private string Name => prefix + _index.ToString("D4", CultureInfo.InvariantCulture);
}
