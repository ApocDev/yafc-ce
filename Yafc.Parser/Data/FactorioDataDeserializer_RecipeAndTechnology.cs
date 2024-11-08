﻿using System;
using System.Linq;
using Yafc.Model;

namespace Yafc.Parser;

internal partial class FactorioDataDeserializer {
    private void DeserializeRecipe(LuaTable table, ErrorCollector errorCollector) {
        var recipe = DeserializeCommon<Recipe>(table, "recipe");
        LoadRecipeData(recipe, table, errorCollector);
        _ = table.Get("category", out string recipeCategory, "crafting");
        recipeCategories.Add(recipeCategory, recipe);
        AllowedEffects allowedEffects = AllowedEffects.None;
        if (table.Get("allow_consumption", true)) {
            allowedEffects |= AllowedEffects.Consumption;
        }
        if (table.Get("allow_speed", true)) {
            allowedEffects |= AllowedEffects.Speed;
        }
        if (table.Get("allow_productivity", false)) {
            allowedEffects |= AllowedEffects.Productivity;
        }
        if (table.Get("allow_pollution", true)) {
            allowedEffects |= AllowedEffects.Pollution;
        }
        if (table.Get("allow_quality", true)) {
            allowedEffects |= AllowedEffects.Quality;
        }

        recipe.allowedEffects = allowedEffects;
        if (table.Get("allowed_module_categories", out LuaTable? categories)) {
            recipe.allowedModuleCategories = categories.ArrayElements<string>().ToArray();
        }
    }

    private static void DeserializeFlags(LuaTable table, RecipeOrTechnology recipe) {
        recipe.hidden = table.Get("hidden", true);
        recipe.enabled = table.Get("enabled", true);
    }

    private void DeserializeTechnology(LuaTable table, ErrorCollector errorCollector) {
        var technology = DeserializeCommon<Technology>(table, "technology");
        LoadTechnologyData(technology, table, errorCollector);
        technology.products = [new(researchUnit, 1)];
    }

    private void DeserializeQuality(LuaTable table, ErrorCollector errorCollector) {
        Quality quality = DeserializeCommon<Quality>(table, "quality");
        if (table.Get("next", out string? nextQuality)) {
            quality.nextQuality = GetObject<Quality>(nextQuality);
            quality.nextQuality.previousQuality = quality;
        }
        quality.BeaconConsumptionFactor = table.Get("beacon_power_usage_multiplier", 1f);
        quality.level = table.Get("level", 0);
    }

    private void UpdateRecipeCatalysts() {
        foreach (var recipe in allObjects.OfType<Recipe>()) {
            foreach (var product in recipe.products) {
                if (product.productivityAmount == product.amount) {
                    float catalyst = recipe.GetConsumptionPerRecipe(product.goods);

                    if (catalyst > 0f) {
                        product.SetCatalyst(catalyst);
                    }
                }
            }
        }
    }

    private void UpdateRecipeIngredientFluids() {
        foreach (var recipe in allObjects.OfType<Recipe>()) {
            foreach (var ingredient in recipe.ingredients) {
                if (ingredient.goods is Fluid fluid && fluid.variants != null) {
                    int min = -1, max = fluid.variants.Count - 1;

                    for (int i = 0; i < fluid.variants.Count; i++) {
                        var variant = fluid.variants[i];

                        if (variant.temperature < ingredient.temperature.min) {
                            continue;
                        }

                        if (min == -1) {
                            min = i;
                        }

                        if (variant.temperature > ingredient.temperature.max) {
                            max = i - 1;
                            break;
                        }
                    }

                    if (min >= 0 && max >= 0) {
                        ingredient.goods = fluid.variants[min];

                        if (max > min) {
                            Fluid[] fluidVariants = new Fluid[max - min + 1];
                            ingredient.variants = fluidVariants;
                            fluid.variants.CopyTo(min, fluidVariants, 0, max - min + 1);
                        }
                    }
                }
            }
        }
    }

    private void LoadTechnologyData(Technology technology, LuaTable table, ErrorCollector errorCollector) {
        if (table.Get("unit", out LuaTable? unit)) {
            technology.ingredients = LoadResearchIngredientList(unit);
            recipeCategories.Add(SpecialNames.Labs, technology);
        }
        else if (table.Get("research_trigger", out LuaTable? researchTriggerTable)) {
            LoadResearchTrigger(researchTriggerTable, ref technology, errorCollector);
            technology.ingredients ??= [];
            recipeCategories.Add(SpecialNames.TechnologyTrigger, technology);
        }
        else {
            errorCollector.Error($"Could not get requirement(s) to unlock {technology.name}.", ErrorSeverity.AnalysisWarning);
        }

        DeserializeFlags(table, technology);
        technology.time = unit.Get("time", 1f);
        technology.count = unit.Get("count", 1000f);

        if (table.Get("prerequisites", out LuaTable? prerequisitesList)) {
            technology.prerequisites = prerequisitesList.ArrayElements<string>().Select(GetObject<Technology>).ToArray();
        }

        if (table.Get("effects", out LuaTable? modifiers)) {
            foreach (LuaTable modifier in modifiers.ArrayElements<LuaTable>()) {
                switch (modifier.Get("type", "")) {
                    case "unlock-recipe": {
                            if (!GetRef<Recipe>(modifier, "recipe", out var recipe)) {
                                continue;
                            }

                            technology.unlockRecipes.Add(recipe);

                            break;
                        }

                    case "change-recipe-productivity": {
                            if (!GetRef<Recipe>(modifier, "recipe", out var recipe)) {
                                continue;
                            }

                            float change = modifier.Get("change", 0f);

                            technology.changeRecipeProductivity.Add(recipe, change);
                            recipe.technologyProductivity.Add(technology, change);

                            break;
                        }

                    case "unlock-quality": {
                            if (GetRef(modifier, "quality", out Quality? quality)) {
                                quality.technologyUnlock.Add(technology);
                            }
                            break;
                        }
                }
            }
        }
    }

    private Func<LuaTable, Product> LoadProduct(string typeDotName, int multiplier = 1) => table => {
        Goods? goods = LoadItemOrFluid(table, true);

        float min, max, catalyst;
        if (goods is Item) {
            if (table.Get("amount", out int effectiveAmount)) {
                min = max = effectiveAmount;
            }
            else if (table.Get("amount_min", out int effectiveMin) && table.Get("amount_max", out int effectiveMax)) {
                min = effectiveMin;
                max = effectiveMax;
            }
            else {
                throw new NotSupportedException($"Could not load amount for one of the products for {typeDotName}, possibly named '{table.Get("name", "")}'.");
            }

            float extraCountFraction = table.Get("extra_count_fraction", 0f);
            min += extraCountFraction;
            max += extraCountFraction;

            catalyst = table.Get("ignored_by_productivity", 0);
        }
        else if (goods is Fluid) {
            if (table.Get("amount", out float amount)) {
                min = max = amount;
            }
            else if (table.Get("amount_min", out min) && table.Get("amount_max", out max)) {
                // nothing to do
            }
            else {
                throw new NotSupportedException($"Could not load amount for one of the products for {typeDotName}, possibly named '{table.Get("name", "")}'.");
            }

            catalyst = table.Get("ignored_by_productivity", 0f);
        }
        else {
            throw new NotSupportedException($"Could not load one of the products for {typeDotName}, possibly named '{table.Get("name", "")}'.");
        }

        Product product = new Product(goods, min * multiplier, max * multiplier, table.Get("probability", 1f));

        if (catalyst > 0f) {
            product.SetCatalyst(catalyst);
        }

        return product;
    };

    private Product[] LoadProductList(LuaTable table, string typeDotName, bool allowSimpleSyntax) {
        if (table.Get("results", out LuaTable? resultList)) {
            return resultList.ArrayElements<LuaTable>().Select(LoadProduct(typeDotName)).Where(x => x.amount != 0).ToArray();
        }

        if (allowSimpleSyntax && table.Get("result", out string? name)) {
            return [(new Product(GetObject<Item>(name), 1))];
        }

        return [];
    }

    private Ingredient[] LoadIngredientList(LuaTable table, string typeDotName, ErrorCollector errorCollector) {
        _ = table.Get("ingredients", out LuaTable? ingredientsList);

        return ingredientsList?.ArrayElements<LuaTable>().Select(table => {
            Goods? goods = LoadItemOrFluid(table, false);

            if (goods is null) {
                errorCollector.Error($"Failed to load at least one ingredient for {typeDotName}.", ErrorSeverity.AnalysisWarning);
                return null!;
            }

            float amount;
            if (goods is Item) {
                _ = table.Get("amount", out int effectiveAmount);
                amount = effectiveAmount;
            }
            else {
                _ = table.Get("amount", out amount);
            }

            Ingredient ingredient = new Ingredient(goods, amount);

            if (goods is Fluid f) {
                ingredient.temperature = table.Get("temperature", out int temp)
                    ? new TemperatureRange(temp)
                    : new TemperatureRange(table.Get("minimum_temperature", f.temperatureRange.min), table.Get("maximum_temperature", f.temperatureRange.max));
            }

            return ingredient;
        }).Where(x => x is not null).ToArray() ?? [];
    }

    private Ingredient[] LoadResearchIngredientList(LuaTable table) {
        _ = table.Get("ingredients", out LuaTable? ingredientsList);
        return ingredientsList?.ArrayElements<LuaTable>().Select(table => {
            if (table.Get(1, out string? name) && table.Get(2, out int amount)) {
                Item goods = GetObject<Item>(name);
                Ingredient ingredient = new Ingredient(goods, amount);
                return ingredient;
            }

            return null!;
        }).Where(x => x is not null).ToArray() ?? [];
    }

    private void LoadResearchTrigger(LuaTable researchTriggerTable, ref Technology technology, ErrorCollector errorCollector) {
        if (!researchTriggerTable.Get("type", out string? type)) {
            errorCollector.Error($"Research trigger of {technology.typeDotName} does not have a type field", ErrorSeverity.MinorDataLoss);
            return;
        }

        switch (type) {
            case "craft-item":
                if (!researchTriggerTable.Get("item", out string? craftItemName)) {
                    errorCollector.Error($"Research trigger {type} of {technology.typeDotName} does not have an item field", ErrorSeverity.MinorDataLoss);
                    break;
                }
                float craftCount = researchTriggerTable.Get("count", 1);
                technology.ingredients = [new Ingredient(GetObject<Item>(craftItemName), craftCount)];
                technology.flags = RecipeFlags.HasResearchTriggerCraft;

                break;
            case "capture-spawner":
                technology.flags = RecipeFlags.HasResearchTriggerCaptureEntity;
                if (researchTriggerTable.Get("entity", out string? entity)) {
                    technology.getTriggerEntities = new(() => [((Entity)Database.objectsByTypeName["Entity." + entity])]);
                }
                else {
                    technology.getTriggerEntities = new(static () =>
                        Database.entities.all.OfType<EntitySpawner>()
                        .Where(e => e.capturedEntityName != null)
                        .ToList());
                }
                break;
            case "mine-entity":
                technology.flags = RecipeFlags.HasResearchTriggerMineEntity;
                if (!researchTriggerTable.Get("entity", out entity)) {
                    errorCollector.Error($"Research trigger {type} of {technology.typeDotName} does not have an entity field", ErrorSeverity.MinorDataLoss);
                    break;
                }
                technology.getTriggerEntities = new(() => [((Entity)Database.objectsByTypeName["Entity." + entity])]);
                break;
            default:
                errorCollector.Error($"Research trigger of {technology.typeDotName} has an unsupported type {type}", ErrorSeverity.MinorDataLoss);
                break;
        }
    }

    private void LoadRecipeData(Recipe recipe, LuaTable table, ErrorCollector errorCollector) {
        recipe.ingredients = LoadIngredientList(table, recipe.typeDotName, errorCollector);
        recipe.products = LoadProductList(table, recipe.typeDotName, allowSimpleSyntax: false);

        recipe.time = table.Get("energy_required", 0.5f);

        if (table.Get("main_product", out string? mainProductName) && mainProductName != "") {
            recipe.mainProduct = recipe.products.FirstOrDefault(x => x.goods.name == mainProductName)?.goods;
        }
        else if (recipe.products.Length == 1) {
            recipe.mainProduct = recipe.products[0]?.goods;
        }

        DeserializeFlags(table, recipe);
    }
}
