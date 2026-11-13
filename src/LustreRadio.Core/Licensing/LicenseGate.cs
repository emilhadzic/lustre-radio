namespace LustreRadio.Core.Licensing;

public enum LicenseStatus
{
    /// <summary>Tek uvezeno; nista nije provereno.</summary>
    Imported,
    UnderReview,

    /// <summary>Provereno - dozvoljeno. Jedini status sa kojim stavka sme u eter.</summary>
    Verified,
    Rejected,

    /// <summary>Bilo provereno, ali se fajl ili izvor promenio.</summary>
    Recheck,
}

public enum Answer
{
    Unknown,
    Yes,
    No,
}

/// <summary>Evidencija licence jedne pesme ili glasovne poruke (spec. deo 9).</summary>
public sealed record LicenseRecord
{
    /// <summary>SHA-256 fajla za koji licenca vazi.</summary>
    public required string ContentHash { get; init; }

    public LicenseStatus Status { get; init; } = LicenseStatus.Imported;

    public string? Source { get; init; }

    public string? SourceUrl { get; init; }

    public string? Author { get; init; }

    /// <summary>Npr. "CC0 1.0", "CC BY 4.0", "Pixabay Content License", "AI TTS".</summary>
    public string? LicenseType { get; init; }

    public DateOnly? DownloadedOn { get; init; }

    public Answer CommercialUse { get; init; }

    /// <summary>Javno izvodjenje u ugostiteljskom objektu.</summary>
    public Answer PublicPerformance { get; init; }

    /// <summary>Izmene: crossfade, normalizacija, promena tempa, obradjene kopije.</summary>
    public Answer Modifications { get; init; }

    public Answer AttributionRequired { get; init; }

    /// <summary>Sacuvan dokaz (PDF ili snimak ekrana).</summary>
    public bool HasProof { get; init; }

    /// <summary>Posebna pisana dozvola autora (jedini nacin da NC/ND licenca prodje).</summary>
    public bool HasWrittenPermission { get; init; }

    public string? VerifiedBy { get; init; }

    public DateTimeOffset? VerifiedAt { get; init; }
}

public sealed record GateResult(bool Allowed, string Reason)
{
    public static GateResult Yes { get; } = new(true, "Provereno.");

    public static GateResult No(string reason) => new(false, reason);
}

/// <summary>
/// Jedina kapija kroz koju stavka moze u eter (spec. D10). Namerno NEMA parametar
/// "odakle dolazi zahtev": raspored, rucni izbor, rezervna lista i glasovne poruke
/// dobijaju isti odgovor, pa nema puta koji kapiju zaobilazi.
/// </summary>
public static class LicenseGate
{
    /// <param name="license">Evidencija licence ili null ako ne postoji.</param>
    /// <param name="actualContentHash">Hes fajla koji bi se zaista pustio.</param>
    public static GateResult CanBroadcast(LicenseRecord? license, string? actualContentHash)
    {
        if (license is null)
        {
            return GateResult.No("Nema evidencije licence.");
        }

        if (license.Status != LicenseStatus.Verified)
        {
            return GateResult.No($"Status licence je '{license.Status}', a ne 'Verified'.");
        }

        if (string.IsNullOrEmpty(actualContentHash) ||
            !string.Equals(license.ContentHash, actualContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return GateResult.No("Fajl se razlikuje od onog za koji je licenca proverena.");
        }

        return GateResult.Yes;
    }

    /// <summary>Pripremljen prelaz je obradjena kopija: obe pesme moraju u eter i obe moraju dozvoljavati izmene.</summary>
    public static GateResult CanPrepareTransition(LicenseRecord? first, string? firstHash, LicenseRecord? second, string? secondHash)
    {
        foreach (var (license, hash) in new[] { (first, firstHash), (second, secondHash) })
        {
            var result = CanBroadcast(license, hash);
            if (!result.Allowed)
            {
                return result;
            }

            if (license!.Modifications != Answer.Yes)
            {
                return GateResult.No("Licenca ne potvrdjuje da su izmene dozvoljene.");
            }
        }

        return GateResult.Yes;
    }

    /// <summary>NC i ND licence se odbijaju, osim uz pisanu dozvolu autora.</summary>
    public static bool IsAutoRejected(LicenseRecord license)
    {
        ArgumentNullException.ThrowIfNull(license);
        if (license.HasWrittenPermission || string.IsNullOrWhiteSpace(license.LicenseType))
        {
            return false;
        }

        var tokens = license.LicenseType.ToUpperInvariant().Split([' ', '-', '_', '/', '.'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Contains("NC") || tokens.Contains("ND");
    }

    /// <summary>Razlozi zbog kojih stavka JOS NE MOZE da dobije status "Provereno". Prazan spisak = moze.</summary>
    public static IReadOnlyList<string> VerificationBlockers(LicenseRecord license)
    {
        ArgumentNullException.ThrowIfNull(license);
        var blockers = new List<string>();

        void Require(bool ok, string message)
        {
            if (!ok)
            {
                blockers.Add(message);
            }
        }

        Require(!string.IsNullOrWhiteSpace(license.ContentHash), "Nedostaje hes fajla.");
        Require(!string.IsNullOrWhiteSpace(license.Source), "Nedostaje izvor.");
        Require(!string.IsNullOrWhiteSpace(license.SourceUrl), "Nedostaje URL izvora.");
        Require(!string.IsNullOrWhiteSpace(license.Author), "Nedostaje autor.");
        Require(!string.IsNullOrWhiteSpace(license.LicenseType), "Nedostaje tip licence.");
        Require(license.DownloadedOn is not null, "Nedostaje datum preuzimanja.");
        Require(license.HasProof, "Nedostaje dokaz o licenci.");
        Require(license.CommercialUse == Answer.Yes, "Komercijalna upotreba nije potvrdjena.");
        Require(license.PublicPerformance == Answer.Yes, "Javno izvodjenje u lokalu nije potvrdjeno.");
        Require(license.Modifications == Answer.Yes, "Izmene (crossfade, normalizacija) nisu potvrdjene.");
        Require(license.AttributionRequired != Answer.Unknown, "Nije utvrdjeno da li se mora navesti autor.");
        Require(!IsAutoRejected(license), "NC/ND licenca bez pisane dozvole autora.");
        return blockers;
    }

    /// <summary>Dodeljuje status "Provereno". Baca gresku ako postoji ijedna prepreka.</summary>
    public static LicenseRecord Verify(LicenseRecord license, string verifiedBy, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(verifiedBy))
        {
            throw new ArgumentException("Mora se znati ko je proverio.", nameof(verifiedBy));
        }

        if (!IsTransitionAllowed(license.Status, LicenseStatus.Verified))
        {
            throw new InvalidOperationException($"Iz statusa '{license.Status}' se ne moze direktno u 'Verified'.");
        }

        var blockers = VerificationBlockers(license);
        if (blockers.Count > 0)
        {
            throw new InvalidOperationException("Licenca ne moze biti potvrdjena: " + string.Join(" ", blockers));
        }

        return license with { Status = LicenseStatus.Verified, VerifiedBy = verifiedBy, VerifiedAt = at };
    }

    /// <summary>Fajl je promenjen: proverena licenca vise ne vazi za njega.</summary>
    public static LicenseRecord OnContentChanged(LicenseRecord license, string newContentHash)
    {
        ArgumentNullException.ThrowIfNull(license);
        if (string.Equals(license.ContentHash, newContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return license;
        }

        return license with
        {
            ContentHash = newContentHash,
            Status = license.Status == LicenseStatus.Verified ? LicenseStatus.Recheck : license.Status,
        };
    }

    public static bool IsTransitionAllowed(LicenseStatus from, LicenseStatus to) => (from, to) switch
    {
        (LicenseStatus.Imported, LicenseStatus.UnderReview) => true,
        (LicenseStatus.UnderReview, LicenseStatus.Verified) => true,
        (LicenseStatus.UnderReview, LicenseStatus.Rejected) => true,
        (LicenseStatus.Verified, LicenseStatus.Recheck) => true,
        (LicenseStatus.Verified, LicenseStatus.Rejected) => true,
        (LicenseStatus.Recheck, LicenseStatus.UnderReview) => true,
        (LicenseStatus.Recheck, LicenseStatus.Rejected) => true,
        (LicenseStatus.Rejected, LicenseStatus.UnderReview) => true,
        _ => false,
    };
}
