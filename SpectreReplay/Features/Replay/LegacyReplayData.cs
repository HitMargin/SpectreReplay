using System.Collections.Generic;

namespace SpectreReplay.Features.Replay;

public class LegacyReplayData
{
    public Dictionary<string, string> strings = [];
    public Dictionary<string, bool> bools = [];
    public Dictionary<string, int> ints = [];
    public Dictionary<string, double> doubles = [];
    public List<KeyEvent> KeyEvent_list = [];
    public List<HitContext> HitContext_list = [];

    public void reset()
    {
        strings.Clear();
        bools.Clear();
        ints.Clear();
        doubles.Clear();
        KeyEvent_list.Clear();
        HitContext_list.Clear();
    }
}
