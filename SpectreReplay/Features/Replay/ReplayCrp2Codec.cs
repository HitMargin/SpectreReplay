using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

internal static class ReplayCrp2Codec
{
    internal static void ConvertToCompact(LegacyReplayData original, out CompactReplayData compact)
    {
        compact = new CompactReplayData();
        if (original == null)
        {
            Debug.Log("ConvertToCompact Error: null input");
            return;
        }
        compact.s = new Dictionary<string, string>(original.strings);
        compact.b = new Dictionary<string, bool>(original.bools);
        compact.i = new Dictionary<string, int>(original.ints);
        compact.d = new Dictionary<string, double>(original.doubles);
        foreach (KeyEvent k in original.KeyEvent_list)
        {
            compact.keyCodes.Add(k.KeyCode);
            compact.keyPresses.Add(k.IsPressed ? 1 : 0);
            compact.keySongPositions.Add(k.SongPosition);
        }
        foreach (HitContext h in original.HitContext_list)
        {
            compact.hitCurrentFloorIDs.Add(h.CurrentFloorID);
            compact.hitCurrAngles.Add(h.CurrAngle);
            compact.hitOverloadCounters.Add(h.OverloadCounter);
            compact.hitNoFailHits.Add(h.NoFailHit ? 1 : 0);
            compact.hitIsAutos.Add(h.IsAuto ? 1 : 0);
            compact.hitNextFloorAutos.Add(h.NextFloorAuto ? 1 : 0);
            compact.hitCachedAngles.Add(h.CachedAngle);
            compact.hitTargetExitAngles.Add(h.TargetExitAngle);
            compact.hitMidspinInfiniteMargins.Add(h.MidspinInfiniteMargin ? 1 : 0);
            compact.hitRDCautos.Add(h.RDC_auto ? 1 : 0);
            compact.hitCurFreeRoamSections.Add(h.curFreeRoamSection);
        }
    }

    internal static void ConvertFromCompact(CompactReplayData compact, out LegacyReplayData original)
    {
        original = new LegacyReplayData();
        original.reset();
        if (compact == null) return;

        original.strings = new Dictionary<string, string>(compact.s);
        original.bools = new Dictionary<string, bool>(compact.b);
        original.ints = new Dictionary<string, int>(compact.i);
        original.doubles = new Dictionary<string, double>(compact.d);

        for (int i = 0; i < compact.keyCodes.Count; i++)
        {
            original.KeyEvent_list.Add(new KeyEvent
            {
                KeyCode = compact.keyCodes[i],
                IsPressed = compact.keyPresses[i] == 1,
                SongPosition = compact.keySongPositions[i]
            });
        }
        for (int j = 0; j < compact.hitCurrentFloorIDs.Count; j++)
        {
            original.HitContext_list.Add(new HitContext
            {
                CurrentFloorID = compact.hitCurrentFloorIDs[j],
                CurrAngle = compact.hitCurrAngles[j],
                OverloadCounter = compact.hitOverloadCounters[j],
                NoFailHit = compact.hitNoFailHits[j] == 1,
                IsAuto = compact.hitIsAutos[j] == 1,
                NextFloorAuto = compact.hitNextFloorAutos[j] == 1,
                CachedAngle = compact.hitCachedAngles[j],
                TargetExitAngle = compact.hitTargetExitAngles[j],
                MidspinInfiniteMargin = compact.hitMidspinInfiniteMargins[j] == 1,
                RDC_auto = compact.hitRDCautos[j] == 1,
                curFreeRoamSection = compact.hitCurFreeRoamSections[j]
            });
        }
    }

    internal static byte[] CompactToOptimizedBinary(CompactReplayData compact)
    {
        if (compact == null)
        {
            Debug.Log("CompactToOptimizedBinary Error: null input");
            return null;
        }
        OptimizedReplayData opt = new OptimizedReplayData
        {
            FormatVersion = 2,
            s = compact.s,
            b = compact.b,
            i = compact.i,
            d = compact.d,
            keyCodes = compact.keyCodes,
            keyPresses = compact.keyPresses,
            keySongPositions = compact.keySongPositions,
            hitCurrentFloorIDs = compact.hitCurrentFloorIDs,
            hitCurrAngles = compact.hitCurrAngles,
            hitOverloadCounters = compact.hitOverloadCounters,
            hitCachedAngles = compact.hitCachedAngles,
            hitTargetExitAngles = compact.hitTargetExitAngles,
            hitCurFreeRoamSections = compact.hitCurFreeRoamSections,
            hitFlags = new List<byte>(compact.hitCurrentFloorIDs.Count)
        };
        for (int i = 0; i < compact.hitCurrentFloorIDs.Count; i++)
        {
            HitContextFlags flags = HitContextFlags.None;
            if (compact.hitNoFailHits[i] == 1)           flags |= HitContextFlags.NoFailHit;
            if (compact.hitIsAutos[i] == 1)               flags |= HitContextFlags.IsAuto;
            if (compact.hitNextFloorAutos[i] == 1)        flags |= HitContextFlags.NextFloorAuto;
            if (compact.hitMidspinInfiniteMargins[i] == 1) flags |= HitContextFlags.MidspinInfiniteMargin;
            if (compact.hitRDCautos[i] == 1)               flags |= HitContextFlags.RDC_auto;
            opt.hitFlags.Add((byte)flags);
        }
        return ReplayBinaryIO.SerializeOptimizedData(opt);
    }

    internal static CompactReplayData OptimizedBinaryToCompact(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            Debug.Log("OptimizedBinaryToCompact Error: null or empty input");
            return null;
        }
        try
        {
            OptimizedReplayData opt = ReplayBinaryIO.DeserializeOptimizedData(data);
            if (opt.FormatVersion != 2)
                Debug.Log($"Warning: Unexpected format version {opt.FormatVersion}");

            int count = opt.hitCurrentFloorIDs?.Count ?? 0;
            int flagsCount = opt.hitFlags?.Count ?? 0;
            CompactReplayData compact = new CompactReplayData
            {
                s = opt.s ?? new Dictionary<string, string>(),
                b = opt.b ?? new Dictionary<string, bool>(),
                i = opt.i ?? new Dictionary<string, int>(),
                d = opt.d ?? new Dictionary<string, double>(),
                keyCodes = opt.keyCodes ?? new List<ushort>(),
                keyPresses = opt.keyPresses ?? new List<int>(),
                keySongPositions = opt.keySongPositions ?? new List<double>(),
                hitCurrentFloorIDs = opt.hitCurrentFloorIDs ?? new List<int>(),
                hitCurrAngles = opt.hitCurrAngles ?? new List<double>(),
                hitOverloadCounters = opt.hitOverloadCounters ?? new List<float>(),
                hitCachedAngles = opt.hitCachedAngles ?? new List<double>(),
                hitTargetExitAngles = opt.hitTargetExitAngles ?? new List<double>(),
                hitCurFreeRoamSections = opt.hitCurFreeRoamSections ?? new List<int>(),
                hitNoFailHits = new List<int>(count),
                hitIsAutos = new List<int>(count),
                hitNextFloorAutos = new List<int>(count),
                hitMidspinInfiniteMargins = new List<int>(count),
                hitRDCautos = new List<int>(count)
            };
            for (int i = 0; i < count; i++)
            {
                byte b = (byte)((i < flagsCount) ? opt.hitFlags[i] : 0);
                HitContextFlags flags = (HitContextFlags)b;
                compact.hitNoFailHits.Add(flags.HasFlag(HitContextFlags.NoFailHit) ? 1 : 0);
                compact.hitIsAutos.Add(flags.HasFlag(HitContextFlags.IsAuto) ? 1 : 0);
                compact.hitNextFloorAutos.Add(flags.HasFlag(HitContextFlags.NextFloorAuto) ? 1 : 0);
                compact.hitMidspinInfiniteMargins.Add(flags.HasFlag(HitContextFlags.MidspinInfiniteMargin) ? 1 : 0);
                compact.hitRDCautos.Add(flags.HasFlag(HitContextFlags.RDC_auto) ? 1 : 0);
            }
            return compact;
        }
        catch (Exception ex)
        {
            Debug.Log("OptimizedBinaryToCompact Error: " + ex.Message);
            return null;
        }
    }
}
