using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Maru Race Hunt (part of Maru Race) by VAMV</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2817638066"/>
    /// Desync-59 (secondary): CompHuntingImpulse.CompTick / RefreshHuntingTarget
    /// call Verse.Rand.RandomElementByWeight outside per-entity seeded scope.
    /// Needs PushState/PopState isolation (default patchPushPop=true).
    [MpCompatFor("VAMV.MaruRaceMod")]
    class MaruRaceHunt
    {
        public MaruRaceHunt(ModContentPack mod)
        {
            var methodsForAll = new[]
            {
                "MaruRaceHunt.CompHuntingImpulse:CompTick",
                "MaruRaceHunt.CompHuntingImpulse:RefreshHuntingTarget",
            };

            PatchingUtilities.PatchSystemRand(methodsForAll);
        }
    }
}
