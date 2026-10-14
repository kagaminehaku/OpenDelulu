using OpenDelulu.Formats.IO;

namespace OpenDelulu.Formats.Character;

/// <summary>One bone of a swinging hair chain.</summary>
/// <param name="Stiffness">Small spring constant (0.002–0.003 in the data).</param>
/// <param name="Param2">3.0 in every file (likely gravity or iteration count).</param>
/// <param name="Damping">0.45 in every file.</param>
public sealed record KysBone(string Name, float Stiffness, float Param2, float Damping);

/// <summary>
/// .kys: hair physics for one hairstyle (whNN_MM.kys next to whNN_MM.xx). Verified on all 29 files:
/// <code>
/// int32  version (1)
/// int32  chain count
/// per chain: byte bone count; per bone: byte name length, name (NOT-encoded, no terminator), 3 floats
/// </code>
/// Chains run from the root joint to the tip (e.g. o01_J_Kami01_F, o01_J_Kami02_F, o01_N_Kami03_F).
/// </summary>
public sealed class KysFile
{
    public int Version { get; init; }
    public IReadOnlyList<IReadOnlyList<KysBone>> Chains { get; init; } = [];

    public static KysFile Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data, writable: false), ShiftJis.Encoding);
        int version = r.ReadInt32();
        int chainCount = r.ReadInt32();
        var chains = new List<IReadOnlyList<KysBone>>(chainCount);
        for (int c = 0; c < chainCount; c++)
        {
            int boneCount = r.ReadByte();
            var bones = new List<KysBone>(boneCount);
            for (int b = 0; b < boneCount; b++)
                bones.Add(new KysBone(r.ReadFixedName(r.ReadByte()), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            chains.Add(bones);
        }
        if (r.BaseStream.Position != data.Length)
            throw new InvalidDataException($".kys: {data.Length - r.BaseStream.Position} unparsed bytes.");
        return new KysFile { Version = version, Chains = chains };
    }
}
