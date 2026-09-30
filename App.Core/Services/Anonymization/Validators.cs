namespace KnowledgeCapture.Core.Services.Anonymization;

/// <summary>Checksum validators. Invalid checksums are NOT treated as the entity.</summary>
public static class Validators
{
    private const string CnpKey = "279146358279";
    private const string CuiKey = "753217532";

    /// <summary>Romanian CNP: 13 digits, S YY MM DD JJ NNN C, control digit over the key 279146358279.</summary>
    public static bool IsValidCnp(string value)
    {
        var d = TextPatterns.DigitsOnly(value);
        if (d.Length != 13 || d[0] == '0') return false;
        var month = int.Parse(d.AsSpan(3, 2));
        var day = int.Parse(d.AsSpan(5, 2));
        if (month is < 1 or > 12 || day is < 1 or > 31) return false;
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (d[i] - '0') * (CnpKey[i] - '0');
        var r = sum % 11;
        var control = r == 10 ? 1 : r;
        return control == d[12] - '0';
    }

    /// <summary>Romanian IBAN: RO + 2 check digits + 4-letter bank code + 16 alphanumerics, ISO 13616 mod-97 == 1.</summary>
    public static bool IsValidRoIban(string value)
    {
        var s = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (s.Length != 24 || !s.StartsWith("RO")) return false;
        if (!char.IsDigit(s[2]) || !char.IsDigit(s[3])) return false;
        for (var i = 4; i < 8; i++) if (s[i] is < 'A' or > 'Z') return false;
        for (var i = 8; i < 24; i++) if (!char.IsAsciiLetterOrDigit(s[i])) return false;
        return Mod97(s[4..] + s[..4]) == 1;
    }

    public static int Mod97(string rearranged)
    {
        var rem = 0;
        foreach (var c in rearranged)
        {
            var v = char.IsDigit(c) ? c - '0' : c - 'A' + 10;
            rem = v >= 10 ? (rem * 100 + v) % 97 : (rem * 10 + v) % 97;
        }
        return rem;
    }

    /// <summary>Luhn check for payment card numbers (13-19 digits).</summary>
    public static bool IsValidLuhn(string value)
    {
        var d = TextPatterns.DigitsOnly(value);
        if (d.Length is < 13 or > 19) return false;
        if (d.Distinct().Count() == 1) return false; // 0000000000000 etc.
        var sum = 0; var dbl = false;
        for (var i = d.Length - 1; i >= 0; i--)
        {
            var n = d[i] - '0';
            if (dbl) { n *= 2; if (n > 9) n -= 9; }
            sum += n; dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    /// <summary>Romanian CUI/CIF: 2-10 digits, last digit = (sum(body*753217532) * 10) mod 11 (10 -> 0).</summary>
    public static bool IsValidCui(string value)
    {
        var d = TextPatterns.DigitsOnly(value);
        if (d.Length is < 2 or > 10) return false;
        var body = d[..^1].PadLeft(9, '0');
        var sum = 0;
        for (var i = 0; i < 9; i++) sum += (body[i] - '0') * (CuiKey[i] - '0');
        var c = sum * 10 % 11;
        if (c == 10) c = 0;
        return c == d[^1] - '0';
    }

    /// <summary>Normalizes Romanian phone digits to the 10-digit national form (07xx..., 02x..., 03x...), or null.</summary>
    public static string? NormalizeRoPhone(string value)
    {
        var d = TextPatterns.DigitsOnly(value);
        var plus = value.TrimStart().StartsWith('+');
        if (d.StartsWith("0040")) d = "0" + d[4..];
        else if (d.StartsWith("40") && (plus || d.Length == 11)) d = "0" + d[2..];
        if (d.Length == 11 && d.StartsWith("00")) d = d[1..];      // +40 (0)7xx written with the trunk zero
        if (d.Length != 10 || d[0] != '0') return null;
        return d[1] is '7' or '2' or '3' ? d : null;
    }

    // Exposed for tests / demo data generation.
    public static int CnpControlDigit(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (CnpKey[i] - '0');
        var r = sum % 11;
        return r == 10 ? 1 : r;
    }
}
