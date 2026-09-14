# Side quests

**Built, 2026-09-13.** `SideQuestCatalog` carries these seven chains, `SideQuestInjector`
registers them and `SideQuestRewirer` is the hook. This page stays as the design record and the
content review; DESIGN.md records what the implementation actually does.

The one design change on contact with the code: a chain cannot carry finished shape codes, because
a shape code is only valid for one `PartCount`. Each layer is stored as a repeating **pattern**
instead - `"Mr"` fills four parts or six - so every chain below works in hexagonal mode as written.

See `Screenshots/jobs-draft.png` for the same thing drawn.

## The game already has this, and it is called Side Quests

No new system is needed. `ResearchSideQuestGroup` is a **titled chain** of `ResearchSideQuest`, and
the research screen has a Side Quests tab (`HUDResearchTabSideQuests`) that renders them.

```csharp
public class SerializedResearchSideQuest
{
    public string Id;
    public bool IsFollowupForLevel;
    public ISerializedResearchReward[] Rewards;
    public SerializedResearchCostShapes[] Costs;   // { string Shape; int Amount; }
}
```

A cost is *a shape code and an amount* - deliver N of that shape - which is exactly the shape of
what we want. And `ResearchSideQuestGroup` has a public constructor taking a title and a list of
serialized quests, so nothing has to go through scenario JSON:

```csharp
new ResearchSideQuestGroup(title, requiredUpgrades, requiredMechanics, serializedQuests)
```

**The chain is automatic.** The constructor walks the quests in order and adds each one's id to the
dependency set for the next, so step 2 is reachable only once step 1 is done. That is the
progression behaviour we want, for free.

Rewards available, from `ResearchRewardFactory`: research points, blueprint currency, chunk limit,
a building group, an island group, a mechanic, or a side upgrade. Only the first three make sense
here - this mod adds no buildings or islands to gate.

## The seven chains

Seven chains, twenty-six quests. Redesigned around one rule: **every step adds one thing to the factory that made the step before
it.** No step asks for a shape that needs a different production line from scratch. Read each row
left to right; the right-hand column is what is new that step.

`Screenshots/jobs-draft.png` draws all of it.

### Rainbow Vortex - dome, one colour per layer

| Shape | Adds |
| --- | --- |
| `MrMrMrMr` | a dome line, red |
| `MrMrMrMr:MyMyMyMy` | a yellow line and a stacker |
| `MrMrMrMr:MyMyMyMy:MgMgMgMg` | a green line |
| `MrMrMrMr:MyMyMyMy:MgMgMgMg:MbMbMbMb` | a blue line |

The cleanest progression of the set: one dome line per step, one new colour each time, nothing else
changes. Worth having first for that reason as much as for how it looks.

### Turn the Wheel - gear, stack, then pin

| Shape | Adds |
| --- | --- |
| `EuEuEuEu` | a gear line |
| `EuEuEuEu:EyEyEyEy` | a painter and a stacker |
| `EuEuEuEu:EyEyEyEy:ErErErEr` | a third line |
| `P-P-P-P-:EuEuEuEu:EyEyEyEy:ErErErEr` | a pin pusher |

### Both Ways - dome and wedge, opposite handedness

| Shape | Adds |
| --- | --- |
| `TrTrTrTr` | a wedge line, red |
| `TrTrTrTr:MwMwMwMw` | a dome line, white |
| `TrTrTrTr:MwMwMwMw:TbTbTbTb` | blue wedge |
| `TrTrTrTr:MwMwMwMw:TbTbTbTb:MwMwMwMw` | a fourth stack |

### In Bloom - flower into crystal

| Shape | Adds |
| --- | --- |
| `BmBmBmBm` | a flower line, magenta |
| `BmBmBmBm:Br--Br--` | a half-cut red flower |
| `BmBmBmBm:BrcrBrcr` | a crystal generator |
| `BmBmBmBm:BrcrBrcr:ByByByBy` | yellow flower |

The crystal step is the point of this chain: step 2 deliberately leaves gaps so step 3 can fill
them. `ShapeOperationCrystallize` replaces every empty part *and every pin*, so the gaps have to be
built first.

### Sharpen - sawblade, and one vanilla line

| Shape | Adds |
| --- | --- |
| `ZuZuZuZu` | a sawblade line |
| `ZuZuZuZu:OrOrOrOr` | a dot line, red |
| `ZuZuZuZu:OrOrOrOr:ZwZwZwZw` | white sawblade |

Three steps, not four - a fourth was a square plate on top, which added a whole vanilla line for a
step that did not earn it.

### Fine Detail - bar, cross, dot, then interleave

| Shape | Adds |
| --- | --- |
| `IcIcIcIc` | a bar line, cyan |
| `IcIcIcIc:KmKmKmKm` | a cross line, magenta |
| `IcIcIcIc:KmKmKmKm:OyOyOyOy` | a dot line, yellow |
| `IcIcIcIc:KmKmKmKm:OyIyOyIy` | half-cut and recombine |

The last step is the only one that *changes* a layer rather than adding one - the top layer goes
from four dots to dots and bars alternating, which needs a half cutter and a recombine. Everything
below it is untouched.

### Foundations - vanilla underneath, new on top

| Shape | Adds |
| --- | --- |
| `RuDuRuDu` | cut and recombine a square line with a diamond line |
| `RuDuRuDu:CwCwCwCw` | a circle line, white |
| `RuDuRuDu:CwCwCwCw:EyEyEyEy` | a gear line, yellow |

This is the chain that leans on the base shapes. It opens on the one step in the whole draft that
needs a half cutter before anything else - square and diamond alternating round one layer - and the
vanilla circle underneath the gear reads as a hub.

## More patterns using the base shapes

Not chains - candidates for the pattern gallery, and for a chain if any of them earn one. The new
parts pair with vanilla better than they pair with each other, because a plain circle or square
underneath gives the busier shapes something to sit on.

| Code | Reads as |
| --- | --- |
| `CuCuCuCu:EyEyEyEy` | a cog with a hub - the single best vanilla pairing |
| `RuRuRuRu:DrDrDrDr` | a diamond inlaid in a plate |
| `RuRuRuRu:OrOrOrOr` | four rivets on a plate |
| `CuCuCuCu:IwIwIwIw` | bars on a disc, like clock hands |
| `RuDuRuDu` | square and diamond alternating, one layer |
| `RwRwRwRw:CuCuCuCu:ObObObOb` | plate, disc, blue dots - a porthole |
| `CuCuCuCu:BmBmBmBm` | flower on a disc |

Three more that need the two shapes this page cannot draw, so they are listed but not illustrated:

| Code | Reads as |
| --- | --- |
| `WuWuWuWu:TyTyTyTy` | windmill under a counter-spinning wedge - a gearbox |
| `SuSuSuSu:LgLgLgLg` | star points with leaves between them |
| `CuRuWuSu:EuDuMuLu` | all four vanilla under four new |

## Pins and crystals in a shape code

Both confirmed against the assemblies rather than assumed:

- **Pin is `P-`.** `ShapeOperationPushPin` adds `new ShapePart(PinShapePart, null)` - a null colour,
  and `ShapePart.ToString` writes `Color?.Code ?? '-'`. It parses because `ShapeHashParser` only
  rejects a colour on a part that disallows colour, and `-` is not a known colour code.
- **Crystal is `c` plus a colour** - `crcrcrcr`, `cgcgcgcg` and `cbcbcbcb` are literals in
  `HUDChooseBlueprintIconComponent`.
- **Pinning shifts everything up.** Pins land at layer 0 under each part that *was* at layer 0, and
  anything at `MaxShapeLayers - 1` or above is discarded. So the pin step has to be last in its
  chain, and only works if the shape below it is within one layer of the cap.

## The one real implementation catch - resolved

`ResearchProgression` keeps collections that a quest has to land in, not one. The draft said five;
it is **four**:

```csharp
private List<ResearchSideQuestGroup> _SideQuestGroups;
private List<ResearchSideQuest> _SideQuests;
private List<IResearchUpgrade> _AllUpgrades;
private Dictionary<ResearchUpgradeId, IResearchUpgrade> _UpgradesById;
```

`_SideQuestsIncludingHidden` is the fifth and it is **dead** - declared, never assigned, never read
anywhere in the assembly. Writing to it would have been cargo cult.

Appending to `_SideQuestGroups` alone would render the tab correctly and then fail to resolve the
quests by id, which is the kind of half-working that does not announce itself. All four need the
entries, and they are private fields on a public class - reachable with the publicizer, the same way
the shape parts are.

**The injection point turned out to be neither of the two the draft considered.** Not a postfix on
`Init_3_SavegameAndMode`, and not `GameRuleManager.Hook_ModifyResearchLayout` - ShapezShifter
already has an official seam, `IGameScenarioRewirer`, which it runs immediately after the game
constructs a `GameScenario`. Extended Research uses the same one, which is as close to a proof that
it works in this game version as reading can get.

That seam is later than this mod's shape part injection, which prefixes `Init_3_SavegameAndMode`
and so runs before `GameMode.From` builds the scenario. The ordering is load bearing: it is what
lets a quest ask for a shape made of the parts this mod just added.

## Risks worth stating before building

- **Quest ids become save state.** `ResearchUpgradeId` is what progress is recorded against, so
  removing the mod from a save that has completed quests leaves dangling ids. `AffectsSaveGames` is
  already true, which covers it, but the ids need a mod-specific prefix so they can never collide
  with a vanilla or another mod's upgrade id.
- **Titles need translations.** `ResearchSideQuest.Title` is an `IText`. Use `RawText`, or add
  `translations.json` entries - `"x".T()` with no matching key renders as `?x`.
- **Validation.** `GameModeValidator` and `CachedGameDataIds` both walk `SideQuestGroups`. Whether
  they run before or after the injection has not been checked; if before, nothing happens, if after,
  a malformed group could throw where a mod cannot catch it.
