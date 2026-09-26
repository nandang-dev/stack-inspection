using StackInspection.Domain;
using StackInspection.Infrastructure.Vision;

namespace StackInspection.Infrastructure.Tests;

/// <summary>
/// Nilai referensi dihitung dari implementasi Python Carton Trainer
/// (carton_trainer/detectors/yolox/preprocess.py: letterbox, decode_predictions).
/// </summary>
public class YoloxPostprocessorTests
{
    [Theory]
    [InlineData(1920, 3413, 0.28127746850278346, 540, 959)] // kasus tepi pembulatan: 959, bukan 960
    [InlineData(1920, 2560, 0.375, 720, 960)]
    [InlineData(4000, 3000, 0.24, 960, 720)]
    public void Letterbox_MatchesPythonReference(int width, int height, double ratio, int resizedWidth, int resizedHeight)
    {
        LetterboxGeometry geometry = LetterboxGeometry.Compute(width, height, 960);

        Assert.Equal(ratio, geometry.Ratio, 12);
        Assert.Equal((resizedWidth, resizedHeight), (geometry.ResizedWidth, geometry.ResizedHeight));
    }

    [Fact]
    public void AnchorCount_For960_Is18900()
    {
        Assert.Equal(18900, YoloxPostprocessor.AnchorCount(960));
        Assert.Equal(3549, YoloxPostprocessor.AnchorCount(416));
    }

    [Fact]
    public void Decode_MatchesPythonReference()
    {
        float[] output =
        [
            50, 50, 20, 20, 0.9f, 0.9f,
            52, 51, 20, 20, 0.8f, 0.9f, // tumpang tindih dengan baris pertama → dibuang NMS
            200, 300, 40, 30, 0.95f, 0.9f,
            10, 10, 4, 4, 0.9f, 0.3f, // skor 0.27 < 0.5 → dibuang
        ];

        List<CartonBox> boxes = YoloxPostprocessor.Decode(output, ratio: 0.5, confidence: 0.5, iou: 0.5, imageWidth: 400, imageHeight: 400);

        Assert.Equal(2, boxes.Count);
        AssertBox(boxes[0], 360, 400, 400, 400, 0.855);
        AssertBox(boxes[1], 80, 80, 120, 120, 0.81);
    }

    [Fact]
    public void Decode_RejectsWrongChannelCount()
    {
        Assert.Throws<ArgumentException>(() => YoloxPostprocessor.Decode(new float[5], 1, 0.5, 0.5, 10, 10));
    }

    [Fact]
    public void Nms_KeepsHighestScoreAndSeparateBoxes()
    {
        CartonBox[] boxes =
        [
            new(new BoundingBox(0, 0, 10, 10), 0.8),
            new(new BoundingBox(1, 1, 10, 10), 0.9),
            new(new BoundingBox(20, 20, 30, 30), 0.7),
        ];

        List<CartonBox> kept = YoloxPostprocessor.Nms(boxes, 0.5);

        Assert.Equal([0.9, 0.7], kept.Select(b => b.Confidence));
    }

    private static void AssertBox(CartonBox box, double x1, double y1, double x2, double y2, double score)
    {
        Assert.Equal(x1, box.Box.X1, 3);
        Assert.Equal(y1, box.Box.Y1, 3);
        Assert.Equal(x2, box.Box.X2, 3);
        Assert.Equal(y2, box.Box.Y2, 3);
        Assert.Equal(score, box.Confidence, 4);
    }
}
