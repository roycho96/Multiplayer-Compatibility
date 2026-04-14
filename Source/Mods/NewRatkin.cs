using Verse;

namespace Multiplayer.Compat
{
    /// <summary>New Ratkin Race by fxz.Solaris</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3497673755"/>
    /// Desync-59 fix: GameComponent_EMPCheck.GameComponentTick consumes
    /// Verse.Rand directly via Rand.MTBEventOccurs / DustMoteSpawnMTB /
    /// FilthSpawnMTB outside any seeded tick context. Needs PushState/PopState
    /// isolation (default patchPushPop=true).
    [MpCompatFor("fxz.Solaris.RatkinRaceMod.odyssey")]
    class NewRatkin
    {
        public NewRatkin(ModContentPack mod)
        {
            var methodsForAll = new[]
            {
                "NewRatkin.GameComponent_EMPCheck:GameComponentTick",
            };

            PatchingUtilities.PatchSystemRand(methodsForAll);
        }
    }
}
