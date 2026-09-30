using System.Text.RegularExpressions;

namespace KnowledgeCapture.Core.Services.Anonymization;

/// <summary>
/// Deterministic safety net behind the LLM recognizer: runs of Capitalized words that are not sentence-initial common
/// words, not stoplisted and not KeepTerms are treated as proper nouns and anonymized even if the small model missed them.
/// Typing: first-name gazetteer -> PERSON, organization cue -> ORG, city/country gazetteer -> LOCATION, else NAME.
/// Trade-off: this over-anonymizes unknown capitalized product names (add them to KeepTerms); it never under-anonymizes.
/// </summary>
public sealed class NameCandidates
{
    // words incl. inner hyphens ("Cluj-Napoca"); apostrophes split ("Northwind's" -> "Northwind" + "s")
    private static readonly Regex Token = new(@"\p{L}[\p{L}\p{Mn}]*(?:-\p{L}[\p{L}\p{Mn}]*)*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HashSet<string> _keep;

    public NameCandidates(IEnumerable<string> keepTerms)
    {
        _keep = keepTerms.SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(TextPatterns.Normalize).ToHashSet();
    }

    public IReadOnlyList<EntitySpan> Find(string text)
    {
        var tokens = Token.Matches(text).Cast<Match>().ToList();
        var spans = new List<EntitySpan>();
        var run = new List<Match>();
        var runStartsSentence = false;

        void Flush()
        {
            if (run.Count > 0)
            {
                var first = TextPatterns.Normalize(run[0].Value);
                // a single capitalized word that starts a sentence is usually just a normal word - unless it is a known first name;
                // an organization cue on its own ("the Bank") is generic
                var skip = run.Count == 1 && (runStartsSentence && !Gazetteer.FirstNames.Contains(first) || Gazetteer.OrgCues.Contains(first));
                if (!skip)
                {
                    var start = run[0].Index;
                    var end = run[^1].Index + run[^1].Length;
                    var value = text[start..end];
                    spans.Add(new EntitySpan(start, end - start, TypeOf(run), value));
                }
            }
            run.Clear();
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var sentenceStart = IsSentenceStart(text, t.Index);
            if (!IsCandidateToken(t.Value) || InsidePlaceholder(text, t.Index))
            {
                Flush();
                continue;
            }
            // continue a run only across a single space (e.g. "Radu Georgescu", "Banca Transilvania")
            if (run.Count > 0)
            {
                var prevEnd = run[^1].Index + run[^1].Length;
                if (sentenceStart || t.Index - prevEnd != 1 || text[prevEnd] != ' ') Flush();
            }
            if (run.Count == 0) runStartsSentence = sentenceStart;
            run.Add(t);
        }
        Flush();
        return spans;
    }

    private bool IsCandidateToken(string token)
    {
        if (!char.IsUpper(token[0])) return false;
        var head = token.Split('-')[0];
        if (head.Length >= 2 && head.All(c => !char.IsLetter(c) || char.IsUpper(c))) return false; // acronym: SAP, CUI-ul, VPN
        if (!token.Skip(1).Any(char.IsLower)) return false;
        var n = TextPatterns.Normalize(token);
        return !Gazetteer.Stop.Contains(n) && !_keep.Contains(n);
    }

    private static string TypeOf(List<Match> run)
    {
        var words = run.Select(m => TextPatterns.Normalize(m.Value)).ToList();
        var joined = string.Join(" ", words);
        if (words.Any(Gazetteer.OrgCues.Contains)) return EntityTypes.Org;
        if (Gazetteer.Places.Contains(joined) || words.Any(Gazetteer.Places.Contains)) return EntityTypes.Location;
        if (Gazetteer.FirstNames.Contains(words[0]) || words.Any(Gazetteer.FirstNames.Contains)) return EntityTypes.Person;
        return EntityTypes.Name;
    }

    private static bool IsSentenceStart(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c is ' ' or '\t' or '"' or '\'' or '(' or '“' or '„' or '*' or '#') continue;
            if (c is '-' or '•' or '–')
            {
                // a bullet at the start of a line starts a sentence; a dash inside a line does not
                var j = i - 1;
                while (j >= 0 && text[j] is ' ' or '\t') j--;
                return j < 0 || text[j] is '\n' or '\r';
            }
            return c is '.' or '!' or '?' or ':' or '\n' or '\r';
        }
        return true;
    }

    private static bool InsidePlaceholder(string text, int index)
    {
        var open = text.LastIndexOf('<', index);
        if (open < 0) return false;
        var close = text.IndexOf('>', open);
        return close >= index && EntityTypes.PlaceholderRegex.IsMatch(text[open..(close + 1)]);
    }
}

/// <summary>Small built-in word lists (normalized: lower-case, no diacritics).</summary>
internal static class Gazetteer
{
    private static HashSet<string> Set(string words) =>
        words.Split([' ', '\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries).Select(TextPatterns.Normalize).ToHashSet();

    // Capitalized words that are not personal data (calendar, headings, departments, roles, generic business words).
    public static readonly HashSet<string> Stop = Set("""
        monday tuesday wednesday thursday friday saturday sunday mon tue wed thu fri sat sun
        january february march april may june july august september october november december
        jan feb mar apr jun jul aug sep sept oct nov dec
        luni marti miercuri joi vineri sambata duminica ianuarie februarie martie aprilie mai iunie iulie
        septembrie octombrie noiembrie decembrie
        i i'm i've i'd i'll ok okay hi hello hey thanks thank dear yes no please welcome great good
        mr mrs ms miss dr dl dna domnul doamna domnisoara sir madam
        goal context steps step tools tool systems system decisions decision criteria exceptions exception
        pitfalls pitfall concrete example examples tips tip new colleague colleagues summary notes note earlier
        employee interviewer assistant topic question answer
        accounts account payable receivable finance financial procurement purchasing sales marketing legal
        support helpdesk service desk operations team lead manager director head department office project
        proiect proiectul client clientul clients supplier suppliers vendor vendors customer customers invoice
        invoices factura facturi payment payments purchase order orders queue tracker process owner workflow
        management onboarding reconciliation daily work role routine problem issue solution handling guide
        checklist report review approval request certificate server portal file internet email e-mail
        firma board committee ceo cfo cto hr it
        english romanian romana engleza euro
        """);

    public static readonly HashSet<string> OrgCues = Set("""
        banca bank group grup grupul company compania corporation corp inc ltd llc gmbh srl sa plc
        solutions services consulting technologies industries holding holdings partners foundation
        fundatia asociatia universitatea university institute institutul ministerul ministry agency agentia
        """);

    public static readonly HashSet<string> Places = Set("""
        bucuresti bucharest cluj cluj-napoca napoca iasi timisoara constanta craiova brasov galati ploiesti oradea
        braila arad pitesti sibiu bacau targu mures baia mare buzau botosani satu suceava deva alba iulia chisinau
        london paris berlin munich munchen vienna wien madrid barcelona rome roma milan milano amsterdam brussels
        bruxelles dublin warsaw varsovia prague praga budapest budapesta sofia belgrade zurich geneva frankfurt
        hamburg stockholm copenhagen oslo helsinki lisbon lisabona athens atena istanbul new york boston chicago
        seattle san francisco los angeles toronto singapore tokyo dubai leeds manchester birmingham edinburgh
        romania moldova germany germania france franta italy italia spain spania hungary ungaria bulgaria
        poland polonia austria netherlands olanda belgium belgia usa india china europe europa
        """);

    public static readonly HashSet<string> FirstNames = Set("""
        alexandru andrei adrian bogdan ciprian claudiu constantin cosmin costel cristian daniel darius dan dragos
        emil eugen florin gabriel george gheorghe horia ion ionut iulian laurentiu lucian liviu marian marius
        matei mihai mihnea mircea nicolae nicu octavian ovidiu paul petru radu razvan robert sebastian sergiu
        silviu sorin stefan teodor tudor valentin vasile vlad victor viorel
        adina adriana alexandra alina ana anca andreea bianca camelia carmen catalina claudia corina cristina
        daniela delia diana doina elena elisabeta florentina gabriela georgiana ileana ioana irina iulia laura
        larisa lavinia liliana loredana luminita madalina maria mariana mihaela monica nicoleta oana otilia
        raluca ramona roxana sabina silvia simona sorina teodora valentina veronica violeta viorica
        james john robert michael david richard joseph thomas charles christopher matthew anthony donald
        steven andrew joshua kenneth kevin brian edward ronald timothy jason jeffrey ryan jacob gary nicholas
        eric jonathan stephen larry justin scott brandon benjamin samuel gregory alexander patrick peter
        oliver harry tom tim mike dave steve chris alex sam ben nick jim joe
        mary patricia jennifer linda elizabeth barbara susan jessica sarah karen lisa nancy betty margaret
        sandra ashley kimberly emily donna michelle dorothy amanda melissa deborah stephanie rebecca sharon
        cynthia kathleen amy angela anna emma olivia sophia isabella charlotte amelia hannah chloe lucy kate
        jane julia helen rachel megan sophie natalie claire nicole
        """);
}
