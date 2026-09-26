using StackInspection.Domain;

namespace StackInspection.Infrastructure.Vision;

/// <summary>Ukuran letterbox YOLOX: resize proporsional lalu tempel di kiri atas kanvas S×S (isi 114).</summary>
public readonly record struct LetterboxGeometry(double Ratio, int ResizedWidth, int ResizedHeight)
{
    public const byte PadValue = 114;

    public static LetterboxGeometry Compute(int width, int height, int size)
    {
        double ratio = Math.Min((double)size / height, (double)size / width);
        return new LetterboxGeometry(ratio, (int)(width * ratio), (int)(height * ratio));
    }
}

/// <summary>
/// Postprocess output YOLOX Carton Trainer: <c>[1, N, 6]</c> = cx, cy, w, h, objectness, classScore dalam
/// piksel kanvas. Skor = objectness × classScore; koordinat asli = koordinat kanvas / ratio; lalu NMS.
/// </summary>
public static class YoloxPostprocessor
{
    public const int Channels = 6;

    public static int AnchorCount(int inputSize) =>
        ((inputSize / 8) * (inputSize / 8)) + ((inputSize / 16) * (inputSize / 16)) + ((inputSize / 32) * (inputSize / 32));

    public static List<CartonBox> Decode(
        ReadOnlySpan<float> output, double ratio, double confidence, double iou, int imageWidth, int imageHeight)
    {
        if (output.Length % Channels != 0)
        {
            throw new ArgumentException($"Panjang output {output.Length} bukan kelipatan {Channels}.", nameof(output));
        }

        List<CartonBox> candidates = [];
        for (int offset = 0; offset < output.Length; offset += Channels)
        {
            double score = output[offset + 4] * (double)output[offset + 5];
            if (score < confidence)
            {
                continue;
            }

            double cx = output[offset];
            double cy = output[offset + 1];
            double w = output[offset + 2];
            double h = output[offset + 3];
            BoundingBox box = new(
                Math.Clamp((cx - (w / 2)) / ratio, 0, imageWidth),
                Math.Clamp((cy - (h / 2)) / ratio, 0, imageHeight),
                Math.Clamp((cx + (w / 2)) / ratio, 0, imageWidth),
                Math.Clamp((cy + (h / 2)) / ratio, 0, imageHeight));
            candidates.Add(new CartonBox(box, score));
        }

        return Nms(candidates, iou);
    }

    /// <summary>NMS 1 class: simpan kotak dengan skor tertinggi, buang yang IoU-nya &gt; threshold.</summary>
    public static List<CartonBox> Nms(IEnumerable<CartonBox> boxes, double iouThreshold)
    {
        List<CartonBox> sorted = [.. boxes.OrderByDescending(b => b.Confidence)];
        List<CartonBox> kept = [];
        bool[] removed = new bool[sorted.Count];
        for (int i = 0; i < sorted.Count; i++)
        {
            if (removed[i])
            {
                continue;
            }

            kept.Add(sorted[i]);
            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (!removed[j] && Iou(sorted[i].Box, sorted[j].Box) > iouThreshold)
                {
                    removed[j] = true;
                }
            }
        }

        return kept;
    }

    public static double Iou(BoundingBox a, BoundingBox b)
    {
        double interW = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        double interH = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        double inter = interW * interH;
        double union = (Math.Max(0, a.Width) * Math.Max(0, a.Height)) + (Math.Max(0, b.Width) * Math.Max(0, b.Height)) - inter;
        return union <= 0 ? 0 : inter / union;
    }
}
