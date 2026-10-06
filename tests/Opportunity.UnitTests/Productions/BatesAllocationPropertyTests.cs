using AwesomeAssertions;

using Opportunity.Core.Productions;

namespace Opportunity.UnitTests.Productions;

/// <summary>
/// E12-T03 property test over generated productions (seeded, so every failure is reproducible): Bates numbers are
/// gap-free, unique, monotonic in production order and family-adjacent; placeholders and native slip sheets consume
/// exactly one number; and the assignment is deterministic across chunk sizes and across crash/restart, i.e. chunks
/// executed in any order, any number of times, with partial results thrown away, assign exactly what one pass assigns.
/// </summary>
public class BatesAllocationPropertyTests
{
    private const int Trials = 300;

    public static TheoryData<int> Seeds => [.. Enumerable.Range(0, 6).Select(i => 20261006 + (i * 7919))];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Generated_productions_satisfy_the_bates_continuity_properties(int seed)
    {
        var random = new Random(seed);
        for (var trial = 0; trial < Trials; trial++)
        {
            var level = random.Next(2) == 0 ? BatesNumberingLevel.Page : BatesNumberingLevel.Document;
            var format = new BatesFormat(random.Next(3) == 0 ? "abc" : "ABC", random.Next(6, 10), random.Next(4) == 0 ? "-C" : string.Empty, level);
            var start = (long)random.Next(1, 50_000);
            var members = Members(random, random.Next(0, 120));
            var context = $"seed {seed}, trial {trial}, {members.Count} members, {level}";

            var reference = Run(format, start, members, documentsPerChunk: int.MaxValue, unitsPerChunk: int.MaxValue, random: null);
            AssertProperties(format, start, members, reference, context);

            // Deterministic whatever the chunking, and across crashes, redeliveries and out-of-order execution.
            var chunked = Run(format, start, members, random.Next(1, 12), random.Next(1, 60), random);
            chunked.Should().BeEquivalentTo(reference, o => o.WithStrictOrdering(), context);
        }
    }

    [Fact]
    public void Placeholders_and_native_slip_sheets_consume_exactly_one_number_at_page_level()
    {
        BatesPlanner.UnitsFor(BatesNumberingLevel.Page, ProductionOutputKind.Placeholder, 37).Should().Be(1);
        BatesPlanner.UnitsFor(BatesNumberingLevel.Page, ProductionOutputKind.Native, 12).Should().Be(1);
        BatesPlanner.UnitsFor(BatesNumberingLevel.Page, ProductionOutputKind.Image, 12).Should().Be(12);
        BatesPlanner.UnitsFor(BatesNumberingLevel.Page, ProductionOutputKind.Image, 0).Should().Be(1, "a document without stored pages still holds a number");
        BatesPlanner.UnitsFor(BatesNumberingLevel.Document, ProductionOutputKind.Image, 12).Should().Be(1);
    }

    [Fact]
    public void Chunks_close_only_between_families()
    {
        var family = Guid.CreateVersion7();
        var planner = new BatesPlanner(BatesNumberingLevel.Document, documentsPerChunk: 2);
        var plan = new[]
        {
            planner.Add(new BatesMember(1, Guid.CreateVersion7(), Guid.CreateVersion7(), ProductionOutputKind.Image, 1)),
            planner.Add(new BatesMember(2, Guid.CreateVersion7(), family, ProductionOutputKind.Image, 1)),
            planner.Add(new BatesMember(3, Guid.CreateVersion7(), family, ProductionOutputKind.Image, 1)),
            planner.Add(new BatesMember(4, Guid.CreateVersion7(), family, ProductionOutputKind.Image, 1)),
            planner.Add(new BatesMember(5, Guid.CreateVersion7(), Guid.CreateVersion7(), ProductionOutputKind.Image, 1)),
        };

        plan.Select(p => p.ChunkSequence).Should().Equal(1, 1, 1, 1, 2);
        planner.Chunks.Should().Be(2);
        planner.TotalUnits.Should().Be(5);
    }

    [Fact]
    public void Overflowing_the_padding_is_refused()
    {
        var format = new BatesFormat("X", 3, string.Empty, BatesNumberingLevel.Page);
        var act = () => BatesAllocator.Assign(format, 998, [new BatesSliceMember(1, Guid.CreateVersion7(), Guid.CreateVersion7(), 3, 0)]);

        act.Should().Throw<BatesOverflowException>();
    }

    [Fact]
    public void Labels_are_formatted_and_parsed()
    {
        var format = new BatesFormat("ABC", 7, "-CONF", BatesNumberingLevel.Document);

        format.Format(123).Should().Be("ABC0000123-CONF");
        format.PageLabel(123, 2).Should().Be("ABC0000123.0002-CONF");
        format.TryParse("abc0000123-conf", out var n).Should().BeTrue();
        n.Should().Be(123);
        format.TryParse("ABC123-CONF", out n).Should().BeTrue();
        n.Should().Be(123);
        format.TryParse("ABD0000123-CONF", out _).Should().BeFalse();
        format.TryParse("ABC00x0123-CONF", out _).Should().BeFalse();
        format.PrefixKey.Should().Be("ABC");
        BatesFormat.Validate("", 7, string.Empty).Should().NotBeNull();
        BatesFormat.Validate("-AB", 7, string.Empty).Should().NotBeNull();
        BatesFormat.Validate("AB C", 7, string.Empty).Should().NotBeNull();
        BatesFormat.Validate("ABC_", 13, string.Empty).Should().NotBeNull();
        BatesFormat.Validate("ABC_", 12, "_X").Should().BeNull();
    }

    [Fact]
    public void The_assignment_hash_changes_with_any_number_or_label()
    {
        var id = Guid.CreateVersion7();
        var a = new BatesAssignment(1, id, 1, 2, "A1", "A2", "A1", "A2");

        Hash(a).Should().Equal(Hash(a));
        Hash(a with { EndNumber = 3 }).Should().NotEqual(Hash(a));
        Hash(a with { ProdEndAttach = "A3" }).Should().NotEqual(Hash(a));

        static byte[] Hash(BatesAssignment assignment)
        {
            using var hasher = new BatesAssignmentHasher();
            hasher.Append(assignment.Sequence, assignment.DocumentId, 4, ProductionOutputKind.Image, 2, assignment);
            return hasher.Finish();
        }
    }

    /// <summary>Families of 1–6 members (some large), adjacent in production order, with random outputs and page counts.</summary>
    private static List<BatesMember> Members(Random random, int count)
    {
        var members = new List<BatesMember>(count);
        while (members.Count < count)
        {
            var family = Guid.CreateVersion7();
            var size = random.Next(10) == 0 ? random.Next(7, 30) : random.Next(1, 7);
            for (var i = 0; i < size && members.Count < count; i++)
            {
                var output = random.Next(10) switch
                {
                    0 => ProductionOutputKind.Placeholder,
                    1 => ProductionOutputKind.Native,
                    _ => ProductionOutputKind.Image,
                };
                members.Add(new BatesMember(members.Count + 1, Guid.CreateVersion7(), family, output, random.Next(0, 25)));
            }
        }

        return members;
    }

    /// <summary>
    /// Plans the production and executes its chunks. With <paramref name="random"/>, chunks run in a shuffled order,
    /// some "crash" (their results are thrown away before commit) and are redelivered, and committed chunks are
    /// sometimes redelivered again (a duplicate delivery whose re-execution must be identical).
    /// </summary>
    private static List<BatesAssignment> Run(
        BatesFormat format, long start, List<BatesMember> members, int documentsPerChunk, int unitsPerChunk, Random? random)
    {
        var planner = new BatesPlanner(format.Level, documentsPerChunk, unitsPerChunk);
        var planned = members.Select(planner.Add).ToList();
        planner.Documents.Should().Be(members.Count);
        var chunks = planned.Zip(members)
            .GroupBy(p => p.First.ChunkSequence)
            .Select(g => g.Select(x => new BatesSliceMember(x.Second.Sequence, x.Second.DocumentId, x.Second.FamilyKey, x.First.Units, x.First.FirstOffset)).ToList())
            .ToList();
        chunks.Should().HaveCount(planner.Chunks);

        var committed = new Dictionary<int, IReadOnlyList<BatesAssignment>>();
        var order = Enumerable.Range(0, chunks.Count).ToList();
        if (random is not null)
        {
            random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(order));
        }

        var queue = new Queue<int>(order);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            var result = BatesAllocator.Assign(format, start, chunks[c]);
            if (random is not null && random.Next(5) == 0)
            {
                queue.Enqueue(c); // crashed before commit: nothing recorded, redelivered later
                continue;
            }

            if (committed.TryGetValue(c, out var earlier))
            {
                result.Should().BeEquivalentTo(earlier, o => o.WithStrictOrdering(), "a redelivered chunk assigns what its committed attempt assigned");
            }
            else
            {
                committed[c] = result;
            }

            if (random is not null && random.Next(8) == 0)
            {
                queue.Enqueue(c); // duplicate delivery after commit
            }
        }

        return [.. committed.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value)];
    }

    private static void AssertProperties(BatesFormat format, long start, List<BatesMember> members, List<BatesAssignment> assignments, string context)
    {
        assignments.Should().HaveCount(members.Count, context);
        var expected = start;
        for (var i = 0; i < assignments.Count; i++)
        {
            var a = assignments[i];
            var m = members[i];
            a.Sequence.Should().Be(m.Sequence, context);
            a.DocumentId.Should().Be(m.DocumentId, context);
            a.BegNumber.Should().Be(expected, "gap-free and monotonic in production order ({0})", context);
            (a.EndNumber - a.BegNumber + 1).Should().Be(BatesPlanner.UnitsFor(format.Level, m.Output, m.PageCount), context);
            if (m.Output != ProductionOutputKind.Image || format.Level == BatesNumberingLevel.Document)
            {
                a.EndNumber.Should().Be(a.BegNumber, "a placeholder, slip sheet or document-level number consumes one ({0})", context);
            }

            a.ProdBegBates.Should().Be(format.Format(a.BegNumber), context);
            a.ProdEndBates.Should().Be(format.Format(a.EndNumber), context);
            expected = a.EndNumber + 1;
        }

        assignments.Select(a => a.ProdBegBates).Should().OnlyHaveUniqueItems(context);
        foreach (var family in members.Select((m, i) => (m.FamilyKey, i)).GroupBy(x => x.FamilyKey))
        {
            var positions = family.Select(x => x.i).ToList();
            (positions[^1] - positions[0] + 1).Should().Be(positions.Count, "a family is adjacent ({0})", context);
            var first = assignments[positions[0]];
            var last = assignments[positions[^1]];
            foreach (var p in positions)
            {
                assignments[p].ProdBegAttach.Should().Be(first.ProdBegBates, context);
                assignments[p].ProdEndAttach.Should().Be(last.ProdEndBates, context);
            }
        }
    }
}
