namespace KlarWin.Services;

public sealed class MetricHistory
{
    private readonly double[] _buffer;
    private int _count;
    private int _index;

    public MetricHistory(int capacity = 120) =>
        _buffer = new double[Math.Max(2, capacity)];

    public int Count => _count;

    public void Add(double value)
    {
        _buffer[_index] = value;
        _index = (_index + 1) % _buffer.Length;
        _count = Math.Min(_count + 1, _buffer.Length);
    }

    public IReadOnlyList<double> Snapshot()
    {
        if (_count == 0) return [];

        var result = new double[_count];
        var start = _count < _buffer.Length ? 0 : _index;
        for (var i = 0; i < _count; i++)
        {
            result[i] = _buffer[(start + i) % _buffer.Length];
        }

        return result;
    }
}
