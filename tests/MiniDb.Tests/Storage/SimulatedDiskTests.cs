using MiniDb.Tests.Simulation;

namespace MiniDb.Tests.Storage;

/// <summary>
/// The crash tests are only as good as the disk they run on, so the disk's own behaviour is
/// pinned down first.
/// </summary>
public class SimulatedDiskTests
{
    private static readonly byte[] Old = Enumerable.Repeat((byte)0x11, 4096).ToArray();
    private static readonly byte[] New = Enumerable.Repeat((byte)0x22, 4096).ToArray();

    [Fact]
    public void Reads_see_writes_that_were_never_flushed()
    {
        var disk = new SimulatedDisk();
        using var file = disk.Open("f");
        file.Write(0, New);
        var read = new byte[4096];
        Assert.Equal(4096, file.Read(0, read));
        Assert.Equal(New, read);
    }

    [Fact]
    public void Only_flushed_writes_survive_when_nothing_else_does()
    {
        var disk = DiskWithOldFlushedAndNewPending();
        Assert.Equal(Old, disk.Reboot(Survival.Nothing).Contents("f"));
    }

    [Fact]
    public void Every_write_survives_when_the_disk_keeps_everything()
    {
        var disk = DiskWithOldFlushedAndNewPending();
        Assert.Equal(New, disk.Reboot(Survival.Everything).Contents("f"));
    }

    [Fact]
    public void A_torn_write_leaves_each_sector_wholly_old_or_wholly_new_and_sometimes_a_mix()
    {
        var disk = DiskWithOldFlushedAndNewPending();
        bool sawMix = false;
        for (int seed = 0; seed < 100; seed++)
        {
            var contents = disk.Reboot(Survival.RandomSectors, seed).Contents("f");
            var sectors = contents.Chunk(SimulatedDisk.SectorSize).ToList();
            Assert.All(sectors, sector => Assert.True(sector.All(b => b == 0x11) || sector.All(b => b == 0x22)));
            sawMix |= sectors.Any(s => s[0] == 0x11) && sectors.Any(s => s[0] == 0x22);
        }
        Assert.True(sawMix, "no seed tore the write");
    }

    [Fact]
    public void The_crash_replaces_the_chosen_operation_and_nothing_runs_after_it()
    {
        var disk = new SimulatedDisk(crashAt: 2);
        var file = disk.Open("f");
        file.Write(0, Old);   // operation 0
        file.Flush();         // operation 1
        Assert.Throws<SimulatedCrashException>(() => file.Write(0, New));
        Assert.True(disk.Crashed);
        Assert.Throws<SimulatedCrashException>(() => file.Read(0, new byte[1]));
        Assert.Throws<SimulatedCrashException>(() => file.Flush());
        Assert.Equal(2, disk.Operations);
    }

    [Fact]
    public void A_write_under_way_when_the_power_goes_may_still_reach_the_disk()
    {
        var disk = new SimulatedDisk(crashAt: 2);
        var file = disk.Open("f");
        file.Write(0, Old);
        file.Flush();
        Assert.Throws<SimulatedCrashException>(() => file.Write(0, New));

        Assert.Equal(Old, disk.Reboot(Survival.Nothing).Contents("f"));
        Assert.Equal(New, disk.Reboot(Survival.Everything).Contents("f"));
    }

    [Fact]
    public void A_flush_that_the_crash_cuts_off_makes_nothing_durable()
    {
        var disk = new SimulatedDisk(crashAt: 1);
        var file = disk.Open("f");
        file.Write(0, New);
        Assert.Throws<SimulatedCrashException>(() => file.Flush());
        Assert.Empty(disk.Reboot(Survival.Nothing).Contents("f"));
    }

    [Fact]
    public void A_file_that_is_open_cannot_be_opened_again()
    {
        var disk = new SimulatedDisk();
        using var file = disk.Open("f");
        Assert.Throws<IOException>(() => disk.Open("f"));
    }

    [Fact]
    public void A_shortened_file_does_not_get_its_old_bytes_back_when_it_grows()
    {
        var disk = new SimulatedDisk();
        using var file = disk.Open("f");
        file.Write(0, New);
        file.SetLength(100);
        file.SetLength(4096);
        var read = new byte[4096];
        file.Read(0, read);
        Assert.All(read[100..], b => Assert.Equal(0, b));
    }

    private static SimulatedDisk DiskWithOldFlushedAndNewPending()
    {
        var disk = new SimulatedDisk();
        using var file = disk.Open("f");
        file.Write(0, Old);
        file.Flush();
        file.Write(0, New);
        return disk;
    }
}
