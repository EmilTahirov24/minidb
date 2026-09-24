using MiniDb.Storage;

namespace MiniDb.Tests.Simulation;

/// <summary>What a disk does with the writes it was never told to keep, when the power goes.</summary>
public enum Survival
{
    /// <summary>Only what was flushed survives.</summary>
    Nothing,

    /// <summary>Every write survives, flushed or not.</summary>
    Everything,

    /// <summary>
    /// Each 512-byte sector of each unflushed write survives or not, independently. This is how
    /// a page gets torn, and how a later write reaches the disk while an earlier one does not.
    /// </summary>
    RandomSectors,
}

/// <summary>The simulated power went at this point; nothing more runs on this disk.</summary>
public sealed class SimulatedCrashException(long operation)
    : Exception($"simulated crash at disk operation {operation}")
{
    public long Operation => operation;
}

/// <summary>
/// A disk in memory that keeps what has been flushed apart from what has only been written, so
/// it can crash at any operation and be rebooted with a chosen part of the unflushed writes.
/// </summary>
/// <remarks>
/// Writes, flushes, length changes, renames and deletes are the operations that count; a crash
/// happens instead of one of them. Creating, renaming and deleting a file take effect at once and
/// durably, as if the directory were always flushed: the database does not rely on it.
/// </remarks>
internal sealed class SimulatedDisk : IStorage
{
    public const int SectorSize = 512;

    private readonly Dictionary<string, FileState> files = [];

    // The database reads and writes from several threads at once; the disk takes one request at a time.
    private readonly object gate = new();
    private readonly long crashAt;

    /// <param name="crashAt">The operation, counted from 0, at which to crash; -1 for never.</param>
    public SimulatedDisk(long crashAt = -1) => this.crashAt = crashAt;

    /// <summary>Operations completed so far.</summary>
    public long Operations { get; private set; }

    public bool Crashed { get; private set; }

    public IStorageFile Open(string name)
    {
        lock (gate)
        {
            return OpenLocked(name);
        }
    }

    private IStorageFile OpenLocked(string name)
    {
        ThrowIfCrashed();
        if (!files.TryGetValue(name, out var state))
        {
            state = new FileState();
            files[name] = state;
        }
        if (state.IsOpen)
        {
            throw new IOException($"{name} is already open");
        }
        state.IsOpen = true;
        return new Handle(this, state);
    }

    public bool Exists(string name)
    {
        lock (gate)
        {
            ThrowIfCrashed();
            return files.ContainsKey(name);
        }
    }

    public void Rename(string from, string to) => Operation(() =>
    {
        if (files.ContainsKey(to))
        {
            throw new IOException($"{to} already exists");
        }
        files[to] = files[from];
        files.Remove(from);
    });

    public void Delete(string name) => Operation(() => files.Remove(name));

    /// <summary>Everything a program would read from the file now, flushed or not.</summary>
    public byte[] Contents(string name)
    {
        lock (gate)
        {
            return files[name].Current.ToArray();
        }
    }

    /// <summary>
    /// The disk a machine restarted now would find: what was flushed, plus whatever unflushed
    /// writes <paramref name="survival"/> lets through, chosen by <paramref name="seed"/>.
    /// </summary>
    /// <param name="crashAt">An operation at which the rebooted disk crashes in turn; -1 for never.</param>
    public SimulatedDisk Reboot(Survival survival, int seed = 0, long crashAt = -1)
    {
        lock (gate)
        {
            return RebootLocked(survival, seed, crashAt);
        }
    }

    private SimulatedDisk RebootLocked(Survival survival, int seed, long crashAt)
    {
        var random = new Random(seed);
        var rebooted = new SimulatedDisk(crashAt);
        foreach (var (name, state) in files)
        {
            var surviving = state.Durable.Clone();
            foreach (var change in state.Pending)
            {
                switch (change)
                {
                    case PendingWrite write:
                        Land(surviving, write, survival, random);
                        break;
                    case PendingLength length when Survives(survival, random):
                        surviving.SetLength(length.Length);
                        break;
                }
            }
            rebooted.files[name] = new FileState { Durable = surviving, Current = surviving.Clone() };
        }
        return rebooted;
    }

    private static void Land(Bytes file, PendingWrite write, Survival survival, Random random)
    {
        long end = write.Offset + write.Data.Length;
        for (long start = write.Offset; start < end;)
        {
            long next = Math.Min(end, (start / SectorSize + 1) * SectorSize);
            if (Survives(survival, random))
            {
                file.Write(start, write.Data.AsSpan((int)(start - write.Offset), (int)(next - start)));
            }
            start = next;
        }
    }

    private static bool Survives(Survival survival, Random random) => survival switch
    {
        Survival.Nothing => false,
        Survival.Everything => true,
        _ => random.Next(2) == 0,
    };

    private void Operation(Action complete, Action? inFlight = null)
    {
        lock (gate)
        {
            OperationLocked(complete, inFlight);
        }
    }

    private void OperationLocked(Action complete, Action? inFlight)
    {
        ThrowIfCrashed();
        if (Operations == crashAt)
        {
            Crashed = true;
            inFlight?.Invoke();
            throw new SimulatedCrashException(Operations);
        }
        complete();
        Operations++;
    }

    private void ThrowIfCrashed()
    {
        if (Crashed)
        {
            throw new SimulatedCrashException(Operations);
        }
    }

    private abstract record Pending;

    private sealed record PendingWrite(long Offset, byte[] Data) : Pending;

    private sealed record PendingLength(long Length) : Pending;

    private sealed class FileState
    {
        /// <summary>The file as of its last flush: what survives any crash.</summary>
        public Bytes Durable { get; set; } = new();

        /// <summary>The file as programs see it: the durable bytes with every pending change applied.</summary>
        public Bytes Current { get; set; } = new();

        /// <summary>Changes since the last flush, in the order they were made.</summary>
        public List<Pending> Pending { get; } = [];

        public bool IsOpen { get; set; }
    }

    private sealed class Handle(SimulatedDisk disk, FileState state) : IStorageFile
    {
        public long Length
        {
            get
            {
                lock (disk.gate)
                {
                    disk.ThrowIfCrashed();
                    return state.Current.Length;
                }
            }
        }

        public int Read(long offset, Span<byte> buffer)
        {
            lock (disk.gate)
            {
                disk.ThrowIfCrashed();
                return state.Current.Read(offset, buffer);
            }
        }

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            var write = new PendingWrite(offset, data.ToArray());
            disk.Operation(
                () =>
                {
                    state.Current.Write(offset, write.Data);
                    state.Pending.Add(write);
                },
                // The power went while this write was on its way: some of it may have landed.
                inFlight: () => state.Pending.Add(write));
        }

        public void SetLength(long length)
        {
            var change = new PendingLength(length);
            disk.Operation(
                () =>
                {
                    state.Current.SetLength(length);
                    state.Pending.Add(change);
                },
                inFlight: () => state.Pending.Add(change));
        }

        public void Flush() => disk.Operation(() =>
        {
            foreach (var change in state.Pending)
            {
                switch (change)
                {
                    case PendingWrite write:
                        state.Durable.Write(write.Offset, write.Data);
                        break;
                    case PendingLength length:
                        state.Durable.SetLength(length.Length);
                        break;
                }
            }
            state.Pending.Clear();
        });

        public void Dispose()
        {
            lock (disk.gate)
            {
                state.IsOpen = false;
            }
        }
    }

    /// <summary>A growable run of bytes, the contents of one version of a file.</summary>
    private sealed class Bytes
    {
        private byte[] data = [];

        public long Length { get; private set; }

        public void Write(long offset, ReadOnlySpan<byte> source)
        {
            long end = offset + source.Length;
            Reserve(end);
            source.CopyTo(data.AsSpan((int)offset));
            Length = Math.Max(Length, end);
        }

        public void SetLength(long length)
        {
            if (length < Length)
            {
                // What is cut off must not come back if the file grows again.
                data.AsSpan((int)length, (int)(Length - length)).Clear();
            }
            else
            {
                Reserve(length);
            }
            Length = length;
        }

        public int Read(long offset, Span<byte> destination)
        {
            if (offset >= Length)
            {
                return 0;
            }
            int count = (int)Math.Min(destination.Length, Length - offset);
            data.AsSpan((int)offset, count).CopyTo(destination);
            return count;
        }

        public Bytes Clone()
        {
            var copy = new Bytes { Length = Length };
            copy.data = data.AsSpan(0, (int)Length).ToArray();
            return copy;
        }

        public byte[] ToArray() => data.AsSpan(0, (int)Length).ToArray();

        private void Reserve(long size)
        {
            if (size > data.Length)
            {
                Array.Resize(ref data, (int)Math.Max(size, data.Length * 2L));
            }
        }
    }
}
