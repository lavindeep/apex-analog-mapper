namespace ApexMapper.Input.Abstractions.Pipeline;

public sealed class SpscRingBuffer<T> where T : struct
{
    private readonly T[] _buffer;
    private readonly int _mask;
    private long _head;
    private long _tail;
    private long _droppedCount;

    public SpscRingBuffer(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "Capacity must be a power of two and at least 2.");
        }

        _buffer = new T[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    public long DroppedCount => Volatile.Read(ref _droppedCount);

    public bool IsEmpty => Volatile.Read(ref _head) == Volatile.Read(ref _tail);

    public int Count => (int)(Volatile.Read(ref _head) - Volatile.Read(ref _tail));

    public bool TryEnqueue(in T item)
    {
        var head = Volatile.Read(ref _head);
        var tail = Volatile.Read(ref _tail);
        if (head - tail >= _buffer.Length)
        {
            Interlocked.Increment(ref _droppedCount);
            return false;
        }

        _buffer[head & _mask] = item;
        Volatile.Write(ref _head, head + 1);
        return true;
    }

    public bool TryDequeue(out T item)
    {
        var tail = Volatile.Read(ref _tail);
        var head = Volatile.Read(ref _head);
        if (head == tail)
        {
            item = default;
            return false;
        }

        item = _buffer[tail & _mask];
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }
}
