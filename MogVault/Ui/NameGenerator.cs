namespace MogVault.Ui;

internal static class NameGenerator
{
    internal enum Style
    {
        Single,

        Full,

        Company,
    }

    private static readonly string[] Starts =
    [
        "Al", "Ba", "Ce", "Da", "El", "Fa", "Ga", "Ha", "Is", "Ja", "Ka", "Le", "Ma", "Ne", "Or", "Pa", "Ra", "Sa", "Ta", "Ul",
        "Va", "Wy", "Ya", "Ze", "Bri", "Cor", "Dra", "Fen", "Gri", "Kel", "Lor", "Mor", "Nyr", "Syl", "Thal", "Vel", "Ae", "Ish",
    ];

    private static readonly string[] Middles = ["", "la", "ri", "na", "to", "mi", "ve", "ra", "lo", "de", "sa", "ne", "ki", "ro", "li"];

    private static readonly string[] Ends =
    [
        "n", "ra", "th", "mir", "wyn", "dor", "ria", "lis", "ion", "eth", "ara", "os", "ius", "ane", "enne", "ric", "ard", "ette", "ille", "us",
    ];

    private static readonly string[] SurnameStarts =
    [
        "Ash", "Black", "Bright", "Cold", "Dawn", "Dusk", "Ember", "Frost", "Gold", "Iron", "Moon", "Night", "Oak", "Rain", "Raven", "Silver",
        "Star", "Stone", "Storm", "Sun", "Thorn", "Wind", "Wolf", "Wild",
    ];

    private static readonly string[] SurnameEnds =
    [
        "blade", "brook", "crest", "dale", "fall", "field", "forge", "heart", "hill", "mane", "ridge", "shade", "song", "stride", "vale",
        "wood", "born", "weaver", "walker", "bloom",
    ];

    private static readonly string[] Adjectives =
    [
        "Silver", "Crimson", "Azure", "Golden", "Hidden", "Wandering", "Quiet", "Iron", "Lucky", "Midnight", "Northern", "Radiant", "Scarlet",
        "Verdant", "Wild", "Amber", "Brave", "Clever", "Distant", "Eternal", "Frozen", "Gentle", "Hollow", "Ivory", "Jade", "Lone", "Merry",
        "Obsidian", "Swift", "Velvet",
    ];

    private static readonly string[] Nouns =
    [
        "Lanterns", "Wyverns", "Moogles", "Chocobos", "Foxes", "Ravens", "Wanderers", "Keepers", "Tides", "Embers", "Blades", "Crowns",
        "Petals", "Sparrows", "Hearths", "Anchors", "Owls", "Stars", "Thorns", "Hounds", "Pilgrims", "Wardens", "Bells", "Comets", "Lilies",
        "Otters", "Sails", "Spires", "Whispers", "Lynxes",
    ];

    // Same seed and id always give the same name. On a clash the id is stepped by SplitMix64's golden ratio increment (2^64 / phi) and hashed again.
    public static string Next(ulong seed, ulong id, Style style, HashSet<string> used)
    {
        for (ulong attempt = 0; attempt < 64; attempt++)
        {
            var name = Make(Mix(seed ^ Mix(id + attempt * 0x9E3779B97F4A7C15)), style);
            if (used.Add(name))
                return name;
        }

        var fallback = $"{Make(Mix(seed ^ Mix(id)), style)} {used.Count}";
        used.Add(fallback);
        return fallback;
    }

    private static string Make(ulong r, Style style) => style switch
    {
        Style.Full => $"{FirstName(ref r)} {Pick(SurnameStarts, ref r)}{Pick(SurnameEnds, ref r)}",
        Style.Company => $"{Pick(Adjectives, ref r)} {Pick(Nouns, ref r)}",
        _ => FirstName(ref r),
    };

    private static string FirstName(ref ulong r) => Pick(Starts, ref r) + Pick(Middles, ref r) + Pick(Ends, ref r);

    private static string Pick(string[] options, ref ulong r)
    {
        var value = options[r % (ulong)options.Length];
        r /= (ulong)options.Length;
        return value;
    }

    // SplitMix64 (Steele, Lea, Flood 2014), ported from Sebastiano Vigna's public domain C version: https://prng.di.unimi.it/splitmix64.c
    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EB;
        return x ^ (x >> 31);
    }
}

