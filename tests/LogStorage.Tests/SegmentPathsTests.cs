using LogStorage.Storage;
using Xunit;

namespace LogStorage.Tests;

public class SegmentPathsTests
{
    [Fact]
    public void DayDirectory_UsaElArbolAnioMesDia()
    {
        var path = SegmentPaths.DayDirectory("/logs", new DateOnly(2026, 8, 5));
        Assert.Equal(Path.Combine("/logs", "2026", "08", "05"), path);
    }

    [Fact]
    public void SegmentFile_NumeraConCincoDigitos()
    {
        var path = SegmentPaths.SegmentFile("/logs/2026/08/05", 7);
        Assert.EndsWith("segment-00007.lsz", path);
    }

    [Theory]
    [InlineData("segment-00001.lsz", true, 1)]
    [InlineData("segment-12345.lsz", true, 12345)]
    [InlineData("segment-00001.bin", false, 0)]
    [InlineData("otracosa.lsz", false, 0)]
    [InlineData("segment-abc.lsz", false, 0)]
    public void TryParseSegmentId_ReconoceSoloLosNombresValidos(string fileName, bool expected, int expectedId)
    {
        Assert.Equal(expected, SegmentPaths.TryParseSegmentId(fileName, out var id));
        if (expected) Assert.Equal(expectedId, id);
    }

    [Fact]
    public void DayOf_DevuelveElDiaUtc()
    {
        var ticks = new DateTime(2026, 8, 20, 23, 59, 59, DateTimeKind.Utc).Ticks;
        Assert.Equal(new DateOnly(2026, 8, 20), SegmentPaths.DayOf(ticks));
    }

    [Fact]
    public void NextSegmentId_EmpiezaEnUnoYSigueDesdeElMayor()
    {
        using var temp = new TempStore();
        var directory = SegmentPaths.DayDirectory(temp.Root, new DateOnly(2026, 8, 20));

        Assert.Equal(1, SegmentPaths.NextSegmentId(directory));

        Directory.CreateDirectory(directory);
        File.WriteAllText(SegmentPaths.SegmentFile(directory, 1), "x");
        File.WriteAllText(SegmentPaths.SegmentFile(directory, 9), "x");

        Assert.Equal(10, SegmentPaths.NextSegmentId(directory));
    }

    [Fact]
    public void EnumerateDays_DevuelveOrdenadoYFiltraPorRango()
    {
        using var temp = new TempStore();
        DateOnly[] days = [new(2025, 12, 31), new(2026, 1, 1), new(2026, 8, 20), new(2027, 3, 3)];
        foreach (var day in days) Directory.CreateDirectory(SegmentPaths.DayDirectory(temp.Root, day));

        var all = SegmentPaths.EnumerateDays(temp.Root);
        Assert.Equal(days, all.Select(x => x.Day).ToArray());

        var filtered = SegmentPaths.EnumerateDays(temp.Root, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 8, 20)], filtered.Select(x => x.Day).ToArray());
    }

    [Fact]
    public void EnumerateDays_IgnoraCarpetasQueNoSonFechas()
    {
        using var temp = new TempStore();
        Directory.CreateDirectory(Path.Combine(temp.Root, "archive", "2026", "08"));
        Directory.CreateDirectory(Path.Combine(temp.Root, "basura"));
        Directory.CreateDirectory(SegmentPaths.DayDirectory(temp.Root, new DateOnly(2026, 8, 20)));

        var days = SegmentPaths.EnumerateDays(temp.Root);

        Assert.Single(days);
        Assert.Equal(new DateOnly(2026, 8, 20), days[0].Day);
    }

    [Fact]
    public void EnumerateDays_SobreCarpetaInexistente_DevuelveVacio()
        => Assert.Empty(SegmentPaths.EnumerateDays(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
}
