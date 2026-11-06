using System;

namespace UiPreview;

internal static class SaveToastOnceCheck
{
    internal static int Run(string projectRoot)
    {
        Console.WriteLine("  -- save toast once --");
        string source = SrcUtil.Source(projectRoot, "State/SpectreState.cs");
        string misc = SrcUtil.Source(projectRoot, "Features/Replay/Patches/Patch_Misc.cs");
        int fail = 0;

        void Expect(bool ok, string message)
        {
            if (ok) Console.WriteLine("    ✓ " + message);
            else
            {
                Console.WriteLine("    ✗ " + message);
                fail++;
            }
        }

        Expect(source.Contains("bool notify = true", StringComparison.Ordinal),
            "SaveFile must expose the notify switch for retry-aware reporting");
        Expect(source.Contains("if (notify && (success || retry)) TriggerMessage(message);",
                StringComparison.Ordinal),
            "SaveFile must suppress the first failed attempt and report only the retry outcome");
        Expect(source.Contains("return SaveFile(without_sound, retry: true, notify: notify);",
                StringComparison.Ordinal),
            "the safe-name retry must preserve the caller's notification decision");
        Expect(misc.Contains("if (!ManualSaveHotkey.TryConsume()) return;",
                StringComparison.Ordinal),
            "manual-save hotkey must be consumed once per frame");
        Expect(misc.Contains("private static int _lastFrame = -1;",
                StringComparison.Ordinal),
            "hotkey guard must remember a frame rather than staying globally disabled");

        return fail;
    }
}
