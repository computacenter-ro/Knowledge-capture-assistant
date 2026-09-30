# Demo — Knowledge Capture (60 seconds)

## Pitch (3 sentences)

When an expert leaves, their know-how leaves with them — and interviewing people usually means collecting their
personal data too. Knowledge Capture is an on-device interviewer: a local LLM on the Copilot+ PC's **NPU** asks one
targeted question at a time until the key areas of a job are covered, then summarizes what it learned. Every turn is
anonymized **on the device before anything is saved** — names, CNPs, IBANs, phones, emails, clients become
`<PERSON_1>`-style placeholders — so the company gets a fine-tuning-ready dataset and nothing personal ever touches disk
or the cloud.

## Before you present

1. `.\run.ps1` (starts Foundry, loads the NPU model, launches the app). Wait for the green pill **● NPU · phi-4-mini**
   in the title bar. The app opens a new conversation automatically ("A process I own" is a good topic).
2. Optional: have Task Manager → Performance → **NPU** visible next to the app — the graph moves while it answers.

## 60-second click path

| Time | Do | Say |
|---|---|---|
| 0:00 | Point at the title-bar pill and the opening question | "Everything runs on this laptop's NPU through Foundry Local — no cloud. The assistant opens the interview itself." |
| 0:08 | Click **Daily work (EN)** → **Send** | "I'm answering like a real employee — with my name, email, phone, the client and the city." |
| 0:15 | Watch tokens stream; point at the right panel | "One question at a time. On the right, **What gets stored**: the name, client, city, email and phone are already placeholders — the original never touches disk." |
| 0:25 | Point at **Knowledge coverage** | "The checklist fills up as I explain — goal, steps, tools. The interviewer targets what's still missing." |
| 0:30 | Click **Proces (RO)** → **Send** | "Romanian works too: CNP and IBAN are checksum-validated, the bank and the colleague are recognized by the local model." |
| 0:42 | Click **Recurring problem (EN)** → **Send** | "IP, internal URL, card number — all gone. Same person twice gets the same placeholder." |
| 0:50 | Open **Stored data**, click the conversation | "This is literally what's in the database — anonymized, with counts per entity type." |
| 0:55 | Open **Export** → **Export JSONL…** | "One click: chat-format JSONL ready for fine-tuning. The mapping to real names was never stored, so it's irreversible." |

If time allows: toggle **Store anonymized → Memory only** to show the opt-out, or open an older conversation to show the
"Resumed conversation" notice and fresh placeholder numbering.

**Voice variant (swap in for 0:30, ~15 s):** click **Speak** (or Ctrl+M), say an answer out loud, e.g. *"Every Monday
I check the open tickets in ServiceNow, and if one is older than three days I escalate it to my team lead"*, click
**Done**. Say: "Whisper turns speech into text right here on the CPU. The recording stays in memory, never on disk. I can
fix a word, then Send, and it's anonymized like anything typed." The footer shows e.g. *5 s of speech → text in 2 s*.
Check the second title-bar pill (**● CPU · whisper-small**) before presenting, and do one test recording in the room.

## 3 sample inputs (the example buttons)

1. **Daily work (EN)** — "I'm Andrei Ionescu, team lead in Accounts Payable for the Contoso account in Cluj-Napoca. My goal is
   simple: suppliers get paid on time and we never pay the same invoice twice. Every morning I open the invoice queue in
   SAP, match each invoice to its purchase order, and flag mismatches in an Excel tracker. If you need me, it's
   andrei.ionescu@contoso.ro or +40 721 234 567."
2. **Proces (RO)** — "Sunt Maria Popescu și mă ocup de onboarding-ul furnizorilor noi în Proiectul Orion. Pașii: verific
   contractul, cer CUI-ul (de exemplu RO14399840), deschid furnizorul în SAP și trimit IBAN-ul RO49AAAA1B31007593840000
   spre validare la Radu Georgescu de la Banca Transilvania. Dacă valoarea contractului depășește 50.000 lei, cer
   aprobarea directorului financiar. Pentru testare folosim CNP-ul 1800101221144."
3. **Recurring problem (EN)** — "A recurring problem: the VPN drops for the Fabrikam team in our Bucharest office. Last
   Tuesday Elena Dumitrescu couldn't reach the file server at 10.20.30.40, so I checked the certificate in the portal
   https://it.fabrikam.internal/vpn … if it is urgent call John Smith on 0744 123 456. The test card on the account is
   4111 1111 1111 1111."

All values are synthetic (fake people; checksum-valid test CNP/IBAN/CUI/card).

## Fallback plan

* **Model slow / first load:** the pill is amber and the InfoBar says "Loading model on NPU…" — talk through the
  architecture slide for 20 s; the warm-up then makes the first real answer fast (~1 s to first token).
* **Background work queues up** (anonymization of the previous turn): the "What gets stored" row shows a progress bar —
  that *is* the point ("it anonymizes before saving"); wait 5-10 s before the next click.
* **Runtime down** (red pill): click **Retry** in the InfoBar; if Foundry is stuck, run `foundry server restart` and
  `foundry model load phi-4-mini-instruct-openvino-npu:1`, then Retry. Your typed answer stays in the box.
* **Voice misbehaves** (noisy room, mic blocked): skip it and use the example buttons; the typed flow is identical.
* **No NPU at all:** show the **Stored data** page of an earlier run and the `leakcheck.ps1` output (0 matches) instead of
  a live interview.
