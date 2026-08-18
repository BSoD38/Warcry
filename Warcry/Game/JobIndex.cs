using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Services;
using GameAction = Lumina.Excel.Sheets.Action;

namespace Warcry.Game;

/// <summary>
/// Answers "does this action belong to this job" — the one question both the mapping
/// editor's job filter and the pack builder's warm scoping hang off.
/// </summary>
/// <remarks>
/// <para>Moved out of the UI layer because the answer is not a UI concern: the pack
/// builder needs it every time the job changes, to decide which compiled clips to warm.
/// One instance, one set of caches, one definition of "belongs to" — the editor's list
/// and the warm set can never disagree.</para>
/// <para>The sheet facts encoded here, each learned the hard way:</para>
/// <list type="bullet">
/// <item>A job's kit is split across the job AND its base class — a Black Mage's early
/// spells are attributed to Thaumaturge — so a job filter must accept the parent too.</item>
/// <item>Some real player actions carry no <c>ClassJob</c> at all (conditional or
/// transformed ones such as RDM's Enchanted Riposte); for those,
/// <c>ClassJobCategory</c> is the only signal.</item>
/// <item>Lumina generates <c>ClassJobCategory</c> with ~40 bool properties named by
/// <b>English</b> job abbreviation and no indexer. <c>ClassJob.Abbreviation</c> is
/// LOCALIZED — on a French client RDM reads "MRG" — so the property lookup must go
/// through the English sheet or it silently returns null.</item>
/// </list>
/// </remarks>
public sealed class JobIndex
{
    private readonly IDataManager data;

    /// <summary>ClassJobCategory property per job, resolved by reflection once each.</summary>
    private readonly Dictionary<uint, PropertyInfo?> categoryProperties = [];

    /// <summary>Memoised verdicts. Both the editor and the warm pass hit the same rows repeatedly.</summary>
    private readonly Dictionary<(uint JobId, uint ActionId), bool> verdicts = [];

    public JobIndex(IDataManager data) => this.data = data;

    /// <summary>Localized abbreviation — for display only, never for matching.</summary>
    public string JobLabel(uint jobId)
    {
        var sheet = this.data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        return sheet.TryGetRow(jobId, out var row) ? row.Abbreviation.ExtractText() : jobId.ToString();
    }

    /// <summary>
    /// Always English — matches Lumina's schema-generated ClassJobCategory property names.
    /// </summary>
    public string EnglishJobAbbreviation(uint jobId)
    {
        var sheet = this.data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>(Dalamud.Game.ClientLanguage.English);
        return sheet.TryGetRow(jobId, out var row) ? row.Abbreviation.ExtractText() : string.Empty;
    }

    /// <summary>The base class a job grew out of, or 0 when it stands alone.</summary>
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

    /// <summary>Memoised by (job, action). The uncached overload does the real work.</summary>
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

        // A job's kit is split across the job and its base class — BLM's early spells
        // are attributed to THM — so the parent must be accepted too.
        var parent = this.ParentJobOf(jobId);
        if (parent != 0 && actionJob == parent)
        {
            return true;
        }

        // Consulted whenever the direct match fails, not only when ClassJob is unset:
        // enchanted/conditional variants can carry a ClassJob that is neither the job
        // nor its parent. The broader categories this admits — role actions, Sprint,
        // general actions — are all things the job can genuinely press.
        return this.CategoryIncludesJob(in row, jobId);
    }

    public bool CategoryIncludesJob(in GameAction row, uint jobId)
    {
        if (!this.categoryProperties.TryGetValue(jobId, out var property))
        {
            // MUST be the English abbreviation — see the class remarks.
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
