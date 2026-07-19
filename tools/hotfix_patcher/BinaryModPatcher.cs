using Mono.Cecil;

using Mono.Cecil.Cil;



namespace CrossgateMod.Patcher;



internal static class BinaryModPatcher

{

    private const uint IsMsgLogFieldToken = 0x0A000437;



    public static int Run(string[] args)

    {

        string? source = null;

        string? output = null;

        var loadExternalMod = false;

        var safeOnly = false;

        var withTick = false;



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

                case "--load-mod":

                    loadExternalMod = true;

                    break;

                case "--safe-only":

                    safeOnly = true;

                    break;

                case "--with-tick":

                    withTick = true;

                    break;

            }

        }



        if (string.IsNullOrWhiteSpace(source))

        {

            Console.WriteLine("用法: HotfixPatcher binary-mod --hotfix <orig> [--output <out>] [--safe-only] [--with-tick] [--load-mod]");

            Console.WriteLine("  默认内联：IsMsgLog + [CrossgateMod] Init OK（#US 末尾追加）");

            Console.WriteLine("  --with-tick：额外每 10 秒打 [CrossgateMod] tick（实验）");

            Console.WriteLine("  --safe-only：仅 IsMsgLog，不写字符串堆");

            Console.WriteLine("  --load-mod：实验性 Assembly.Load（勿用）");

            return 1;

        }



        output ??= source;

        if (loadExternalMod)

        {

            PatchExternalModLoader(source, output);

        }

        else if (safeOnly)

        {

            PatchSafeMsgLog(source, output);

        }

        else

        {

            PatchInlineMod(source, output, withTick);

        }



        Console.WriteLine("[OK] 二进制 Mod 补丁完成: " + output);

        return 0;

    }



    private static void PatchSafeMsgLog(string sourcePath, string outputPath)

    {

        var origBytes = File.ReadAllBytes(sourcePath);

        var data = (byte[])origBytes.Clone();



        var hotfixDataDir = Path.GetDirectoryName(sourcePath)!;

        var resolver = new HotfixAssemblyResolver(hotfixDataDir);



        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters

        {

            AssemblyResolver = resolver,

            InMemory = true,

        });



        var gameManager = asm.MainModule.Types.First(t => t.Name == "GameManagerHotfix");

        var start = gameManager.Methods.First(m => m.Name == "Start" && m.HasBody);

        var isMsgLog = (FieldReference)asm.MainModule.LookupToken((int)IsMsgLogFieldToken);



        var injectBefore = start.Body.Instructions.First(i =>

            i.OpCode == OpCodes.Callvirt

            && i.Operand is MethodReference called

            && called.Name == "Dispatch")

            ?? throw new InvalidOperationException("未找到 ScreenTransiton.Dispatch");



        var startSnapshot = IlSerializer.Serialize(start.Body);

        var il = start.Body.GetILProcessor();

        il.InsertBefore(injectBefore, il.Create(OpCodes.Ldc_I4_1));

        il.InsertBefore(injectBefore, il.Create(OpCodes.Stsfld, isMsgLog));



        IlSerializer.RecalculateOffsets(start.Body);

        start.Body.MaxStackSize = Math.Max(start.Body.MaxStackSize, (short)8);

        var startBody = IlSerializer.Serialize(start.Body);

        BinaryPeWriter.ReplaceMethodBody(data, start.RVA, startSnapshot, startBody);



        if (data.Length != origBytes.Length)

        {

            throw new InvalidOperationException("二进制 Mod 补丁改变了文件大小");

        }



        File.WriteAllBytes(outputPath, data);

        Console.WriteLine("[MODE] 安全模式：仅 IsMsgLog=true");

        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");

    }



    private static void PatchInlineMod(string sourcePath, string outputPath, bool withTick)

    {

        var origBytes = File.ReadAllBytes(sourcePath);

        var data = (byte[])origBytes.Clone();



        var hotfixDataDir = Path.GetDirectoryName(sourcePath)!;

        var resolver = new HotfixAssemblyResolver(hotfixDataDir);



        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters

        {

            AssemblyResolver = resolver,

            InMemory = true,

        });



        var module = asm.MainModule;

        var gameManager = module.Types.First(t => t.Name == "GameManagerHotfix");

        var start = gameManager.Methods.First(m => m.Name == "Start" && m.HasBody);

        var isMsgLog = (FieldReference)module.LookupToken((int)IsMsgLogFieldToken);



        var userStrings = UserStringHeap.FromPe(data);

        MethodReference? tickMethodRef = null;



        if (withTick)

        {

            var hotfixEntry = module.Types.First(t => t.Name == "HotfixEntry");

            var tickMethod = hotfixEntry.Methods.First(m => m.Name == "OnApplicationQuit" && m.HasBody);

            var tickSnapshot = IlSerializer.Serialize(tickMethod.Body);

            var tickBody = ModInlineIlBuilder.BuildTickBody(tickMethod, module, userStrings);

            BinaryPeWriter.ReplaceMethodBody(data, tickMethod.RVA, tickSnapshot, tickBody);

            tickMethodRef = module.ImportReference(tickMethod);

        }



        var startSnapshot = IlSerializer.Serialize(start.Body);

        var startBody = ModInlineIlBuilder.BuildStartLogBody(

            start.Body,

            isMsgLog,

            userStrings,

            module,

            withTick,

            tickMethodRef);

        BinaryPeWriter.ReplaceMethodBody(data, start.RVA, startSnapshot, startBody);



        if (data.Length != origBytes.Length)

        {

            throw new InvalidOperationException("二进制 Mod 补丁改变了文件大小");

        }



        if (!userStrings.HasString(ModInlineIlBuilder.InitLogMessage))

        {

            throw new InvalidOperationException("未在 #US 堆追加 [CrossgateMod] Init OK");

        }



        if (withTick && !userStrings.HasString(ModInlineIlBuilder.TickLogMessage))

        {

            throw new InvalidOperationException("未在 #US 堆追加 [CrossgateMod] tick");

        }



        File.WriteAllBytes(outputPath, data);



        Console.WriteLine(withTick

            ? "[MODE] 内联 Mod：IsMsgLog + Init + 10s tick"

            : "[MODE] 内联 Mod：IsMsgLog + Init OK");

        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");

    }



    private static void PatchExternalModLoader(string sourcePath, string outputPath)

    {

        var origBytes = File.ReadAllBytes(sourcePath);

        var data = (byte[])origBytes.Clone();



        var hotfixDataDir = Path.GetDirectoryName(sourcePath)!;

        var resolver = new HotfixAssemblyResolver(hotfixDataDir);



        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters

        {

            AssemblyResolver = resolver,

            InMemory = true,

        });



        var hotfixEntry = asm.MainModule.Types.First(t => t.Name == "HotfixEntry");

        var loaderMethod = hotfixEntry.Methods.First(m => m.Name == "OnApplicationPause" && m.HasBody);

        var gameManager = asm.MainModule.Types.First(t => t.Name == "GameManagerHotfix");

        var start = gameManager.Methods.First(m => m.Name == "Start" && m.HasBody);



        var userStrings = UserStringHeap.FromPe(data);



        var loaderSnapshot = IlSerializer.Serialize(loaderMethod.Body);

        var loaderBody = ModLoaderIlBuilder.BuildLoaderBody(loaderMethod, asm.MainModule, userStrings);

        BinaryPeWriter.ReplaceMethodBody(data, loaderMethod.RVA, loaderSnapshot, loaderBody);



        var startSnapshot = IlSerializer.Serialize(start.Body);

        var startBody = ModLoaderIlBuilder.BuildStartHookBody(

            start.Body,

            asm.MainModule.ImportReference(loaderMethod),

            userStrings);

        BinaryPeWriter.ReplaceMethodBody(data, start.RVA, startSnapshot, startBody);



        if (data.Length != origBytes.Length)

        {

            throw new InvalidOperationException("二进制 Mod 补丁改变了文件大小");

        }



        File.WriteAllBytes(outputPath, data);

        Console.WriteLine("[MODE] 实验模式：反射加载 CrossgateMod.dll");

        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");

    }

}


