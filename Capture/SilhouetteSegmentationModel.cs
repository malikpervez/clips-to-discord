using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ClipsToDiscord;

internal interface ISilhouetteFrameSegmenter : IDisposable
{
    int InputWidth { get; }
    int InputHeight { get; }
    void Segment(ReadOnlySpan<byte> rgb24, Span<byte> alpha8);
}

/// <summary>
/// Offline person segmentation for a completed Reaction Camera layer. The model is deliberately
/// small and the session lives only in the isolated rendition worker; the capture host never loads
/// ONNX Runtime or competes for inference resources while it is recording a frame.
/// </summary>
internal sealed class OnnxSilhouetteFrameSegmenter : ISilhouetteFrameSegmenter
{
    internal const int ModelWidth = 512;
    internal const int ModelHeight = 288;
    internal const string ModelId = "modnet.photographic-portrait-matting.v1";
    internal const string ModelSha256 =
        "07c308cf0fc7e6e8b2065a12ed7fc07e1de8febb7dc7839d7b7f15dd66584df9";
    internal const string SourceModelSha256 =
        "07c308cf0fc7e6e8b2065a12ed7fc07e1de8febb7dc7839d7b7f15dd66584df9";
    internal const string RelativeModelPath = "models\\modnet-photographic.onnx";

    private const float ForegroundThreshold = 0.45f;
    private const float SoftEdgeLow = 0.10f;
    private const float SoftEdgeHigh = 0.90f;
    private const float CurrentFrameWeight = 0.72f;
    private const int EdgeDilationRadius = 2;

    private readonly string _modelPath;
    private InferenceSession _session;
    private string _inputName;
    private bool _usingDirectMl;
    private readonly float[] _input = new float[ModelWidth * ModelHeight * 3];
    private readonly float[] _smoothed = new float[ModelWidth * ModelHeight];
    private readonly int[] _labels = new int[ModelWidth * ModelHeight];
    private readonly int[] _queue = new int[ModelWidth * ModelHeight];
    private bool _hasPreviousFrame;
    private bool _disposed;

    internal OnnxSilhouetteFrameSegmenter(string modelPath, bool preferDirectMl = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        var normalizedPath = Path.GetFullPath(modelPath);
        if (!File.Exists(normalizedPath))
        {
            throw new FileNotFoundException(
                "ClipCord's local silhouette model is unavailable.",
                normalizedPath);
        }
        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(normalizedPath)))
            .ToLowerInvariant();
        if (!actualHash.Equals(ModelSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "ClipCord's local silhouette model failed its integrity check.");
        }

        _modelPath = normalizedPath;
        _session = CreateSession(normalizedPath, preferDirectMl, out _usingDirectMl);
        try
        {
            _inputName = ValidateSessionContract(_session);
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    private static string ValidateSessionContract(InferenceSession session)
    {
        var input = session.InputMetadata.Single();
        if (!input.Value.IsTensor ||
            input.Value.ElementType != typeof(float) ||
            !HasExpectedInputDimensions(input.Value.Dimensions))
        {
            throw new InvalidDataException(
                "ClipCord's local silhouette model has an unexpected input contract.");
        }
        var output = session.OutputMetadata.Single();
        if (!output.Value.IsTensor ||
            output.Value.ElementType != typeof(float) ||
            !HasExpectedOutputDimensions(output.Value.Dimensions))
        {
            throw new InvalidDataException(
                "ClipCord's local silhouette model has an unexpected output contract.");
        }
        return input.Key;
    }

    internal static string GetPackagedModelPath(string? applicationBase = null) =>
        Path.Combine(
            Path.GetFullPath(applicationBase ?? AppContext.BaseDirectory),
            RelativeModelPath);

    public int InputWidth => ModelWidth;
    public int InputHeight => ModelHeight;

    public void Segment(ReadOnlySpan<byte> rgb24, Span<byte> alpha8)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (rgb24.Length != _input.Length || alpha8.Length != _smoothed.Length)
        {
            throw new ArgumentException("The silhouette frame has an unexpected size.");
        }

        var pixelCount = ModelWidth * ModelHeight;
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var source = pixel * 3;
            _input[pixel] = rgb24[source] / 127.5f - 1f;
            _input[pixelCount + pixel] = rgb24[source + 1] / 127.5f - 1f;
            _input[pixelCount * 2 + pixel] = rgb24[source + 2] / 127.5f - 1f;
        }

        try
        {
            RunInference();
        }
        catch (OnnxRuntimeException exception) when (_usingDirectMl)
        {
            Log.Error(
                "DirectML stopped during offline silhouette processing; ClipCord will retry this clip with the local CPU fallback.",
                exception);
            _session.Dispose();
            _session = CreateSession(_modelPath, preferDirectMl: false, out _usingDirectMl);
            _inputName = ValidateSessionContract(_session);
            RunInference();
        }
        _hasPreviousFrame = true;
        KeepLargestForegroundComponent(alpha8);
    }

    private void RunInference()
    {
        var tensor = new DenseTensor<float>(_input, [1, 3, ModelHeight, ModelWidth]);
        using var results = _session.Run(
            [NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var output = results.Single().AsTensor<float>();
        if (output.Length != _smoothed.Length)
        {
            throw new InvalidDataException(
                "ClipCord's local silhouette model returned an unexpected mask.");
        }

        for (var index = 0; index < _smoothed.Length; index++)
        {
            var probability = Math.Clamp(output.GetValue(index), 0f, 1f);
            _smoothed[index] = _hasPreviousFrame
                ? probability * CurrentFrameWeight +
                  _smoothed[index] * (1f - CurrentFrameWeight)
                : probability;
        }
    }

    private static InferenceSession CreateSession(
        string modelPath,
        bool preferDirectMl,
        out bool usingDirectMl)
    {
        usingDirectMl = false;
        if (preferDirectMl)
        {
            try
            {
                using var directMl = CreateSessionOptions();
                directMl.EnableMemoryPattern = false;
                directMl.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                directMl.AppendExecutionProvider_DML(0);
                var session = new InferenceSession(modelPath, directMl);
                usingDirectMl = true;
                return session;
            }
            catch (Exception exception) when (
                exception is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
            {
                Log.Error(
                    "DirectML was unavailable for offline silhouette processing; ClipCord will use the local CPU fallback.",
                    exception);
            }
        }

        using var cpu = CreateSessionOptions();
        cpu.IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 2);
        cpu.InterOpNumThreads = 1;
        return new InferenceSession(modelPath, cpu);
    }

    private static SessionOptions CreateSessionOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
    };

    private static bool HasExpectedInputDimensions(IReadOnlyList<int> dimensions) =>
        dimensions.Count == 4 &&
        IsOneOrDynamic(dimensions[0]) &&
        dimensions[1] == 3 &&
        IsDimensionOrDynamic(dimensions[2], ModelHeight) &&
        IsDimensionOrDynamic(dimensions[3], ModelWidth);

    private static bool HasExpectedOutputDimensions(IReadOnlyList<int> dimensions) =>
        dimensions.Count == 4 &&
        IsOneOrDynamic(dimensions[0]) &&
        dimensions[1] == 1 &&
        IsDimensionOrDynamic(dimensions[2], ModelHeight) &&
        IsDimensionOrDynamic(dimensions[3], ModelWidth);

    private static bool IsOneOrDynamic(int value) => value is 1 or -1;

    private static bool IsDimensionOrDynamic(int value, int expected) =>
        value == expected || value == -1;

    private void KeepLargestForegroundComponent(Span<byte> alpha8)
    {
        Array.Fill(_labels, -1);
        var largestLabel = -1;
        var largestSize = 0;
        var nextLabel = 0;

        for (var index = 0; index < _smoothed.Length; index++)
        {
            if (_labels[index] >= 0 || _smoothed[index] < ForegroundThreshold) continue;
            var head = 0;
            var tail = 0;
            _queue[tail++] = index;
            _labels[index] = nextLabel;
            while (head < tail)
            {
                var current = _queue[head++];
                var x = current % ModelWidth;
                var y = current / ModelWidth;
                Visit(x - 1, y, nextLabel, ref tail);
                Visit(x + 1, y, nextLabel, ref tail);
                Visit(x, y - 1, nextLabel, ref tail);
                Visit(x, y + 1, nextLabel, ref tail);
            }
            if (tail > largestSize)
            {
                largestLabel = nextLabel;
                largestSize = tail;
            }
            nextLabel++;
        }

        alpha8.Clear();
        if (largestLabel < 0) return;
        for (var y = 0; y < ModelHeight; y++)
        {
            for (var x = 0; x < ModelWidth; x++)
            {
                var index = y * ModelWidth + x;
                if (!TouchesComponent(x, y, largestLabel)) continue;
                var normalized = Math.Clamp(
                    (_smoothed[index] - SoftEdgeLow) / (SoftEdgeHigh - SoftEdgeLow),
                    0f,
                    1f);
                var smooth = normalized * normalized * (3f - 2f * normalized);
                alpha8[index] = (byte)Math.Clamp(
                    (int)Math.Round(smooth * byte.MaxValue),
                    0,
                    byte.MaxValue);
            }
        }
    }

    private void Visit(int x, int y, int label, ref int tail)
    {
        if ((uint)x >= ModelWidth || (uint)y >= ModelHeight) return;
        var index = y * ModelWidth + x;
        if (_labels[index] >= 0 || _smoothed[index] < ForegroundThreshold) return;
        _labels[index] = label;
        _queue[tail++] = index;
    }

    private bool TouchesComponent(int x, int y, int label)
    {
        var left = Math.Max(0, x - EdgeDilationRadius);
        var right = Math.Min(ModelWidth - 1, x + EdgeDilationRadius);
        var top = Math.Max(0, y - EdgeDilationRadius);
        var bottom = Math.Min(ModelHeight - 1, y + EdgeDilationRadius);
        for (var candidateY = top; candidateY <= bottom; candidateY++)
        {
            for (var candidateX = left; candidateX <= right; candidateX++)
            {
                if (_labels[candidateY * ModelWidth + candidateX] == label) return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
