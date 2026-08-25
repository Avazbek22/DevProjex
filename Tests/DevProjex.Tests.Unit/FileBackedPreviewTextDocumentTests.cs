using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit;

public sealed class FileBackedPreviewTextDocumentTests
{
    [Fact]
    public void GetLineText_ClampsIndexesAndTrimsLineTerminators()
    {
        using var temp = new TemporaryDirectory();
        using var document = CreateDocument(
            temp,
            ("alpha\r", "alpha"),
            ("", string.Empty),
            ("gamma", "gamma"));

        Assert.Equal(3, document.LineCount);
        Assert.Equal("alpha", document.GetLineText(1));
        Assert.Equal(string.Empty, document.GetLineText(2));
        Assert.Equal("gamma", document.GetLineText(3));
        Assert.Equal("alpha", document.GetLineText(0));
        Assert.Equal("gamma", document.GetLineText(99));
    }

    [Fact]
    public void GetLineRangeText_AndDispose_PreserveContentAndCleanUpStorage()
    {
        using var temp = new TemporaryDirectory();
        var (document, storagePath) = CreateDocumentWithPath(
            temp,
            ("alpha", "alpha"),
            ("", string.Empty),
            ("gamma", "gamma"));

        Assert.Equal("alpha\n\ngamma", document.GetLineRangeText(1, 99));

        document.Dispose();

        Assert.False(File.Exists(storagePath));
        Assert.Throws<ObjectDisposedException>(() => document.GetLineText(1));
    }

    private static FileBackedPreviewTextDocument CreateDocument(
        TemporaryDirectory temp,
        params (string RawLine, string VisibleLine)[] lines)
        => CreateDocumentWithPath(temp, lines).Document;

    private static (FileBackedPreviewTextDocument Document, string StoragePath) CreateDocumentWithPath(
        TemporaryDirectory temp,
        params (string RawLine, string VisibleLine)[] lines)
    {
        var storagePath = Path.Combine(temp.Path, $"{Guid.NewGuid():N}.preview.txt");
        var lineOffsets = new long[lines.Length];
        long currentOffset = 0;

        using (var stream = new FileStream(storagePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            for (var i = 0; i < lines.Length; i++)
            {
                lineOffsets[i] = currentOffset;
                var bytes = Encoding.UTF8.GetBytes(lines[i].RawLine);
                stream.Write(bytes, 0, bytes.Length);
                stream.WriteByte((byte)'\n');
                currentOffset += bytes.Length + 1;
            }
        }

        var document = new FileBackedPreviewTextDocument(
            storagePath,
            lineOffsets,
            currentOffset,
            lines.Max(static line => line.VisibleLine.Length),
            lines.Sum(static line => line.RawLine.Length + 1L));

        return (document, storagePath);
    }
}
