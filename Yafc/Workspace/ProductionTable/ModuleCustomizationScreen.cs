﻿using System;
using System.Linq;
using Yafc.Model;
using Yafc.UI;

namespace Yafc;

public class ModuleCustomizationScreen : PseudoScreenWithResult<ModuleTemplateBuilder> {
    private static readonly ModuleCustomizationScreen Instance = new ModuleCustomizationScreen();

    private RecipeRow? recipe;
    private ProjectModuleTemplate? template;
    private ModuleTemplateBuilder? modules;

    public static void Show(RecipeRow recipe) {
        Instance.template = null;
        Instance.recipe = recipe;
        Instance.modules = recipe.modules?.GetBuilder();
        Instance.completionCallback = (hasResult, builder) => {
            if (hasResult) {
                recipe.RecordUndo().modules = builder?.Build(recipe);
            }
        };
        _ = MainScreen.Instance.ShowPseudoScreen(Instance);
    }

    public static void Show(ProjectModuleTemplate template) {
        Instance.recipe = null;
        Instance.template = template;
        Instance.modules = template.template.GetBuilder();
        Instance.completionCallback = (hasResult, builder) => {
            if (hasResult) {
                template.RecordUndo().template = builder!.Build(template);
            }
        };
        _ = MainScreen.Instance.ShowPseudoScreen(Instance);
    }

    public override void Build(ImGui gui) {
        BuildHeader(gui, "Module customization");
        if (template != null) {
            using (gui.EnterRow()) {
                if (gui.BuildFactorioObjectButton(template.icon, ButtonDisplayStyle.Default) == Click.Left) {
                    SelectSingleObjectPanel.SelectWithNone(Database.objects.all, "Select icon", x => {
                        template.RecordUndo().icon = x;
                        Rebuild();
                    });
                }

                if (gui.BuildTextInput(template.name, out string newName, "Enter name", delayed: true) && newName != "") {
                    template.RecordUndo().name = newName;
                }
            }

            gui.BuildText("Filter by crafting buildings (Optional):");
            using var grid = gui.EnterInlineGrid(2f, 1f);

            for (int i = 0; i < template.filterEntities.Count; i++) {
                var entity = template.filterEntities[i];
                grid.Next();
                gui.BuildFactorioObjectIcon(entity, new(2, MilestoneDisplay.Contained, false));
                if (gui.BuildMouseOverIcon(Icon.Close, SchemeColor.Error)) {
                    template.RecordUndo().filterEntities.RemoveAt(i);
                }
            }

            grid.Next();

            if (gui.BuildButton(Icon.Plus, SchemeColor.Primary, SchemeColor.PrimaryAlt, size: 1.5f)) {
                bool canBeAffected(EntityCrafter x) => x.allowedEffects != AllowedEffects.None;
                bool isNotSelected(EntityCrafter x) => !template.filterEntities.Contains(x);
                bool isSuitable(EntityCrafter x) => canBeAffected(x) && isNotSelected(x);

                void doToSelectedItem(EntityCrafter selectedCrafter) {
                    template.RecordUndo().filterEntities.Add(selectedCrafter);
                    gui.Rebuild();
                }

                SelectSingleObjectPanel.Select(Database.allCrafters.Where(isSuitable),
                                               "Add module template filter",
                                               doToSelectedItem);
            }
        }

        if (modules == null) {
            if (gui.BuildButton("Enable custom modules")) {
                modules = new ModuleTemplateBuilder();
            }
        }
        else {
            ModuleEffects effects = new ModuleEffects();
            if (recipe == null || recipe.entity?.target.moduleSlots > 0) {
                gui.BuildText("Internal modules:", Font.subheader);
                gui.BuildText("Leave zero amount to fill the remaining slots");
                DrawRecipeModules(gui, null, ref effects);
            }
            else {
                gui.BuildText("This building doesn't have module slots, but can be affected by beacons");
            }

            gui.BuildText("Beacon modules:", Font.subheader);

            if (modules.beacon == null) {
                gui.BuildText("Use default parameters");
                if (gui.BuildButton("Override beacons as well")) {
                    SelectBeacon(gui);
                }

                var defaultFiller = recipe?.GetModuleFiller();
                if (defaultFiller?.GetBeaconsForCrafter(recipe?.entity?.target) is BeaconConfiguration { beacon: not null, beaconModule: not null } beaconsToUse) {
                    EntityBeacon beacon = beaconsToUse.beacon.target;
                    effects.AddModules(beaconsToUse.beaconModule, beaconsToUse.beacon.GetBeaconEfficiency() * beacon.GetProfile(beaconsToUse.beaconCount) * beacon.moduleSlots * beaconsToUse.beaconCount);
                }
            }
            else {
                if (gui.BuildFactorioObjectButtonWithText(modules.beacon) == Click.Left) {
                    SelectBeacon(gui);
                }

                string modulesNotBeacons = "Input the amount of modules, not the amount of beacons. Single beacon can hold " + modules.beacon.target.moduleSlots + " modules.";
                gui.BuildText(modulesNotBeacons, TextBlockDisplayStyle.WrappedText);
                DrawRecipeModules(gui, modules.beacon, ref effects);
            }

            if (recipe?.entity?.target.effectReceiver.baseEffect is { } baseEffect) {
                effects.productivity += baseEffect.productivity;
                effects.speed += baseEffect.speed;
                effects.consumption += baseEffect.consumption;
            }

            if (recipe != null) {
                float craftingSpeed = (recipe.entity?.GetCraftingSpeed() ?? 1f) * effects.speedMod;
                gui.BuildText("Current effects:", Font.subheader);
                gui.BuildText("Productivity bonus: " + DataUtils.FormatAmount(effects.productivity, UnitOfMeasure.Percent));
                gui.BuildText("Speed bonus: " + DataUtils.FormatAmount(effects.speedMod - 1, UnitOfMeasure.Percent) + " (Crafting speed: " +
                    DataUtils.FormatAmount(craftingSpeed, UnitOfMeasure.None) + ")");
                gui.BuildText("Quality bonus: " + DataUtils.FormatAmount(effects.qualityMod, UnitOfMeasure.Percent) + " (multiplied by quality upgrade chance)");

                string energyUsageLine = "Energy usage: " + DataUtils.FormatAmount(effects.energyUsageMod, UnitOfMeasure.Percent);

                if (recipe.entity != null) {
                    float power = effects.energyUsageMod * recipe.entity.GetPower() / recipe.entity.target.energy.effectivity;
                    if (!recipe.recipe.target.flags.HasFlagAny(RecipeFlags.UsesFluidTemperature | RecipeFlags.ScaleProductionWithPower) && recipe.entity != null) {
                        energyUsageLine += " (" + DataUtils.FormatAmount(power, UnitOfMeasure.Megawatt) + " per building)";
                    }

                    gui.BuildText(energyUsageLine);

                    float pps = craftingSpeed * (1f + MathF.Max(0f, effects.productivity)) / recipe.recipe.target.time;
                    gui.BuildText("Overall crafting speed (including productivity): " + DataUtils.FormatAmount(pps, UnitOfMeasure.PerSecond));
                    gui.BuildText("Energy cost per recipe output: " + DataUtils.FormatAmount(power / pps, UnitOfMeasure.Megajoule));
                }
                else {
                    gui.BuildText(energyUsageLine);
                }
            }
        }

        gui.AllocateSpacing(3f);
        using (gui.EnterRow(allocator: RectAllocator.RightRow)) {
            if (template == null && gui.BuildButton("Cancel")) {
                Close();
            }
            if (template != null && gui.BuildButton("Cancel (partial)")) {
                Close();
            }
            if (gui.BuildButton("Done")) {
                CloseWithResult(modules);
            }

            gui.allocator = RectAllocator.LeftRow;
            if (modules != null && recipe != null && gui.BuildRedButton("Remove module customization")) {
                CloseWithResult(null);
            }
        }
    }

    private void SelectBeacon(ImGui gui) {
        if (modules!.beacon is null) { // null-forgiving: Both calls are from places where we know modules is not null
            gui.BuildObjectQualitySelectDropDown(Database.allBeacons, sel => {
                modules.beacon = sel;
                contents.Rebuild();
            }, new("Select beacon"), Quality.Normal);
        }
        else {
            gui.BuildObjectQualitySelectDropDownWithNone(Database.allBeacons, sel => {
                modules.beacon = sel;
                contents.Rebuild();
            }, new("Select beacon"), modules.beacon.quality, quality => {
                modules.beacon = modules.beacon.With(quality);
                contents.Rebuild();
            });
        }
    }

    private Module[] GetModules(IObjectWithQuality<EntityBeacon>? beacon) {
        var modules = (beacon == null && recipe is { recipe: IObjectWithQuality<RecipeOrTechnology> rec })
            ? [.. Database.allModules.Where(rec.CanAcceptModule)]
            : Database.allModules;
        EntityWithModules? filter = (EntityWithModules?)beacon?.target ?? recipe?.entity?.target;
        if (filter == null) {
            return modules;
        }

        return [.. modules.Where(x => filter.CanAcceptModule(x.moduleSpecification))];
    }

    private void DrawRecipeModules(ImGui gui, IObjectWithQuality<EntityBeacon>? beacon, ref ModuleEffects effects) {
        int remainingModules = recipe?.entity?.target.moduleSlots ?? 0;
        using var grid = gui.EnterInlineGrid(3f, 1f);
        var list = beacon != null ? modules!.beaconList : modules!.list;// null-forgiving: Both calls are from places where we know modules is not null
        for (int i = 0; i < list.Count; i++) {
            grid.Next();
            (IObjectWithQuality<Module> module, int fixedCount) = list[i];
            DisplayAmount amount = fixedCount;
            switch (gui.BuildFactorioObjectWithEditableAmount(module, amount, ButtonDisplayStyle.ProductionTableUnscaled)) {
                case GoodsWithAmountEvent.LeftButtonClick:
                    int idx = i; // Capture the current value of i.

                    gui.BuildObjectQualitySelectDropDownWithNone(GetModules(beacon), sel => {
                        if (sel == null) {
                            list.RemoveAt(idx);
                        }
                        else {
                            list[idx] = (sel, list[idx].fixedCount);
                        }
                        gui.Rebuild();
                    }, new("Select module", DataUtils.FavoriteModule), list[idx].module.quality, quality => {
                        list[idx] = list[idx] with { module = list[idx].module.target.With(quality) };
                        gui.Rebuild();
                    });
                    break;

                case GoodsWithAmountEvent.TextEditing when amount.Value >= 0:
                    list[i] = (module, (int)amount.Value);
                    break;
            }

            if (beacon == null) {
                int count = Math.Min(remainingModules, fixedCount > 0 ? fixedCount : int.MaxValue);
                if (count > 0) {
                    effects.AddModules(module, count);
                    remainingModules -= count;
                }
            }
            else {
                int beaconCount = (modules.beaconList.Sum(x => x.fixedCount) - 1) / beacon.target.moduleSlots + 1;
                effects.AddModules(module, fixedCount * beacon.GetBeaconEfficiency() * beacon.target.GetProfile(beaconCount));
            }
        }

        grid.Next();
        if (gui.BuildButton(Icon.Plus, SchemeColor.Primary, SchemeColor.PrimaryAlt, size: 2.5f)) {
            gui.BuildObjectQualitySelectDropDown(GetModules(beacon), sel => {
                list.Add(new(sel, 0));
                gui.Rebuild();
            }, new("Select module", DataUtils.FavoriteModule), Quality.Normal);
        }
    }

    protected override void ReturnPressed() => CloseWithResult(modules);
}
