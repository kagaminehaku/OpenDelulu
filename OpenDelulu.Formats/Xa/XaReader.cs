using System.Numerics;
using OpenDelulu.Formats.IO;

namespace OpenDelulu.Formats.Xa;

/// <summary>
/// Parser for .xa files. Sections are each prefixed by a presence byte (0 = absent).
/// Layout cross-checked against the community SB3Utility/SB3UGS tools.
/// </summary>
public sealed class XaReader
{
    readonly BinaryReader _r;
    int _format;

    XaReader(BinaryReader reader) => _r = reader;

    public static XaModel Read(byte[] data) => Read(new MemoryStream(data, writable: false));

    public static XaModel Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, ShiftJis.Encoding, leaveOpen: true);
        return new XaReader(reader).ReadModel();
    }

    XaModel ReadModel()
    {
        byte type = _r.ReadByte();
        byte[]? header = null;
        XaMaterialSection? materials = null;
        XaSection2? section2;
        XaMorphSection? morphs;
        XaSection4? section4 = null;

        bool sb3Layout;
        if (type is 0x02 or 0x03)
        {
            sb3Layout = true;
        }
        else if (type == 0x01)
        {
            // Format 1 header (01 00 00 00 ..) vs. pre-SB3 file starting with a material section.
            var peek = _r.ReadBytes(4);
            _r.BaseStream.Seek(-4, SeekOrigin.Current);
            sb3Layout = (peek[0] | peek[1] | peek[2]) == 0;
        }
        else if (type == 0x00)
        {
            sb3Layout = false;
        }
        else
        {
            throw new InvalidDataException($"Unknown .xa type byte 0x{type:X2}.");
        }

        if (sb3Layout)
        {
            header = new byte[5];
            header[0] = type;
            _r.BaseStream.ReadExactly(header.AsSpan(1));
            _format = BitConverter.ToInt32(header, 0);
            materials = _r.ReadByte() == 1 ? ReadMaterials(_r.ReadInt32()) : null;
            section2 = ReadSection2();
            morphs = ReadMorphs();
            if (_format >= 2)
                section4 = ReadSection4();
        }
        else
        {
            _format = -1;
            if (type == 0x01)
                materials = ReadMaterials(_r.ReadInt32());
            section2 = ReadSection2();
            morphs = ReadMorphs();
        }

        var animation = ReadAnimation();
        var footer = _r.ReadBytes((int)(_r.BaseStream.Length - _r.BaseStream.Position));

        return new XaModel
        {
            Format = _format,
            Header = header,
            Materials = materials,
            Section2 = section2,
            Morphs = morphs,
            Section4 = section4,
            Animation = animation,
            Footer = footer,
        };
    }

    XaMaterialSection ReadMaterials(int count)
    {
        var section = new XaMaterialSection();
        for (int i = 0; i < count; i++)
        {
            var material = new XaMaterial { Name = _r.ReadName() };
            int colors = _r.ReadInt32();
            for (int j = 0; j < colors; j++)
            {
                material.Colors.Add(new XaMaterialColor
                {
                    Diffuse = _r.ReadColor4(),
                    Ambient = _r.ReadColor4(),
                    Specular = _r.ReadColor4(),
                    Emissive = _r.ReadColor4(),
                    Power = _r.ReadSingle(),
                    Unknown1 = _r.ReadExactBytes(4),
                });
            }
            section.Materials.Add(material);
        }
        return section;
    }

    XaSection2? ReadSection2()
    {
        if (_r.ReadByte() == 0)
            return null;
        var section = new XaSection2();
        int count = _r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var item = new XaSection2Item
            {
                Unknown1 = _r.ReadExactBytes(4),
                Name = _r.ReadName(),
                Unknown2 = _r.ReadExactBytes(4),
                Unknown3 = _r.ReadExactBytes(4),
            };
            int blocks = _r.ReadInt32();
            item.Unknown4 = _r.ReadExactBytes(4);
            for (int j = 0; j < blocks; j++)
                item.Blocks.Add((_r.ReadExactBytes(1), _r.ReadExactBytes(4)));
            item.Unknown5 = _r.ReadExactBytes(1);
            section.Items.Add(item);
        }
        return section;
    }

    XaMorphSection? ReadMorphs()
    {
        if (_r.ReadByte() == 0)
            return null;
        var section = new XaMorphSection();

        int sets = _r.ReadInt32();
        for (int i = 0; i < sets; i++)
        {
            var set = new XaMorphIndexSet { Unknown1 = _r.ReadExactBytes(1) };
            int count = _r.ReadInt32();
            set.MeshIndices = ReadUInt16Array(count);
            set.MorphIndices = ReadUInt16Array(count);
            set.Name = _r.ReadName();
            section.IndexSets.Add(set);
        }

        int keyframes = _r.ReadInt32();
        for (int i = 0; i < keyframes; i++)
        {
            int count = _r.ReadInt32();
            var keyframe = new XaMorphKeyframe { Positions = new Vector3[count], Normals = new Vector3[count] };
            for (int j = 0; j < count; j++)
                keyframe.Positions[j] = _r.ReadVector3();
            for (int j = 0; j < count; j++)
                keyframe.Normals[j] = _r.ReadVector3();
            keyframe.Name = _r.ReadName();
            section.Keyframes.Add(keyframe);
        }

        int clips = _r.ReadInt32();
        for (int i = 0; i < clips; i++)
        {
            var clip = new XaMorphClip { MeshName = _r.ReadName(), Name = _r.ReadName() };
            int refs = _r.ReadInt32();
            for (int j = 0; j < refs; j++)
            {
                clip.KeyframeRefs.Add(new XaMorphKeyframeRef
                {
                    Unknown1 = _r.ReadExactBytes(1),
                    Index = _r.ReadInt32(),
                    Unknown2 = _r.ReadExactBytes(1),
                    Name = _r.ReadName(),
                });
            }
            clip.Unknown1 = _r.ReadExactBytes(4);
            section.Clips.Add(clip);
        }
        return section;
    }

    XaSection4? ReadSection4()
    {
        if (_r.ReadByte() == 0)
            return null;
        var section = new XaSection4();
        int lists = _r.ReadInt32();
        for (int i = 0; i < lists; i++)
        {
            int count = _r.ReadInt32();
            var items = new List<byte[]>(count);
            for (int j = 0; j < count; j++)
                items.Add(_r.ReadExactBytes(104 + 4 + 64));
            section.ItemLists.Add(items);
        }
        return section;
    }

    XaAnimationSection? ReadAnimation()
    {
        if (_r.BaseStream.Position >= _r.BaseStream.Length || _r.ReadByte() == 0)
            return null;
        var section = new XaAnimationSection();

        int clipCount = _format == 3 ? 1024 : 512;
        for (int i = 0; i < clipCount; i++)
        {
            section.Clips.Add(new XaAnimationClip
            {
                Name = _r.ReadFixedName(64),
                Speed = _r.ReadSingle(),
                Unknown1 = _r.ReadExactBytes(4),
                Start = _r.ReadSingle(),
                End = _r.ReadSingle(),
                Unknown2 = _r.ReadExactBytes(1),
                Unknown3 = _r.ReadExactBytes(1),
                Unknown4 = _r.ReadExactBytes(1),
                Next = _r.ReadInt32(),
                Unknown5 = _r.ReadExactBytes(1),
                Unknown6 = _r.ReadExactBytes(4),
                Unknown7 = _r.ReadExactBytes(16),
            });
        }

        int tracks = _r.ReadInt32();
        for (int i = 0; i < tracks; i++)
        {
            var track = new XaAnimationTrack { Name = _r.ReadName() };
            int count = _r.ReadInt32();
            track.Unknown1 = _r.ReadExactBytes(4);
            track.Keyframes = new XaKeyframe[count];
            for (int j = 0; j < count; j++)
            {
                track.Keyframes[j] = new XaKeyframe
                {
                    Index = _r.ReadInt32(),
                    Rotation = new Quaternion(_r.ReadSingle(), _r.ReadSingle(), _r.ReadSingle(), _r.ReadSingle()),
                    Unknown1 = _r.ReadUInt64(),
                    Translation = _r.ReadVector3(),
                    Scaling = _r.ReadVector3(),
                };
            }
            section.Tracks.Add(track);
        }
        return section;
    }

    ushort[] ReadUInt16Array(int count)
    {
        var values = new ushort[count];
        for (int i = 0; i < count; i++)
            values[i] = _r.ReadUInt16();
        return values;
    }
}
