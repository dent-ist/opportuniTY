using System.Diagnostics;
using System.Globalization;
using System.Text;

using AwesomeAssertions;

using Opportunity.Import.LoadFiles;

namespace Opportunity.UnitTests.Import;

/// <summary>
/// Throughput and memory of the streaming reader on synthetic DATs generated on the fly (nothing on disk).
/// The 1M-row run is part of the unit suite; the multi-GB run (AC: 5 GB / 1M rows under 200 MB working set) is
/// opt-in: set <c>OPPORTUNITY_DAT_SCALE_GB</c> (e.g. 5).
/// Runs outside the parallel test pool so memory readings are not polluted by other tests.
/// </summary>
[Collection(nameof(DatReaderScaleTests))]
public class DatReaderScaleTests
{
    [Fact]
    public async Task One_million_rows_parse_in_constant_memory()
    {
        Measurement m = await MeasureAsync(rows: 1_000_000, textBytes: 200);
        Report("1M rows", m);
        m.Rows.Should().Be(1_000_000);
        m.Issues.Should().Be(0);
        m.PeakManagedGrowthBytes.Should().BeLessThan(64L * 1024 * 1024, "the reader must not buffer the file");
    }

    [Fact]
    public async Task Multi_gigabyte_dat_parses_under_200_mb_working_set()
    {
        string? gb = Environment.GetEnvironmentVariable("OPPORTUNITY_DAT_SCALE_GB");
        Assert.SkipWhen(string.IsNullOrEmpty(gb), "Set OPPORTUNITY_DAT_SCALE_GB (e.g. 5) to run the multi-GB DAT benchmark.");
        long targetBytes = (long)(double.Parse(gb!, CultureInfo.InvariantCulture) * 1024 * 1024 * 1024);
        const int rows = 1_000_000;
        int textBytes = (int)Math.Max(200, (targetBytes / rows) - 400);
        Measurement m = await MeasureAsync(rows, textBytes);
        Report($"{gb} GB", m);
        m.Rows.Should().Be(rows);
        (m.PeakWorkingSetBytes - m.BaselineWorkingSetBytes).Should().BeLessThan(200L * 1024 * 1024, "reading must add < 200 MB to the process working set");
    }

    private static void Report(string name, Measurement m)
    {
        string line = string.Create(CultureInfo.InvariantCulture,
            $"DAT reader {name}: {m.Rows:N0} rows, {m.Bytes / 1048576.0:N0} MiB in {m.Elapsed.TotalSeconds:F2} s = {m.Rows / m.Elapsed.TotalSeconds:N0} rows/s, " +
            $"{m.Bytes / 1048576.0 / m.Elapsed.TotalSeconds:N0} MiB/s (process CPU {m.Cpu.TotalSeconds:F2} s, incl. the synthetic source); peak live managed heap growth {m.PeakManagedGrowthBytes / 1048576.0:N1} MiB; " +
            $"test-host working set {m.BaselineWorkingSetBytes / 1048576.0:N0} MiB before, peak {m.PeakWorkingSetBytes / 1048576.0:N0} MiB; allocated {m.AllocatedBytes / 1048576.0:N0} MiB");
        TestContext.Current.SendDiagnosticMessage(line);
        TestContext.Current.TestOutputHelper?.WriteLine(line);
    }

    private sealed record Measurement(long Rows, long Bytes, long Issues, TimeSpan Elapsed, TimeSpan Cpu, long PeakManagedGrowthBytes, long PeakWorkingSetBytes, long BaselineWorkingSetBytes, long AllocatedBytes);

    private static async Task<Measurement> MeasureAsync(int rows, int textBytes)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);
        long allocatedBefore = GC.GetTotalAllocatedBytes();
        using var process = Process.GetCurrentProcess();
        long peakManaged = 0;
        process.Refresh();
        long baselineWorkingSet = process.WorkingSet64;
        long peakWorkingSet = baselineWorkingSet;
        var stream = new SyntheticDatStream(rows, textBytes);
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        long count = 0;
        long fields = 0;
        await using (DatReader reader = await DatReader.OpenAsync(stream, new DatReaderOptions { BufferBytes = 1 << 20 }, cancellationToken: TestContext.Current.CancellationToken))
        {
            await foreach (DatRecord record in reader.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                count++;
                fields += record.Values.Count;
                if ((count & 0x1FFFF) == 0)
                {
                    peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(forceFullCollection: true) - baseline);
                    process.Refresh();
                    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                }
            }

            watch.Stop();
            process.Refresh();
            fields.Should().Be(count * SyntheticDatStream.Columns);
            return new Measurement(count, stream.BytesProduced, reader.Statistics.RejectedRows + reader.Statistics.RowsWithWarnings,
                watch.Elapsed, process.TotalProcessorTime - cpuBefore, peakManaged, peakWorkingSet, baselineWorkingSet, GC.GetTotalAllocatedBytes() - allocatedBefore);
        }
    }

    /// <summary>A Concordance UTF-8 DAT produced row by row on read: realistic columns, non-ASCII, ® line breaks, doubled qualifiers.</summary>
    private sealed class SyntheticDatStream(int rows, int textBytes) : Stream
    {
        public const int Columns = 24;

        private readonly StringBuilder _sb = new();
        private byte[] _pending = new byte[4096];
        private char[] _chars = new char[4096];
        private int _pendingLength;
        private int _pendingPos;
        private int _row = -1;

        public long BytesProduced { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => BytesProduced; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count)
            {
                if (_pendingPos == _pendingLength && !NextRow())
                {
                    break;
                }

                int n = Math.Min(count - written, _pendingLength - _pendingPos);
                Array.Copy(_pending, _pendingPos, buffer, offset + written, n);
                _pendingPos += n;
                written += n;
            }

            BytesProduced += written;
            return written;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private bool NextRow()
        {
            if (_row >= rows)
            {
                return false;
            }

            _sb.Clear();
            if (_row < 0)
            {
                _sb.Append(string.Join('\u0014', Enumerable.Range(0, Columns).Select(i => "þField" + i + "þ")));
            }
            else
            {
                int r = _row;
                for (int c = 0; c < Columns; c++)
                {
                    if (c > 0)
                    {
                        _sb.Append('\u0014');
                    }

                    _sb.Append('þ');
                    switch (c)
                    {
                        case 0:
                            _sb.Append("ABC").Append(r.ToString("D8", CultureInfo.InvariantCulture));
                            break;
                        case 1:
                            _sb.Append("Re: Quarterly résumé þþ").Append(r % 97).Append("þþ ®second line");
                            break;
                        case 2:
                            _sb.Append("Müller, Zoë; Smith, John; 山田太郎");
                            break;
                        case 3:
                            _sb.Append("2019-03-").Append((r % 28) + 1).Append("T10:15:00-05:00");
                            break;
                        case 4:
                            _sb.Append(new string((char)('a' + (r % 26)), textBytes));
                            break;
                        default:
                            if ((r + c) % 3 != 0)
                            {
                                _sb.Append("value-").Append(c).Append('-').Append(r % 1000);
                            }

                            break;
                    }

                    _sb.Append('þ');
                }
            }

            _sb.Append("\r\n");
            if (_chars.Length < _sb.Length)
            {
                _chars = new char[_sb.Length * 2];
                _pending = new byte[_sb.Length * 8];
            }

            _sb.CopyTo(0, _chars, _sb.Length);
            _pendingLength = Encoding.UTF8.GetBytes(_chars, 0, _sb.Length, _pending, 0);
            _pendingPos = 0;
            _row++;
            return true;
        }
    }
}

[CollectionDefinition(nameof(DatReaderScaleTests), DisableParallelization = true)]
public sealed class DatReaderScaleSerial;
