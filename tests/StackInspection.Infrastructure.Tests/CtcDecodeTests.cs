using Microsoft.ML.OnnxRuntime.Tensors;
using StackInspection.Infrastructure.Vision;

namespace StackInspection.Infrastructure.Tests;

public class CtcDecodeTests
{
    private static readonly string[] Characters = ["", "6", "5", "9", "0", " "];

    [Fact]
    public void Greedy_MergesRepeatsDropsBlank_AndAveragesChosenProbabilities()
    {
        // langkah: 6 6 blank 5 5 blank 5 → "655" (5 sesudah blank dihitung lagi)
        int[] best = [1, 1, 0, 2, 2, 0, 2];
        float[] scores = [0.9f, 0.8f, 0.99f, 0.7f, 0.6f, 0.95f, 0.5f];
        DenseTensor<float> probs = new([1, best.Length, Characters.Length]);
        for (int t = 0; t < best.Length; t++)
        {
            for (int k = 0; k < Characters.Length; k++)
            {
                probs[0, t, k] = k == best[t] ? scores[t] : (1 - scores[t]) / Characters.Length;
            }
        }

        (string text, double confidence) = CtcDecode.Greedy(probs, 0, Characters);

        Assert.Equal("655", text);
        Assert.Equal((0.9 + 0.7 + 0.5) / 3, confidence, 5);
    }

    [Fact]
    public void Greedy_OnlyBlank_IsEmptyWithZeroConfidence()
    {
        DenseTensor<float> probs = new([1, 3, Characters.Length]);
        for (int t = 0; t < 3; t++)
        {
            probs[0, t, 0] = 1;
        }

        Assert.Equal((string.Empty, 0.0), CtcDecode.Greedy(probs, 0, Characters));
    }
}
