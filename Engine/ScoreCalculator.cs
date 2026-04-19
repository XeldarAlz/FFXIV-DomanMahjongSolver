namespace DomanMahjongAI.Engine;

public static class ScoreCalculator
{
    /// <summary>
    /// Given han (including dora) and fu, return (basePoints, tierName).
    /// For yakuman hands pass han = 13 × (number of yakuman), isYakuman=true.
    /// </summary>
    public static (int Base, string Tier) BasePoints(int han, int fu, bool isYakuman)
    {
        if (isYakuman)
        {
            int multiplier = Math.Max(1, han / 13);
            return (8000 * multiplier, "yakuman");
        }

        if (han >= 13) return (8000, "yakuman");  // counted yakuman (13+ han from normal yaku)
        if (han >= 11) return (6000, "sanbaiman");
        if (han >= 8)  return (4000, "baiman");
        if (han >= 6)  return (3000, "haneman");
        if (han >= 5)  return (2000, "mangan");

        long basePoints = (long)fu * (1L << (han + 2));
        if (basePoints >= 2000) return (2000, "mangan");
        return ((int)basePoints, "");
    }

    public static Payments Pay(int basePoints, bool isDealer, WinKind kind)
    {
        if (isDealer)
        {
            if (kind == WinKind.Ron)
            {
                int total = RoundUp(basePoints * 6, 100);
                return new Payments(0, 0, total, total);
            }
            // Dealer tsumo: three non-dealers each pay 2×base, rounded up to 100.
            int per = RoundUp(basePoints * 2, 100);
            return new Payments(0, per, 0, per * 3);
        }
        else
        {
            if (kind == WinKind.Ron)
            {
                int total = RoundUp(basePoints * 4, 100);
                return new Payments(0, 0, total, total);
            }
            // Non-dealer tsumo: dealer pays 2×base, each of the other two non-dealers pays 1×base.
            int dealerPay = RoundUp(basePoints * 2, 100);
            int otherPay = RoundUp(basePoints * 1, 100);
            return new Payments(dealerPay, otherPay, 0, dealerPay + otherPay * 2);
        }
    }

    private static int RoundUp(int value, int step)
    {
        int rem = value % step;
        return rem == 0 ? value : value + (step - rem);
    }
}
