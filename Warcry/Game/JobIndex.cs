using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Services;
using GameAction = Lumina.Excel.Sheets.Action;

namespace Warcry.Game;

// "Does this action belong to this job" — one definition, shared by the mapping editor's
// filter and the pack builder's warm scoping, so the two can never disagree.
//
// Sheet facts this encodes:
//  - A job's kit is split across the job AND its base class (a Black Mage's early spells
//    are attributed to Thaumaturge), so a job filter must accept the parent too.
//  - Some real player actions carry no ClassJob at all (conditional or transformed ones
//    such as RDM's Enchanted Riposte); for those ClassJobCategory is the only signal.
//  - Lumina generates ClassJobCategory with ~40 bool properties named by ENGLISH job
//    abbreviation and no indexer. ClassJob.Abbreviation is localized — on a French client
//    RDM reads "MRG" — so the lookup must go through the English sheet or silently
//    returns null.
public sealed class JobIndex
{
    private readonly IDataManager data;

    private readonly Dictionary<uint, PropertyInfo?> categoryProperties = [];

    private readonly Dictionary<(uint JobId, uint ActionId), bool> verdicts = [];

    public JobIndex(IDataManager data) => this.data = data;

    // Localized — display only, never for matching.
    public string JobLabel(uint jobId)
    {
        var sheet = this.data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        return sheet.TryGetRow(jobId, out var row) ? row.Abbreviation.ExtractText() : jobId.ToString();
    }

    // Matches Lumina's schema-generated ClassJobCategory property names.
    public string EnglishJobAbbreviation(uint jobId)
    {
        var sheet = this.data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>(Dalamud.Game.ClientLanguage.English);
        return sheet.TryGetRow(jobId, out var row) ? row.Abbreviation.ExtractText() : string.Empty;
    }

    // 0 when the job stands alone.
    public uint ParentJobOf(uint jobId)
    {
        var sheet = this.data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        if (!sheet.TryGetRow(jobId, out var row))
        {
            return 0;
        }

        var parent = row.ClassJobParent.RowId;
        return parent == jobId ? 0 : parent;
    }

    public bool ActionBelongsToJob(uint actionId, uint jobId)
    {
        if (jobId == 0)
        {
            return true;
        }

        if (this.verdicts.TryGetValue((jobId, actionId), out var cached))
        {
            return cached;
        }

        var sheet = this.data.GetExcelSheet<GameAction>();
        var verdict = sheet.TryGetRow(actionId, out var row) && this.ActionBelongsToJob(in row, jobId);
        this.verdicts[(jobId, actionId)] = verdict;
        return verdict;
    }

    public bool ActionBelongsToJob(in GameAction row, uint jobId)
    {
        if (jobId == 0)
        {
            return true;
        }

        var actionJob = row.ClassJob.RowId;

        if (actionJob == jobId)
        {
            return true;
        }

        var parent = this.ParentJobOf(jobId);
        if (parent != 0 && actionJob == parent)
        {
            return true;
        }

        // Consulted whenever the direct match fails, not only when ClassJob is unset:
        // enchanted/conditional variants can carry a ClassJob that is neither the job nor
        // its parent. The broader categories this admits — role actions, Sprint, general
        // actions — are all things the job can genuinely press.
        return this.CategoryIncludesJob(in row, jobId);
    }

    public bool CategoryIncludesJob(in GameAction row, uint jobId)
    {
        if (!this.categoryProperties.TryGetValue(jobId, out var property))
        {
            var abbreviation = this.EnglishJobAbbreviation(jobId);
            property = string.IsNullOrWhiteSpace(abbreviation)
                ? null
                : typeof(Lumina.Excel.Sheets.ClassJobCategory)
                    .GetProperty(abbreviation, BindingFlags.Public | BindingFlags.Instance);
            this.categoryProperties[jobId] = property;
        }

        if (property is null)
        {
            return false;
        }

        var category = row.ClassJobCategory.ValueNullable;
        return category.HasValue && property.GetValue(category.Value) is true;
    }
}
