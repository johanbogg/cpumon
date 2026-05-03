using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    static int Main()
    {
        try
        {
            TestReceiveChunkCompletesAndValidatesOffsets();
            TestReceiveChunkReplacesDuplicateTransfer();
            TestLineLengthLimitedStream();
            TestUpdateIntegrity();
            TestSendPacerWakesOnModeChange();
            Console.WriteLine("cpumon smoke tests passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static void TestReceiveChunkCompletesAndValidatesOffsets()
    {
        using var td = new TempDir();
        var uploads = new ConcurrentDictionary<string, FileStream>();
        var first = new FileChunkData
        {
            TransferId = "t1",
            FileName = "sample.bin",
            Offset = 0,
            TotalSize = 6,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("abc")),
            IsLast = false
        };
        Assert(FileBrowserService.ReceiveChunk(first, uploads, td.Path) == "", "first chunk should not complete");

        var bad = new FileChunkData
        {
            TransferId = "t1",
            FileName = "sample.bin",
            Offset = 9,
            TotalSize = 6,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("def")),
            IsLast = true
        };
        Assert(FileBrowserService.ReceiveChunk(bad, uploads, td.Path).StartsWith("Upload error"), "offset mismatch should fail");
        Assert(uploads.ContainsKey("t1"), "offset mismatch should keep stream for caller cleanup/retry decision");

        var last = new FileChunkData
        {
            TransferId = "t1",
            FileName = "sample.bin",
            Offset = 3,
            TotalSize = 6,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("def")),
            IsLast = true
        };
        string result = FileBrowserService.ReceiveChunk(last, uploads, td.Path);
        Assert(result.StartsWith("Upload complete"), "last chunk should complete");
        Assert(!uploads.ContainsKey("t1"), "completed transfer should be removed");
        Assert(File.ReadAllText(Path.Combine(td.Path, "sample.bin")) == "abcdef", "file content should match chunks");
    }

    static void TestReceiveChunkReplacesDuplicateTransfer()
    {
        using var td = new TempDir();
        var uploads = new ConcurrentDictionary<string, FileStream>();
        var chunk = new FileChunkData
        {
            TransferId = "dup",
            FileName = "one.txt",
            Offset = 0,
            TotalSize = 3,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("one")),
            IsLast = false
        };
        FileBrowserService.ReceiveChunk(chunk, uploads, td.Path);
        FileBrowserService.ReceiveChunk(new FileChunkData
        {
            TransferId = "dup",
            FileName = "two.txt",
            Offset = 0,
            TotalSize = 3,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("two")),
            IsLast = true
        }, uploads, td.Path);
        Assert(File.ReadAllText(Path.Combine(td.Path, "two.txt")) == "two", "duplicate transfer should restart cleanly");
    }

    static void TestLineLengthLimitedStream()
    {
        byte[] okBytes = Encoding.UTF8.GetBytes("hello\n");
        using (var ok = new LineLengthLimitedStream(new MemoryStream(okBytes)))
        {
            byte[] buf = new byte[okBytes.Length];
            Assert(ok.Read(buf, 0, buf.Length) == okBytes.Length, "short line should read");
        }

        byte[] longBytes = new byte[LineLengthLimitedStream.MaxLineBytes + 1];
        Array.Fill<byte>(longBytes, (byte)'a');
        using var tooLong = new LineLengthLimitedStream(new MemoryStream(longBytes));
        AssertThrows<IOException>(() => tooLong.Read(new byte[longBytes.Length], 0, longBytes.Length), "long line should throw");
    }

    static void TestUpdateIntegrity()
    {
        using var td = new TempDir();
        string file = Path.Combine(td.Path, "update.bin");
        File.WriteAllText(file, "payload");
        string hash;
        using (var fs = File.OpenRead(file))
            hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(fs));
        Assert(UpdateIntegrity.VerifySha256Base64(file, hash, out var actual), "valid hash should pass");
        Assert(actual == hash, "actual hash should be reported");
        Assert(!UpdateIntegrity.VerifySha256Base64(file, null, out _), "missing hash should fail");
        Assert(!UpdateIntegrity.VerifySha256Base64(file, Convert.ToBase64String(new byte[32]), out _), "wrong hash should fail");
    }

    static void TestSendPacerWakesOnModeChange()
    {
        var pacer = new SendPacer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = Task.Run(() => pacer.Wait(cts.Token));
        Thread.Sleep(100);
        pacer.Mode = "monitor";
        Assert(task.Wait(1000), "mode change should wake pacer");
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cpumon-tests-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}

