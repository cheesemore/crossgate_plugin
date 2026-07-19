using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>BOSS 挑战面板打开时直接进入「无尽之塔」。</summary>
internal static class BossChallengeTowerTabIlPatcher
{
    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hotfix" when i + 1 < args.Length:
                    source = Path.GetFullPath(args[++i]);
                    break;
                case "--output" when i + 1 < args.Length:
                    output = Path.GetFullPath(args[++i]);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine("用法: HotfixPatcher boss-challenge-tower-tab-patch --hotfix <hotfix> --output <out>");
            return 1;
        }

        output ??= source;
        Apply(source, output);
        Console.WriteLine("[OK] BOSS 挑战无尽之塔 Tab 补丁已写入: " + output);
        return 0;
    }

    public static void Apply(string sourcePath, string outputPath)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);

        var data = (byte[])origBytes.Clone();
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var panel = asm.MainModule.Types.First(t => t.Name == "BOSSChallengePanel");
        var onShow = panel.Methods.First(m => m.Name == "OnShow" && m.HasBody);
        var tab2Field = panel.Fields.First(f => f.Name == "m_Tog_Tab_2");
        var tab1Handler = panel.Methods.First(m => m.Name == "OnValueChangeTab1" && m.HasBody);
        var snapshot = ReadMethodBodyFromPe(origBytes, onShow.RVA);

        if (HasTowerTabPatch(snapshot, tab1Handler))
        {
            Console.WriteLine("[SKIP] BOSSChallengePanel.OnShow 已含无尽之塔 Tab 补丁");
            return;
        }

        var newBody = (byte[])snapshot.Clone();
        CompactIlBody.ReplaceBossDefaultTab(newBody, tab2Field, tab1Handler);
        BinaryPeWriter.ReplaceMethodBody(data, onShow.RVA, snapshot, newBody);
        Console.WriteLine("[PATCH] BOSSChallengePanel.OnShow -> 默认调用 OnValueChangeTab1(true)");

        HotfixSize.EnsureUnchanged(data, expectedSize);

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    public static bool SniffTargets(string hotfixPath)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        if (!asm.MainModule.Types.Any(t => t.Name == "BOSSChallengePanel"))
        {
            return false;
        }

        var panel = asm.MainModule.Types.First(t => t.Name == "BOSSChallengePanel");
        return panel.Fields.Any(f => f.Name == "m_Tog_Tab_2")
            && panel.Methods.Any(m => m.Name == "OnValueChangeTab1" && m.HasBody);
    }

    public static bool IsTowerTabPatched(string hotfixPath)
    {
        var origBytes = File.ReadAllBytes(hotfixPath);
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var panel = asm.MainModule.Types.FirstOrDefault(t => t.Name == "BOSSChallengePanel");
        if (panel == null)
        {
            return false;
        }

        var onShow = panel.Methods.FirstOrDefault(m => m.Name == "OnShow" && m.HasBody);
        var tab1Handler = panel.Methods.FirstOrDefault(m => m.Name == "OnValueChangeTab1" && m.HasBody);
        if (onShow == null || tab1Handler == null)
        {
            return false;
        }

        var snapshot = ReadMethodBodyFromPe(origBytes, onShow.RVA);
        return HasTowerTabPatch(snapshot, tab1Handler);
    }

    private static bool HasTowerTabPatch(byte[] methodBody, MethodReference tab1Handler)
    {
        return CompactIlBody.ContainsCallMethod(methodBody, tab1Handler);
    }

    private static byte[] ReadMethodBodyFromPe(byte[] pe, int rva)
    {
        var off = PeLayout.RvaToOffset(pe, rva);
        var flags = pe[off];
        if ((flags & 0x3) == 0x2)
        {
            var codeSize = flags >> 2;
            var len = 1 + codeSize;
            var buf = new byte[len];
            Array.Copy(pe, off, buf, 0, len);
            return buf;
        }

        if ((flags & 0x3) == 0x3)
        {
            var codeSize = BitConverter.ToInt32(pe, off + 4);
            var len = 12 + codeSize;
            var buf = new byte[len];
            Array.Copy(pe, off, buf, 0, len);
            return buf;
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2} @ RVA 0x{rva:X}");
    }
}
