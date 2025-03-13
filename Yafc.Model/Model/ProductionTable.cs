﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Google.OrTools.LinearSolver;
using Serilog;
using Yafc.UI;

namespace Yafc.Model;

public struct ProductionTableFlow(IObjectWithQuality<Goods> goods, float amount, ProductionLink? link) {
    public IObjectWithQuality<Goods> goods = goods;
    public float amount = amount;
    public ProductionLink? link = link;
}

public class ProductionTable : ProjectPageContents, IComparer<ProductionTableFlow>, IElementGroup<RecipeRow> {
    private static readonly ILogger logger = Logging.GetLogger<ProductionTable>();
    [SkipSerialization] public Dictionary<IObjectWithQuality<Goods>, ProductionLink> linkMap { get; } = [];
    List<RecipeRow> IElementGroup<RecipeRow>.elements => recipes;
    [NoUndo]
    public bool expanded { get; set; } = true;
    public List<ProductionLink> links { get; } = [];
    public List<RecipeRow> recipes { get; } = [];
    public ProductionTableFlow[] flow { get; private set; } = [];
    public ModuleFillerParameters? modules { get; } // If you add a setter for this, ensure it calls RecipeRow.ModuleFillerParametersChanging().
    public bool containsDesiredProducts { get; private set; }

    public ProductionTable(ModelObject owner) : base(owner) {
        if (owner is ProjectPage) {
            modules = new ModuleFillerParameters(this);
        }
    }

    protected internal override void ThisChanged(bool visualOnly) {
        RebuildLinkMap();
        if (owner is ProjectPage page) {
            page.ContentChanged(visualOnly);
        }
        else if (owner is RecipeRow recipe) {
            recipe.ThisChanged(visualOnly);
        }
    }

    public void RebuildLinkMap() {
        linkMap.Clear();

        foreach (var link in links) {
            linkMap[link.goods] = link;
        }
    }

    private void Setup(List<IRecipeRow> allRecipes, List<IProductionLink> allLinks) {
        containsDesiredProducts = false;
        foreach (var link in links) {
            if (link.amount != 0f) {
                containsDesiredProducts = true;
            }

            allLinks.Add(link);
            link.capturedRecipes.Clear();
        }

        foreach (var recipe in recipes) {
            if (!recipe.enabled) {
                ClearDisabledRecipeContents(recipe);
                continue;
            }

            recipe.hierarchyEnabled = true;
            allRecipes.Add(recipe);
            recipe.subgroup?.Setup(allRecipes, allLinks);
        }
    }

    private static void ClearDisabledRecipeContents(RecipeRow recipe) {
        recipe.recipesPerSecond = 0;
        recipe.parameters = RecipeParameters.Empty;
        recipe.hierarchyEnabled = false;
        var subgroup = recipe.subgroup;

        if (subgroup != null) {
            subgroup.flow = [];

            foreach (var link in subgroup.links) {
                link.flags = 0;
                link.linkFlow = 0;
            }
            foreach (var sub in subgroup.recipes) {
                ClearDisabledRecipeContents(sub);
            }
        }
    }

    public bool Search(SearchQuery query) {
        bool hasMatch = false;

        foreach (var recipe in recipes) {
            recipe.visible = false;

            if (recipe.subgroup != null && recipe.subgroup.Search(query)) {
                goto match;
            }

            if (recipe.recipe.Match(query) || recipe.fuel.Match(query) || (recipe.entity?.target).Match(query)) {
                goto match;
            }

            foreach (var ingr in recipe.Ingredients) {
                if (ingr.Goods.Match(query)) {
                    goto match;
                }
            }

            foreach (var product in recipe.Products) {
                if (product.Goods.Match(query)) {
                    goto match;
                }
            }

            continue; // no match;
match:
            hasMatch = true;
            recipe.visible = true;
        }

        if (hasMatch) {
            return true;
        }

        foreach (var link in links) {
            if (link.goods.Match(query)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Add a recipe or technology to this table, and configure the crafter and fuel to reasonable values.
    /// </summary>
    /// <param name="recipe">The recipe or technology to add.</param>
    /// <param name="ingredientVariantComparer">The comparer to use when deciding which fluid variants to use.</param>
    /// <param name="selectedFuel">If not <see langword="null"/>, this method will select a crafter or lab that can use this fuel, assuming such an entity exists.
    /// For example, if the selected fuel is coal, the recipe will be configured with a burner assembler/lab if any are available.</param>
    /// <param name="spentFuel"></param>
    public void AddRecipe(ObjectWithQuality<RecipeOrTechnology> recipe, IComparer<Goods> ingredientVariantComparer,
        ObjectWithQuality<Goods>? selectedFuel = null, IObjectWithQuality<Goods>? spentFuel = null) {

        RecipeRow recipeRow = new RecipeRow(this, recipe);
        this.RecordUndo().recipes.Add(recipeRow);
        EntityCrafter? selectedFuelCrafter = GetSelectedFuelCrafter(recipe.target, selectedFuel);
        EntityCrafter? spentFuelRecipeCrafter = GetSpentFuelCrafter(recipe.target, spentFuel);

        recipeRow.entity = (selectedFuelCrafter ?? spentFuelRecipeCrafter ?? recipe.target.crafters.AutoSelect(DataUtils.FavoriteCrafter), Quality.Normal);

        if (recipeRow.entity != null) {
            recipeRow.fuel = GetSelectedFuel(selectedFuel, recipeRow)
                ?? GetFuelForSpentFuel(spentFuel, recipeRow)
                ?? recipeRow.entity.target.energy?.fuels.AutoSelect(DataUtils.FavoriteFuel).With(Quality.Normal);
        }

        foreach (RecipeRowIngredient ingredient in recipeRow.Ingredients) {
            List<Fluid>? variants = ingredient.Goods?.target.fluid?.variants;
            if (variants != null) {
                _ = recipeRow.variants.Add(variants.AutoSelect(ingredientVariantComparer)!); // null-forgiving: variants is never empty, and AutoSelect never returns null from a non-empty collection (of non-null items).
            }
        }
    }

    private static EntityCrafter? GetSelectedFuelCrafter(RecipeOrTechnology recipe, ObjectWithQuality<Goods>? selectedFuel) =>
        selectedFuel?.target.fuelFor.OfType<EntityCrafter>()
            .Where(e => e.recipes.Contains(recipe))
            .AutoSelect(DataUtils.FavoriteCrafter);

    private static EntityCrafter? GetSpentFuelCrafter(RecipeOrTechnology recipe, IObjectWithQuality<Goods>? spentFuel) {
        if (spentFuel is null) {
            return null;
        }

        return recipe.crafters
            .Where(c => c.energy.fuels.OfType<Item>().Any(e => e.fuelResult == spentFuel.target))
            .AutoSelect(DataUtils.FavoriteCrafter);
    }

    private static ObjectWithQuality<Goods>? GetFuelForSpentFuel(IObjectWithQuality<Goods>? spentFuel, [NotNull] RecipeRow recipeRow) {
        if (spentFuel is null) {
            return null;
        }

        return recipeRow.entity?.target.energy.fuels.Where(e => spentFuel.target.miscSources.Contains(e))
            .AutoSelect(DataUtils.FavoriteFuel).With(spentFuel.quality);
    }

    private static ObjectWithQuality<Goods>? GetSelectedFuel(ObjectWithQuality<Goods>? selectedFuel, [NotNull] RecipeRow recipeRow) =>
        // Skipping AutoSelect since there will only be one result at most.
        recipeRow.entity?.target.energy?.fuels.FirstOrDefault(e => e == selectedFuel?.target)?.With(selectedFuel!.quality);

    /// <summary>
    /// Get all <see cref="RecipeRow"/>s contained in this <see cref="ProductionTable"/>, in a depth-first ordering. (The same as in the UI when all nested tables are expanded.)
    /// </summary>
    /// <param name="skip">Filter out all these recipes and do not step into their childeren.</param>
    public IEnumerable<RecipeRow> GetAllRecipes(ProductionTable? skip = null) => this == skip ? [] : recipes
        .SelectMany<RecipeRow, RecipeRow>(row => [row, .. row?.subgroup?.GetAllRecipes(skip) ?? []]);

    private static void AddFlow(RecipeRow recipe, Dictionary<IObjectWithQuality<Goods>, (double prod, double cons)> summer) {
        foreach (var product in recipe.Products) {
            _ = summer.TryGetValue(product.Goods!, out var prev); // Null-forgiving: recipe is always enabled, so products are never blank.
            double amount = product.Amount;
            prev.prod += amount;
            summer[product.Goods!] = prev;
        }

        foreach (var ingredient in recipe.Ingredients) {
            var linkedGoods = ingredient.Goods;

            if (linkedGoods is not null) {
                _ = summer.TryGetValue(linkedGoods, out var prev);
                prev.cons += ingredient.Amount;
                summer[linkedGoods] = prev;
            }
            else {
                Debug.WriteLine("linkedGoods should not have been null here.");
            }
        }

        if (recipe.fuel != null && !float.IsNaN(recipe.parameters.fuelUsagePerSecondPerBuilding)) {
            _ = summer.TryGetValue(recipe.fuel, out var prev);
            double fuelUsage = recipe.fuelUsagePerSecond;
            prev.cons += fuelUsage;
            summer[recipe.fuel] = prev;
        }
    }

    private void CalculateFlow(RecipeRow? include) {
        Dictionary<IObjectWithQuality<Goods>, (double prod, double cons)> flowDict = [];

        if (include != null) {
            AddFlow(include, flowDict);
        }

        foreach (var recipe in recipes) {
            if (!recipe.enabled) {
                continue;
            }

            if (recipe.subgroup != null) {
                recipe.subgroup.CalculateFlow(recipe);
                foreach (var elem in recipe.subgroup.flow) {
                    _ = flowDict.TryGetValue(elem.goods, out var prev);

                    if (elem.amount > 0f) {
                        prev.prod += elem.amount;
                    }
                    else {
                        prev.cons -= elem.amount;
                    }

                    flowDict[elem.goods] = prev;
                }
            }
            else {
                AddFlow(recipe, flowDict);
            }
        }

        foreach (ProductionLink link in links) {
            (double prod, double cons) flowParams;

            if (!link.flags.HasFlagAny(ProductionLink.Flags.LinkNotMatched)) {
                _ = flowDict.Remove(link.goods, out flowParams);
            }
            else {
                _ = flowDict.TryGetValue(link.goods, out flowParams);
                if (Math.Abs(flowParams.prod - flowParams.cons) > 1e-8f && link.owner.owner is RecipeRow recipe && recipe.owner.FindLink(link.goods, out var parent)) {
                    parent.flags |= ProductionLink.Flags.ChildNotMatched | ProductionLink.Flags.LinkNotMatched;
                }
            }

            link.linkFlow = (float)flowParams.prod;
        }

        ProductionTableFlow[] flowArr = new ProductionTableFlow[flowDict.Count];
        int index = 0;

        foreach (var (k, (prod, cons)) in flowDict) {
            _ = FindLink(k, out var link);
            flowArr[index++] = new ProductionTableFlow(k, (float)(prod - cons), link);
        }

        Array.Sort(flowArr, 0, flowArr.Length, this);
        flow = flowArr;
    }

    /// <summary>
    /// Add/update the variable value for the constraint with the given amount, and store the recipe to the production link.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddLinkCoefficient(Constraint cst, Variable var, IProductionLink link, IRecipeRow recipe, float amount) {
        // GetCoefficient will return 0 when the variable is not available in the constraint
        amount += (float)cst.GetCoefficient(var);
        _ = link.capturedRecipes.Add(recipe);
        cst.SetCoefficient(var, amount);
    }

    public override async Task<string?> Solve(ProjectPage page) {
        using var productionTableSolver = DataUtils.CreateSolver();
        var objective = productionTableSolver.Objective();
        objective.SetMinimization();
        List<IRecipeRow> allRecipes = [];
        List<IProductionLink> allLinks = [];
        Setup(allRecipes, allLinks);
        Variable[] vars = new Variable[allRecipes.Count];
        float[] objCoefficients = new float[allRecipes.Count];

        for (int i = 0; i < allRecipes.Count; i++) {
            var recipe = allRecipes[i];
            recipe.parameters = RecipeParameters.CalculateParameters(recipe);
            var variable = productionTableSolver.MakeNumVar(0f, double.PositiveInfinity, recipe.SolverName);

            if (recipe.fixedBuildings > 0f) {
                double fixedRps = (double)recipe.fixedBuildings / recipe.parameters.recipeTime;
                variable.SetBounds(fixedRps, fixedRps);
            }
            vars[i] = variable;
        }

        Constraint[] constraints = new Constraint[allLinks.Count];

        for (int i = 0; i < allLinks.Count; i++) {
            var link = allLinks[i];
            float min = link.algorithm == LinkAlgorithm.AllowOverConsumption ? float.NegativeInfinity : link.amount;
            float max = link.algorithm == LinkAlgorithm.AllowOverProduction ? float.PositiveInfinity : link.amount;
            var constraint = productionTableSolver.MakeConstraint(min, max, link.goods.QualityName() + "_recipe");
            constraints[i] = constraint;
            link.solverIndex = i;
            link.flags = link.amount > 0 ? ProductionLink.Flags.HasConsumption : link.amount < 0 ? ProductionLink.Flags.HasProduction : 0;
        }

        for (int i = 0; i < allRecipes.Count; i++) {
            var recipe = allRecipes[i];
            var recipeVar = vars[i];
            var links = recipe.links;

            foreach (var product in recipe.ProductsForSolver) {
                if (product.Amount <= 0f) {
                    continue;
                }

                if (recipe.FindLink(product.Goods, out var link)) {
                    link.flags |= ProductionLink.Flags.HasProduction;
                    float added = product.Amount;
                    AddLinkCoefficient(constraints[link.solverIndex], recipeVar, link, recipe, added);
                    float cost = product.Goods.target.Cost();

                    if (cost > 0f) {
                        objCoefficients[i] += added * cost;
                    }
                }

                links.products[product.LinkIndex, product.Goods.quality] = link as ProductionLink;
            }

            foreach (var ingredient in recipe.IngredientsForSolver) {
                if (recipe.FindLink(ingredient.Goods, out var link)) {
                    link.flags |= ProductionLink.Flags.HasConsumption;
                    AddLinkCoefficient(constraints[link.solverIndex], recipeVar, link, recipe, -ingredient.Amount);
                }

                links.ingredients[ingredient.LinkIndex] = link as ProductionLink;
            }

            links.fuel = links.spentFuel = null;

            if (recipe.fuel != null) {
                float fuelAmount = recipe.parameters.fuelUsagePerSecondPerRecipe;

                if (recipe.FindLink(recipe.fuel, out var link)) {
                    links.fuel = link as ProductionLink;
                    link.flags |= ProductionLink.Flags.HasConsumption;
                    AddLinkCoefficient(constraints[link.solverIndex], recipeVar, link, recipe, -fuelAmount);
                }

                if (recipe.fuel.FuelResult() is IObjectWithQuality<Item> spentFuel && recipe.FindLink(spentFuel, out link)) {
                    links.spentFuel = link as ProductionLink;
                    link.flags |= ProductionLink.Flags.HasProduction;
                    AddLinkCoefficient(constraints[link.solverIndex], recipeVar, link, recipe, fuelAmount);

                    if (spentFuel.target.Cost() > 0f) {
                        objCoefficients[i] += fuelAmount * spentFuel.target.Cost();
                    }
                }
            }
        }

        foreach (var link in allLinks) {
            link.notMatchedFlow = 0f;

            if (!link.flags.HasFlags(ProductionLink.Flags.HasProductionAndConsumption)) {
                if (!link.flags.HasFlagAny(ProductionLink.Flags.HasProductionAndConsumption) && !link.owner.HasDisabledRecipeReferencing(link.goods)) {
                    _ = link.owner.RecordUndo(true).links.Remove((link as ProductionLink)!); // null-forgiving: removing null from a non-null collection is a no-op.
                }

                link.flags |= ProductionLink.Flags.LinkNotMatched;
                constraints[link.solverIndex].SetBounds(double.NegativeInfinity, double.PositiveInfinity); // remove link constraints
            }
        }

        await Ui.ExitMainThread();

        for (int i = 0; i < allRecipes.Count; i++) {
            objective.SetCoefficient(vars[i], allRecipes[i].BaseCost);
        }

        var result = productionTableSolver.Solve();

        if (result is not Solver.ResultStatus.FEASIBLE and not Solver.ResultStatus.OPTIMAL) {
            objective.Clear();
            var (deadlocks, splits) = GetInfeasibilityCandidates(allRecipes);
            (Variable? positive, Variable? negative)[] slackVars = new (Variable? positive, Variable? negative)[allLinks.Count];

            // Solution does not exist. Adding slack variables to find the reason
            foreach (var link in deadlocks) {
                // Adding negative slack to possible deadlocks (loops)
                var constraint = constraints[link.solverIndex];
                float cost = MathF.Abs(link.goods.target.Cost());
                var negativeSlack = productionTableSolver.MakeNumVar(0d, double.PositiveInfinity, "negative-slack." + link.goods.QualityName());
                constraint.SetCoefficient(negativeSlack, cost);
                objective.SetCoefficient(negativeSlack, 1f);
                slackVars[link.solverIndex].negative = negativeSlack;
            }

            foreach (var link in splits) {
                // Adding positive slack to splits
                float cost = MathF.Abs(link.goods.target.Cost());
                var constraint = constraints[link.solverIndex];
                var positiveSlack = productionTableSolver.MakeNumVar(0d, double.PositiveInfinity, "positive-slack." + link.goods.QualityName());
                constraint.SetCoefficient(positiveSlack, -cost);
                objective.SetCoefficient(positiveSlack, 1f);
                slackVars[link.solverIndex].positive = positiveSlack;
            }

            result = productionTableSolver.Solve();

            logger.Information("Solver finished with result {result}", result);
            await Ui.EnterMainThread();

            if (result is Solver.ResultStatus.OPTIMAL or Solver.ResultStatus.FEASIBLE) {
                List<IProductionLink> linkList = [];

                for (int i = 0; i < allLinks.Count; i++) {
                    var (posSlack, negSlack) = slackVars[i];

                    if (posSlack is not null && posSlack.BasisStatus() != Solver.BasisStatus.AT_LOWER_BOUND) {
                        linkList.Add(allLinks[i]);
                        allLinks[i].notMatchedFlow += (float)posSlack.SolutionValue();
                    }

                    if (negSlack is not null && negSlack.BasisStatus() != Solver.BasisStatus.AT_LOWER_BOUND) {
                        linkList.Add(allLinks[i]);
                        allLinks[i].notMatchedFlow -= (float)negSlack.SolutionValue();
                    }
                }

                foreach (var link in linkList) {
                    if (link.notMatchedFlow == 0f) {
                        continue;
                    }

                    link.flags |= ProductionLink.Flags.LinkNotMatched | ProductionLink.Flags.LinkRecursiveNotMatched;
                    RecipeRow? ownerRecipe = link.owner.owner as RecipeRow;

                    while (ownerRecipe != null) {
                        if (link.notMatchedFlow > 0f) {
                            ownerRecipe.parameters.warningFlags |= WarningFlags.OverproductionRequired;
                        }
                        else {
                            ownerRecipe.parameters.warningFlags |= WarningFlags.DeadlockCandidate;
                        }

                        ownerRecipe = ownerRecipe.owner.owner as RecipeRow;
                    }
                }

                foreach (var recipe in allRecipes) {
                    FindAllRecipeLinks(recipe, linkList, linkList);

                    foreach (var link in linkList) {
                        if (link.flags.HasFlags(ProductionLink.Flags.LinkRecursiveNotMatched)) {
                            if (link.notMatchedFlow > 0f) {
                                recipe.parameters.warningFlags |= WarningFlags.OverproductionRequired;
                            }
                            else {
                                recipe.parameters.warningFlags |= WarningFlags.DeadlockCandidate;
                            }
                        }
                    }
                }
            }
            else {
                if (result == Solver.ResultStatus.INFEASIBLE) {
                    return "YAFC failed to solve the model and to find deadlock loops. As a result, the model was not updated.";
                }

                if (result == Solver.ResultStatus.ABNORMAL) {
                    return "This model has numerical errors (probably too small or too large numbers) and cannot be solved";
                }

                return "Unaccounted error: MODEL_" + result;
            }
        }

        for (int i = 0; i < allLinks.Count; i++) {
            var link = allLinks[i];
            var constraint = constraints[i];

            if (constraint == null) {
                continue;
            }

            var basisStatus = constraint.BasisStatus();

            if ((basisStatus == Solver.BasisStatus.BASIC || basisStatus == Solver.BasisStatus.FREE) && (link.notMatchedFlow != 0 || link.algorithm != LinkAlgorithm.Match)) {
                link.flags |= ProductionLink.Flags.LinkNotMatched;
            }

        }

        for (int i = 0; i < allRecipes.Count; i++) {
            var recipe = allRecipes[i];
            recipe.recipesPerSecond = vars[i].SolutionValue();
        }

        bool builtCountExceeded = CheckBuiltCountExceeded();

        CalculateFlow(null);

        return builtCountExceeded ? "This model requires more buildings than are currently built" : null;
    }

    /// <summary>
    /// Search the disabled recipes in this table and see if any of them produce or consume <paramref name="goods"/>. If they do, the corresponding <see cref="ProductionLink"/> should not be deleted.
    /// </summary>
    /// <param name="goods">The <see cref="Goods"/> that might have its link removed.</param>
    /// <returns><see langword="true"/> if the link should be preserved, or <see langword="false"/> if it is ok to delete the link.</returns>
    private bool HasDisabledRecipeReferencing(IObjectWithQuality<Goods> goods)
        => GetAllRecipes().Any(row => !row.hierarchyEnabled
        && (row.fuel == goods || row.recipe.target.ingredients.Any(i => i.goods == goods) || row.recipe.target.products.Any(p => p.goods == goods)));

    private bool CheckBuiltCountExceeded() {
        bool builtCountExceeded = false;

        for (int i = 0; i < recipes.Count; i++) {
            var recipe = recipes[i];

            if (recipe.buildingCount > recipe.builtBuildings) {
                recipe.parameters.warningFlags |= WarningFlags.ExceedsBuiltCount;
                builtCountExceeded = true;
            }
            else if (recipe.subgroup != null) {
                if (recipe.subgroup.CheckBuiltCountExceeded()) {
                    recipe.parameters.warningFlags |= WarningFlags.ExceedsBuiltCount;
                    builtCountExceeded = true;
                }
            }
        }

        return builtCountExceeded;
    }

    private static void FindAllRecipeLinks(IRecipeRow recipe, List<IProductionLink> sources, List<IProductionLink> targets) {
        sources.Clear();
        targets.Clear();

        foreach (var link in recipe.links.products) {
            if (link != null) {
                targets.Add(link);
            }
        }

        foreach (var link in recipe.links.ingredients) {
            if (link != null) {
                sources.Add(link);
            }
        }

        if (recipe.links.fuel != null) {
            sources.Add(recipe.links.fuel);
        }

        if (recipe.links.spentFuel != null) {
            targets.Add(recipe.links.spentFuel);
        }
    }

    private static (List<IProductionLink> merges, List<IProductionLink> splits) GetInfeasibilityCandidates(List<IRecipeRow> recipes) {
        Graph<IProductionLink> graph = new Graph<IProductionLink>();
        List<IProductionLink> sources = [];
        List<IProductionLink> targets = [];
        List<IProductionLink> splits = [];

        foreach (var recipe in recipes) {
            FindAllRecipeLinks(recipe, sources, targets);

            foreach (var src in sources) {
                foreach (var tgt in targets) {
                    graph.Connect(src, tgt);
                }
            }

            if (targets.Count > 1) {
                splits.AddRange(targets);
            }
        }

        var loops = graph.MergeStrongConnectedComponents();
        sources.Clear();

        foreach (var possibleLoop in loops) {
            if (possibleLoop.userData.list != null) {
                var list = possibleLoop.userData.list;
                var last = list[^1];
                sources.Add(last);

                for (int i = 0; i < list.Length - 1; i++) {
                    for (int j = i + 2; j < list.Length; j++) {
                        if (graph.HasConnection(list[i], list[j])) {
                            sources.Add(list[i]);
                            break;
                        }
                    }
                }
            }
        }

        return (sources, splits);
    }

    public bool FindLink(IObjectWithQuality<Goods> goods, [MaybeNullWhen(false)] out ProductionLink link) {
        if (goods == null) {
            link = null;
            return false;
        }

        var searchFrom = this;

        while (true) {
            if (searchFrom.linkMap.TryGetValue(goods, out link)) {
                return true;
            }

            if (searchFrom.owner is RecipeRow row) {
                searchFrom = row.owner;
            }
            else {
                return false;
            }
        }
    }

    public int Compare(ProductionTableFlow x, ProductionTableFlow y) {
        float amt1 = x.goods.target.fluid != null ? x.amount / 50f : x.amount;
        float amt2 = y.goods.target.fluid != null ? y.amount / 50f : y.amount;

        return amt1.CompareTo(amt2);
    }
}

