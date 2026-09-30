using System.Text.Json;
using KnowledgeCapture.Core.Models;
using KnowledgeCapture.Core.Services;
using KnowledgeCapture.Core.Services.Anonymization;

namespace KnowledgeCapture.SelfTest;

/// <summary>Scripted recognizer: returns the known entities that literally occur in the text (simulates NER hits/misses).</summary>
internal sealed class DictionaryNer(params (string Text, string Type)[] known) : IEntityRecognizer
{
    public string Name => "dict";
    public Task<NerResult> RecognizeAsync(string text, CancellationToken ct) =>
        Task.FromResult(NerResult.Ok(known.Where(k => text.Contains(k.Text)).Select(k => new NerEntity(k.Text, k.Type)).ToList()));
}

internal sealed class FailingNer : IEntityRecognizer
{
    public string Name => "fail";
    public Task<NerResult> RecognizeAsync(string text, CancellationToken ct) => Task.FromResult(NerResult.Fail("simulated failure"));
}

internal static class OfflineTests
{
    private const string P = EntityTypes.Person, O = EntityTypes.Org, L = EntityTypes.Location;

    public static void Run(Tests t, AppSettings settings)
    {
        var a = settings.Anonymization;

        t.Section("Validators");
        t.Check("valid CNP passes checksum", Validators.IsValidCnp(DemoData.ValidCnp));
        t.Check("invalid CNP (wrong control digit) is rejected", !Validators.IsValidCnp(DemoData.InvalidCnp));
        t.Check("CNP with impossible month is rejected", !Validators.IsValidCnp("1801301221144"));
        t.Check("valid RO IBAN passes mod-97", Validators.IsValidRoIban(DemoData.ValidIban));
        t.Check("valid RO IBAN with spaces passes", Validators.IsValidRoIban("RO49 AAAA 1B31 0075 9384 0000"));
        t.Check("invalid RO IBAN (bad check digits) is rejected", !Validators.IsValidRoIban(DemoData.InvalidIban));
        t.Check("valid CUI passes", Validators.IsValidCui("14399840"));
        t.Check("invalid CUI is rejected", !Validators.IsValidCui("14399841"));
        t.Check("Luhn-valid card passes", Validators.IsValidLuhn("4111 1111 1111 1111"));
        t.Check("Luhn-invalid card is rejected", !Validators.IsValidLuhn("4111 1111 1111 1112"));

        t.Section("Deterministic recognizers");
        var det = new Anonymizer(a, ner: null);
        void Expect(string name, string input, string[] mustContain, string[] mustNotContain, Anonymizer? an = null, PlaceholderMap? map = null)
        {
            var r = (an ?? det).AnonymizeAsync(input, map ?? new PlaceholderMap()).GetAwaiter().GetResult();
            var missing = mustContain.Where(s => !r.Text.Contains(s)).ToList();
            var leaked = mustNotContain.Where(s => r.Text.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
            t.Check(name, r.Success && missing.Count == 0 && leaked.Count == 0,
                !r.Success ? $"failed: {r.Error}" : missing.Count + leaked.Count == 0 ? r.Text
                : $"got \"{r.Text}\" (missing {string.Join(",", missing)}; leaked {string.Join(",", leaked)})");
        }

        Expect("valid CNP -> <CNP_1>", $"Clientul are CNP {DemoData.ValidCnp}.", ["<CNP_1>"], [DemoData.ValidCnp]);
        Expect("invalid CNP is NOT anonymized", $"Codul {DemoData.InvalidCnp} nu e valid.", [DemoData.InvalidCnp], ["<CNP_"]);
        Expect("valid IBAN (compact) -> <IBAN_1>", $"IBAN: {DemoData.ValidIban}", ["<IBAN_1>"], [DemoData.ValidIban, "AAAA"]);
        Expect("valid IBAN (spaced) -> <IBAN_1>", "IBAN RO49 AAAA 1B31 0075 9384 0000 pentru plată", ["<IBAN_1>"], ["AAAA", "9384"]);
        Expect("invalid IBAN is NOT anonymized", $"IBAN greșit {DemoData.InvalidIban}.", [DemoData.InvalidIban], ["<IBAN_"]);
        Expect("CUI with keyword -> <CUI_1>", "Firma are CUI: 14399840.", ["<CUI_1>"], ["14399840"]);
        Expect("CUI with RO prefix (valid checksum) -> <CUI_1>", $"cer CUI-ul (de exemplu {DemoData.ValidCui})", ["<CUI_1>"], ["14399840"]);
        Expect("RO-prefixed number with invalid CUI checksum is NOT anonymized", "referința RO14399841", ["RO14399841"], ["<CUI_"]);
        Expect("Luhn-valid card -> <CARD_1>", "Card 4111 1111 1111 1111.", ["<CARD_1>"], ["4111"]);
        Expect("Luhn-invalid card is NOT anonymized", "Nr. 4111 1111 1111 1112.", ["4111 1111 1111 1112"], ["<CARD_"]);
        foreach (var phone in new[] { "+40 721 234 567", "0721-234-567", "0721.234.567", "0040 721 234 567", "+40721234567", "021 123 4567", "+44 20 7946 0958" })
            Expect($"phone {phone} -> <PHONE_1>", $"Call {phone} today.", ["<PHONE_1>"], [phone, "234", "7946"]);
        Expect("two adjacent phones are both caught", "Sună la 0721 234 567 / 0744 123 456.", ["<PHONE_1>", "<PHONE_2>"], ["0721", "0744", "567", "456"]);
        Expect("same phone in two formats -> same placeholder", "+40 721 234 567 or 0721234567", ["<PHONE_1> or <PHONE_1>"], ["<PHONE_2>"]);
        Expect("email -> <EMAIL_1>", "Write to andrei.ionescu@contoso.ro.", ["<EMAIL_1>"], ["andrei", "contoso.ro"]);
        Expect("URL -> <URL_1>", "Portal https://it.fabrikam.internal/vpn, then retry.", ["<URL_1>"], ["fabrikam", "https"]);
        Expect("bare domain -> <URL_1>", "Open portal.contoso.ro first.", ["<URL_1>"], ["contoso"]);
        Expect("IPv4 -> <IP_1>", "Server 10.20.30.40 is down.", ["<IP_1>"], ["10.20.30.40"]);
        Expect("IPv6 -> <IP_1>", "Try fe80::1ff:fe23:4567:890a now.", ["<IP_1>"], ["fe80"]);
        Expect("deny-list project (config) -> <PROJECT_1>", "Lucrez în Proiectul Orion și în project ORION.", ["<PROJECT_1>"], ["Orion"]);
        Expect("deny-list clients -> <CLIENT_n>", "Contoso și Fabrikam au trimis facturi.", ["<CLIENT_1>", "<CLIENT_2>"], ["Contoso", "Fabrikam"]);
        var diacritic = new Anonymizer(new AnonymizationSettings { Projects = ["Proiectul Ștefănești"] }, null);
        Expect("deny-list is diacritic-insensitive", "Am terminat proiectul Stefanesti ieri.", ["<PROJECT_1>"], ["Stefanesti"], diacritic);
        Expect("titled Romanian name (booster) -> <PERSON_1>", "Domnul Radu Georgescu a aprobat plata.", ["<PERSON_1>"], ["Radu", "Georgescu"]);
        Expect("self-introduced English name (booster) -> <PERSON_1>", "Hi, my name is John Smith and I run payroll.", ["<PERSON_1>"], ["John", "Smith"]);
        Expect("work facts are preserved", "We use SAP and Excel every Monday; the threshold is 50.000 lei and it takes 10 minutes.",
            ["SAP", "Excel", "Monday", "50.000 lei", "10 minutes"], ["<"]);

        t.Section("Placeholder engine (scripted recognizer)");
        var fullNer = new Anonymizer(a, new DictionaryNer(("Andrei Ionescu", P), ("Maria Popescu", P), ("Radu Georgescu", P),
            ("Elena Dumitrescu", P), ("John Smith", P), ("Banca Transilvania", O), ("Northwind", O), ("Cluj-Napoca", L), ("Bucharest", L)));
        var missNer = new Anonymizer(a, new DictionaryNer()); // simulates the LLM missing everything
        var map = new PlaceholderMap();
        Expect("turn 1: person + client", "I'm Andrei Ionescu from Contoso.", ["<PERSON_1>", "<CLIENT_1>"], ["Andrei", "Ionescu", "Contoso"], fullNer, map);
        Expect("PII echoed in model reply is caught even if NER misses it", "Thanks, Andrei! How does Contoso handle Ionescu's approvals?",
            ["<PERSON_1>", "<CLIENT_1>"], ["Andrei", "Ionescu", "Contoso"], missNer, map);
        Expect("turn 3: same person keeps <PERSON_1>, new person gets <PERSON_2>", "Later Andrei Ionescu approved it and Maria Popescu checked it.",
            ["<PERSON_1> approved", "<PERSON_2> checked"], ["Andrei", "Maria", "<PERSON_3>"], fullNer, map);
        Expect("surname alone later -> same placeholder", "Popescu said it was fine.", ["<PERSON_2> said"], ["Popescu"], missNer, map);
        var rmap = new PlaceholderMap();
        Expect("Romanian name with diacritics", "Ștefan Rădulescu a verificat.", ["<PERSON_1>"], ["Ștefan", "Rădulescu"],
            new Anonymizer(a, new DictionaryNer(("Ștefan Rădulescu", P))), rmap);
        Expect("same name without diacritics -> same placeholder", "Mulțumesc, Stefan Radulescu!", ["<PERSON_1>"], ["Stefan", "Radulescu", "<PERSON_2>"], missNer, rmap);

        var resumed = new PlaceholderMap();
        resumed.SeedCountersFrom(["<PERSON_2> și <EMAIL_1> au lucrat cu <PERSON_1>."]);
        Expect("resumed conversation continues numbering (<PERSON_3>, <EMAIL_2>)", "Ioana Marin (ioana.marin@example.ro) preia.",
            ["<PERSON_3>", "<EMAIL_2>"], ["Ioana", "Marin", "<PERSON_1>", "<EMAIL_1>"], new Anonymizer(a, new DictionaryNer(("Ioana Marin", P))), resumed);
        Expect("tool names in KeepTerms are not anonymized", "We use SAP and Excel.", ["SAP", "Excel"], ["<ORG_"],
            new Anonymizer(a, new DictionaryNer(("SAP", O), ("Excel", O))));
        Expect("existing placeholders are left intact", "<PERSON_1> sent the file.", ["<PERSON_1> sent the file."], ["<PERSON_2>"],
            new Anonymizer(a, new DictionaryNer(("<PERSON_1>", P))));

        t.Section("Proper-noun safety net (recognizer misses everything)");
        Expect("missed Romanian person + bank are still caught", "trimit IBAN-ul spre validare la Radu Georgescu de la Banca Transilvania.",
            ["<PERSON_1>", "<ORG_1>"], ["Radu", "Georgescu", "Transilvania"], missNer);
        Expect("missed city is caught and typed LOCATION", "for the account in Cluj-Napoca and Bucharest.", ["<LOCATION_1>", "<LOCATION_2>"], ["Cluj", "Bucharest"], missNer);
        Expect("unknown proper noun -> <NAME_1>", "Last March the supplier Northwind sent invoice 7781 twice.", ["<NAME_1>"], ["Northwind"], missNer);
        Expect("first name at sentence start is caught", "Radu approved it yesterday.", ["<PERSON_1>"], ["Radu"], missNer);
        Expect("possessive of a known surname keeps its placeholder", "Thanks, Andrei! How does Contoso handle Ionescu's approvals?",
            ["<PERSON_1>", "<PERSON_1>'s"], ["Andrei", "Ionescu", "<NAME_"], missNer, map);
        Expect("tools, weekdays and departments are not over-anonymized", "We use SAP, Excel and Jira every Monday in Accounts Payable.",
            ["SAP", "Excel", "Jira", "Monday", "Accounts Payable"], ["<"], missNer);
        Expect("sentence-initial words are not over-anonymized", "Pitfall: people reinstall the client. Tip for a new colleague: always check first.",
            ["Pitfall", "Tip"], ["<"], missNer);
        Expect("summary headings are not over-anonymized", "- Goal / Context: pay suppliers on time\n- Tools & Systems: SAP, Excel\n- Tips for a New Colleague: check twice",
            ["Goal / Context", "Tools & Systems", "New Colleague"], ["<"], missNer);

        var failing = new Anonymizer(a, new FailingNer()).AnonymizeAsync("Andrei Ionescu approved it.", new PlaceholderMap()).GetAwaiter().GetResult();
        var threw = false;
        try { _ = failing.Anonymized; } catch (InvalidOperationException) { threw = true; }
        t.Check("recognizer failure -> fail closed (no storable text)", !failing.Success && threw && failing.Text.Length == 0, failing.Error);

        var demoMap = new PlaceholderMap();
        var leaks = new List<string>();
        foreach (var answer in DemoData.ScriptedAnswers)
        {
            var r = fullNer.AnonymizeAsync(answer, demoMap).GetAwaiter().GetResult();
            leaks.AddRange(DemoData.PiiValues.Where(v => r.Text.Contains(v, StringComparison.OrdinalIgnoreCase)));
        }
        t.Check("all demo answers: no PII value survives (deterministic + sweep plumbing)", leaks.Count == 0,
            leaks.Count == 0 ? $"{demoMap.Count} values mapped" : "leaked: " + string.Join(", ", leaks.Distinct()));

        t.Section("Storage + export (SQLite)");
        var dbPath = Path.Combine(DataPaths.Root, "selftest-offline.db");
        if (File.Exists(dbPath)) File.Delete(dbPath);
        var store = new ConversationStore(dbPath);
        var smap = new PlaceholderMap();
        AnonText A(string s) => fullNer.AnonymizeAsync(s, smap).GetAwaiter().GetResult().Anonymized;
        var cid = Guid.NewGuid().ToString("N");
        store.AddMessages(cid, "A process I own", "hash", [
            new MessageToStore(ChatRole.Assistant, A("What process do you own?"), "m", "v", new Dictionary<string, int>()),
            new MessageToStore(ChatRole.User, A("I'm Andrei Ionescu and I reconcile invoices in SAP."), "m", "v", new Dictionary<string, int> { ["PERSON"] = 1 }),
            new MessageToStore(ChatRole.Assistant, A("Thanks. What happens when an invoice is a duplicate?"), "m", "v", new Dictionary<string, int>()),
        ]);
        store.UpdateCoverage(cid, "goal,steps,tools");
        store.UpdateTitle(cid, A("Invoice reconciliation"));
        var list = store.ListConversations();
        var loaded = store.Load(cid);
        t.Check("conversation round-trips through SQLite", list.Count == 1 && loaded?.Messages.Count == 3 && loaded.Messages[1].Content.Contains("<PERSON_1>"),
            $"{list.Count} conversation(s), coverage {list.FirstOrDefault()?.CoveragePercent}%, entities {string.Join(",", list.FirstOrDefault()?.EntityCounts.Select(kv => kv.Key + "=" + kv.Value) ?? [])}");
        store.StoreEnabled = false;
        var off = !store.StoreEnabled;
        store.StoreEnabled = true;
        t.Check("opt-out setting persists", off && store.StoreEnabled);

        var jsonl = Path.Combine(DataPaths.Root, "selftest-offline.jsonl");
        using (var fs = File.Create(jsonl))
            ExportService.ExportJsonlAsync(store, new ExportFilter(null, null, 1, 0), fs, jsonl).GetAwaiter().GetResult();
        var lines = File.ReadAllLines(jsonl);
        var okShape = lines.Length == 1 && lines.All(l =>
        {
            using var doc = JsonDocument.Parse(l);
            var msgs = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
            return msgs.Count == 3 && msgs.All(m => m.GetProperty("role").GetString() is "user" or "assistant")
                && doc.RootElement.GetProperty("metadata").GetProperty("topic").GetString() == "A process I own";
        });
        t.Check("JSONL export: chat format, no system role, topic metadata", okShape, lines.FirstOrDefault()?.Length > 160 ? lines[0][..160] + "…" : lines.FirstOrDefault());
        t.Check("export filter: minimum turns", ExportService.Select(store, new ExportFilter(null, null, 5, 0)).Count == 0);
        t.Check("export filter: minimum coverage", ExportService.Select(store, new ExportFilter(null, null, 0, 100)).Count == 0
            && ExportService.Select(store, new ExportFilter(null, null, 0, 40)).Count == 1);
        t.Check("export filter: date range", ExportService.Select(store, new ExportFilter(DateTime.UtcNow.AddDays(1), null, 0, 0)).Count == 0);
    }
}
