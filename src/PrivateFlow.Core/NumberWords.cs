namespace PrivateFlow.Core;

// Numbers spelled as a speaker of English, Spanish or German says them, so the evaluation can search a transcript the way a user
// would type a number they heard. TranscriptScoring.Words reads every form back as digits.
public static class NumberWords
{
    private static readonly string[] EnglishUnits = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] EnglishTens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
    private static readonly string[] SpanishUnits = ["cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve", "diez", "once", "doce", "trece", "catorce", "quince", "dieciséis", "diecisiete", "dieciocho", "diecinueve",
        "veinte", "veintiuno", "veintidós", "veintitrés", "veinticuatro", "veinticinco", "veintiséis", "veintisiete", "veintiocho", "veintinueve"];
    private static readonly string[] SpanishTens = ["", "", "veinte", "treinta", "cuarenta", "cincuenta", "sesenta", "setenta", "ochenta", "noventa"];
    private static readonly string[] SpanishHundreds = ["", "ciento", "doscientos", "trescientos", "cuatrocientos", "quinientos", "seiscientos", "setecientos", "ochocientos", "novecientos"];
    private static readonly string[] GermanUnits = ["null", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun", "zehn", "elf", "zwölf", "dreizehn", "vierzehn", "fünfzehn", "sechzehn", "siebzehn", "achtzehn", "neunzehn"];
    private static readonly string[] GermanTens = ["", "", "zwanzig", "dreißig", "vierzig", "fünfzig", "sechzig", "siebzig", "achtzig", "neunzig"];

    // 0 to 999,999 in "en", "es" or "de".
    public static string Say(int value, string language)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 999_999);
        return language switch
        {
            "es" => Spanish(value),
            "de" => German(value),
            _ => English(value)
        };
    }
    // A clock time as it is said: "twelve thirty-four", "doce y treinta y cuatro", "zwölf Uhr vierunddreißig"; a full hour is the hour alone.
    public static string SayTime(int hour, int minute, string language) => minute == 0 ? Say(hour, language) : language switch
    {
        "es" => $"{Spanish(hour)} y {Spanish(minute)}",
        "de" => $"{German(hour)} Uhr {German(minute)}",
        _ => minute < 10 ? $"{English(hour)} oh {English(minute)}" : $"{English(hour)} {English(minute)}"
    };

    private static string English(int value)
    {
        if (value < 20) return EnglishUnits[value];
        if (value < 100) return EnglishTens[value / 10] + (value % 10 == 0 ? "" : "-" + EnglishUnits[value % 10]);
        if (value < 1000) return EnglishUnits[value / 100] + " hundred" + (value % 100 == 0 ? "" : " " + English(value % 100));
        return English(value / 1000) + " thousand" + (value % 1000 == 0 ? "" : " " + English(value % 1000));
    }
    private static string Spanish(int value)
    {
        if (value < 30) return SpanishUnits[value];
        if (value < 100) return SpanishTens[value / 10] + (value % 10 == 0 ? "" : " y " + SpanishUnits[value % 10]);
        if (value == 100) return "cien";
        if (value < 1000) return SpanishHundreds[value / 100] + (value % 100 == 0 ? "" : " " + Spanish(value % 100));
        return (value / 1000 == 1 ? "" : Spanish(value / 1000) + " ") + "mil" + (value % 1000 == 0 ? "" : " " + Spanish(value % 1000));
    }
    private static string German(int value)
    {
        if (value < 20) return GermanUnits[value];
        if (value < 100) return (value % 10 == 0 ? "" : (value % 10 == 1 ? "ein" : GermanUnits[value % 10]) + "und") + GermanTens[value / 10];
        if (value < 1000) return (value / 100 == 1 ? "ein" : GermanUnits[value / 100]) + "hundert" + (value % 100 == 0 ? "" : German(value % 100));
        return (value / 1000 == 1 ? "ein" : German(value / 1000)) + "tausend" + (value % 1000 == 0 ? "" : German(value % 1000));
    }
}
