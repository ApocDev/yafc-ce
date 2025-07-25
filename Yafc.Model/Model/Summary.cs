using System.Collections.Generic;
using System.Threading.Tasks;

namespace Yafc.Model;

public class Summary(ModelObject page) : ProjectPageContents(page) {

    public bool showOnlyIssues { get; set; }
    public bool showMissingItems { get; set; } = true;

    [SkipSerialization] public List<(string goodsName, float deficit)> missingItems { get; } = [];

    public override Task<string?> Solve(ProjectPage page) => Task.FromResult<string?>(null);

    public void UpdateMissingItems(Dictionary<string, (float totalProvided, float totalNeeded, float extraProduced, float sum)> allGoods) {
        missingItems.Clear();

        foreach (var goodInfo in allGoods) {
            float amountAvailable = (goodInfo.Value.totalProvided > 0 ? goodInfo.Value.totalProvided : 0) + goodInfo.Value.extraProduced;
            float amountNeeded = (goodInfo.Value.totalProvided < 0 ? -goodInfo.Value.totalProvided : 0) + goodInfo.Value.totalNeeded;

            if (amountNeeded > amountAvailable && amountNeeded > 0) {
                missingItems.Add((goodInfo.Key, amountNeeded - amountAvailable));
            }
        }

        // Sort by deficit amount (highest first)
        missingItems.Sort((a, b) => b.deficit.CompareTo(a.deficit));
    }
}
