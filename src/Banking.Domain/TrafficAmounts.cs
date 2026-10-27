namespace Banking.Domain;

public static class TrafficAmounts
{
    /// <summary>Uniform whole-cent sample, bounded by available funds. Random is injectable for tests.</summary>
    public static decimal Sample(decimal minimum, decimal maximum, decimal budget, Random? random = null)
    {
        var low = (long)decimal.Ceiling(minimum * 100m);
        var high = (long)decimal.Floor(Math.Min(maximum, budget) * 100m);
        if (low < 1 || high < low) return 0m;
        return (random ?? Random.Shared).NextInt64(low, high + 1) / 100m;
    }
}
