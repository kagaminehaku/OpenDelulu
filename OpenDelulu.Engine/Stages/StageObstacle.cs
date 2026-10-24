using System.Globalization;
using System.Numerics;
using OpenDelulu.Formats.Lst;

namespace OpenDelulu.Engine.Stages;

/// <summary>
/// A stage object the camera may look through (ld02_02, 30 rows per stage): when the line from the camera to the girl
/// crosses its hit box (o01_A_*, a flat mesh of moNN.xx), the exe hides the object (o01_N_*) — CONFIG's "erase
/// obstacles". The flags say in which situation it may be hidden: 20 for the date (columns 3..22), 10 for the oil scene
/// (23..32), 10 for the H scenes (33..42); the exe reads them at 0x44F800 and applies them at 0x42FAC0.
/// </summary>
public sealed record StageObstacle(string Frame, string HitBox, bool[] Date, bool[] Oil, bool[] H)
{
    /// <summary>The obstacles of every stage (key = stage number).</summary>
    public static Dictionary<int, IReadOnlyList<StageObstacle>> Load(LstTable table)
    {
        var result = new Dictionary<int, List<StageObstacle>>();
        foreach (var row in table.Rows)
        {
            if (!int.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < 1
                || row.Length < 3 || row[1].Trim() is "" or "0" || row[2].Trim() is "" or "0")
                continue;
            bool[] Flags(int first, int count) =>
                [.. Enumerable.Range(first, count).Select(c => c < row.Length && row[c].Trim() != "0" && row[c].Trim() != "")];
            int stage = (number - 1) / 30 + 1;
            if (!result.TryGetValue(stage, out var list))
                result[stage] = list = [];
            list.Add(new StageObstacle(row[1].Trim(), row[2].Trim(), Flags(3, 20), Flags(23, 10), Flags(33, 10)));
        }
        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<StageObstacle>)p.Value);
    }

    /// <summary>
    /// True when the camera→girl line, seen from above (x, z), crosses one of the hit box triangles or the camera stands
    /// inside one (the exe's 2D edge tests 0x504540 and a downward D3DXIntersectTri).
    /// </summary>
    public static bool Blocks(ReadOnlySpan<Vector2> triangles, Vector2 eye, Vector2 target)
    {
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            Vector2 a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            if (Crosses(eye, target, a, b) || Crosses(eye, target, b, c) || Crosses(eye, target, c, a) || Inside(eye, a, b, c))
                return true;
        }
        return false;
    }

    static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    static bool Crosses(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
    {
        float d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0, positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }
}
