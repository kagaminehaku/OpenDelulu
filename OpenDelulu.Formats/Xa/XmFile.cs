using OpenDelulu.Formats.IO;

namespace OpenDelulu.Formats.Xa;

/// <summary>
/// .xm: breast / swimsuit-top / bottom deformation that follows a body animation (onNN_CC = body type NN,
/// cut CC; sb3_0520). The data is the .xa morph section with a different field order: names come first.
/// Index sets are named defo_mune_L/R/A, defo_bura01/02_L/R/A, defo_pantu01/02_F/B/A; a keyframe is named
/// "set_frame" (e.g. "0_98") and a clip on mesh o01_P_Body / o01_P_Bura01... lists, per animation frame,
/// which keyframe to blend to. Verified on all 18 files:
/// <code>
/// int32 version (0)
/// int32 sets;      per set: name, byte, int32 n, uint16 meshIndices[n], uint16 morphIndices[n]
/// int32 keyframes; per keyframe: name, int32 n, Vector3 positions[n], Vector3 normals[n]
/// int32 clips;     per clip: 4 bytes (1), mesh name, clip name, int32 refs,
///                  refs x (int32 frame, byte, byte, keyframe name)
/// </code>
/// </summary>
public sealed class XmFile
{
    public int Version { get; init; }
    public required XaMorphSection Morphs { get; init; }

    public static XmFile Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data, writable: false), ShiftJis.Encoding);
        int version = r.ReadInt32();
        var section = new XaMorphSection();

        int sets = r.ReadInt32();
        for (int i = 0; i < sets; i++)
        {
            var set = new XaMorphIndexSet { Name = r.ReadName(), Unknown1 = r.ReadExactBytes(1) };
            int count = ReadCount(r);
            set.MeshIndices = ReadUInt16s(r, count);
            set.MorphIndices = ReadUInt16s(r, count);
            section.IndexSets.Add(set);
        }

        int keyframes = ReadCount(r);
        for (int i = 0; i < keyframes; i++)
        {
            string name = r.ReadName();
            int count = ReadCount(r);
            var keyframe = new XaMorphKeyframe { Name = name, Positions = new System.Numerics.Vector3[count], Normals = new System.Numerics.Vector3[count] };
            for (int j = 0; j < count; j++)
                keyframe.Positions[j] = r.ReadVector3();
            for (int j = 0; j < count; j++)
                keyframe.Normals[j] = r.ReadVector3();
            section.Keyframes.Add(keyframe);
        }

        int clips = ReadCount(r);
        for (int i = 0; i < clips; i++)
        {
            var unknown = r.ReadExactBytes(4);
            var clip = new XaMorphClip { MeshName = r.ReadName(), Name = r.ReadName(), Unknown1 = unknown };
            int refs = ReadCount(r);
            for (int j = 0; j < refs; j++)
            {
                clip.KeyframeRefs.Add(new XaMorphKeyframeRef
                {
                    Index = r.ReadInt32(),
                    Unknown1 = r.ReadExactBytes(1),
                    Unknown2 = r.ReadExactBytes(1),
                    Name = r.ReadName(),
                });
            }
            section.Clips.Add(clip);
        }

        if (r.BaseStream.Position != data.Length)
            throw new InvalidDataException($".xm: {data.Length - r.BaseStream.Position} unparsed bytes.");
        return new XmFile { Version = version, Morphs = section };
    }

    static int ReadCount(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count is < 0 or > 1_000_000)
            throw new InvalidDataException($".xm: implausible count {count}.");
        return count;
    }

    static ushort[] ReadUInt16s(BinaryReader r, int count)
    {
        var values = new ushort[count];
        for (int i = 0; i < count; i++)
            values[i] = r.ReadUInt16();
        return values;
    }
}
