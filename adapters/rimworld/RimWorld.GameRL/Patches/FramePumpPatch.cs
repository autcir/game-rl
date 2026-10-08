// Frame-level command pump.
//
// ProcessCommands is driven from the tick postfix, and TickManager only exists
// once a game is ticking. At the main menu there is no TickManager, so a reset
// sent before a game exists queued forever and the Rust side waited on it with
// no timeout. These postfixes drain the queue once per frame instead.
//
// Root.Update is the frame hook, but it is virtual and both concrete roots
// override it, so patching the base declaration would never run: Unity calls
// the override. Verse.Root_Entry and Verse.Root_Play are the only two Root
// subclasses in 1.6.4633, and each is patched directly.

using HarmonyLib;
using Verse;

namespace RimWorld.GameRL.Patches
{
    /// <summary>
    /// Main menu / world-select frames.
    /// </summary>
    [HarmonyPatch(typeof(Root_Entry), nameof(Root_Entry.Update))]
    public static class RootEntryFramePatch
    {
        static void Postfix()
        {
            GameRLMod.OnFrame("frame:root_entry");
        }
    }

    /// <summary>
    /// In-game frames.
    /// </summary>
    [HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
    public static class RootPlayFramePatch
    {
        static void Postfix()
        {
            GameRLMod.OnFrame("frame:root_play");
        }
    }
}
