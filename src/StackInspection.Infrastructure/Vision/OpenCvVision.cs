using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Application.Exceptions;
using StackInspection.Domain;

namespace StackInspection.Infrastructure.Vision;

/// <summary>Gambar BGR OpenCV (orientasi EXIF sudah diterapkan oleh imdecode).</summary>
public sealed class OpenCvImage : VisionImage
{
    public OpenCvImage(Mat mat)
        : base(mat?.Width ?? 0, mat?.Height ?? 0)
    {
        ArgumentNullException.ThrowIfNull(mat);
        Mat = mat;
    }

    public Mat Mat { get; }

    public static OpenCvImage From(VisionImage image) =>
        image as OpenCvImage ?? throw new ArgumentException("Vision image bukan OpenCvImage.", nameof(image));

    /// <summary>Crop area (di-clamp ke batas gambar). null jika area terlalu kecil.</summary>
    public Mat? Crop(BoundingBox region, int minSize = 8)
    {
        int x1 = (int)Math.Clamp(Math.Floor(region.X1), 0, Width);
        int y1 = (int)Math.Clamp(Math.Floor(region.Y1), 0, Height);
        int x2 = (int)Math.Clamp(Math.Ceiling(region.X2), 0, Width);
        int y2 = (int)Math.Clamp(Math.Ceiling(region.Y2), 0, Height);
        if (x2 - x1 < minSize || y2 - y1 < minSize)
        {
            return null;
        }

        return new Mat(Mat, new Rect(x1, y1, x2 - x1, y2 - y1));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Mat.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Decode JPG/PNG dengan OpenCV. imdecode menerapkan orientasi EXIF (sama seperti browser dan Carton Trainer).</summary>
public sealed class OpenCvImageDecoder : IImageDecoder
{
    public VisionImage Decode(ReadOnlyMemory<byte> data)
    {
        Mat mat = Cv2.ImDecode(data.Span, ImreadModes.Color);
        if (mat.Empty())
        {
            mat.Dispose();
            throw new InvalidImageException("Gambar tidak bisa dibaca.");
        }

        return new OpenCvImage(mat);
    }
}

/// <summary>Detector kardus YOLOX (ONNX Runtime, CPU). Session dibuat sekali (singleton, thread-safe untuk Run).</summary>
public sealed class OnnxCartonDetector : ICartonDetector, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly VisionOptions _options;

    public OnnxCartonDetector(IOptions<VisionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        string path = ResolvePath(_options.DetectorModelPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Model deteksi tidak ditemukan: {path}. Salin file .onnx hasil export Carton Trainer ke lokasi ini.", path);
        }

        SessionOptions sessionOptions = new()
        {
            IntraOpNumThreads = _options.Threads,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(path, sessionOptions);
        _inputName = _session.InputMetadata.Keys.First();
    }

    public static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    public Task<IReadOnlyList<CartonBox>> DetectAsync(VisionImage image, CancellationToken cancellationToken)
    {
        OpenCvImage source = OpenCvImage.From(image);
        int size = _options.InputSize;
        LetterboxGeometry geometry = LetterboxGeometry.Compute(source.Width, source.Height, size);

        using Mat resized = new();
        Cv2.Resize(source.Mat, resized, new Size(geometry.ResizedWidth, geometry.ResizedHeight), 0, 0, InterpolationFlags.Linear);
        using Mat canvas = new(new Size(size, size), MatType.CV_8UC3, Scalar.All(LetterboxGeometry.PadValue));
        using (Mat target = new(canvas, new Rect(0, 0, geometry.ResizedWidth, geometry.ResizedHeight)))
        {
            resized.CopyTo(target);
        }

        float[] input = ToChwFloat(canvas, size);
        DenseTensor<float> tensor = new(input, [1, 3, size, size]);
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        Tensor<float> output = results[0].AsTensor<float>();
        float[] values = output is DenseTensor<float> dense ? dense.Buffer.ToArray() : [.. output];

        IReadOnlyList<CartonBox> boxes = YoloxPostprocessor.Decode(
            values, geometry.Ratio, _options.ConfidenceThreshold, _options.IouThreshold, source.Width, source.Height);
        return Task.FromResult(boxes);
    }

    /// <summary>HWC uint8 BGR → CHW float32 tanpa normalisasi (0–255), sesuai training YOLOX.</summary>
    private static float[] ToChwFloat(Mat canvas, int size)
    {
        int plane = size * size;
        float[] chw = new float[3 * plane];
        Mat[] channels = Cv2.Split(canvas);
        try
        {
            for (int c = 0; c < 3; c++)
            {
                using Mat asFloat = new();
                channels[c].ConvertTo(asFloat, MatType.CV_32FC1);
                asFloat.GetArray(out float[] data);
                Array.Copy(data, 0, chw, c * plane, plane);
            }
        }
        finally
        {
            foreach (Mat channel in channels)
            {
                channel.Dispose();
            }
        }

        return chw;
    }

    public void Dispose() => _session.Dispose();
}
