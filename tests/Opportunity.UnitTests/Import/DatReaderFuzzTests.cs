using AwesomeAssertions;

using Opportunity.Import.LoadFiles;

namespace Opportunity.UnitTests.Import;

/// <summary>
/// Deterministic fuzzing: random bytes and mutated valid DATs under random profiles, encodings and limits must never
/// crash the reader — only report issues, or stop with the documented pre-flight / threshold exceptions.
/// </summary>
public class DatReaderFuzzTests
{
    private static readonly LoadFileEncodingKind?[] Encodings = [null, LoadFileEncodingKind.Utf8, LoadFileEncodingKind.Utf16LE, LoadFileEncodingKind.Utf16BE, LoadFileEncodingKind.Windows1252];

    [Fact]
    public async Task Random_and_mutated_input_never_crashes_the_reader()
    {
        var random = new Random(75);
        DelimiterProfile p = DelimiterProfile.Concordance;
        string seedText = DatTestSupport.Dat(p, ["ControlNumber", "Subject", "Body"], ["ABC1", "Zoë þ quoted", "line®two"], ["ABC2", "", "¶,\"x\""], ["ABC3", "山田", "end"]);
        byte[][] seeds =
        [
            DatTestSupport.Encode(seedText, LoadFileEncodingKind.Utf8, true),
            DatTestSupport.Encode(seedText, LoadFileEncodingKind.Utf16LE, true),
            DatTestSupport.Encode(seedText, LoadFileEncodingKind.Windows1252, false),
            DatTestSupport.Encode("Id,Body\r\n1,\"a\r\nb\"\r\n2,\"x\"\"y\"\r\n", LoadFileEncodingKind.Utf8, false),
        ];
        byte[] interesting = [0x14, 0xFE, 0xC3, 0xBE, 0xAE, 0xB6, 0xC2, 0x0D, 0x0A, 0x00, 0xFF, 0x22, 0x2C, 0xEF, 0xBB, 0xBF];
        long rows = 0;
        for (int iteration = 0; iteration < 4000; iteration++)
        {
            byte[] input = iteration % 4 == 0 ? RandomBytes(random, random.Next(0, 600), interesting) : Mutate(random, seeds[random.Next(seeds.Length)], interesting);
            var options = new DatReaderOptions
            {
                Profile = DelimiterProfile.Presets[random.Next(DelimiterProfile.Presets.Count)],
                EncodingOverride = Encodings[random.Next(Encodings.Length)],
                HasHeader = random.Next(4) != 0,
                AllowDuplicateHeaders = random.Next(2) == 0,
                AllowLineBreaksInQualifiedValues = random.Next(3) switch { 0 => true, 1 => false, _ => null },
                MaxRecordBytes = random.Next(2) == 0 ? 16 + random.Next(200) : 1 << 20,
                MaxFieldCount = 1 + random.Next(10),
                BufferBytes = 64 + random.Next(300),
                MaxRejectedRows = random.Next(3) == 0 ? 2 : null,
                ReturnRejectedRecords = random.Next(2) == 0,
            };

            try
            {
                Stream stream = new DatTestSupport.TricklingStream(input, 1 + random.Next(64));
                await using DatReader reader = await DatReader.OpenAsync(stream, options, cancellationToken: TestContext.Current.CancellationToken);
                if (reader.HasPreflightErrors)
                {
                    continue;
                }

                await foreach (DatRecord record in reader.ReadAllAsync(TestContext.Current.CancellationToken))
                {
                    if (!record.IsRejected)
                    {
                        record.Values.Should().HaveCount(reader.Header.Count);
                    }
                }

                DatReadStatistics s = reader.Statistics;
                (s.AcceptedRows + s.RejectedRows).Should().Be(s.Rows, $"iteration {iteration}");
                s.Bytes.Should().Be(input.Length);
                rows += s.Rows;
            }
            catch (DatErrorThresholdExceededException)
            {
            }
        }

        rows.Should().BePositive();
    }

    private static byte[] RandomBytes(Random random, int length, byte[] interesting)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = random.Next(3) == 0 ? interesting[random.Next(interesting.Length)] : (byte)random.Next(256);
        }

        return bytes;
    }

    private static byte[] Mutate(Random random, byte[] seed, byte[] interesting)
    {
        var bytes = new List<byte>(seed);
        int mutations = 1 + random.Next(8);
        for (int m = 0; m < mutations && bytes.Count > 0; m++)
        {
            int at = random.Next(bytes.Count);
            switch (random.Next(5))
            {
                case 0:
                    bytes[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    bytes.Insert(at, interesting[random.Next(interesting.Length)]);
                    break;
                case 2:
                    bytes.RemoveAt(at);
                    break;
                case 3:
                    bytes.RemoveRange(at, bytes.Count - at);
                    break;
                default:
                    int from = random.Next(bytes.Count);
                    bytes.InsertRange(at, bytes.GetRange(from, Math.Min(32, bytes.Count - from)));
                    break;
            }
        }

        return [.. bytes];
    }
}
