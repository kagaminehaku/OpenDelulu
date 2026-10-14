using System.Numerics;
using OpenDelulu.Formats.IO;

namespace OpenDelulu.Formats.Scene;

/// <summary>D3DLIGHTTYPE.</summary>
public enum LightType
{
    Point = 1,
    Spot = 2,
    Directional = 3,
}

/// <summary>A Direct3D 9 light (D3DLIGHT9) plus its node matrix.</summary>
public sealed class XlLight
{
    public string Name { get; init; } = "";
    /// <summary>Raw flag bytes before the light: int32 (1), byte 0xFF, byte space (1 = world, 2 = camera-relative).</summary>
    public byte[] Flags { get; init; } = [];
    public LightType Type { get; init; }
    public Vector4 Diffuse { get; init; }
    public Vector4 Specular { get; init; }
    public Vector4 Ambient { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Direction { get; init; }
    public float Range { get; init; }
    public float Falloff { get; init; }
    public float Attenuation0 { get; init; }
    public float Attenuation1 { get; init; }
    public float Attenuation2 { get; init; }
    /// <summary>Spot inner/outer cone angles (radians).</summary>
    public float Theta { get; init; }
    public float Phi { get; init; }
    public Matrix4x4 Matrix { get; init; }
    public byte[]? Trailer { get; init; }

    /// <summary>Lights named Camera01..04 (flag 2) follow the camera: the character lighting rigs bo00_*.xl.</summary>
    public bool CameraRelative => Flags.Length >= 6 && Flags[5] == 2;

    public override string ToString() => $"{Name} {Type}";
}

/// <summary>
/// .xl: the lights of a stage (mpNN_*.xl) or the camera-relative character lighting (bo00_*.xl).
/// Verified on all 35 files:
/// <code>
/// int32  version (1 or 2)
/// byte   0, D3DCOLOR scene ambient (B, G, R, A)
/// int32  0                      (version 2 only)
/// byte   light count
/// per light: name (int32 length + NOT-encoded), 6 flag bytes, D3DLIGHT9 (104 bytes), matrix (64 bytes),
///            int32 (version 2 only)
/// </code>
/// </summary>
public sealed class XlFile
{
    public int Version { get; init; }
    /// <summary>Scene ambient color, 0..1 RGBA.</summary>
    public Vector4 SceneAmbient { get; init; }
    public IReadOnlyList<XlLight> Lights { get; init; } = [];

    public static XlFile Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data, writable: false), ShiftJis.Encoding);
        int version = r.ReadInt32();
        r.ReadByte();
        byte b = r.ReadByte(), g = r.ReadByte(), red = r.ReadByte(), a = r.ReadByte();
        if (version >= 2)
            r.ReadInt32();
        int count = r.ReadByte();
        var lights = new List<XlLight>(count);
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadName();
            var flags = r.ReadExactBytes(6);
            var type = (LightType)r.ReadInt32();
            var diffuse = r.ReadColor4();
            var specular = r.ReadColor4();
            var ambient = r.ReadColor4();
            var position = r.ReadVector3();
            var direction = r.ReadVector3();
            lights.Add(new XlLight
            {
                Name = name,
                Flags = flags,
                Type = type,
                Diffuse = diffuse,
                Specular = specular,
                Ambient = ambient,
                Position = position,
                Direction = direction,
                Range = r.ReadSingle(),
                Falloff = r.ReadSingle(),
                Attenuation0 = r.ReadSingle(),
                Attenuation1 = r.ReadSingle(),
                Attenuation2 = r.ReadSingle(),
                Theta = r.ReadSingle(),
                Phi = r.ReadSingle(),
                Matrix = r.ReadMatrix(),
                Trailer = version >= 2 ? r.ReadExactBytes(4) : null,
            });
        }
        if (r.BaseStream.Position != data.Length)
            throw new InvalidDataException($".xl: {data.Length - r.BaseStream.Position} unparsed bytes.");
        return new XlFile
        {
            Version = version,
            SceneAmbient = new Vector4(red, g, b, a) / 255f,
            Lights = lights,
        };
    }
}
