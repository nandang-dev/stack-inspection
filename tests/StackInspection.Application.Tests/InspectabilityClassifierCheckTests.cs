using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Application.Exceptions;
using StackInspection.Application.Inspectability;

namespace StackInspection.Application.Tests;

public sealed class InspectabilityClassifierCheckTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class StubClassifier(double pReject) : IPhotoClassifier
    {
        public int Calls { get; private set; }

        public Task<double> RejectProbabilityAsync(VisionImage image, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(pReject);
        }
    }

    private static InspectabilityClassifierCheck Check(IPhotoClassifier classifier, string model = "fast-reject-v3", double threshold = 0.7565) =>
        new(classifier, Options.Create(new PhotoOptions { ClassifierModel = model, ClassifierRejectThreshold = threshold }),
            NullLogger<InspectabilityClassifierCheck>.Instance);

    [Theory]
    [InlineData(0.7565)]
    [InlineData(0.95)]
    public async Task AtOrAboveThreshold_IsFastRejected(double pReject)
    {
        PhotoNotInspectableException error = await Assert.ThrowsAsync<PhotoNotInspectableException>(
            () => Check(new StubClassifier(pReject)).CheckAsync(new StubImage(3000, 4000), None));

        Assert.Equal(PhotoRejectReasons.FastRejected, error.Reason);
    }

    [Fact]
    public async Task BelowThreshold_Passes()
    {
        await Check(new StubClassifier(0.7564)).CheckAsync(new StubImage(3000, 4000), None);
    }

    [Fact]
    public async Task WithoutModel_ClassifierIsNotCalled()
    {
        StubClassifier classifier = new(0.99);

        await Check(classifier, model: "").CheckAsync(new StubImage(3000, 4000), None);

        Assert.Equal(0, classifier.Calls);
    }

    [Fact]
    public async Task RunsAfterResolution_AndBeforeDetection()
    {
        StubClassifier classifier = new(0.99);
        IOptions<PhotoOptions> photo = Options.Create(new PhotoOptions { ClassifierModel = "fast-reject-v3" });
        PhotoInspectabilityService service = new(
        [
            new CameraOverlayCheck(new StubSpotter(), photo),
            new InspectabilityClassifierCheck(classifier, photo, NullLogger<InspectabilityClassifierCheck>.Instance),
            new ResolutionCheck(photo),
        ]);

        PhotoNotInspectableException lowRes = await Assert.ThrowsAsync<PhotoNotInspectableException>(
            () => service.EnsureInspectableAsync(new StubImage(800, 600), None));
        Assert.Equal((PhotoRejectReasons.ResolutionTooLow, 0), (lowRes.Reason, classifier.Calls));

        PhotoNotInspectableException rejected = await Assert.ThrowsAsync<PhotoNotInspectableException>(
            () => service.EnsureInspectableAsync(new StubImage(3000, 4000), None));
        Assert.Equal((PhotoRejectReasons.FastRejected, 1), (rejected.Reason, classifier.Calls));
    }
}
