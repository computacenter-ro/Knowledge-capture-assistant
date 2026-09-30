namespace KnowledgeCapture.Core.Services;

/// <summary>
/// Synthetic, PII-rich demo answers (fake people; checksum-valid test IDs). Used by the example buttons and by the
/// self-test / leakcheck. None of these values may ever appear in storage, logs or exports.
/// </summary>
public static class DemoData
{
    public const string ValidCnp = "1800101221144";          // checksum-valid test CNP
    public const string InvalidCnp = "1800101221145";        // same, wrong control digit
    public const string ValidIban = "RO49AAAA1B31007593840000";
    public const string InvalidIban = "RO48AAAA1B31007593840000";
    public const string ValidCui = "RO14399840";

    public static readonly (string Label, string Text)[] Examples =
    [
        ("Daily work (EN)",
            "I'm Andrei Ionescu, team lead in Accounts Payable for the Contoso account in Cluj-Napoca. My goal is simple: " +
            "suppliers get paid on time and we never pay the same invoice twice. Every morning I open the invoice queue in SAP, " +
            "match each invoice to its purchase order, and flag mismatches in an Excel tracker. If you need me, it's " +
            "andrei.ionescu@contoso.ro or +40 721 234 567."),
        ("Proces (RO)",
            "Sunt Maria Popescu și mă ocup de onboarding-ul furnizorilor noi în Proiectul Orion. Pașii: verific contractul, " +
            $"cer CUI-ul (de exemplu {ValidCui}), deschid furnizorul în SAP și trimit IBAN-ul {ValidIban} spre validare la " +
            "Radu Georgescu de la Banca Transilvania. Dacă valoarea contractului depășește 50.000 lei, cer aprobarea " +
            $"directorului financiar. Pentru testare folosim CNP-ul {ValidCnp}."),
        ("Recurring problem (EN)",
            "A recurring problem: the VPN drops for the Fabrikam team in our Bucharest office. Last Tuesday Elena Dumitrescu " +
            "couldn't reach the file server at 10.20.30.40, so I checked the certificate in the portal " +
            "https://it.fabrikam.internal/vpn, found it had expired, renewed it and she was back online in 10 minutes. " +
            "Pitfall: people reinstall the client first, which takes an hour and never helps. Tip for a new colleague: always " +
            "check the certificate expiry date first, and if it is urgent call John Smith on 0744 123 456. " +
            "The test card on the account is 4111 1111 1111 1111."),
    ];

    public const string ScriptTopic = "A process I own";

    /// <summary>Scripted 5-turn interview: the 3 demo answers plus a repeat of a person (turn 3) and a memory probe (turn 5).</summary>
    public static readonly string[] ScriptedAnswers =
    [
        Examples[0].Text,
        Examples[1].Text,
        "When the same invoice arrives twice, SAP does not always block it, so I compare the invoice number, amount and " +
        "date by hand before the payment run. Example: last March the supplier Northwind sent invoice 7781 twice for " +
        "12,400 EUR, and Andrei Ionescu stopped the second payment just before the Friday payment run.",
        Examples[2].Text,
        "One more tip: never approve an invoice you entered yourself, always ask a second person to approve it. " +
        "And a quick check for you: which system did I say at the very start that I use for the invoice queue?",
    ];

    /// <summary>The turn-1 fact the model must still know in turn 5 (memory self-test).</summary>
    public const string MemoryFact = "SAP";

    /// <summary>Every original PII value in the demo data, with common variants, for the leak check.</summary>
    public static readonly string[] PiiValues =
    [
        "Andrei Ionescu", "Andrei", "Ionescu", "andrei.ionescu@contoso.ro", "andrei.ionescu", "+40 721 234 567",
        "721 234 567", "0721234567", "721234567", "Contoso", "Cluj-Napoca", "Cluj", "Napoca",
        "Maria Popescu", "Maria", "Popescu", "Orion", ValidCui, "14399840", ValidIban, "RO49 AAAA", "1B31007593840000",
        "Radu Georgescu", "Radu", "Georgescu", "Banca Transilvania", "Transilvania", ValidCnp,
        "Fabrikam", "Bucharest", "Elena Dumitrescu", "Elena", "Dumitrescu", "10.20.30.40", "it.fabrikam.internal",
        "John Smith", "Smith", "0744 123 456", "0744123456", "744 123 456", "4111 1111 1111 1111", "4111111111111111",
        "Northwind",
    ];
}
