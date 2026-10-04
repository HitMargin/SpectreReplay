using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

public class CompactReplayData
{
    public Dictionary<string, string> s = [];
    public Dictionary<string, bool> b = [];
    public Dictionary<string, int> i = [];
    public Dictionary<string, double> d = [];
    public List<ushort> keyCodes = [];
    public List<int> keyPresses = [];
    public List<double> keySongPositions = [];
    public List<int> hitCurrentFloorIDs = [];
    public List<double> hitCurrAngles = [];
    public List<float> hitOverloadCounters = [];
    public List<int> hitNoFailHits = [];
    public List<int> hitIsAutos = [];
    public List<int> hitNextFloorAutos = [];
    public List<double> hitCachedAngles = [];
    public List<double> hitTargetExitAngles = [];
    public List<int> hitMidspinInfiniteMargins = [];
    public List<int> hitRDCautos = [];
    public List<int> hitCurFreeRoamSections = [];

    public void reset()
    {
        s.Clear();
        b.Clear();
        i.Clear();
        d.Clear();
        keyCodes.Clear();
        keyPresses.Clear();
        keySongPositions.Clear();
        hitCurrentFloorIDs.Clear();
        hitCurrAngles.Clear();
        hitOverloadCounters.Clear();
        hitNoFailHits.Clear();
        hitIsAutos.Clear();
        hitNextFloorAutos.Clear();
        hitCachedAngles.Clear();
        hitTargetExitAngles.Clear();
        hitMidspinInfiniteMargins.Clear();
        hitRDCautos.Clear();
        hitCurFreeRoamSections.Clear();
    }
}

public class OptimizedReplayData
{
    public int FormatVersion = 2;
    public Dictionary<string, string> s;
    public Dictionary<string, bool> b;
    public Dictionary<string, int> i;
    public Dictionary<string, double> d;
    public List<ushort> keyCodes;
    public List<int> keyPresses;
    public List<double> keySongPositions;
    public List<int> hitCurrentFloorIDs;
    public List<double> hitCurrAngles;
    public List<float> hitOverloadCounters;
    public List<double> hitCachedAngles;
    public List<double> hitTargetExitAngles;
    public List<int> hitCurFreeRoamSections;
    public List<byte> hitFlags;
}

[Flags]
public enum HitContextFlags : byte
{
    None = 0,
    NoFailHit = 1,
    IsAuto = 2,
    NextFloorAuto = 4,
    MidspinInfiniteMargin = 8,
    RDC_auto = 0x10
}
