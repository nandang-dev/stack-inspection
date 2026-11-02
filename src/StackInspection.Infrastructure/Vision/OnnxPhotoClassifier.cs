using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;

namespace StackInspection.Infrastructure.Vision;

/// <summary>
/// Classifier kelayakan foto (MobileNetV3 hasil <c>fast_reject_classifier.py</c> Carton Trainer, ONNX Runtime CPU).
/// Model: <c>&lt;Vision:ModelsDirectory&gt;/&lt;Photo:ClassifierModel&gt;/*.onnx</c>, session dibuat saat
/// pertama kali dipakai. Preprocess harus sama dengan training: resize langsung (tanpa letterbox, INTER_AREA)
/// ke <see cref="InputSize"/>, BGR → RGB, /255, NCHW; normalisasi ImageNet ada di dalam model.
/// Output <c>[1, 2]</c> softmax, index 1 = p(tolak).
/// </summary>
public sealed class OnnxPhotoClassifier : IPhotoClassifier, IDisposable
{
    public const int InputSize = 320;
    private const int RejectIndex = 1;

    private readonly Lazy<InferenceSession> _session;

    public OnnxPhotoClassifier(IOptions<VisionOptions> vision, IOptions<PhotoOptions> photo)
    {
        ArgumentNullException.ThrowIfNull(vision);
        ArgumentNullException.ThrowIfNull(photo);
        string folder = Path.Combine(OnnxCartonDetector.ResolvePath(vision.Value.ModelsDirectory), photo.Value.ClassifierModel);
        int threads = vision.Value.Threads;
        _session = new Lazy<InferenceSession>(() => CreateSession(folder, threads));
    }

    private static InferenceSession CreateSession(string folder, int threads)
    {
        string? path = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.onnx").Order(StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (path is null)
        {
            throw new FileNotFoundException($"Model classifier (.onnx) tidak ditemukan di folder: {folder}.", folder);
        }

        SessionOptions sessionOptions = new()
        {
            IntraOpNumThreads = threads,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        return new InferenceSession(path, sessionOptions);
    }

    public Task<double> RejectProbabilityAsync(VisionImage image, CancellationToken cancellationToken)
    {
        OpenCvImage source = OpenCvImage.From(image);
        InferenceSession session = _session.Value;

        using Mat resized = new();
        Cv2.Resize(source.Mat, resized, new Size(InputSize, InputSize), 0, 0, InterpolationFlags.Area);
        using Mat rgb = new();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);
        float[] input = ToChwUnit(rgb);

        DenseTensor<float> tensor = new(input, [1, 3, InputSize, InputSize]);
        cancellationToken.ThrowIfCancellationRequested();
        string inputName = session.InputMetadata.Keys.First();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        Tensor<float> output = results[0].AsTensor<float>();
        return Task.FromResult((double)output[0, RejectIndex]);
    }

    /// <summary>HWC uint8 RGB → CHW float32 skala 0–1.</summary>
    private static float[] ToChwUnit(Mat rgb)
    {
        const int plane = InputSize * InputSize;
        float[] chw = new float[3 * plane];
        Mat[] channels = Cv2.Split(rgb);
        try
        {
            for (int c = 0; c < 3; c++)
            {
                using Mat asFloat = new();
                channels[c].ConvertTo(asFloat, MatType.CV_32FC1, 1.0 / 255);
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

    public void Dispose()
    {
        if (_session.IsValueCreated)
        {
            _session.Value.Dispose();
        }
    }
}
