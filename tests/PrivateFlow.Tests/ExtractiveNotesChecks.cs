using PrivateFlow.Core;
using static Harness;

static class ExtractiveNotesChecks
{
    public static void VerifyExtractiveNotes()
    {
        // Extractive notes: each rule in English, German and Spanish; every line quotes and cites the rows it rests on.
        {
            MeetingDocument Meeting(params (string Speaker, string Text)[] spoken) => new() { Title = "Sync", Vocabulary = "Diego, Priya, Jonas, Petra, Lucía, Carmen, Murat, Ben",
                Segments = [.. spoken.Select((row, index) => new MeetingSegment { Id = $"{index + 1:x32}", Start = 10 * index, End = 10 * index + 5, Speaker = row.Speaker, Text = row.Text })] };
            string[] Block(MeetingDocument document, string heading)
            {
                var notes = MeetingArchive.Notes(document);
                var start = notes.IndexOf("\n\n" + heading + "\n", StringComparison.Ordinal);
                if (start < 0) return ["missing section " + heading];
                var rest = notes[(start + heading.Length + 3)..];
                var block = rest.IndexOf("\n\n", StringComparison.Ordinal) is var end and >= 0 ? rest[..end] : rest;
                return block == "None identified." ? [] : block.Split('\n');
            }
            // A line without its citations, and the numbers of the rows it cites.
            string Lines(MeetingDocument document, string heading) => string.Join(" | ", Block(document, heading).Select(line => System.Text.RegularExpressions.Regex.Replace(line, @" \[Sync · [^\]]*\]", "")));
            string Rows(MeetingDocument document, string heading) => string.Join(" | ", Block(document, heading).Select(line =>
                string.Join(",", System.Text.RegularExpressions.Regex.Matches(line, @" · ([0-9a-f]{32})\]").Select(match => Convert.ToInt32(match.Groups[1].Value, 16)))));
            const string Decisions = ExtractiveNotes.DecisionsHeading, Actions = ExtractiveNotes.ActionsHeading, Questions = ExtractiveNotes.QuestionsHeading, Times = ExtractiveNotes.TimesHeading, Summary = ExtractiveNotes.SummaryHeading;

            var agreed = new[] { Meeting(("Ana", "Let's ship on Friday."), ("Ben", "Sounds good.")), Meeting(("Petra", "Ich schlage vor, wir liefern am Freitag."), ("Jonas", "Einverstanden.")),
                Meeting(("Lucía", "Propongo lanzar el viernes."), ("Carmen", "Me parece bien.")) };
            Same(string.Join(" / ", agreed.Select(document => Lines(document, Decisions) + " " + Rows(document, Decisions))),
                "- Let's ship on Friday. · Ben: “Sounds good.” 1,2 / - Ich schlage vor, wir liefern am Freitag. · Jonas: “Einverstanden.” 1,2 / - Propongo lanzar el viernes. · Carmen: “Me parece bien.” 1,2",
                "a proposal someone else agrees to is one decision candidate that quotes and cites the agreement, in English, German and Spanish");
            var notAgreement = new[] { Meeting(("Ana", "Let's cancel the release."), ("Ben", "Great, thanks for the slides.")), Meeting(("Petra", "Lass uns das Release absagen."), ("Jonas", "Super, danke für die Folien.")),
                Meeting(("Lucía", "Cancelamos el lanzamiento."), ("Carmen", "Genial, gracias por las diapositivas.")), Meeting(("Ana", "Let's ship on Friday."), ("Ben", "Good morning, everyone.")),
                Meeting(("Ana", "Let's ship on Friday."), ("Ben", "Good question, let me check.")), Meeting(("Petra", "Wir liefern am Freitag."), ("Jonas", "Ja, das habe ich gesehen.")),
                Meeting(("Lucía", "Lanzamos el viernes."), ("Carmen", "Sí, eso lo vi ayer.")) };
            Check(notAgreement.All(document => Block(document, Decisions).Length == 0), "thanks, a greeting, a question back or a yes about something else after a proposal is no agreement");
            var fillers = new[] { Meeting(("Ana", "Let's ship on Friday."), ("Ben", "Hmm.")), Meeting(("Petra", "Wir liefern am Freitag."), ("Jonas", "Ähm, also.")), Meeting(("Lucía", "Lanzamos el viernes."), ("Carmen", "Pues, eh.")),
                Meeting(("Ana", "Ben, could you send the report?"), ("Ben", "Well.")) };
            Same(string.Join(" | ", fillers.Select(document => string.Join(";", Block(document, Decisions).Concat(Block(document, Actions))))), " |  |  | ", "a filler alone after a proposal or a request agrees to nothing");
            var acceptedUnrecognised = new[] { Meeting(("Remote speaker", "Call me back at 1230 about the invoice."), ("You", "Will do.")), Meeting(("Jonas", "Ruf mich um halb eins wegen der Rechnung zurück."), ("Petra", "Mach ich.")),
                Meeting(("Carmen", "Llámame a las doce y media por la factura."), ("Lucía", "Hecho.")), Meeting(("Ana", "The invoice is due."), ("Ben", "Sure, by Friday.")) };
            var ownTask = Meeting(("Ana", "The invoice is due."), ("Ben", "I'll send the invoice on Friday."));
            Same(string.Join(" | ", acceptedUnrecognised.Select(document => string.Join(";", Block(document, Actions)))) + " / " + Lines(ownTask, Actions), " |  |  |  / - I'll send the invoice on Friday. · Owner: Ben · Due: Friday",
                "an acceptance after a row that is not recognised as a request names no task and is not listed as an action item; a commitment that names its task still is");
            var bare = new[] { Meeting(("Ana", "The build is green."), ("Ben", "Okay.")), Meeting(("Petra", "Der Build ist grün."), ("Jonas", "Gut.")), Meeting(("Lucía", "El build está en verde."), ("Carmen", "Vale.")),
                Meeting(("Ana", "Agreed.")), Meeting(("Ana", "Let's ship on Friday."), ("Ana", "Okay.")) };
            Check(bare.All(document => Block(document, Decisions).Length == 0), "an agreement after a statement that proposes nothing, a bare agreement and agreeing with oneself are no decision");
            var reversed = new[]
            {
                Meeting(("Ana", "Let's ship on Friday."), ("Ben", "Agreed."), ("Priya", "The printer is late."), ("Ana", "Actually, let's ship on Monday instead."), ("Ben", "Fine with me.")),
                Meeting(("Petra", "Wir liefern am Freitag, oder?"), ("Jonas", "Ja."), ("Murat", "Die Inventur ist am Freitag."), ("Petra", "Dann liefern wir doch erst am Montag."), ("Jonas", "Einverstanden.")),
                Meeting(("Lucía", "Lanzamos el viernes."), ("Carmen", "De acuerdo."), ("Diego", "La imprenta llega tarde."), ("Lucía", "Al final lanzamos el lunes."), ("Carmen", "Vale.")),
            };
            Same(string.Join(" / ", reversed.Select(document => Rows(document, Decisions))), "1,2,4 | 4,5 / 1,2,4 | 4,5 / 1,2,4 | 4,5", "a later decision with a change cue marks the earlier one on the same subject and cites the change");
            Check(Lines(reversed[0], Decisions).StartsWith("- Let's ship on Friday. · Ben: “Agreed.” · Later changed: Ana: “Actually, let's ship on Monday instead.” | - Actually"), "the changed decision quotes what replaced it");
            var notReversed = new[]
            {
                Meeting(("Ana", "We decided to ship the mobile app on Friday."), ("Ben", "The budget review is complete."), ("Ana", "We decided to hire a designer on Friday instead.")),
                Meeting(("Petra", "Wir haben beschlossen, die App am Freitag zu liefern."), ("Jonas", "Das Budget ist fertig."), ("Petra", "Wir haben beschlossen, stattdessen am Freitag einen Designer einzustellen.")),
                Meeting(("Lucía", "Decidimos lanzar la app el viernes."), ("Carmen", "El presupuesto está listo."), ("Lucía", "Decidimos contratar a una diseñadora el viernes en lugar de eso.")),
                Meeting(("Ana", "Let's use Postgres instead of MySQL."), ("Ben", "Agreed."), ("Ana", "Let's use Postgres for the cache too."), ("Ben", "Sounds good.")),
                Meeting(("Ana", "We decided to ship the app on Friday."), ("Ben", "We decided to ship the docs on Monday."), ("Priya", "The printer is late."), ("Ana", "Let's ship it on Tuesday instead."), ("Ben", "Agreed.")),
            };
            Check(notReversed.All(document => !Lines(document, Decisions).Contains("Later changed")), "a shared date or decision word, a cue inside the earlier decision itself or two earlier decisions on the subject mark no change");
            var confirmed = new[] { Meeting(("Ana", "So we ship on Friday?"), ("Ben", "Yes.")), Meeting(("Petra", "Und wir schalten den Server am Montag ab, oder?"), ("Jonas", "Ja, Montag.")),
                Meeting(("Lucía", "¿Entonces lanzamos el viernes?"), ("Carmen", "Sí.")) };
            Same(string.Join(" / ", confirmed.Select(document => Rows(document, Decisions))), "1,2 / 1,2 / 1,2", "a decision asked as a question and confirmed is a decision");
            Check(new[] { Meeting(("Ana", "Did the tests pass?"), ("Ben", "Yes.")), Meeting(("Petra", "Ist der Build grün?"), ("Jonas", "Ja.")), Meeting(("Lucía", "¿Está listo el build?"), ("Carmen", "Sí.")) }
                .All(document => Block(document, Decisions).Length == 0), "a yes to a question about facts is no decision");
            var negatedAndConditional = new[] { Meeting(("Ana", "We will not ship on Friday."), ("Ben", "Agreed.")), Meeting(("Petra", "Wir liefern am Freitag nicht."), ("Jonas", "Einverstanden.")),
                Meeting(("Lucía", "No lanzamos el viernes."), ("Carmen", "De acuerdo.")), Meeting(("Ana", "If the tests pass, we ship on Friday."), ("Ben", "Agreed.")),
                Meeting(("Petra", "Wenn der Test grün ist, liefern wir am Freitag."), ("Jonas", "Gut.")), Meeting(("Lucía", "Si pasan las pruebas, lanzamos el viernes."), ("Carmen", "Vale.")) };
            Same(string.Join(" / ", negatedAndConditional.Select(document => Lines(document, Decisions))),
                "- We will not ship on Friday. · Ben: “Agreed.” / - Wir liefern am Freitag nicht. · Jonas: “Einverstanden.” / - No lanzamos el viernes. · Carmen: “De acuerdo.” / - If the tests pass, we ship on Friday. · Ben: “Agreed.”"
                + " / - Wenn der Test grün ist, liefern wir am Freitag. · Jonas: “Gut.” / - Si pasan las pruebas, lanzamos el viernes. · Carmen: “Vale.”",
                "a negated or conditional decision keeps its negation and condition because the row is quoted");
            Check(new[] { Meeting(("Ana", "Tom said we agreed to ship on Friday."), ("Ben", "Okay.")), Meeting(("Petra", "Herr Becker meinte, wir hätten beschlossen, am Freitag zu liefern."), ("Jonas", "Aha.")),
                Meeting(("Lucía", "Javier dijo que ya decidimos lanzar el viernes."), ("Carmen", "Vale.")), Meeting(("Ana", "We haven't decided that yet.")), Meeting(("Petra", "Das haben wir nicht beschlossen.")),
                Meeting(("Lucía", "Eso no lo hemos decidido.")), Meeting(("Ana", "We haven't made a decision yet.")), Meeting(("Ana", "There is no decision yet.")), Meeting(("Lucía", "Entonces tenemos un problema."), ("Carmen", "Vale.")),
                Meeting(("Petra", "Noch nicht entschieden.")) }.All(document => Block(document, Decisions).Length == 0), "quoted speech, a negated decision word, \"no decision yet\" and a consequence such as \"Entonces tenemos un problema\" are no decision");
            Check(new[] { Meeting(("Ana", "Let's deploy on the weekend."), ("Ben", "Agreed.")), Meeting(("Petra", "Morgen liefern wir die Geräte aus."), ("Jonas", "Einverstanden.")),
                Meeting(("Lucía", "Lanzamos el fin de semana."), ("Carmen", "De acuerdo.")) }.All(document => Block(document, Decisions).Length == 1),
                "a proposal mentioning the weekend or starting with \"Morgen\" is no small talk");
            Check(Lines(Meeting(("Ana", "We agreed: 15%."), ("Ana", "Agreed.")), Decisions) == "- We agreed: 15%." && Lines(Meeting(("Ana", "Decidimos lanzar el jueves.")), Decisions) == "- Decidimos lanzar el jueves."
                && Lines(Meeting(("Ana", "Wir haben beschlossen, am Freitag zu liefern.")), Decisions) == "- Wir haben beschlossen, am Freitag zu liefern.", "an explicit decision needs no agreement");

            var committed = new[] { Meeting(("Ben", "I'll send the report by Friday.")), Meeting(("Jonas", "Ich schicke den Bericht bis Freitag.")), Meeting(("Diego", "Me encargo del informe para el viernes.")) };
            Same(string.Join(" / ", committed.Select(document => Lines(document, Actions))),
                "- I'll send the report by Friday. · Owner: Ben · Due: Friday / - Ich schicke den Bericht bis Freitag. · Owner: Jonas · Due: Freitag / - Me encargo del informe para el viernes. · Owner: Diego · Due: viernes",
                "a commitment belongs to its speaker and is due when the row says");
            var accepted = new[] { Meeting(("Ana", "Priya, can you update the page by Monday?"), ("Priya", "Sure.")), Meeting(("Petra", "Jonas, kannst du die Liste bis Mittwoch schicken?"), ("Jonas", "Klar, mach ich.")),
                Meeting(("Lucía", "Carmen, ¿puedes confirmar la reserva antes del viernes?"), ("Carmen", "Sí, claro.")) };
            Same(string.Join(" / ", accepted.Select(document => Lines(document, Actions) + " " + Rows(document, Actions))),
                "- Priya, can you update the page by Monday? · Owner: Priya · Due: Monday 1,2 / - Jonas, kannst du die Liste bis Mittwoch schicken? · Owner: Jonas · Due: Mittwoch 1,2"
                + " / - Carmen, ¿puedes confirmar la reserva antes del viernes? · Owner: Carmen · Due: viernes 1,2", "an accepted request belongs to the one who accepts it and cites request and acceptance");
            var shortAcceptances = new[] { Meeting(("Ana", "Priya, can you update the page?"), ("Priya", "Sure, by Friday.")), Meeting(("Petra", "Jonas, kannst du die Liste schicken?"), ("Jonas", "Ja, bis Freitag.")),
                Meeting(("Lucía", "Carmen, ¿puedes confirmar la reserva?"), ("Carmen", "Sí, para el viernes.")), Meeting(("Petra", "Jonas, kannst du die Liste schicken?"), ("Jonas", "Gerne.")),
                Meeting(("Ana", "Ben, could you send the slides?"), ("Ben", "On it.")) };
            Same(string.Join(" / ", shortAcceptances.Select(document => Lines(document, Actions) + " " + Rows(document, Actions))),
                "- Priya, can you update the page? · Owner: Priya · Due: Friday 1,2 / - Jonas, kannst du die Liste schicken? · Owner: Jonas · Due: Freitag 1,2"
                + " / - Carmen, ¿puedes confirmar la reserva? · Owner: Carmen · Due: viernes 1,2 / - Jonas, kannst du die Liste schicken? · Owner: Jonas 1,2 / - Ben, could you send the slides? · Owner: Ben 1,2",
                "a short acceptance keeps the action item, with the date it names as the due date");
            Check(new[] { Meeting(("Ana", "Ben, could you send the slides?"), ("Ben", "Sure, but not by Friday.")), Meeting(("Ana", "Ben, could you send the slides?"), ("Ben", "Okay, Friday is the review.")),
                Meeting(("Ana", "Ben, could you send the slides?"), ("Ben", "Yes, the train leaves on Friday.")), Meeting(("Petra", "Jonas, kannst du die Liste schicken?"), ("Jonas", "Ja, der Zug fährt am Freitag.")),
                Meeting(("Lucía", "Carmen, ¿puedes confirmar la reserva?"), ("Carmen", "Sí, el tren sale el viernes.")) }.All(document => Block(document, Actions).Length == 0),
                "a short reply that says something else besides when is no acceptance");
            var declined = new[] { Meeting(("Ana", "Ben, could you cover Tuesday?"), ("Ben", "Sorry, I can't.")), Meeting(("Petra", "Murat, schaffst du das am Montag?"), ("Murat", "Das kann ich leider nicht.")),
                Meeting(("Lucía", "Diego, ¿te encargas tú?"), ("Diego", "No puedo, imposible esta semana.")), Meeting(("Ana", "Ben, could you cover Tuesday?"), ("Priya", "The printer is late.")) };
            Check(declined.All(document => Block(document, Actions).Length == 0), "a declined or unanswered request is no action");
            var otherTask = new[] { Meeting(("Ana", "Ben, could you send the report?"), ("Clara", "I'll book the room by Monday.")), Meeting(("Ana", "Ben, could you send the report?"), ("Clara", "Okay. I'll book the room by Monday.")),
                Meeting(("Petra", "Jonas, kannst du den Bericht schicken?"), ("Murat", "Ich buche den Raum bis Montag.")), Meeting(("Lucía", "Diego, ¿puedes mandar el informe?"), ("Carmen", "Reservo la sala el lunes.")) };
            Same(string.Join(" / ", otherTask.Select(document => Rows(document, Actions) + " " + System.Text.RegularExpressions.Regex.Match(Lines(document, Actions), " · Owner: ([^·]+?) · ").Groups[1].Value)),
                "2 Clara / 2 Clara / 2 Murat / 2 Carmen", "a commitment to something else after a request is its own task, not an acceptance of the request");
            var ownDeadline = new[] { Meeting(("Ben", "The review is on Tuesday. I'll send the report by Friday.")), Meeting(("Jonas", "Die Prüfung ist am Dienstag. Ich schicke den Bericht bis Freitag.")),
                Meeting(("Diego", "La revisión es el martes. Me encargo del informe para el viernes.")),
                Meeting(("Ben", "I'll send the report."), ("Ana", "By when?"), ("Clara", "I'll book the room by Monday.")) };
            Same(string.Join(" / ", ownDeadline.Select(document => Lines(document, Actions))),
                "- The review is on Tuesday. I'll send the report by Friday. · Owner: Ben · Due: Friday / - Die Prüfung ist am Dienstag. Ich schicke den Bericht bis Freitag. · Owner: Jonas · Due: Freitag"
                + " / - La revisión es el martes. Me encargo del informe para el viernes. · Owner: Diego · Due: viernes / - I'll send the report. · Owner: Ben | - I'll book the room by Monday. · Owner: Clara · Due: Monday", "a due date is only a time of the task's own sentence or the answer to \"by when?\" right after it");
            Check(new[] { Meeting(("Ana", "Can you see my screen?"), ("Ben", "Yes.")), Meeting(("Petra", "Kannst du mich hören?"), ("Jonas", "Ja.")), Meeting(("Lucía", "¿Puedes oírme?"), ("Carmen", "Sí.")) }
                .All(document => Block(document, Actions).Length == 0 && Block(document, Questions).Length == 0 && Block(document, Decisions).Length == 0), "an audio or screen check is no action, question or decision");
            var turkish = System.Globalization.CultureInfo.CurrentCulture;
            var shouted = new MeetingDocument { Title = "Sync", Vocabulary = "Iris", Segments = [new() { Id = $"{1:x32}", Start = 0, Speaker = "Remote speaker", Text = "IRIS, can you send the slides?" }, new() { Id = $"{2:x32}", Start = 10, Speaker = "Remote speaker", Text = "Sure." }] };
            var invariantNotes = NotesReport.Development.Select(meeting => MeetingArchive.Notes(meeting.Document)).Append(MeetingArchive.Notes(shouted)).ToArray();
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
                Check(Lines(shouted, Actions).Contains("· Owner: Iris") && NotesReport.Development.Select(meeting => MeetingArchive.Notes(meeting.Document)).Append(MeetingArchive.Notes(shouted)).SequenceEqual(invariantNotes),
                    "names and phrases match the same under a Turkish culture, so stored transcripts give the same owners and notes everywhere");
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = turkish; }
            var takenOver = new[] { Meeting(("Ana", "Ben, could you cover Tuesday?"), ("Ben", "Sorry, I can't."), ("Diego", "I can take Tuesday.")),
                Meeting(("Petra", "Murat, bestellst du die Geräte?"), ("Murat", "Das kann ich leider nicht."), ("Jonas", "Dann mache ich das.")),
                Meeting(("Lucía", "Diego, ¿compras tú los billetes?"), ("Diego", "No, yo no puedo."), ("Carmen", "Si quieres lo hago yo.")),
                Meeting(("Remote speaker", "Diego, can you send the slides tomorrow?"), ("Remote speaker", "Sure, will do.")),
                Meeting(("Remote speaker", "Diego, can you send the slides tomorrow?"), ("Remote speaker", "Sorry, I can't."), ("Remote speaker", "I'll do it.")) };
            Same(string.Join(" / ", takenOver.Select(document => string.Join(",", Block(document, Actions).Select(line => System.Text.RegularExpressions.Regex.Match(line, " · Owner: ([^·\\[]+?)(?= · | \\[)").Groups[1].Value)))),
                "Diego / Jonas / Carmen / Diego / Remote speaker", "someone other than the addressee who accepts owns the task; under one label for several people the addressed name does, unless they declined");
            var unassigned = new[] { Meeting(("Ana", "Someone needs to book the room.")), Meeting(("Petra", "Jemand muss den Raum buchen.")), Meeting(("Lucía", "Hay que reservar la sala.")) };
            Same(string.Join(" / ", unassigned.Select(document => Lines(document, Actions))), "- Someone needs to book the room. / - Jemand muss den Raum buchen. / - Hay que reservar la sala.", "a task nobody takes on is listed without an owner");
            var dueLater = new[] { Meeting(("Ana", "Ben, can you write the summary?"), ("Ben", "Sure."), ("Ana", "By when?"), ("Ben", "Friday.")),
                Meeting(("Petra", "Jonas, übernimmst du die Schulung?"), ("Jonas", "Ja, kann ich machen."), ("Petra", "Bis wann schaffst du das?"), ("Jonas", "Bis Freitag.")),
                Meeting(("Lucía", "Diego, ¿preparas tú el guion?"), ("Diego", "Sí, lo preparo yo."), ("Lucía", "¿Para cuándo lo puedes tener?"), ("Diego", "Para el lunes que viene.")) };
            Same(string.Join(" / ", dueLater.Select(document => Rows(document, Actions) + " " + System.Text.RegularExpressions.Regex.Match(Lines(document, Actions), " · Due: (.+)$").Groups[1].Value)),
                "1,2,3,4 Friday / 1,2,3,4 Freitag / 1,2,3,4 lunes que viene", "a due date asked for and given in a later row joins the action");
            var relative = new[] { Meeting(("Ben", "I'll do it next Friday.")), Meeting(("Jonas", "Ich mache das morgen.")), Meeting(("Carmen", "La termino yo, pasado mañana la tienes.")),
                Meeting(("Jonas", "Ich mache das nächsten Freitag.")), Meeting(("Petra", "Ich frage die Rechtsabteilung bis Ende der Woche.")), Meeting(("Diego", "Lo reviso el lunes que viene.")) };
            Same(string.Join(" / ", relative.Select(document => System.Text.RegularExpressions.Regex.Match(Lines(document, Actions), " · Due: (.+)$").Groups[1].Value)),
                "next Friday / morgen / pasado mañana / nächsten Freitag / Ende der Woche / lunes que viene", "relative due dates are kept as spoken");
            Check(new[] { Meeting(("Ben", "I won't send it.")), Meeting(("Jonas", "Ich kann das nicht machen.")), Meeting(("Diego", "No voy a hacerlo.")), Meeting(("Ben", "I'll share my screen.")) }
                .All(document => Block(document, Actions).Length == 0), "a negated commitment and a meeting gesture are no action");

            var unanswered = new[] { Meeting(("Ana", "Has anyone heard back from the lawyers?"), ("Ana", "Okay, that's it, thanks everyone.")),
                Meeting(("Petra", "Hat jemand etwas von der Rechtsabteilung gehört?"), ("Petra", "Gut, das war's.")), Meeting(("Lucía", "¿Alguien sabe algo de los abogados?"), ("Lucía", "Bueno, eso es todo.")) };
            Same(string.Join(" / ", unanswered.Select(document => Lines(document, Questions))),
                "- Has anyone heard back from the lawyers? · Not answered before: Ana: “Okay, that's it, thanks everyone.” / - Hat jemand etwas von der Rechtsabteilung gehört? · Not answered before: Petra: “Gut, das war's.”"
                + " / - ¿Alguien sabe algo de los abogados? · Not answered before: Lucía: “Bueno, eso es todo.”",
                "a question the meeting closes on is a candidate");
            var deferred = new[] { Meeting(("Ana", "Do we have the German translation?"), ("Ben", "No idea, let's park that.")), Meeting(("Petra", "Wie lange ist die Lieferzeit?"), ("Jonas", "Keine Ahnung, klären wir später.")),
                Meeting(("Lucía", "¿El presupuesto incluye las cenas?"), ("Carmen", "No lo sé, ya veremos.")) };
            Same(string.Join(" / ", deferred.Select(document => Lines(document, Questions) + " " + Rows(document, Questions))),
                "- Do we have the German translation? · Answer deferred: Ben: “No idea, let's park that.” 1,2 / - Wie lange ist die Lieferzeit? · Answer deferred: Jonas: “Keine Ahnung, klären wir später.” 1,2"
                + " / - ¿El presupuesto incluye las cenas? · Answer deferred: Carmen: “No lo sé, ya veremos.” 1,2", "a question whose answer defers it is a candidate and quotes the deferral");
            var answered = new[] { Meeting(("Ana", "How long will the hotfix take?"), ("Ben", "About a week.")), Meeting(("Petra", "Wie viele Leute kommen?"), ("Jonas", "Zwanzig.")),
                Meeting(("Lucía", "¿Cuántas personas vienen al stand?"), ("Carmen", "Veinte.")), Meeting(("Ana", "Do you offer training?"), ("Ben", "We do, two days. I'm not sure about the price.")),
                Meeting(("Ana", "Do we have a spare laptop?"), ("Ben", "Not sure."), ("Priya", "The printer is late."), ("Diego", "The room is booked."), ("Ana", "The slides are done."), ("Ben", "Back to the spare laptop: there is one in the cupboard.")),
                Meeting(("Lucía", "¿Tenemos una segunda tablet?"), ("Carmen", "No sé."), ("Diego", "La imprenta llega tarde."), ("Carmen", "El stand está reservado."), ("Diego", "Los folletos están listos."), ("Lucía", "Volviendo a lo de la tablet: sí, tenemos dos.")),
                Meeting(("Petra", "Haben wir noch einen Ersatzscanner?"), ("Jonas", "Weiß ich nicht."), ("Murat", "Der Drucker ist spät dran."), ("Jonas", "Der Raum ist gebucht."), ("Murat", "Die Folien sind fertig."), ("Jonas", "Zurück zu dem Ersatzscanner: Es gibt einen im Schrank.")),
                Meeting(("Omar", "Who doesn't love breakfast?"), ("Lea", "Ha.")), Meeting(("Petra", "Wer will schon nicht Kaffee?"), ("Jonas", "Ha.")), Meeting(("Lucía", "¿Quién no quiere café?"), ("Carmen", "Ja.")),
                Meeting(("Ana", "Can you all hear me?"), ("Ben", "Yes.")), Meeting(("Ana", "What is causing it? It is the date format.")),
                Meeting(("Ana", "Who's on the line today?"), ("Ben", "Hi, this is Ben."), ("Clara", "Hello.")), Meeting(("Ana", "What is the launch date?"), ("Priya", "The printer is late.")),
                Meeting(("Ana", "Who renews the certificate?"), ("Ben", "No idea."), ("Kim", "I can check the Apple account.")), Meeting(("Ana", "Is iOS working?"), ("Ben", "Todavía no, the certificate expired.")) };
            Check(answered.All(document => Block(document, Questions).Length == 0),
                "answered questions, those answered later with a reference back, rhetorical ones, audio checks, greetings as answers, unclear cases and questions someone takes on are no candidates");
            Check(new[] { Meeting(("Ana", "Which camera should we buy?"), ("Ben", "Okay, that's it for today.")) }.All(document => Block(document, Questions).Length == 1), "a question about a camera is no small talk");
            var pathological = new[] { "What is the launch date?", string.Join(" ", Enumerable.Repeat("hmmmmmm", 40)) + " x", string.Concat(Enumerable.Repeat("a, ", 4000)), string.Concat(Enumerable.Repeat("1.", 3000)),
                string.Concat(Enumerable.Repeat("¿", 5000)), string.Join(" ", Enumerable.Repeat("let's not let's not", 800)) + " x" };
            var reading = System.Diagnostics.Stopwatch.StartNew();
            var odd = MeetingArchive.Notes(Meeting([.. pathological.Select((text, index) => (index % 2 == 0 ? "Ana" : "Ben", text))]));
            reading.Stop();
            Check(reading.Elapsed < TimeSpan.FromSeconds(3) && odd.StartsWith(ExtractiveNotes.Header), "rows built to make the patterns backtrack are read in bounded time");
            var blank = new MeetingDocument { Title = "Sync", Segments = [new() { Start = 0, Text = " " }, new() { Start = 1, Speaker = "Ben", Text = "I'll send the report by Friday." }, new() { Start = 2, Text = "" }] };
            Check(Lines(blank, Actions) == "- I'll send the report by Friday. · Owner: Ben · Due: Friday" && MeetingArchive.Notes(new MeetingDocument { Segments = [new() { Text = "  " }] }) == "No transcript evidence yet."
                && ModelNotes.Fallback(blank, "API timed out.").Contains(ExtractiveNotes.ActionsHeading), "rows the editor emptied are skipped, also in the fallback of model notes");

            Same(string.Join(" / ", new[] { "Lumen 2.10 ships on Friday at 9:00.", "Nos vemos a las 12.30 el martes.", "Die Schulung ist am 3. April um 16 Uhr.", "This week or next month.",
                "Version 4.1.3 runs fine.", "We run version 8.0.10 now.", "Update to 4.1.30 today.", "Der Termin ist am 12.03.2026.", "Let's meet on Thursday at 4 pm." }.Select(text => string.Join(", ", ExtractiveNotes.TimesIn(text)))),
                "9:00, Friday / 12:30, martes / 3. April, 16:00 /  /  /  /  / 12.03.2026 / Thursday, 4 pm", "times mentioned are clock times, weekdays and dates; version numbers and vague spans are not");

            var opening = Meeting(("Ana", "Morning everyone, can you hear me?"), ("Ben", "Yes, loud and clear."), ("Ana", "Today we have three topics: the launch date, the pricing page and the support rota."),
                ("Ben", "The pricing page still shows the old price of 12 euros."), ("Ana", "Bye."));
            Check(Lines(opening, Summary).StartsWith("- Today we have three topics: the launch date, the pricing page and the support rota.") && !Lines(opening, Summary).Contains("Morning"), "the summary starts with the agenda row and leaves out small talk");
            Check(MeetingArchive.Notes(new MeetingDocument()) == "No transcript evidence yet.", "an empty transcript says so");
            var shown = MeetingArchive.Notes(opening);
            Check(shown.StartsWith(ExtractiveNotes.Header) && ExtractiveNotes.Header.Contains("only candidates") && ExtractiveNotes.Header.Contains("speaker label or the person addressed")
                && shown.Contains("\n\nDecision candidates — verify against the cited rows\n") && shown.Contains("\n\nOpen question candidates — verify against the cited rows\n"),
                "the notes themselves say that decisions and open questions are candidates and where owners come from, wherever they are shown or exported");
            var abbreviation = new MeetingDocument { Title = "Sync", Vocabulary = "AI", Segments = [new() { Start = 0, Speaker = "Ana", Text = "The email went out to the main list today." }] };
            var named = new MeetingDocument { Title = "Sync", Vocabulary = "AI, Lumen", Segments = [new() { Start = 0, Speaker = "Ana", Text = "The AI model for Lumen went out today." }] };
            Check(Block(abbreviation, Summary).Length == 0 && Block(named, Summary).Length == 1, "a vocabulary name counts as a whole word, not inside \"email\" or \"main\"");
            var smallTalk = Meeting(("Ana", "Hi all, can you hear me?"), ("Ben", "Morning."), ("Ana", "Thanks, bye."));
            Check(new[] { Summary, Decisions, Actions, Questions, Times }.All(heading => Block(smallTalk, heading).Length == 0) && MeetingArchive.Notes(smallTalk).StartsWith(ExtractiveNotes.Header),
                "a meeting of only small talk has every section and nothing in it");
            Check(Lines(Meeting(("Ben", "I'll send the report by Friday.")), Actions) == "- I'll send the report by Friday. · Owner: Ben · Due: Friday", "a transcript of one row is read");
            var longRow = Meeting(("Ana", string.Join(" ", Enumerable.Repeat("We discussed the launch plan in detail and the budget of 12 euros, then the rota for Tuesday and Wednesday, and nobody objected to anything.", 25)) + " I'll send the minutes by Friday."),
                ("Ben", "Agreed."));
            Check(Lines(longRow, Actions).EndsWith("I'll send the minutes by Friday. · Owner: Ana · Due: Friday") && Block(longRow, Times).Length == 1, "a row of several hundred words is read whole");
            var sameTime = Meeting(("Ana", "Let's ship on Friday."), ("Ben", "Agreed."), ("Ben", "I'll send the report by Monday."));
            foreach (var segment in sameTime.Segments) segment.Start = 0;
            Same(Rows(sameTime, Decisions) + " / " + Rows(sameTime, Actions), "1,2 / 3", "rows with identical timestamps keep their transcript order");
            var meetings = NotesReport.Development.Select(meeting => meeting.Document).Append(sameTime).ToArray();
            Check(meetings.All(document => MeetingArchive.Notes(document) == MeetingArchive.Notes(System.Text.Json.JsonSerializer.Deserialize<MeetingDocument>(System.Text.Json.JsonSerializer.Serialize(document))!)),
                "the same transcript gives byte-identical notes");
            // Nothing the notes state comes from outside the transcript: every point cites rows, and every owner and due date is a speaker or words of those rows.
            var grounded = meetings.All(document => MeetingArchive.Notes(document).Split('\n').Where(line => line.StartsWith("- ")).All(line =>
            {
                var cited = System.Text.RegularExpressions.Regex.Matches(line, @" · ([0-9a-f]{32})\]").Select(match => document.Segments.Single(segment => segment.Id == match.Groups[1].Value)).ToArray();
                var owner = System.Text.RegularExpressions.Regex.Match(line, " · Owner: (.+?)(?= · | \\[)"); var due = System.Text.RegularExpressions.Regex.Match(line, " · Due: (.+?)(?= · | \\[)");
                var said = string.Join(" ", cited.Select(segment => segment.Text));
                return cited.Length > 0 && (!owner.Success || cited.Any(segment => segment.Speaker == owner.Groups[1].Value) || said.Contains(owner.Groups[1].Value))
                    && (!due.Success || due.Groups[1].Value.Split(", ").All(part => said.Contains(part, StringComparison.OrdinalIgnoreCase) || ExtractiveNotes.TimesIn(said).Contains(part)));
            }));
            Check(grounded, "every point of the extractive notes cites transcript rows, and owners and due dates are speakers or words of those rows");
            // About as many rows as Whisper writes in an hour of conversation, one every five and a half seconds.
            var hour = new MeetingDocument { Title = "Hour", Segments = [.. Enumerable.Repeat(NotesReport.Development.SelectMany(meeting => meeting.Document.Segments), 2).SelectMany(round => round).Take(655)
                .Select((segment, index) => new MeetingSegment { Id = Guid.NewGuid().ToString("N"), Start = index * 5.5, End = index * 5.5 + 5, Speaker = segment.Speaker, Text = segment.Text })] };
            var building = System.Diagnostics.Stopwatch.StartNew(); MeetingArchive.Notes(hour); building.Stop();
            Console.WriteLine($"Extractive notes of {hour.Segments.Count} rows ({hour.Segments.Sum(segment => segment.Text.Length):N0} characters) took {building.ElapsedMilliseconds} ms.");
            Check(building.Elapsed < TimeSpan.FromSeconds(5), "notes of a 60-minute meeting are built within seconds");
            var development = NotesReport.Development.Select(meeting => NotesReport.Score(meeting, MeetingArchive.Notes(meeting.Document))).Aggregate((first, second) => first + second);
            Console.WriteLine(NotesReport.Table(NotesReport.Development.Select(meeting => (meeting, NotesReport.Score(meeting, MeetingArchive.Notes(meeting.Document))))));
            Check(development.Decisions.Precision >= 0.9 && development.Actions.Precision >= 0.9 && development.Actions.Recall >= 0.9 && development.BothRight >= 0.85 * development.Actions.Truth
                && development.Questions.Precision >= 0.9 && development.Times.Precision >= 0.85, "the extractive notes of the development meetings stay at the precision they were measured at");
            var dueMeeting = SyntheticMeeting.Parse("due", "Title: Due\nBen: I'll send the report by Friday. ## A:report; owner=Ben; due=Friday");
            var dueNotes = MeetingArchive.Notes(dueMeeting.Document);
            Check(NotesReport.Score(dueMeeting, dueNotes).DueRight == 1 && NotesReport.Score(dueMeeting, dueNotes.Replace("· Due: Friday", "· Due: Friday, Monday")).DueRight == 0,
                "the evaluation counts a due date with a date the ground truth lacks as wrong");
            Check(MeetingArchive.ExtractiveNotice(agreed[0]) == MeetingArchive.ExtractiveNoticeText && MeetingArchive.ExtractiveNotice(new MeetingDocument()) is null, "the notice applies once there is a transcript");
        }
    }
}
