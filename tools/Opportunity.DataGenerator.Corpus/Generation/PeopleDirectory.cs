using System.Globalization;

using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.Generation;

public sealed record Person(int Id, string DisplayName, string Address, TimeSpan Offset, bool IsInternal)
{
    /// <summary>"Display Name &lt;address&gt;" as used in From/To/CC/BCC values.</summary>
    public string Formatted { get; } = DisplayName + " <" + Address + ">";
}

/// <summary>
/// Invented people: ids [0, internal) are employees of the internal domain, the first Custodians.Count of
/// them are the custodians; the rest are external contacts on invented ".example" domains.
/// </summary>
public sealed class PeopleDirectory
{
    private readonly Person[] _people;
    private readonly AliasTable _custodianTable;

    public PeopleDirectory(CorpusProfile profile, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        PeopleProfile p = profile.People;
        InternalCount = p.InternalCount;
        CustodianCount = profile.Custodians.Count;

        var domainRng = Rng.For(seed, StreamTag.People, 1);
        string[] externalDomains = new string[p.ExternalDomainCount];
        for (int i = 0; i < externalDomains.Length; i++)
        {
            externalDomains[i] = string.Create(CultureInfo.InvariantCulture, $"{Syllables.Word(domainRng, 2, 3)}{i}.example");
        }

        TimeSpan[] offsets = [.. profile.Dates.TimeZones.Select(z => ProfileSerializer.TryParseOffset(z.Offset, out TimeSpan o) ? o : TimeSpan.Zero)];
        var offsetTable = new AliasTable([.. profile.Dates.TimeZones.Select(z => z.Weight)]);

        int total = p.InternalCount + p.ExternalCount;
        _people = new Person[total];
        for (int id = 0; id < total; id++)
        {
            var rng = Rng.For(seed, StreamTag.People, 2, (ulong)id);
            bool isInternal = id < p.InternalCount;
            double script = rng.NextDouble();
            string displayName;
            string local;
            if (script < p.CjkShare)
            {
                displayName = Syllables.Cjk(rng, 1) + Syllables.Cjk(rng, rng.NextInt(1, 2));
                local = "user" + id.ToString(CultureInfo.InvariantCulture);
            }
            else if (script < p.CjkShare + p.RtlShare)
            {
                bool arabic = rng.Chance(0.6);
                displayName = Syllables.Rtl(rng, arabic, rng.NextInt(3, 5)) + " " + Syllables.Rtl(rng, arabic, rng.NextInt(3, 6));
                local = "user" + id.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                string given = Syllables.Word(rng, 1, 2);
                string family = Syllables.Word(rng, 2, 3);
                local = string.Create(CultureInfo.InvariantCulture, $"{given}.{family}{id}");
                if (script < p.CjkShare + p.RtlShare + p.AccentedShare)
                {
                    given = Syllables.Accent(rng, given);
                    family = Syllables.Accent(rng, family);
                }

                displayName = rng.Chance(0.2)
                    ? Syllables.Capitalize(family) + ", " + Syllables.Capitalize(given)
                    : Syllables.Capitalize(given) + " " + Syllables.Capitalize(family);
            }

            string domain = isInternal ? p.InternalDomain : externalDomains[rng.NextInt(externalDomains.Length)];
            _people[id] = new Person(id, displayName, local + "@" + domain, offsets[offsetTable.Sample(rng)], isInternal);
        }

        _custodianTable = new AliasTable(AliasTable.ZipfWeights(CustodianCount, profile.Custodians.ZipfExponent));
        _custodianNames = new string[CustodianCount];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int id = 0; id < CustodianCount; id++)
        {
            string name = _people[id].DisplayName;
            _custodianNames[id] = used.Add(name) ? name : string.Create(CultureInfo.InvariantCulture, $"{name} ({id})");
        }
    }

    private readonly string[] _custodianNames;

    public int InternalCount { get; }

    public int CustodianCount { get; }

    public int Count => _people.Length;

    public Person this[int id] => _people[id];

    /// <summary>Custodian name used in Custodian/AllCustodians values.</summary>
    public string CustodianName(int custodianId) => _custodianNames[custodianId];

    /// <summary>Zipf-distributed custodian id (rank 0 is the most prolific custodian).</summary>
    public int SampleCustodian(Rng rng) => _custodianTable.Sample(rng);

    public int SampleAnyone(Rng rng) => rng.NextInt(_people.Length);

    public int SampleInternal(Rng rng) => rng.NextInt(InternalCount);

    public int SampleExternal(Rng rng) => InternalCount + rng.NextInt(_people.Length - InternalCount);
}
