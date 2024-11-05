﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Yafc.Blueprints;
using Yafc.Model;
using Yafc.UI;

namespace Yafc;

public class ShoppingListScreen : PseudoScreen {
    private enum DisplayState { Total, Built, Missing }
    private readonly VirtualScrollList<(IObjectWithQuality<FactorioObject>, float)> list;
    private float shoppingCost, totalBuildings, totalModules;
    private bool decomposed = false;
    private static DisplayState displayState {
        get => (DisplayState)(Preferences.Instance.shoppingDisplayState >> 1);
        set {
            Preferences.Instance.shoppingDisplayState = ((int)value) << 1 | (Preferences.Instance.shoppingDisplayState & 1);
            Preferences.Instance.Save();
        }
    }
    private static bool assumeAdequate {
        get => (Preferences.Instance.shoppingDisplayState & 1) != 0;
        set {
            Preferences.Instance.shoppingDisplayState = (Preferences.Instance.shoppingDisplayState & ~1) | (value ? 1 : 0);
            Preferences.Instance.Save();
        }
    }

    private readonly List<RecipeRow> recipes;

    private ShoppingListScreen(List<RecipeRow> recipes) : base(42) {
        list = new(30f, new Vector2(float.PositiveInfinity, 2), ElementDrawer);
        this.recipes = recipes;
        RebuildData();
    }

    private void ElementDrawer(ImGui gui, (IObjectWithQuality<FactorioObject> obj, float count) element, int index) {
        using (gui.EnterRow()) {
            gui.BuildFactorioObjectIcon(element.obj.target, new IconDisplayStyle(2, MilestoneDisplay.Contained, false));
            gui.RemainingRow().BuildText(DataUtils.FormatAmount(element.count, UnitOfMeasure.None, "x") + ": " + element.obj.target.locName);
        }
        _ = gui.BuildFactorioObjectButtonBackground(gui.lastRect, element.obj);
    }

    public static void Show(List<RecipeRow> recipes) => _ = MainScreen.Instance.ShowPseudoScreen(new ShoppingListScreen(recipes));

    private void RebuildData() {
        decomposed = false;

        // Count buildings and modules
        Dictionary<IObjectWithQuality<FactorioObject>, int> counts = [];
        foreach (RecipeRow recipe in recipes) {
            if (recipe.entity != null) {
                ObjectWithQuality<FactorioObject> shopItem = new(recipe.entity.target.itemsToPlace?.FirstOrDefault() ?? (FactorioObject)recipe.entity.target, recipe.entity.quality);
                _ = counts.TryGetValue(shopItem, out int prev);
                int builtCount = recipe.builtBuildings ?? (assumeAdequate ? MathUtils.Ceil(recipe.buildingCount) : 0);
                int displayCount = displayState switch {
                    DisplayState.Total => MathUtils.Ceil(recipe.buildingCount),
                    DisplayState.Built => builtCount,
                    DisplayState.Missing => MathUtils.Ceil(Math.Max(recipe.buildingCount - builtCount, 0)),
                    _ => throw new InvalidOperationException(nameof(displayState) + " has an unrecognized value.")
                };
                counts[shopItem] = prev + displayCount;
                if (recipe.usedModules.modules != null) {
                    foreach ((Module module, int moduleCount, bool beacon) in recipe.usedModules.modules) {
                        if (!beacon) {
                            _ = counts.TryGetValue(new ObjectWithQuality<FactorioObject>(module, Quality.Normal), out prev);
                            counts[new ObjectWithQuality<FactorioObject>(module, Quality.Normal)] = prev + displayCount * moduleCount;
                        }
                    }
                }
            }
        }
        list.data = [.. counts.Where(x => x.Value > 0).Select(x => (x.Key, Value: (float)x.Value)).OrderByDescending(x => x.Value)];

        // Summarize building requirements
        float cost = 0f, buildings = 0f, modules = 0f;
        decomposed = false;
        foreach ((IObjectWithQuality<FactorioObject> obj, float count) in list.data) {
            if (obj.target is Module module) {
                modules += count;
            }
            else if (obj.target is Entity or Item) {
                buildings += count;
            }
            cost += obj.target.Cost() * count;
        }
        shoppingCost = cost;
        totalBuildings = buildings;
        totalModules = modules;
    }

    private static readonly (string, string?)[] displayStateOptions = [
        ("Total buildings", "Display the total number of buildings required, ignoring the built building count."),
        ("Built buildings", "Display the number of buildings that are reported in built building count."),
        ("Missing buildings", "Display the number of additional buildings that need to be built.")];
    private static readonly (string, string?)[] assumeAdequateOptions = [
        ("No buildings", "When the built building count is not specified, behave as if it was set to 0."),
        ("Enough buildings", "When the built building count is not specified, behave as if it matches the required building count.")];

    public override void Build(ImGui gui) {
        BuildHeader(gui, "Shopping list");
        gui.BuildText(
            "Total cost of all objects: " + DataUtils.FormatAmount(shoppingCost, UnitOfMeasure.None, "¥") + ", buildings: " +
            DataUtils.FormatAmount(totalBuildings, UnitOfMeasure.None) + ", modules: " + DataUtils.FormatAmount(totalModules, UnitOfMeasure.None), TextBlockDisplayStyle.Centered);
        using (gui.EnterRow()) {
            if (gui.BuildRadioGroup(displayStateOptions, (int)displayState, out int newSelected)) {
                displayState = (DisplayState)newSelected;
                RebuildData();
            }
        }
        using (gui.EnterRow()) {
            SchemeColor textColor = displayState == DisplayState.Total ? SchemeColor.PrimaryTextFaint : SchemeColor.PrimaryText;
            gui.BuildText("When not specified, assume:", TextBlockDisplayStyle.Default(textColor), topOffset: .15f);
            if (gui.BuildRadioGroup(assumeAdequateOptions, assumeAdequate ? 1 : 0, out int newSelected, enabled: displayState != DisplayState.Total)) {
                assumeAdequate = newSelected == 1;
                RebuildData();
            }
        }
        gui.AllocateSpacing(1f);
        list.Build(gui);
        using (gui.EnterRow(allocator: RectAllocator.RightRow)) {
            if (gui.BuildButton("Done")) {
                Close();
            }

            if (gui.BuildButton("Decompose", active: !decomposed)) {
                Decompose();
            }

            if (gui.BuildButton("Export to blueprint", SchemeColor.Grey)) {
                gui.ShowDropDown(ExportBlueprintDropdown);
            }
        }
    }

    private List<(T, int)> ExportGoods<T>() where T : Goods {
        List<(T, int)> items = [];
        foreach (((FactorioObject element, _), float amount) in list.data) {
            int rounded = MathUtils.Round(amount);
            if (rounded == 0) {
                continue;
            }

            if (element is T g) {
                items.Add((g, rounded));
            }
            else if (element is Entity e && e.itemsToPlace.Length > 0) {
                items.Add(((T)(object)e.itemsToPlace[0], rounded));
            }
        }

        return items;
    }

    private void ExportBlueprintDropdown(ImGui gui) {
        gui.BuildText("Blueprint string will be copied to clipboard", TextBlockDisplayStyle.WrappedText);
        if (Database.objectsByTypeName.TryGetValue("Entity.constant-combinator", out var combinator)
            && gui.BuildFactorioObjectButtonWithText(combinator) == Click.Left && gui.CloseDropdown()) {

            _ = BlueprintUtilities.ExportConstantCombinators("Shopping list", ExportGoods<Goods>());
        }

        foreach (var container in Database.allContainers) {
            if (container.logisticMode == "requester" && gui.BuildFactorioObjectButtonWithText(container) == Click.Left && gui.CloseDropdown()) {
                _ = BlueprintUtilities.ExportRequesterChests("Shopping list", ExportGoods<Item>(), container);
            }
        }
    }

    private static Recipe? FindSingleProduction(Recipe[] production) {
        Recipe? current = null;
        foreach (Recipe recipe in production) {
            if (recipe.IsAccessible()) {
                if (current != null) {
                    return null;
                }

                current = recipe;
            }
        }

        return current;
    }

    private void Decompose() {
        decomposed = true;
        Queue<IObjectWithQuality<FactorioObject>> decompositionQueue = [];
        Dictionary<IObjectWithQuality<FactorioObject>, float> decomposeResult = [];

        void addDecomposition(FactorioObject obj, Quality quality, float amount) {
            ObjectWithQuality<FactorioObject> key = new(obj, quality);
            if (!decomposeResult.TryGetValue(key, out float prev)) {
                decompositionQueue.Enqueue(key);
            }

            decomposeResult[key] = prev + amount;
        }
        foreach (((FactorioObject obj, Quality quality), float count) in list.data) {
            addDecomposition(obj, quality, count);
        }

        int steps = 0;
        while (decompositionQueue.Count > 0) {
            var elem = decompositionQueue.Dequeue();
            float amount = decomposeResult[elem];
            if (elem.target is Entity e && e.itemsToPlace.Length == 1) {
                addDecomposition(e.itemsToPlace[0], elem.quality, amount);
            }
            else if (elem.target is Recipe rec) {
                if (rec.HasIngredientVariants()) {
                    continue;
                }

                foreach (var ingredient in rec.ingredients) {
                    if (ingredient.goods is Fluid) {
                        addDecomposition(ingredient.goods, Quality.Normal, ingredient.amount * amount);
                    }
                    else {
                        addDecomposition(ingredient.goods, elem.quality, ingredient.amount * amount);
                    }
                }
            }
            else if (elem.target is Goods g && (g.usages.Length <= 5 || (g is Item item && (item.factorioType != "item" || item.placeResult != null)))
                && (rec = FindSingleProduction(g.production)!) != null) {

                addDecomposition(g.production[0], elem.quality, amount / rec.GetProductionPerRecipe(g));
            }
            else {
                continue;
            }

            _ = decomposeResult.Remove(elem);
            if (steps++ > 1000) {
                break;
            }
        }

        list.data = [.. decomposeResult.Select(x => (x.Key, x.Value)).OrderByDescending(x => x.Value)];
    }
}
