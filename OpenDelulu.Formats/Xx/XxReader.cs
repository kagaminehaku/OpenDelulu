using System.Buffers.Binary;
using System.Numerics;
using OpenDelulu.Formats.IO;

namespace OpenDelulu.Formats.Xx;

/// <summary>
/// Parser for .xx models. Layout documented in docs/formats.md; cross-checked against the
/// community SB3Utility/SB3UGS tools.
/// </summary>
public sealed class XxReader
{
    // Float version ids (1.12 .. 1.40) used by pre-versioned files (format -1).
    static readonly HashSet<uint> LegacyIds =
    [
        0x3F8F5C29, 0x3F90A3D7, 0x3F91EB85, 0x3F933333, 0x3F947AE1,
        0x3F95C28F, 0x3F970A3D, 0x3F99999A, 0x3FA66666, 0x3FB33333,
    ];

    readonly BinaryReader _reader;
    readonly int _format;

    XxReader(BinaryReader reader, int format)
    {
        _reader = reader;
        _format = format;
    }

    public static XxModel Read(byte[] data) => Read(new MemoryStream(data, writable: false));

    public static XxModel Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, ShiftJis.Encoding, leaveOpen: true);
        var head = reader.ReadExactBytes(5);
        int format = DetectFormat(head);
        var header = new byte[format >= 1 ? 26 : 21];
        head.CopyTo(header, 0);
        reader.BaseStream.ReadExactly(header.AsSpan(head.Length));

        var model = ReadBody(reader, format, header);
        if (stream.CanSeek && stream.Position != stream.Length)
            throw new InvalidDataException($"xx parsing stopped at 0x{stream.Position:X} of 0x{stream.Length:X}.");
        return model;
    }

    /// <summary>
    /// Reads frames, materials, textures (and the format-2 footer) at the reader's position. Used for .xx files
    /// and for models embedded in other files (.ptcl), which carry their own header.
    /// </summary>
    public static XxModel ReadBody(BinaryReader reader, int format, byte[] header)
    {
        var parser = new XxReader(reader, format);
        var root = parser.ReadFrame(null);

        var materialUnknown = reader.ReadExactBytes(4);
        int materialCount = reader.ReadInt32();
        var materials = new List<XxMaterial>(materialCount);
        for (int i = 0; i < materialCount; i++)
            materials.Add(parser.ReadMaterial());

        int textureCount = reader.ReadInt32();
        var textures = new List<XxTexture>(textureCount);
        for (int i = 0; i < textureCount; i++)
            textures.Add(parser.ReadTexture());

        byte[]? footer = format >= 2 ? reader.ReadExactBytes(10) : null;

        return new XxModel
        {
            Format = format,
            Header = header,
            Root = root,
            MaterialSectionUnknown = materialUnknown,
            Materials = materials,
            Textures = textures,
            Footer = footer,
        };
    }

    public static int DetectFormat(ReadOnlySpan<byte> head)
    {
        if (head[0] >= 0x01 && BinaryPrimitives.ReadInt32LittleEndian(head[1..]) == 0)
            return BinaryPrimitives.ReadInt32LittleEndian(head);
        return LegacyIds.Contains(BinaryPrimitives.ReadUInt32LittleEndian(head)) ? -1 : 0;
    }

    XxFrame ReadFrame(XxFrame? parent)
    {
        var r = _reader;
        var frame = new XxFrame { Parent = parent, Name = r.ReadName() };
        int childCount = r.ReadInt32();
        frame.Matrix = r.ReadMatrix();
        frame.Unknown1 = r.ReadExactBytes(_format >= 7 ? 32 : 16);
        int submeshCount = r.ReadInt32();
        frame.BoundsMin = r.ReadVector3();
        frame.BoundsMax = r.ReadVector3();
        frame.Unknown2 = r.ReadExactBytes(_format >= 7 ? 64 : 16);
        if (_format >= 6)
            frame.Name2 = r.ReadName();

        if (submeshCount > 0)
            frame.Mesh = ReadMesh(submeshCount);

        for (int i = 0; i < childCount; i++)
            frame.Children.Add(ReadFrame(frame));
        return frame;
    }

    XxMesh ReadMesh(int submeshCount)
    {
        var r = _reader;
        var mesh = new XxMesh { NumVector2PerVertex = r.ReadByte() };
        for (int i = 0; i < submeshCount; i++)
            mesh.Submeshes.Add(ReadSubmesh(mesh.NumVector2PerVertex));

        int duplicateCount = r.ReadUInt16();
        mesh.DuplicateVerticesUnknown = r.ReadExactBytes(8);
        for (int i = 0; i < duplicateCount; i++)
            mesh.DuplicateVertices.Add(ReadVertex(out _));

        int boneCount = r.ReadInt32();
        for (int i = 0; i < boneCount; i++)
        {
            mesh.Bones.Add(new XxBone
            {
                Name = r.ReadName(),
                Index = r.ReadInt32(),
                Matrix = r.ReadMatrix(),
            });
        }
        return mesh;
    }

    XxSubmesh ReadSubmesh(byte uvSets)
    {
        var r = _reader;
        var submesh = new XxSubmesh
        {
            Unknown1 = r.ReadExactBytes(_format >= 7 ? 64 : 16),
            MaterialIndex = r.ReadInt32(),
        };

        int indexCount = r.ReadInt32();
        var indices = new ushort[indexCount];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = r.ReadUInt16();
        submesh.Indices = indices;

        int vertexCount = r.ReadInt32();
        var vertices = new XxVertex[vertexCount];
        byte[]? extra = _format >= 4 ? new byte[vertexCount * 20] : null;
        for (int i = 0; i < vertexCount; i++)
        {
            vertices[i] = ReadVertex(out var vertexExtra);
            vertexExtra?.CopyTo(extra!, i * 20);
        }
        submesh.Vertices = vertices;
        submesh.VertexExtra = extra;

        if (_format >= 7)
            submesh.Unknown2 = r.ReadExactBytes(20);

        if (uvSets > 0)
        {
            var uvs = new Vector2[vertexCount][];
            for (int i = 0; i < vertexCount; i++)
            {
                uvs[i] = new Vector2[uvSets];
                for (int k = 0; k < uvSets; k++)
                    uvs[i][k] = r.ReadVector2();
            }
            submesh.ExtraUVs = uvs;
        }

        if (_format >= 2)
            submesh.Unknown3 = r.ReadExactBytes(100);

        if (_format >= 7)
        {
            submesh.Unknown4 = r.ReadExactBytes(284);
            if (_format >= 8)
            {
                // byte format, inverted name, 16 bytes; kept raw for round-tripping.
                long start = r.BaseStream.Position;
                r.ReadByte();
                int nameLength = r.ReadInt32();
                r.BaseStream.Seek(nameLength + 16, SeekOrigin.Current);
                int length = (int)(r.BaseStream.Position - start);
                r.BaseStream.Position = start;
                submesh.Unknown5 = r.ReadExactBytes(length);
            }
        }
        else
        {
            if (_format >= 3)
                submesh.Unknown4 = r.ReadExactBytes(64);
            if (_format >= 5)
                submesh.Unknown5 = r.ReadExactBytes(20);
            if (_format >= 6)
                submesh.Unknown6 = r.ReadExactBytes(28);
        }
        return submesh;
    }

    XxVertex ReadVertex(out byte[]? extra)
    {
        var r = _reader;
        var vertex = new XxVertex
        {
            Index = _format >= 4 ? r.ReadUInt16() : r.ReadInt32(),
            Position = r.ReadVector3(),
            Weights3 = r.ReadVector3(),
            BoneIndices = r.ReadUInt32(),
            Normal = r.ReadVector3(),
            UV = r.ReadVector2(),
        };
        extra = _format >= 4 ? r.ReadExactBytes(20) : null;
        return vertex;
    }

    XxMaterial ReadMaterial()
    {
        var r = _reader;
        var material = new XxMaterial
        {
            Name = r.ReadName(),
            Diffuse = r.ReadColor4(),
            Ambient = r.ReadColor4(),
            Specular = r.ReadColor4(),
            Emissive = r.ReadColor4(),
            Power = r.ReadSingle(),
        };
        var slots = new XxMaterialTexture[4];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = new XxMaterialTexture { Name = r.ReadName(), Unknown1 = r.ReadExactBytes(16) };
        material.Textures = slots;
        material.Unknown1 = r.ReadExactBytes(_format < 0 ? 4 : 88);
        return material;
    }

    XxTexture ReadTexture()
    {
        var r = _reader;
        var texture = new XxTexture
        {
            Name = r.ReadName(),
            Unknown1 = r.ReadExactBytes(4),
            Width = r.ReadInt32(),
            Height = r.ReadInt32(),
            Depth = r.ReadInt32(),
            MipLevels = r.ReadInt32(),
            D3DFormat = r.ReadInt32(),
            ResourceType = r.ReadInt32(),
            ImageFileFormat = r.ReadInt32(),
            Checksum = r.ReadByte(),
        };
        int length = r.ReadInt32();
        texture.ImageData = r.ReadExactBytes(length);
        return texture;
    }
}
