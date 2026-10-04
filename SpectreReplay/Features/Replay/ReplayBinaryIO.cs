using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SpectreReplay.Features.Replay;

internal static class ReplayBinaryIO
{
    internal static byte[] SerializeOptimizedData(OptimizedReplayData data)
    {
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(ms);
        w.Write(data.FormatVersion);
        WriteStringDict(w, data.s);
        WriteBoolDict(w, data.b);
        WriteIntDict(w, data.i);
        WriteDoubleDict(w, data.d);
        WriteUshortList(w, data.keyCodes);
        WriteIntList(w, data.keyPresses);
        WriteDoubleList(w, data.keySongPositions);
        WriteIntList(w, data.hitCurrentFloorIDs);
        WriteDoubleList(w, data.hitCurrAngles);
        WriteFloatList(w, data.hitOverloadCounters);
        WriteDoubleList(w, data.hitCachedAngles);
        WriteDoubleList(w, data.hitTargetExitAngles);
        WriteIntList(w, data.hitCurFreeRoamSections);
        WriteByteList(w, data.hitFlags);
        w.Flush();
        return ms.ToArray();
    }

    internal static OptimizedReplayData DeserializeOptimizedData(byte[] bytes)
    {
        using MemoryStream ms = new MemoryStream(bytes);
        using BinaryReader r = new BinaryReader(ms);
        OptimizedReplayData d = new OptimizedReplayData
        {
            FormatVersion = r.ReadInt32(),
            s = ReadStringDict(r),
            b = ReadBoolDict(r),
            i = ReadIntDict(r),
            d = ReadDoubleDict(r),
            keyCodes = ReadUshortList(r),
            keyPresses = ReadIntList(r),
            keySongPositions = ReadDoubleList(r),
            hitCurrentFloorIDs = ReadIntList(r),
            hitCurrAngles = ReadDoubleList(r),
            hitOverloadCounters = ReadFloatList(r),
            hitCachedAngles = ReadDoubleList(r),
            hitTargetExitAngles = ReadDoubleList(r),
            hitCurFreeRoamSections = ReadIntList(r),
            hitFlags = ReadByteList(r)
        };
        return d;
    }

    // ── Primitive serdes ───────────────────────────────

    private static void WriteStringDict(BinaryWriter w, Dictionary<string, string> dict)
    {
        w.Write(dict?.Count ?? 0);
        if (dict == null) return;
        foreach (KeyValuePair<string, string> kv in dict)
        {
            w.Write(kv.Key ?? "");
            w.Write(kv.Value ?? "");
        }
    }

    private static void WriteBoolDict(BinaryWriter w, Dictionary<string, bool> dict)
    {
        w.Write(dict?.Count ?? 0);
        if (dict == null) return;
        foreach (KeyValuePair<string, bool> kv in dict)
        {
            w.Write(kv.Key ?? "");
            w.Write(kv.Value);
        }
    }

    private static void WriteIntDict(BinaryWriter w, Dictionary<string, int> dict)
    {
        w.Write(dict?.Count ?? 0);
        if (dict == null) return;
        foreach (KeyValuePair<string, int> kv in dict)
        {
            w.Write(kv.Key ?? "");
            w.Write(kv.Value);
        }
    }

    private static void WriteDoubleDict(BinaryWriter w, Dictionary<string, double> dict)
    {
        w.Write(dict?.Count ?? 0);
        if (dict == null) return;
        foreach (KeyValuePair<string, double> kv in dict)
        {
            w.Write(kv.Key ?? "");
            w.Write(kv.Value);
        }
    }

    private static Dictionary<string, string> ReadStringDict(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 100000)
            throw new InvalidDataException("String dict count out of range");
        var d = new Dictionary<string, string>(n);
        for (int i = 0; i < n; i++)
        {
            string key = r.ReadString();
            if (d.ContainsKey(key))
                throw new InvalidDataException($"Duplicate key in string dict: {key}");
            d[key] = r.ReadString();
        }
        return d;
    }

    private static Dictionary<string, bool> ReadBoolDict(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 100000)
            throw new InvalidDataException("Bool dict count out of range");
        var d = new Dictionary<string, bool>(n);
        for (int i = 0; i < n; i++)
            d[r.ReadString()] = r.ReadBoolean();
        return d;
    }

    private static Dictionary<string, int> ReadIntDict(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 100000)
            throw new InvalidDataException("Int dict count out of range");
        var d = new Dictionary<string, int>(n);
        for (int i = 0; i < n; i++)
            d[r.ReadString()] = r.ReadInt32();
        return d;
    }

    private static Dictionary<string, double> ReadDoubleDict(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 100000)
            throw new InvalidDataException("Double dict count out of range");
        var d = new Dictionary<string, double>(n);
        for (int i = 0; i < n; i++)
            d[r.ReadString()] = r.ReadDouble();
        return d;
    }

    private static void WriteUshortList(BinaryWriter w, List<ushort> list)
    {
        w.Write(list?.Count ?? 0);
        if (list == null) return;
        foreach (ushort v in list) w.Write(v);
    }

    private static void WriteIntList(BinaryWriter w, List<int> list)
    {
        w.Write(list?.Count ?? 0);
        if (list == null) return;
        foreach (int v in list) w.Write(v);
    }

    private static void WriteDoubleList(BinaryWriter w, List<double> list)
    {
        w.Write(list?.Count ?? 0);
        if (list == null) return;
        foreach (double v in list) w.Write(v);
    }

    private static void WriteFloatList(BinaryWriter w, List<float> list)
    {
        w.Write(list?.Count ?? 0);
        if (list == null) return;
        foreach (float v in list) w.Write(v);
    }

    private static void WriteByteList(BinaryWriter w, List<byte> list)
    {
        w.Write(list?.Count ?? 0);
        if (list == null) return;
        foreach (byte v in list) w.Write(v);
    }

    private static List<ushort> ReadUshortList(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 100000)
            throw new InvalidDataException("Ushort list count out of range");
        var l = new List<ushort>(n);
        for (int i = 0; i < n; i++) l.Add(r.ReadUInt16());
        return l;
    }

    private static List<int> ReadIntList(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 100000)
            throw new InvalidDataException("Int list count out of range");
        var l = new List<int>(n);
        for (int i = 0; i < n; i++) l.Add(r.ReadInt32());
        return l;
    }

    private static List<double> ReadDoubleList(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 100000)
            throw new InvalidDataException("Double list count out of range");
        var l = new List<double>(n);
        for (int i = 0; i < n; i++) l.Add(r.ReadDouble());
        return l;
    }

    private static List<float> ReadFloatList(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 100000)
            throw new InvalidDataException("Float list count out of range");
        var l = new List<float>(n);
        for (int i = 0; i < n; i++) l.Add(r.ReadSingle());
        return l;
    }

    private static List<byte> ReadByteList(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 100000)
            throw new InvalidDataException("Byte list count out of range");
        var l = new List<byte>(n);
        for (int i = 0; i < n; i++) l.Add(r.ReadByte());
        return l;
    }
}
