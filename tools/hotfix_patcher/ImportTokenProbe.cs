using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class ImportTokenProbe
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: HotfixPatcher import-probe <hotfix>");
            return 1;
        }

        var hotfixPath = Path.GetFullPath(args[0]);
        var resolver = new HotfixAssemblyResolver(Path.GetDirectoryName(hotfixPath)!);
        using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var module = asm.MainModule;
        var before = module.GetMemberReferences().Count();
        var loadBytes = BridgeLoaderIlBuilder.ImportFileUtilLoadBytesPublic(module);
        var assemblyLoad = BridgeLoaderIlBuilder.ImportAssemblyLoadPublic(module);
        var after = module.GetMemberReferences().Count();

        Console.WriteLine($"MemberRef count before={before} after={after}");
        Print("LoadBytes", loadBytes);
        Print("Assembly.Load", assemblyLoad);

        var temp = Path.Combine(Path.GetTempPath(), "probe_" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            var probeType = new TypeDefinition("", "<Probe>", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
            var probe = new MethodDefinition("P", MethodAttributes.Static | MethodAttributes.Private, module.TypeSystem.Void);
            probeType.Methods.Add(probe);
            module.Types.Add(probeType);
            probe.Body = new MethodBody(probe);
            var il = probe.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldstr, "x"));
            il.Append(il.Create(OpCodes.Call, loadBytes));
            il.Append(il.Create(OpCodes.Pop));
            il.Append(il.Create(OpCodes.Ret));

            asm.Write(temp);
            var pe = File.ReadAllBytes(temp);
            using var written = AssemblyDefinition.ReadAssembly(temp);
            Console.WriteLine(
                $"Written MemberRef count={written.MainModule.GetMemberReferences().Count()}, orig={before}");
            Console.WriteLine($"Written size={pe.Length}, orig size={new FileInfo(hotfixPath).Length}, contains LoadBytes={Contains(pe, "LoadBytesFromHotfixAssets")}");
            try
            {
                var tok = MemberRefTokenLookup.FindToken(pe, "FileUtil", "LoadBytesFromHotfixAssets");
                Console.WriteLine($"Written PE token LoadBytes=0x{tok:X8}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Written PE lookup: " + ex.Message);
            }

            foreach (var mr in written.MainModule.GetMemberReferences())
            {
                if (mr is MethodReference m && m.Name == "LoadBytesFromHotfixAssets")
                {
                    Console.WriteLine(
                        $"Written Cecil LoadBytes token=0x{m.MetadataToken.ToUInt32():X8} type={m.DeclaringType?.FullName}");
                }
            }
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }

        return 0;
    }

    private static void Print(string label, MethodReference method)
    {
        Console.WriteLine(
            $"{label}: token=0x{method.MetadataToken.ToUInt32():X8} resolved={method.Resolve()?.FullName ?? method.FullName}");
    }

    private static bool Contains(byte[] data, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        for (var i = 0; i <= data.Length - bytes.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < bytes.Length; j++)
            {
                if (data[i + j] != bytes[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return true;
            }
        }

        return false;
    }
}
