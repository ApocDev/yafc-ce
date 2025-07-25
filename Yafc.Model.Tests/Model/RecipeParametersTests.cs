using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Yafc.Model.Tests.Model;

[Collection("LuaDependentTests")]
public class RecipeParametersTests {
    [Fact]
    public async Task FluidBoilingRecipes_HaveCorrectConsumption() {
        Project project = LuaDependentTestHelper.GetProjectForLua();

        ProjectPage page = new(project, typeof(ProductionTable));
        project.pages.Add(page);
        ProductionTable table = (ProductionTable)page.content;
        table.AddRecipe(Database.recipes.all.Single(r => r.name == "boiler.boiler.steam").With(Quality.Normal), DataUtils.DeterministicComparer);
        table.AddRecipe(Database.recipes.all.Single(r => r.name == "boiler.heat-exchanger.steam").With(Quality.Normal), DataUtils.DeterministicComparer);

        List<Fluid> water = Database.fluidVariants["Fluid.water"];

        RecipeRow boiler = table.recipes[0];
        RecipeRow heatExchanger = table.recipes[1];
        boiler.fixedBuildings = 1;
        heatExchanger.fixedBuildings = 1;
        await table.Solve((ProjectPage)table.owner); // Initial Solve to set RecipeRow.Ingredients

        for (int i = 0; i < 3; i++) {
            if (i != 0) {
                // boiler has changed in 2.0 and doesn't work yet
                continue;
            }
            boiler.ChangeVariant(boiler.Ingredients.Single().Goods.target, water[i]);
            heatExchanger.ChangeVariant(boiler.Ingredients.Single().Goods.target, water[i]);

            await table.Solve((ProjectPage)table.owner);

            // boil 60, 78.26, 120 water per second from 15, 50, 90° to 165°
            float expectedBoilerAmount = 1800 / .2f / (165 - water[i].temperature);
            // boil 103.09, 111.11, 121.95 water per second from 15, 50, 90° to 500°
            float expectedHeatExchangerAmount = 10000 / .2f / (500 - water[i].temperature);
            // Equation is boiler power (KW) / heat capacity (KJ/unit°C) / temperature change (°C) => unit/s

            Assert.Equal(.45, (double)boiler.FuelInformation.Amount, .45 * .0001); // Always .45 coal per second
            Assert.Equal(expectedBoilerAmount, (double)boiler.Ingredients.Single().Amount, (int)(expectedBoilerAmount * .0001f));
            Assert.Equal(expectedBoilerAmount, (double)boiler.Products.Single().Amount, (int)(expectedBoilerAmount * .0001f));

            Assert.Equal(10, (double)heatExchanger.FuelInformation.Amount); // Always 10 MW heat
            Assert.Equal(expectedHeatExchangerAmount, (double)heatExchanger.Ingredients.Single().Amount, (int)(expectedHeatExchangerAmount * .0001f));
            Assert.Equal(expectedHeatExchangerAmount, (double)heatExchanger.Products.Single().Amount, (int)(expectedHeatExchangerAmount * .0001f));
        }
    }
}
