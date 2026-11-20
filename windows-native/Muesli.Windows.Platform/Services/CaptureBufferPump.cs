using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Muesli.Windows.Services;

/// <summary>
/// MAC-35 (macOS e8f1d950, 8ca757e5, 7df9cc39): capture callbacks do no processing. The callback copies
/// each buffer into one of a fixed set of preallocated slots and returns; one serial worker writes the
/// file and computes levels and live PCM, in arrival order.
///
/// <list type="bullet">
/// <item>Admission is checked per buffer at the producer. A buffer is accepted once it's queued; every
/// accepted buffer is consumed by <see cref="DrainAsync"/>, unless <see cref="Discard"/> came first.</item>
/// <item>The first stop request decides: draining that has begun can't be undone by a later discard,
/// so an early shutdown never drops queued audio; a discard closes delivery at once.</item>
/// <item>Slots go back to the pool whether or not their buffer was consumed.</item>
/// <item>Offers after a stop request (stale callbacks from a stopped device) are refused.</item>
/// </list>
/// </summary>
public sealed class CaptureBufferPump
{
    private const int Open = 0, Draining = 1, Discarded = 2;

    private readonly Channel<(byte[] Slot, int Count)> _queue;
    private readonly ConcurrentBag<byte[]> _free = [];
    private readonly Action<byte[], int> _consume;
    private readonly Action<Exception> _faulted;
    private readonly Task _worker;
    private int _state;
    private int _failed;
    private long _dropped;

    /// <param name="consume">Runs on the worker, one buffer at a time, in arrival order.</param>
    /// <param name="faulted">Called once if <paramref name="consume"/> throws; later buffers are skipped.</param>
    /// <param name="slots">How many buffers may wait; at 10-100 ms per WASAPI packet, 256 is several seconds.</param>
    public CaptureBufferPump(Action<byte[], int> consume, Action<Exception> faulted, int slots = 256, int slotBytes = 32 * 1024)
    {
        _consume = consume;
        _faulted = faulted;
        for (var i = 0; i < slots; i++) _free.Add(new byte[slotBytes]);
        _queue = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(slots) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _worker = Task.Run(WorkAsync);
    }

    /// <summary>Buffers refused because every slot was waiting; the worker fell behind.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Capture-thread side: copy and queue. False when stopped, full, or empty.</summary>
    public bool Offer(byte[] buffer, int count)
    {
        if (count <= 0 || Volatile.Read(ref _state) != Open) return false;
        if (!_free.TryTake(out var slot))
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }
        // A packet larger than a slot (a long device period) gets its own slot, which then stays in the pool.
        if (slot.Length < count) slot = new byte[count];
        Buffer.BlockCopy(buffer, 0, slot, 0, count);
        if (_queue.Writer.TryWrite((slot, count))) return true;
        _free.Add(slot);
        return false;
    }

    /// <summary>Stop accepting, then consume everything already accepted.</summary>
    public Task DrainAsync()
    {
        Interlocked.CompareExchange(ref _state, Draining, Open);
        _queue.Writer.TryComplete();
        return _worker;
    }

    /// <summary>Stop accepting and drop what's queued, unless a drain already began.</summary>
    public void Discard()
    {
        Interlocked.CompareExchange(ref _state, Discarded, Open);
        _queue.Writer.TryComplete();
    }

    private async Task WorkAsync()
    {
        await foreach (var (slot, count) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Volatile.Read(ref _state) != Discarded && Volatile.Read(ref _failed) == 0)
            {
                try
                {
                    _consume(slot, count);
                }
                catch (Exception error)
                {
                    if (Interlocked.Exchange(ref _failed, 1) == 0) _faulted(error);
                }
            }
            _free.Add(slot);
        }
    }
}
