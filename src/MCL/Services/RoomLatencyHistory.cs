namespace MCL.Services;

internal sealed class RoomLatencyHistory
{
    internal const int Capacity = 30;
    private readonly Queue<double?> _samples = new(Capacity);

    internal void Add(double? sample)
    {
        if (_samples.Count == Capacity) _samples.Dequeue();
        _samples.Enqueue(sample is { } value && double.IsFinite(value) && value >= 0 ? value : null);
    }

    internal void Clear() => _samples.Clear();

    internal RoomLatencySnapshot Snapshot()
    {
        var samples = _samples.ToArray();
        double total = 0, variation = 0;
        var successful = 0;
        var pairs = 0;
        double? previous = null;
        foreach (var sample in samples)
        {
            if (sample is { } current)
            {
                total += current;
                successful++;
                if (previous is { } last)
                {
                    variation += Math.Abs(current - last);
                    pairs++;
                }
            }
            previous = sample;
        }
        return new(samples, samples.LastOrDefault(), successful > 0 ? total / successful : null,
            pairs > 0 ? variation / pairs : null, samples.Length - successful);
    }
}

internal sealed record RoomLatencySnapshot(double?[] Samples, double? Current, double? Average, double? Jitter, int Missed);
