using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Oranda.Tibero.Tests.Driver;

/// <summary>시험용 최소 관리 어셈블리 이미지를 메모리에서 만든다(형식 하나 없이 어셈블리 정의·corlib 참조만).</summary>
internal static class TestAssemblies
{
    internal static byte[] Build(string name, Version version, string corlib = "System.Runtime", bool ilOnly = true, params string[] references)
    {
        var md = new MetadataBuilder();
        md.AddAssembly(md.GetOrAddString(name), version, default, default, 0, AssemblyHashAlgorithm.None);
        md.AddModule(0, md.GetOrAddString(name + ".dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
        md.AddTypeDefinition(
            0,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        md.AddAssemblyReference(md.GetOrAddString(corlib), new Version(4, 0, 0, 0), default, default, 0, default);
        foreach (var reference in references)
        {
            md.AddAssemblyReference(md.GetOrAddString(reference), new Version(1, 0, 0, 0), default, default, 0, default);
        }

        var pe = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
            new MetadataRootBuilder(md),
            new BlobBuilder(),
            strongNameSignatureSize: 0,
            flags: ilOnly ? CorFlags.ILOnly : 0);
        var blob = new BlobBuilder();
        pe.Serialize(blob);
        return blob.ToArray();
    }

    internal static Stream Stream(string name, Version version, string corlib = "System.Runtime", bool ilOnly = true) =>
        new MemoryStream(Build(name, version, corlib, ilOnly));
}

/// <summary>Temp 아래 Guid 폴더. 지울 때 잠긴 파일이 있어도 넘어간다.</summary>
internal sealed class TempFolder : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oranda-tbdrv-" + Guid.NewGuid().ToString("N"));

    internal TempFolder() => Directory.CreateDirectory(Path);

    internal string File(string relative, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 올려 둔 파일이 잠겨 있으면 Temp 에 남는다.
        }
    }
}
