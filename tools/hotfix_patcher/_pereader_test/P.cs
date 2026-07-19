using System;
using System.IO;
using System.Reflection.PortableExecutable;
class T {
  static void Main(string[] a) {
    foreach (var p in a) {
      try {
        using var fs = File.OpenRead(p);
        using var pe = new PEReader(fs);
        var mr = pe.GetMetadataReader();
        Console.WriteLine(Path.GetFileName(p) + " OK rows TypeRef=" + mr.GetTableRowCount(System.Reflection.Metadata.Ecma335.TableIndex.TypeRef));
      } catch (Exception ex) {
        Console.WriteLine(Path.GetFileName(p) + " FAIL " + ex.GetType().Name + ": " + ex.Message);
      }
    }
  }
}
