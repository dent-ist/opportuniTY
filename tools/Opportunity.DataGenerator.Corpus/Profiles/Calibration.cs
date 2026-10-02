using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.Profiles;

/// <summary>
/// Internal sampling parameters solved from the profile so that the configured targets (mean family size,
/// email share, duplicate rate, text percentiles) hold in expectation. All values are recorded in the manifest.
/// </summary>
public sealed class Calibration
{
    private const double Z99 = 2.3263478740408408;

    public Calibration(CorpusProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        FamilyProfile f = profile.Families;
        Container = new PowerLawRange(f.ContainerMinChildren, f.ContainerMaxChildren, f.ContainerSizeExponent);
        ThreadLength = new PowerLawRange(profile.Threads.MinLength, profile.Threads.MaxLength, profile.Threads.LengthExponent);

        double emailShare = profile.Mix.EmailShare;
        double targetChildren = f.MeanFamilySize - 1;
        EdocMeanChildren = ((1 - f.EdocContainerShare) * f.EdocMeanEmbeddedChildren) + (f.EdocContainerShare * Container.Mean);
        if (emailShare > 0)
        {
            EmailMeanChildren = (targetChildren - ((1 - emailShare) * EdocMeanChildren)) / emailShare;
            EmailGeometricMean = (EmailMeanChildren - (f.EmailContainerShare * Container.Mean)) / (1 - f.EmailContainerShare);
        }
        else
        {
            EmailMeanChildren = 0;
            EmailGeometricMean = 0;
        }

        var errors = new List<string>();
        if (EmailGeometricMean < 0 || (emailShare == 0 && Math.Abs(EdocMeanChildren - targetChildren) > 1e-9))
        {
            errors.Add($"families.meanFamilySize {f.MeanFamilySize} is unreachable with the configured container/e-doc settings (e-doc families alone average {1 + EdocMeanChildren:0.###}).");
        }

        // Within-family duplicates need >= 2 attachments: P(C >= 2) for a geometric with mean m is (m / (1 + m))^2.
        double emailAtLeastTwo = ((1 - f.EmailContainerShare) * Square(Ratio(EmailGeometricMean))) + f.EmailContainerShare;
        double edocAtLeastTwo = ((1 - f.EdocContainerShare) * Square(Ratio(f.EdocMeanEmbeddedChildren))) + f.EdocContainerShare;
        WithinFamilyDuplicatesPerFamily = profile.Duplicates.WithinFamilyRate * ((emailShare * emailAtLeastTwo) + ((1 - emailShare) * edocAtLeastTwo));

        // D = (E[k]·μ + p) / ((1 + E[k])·μ)  =>  E[k] = (D·μ − p) / ((1 − D)·μ), k = extra whole-family copies.
        double rate = profile.Duplicates.Rate;
        double mu = f.MeanFamilySize;
        MeanExtraCopies = Math.Max(0, ((rate * mu) - WithinFamilyDuplicatesPerFamily) / ((1 - rate) * mu));
        DuplicatedFamilyProbability = MeanExtraCopies / profile.Duplicates.CopiesWhenDuplicatedMean;
        if (DuplicatedFamilyProbability > 1)
        {
            errors.Add("duplicates.rate is unreachable: raise duplicates.copiesWhenDuplicatedMean.");
        }

        if ((rate * mu) < WithinFamilyDuplicatesPerFamily)
        {
            errors.Add("duplicates.withinFamilyRate alone exceeds duplicates.rate.");
        }

        // Email units hold L messages (families); e-doc units hold one family. Solve P(email unit) for the family-level share.
        MeanThreadLength = profile.Threads.SingleMessageShare + ((1 - profile.Threads.SingleMessageShare) * ThreadLength.Mean);
        EmailUnitProbability = emailShare <= 0 ? 0 : emailShare / (emailShare + ((1 - emailShare) * MeanThreadLength));

        TextMu = Math.Log(profile.Text.MedianBytes);
        TextSigma = (Math.Log(profile.Text.P99Bytes) - TextMu) / Z99;

        if (errors.Count > 0)
        {
            throw new ProfileValidationException(errors);
        }
    }

    public PowerLawRange Container { get; }

    public PowerLawRange ThreadLength { get; }

    public double EdocMeanChildren { get; }

    public double EmailMeanChildren { get; }

    public double EmailGeometricMean { get; }

    public double WithinFamilyDuplicatesPerFamily { get; }

    public double MeanExtraCopies { get; }

    public double DuplicatedFamilyProbability { get; }

    public double MeanThreadLength { get; }

    public double EmailUnitProbability { get; }

    public double TextMu { get; }

    public double TextSigma { get; }

    /// <summary>Theoretical text-size quantile of the (unclamped) log-normal.</summary>
    public long TextQuantile(double z) => (long)Math.Round(Math.Exp(TextMu + (TextSigma * z)));

    private static double Ratio(double mean) => mean / (1 + mean);

    private static double Square(double v) => v * v;
}
