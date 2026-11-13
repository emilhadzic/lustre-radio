using LustreRadio.Core.Licensing;
using Xunit;

namespace LustreRadio.Core.Tests;

/// <summary>Testovi T-LIC-01 do T-LIC-04 na nivou kapije (spec. deo 19).</summary>
public class LicenseGateTests
{
    private const string Hash = "ab12cd34";

    private static LicenseRecord Complete(LicenseStatus status = LicenseStatus.UnderReview) => new()
    {
        ContentHash = Hash,
        Status = status,
        Source = "Pixabay Music",
        SourceUrl = "https://example.com/pesma",
        Author = "Autor",
        LicenseType = "CC BY 4.0",
        DownloadedOn = new DateOnly(2026, 11, 13),
        CommercialUse = Answer.Yes,
        PublicPerformance = Answer.Yes,
        Modifications = Answer.Yes,
        AttributionRequired = Answer.Yes,
        HasProof = true,
    };

    [Fact]
    public void NoLicenseRecord_IsNotAllowed()
    {
        Assert.False(LicenseGate.CanBroadcast(null, Hash).Allowed);
    }

    [Theory]
    [InlineData(LicenseStatus.Imported)]
    [InlineData(LicenseStatus.UnderReview)]
    [InlineData(LicenseStatus.Rejected)]
    [InlineData(LicenseStatus.Recheck)]
    public void AnyStatusExceptVerified_IsNotAllowed(LicenseStatus status)
    {
        // cak i kad su sva polja popunjena i fajl isti
        Assert.False(LicenseGate.CanBroadcast(Complete(status), Hash).Allowed);
    }

    [Fact]
    public void Verified_WithSameFile_IsAllowed()
    {
        Assert.True(LicenseGate.CanBroadcast(Complete(LicenseStatus.Verified), Hash).Allowed);
        Assert.True(LicenseGate.CanBroadcast(Complete(LicenseStatus.Verified), Hash.ToUpperInvariant()).Allowed);
    }

    [Theory]
    [InlineData("drugi-hes")]
    [InlineData("")]
    [InlineData(null)]
    public void Verified_ButDifferentOrUnknownFile_IsNotAllowed(string? actualHash)
    {
        Assert.False(LicenseGate.CanBroadcast(Complete(LicenseStatus.Verified), actualHash).Allowed);
    }

    [Fact] // T-LIC-02
    public void ChangedFile_MovesVerifiedToRecheck_AndBlocksBroadcast()
    {
        var verified = Complete(LicenseStatus.Verified);
        var changed = LicenseGate.OnContentChanged(verified, "novi-hes");

        Assert.Equal(LicenseStatus.Recheck, changed.Status);
        Assert.False(LicenseGate.CanBroadcast(changed, "novi-hes").Allowed);
        Assert.Same(verified, LicenseGate.OnContentChanged(verified, Hash));
    }

    [Theory] // T-LIC-03
    [InlineData("CC BY-NC 4.0", true)]
    [InlineData("CC BY-ND 4.0", true)]
    [InlineData("CC BY-NC-ND 4.0", true)]
    [InlineData("cc by-nc-sa 3.0", true)]
    [InlineData("CC BY 4.0", false)]
    [InlineData("CC BY-SA 4.0", false)]
    [InlineData("CC0 1.0", false)]
    [InlineData("Pixabay Content License", false)]
    [InlineData("AI TTS", false)]
    public void NonCommercialAndNoDerivatives_AreAutoRejected(string licenseType, bool rejected)
    {
        var license = Complete() with { LicenseType = licenseType };
        Assert.Equal(rejected, LicenseGate.IsAutoRejected(license));
        Assert.Equal(rejected, LicenseGate.VerificationBlockers(license).Count > 0);
        Assert.False(LicenseGate.IsAutoRejected(license with { HasWrittenPermission = true }));
    }

    [Fact]
    public void CompleteRecord_CanBeVerified_AndThenBroadcast()
    {
        var at = new DateTimeOffset(2026, 11, 13, 20, 0, 0, TimeSpan.FromHours(1));
        var verified = LicenseGate.Verify(Complete(), "admin", at);

        Assert.Equal(LicenseStatus.Verified, verified.Status);
        Assert.Equal("admin", verified.VerifiedBy);
        Assert.Equal(at, verified.VerifiedAt);
        Assert.True(LicenseGate.CanBroadcast(verified, Hash).Allowed);
    }

    [Fact]
    public void FreshlyImportedRecord_CannotJumpToVerified()
    {
        Assert.Throws<InvalidOperationException>(() => LicenseGate.Verify(Complete(LicenseStatus.Imported), "admin", DateTimeOffset.Now));
    }

    [Fact]
    public void EveryMissingOrUnclearField_BlocksVerification()
    {
        var complete = Complete();
        LicenseRecord[] incomplete =
        [
            complete with { Source = null },
            complete with { SourceUrl = " " },
            complete with { Author = null },
            complete with { LicenseType = null },
            complete with { DownloadedOn = null },
            complete with { HasProof = false },
            complete with { CommercialUse = Answer.Unknown },
            complete with { CommercialUse = Answer.No },
            complete with { PublicPerformance = Answer.Unknown },
            complete with { PublicPerformance = Answer.No },
            complete with { Modifications = Answer.Unknown },
            complete with { Modifications = Answer.No },
            complete with { AttributionRequired = Answer.Unknown },
        ];

        Assert.Empty(LicenseGate.VerificationBlockers(complete));
        foreach (var record in incomplete)
        {
            Assert.Single(LicenseGate.VerificationBlockers(record));
            Assert.Throws<InvalidOperationException>(() => LicenseGate.Verify(record, "admin", DateTimeOffset.Now));
        }

        Assert.Throws<ArgumentException>(() => LicenseGate.Verify(complete, " ", DateTimeOffset.Now));
    }

    [Fact] // T-LIC-04
    public void PreparedTransition_NeedsBothTracksAllowed_AndModificationsConfirmed()
    {
        var ok = Complete(LicenseStatus.Verified);
        var noModifications = ok with { Modifications = Answer.Unknown };
        var notVerified = Complete(LicenseStatus.Recheck);

        Assert.True(LicenseGate.CanPrepareTransition(ok, Hash, ok, Hash).Allowed);
        Assert.False(LicenseGate.CanPrepareTransition(ok, Hash, noModifications, Hash).Allowed);
        Assert.False(LicenseGate.CanPrepareTransition(noModifications, Hash, ok, Hash).Allowed);
        Assert.False(LicenseGate.CanPrepareTransition(ok, Hash, notVerified, Hash).Allowed);
        Assert.False(LicenseGate.CanPrepareTransition(ok, Hash, ok, "drugi-hes").Allowed);
        Assert.False(LicenseGate.CanPrepareTransition(ok, Hash, null, Hash).Allowed);
    }

    [Fact]
    public void StatusTransitions_FollowTheSpecification()
    {
        (LicenseStatus From, LicenseStatus To)[] allowed =
        [
            (LicenseStatus.Imported, LicenseStatus.UnderReview),
            (LicenseStatus.UnderReview, LicenseStatus.Verified),
            (LicenseStatus.UnderReview, LicenseStatus.Rejected),
            (LicenseStatus.Verified, LicenseStatus.Recheck),
            (LicenseStatus.Verified, LicenseStatus.Rejected),
            (LicenseStatus.Recheck, LicenseStatus.UnderReview),
            (LicenseStatus.Recheck, LicenseStatus.Rejected),
            (LicenseStatus.Rejected, LicenseStatus.UnderReview),
        ];

        foreach (var from in Enum.GetValues<LicenseStatus>())
        {
            foreach (var to in Enum.GetValues<LicenseStatus>())
            {
                Assert.Equal(allowed.Contains((from, to)), LicenseGate.IsTransitionAllowed(from, to));
            }
        }
    }
}
