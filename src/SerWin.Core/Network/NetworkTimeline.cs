namespace SerWin.Core.Network;

/// <summary>Une tranche de la frise : début, fin et nombre d'échanges par catégorie.</summary>
public sealed record TimelineBucket<TCategory>(DateTime Start, DateTime End, IReadOnlyDictionary<TCategory, int> Counts)
    where TCategory : notnull
{
    public int Total => Counts.Values.Sum();
}

/// <summary>Regroupe les échanges datés en tranches régulières (frise 24 h ou 7 jours).</summary>
public static class NetworkTimeline
{
    /// <summary>
    /// Découpe la période qui se termine à la fin de la tranche contenant <paramref name="now"/> en
    /// <paramref name="bucketCount"/> tranches de <paramref name="bucket"/>. Les tranches sont alignées sur l'heure ronde
    /// (et sur minuit pour des tranches multiples d'une heure qui divisent 24 h), pour des étiquettes lisibles.
    /// </summary>
    public static List<TimelineBucket<TCategory>> Build<TCategory>(
        IEnumerable<NetworkEvent> events, Func<NetworkEvent, TCategory> classify, DateTime now, TimeSpan bucket, int bucketCount)
        where TCategory : notnull
    {
        if (bucket <= TimeSpan.Zero || bucketCount <= 0) return [];
        var hours = Math.Max(1, (int)bucket.TotalHours);
        var currentStart = now.Date.AddHours(now.Hour / hours * hours);
        var end = currentStart + bucket;
        var start = end - bucket * bucketCount;

        var counts = Enumerable.Range(0, bucketCount).Select(_ => new Dictionary<TCategory, int>()).ToList();
        foreach (var e in events)
        {
            if (e.Time < start || e.Time >= end) continue;
            var i = (int)((e.Time - start).Ticks / bucket.Ticks);
            var c = classify(e);
            counts[i][c] = counts[i].GetValueOrDefault(c) + 1;
        }
        return counts.Select((c, i) => new TimelineBucket<TCategory>(start + bucket * i, start + bucket * (i + 1), c)).ToList();
    }
}
