using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Maru Race Hunt (part of Maru Race) by VAMV</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2817638066"/>
    /// Desync-59 (secondary): MaruRace.CompHuntingImpulse.CompTick /
    /// RefreshHuntingTarget call Verse.Rand.RandomElementByWeight outside
    /// per-entity seeded scope. Needs PushState/PopState isolation
    /// (default patchPushPop=true).
    ///
    /// NOTE: namespace is `MaruRace` (confirmed via decompile of
    /// MaruRaceHunt.dll — file name != namespace).
    [MpCompatFor("VAMV.MaruRaceMod")]
    class MaruRaceHunt
    {
        public MaruRaceHunt(ModContentPack mod)
        {
            var methodsForAll = new[]
            {
                "MaruRace.CompHuntingImpulse:CompTick",
                "MaruRace.CompHuntingImpulse:RefreshHuntingTarget",
            };

            PatchingUtilities.PatchSystemRand(methodsForAll);
        }
    }
}
