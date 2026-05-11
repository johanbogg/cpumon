using System;
using System.Drawing;
using System.IO;

if (args.Length != 4
    || !int.TryParse(args[1], out int r)
    || !int.TryParse(args[2], out int g)
    || !int.TryParse(args[3], out int b))
{
    Console.Error.WriteLine("Usage: iconGen <outPath> <R> <G> <B>");
    return 1;
}

string outPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
File.WriteAllBytes(outPath, Th.MakeHexIconBytes(Color.FromArgb(r, g, b)));
Console.WriteLine($"Wrote {outPath} ({new FileInfo(outPath).Length} bytes)");
return 0;
