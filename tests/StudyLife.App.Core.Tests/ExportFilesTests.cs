using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public sealed class ExportFilesTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "exportfiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    [Fact]
    public void Writes_the_file_into_the_exports_folder_of_the_cache()
    {
        var path = ExportFiles.Write(_cache, "studyatrium-export-20261116.json", [1, 2, 3]);

        Assert.Equal(Path.Combine(Path.GetFullPath(_cache), ExportFiles.FolderName, "studyatrium-export-20261116.json"), path);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_new_download_removes_the_previous_one()
    {
        var codes = ExportFiles.Write(_cache, "studyatrium-recovery-codes.txt", "AAAA-1111"u8.ToArray());

        var export = ExportFiles.Write(_cache, "studyatrium-export-20261116.json", [1]);

        Assert.False(File.Exists(codes));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(export)!));
    }

    [Fact]
    public void The_same_name_twice_overwrites()
    {
        ExportFiles.Write(_cache, "codes.txt", [1]);
        var path = ExportFiles.Write(_cache, "codes.txt", [2]);

        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/inner.txt")]
    [InlineData("..")]
    public void A_name_that_is_not_a_plain_file_name_is_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => ExportFiles.Write(_cache, name, [1]));
        Assert.False(File.Exists(Path.Combine(_cache, "escape.txt")));
    }

    [Fact]
    public void Leaves_the_rest_of_the_cache_alone()
    {
        Directory.CreateDirectory(_cache);
        var other = Path.Combine(_cache, "keep.bin");
        File.WriteAllBytes(other, [9]);

        ExportFiles.Write(_cache, "a.txt", [1]);
        ExportFiles.Write(_cache, "b.txt", [1]);

        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Clear_removes_the_export_and_the_recovery_codes()
    {
        var export = ExportFiles.Write(_cache, "studyatrium-export-20261116.json", [1]);
        var folder = Path.GetDirectoryName(export)!;
        File.WriteAllBytes(Path.Combine(folder, "studyatrium-recovery-codes.txt"), "AAAA-1111"u8.ToArray());

        ExportFiles.Clear(_cache);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Clear_leaves_the_rest_of_the_cache_alone()
    {
        Directory.CreateDirectory(_cache);
        var other = Path.Combine(_cache, "keep.bin");
        File.WriteAllBytes(other, [9]);
        ExportFiles.Write(_cache, "a.txt", [1]);

        ExportFiles.Clear(_cache);

        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Clear_without_an_exports_folder_does_nothing()
    {
        ExportFiles.Clear(_cache);

        Assert.False(Directory.Exists(_cache));
    }
}