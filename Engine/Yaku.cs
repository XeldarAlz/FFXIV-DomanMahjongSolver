namespace DomanMahjongAI.Engine;

public enum Yaku : byte
{
    // 1 han
    Riichi,
    Ippatsu,
    MenzenTsumo,
    Pinfu,
    Tanyao,
    Iipeiko,
    YakuhaiHaku,    // white dragon
    YakuhaiHatsu,   // green dragon
    YakuhaiChun,    // red dragon
    YakuhaiRound,   // round wind matches a triplet
    YakuhaiSeat,    // seat wind matches a triplet
    Rinshan,
    Chankan,
    Haitei,
    Houtei,

    // 2 han
    DoubleRiichi,
    Chiitoitsu,
    SanshokuDoujun,
    SanshokuDoukou,
    Ittsu,
    Toitoi,
    Sanankou,
    Sankantsu,
    Honroutou,
    Shousangen,
    Chanta,

    // 3 han
    Ryanpeikou,
    Junchan,
    Honitsu,

    // 6 han
    Chinitsu,

    // Yakuman
    Kokushi,
    Suuankou,
    Daisangen,
    Shousuushii,
    Daisuushii,
    Tsuuiisou,
    Chinroutou,
    Ryuuiisou,
    ChuurenPoutou,
    Suukantsu,
    Tenhou,
    Chihou,
}

public readonly record struct YakuHit(Yaku Yaku, int Han, bool IsYakuman = false)
{
    public override string ToString() =>
        IsYakuman ? $"{Yaku} (yakuman)" : $"{Yaku} ({Han}han)";
}

public static class YakuInfo
{
    public static bool IsYakuman(Yaku y) => y >= Yaku.Kokushi;

    public static string DisplayName(Yaku y) => y switch
    {
        Yaku.Riichi => "Riichi",
        Yaku.Ippatsu => "Ippatsu",
        Yaku.MenzenTsumo => "Menzen Tsumo",
        Yaku.Pinfu => "Pinfu",
        Yaku.Tanyao => "Tanyao",
        Yaku.Iipeiko => "Iipeiko",
        Yaku.YakuhaiHaku => "Yakuhai (Haku)",
        Yaku.YakuhaiHatsu => "Yakuhai (Hatsu)",
        Yaku.YakuhaiChun => "Yakuhai (Chun)",
        Yaku.YakuhaiRound => "Yakuhai (Round)",
        Yaku.YakuhaiSeat => "Yakuhai (Seat)",
        Yaku.Rinshan => "Rinshan",
        Yaku.Chankan => "Chankan",
        Yaku.Haitei => "Haitei",
        Yaku.Houtei => "Houtei",
        Yaku.DoubleRiichi => "Double Riichi",
        Yaku.Chiitoitsu => "Chiitoitsu",
        Yaku.SanshokuDoujun => "Sanshoku Doujun",
        Yaku.SanshokuDoukou => "Sanshoku Doukou",
        Yaku.Ittsu => "Ittsu",
        Yaku.Toitoi => "Toitoi",
        Yaku.Sanankou => "Sanankou",
        Yaku.Sankantsu => "Sankantsu",
        Yaku.Honroutou => "Honroutou",
        Yaku.Shousangen => "Shousangen",
        Yaku.Chanta => "Chanta",
        Yaku.Ryanpeikou => "Ryanpeikou",
        Yaku.Junchan => "Junchan",
        Yaku.Honitsu => "Honitsu",
        Yaku.Chinitsu => "Chinitsu",
        Yaku.Kokushi => "Kokushi Musou",
        Yaku.Suuankou => "Suuankou",
        Yaku.Daisangen => "Daisangen",
        Yaku.Shousuushii => "Shousuushii",
        Yaku.Daisuushii => "Daisuushii",
        Yaku.Tsuuiisou => "Tsuuiisou",
        Yaku.Chinroutou => "Chinroutou",
        Yaku.Ryuuiisou => "Ryuuiisou",
        Yaku.ChuurenPoutou => "Chuuren Poutou",
        Yaku.Suukantsu => "Suukantsu",
        Yaku.Tenhou => "Tenhou",
        Yaku.Chihou => "Chihou",
        _ => y.ToString(),
    };
}
