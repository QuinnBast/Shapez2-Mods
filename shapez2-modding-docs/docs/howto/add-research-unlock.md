# Add a research unlock

**Problem.** New content has to be unlockable, or it never appears on the toolbar.

**Solution.** One of three `Unlocked…` calls in the builder chain. Pick by how much
research UI you want to own.

| Call | Effect | Use when |
| --- | --- | --- |
| `UnlockedAtMilestone(selector)` | unlocks with an existing milestone | simplest — you want it available from some tier onward |
| `UnlockedWithExistingSideUpgrade(selector)` | rides an existing side upgrade | your thing belongs with a vanilla upgrade |
| `UnlockedWithNewSideUpgrade(builder)` | creates a new purchasable upgrade | you want a research node with its own cost and description |

## Unlock at a milestone

```csharp
.UnlockedAtMilestone(new ByIndexMilestoneSelector(^1))   // the last milestone
.UnlockedAtMilestone(new ByIdMilestoneSelector(new ResearchUpgradeId("Milestone_Initial")))
```

`^1` means "the final milestone", i.e. late game. For "available immediately", target
the first milestone by id.

**No milestone id exists in every scenario.** Exporting all seven with
`debug.export-game-data` and comparing them gives:

| Scenario | Levels | First milestone |
| --- | --- | --- |
| default / hard / insane | 13 | `Milestone_Initial` |
| converter-regular / converter-hard | 11 | `ConverterMilestoneTier_Initial` |
| hexagonal | 9 | `Milestone_Initial` |
| onboarding | 5 | `Milestone_Initial` |

So a hard-coded `ByIdMilestoneSelector` is wrong in at least the two converter scenarios,
and index selection means very different things in a 5-level scenario and a 13-level one —
`^1` is the late game in both, but index `4` is the *end* of onboarding and the early
midgame of default. Index `0` is the only index that means the same thing everywhere.

Select per scenario instead:

```csharp
.UnlockedAtMilestone(new ByIdPerScenarioMilestoneSelector(MilestoneForScenario))

private ResearchUpgradeId MilestoneForScenario(ScenarioId scenarioId)
{
    return new ResearchUpgradeId(
        scenarioId == /* converter scenario */
            ? "ConverterMilestoneTier_Initial"
            : "Milestone_Initial");
}
```

That is exactly what `BiggerPlatforms` does, and it exists because getting it wrong
means content that never unlocks in one scenario. There is also
`ByIndexPerScenarioMilestoneSelector` when you want positional selection per scenario.

## Create a new side upgrade

```csharp
IPresentableUnlockableSideUpgradeBuilder upgrade = SideUpgrade.New()
   .WithPresentationData(new SideUpgradePresentationData(
        new ResearchUpgradeId("my-mod.cutter"),  // this node's OWN id
        borrowedImageId,                         // must resolve - see below
        GameVideoId.Empty,
        "my-mod.cutter.title".T(),
        "my-mod.cutter.description".T(),
        false,
        "Buildings"))                            // research category
   .WithCost(new ResearchCostPoints(new ResearchPointCurrency(50)).AsEnumerable())
   .WithCustomRequirements(
        Array.Empty<ResearchMechanicId>(),
        new[] { new ResearchUpgradeId("CBCutting_FullCutter") });   // the node it hangs off
```

`CBCutting_FullCutter` is a real id, and one of only **18 side upgrades that exist in all
seven scenarios** — most do not. Get the list for yourself from
`basedata-v<N>/scenarios/*.json` under `Progression.ScenarioContent.ContentBundles`; do
not copy an id out of a tutorial without checking it, including this one.

The chain is enforced in the same way as the building chain — presentation, then cost,
then requirements:

```csharp
IPresentableSideUpgradeBuilder      → WithCost(IEnumerable<IResearchCost>)
ICostingSideUpgradeBuilder          → WithCustomRequirements(mechanics, upgrades)
                                    → CopyingRequirements(sideUpgradeSelector)
                                    → WithoutCustomRequirements()
IPresentableUnlockableSideUpgradeBuilder → WithAdditionalRewards(...)
```

`CopyingRequirements(...)` is the shortcut worth knowing: it clones the prerequisites of
an existing upgrade instead of you enumerating them, which keeps your node consistent
with whatever it sits next to.

Two empty requirement arrays mean no prerequisites at all — the node is purchasable from
the start of the game, which is rarely what you want. Requirements are the only thing
placing your node in the tree.

### The preview image is not optional

`HUDResearchSideUpgradeDisplay.RebuildView` calls `GameData.GetImage(upgrade.ImageId)`
unconditionally, and `GetImage` throws on an id it cannot resolve — `GameImageId.Empty`
included. That throw propagates out of the whole `HUDResearchTree` construction, so the
research screen comes up half-built and cannot be closed. This is not a cosmetic
shortcut; an empty image id is a broken game.

Image ids live in Unity assets, so the reliable way to get one is to borrow it from an
upgrade the game already renders:

```csharp
foreach (ResearchSideUpgrade upgrade in progression.SideUpgrades)
{
    if (upgrade.ImageId.HasValue) { return upgrade.ImageId; }
}
```

### One node shared by several pieces of content

`UnlockedWithNewSideUpgrade` registers the node **once per building or island group**, not
once. `UnlockIslandWithNewSideUpgradeResearchProgressionExtender.ExtendResearch` runs for
each group and its body is `SideUpgradeBuilder.Build(...)`, which appends to
`_SideUpgrades`, `_ShopItems`, `_AllUpgrades` and `_UpgradesById` every call. Ten islands
sharing one builder puts ten identical nodes in the shop.

`CustomSideUpgradeSelector` is not the escape — its `Select` is a call to `Build`, so
passing it to `UnlockedWithExistingSideUpgrade` duplicates identically.

Write a get-or-create selector instead. Every island passes the same instance to
`UnlockedWithExistingSideUpgrade`; the first one to be extended builds the node, and the
rest find it, whereupon `UnlockIslandWithExistingSideUpgradeResearchProgressionExtender`
appends their group to its `Rewards`:

```csharp
public ResearchSideUpgrade Select(ScenarioId scenarioId, ResearchProgression progression)
{
    // `is`, not a cast: _UpgradesById holds levels and side quests too.
    if (progression.TryGetUpgrade(Id, out IResearchUpgrade existing)
        && existing is ResearchSideUpgrade built)
    {
        return built;
    }

    return Build(scenarioId, progression);
}
```

Key the lookup on the `ResearchProgression` you are handed, never on a cached field:
`ExtendResearch` runs once per scenario load with a fresh progression each time, and a
cached upgrade would be appended to a progression that never contained it.

## Costs

```csharp
new ResearchCostPoints(new ResearchPointCurrency(48))   // displays as "4.8k"
```

**The number you pass is not the number the player sees.**
`StringFormattingExtensions.Format(this ResearchPointCurrency)` is
`FormatIntegerMax4Digits(amount.Amount * 100)`, so a `ResearchPointCurrency` is in
*hundreds* of displayed points: `48` renders as "4.8k" and `100` renders as "10.0k".
Write the cost you want on screen and divide by 100. Getting this backwards prices a node
a hundredfold too high and it still looks plausible, because the research screen has
five-figure nodes in it.

`WithCost` takes an `IEnumerable<IResearchCost>`, so multiple costs are possible;
`.AsEnumerable()` wraps a single one. Price it against neighbouring vanilla upgrades in
the same category — a 50-point node next to 5,000-point nodes reads as a bug.

## Side quest chains

A **side quest** is not a side upgrade. `ResearchSideQuestGroup` is a titled chain rendered by
the Side Quests tab (`HUDResearchTabSideQuests`), and each `ResearchSideQuest` in it costs
*a shape code and an amount* — deliver N of that shape. There is no scenario JSON involved:
the group has a public constructor.

```csharp
new ResearchSideQuestGroup(
    new RawText("Rainbow Vortex"),
    Array.Empty<ResearchUpgradeId>(),      // gating; see below
    Array.Empty<ResearchMechanicId>(),
    new[]
    {
        new SerializedResearchSideQuest
        {
            Id = "mymod.rainbow.1",
            Costs = new[] { new SerializedResearchCostShapes { Shape = "RbRbRbRb", Amount = 1200 } },
            Rewards = new ISerializedResearchReward[]
            {
                new SerializedResearchRewardResearchPoints { Amount = 1 },
                new SerializedResearchRewardChunkLimit { Amount = 30 },
            },
        },
        // …
    });
```

**The chain builds itself.** The constructor walks the quests in order and folds each one's id
into the dependency set for the next, so step 2 is unreachable until step 1 is done. That is the
whole progression behaviour and it is free.

### Where to do it

`IGameScenarioRewirer.ModifyGameScenario` — ShapezShifter runs it right after the game constructs
a `GameScenario`, which is the only moment where the progression is fully assembled and nothing
has read it. See [Run code when the game loads](run-code-when-game-loads.md).

### Four lists, not one

`ResearchProgression` keeps its quests in several places, and appending to one is the kind of
half-working that does not announce itself — the tab would render the quests and then fail to
resolve them by id. All four need the entries, and they are private fields reachable with the
publicizer:

```csharp
progression._SideQuestGroups.Add(group);
foreach (ResearchSideQuest quest in group.SideQuests)
{
    progression._SideQuests.Add(quest);
    progression._AllUpgrades.Add(quest);
    progression._UpgradesById[quest.Id] = quest;
}
```

There is a fifth field, `_SideQuestsIncludingHidden`. It is **dead** — declared, never assigned,
never read anywhere in the assembly. Writing to it is cargo cult.

### The shape code has to match the mode's part count

`StrictShapeDefinitionFactory` rejects any hash whose first layer is not exactly `PartCount`
pairs, so `RbRbRbRb` is a *quad* quest and is invalid in hexagonal mode rather than merely ugly.
Store a repeating pattern and fill it at injection time instead — `"Rb"` is four parts or six
depending on where it is read, and `"Rb--"` alternates. Vanilla's own side tasks are written that
way (`SG_Fluids_1` costs `Rb--Rb--:Cu--Cu--`).

Validate before registering, with the game's own parser over the scenario's own parts and colours:

```csharp
var validator = new StrictShapeDefinitionFactory(
    shapes.PartCount, shapes.Parts, colors.Colors, new ShapeHashParser(), new ShapeIdManager());
if (!validator.TryCreateShapeDefinition(code, out _)) { /* skip, and say why */ }
```

`shapes` and `colors` come from `IGameData.GetShapesConfiguration` and `GetColorScheme`, keyed by
`scenario.ResearchConfig.ShapesConfigurationId` / `ColorSchemeConfigurationId`. The layer cap is
`scenario.ResearchConfig.MaxShapeLayers`, so a scenario with a lower cap can be given a shorter
chain rather than a broken one.

### Pricing it

Vanilla's numbers are authored data, but they are readable out of `resources.assets` —
`Scenarios/Classic/Regular/SideTasks/SG_Fluids_1` is a five step chain paying **1, 2, 2, 4 and 12
research points** and **30 to 60 chunk limit**, for **1200 to 2500 shapes** a step. Price against
that rather than inventing a scale.

## Gotchas

- **A side quest with no rewards is deleted.** `RemoveUpgradesWithZeroRewards` prunes any
  non-`ResearchLevel` upgrade whose reward list is empty. It runs from `ResearchProgression`'s
  constructor, which has already finished by the time a rewirer fires — so a reward-less quest
  survives injection and then vanishes the first time anything calls `TryRemoveUpgrade`, which
  prunes again. Give every quest a reward.
- **`Validate()` has already run too.** A `RequiredUpgradeId` that does not exist would have thrown
  during construction; added afterwards it throws nothing and simply never resolves — the group is
  invisible forever with nothing in the log. Look the id up with `TryGetUpgrade` first.
  **Scenarios do not share a late game**: `Milestone_PostFinal_Tier1` exists in the quad scenarios,
  the hexagonal one stops at `Milestone_Final` after nine milestones, and the converter ones use
  `ConverterMilestoneTier*`. Name candidates in order and take the first that resolves. If none do,
  prefer skipping the content to registering it un-gated — a scenario that lacks the gate usually
  lacks the machine, so an un-gated quest there is simply an impossible one.
- **`debug.export-game-data` tells you when a gate is reachable, and when a colour is.** Ask, for
  each colour, the earliest milestone goal and side quest in which vanilla asks for it. In the
  shipped scenarios `u r b` are free, `g` arrives with fluid extraction, `c y w` with the mixer,
  `m` with `CBSpecial_SpaceFloor3`, and `k` is post-final — which is easy to get wrong, because
  black looks like an ordinary colour and is not.
- **Quest ids are save state.** `ResearchUpgradeId` is what completion is recorded against, so
  prefix them with the mod and never reuse one for a different quest. Removing the mod from a save
  with completed quests leaves dangling ids — `AffectsSaveGames` covers that.
- **`P-` is a legal shape code and `Pu` is not.** `ShapeHashParser` rejects a colour on a part whose
  `AllowColor` is false, and the pin's is false — verified in `resources.assets`, where `PinQuad`
  and `PinHex` both carry `_AllowColor = 0`. Crystal is the opposite: `AllowColor` true,
  `AllowChangingColor` false, so `cr` parses and `c-` does not.
- **Unlock and toolbar are separate.** Content needs both; a correct toolbar entry with
  no unlock is invisible in-game, which looks exactly like a broken toolbar index.
- The `ResearchUpgradeId` in `SideUpgradePresentationData` is your **own** node's id —
  `SideUpgradeBuilder.Build` uses it as `_UpgradesById[...]`. Parents come from
  `WithCustomRequirements(mechanics, upgrades)` or `CopyingRequirements(...)`. Get the
  parent wrong and your node either vanishes or lands in an odd part of the tree.
- The category string (`"Buildings"`) must match an existing research category, or the
  node has nowhere to render — `ResearchProgression`'s constructor logs
  `Unknown/non-configured upgrade category` and moves on. Categories are authored per
  scenario (`serialized.ScenarioContent.UpgradeCategories`), so a hardcoded one can be
  right in the main scenario and wrong in the converter. Copying the category off a
  vanilla node you can find *structurally* — by a reward it grants rather than by its id —
  survives that. Search `progression.AllUpgrades`, not `SideUpgrades`: a `ResearchLevel`
  is an `IResearchUpgrade` too and grants rewards the same way, so whether vanilla content
  is unlocked by a milestone or by a shop node is not something you have to know.
- Milestone **indices** shift between game versions; milestone **ids** are stabler.
  Prefer ids unless you specifically mean "the last one".
- Titles and descriptions are translation keys, not literal text — see
  [Add translations](add-translations.md).
