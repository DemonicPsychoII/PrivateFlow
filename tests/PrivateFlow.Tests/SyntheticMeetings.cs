// Invented meetings for the notes evaluation; names, companies and figures are made up. The ground truth is in the tags (see
// NotesEvaluation.cs). The development meetings were written before the extractive rules; the held-back ones only after the rules were fixed.
static class SyntheticMeetings
{
    public const string Status = """
        Title: Lumen launch sync
        Vocabulary: Lumen, Diego, Priya, Okafor
        Languages: en
        Topics: launch date = launch; pricing = pricing, 1,250; support rota = rota, inbox; beta results = beta, testers
        Ana: Morning everyone, can you all hear me? ## S
        Ben: Yes, loud and clear. ## S
        Diego: Morning. Sorry, my camera is acting up again, one second. ## S
        Priya: Hi all. It is pouring here by the way, so if you hear thunder that's me. ## S
        Ana: Okay, let's start. Today we have three topics: the launch date for Lumen 2.10, the pricing page, and the support rota for launch week.
        Ana: First the launch date. QA signed off on the release candidate yesterday, so from my side we could ship on Tuesday. ## T: Tuesday
        Ben: What about the app store review? Last time it took four days.
        Diego: This time it should be faster, we submitted the build on Friday and it's already in review. ## T: Friday
        Priya: Then I propose we launch on Tuesday and announce it at 10:00 in the newsletter. ## D:launch | T: Tuesday, 10:00
        Ben: Sounds good to me.
        Ana: Agreed. Launch on Tuesday, newsletter at ten. ## D:launch | T: Tuesday, 10:00
        Diego: One thing, the pricing page still says version 2.9 everywhere
        Ana: right that's the second topic. Priya, can you update the pricing page copy before Monday? ## A:pricing-page; owner=Priya; due=Monday/Monday noon | T: Monday
        Priya: Sure, I'll do it by Monday noon. ## A:pricing-page | T: Monday, noon
        Ben: And the enterprise tier, are we keeping it at 1,250 euros a year?
        Ana: Yes, 1,250 stays. We only drop the monthly plan to twelve euros. ## D:pricing
        Priya: Okay, um, I'll put both numbers on the page then.
        Diego: Do we have the German translation of the page?  ## Q:translation
        Ben: Hmm, good question. I'm not sure who did that last time.
        Ana: Let's park that for now.
        Ana: Third topic, the support rota for launch week. We need two people on the support inbox from Tuesday to Thursday. ## T: Tuesday, Thursday
        Ana: Ben, could you cover the inbox on Tuesday and Wednesday? ## T: Tuesday, Wednesday
        Ben: Sorry, I can't, I'm at the trade fair in Lyon all week.
        Diego: I can take Tuesday and Wednesday. ## A:rota-diego; owner=Diego; due=Tuesday Wednesday | T: Tuesday, Wednesday
        Priya: And I'll do Thursday. ## A:rota-priya; owner=Priya; due=Thursday | T: Thursday
        Ana: Great, thanks both.
        @06:30
        Ana: Back to the launch for a second. Okafor from marketing wrote me this morning.
        Ana: The press embargo ends on Wednesday, so a Tuesday launch would break it. ## T: Wednesday, Tuesday
        Ben: Then we can't launch on Tuesday. We can't. ## T: Tuesday
        Priya: Let's launch on Wednesday at 9:00 instead, right when the embargo ends. ## D:launch-moved | R:launch | T: Wednesday, 9:00
        Diego: Fine with me.
        Ana: Okay, Wednesday it is. Newsletter at 9:30 then. ## D:launch-moved | T: Wednesday, 9:30
        Ben: Who tells the app store team?
        Diego: I'll do it. I'll move the release date in the console tomorrow morning. ## A:console; owner=Diego; due=tomorrow
        Ana: Perfect. And Diego please send me the release notes for 2.10 by Friday ## A:release-notes; owner=Diego; due=Friday | T: Friday
        Diego: Will do. ## A:release-notes
        Priya: Quick one, should we also do a webinar for the launch?  ## Q:webinar
        Ana: Maybe next month. We'll see how the launch goes.
        Ben: For the record, the beta had one hundred forty testers and 37 bug reports.
        Ben: Most of them were about sync on Android 14 and, uh, the widget.
        Ana: Ben are those all fixed
        Ben: All but two. The last two are scheduled for 2.10.1.
        Priya: How long will the hotfix take?
        Ben: About a week, maybe a week before the end of the month.
        Ana: Okay. Let's meet again on Thursday at 4 pm to check the numbers. ## D:follow-up | T: Thursday, 16:00
        Diego: Works for me.
        Priya: Same here.
        Diego: One last thing. Has anyone heard back from the lawyers about the new terms? ## Q:terms
        Ana: Okay, I think that's it. Thanks everyone, have a good day. ## S
        Ben: Bye. ## S
        Priya: Bye all. ## S
        """;

    public const string OneToOne = """
        Title: Weekly one-to-one
        Vocabulary: Marta, Kestrel
        Languages: en
        Topics: Kestrel migration = Kestrel, migration; training = course, training; holidays = holiday, vacation
        Remote speaker: Hey, how was your weekend? ## S
        You: Good, thanks. We finally went hiking, the weather was perfect. ## S
        Remote speaker: Nice. So, I have two things on my list and you said you had one.
        You: Yes, the Kestrel migration. I'm a bit behind. The import of the old tickets fails on about 3 percent of the records.
        Remote speaker: What's causing it?
        You: Mostly the date format. The old system writes days first, so the twelfth of March comes in as March twelfth, or it breaks. ## T: March 12
        Remote speaker: Can you fix that before the cut-over?
        You: I think so. I'll write a converter and rerun the import on Thursday. ## A:converter; owner=You; due=Thursday | T: Thursday
        Remote speaker: Okay. And the cut-over itself, is that still on the 28th?
        You: That's the plan, unless the converter fails again.
        Remote speaker: Then let's keep the 28th, and if the rerun fails we move it by a week. ## D:cutover
        You: Agreed.
        Remote speaker: Second thing, the training budget. You asked about the security course.
        You: Right, the one in Munich, it's four days and costs around two thousand euros.
        Remote speaker: I talked to finance. It's approved, but it has to happen before the end of the year. ## D:course
        You: Great, thank you, Marta.
        Remote speaker: Book it yourself, okay? And send me the invoice when you have it. ## A:book-course; owner=You; due=this week
        You: Sure, I'll book it this week. ## A:book-course
        Remote speaker: Third, the holiday plan. Who covers for you in August? ## Q:cover
        You: Hmm. I haven't asked anyone yet. Maybe Jonas, but I don't know if he's around.
        Remote speaker: Okay, let's figure that out later.
        You: Can I ask one more thing, will the team get a second tester next year? ## Q:tester
        Remote speaker: Honestly, no idea. That depends on the budget round.
        You: Fair enough.
        Remote speaker: Anything else? You seem a bit stressed lately.
        You: It's fine, it's just the migration. Once that's done, it's done.
        Remote speaker: Okay. Same time next week, Tuesday at 2 pm? ## T: Tuesday, 14:00
        You: Tuesday at 2 works. ## T: Tuesday, 14:00
        Remote speaker: Talk soon. ## S
        """;

    public const string Brainstorm = """
        Title: Onboarding brainstorm
        Vocabulary: Kai, Lea, Omar
        Languages: en
        Topics: first-day experience = first day, welcome; buddy program = buddy; documentation = wiki, docs
        Kai: Okay folks, this is a brainstorm, so no idea is too silly. The topic is how we make the first week better for new hires.
        Lea: Can I start? What if every new person gets a buddy for the first month?
        Omar: I like that. Or a buddy plus a weekly lunch with someone from another team.
        Kai: What would the buddy actually do, though?
        Lea: Answer the stupid questions. The ones you don't want to ask your manager.
        Omar: Ha, there are no stupid questions, right? ## S
        Kai: Another idea, we could record short videos of each team explaining what they do.
        Lea: Like two minutes each? That could work. Nobody reads the wiki anyway.
        Omar: The wiki is the problem, honestly. Half of it is from 2019 and wrong.
        Kai: So maybe a wiki cleanup day before the next hiring wave?
        Lea: Maybe. Or we just delete everything older than two years.
        Omar: That's a bit radical, no?
        Kai: Let's write it down anyway. Radical is fine today.
        Lea: What about the laptop? Last time it took three days until Sam had a working laptop.
        Omar: That's an IT thing, but yes, we could ask them to prepare it the Friday before. ## T: Friday
        Kai: I'll collect all of these in a doc after the meeting. ## A:collect; owner=Kai; due=-
        Lea: And a welcome breakfast on day one? With the whole team?
        Omar: People love breakfast. Who doesn't love breakfast? ## S
        Kai: How do we measure if any of this works?
        Lea: A short survey after thirty days, maybe? And after ninety?
        Omar: Do we even have numbers from the last hires to compare? ## Q:numbers
        Kai: Good point, I have no idea.
        Lea: We could ask HR.
        Kai: Okay, I think we have plenty. Thanks, everyone. ## S
        """;

    public const string CustomerCall = """
        Title: Call with Brightwater
        Vocabulary: Brightwater, Helix, Sanjay
        Languages: en
        Topics: rollout = rollout, pilot; outage = outage, incident; contract = contract, licences, licenses
        You: Hi Sanjay, hi everyone, thanks for making the time.
        Remote speaker: Hi. I have Rachel from our IT team with me today.
        Remote speaker: Hello. ## S
        You: Great. I'd suggest we go through three things, the pilot results, the outage last week, and the contract for the rollout.
        Remote speaker: Sounds good.
        You: So the pilot ran for six weeks with forty two users. Average response time went from 4 seconds to 1.2 seconds.
        Remote speaker: That matches what our people told us. They were happy, mostly.
        Remote speaker: Mostly, yes. The export to Excel is still slow.
        You: We know. That's fixed in Helix 5.4 which comes out in, uh, in two weeks.
        Remote speaker: Then the outage. What exactly happened on Wednesday? ## T: Wednesday
        You: A certificate expired on one of our gateways at 6:40 in the morning. Service was back at 8:15. ## T: 6:40, 8:15
        Remote speaker: An hour and a half. That's not great.
        You: No, it isn't. We've added monitoring for every certificate, and I'll send you the written incident report by Friday. ## A:incident-report; owner=You; due=Friday | T: Friday
        Remote speaker: Thank you. Could you also include what you changed in the monitoring?
        You: Yes, it'll be in there. ## A:incident-report
        Remote speaker: Okay. And the contract. We'd like to go from forty licences to four hundred.
        You: That works for us. The price per licence drops at three hundred, so you'd pay 18 euros instead of 25.
        Remote speaker: Can you put that in a written offer?
        You: Sure. Our sales team sends the offer on Monday. ## A:offer; owner=You/sales team; due=Monday | T: Monday
        Remote speaker: Do you also offer on-site training?
        You: We do, two days. I'm not sure about the price, I'll check that and add it to the offer. ## A:training-price; owner=You; due=-
        Remote speaker: One more question from IT. Can we host it ourselves later on? ## Q:self-host
        You: That's on our roadmap, but I can't promise a date yet.
        Remote speaker: Okay. So the plan is a full rollout in January if the offer is fine? ## D:rollout
        You: Yes, January, if the offer works for you. ## D:rollout
        Remote speaker: Good. Let's talk again after we've read the offer, maybe Thursday the 9th? ## T: Thursday
        You: Thursday works. Thanks a lot. ## T: Thursday
        Remote speaker: Thanks, bye. ## S
        """;

    public const string Short = """
        Title: Kurzer Check-in
        Vocabulary: Jonas, Petra
        Languages: de
        Topics: Serverumzug = Server, Umzug
        Petra: Hallo Jonas, nur kurz wegen dem Serverumzug. ## S
        Jonas: Hi. Ja, äh, der läuft. Die Datenbank ist schon drüben.
        Petra: Super. Und wann ist der Rest fertig?
        Jonas: Bis Donnerstag, wenn nichts schiefgeht. ## T: Donnerstag
        Petra: Kannst du mir dann eine kurze Mail schreiben? ## A:mail; owner=Jonas; due=Donnerstag
        Jonas: Klar, mach ich. ## A:mail
        Petra: Und wir schalten den alten Server am Montag ab, oder? ## D:shutdown | T: Montag
        Jonas: Ja, Montag um acht. ## D:shutdown | T: Montag, 8:00
        Petra: Gut, danke dir. ## S
        Jonas: Tschüss. ## S
        """;

    public const string Spanish = """
        Title: Planificación feria Valencia
        Vocabulary: Lucía, Carmen, Tom, Valencia, Orbita
        Languages: es,en
        Topics: stand = stand, puesto; folletos = folletos, flyers; demo = demo; presupuesto = presupuesto, budget
        Lucía: Buenos días a todos. ¿Me escucháis bien? ## S
        Carmen: Sí, perfecto. ## S
        Diego: Buenos días. Perdón, llego un minuto tarde, el tráfico estaba fatal. ## S
        Tom: Morning everyone, sorry, my Spanish is still a bit slow, so I'll switch to English sometimes. ## S
        Lucía: No pasa nada, Tom. Bueno, el orden del día de hoy: el stand para la feria de Valencia, los folletos, la demo de Orbita y el presupuesto.
        Lucía: Empezamos con el stand. La feria es del 12 al 14 de marzo y tenemos el puesto 41 en el pabellón dos. ## T: 14 de marzo
        Carmen: El año pasado el stand de nueve metros se quedó pequeño. ¿Pedimos uno más grande?
        Diego: Uno de dieciocho metros cuesta casi el doble, unos 7 400 euros.
        Lucía: Mmm, es mucho. Yo propongo quedarnos con el de nueve metros pero sin la mesa grande, para que haya más espacio.
        Carmen: Me parece bien. ## D:stand
        Diego: Vale, de acuerdo. ## D:stand
        Tom: Sounds fine to me too.
        Lucía: Perfecto, entonces nueve metros. Carmen, ¿puedes confirmar la reserva con la organización antes del viernes? ## A:reserva; owner=Carmen; due=viernes/viernes mañana | T: viernes
        Carmen: Sí, claro, lo hago mañana. ## A:reserva | T: mañana
        Lucía: Gracias. Ah, y otra cosa del stand: hay que contratar la conexión a internet. ## A:internet; owner=-; due=-
        Diego: Eso cuesta 300 euros por tres días, lo pone en el formulario.
        Lucía: Bueno, ya lo miraremos.
        Lucía: Segundo punto, los folletos. Tom, you were working on the English version, right?
        Tom: Yes, the English text is done. But the printer needs the final files by next Wednesday, the 4th. ## T: Wednesday
        Lucía: ¿Y la versión en español? ¿Quién la tiene?
        Carmen: La tenía Pablo, pero se fue de vacaciones.
        Lucía: Diego, ¿te encargas tú de terminar la versión en español?
        Diego: Uf, esta semana imposible, tengo las auditorías hasta el jueves. ## T: jueves
        Carmen: Si quieres la termino yo. Pasado mañana la tienes. ## A:folleto-es; owner=Carmen; due=pasado mañana
        Lucía: Genial, gracias, Carmen.
        Tom: How many flyers do we print? Last year it was two thousand and we brought half of them back.
        Lucía: Pues esta vez imprimimos mil. ## D:flyers
        Carmen: Mil está bien. ## D:flyers
        Tom: Okay, one thousand. ## D:flyers
        @12:00
        Lucía: Tercer punto, la demo de Orbita. Diego, ¿cómo va?
        Diego: Va bien, la versión 3.2 ya funciona en la tablet. Lo único es que la batería dura poco, unas dos horas y media.
        Tom: Two and a half hours is not enough for a full day at the booth.
        Diego: No, por eso necesitamos un cargador en el stand. O dos tablets.
        Lucía: ¿Tenemos una segunda tablet en la oficina?
        Carmen: Eh, creo que sí, pero no sé si funciona.
        Diego: Ni idea, la verdad.
        Lucía: Bueno, lo vemos luego.
        Tom: About the demo script. Javier said we already agreed to show the new dashboard first, but I don't remember that.
        Lucía: No, eso no lo hemos decidido. Pero me parece buena idea: si la demo de la tablet funciona, enseñamos primero el dashboard nuevo. ## D:demo-order
        Diego: Sí, vale.
        Carmen: De acuerdo. ## D:demo-order
        Lucía: Diego, ¿preparas tú el guion de la demo?
        Diego: Sí, lo preparo yo. ## A:guion; owner=Diego; due=lunes que viene
        Lucía: ¿Para cuándo lo puedes tener?
        Diego: Para el lunes que viene. ## A:guion | T: lunes
        Lucía: Perfecto.
        Tom: Sorry, quick question, who is actually going to Valencia? Do we have the hotel? ## Q:hotel
        Lucía: Vamos los cuatro. El hotel... eso lo tengo que mirar todavía.
        Carmen: Pero ojo, que los hoteles se llenan rápido con la feria.
        @18:30
        Lucía: Cuarto punto, el presupuesto. En total tenemos 12 000 euros para la feria.
        Diego: Con el stand, la conexión, los folletos y los viajes nos quedamos en unos once mil quinientos.
        Tom: That's really tight. What if something goes wrong?
        Lucía: Por eso no vamos a pedir el stand grande. Y los regalos para los clientes este año no los hacemos. ## D:no-gifts
        Carmen: ¿Ninguno? ¿Ni los bolígrafos?
        Lucía: Ninguno. ## D:no-gifts
        Carmen: Vale, es una pena, pero lo entiendo.
        Diego: Una pregunta, ¿el presupuesto incluye las cenas con clientes? ## Q:dinners
        Lucía: Buena pregunta. No lo sé, eso lo tengo que consultar con finanzas.
        Tom: Also, we never said if we go by train or by plane.
        Carmen: El tren es más barato y tarda solo tres horas desde Madrid.
        Diego: Yo prefiero el tren también.
        Lucía: Entonces vamos en tren. Carmen, ¿compras tú los billetes? ## D:train
        Carmen: No, yo no puedo comprar con la tarjeta de la empresa.
        Tom: I can do it, I have the company card. I'll book the train tickets by Friday. ## A:billetes; owner=Tom; due=Friday | T: Friday
        Lucía: Great, thanks Tom.
        @24:00
        Lucía: Volviendo a lo de la segunda tablet: acabo de mirar en el armario y sí está, y funciona.
        Diego: Ah, perfecto, entonces llevamos dos. ## D:two-tablets
        Lucía: Una cosa más sobre el stand. Me acaba de escribir la organización. El puesto de nueve metros ya no está disponible.
        Carmen: ¿Qué? ¿Entonces?
        Lucía: Solo quedan puestos de doce metros. Así que al final cogemos uno de doce metros. ## D:stand-12 | R:stand
        Diego: Bueno, no hay otra. De acuerdo. ## D:stand-12
        Tom: Does that still fit the budget?
        Lucía: Justo, con los regalos quitados creo que sí, pero lo reviso con finanzas antes del jueves. ## A:revisar-presupuesto; owner=Lucía; due=jueves | T: jueves
        Carmen: ¿Y quién va a hablar con el cliente de Sevilla? ¿Lo invitamos al stand? ## Q:sevilla
        Diego: Uf, no sé.
        Lucía: Lo dejamos para la próxima reunión.
        Lucía: Bueno, creo que ya está. ¿Nos vemos el martes a las diez para revisar todo? ## D:next-meeting | T: martes, 10:00
        Carmen: Sí, el martes a las 10 me va bien. ## D:next-meeting | T: martes, 10:00
        Tom: Tuesday at ten, perfect. ## T: Tuesday, 10:00
        Diego: Vale, hasta el martes. ## S | T: martes
        Lucía: Gracias a todos, ¡hasta luego! ## S
        """;

    public const string German = """
        Title: Projektrunde Lagerverwaltung
        Vocabulary: Jonas, Petra, Murat, Sabine, Becker, Lagerix
        Languages: de
        Topics: Release 4.2 = Release, 4.2; Scanner = Scanner, Handscanner; Schulung = Schulung; Lasttest = Lasttest; Datenschutz = Datenschutz
        Sabine: Guten Morgen zusammen. Hört ihr mich? ## S
        Murat: Ja, alles gut. ## S
        Petra: Morgen. Ich bin heute nur bis halb zwölf da, danach habe ich einen Arzttermin. ## S | T: 11:30
        Jonas: Morgen. Moment, ich muss noch kurz das Headset umstecken. ## S
        Jonas: So, jetzt. ## S
        Sabine: Gut. Also, heute stehen fünf Punkte auf der Tagesordnung: der Stand von Release 4.2, die neuen Handscanner, die Schulung für das Lager in Hamburg, der Lasttest und am Ende kurz der Datenschutz.
        Sabine: Fangen wir mit dem Release an. Murat, magst du kurz den Stand erzählen?
        Murat: Ja. Also, äh, von den 38 Tickets für 4.2 sind 31 erledigt, vier sind im Review und drei sind noch offen. Die drei offenen sind alle beim Wareneingang, da geht es um die Etiketten mit den neuen Barcodes, und eins davon ist ziemlich groß, weil wir dafür die Druckvorlagen komplett umbauen müssen. Ich schätze, dass wir dafür noch so zwei bis drei Tage brauchen, wenn nichts dazwischenkommt.
        Sabine: Das heißt, der Termin am Freitag, den 14. März, hält? ## T: Freitag, 14. März
        Murat: Knapp. Ich würde sagen, ja, aber nur, wenn wir die Druckvorlagen nicht noch mal ändern.
        Petra: Die Druckvorlagen sind fertig abgestimmt, da ändert sich nichts mehr.
        Sabine: Dann bleibt es beim Freitag, den 14. März, für 4.2. ## D:release | T: Freitag, 14. März
        Jonas: Einverstanden. ## D:release
        Murat: Ja, passt.
        Sabine: Murat, schickst du mir bis Mittwoch die Liste der offenen Punkte, damit ich sie an Herrn Becker weitergeben kann? ## A:liste; owner=Murat; due=Mittwoch | T: Mittwoch
        Murat: Mach ich. ## A:liste
        Jonas: Kurze Frage zum Release: Wer macht eigentlich die Release Notes dieses Mal? ## Q:release-notes
        Petra: Das hat letztes Mal der Tim gemacht, aber der ist ja nicht mehr im Team.
        Sabine: Hm. Das müssen wir noch klären.
        Murat: Und noch was. Die Version 4.1.3 läuft im Lager seit drei Wochen ohne Absturz. Das ist schon mal gut.
        Sabine: Sehr schön.
        @11:00
        Sabine: Zweiter Punkt, die Handscanner. Petra, du hast die Angebote eingeholt?
        Petra: Genau. Wir haben drei Angebote. Das günstigste ist 289 Euro pro Gerät, das teuerste 412 Euro. Bei 60 Geräten ist das ein Unterschied von, äh, etwa siebentausend Euro. Das mittlere Angebot ist 330 Euro, und das ist das einzige, bei dem die Akkus im laufenden Betrieb getauscht werden können, was das Lager ausdrücklich wollte.
        Jonas: Das mittlere klingt vernünftig.
        Murat: Ja, der Akkutausch ist im Schichtbetrieb echt wichtig.
        Petra: Ich schlage also das mittlere Angebot vor, 60 Geräte zu je 330 Euro.
        Sabine: Gut, dann nehmen wir das mittlere. ## D:scanner
        Jonas: Ja. ## D:scanner
        Murat: Einverstanden. ## D:scanner
        Sabine: Petra, bestellst du die Geräte? ## A:bestellung
        Petra: Das kann ich leider nicht, ich habe keine Freigabe für Bestellungen über zehntausend Euro.
        Jonas: Dann mache ich das, ich habe die Freigabe. ## A:bestellung
        Sabine: Danke, Jonas. Bis wann schaffst du das?
        Jonas: Nächste Woche, spätestens am Dienstag. ## A:bestellung; owner=Jonas; due=Dienstag/nächste Woche Dienstag | T: Dienstag
        Petra: Wie lange ist eigentlich die Lieferzeit? ## Q:lieferzeit
        Jonas: Das weiß ich nicht, steht nicht im Angebot.
        Murat: Müsste man mal nachfragen.
        Sabine: Ok.
        Petra: Was ist eigentlich mit den alten Scannern, die schmeißen wir doch nicht einfach weg, oder? ## Q:alte-scanner
        Sabine: Gute Frage, keine Ahnung. Machen wir später.
        @22:00
        Sabine: Dritter Punkt, die Schulung in Hamburg. Die ist für den 2. April geplant. ## T: 2. April
        Jonas: Der 2. April ist ein Mittwoch, oder? ## T: 2. April, Mittwoch
        Sabine: Ja, Mittwoch.
        Petra: An dem Tag ist aber Inventur in Hamburg, da hat keiner Zeit.
        Sabine: Oh. Das wusste ich nicht.
        Murat: Dann lieber den Donnerstag danach, den 3. April? ## T: Donnerstag, 3. April
        Sabine: Passt das für alle?
        Petra: Ja.
        Jonas: Ja, Donnerstag geht. ## D:schulung | T: Donnerstag
        Sabine: Gut, dann machen wir die Schulung am Donnerstag, den 3. April, von neun bis zwölf Uhr. ## D:schulung | T: Donnerstag, 3. April, 9:00, 12:00
        Murat: Wer hält die Schulung? Ich kann das nicht, ich bin in der Woche im Urlaub.
        Sabine: Jonas, übernimmst du das?
        Jonas: Ja, kann ich machen. Ich brauche aber die Folien von letztem Jahr. ## A:schulung-halten; owner=Jonas; due=Donnerstag 3. April
        Petra: Die schicke ich dir morgen. ## A:folien; owner=Petra; due=morgen
        Sabine: Und jemand muss den Raum in Hamburg buchen. ## A:raum; owner=-; due=-
        Murat: Ja, und Kaffee.
        Jonas: Ganz wichtig, ohne Kaffee keine Schulung. ## S
        Sabine: Wie viele Leute kommen eigentlich? ## Q:teilnehmer
        Petra: Hm, so zwanzig vielleicht, aber das ist geraten.
        Sabine: Okay, das müssen wir noch genau klären.
        @33:00
        Sabine: Vierter Punkt, der Lasttest. Murat?
        Murat: Ja. Wir haben den Lasttest letzte Woche gemacht, mit 500 gleichzeitigen Nutzern. Bis 400 Nutzer war die Antwortzeit unter einer Sekunde, ab 450 ging sie hoch auf drei Sekunden, und bei 500 gab es die ersten Timeouts. Im Lager sind im Moment maximal 250 Leute gleichzeitig angemeldet, also haben wir genug Luft. Aber im Weihnachtsgeschäft können es mehr werden.
        Jonas: Wie viele werden es im Weihnachtsgeschäft?
        Murat: Letztes Jahr waren es 380 in der Spitze.
        Sabine: Dann reicht das. Sollen wir trotzdem noch einen zweiten Server dazunehmen?
        Jonas: Würde ich nicht machen, das kostet im Monat fast neunhundert Euro.
        Petra: Sehe ich auch so.
        Sabine: Gut, dann nehmen wir keinen zweiten Server dazu. ## D:no-server
        Murat: Einverstanden, aber wenn die Zahlen im Oktober über 400 gehen, müssen wir das neu besprechen.
        Sabine: Ja, guter Punkt.
        Jonas: Murat, kannst du den Lasttest im Oktober noch mal laufen lassen? ## A:lasttest; owner=Murat; due=Oktober
        Murat: Ja, mache ich Anfang Oktober. ## A:lasttest
        @41:00
        Petra: Darf ich noch mal zum Release zurück? Herr Becker hat gestern gemeint, wir hätten schon beschlossen, das Release auf Montag zu legen. Das stimmt aber nicht, oder? ## T: Montag
        Sabine: Nein, das haben wir nicht beschlossen. Freitag, der 14. bleibt. ## T: Freitag
        Murat: Moment, Moment. Ich habe gerade eine Mail vom Lager bekommen.
        Murat: Die Inventur in Hamburg wurde verschoben, und zwar genau auf den Freitag, den 14. März. Am Inventurtag dürfen wir nichts einspielen. ## T: Freitag, 14. März
        Sabine: Na toll. Dann können wir am Freitag nicht ausliefern. ## T: Freitag
        Jonas: Dann machen wir es doch am Montag, den 17. März? Am Montag ist ruhig. ## T: Montag, 17. März
        Petra: Montag ist gut.
        Sabine: Okay, dann liefern wir 4.2 doch erst am Montag, den 17. März, aus, und zwar nicht vor 18 Uhr, wenn die Spätschicht weg ist. ## D:release-moved | R:release | T: Montag, 17. März, 18:00
        Murat: Einverstanden. ## D:release-moved
        Petra: Dann sag ich Herrn Becker, dass er diesmal recht hatte. ## A:becker; owner=Petra; due=-
        Sabine: Sehr witzig. Aber ja, bitte sag es ihm.
        Sabine: Murat, schaffst du das Einspielen am Montagabend?
        Murat: Montagabend kann ich nicht, da habe ich Elternabend.
        Jonas: Ich mache das. Ich bin sowieso bis neunzehn Uhr da. ## A:einspielen; owner=Jonas; due=Montag | T: 19:00
        Sabine: Danke.
        @49:00
        Sabine: Letzter Punkt, Datenschutz. Die Datenschutzbeauftragte möchte wissen, wie lange wir die Scannerprotokolle aufheben.
        Murat: Im Moment 180 Tage. Wir haben das nie bewusst entschieden, das war einfach der Standardwert.
        Petra: Brauchen wir die überhaupt so lange?
        Jonas: Für Reklamationen brauchen wir sie ungefähr 90 Tage.
        Sabine: Dann schlage ich vor, wir gehen auf 90 Tage runter. Ist das für alle okay? ## D:protokolle
        Murat: Ja. ## D:protokolle
        Petra: Ja, aber nur wenn die Rechtsabteilung zustimmt.
        Sabine: Natürlich. Ich frage die Rechtsabteilung bis Ende der Woche. ## A:rechtsabteilung; owner=Sabine; due=Ende der Woche
        Jonas: Und wer stellt den Wert dann um? Das ist ja nur eine Einstellung.
        Murat: Das mache ich, wenn die Rechtsabteilung zugestimmt hat. ## A:umstellen; owner=Murat; due=-
        Sabine: Gut. Gibt es sonst noch was?
        Petra: Ja, eine Sache noch, ganz kurz: Haben wir eigentlich jemals eine Antwort auf die Anfrage der Firma Lagerix wegen der Schnittstelle bekommen? ## Q:lagerix
        Sabine: Hm. Ich glaube nicht.
        Jonas: Ich auch nicht.
        Sabine: Dann ist das jetzt offen. Okay. Ich glaube, das war's. ## S
        Sabine: Vielen Dank euch allen. Und Petra, gute Besserung, falls es was Schlimmes ist. ## S
        Petra: Danke, ist nur eine Routineuntersuchung. Tschüss. ## S
        Murat: Ciao. ## S
        Jonas: Tschüss zusammen. ## S
        """;

    // Held back in round 1 (written after the rules were fixed at commit e064a15); used to change the rules in round 2.
    public const string Disagreement = """
        Title: Architecture review - sync engine
        Vocabulary: Mara, Felix, Jun, Postgres, SQLite
        Languages: en
        Topics: offline sync = sync, offline; database = Postgres, SQLite, database; deadline = deadline, quarter
        Mara: Okay, I know this one is going to be spicy. We need to settle how the mobile app syncs when it's offline.
        Felix: It's not spicy, it's obvious. We keep SQLite on the device and push a change log to Postgres. Done.
        Jun: That's what we said two years ago and it gave us the conflict mess with the field reports.
        Felix: The conflict mess came from bad merge rules, not from the log.
        Jun: No, sorry, I really disagree. With a log you can never tell which edit wins when two technicians edit the same report offline.
        Mara: Can we, uh, can we stay concrete? How many conflicts did we actually have last quarter?
        Jun: I counted about three hundred and forty, out of maybe nine thousand reports.
        Felix: Three hundred forty out of nine thousand is under four percent. That's not a crisis.
        Jun: It is a crisis when one of them is a safety inspection that got overwritten.
        Felix: Okay, that one was bad, I give you that.
        Mara: What's the alternative you'd propose, Jun?
        Jun: Per-field versions. Every field carries its own version, and we merge field by field instead of record by record.
        Felix: That triples the storage and nobody on the team has built that before.
        Jun: It doesn't triple anything, that's just not true
        Felix: It does if you keep history, which you'd have to for audits.
        Mara: Hold on, hold on. Felix, do you have numbers for the storage, or is that a guess? ## Q:storage
        Felix: It's an estimate. I'd have to check.
        Jun: So it's a guess.
        Felix: It's an informed estimate.
        Mara: Okay. So we don't know the storage cost. That's open.
        Jun: And what about the deadline? Do we even have time for a rewrite before the end of the quarter? ## Q:deadline
        Felix: That's exactly my point, we don't.
        Jun: We don't know that either. Nobody has estimated it.
        Mara: Let me suggest something. What if we prototype per-field versions on the inspection reports only, and keep the log for everything else?
        Felix: A hybrid? Then we maintain two sync paths forever. I'm against that.
        Jun: I could live with it, but only as a first step.
        Felix: I'm not agreeing to two sync paths. Sorry.
        Mara: Fine. Then we're not deciding this today. I don't want to force it.
        Jun: Can we at least agree on who looks at the storage question?
        Felix: I'll write down the storage numbers properly, give me until Thursday. ## A:storage-numbers; owner=Felix; due=Thursday | T: Thursday
        Mara: Thanks. Jun, could you write up the per-field design on one page, so we compare the same thing?
        Jun: Yes, I'll have it by Wednesday. ## A:design-page; owner=Jun; due=Wednesday | T: Wednesday
        Mara: And then we meet again next Friday at eleven. Does that work? ## D:follow-up | T: Friday, 11:00
        Felix: Friday's fine. ## D:follow-up | T: Friday
        Jun: Works. ## S
        Felix: For the record, I still think it's overkill.
        Jun: Noted. For the record, I still think the log is how we lost the inspection.
        Mara: Okay, okay. Thank you both, really. Let's stop here. ## S
        """;

    public const string OneLabel = """
        Title: Partner call Nordwind
        Vocabulary: Nordwind, Ingrid, Tomás, Amira, Lisbon
        Languages: en
        Topics: integration = API, integration, webhook; training = workshop, training; go-live = go-live, launch
        You: Hi everyone. Who's on the line from Nordwind today?
        Remote speaker: Hi, this is Ingrid, I'm with Tomás and Amira. ## S
        Remote speaker: Hello. ## S
        Remote speaker: Hi there. ## S
        You: Great. My plan for today was the API integration first, then the workshop in Lisbon, and last the go-live date.
        Remote speaker: Perfect. On the integration, our developers finished the webhook part last week
        Remote speaker: but the authentication still fails in the test environment. We get a 401 every time.
        You: That's probably the token scope. Which scopes did you request?
        Remote speaker: I think only read. Amira, you set it up, right?
        Remote speaker: Yes, only read. I didn't know we needed write.
        You: You need write for the webhooks. I'll send you the exact scope list today. ## A:scopes; owner=You; due=today
        Remote speaker: Thanks, that would help a lot.
        Remote speaker: Another question from my side. Is there a rate limit on the orders endpoint?
        You: Yes, one hundred twenty requests per minute per client.
        Remote speaker: Okay, that's enough for us.
        You: Then the workshop. We planned it for the twelfth of May in Lisbon, two days. ## T: 12 May
        Remote speaker: The twelfth doesn't work for us, it's a holiday week in our office.
        Remote speaker: Could we do the nineteenth instead? ## D:workshop
        You: The nineteenth works on our side.
        Remote speaker: Then let's do it on May 19th and 20th. ## D:workshop | T: May 19
        You: Agreed, May 19th and 20th in Lisbon. ## D:workshop | T: May 19
        You: Tomás, can you send me the list of participants by the end of next week? ## A:participants; owner=Tomás; due=end of next week
        Remote speaker: Sure, I'll do that. ## A:participants
        Remote speaker: How many people can attend, by the way? ## Q:capacity
        You: Good question, I need to check the room size. I'll come back to you. ## A:room-size; owner=You; due=-
        Remote speaker: Okay.
        You: Last topic, the go-live. We'd suggest Monday the 2nd of June. ## T: Monday, 2 June
        Remote speaker: That's a bit early for us. Our own release freeze ends on the 6th.
        Remote speaker: So maybe the week after? ## Q:go-live
        You: The week after is possible, but our support team is thinner then. I'd have to ask them first.
        Remote speaker: Then let's not fix it today.
        You: Okay, so the go-live date stays open until I've talked to support.
        Remote speaker: Fine with us.
        You: I'll ask support and get back to you by Wednesday. ## A:support; owner=You; due=Wednesday | T: Wednesday
        Remote speaker: Great. Anything else from our side? No? ## S
        Remote speaker: Then thank you, talk soon. ## S
        You: Thanks, bye. ## S
        """;

    public const string CodeSwitch = """
        Title: Sprint review app móvil
        Vocabulary: Valeria, Andrés, Kim, Figma, TestFlight
        Languages: es,en
        Topics: login = login; push = push, notificaciones; TestFlight = TestFlight, beta; diseño = Figma, design
        Valeria: Hola team, ¿empezamos? Kim, if you need English just shout. ## S
        Kim: Thanks, I mostly follow, pero hablo despacio. ## S
        Andrés: Vale. So the login flow ya está terminado, the only thing missing es el reset de contraseña.
        Valeria: ¿Y eso cuánto falta? Like, days or weeks?
        Andrés: Dos días, máximo tres. I'll finish it before Thursday. ## A:reset; owner=Andrés; due=Thursday | T: Thursday
        Kim: Okay, and the push notifications, are they working on Android now?
        Andrés: On Android sí, on iOS todavía no, because el certificado expired last week.
        Valeria: Who renews the certificate?
        Andrés: Ni idea, honestly, I think it was Pablo's account.
        Kim: I can check the Apple account, I have admin access. ## A:certificate; owner=Kim; due=-
        Valeria: Perfect, gracias Kim.
        Valeria: Next, TestFlight. Kim, ¿cuántos testers tenemos?
        Kim: Forty-two external testers, and like ten internal.
        Valeria: Bien. I want to send build 1.8.2 to all of them el viernes. ## T: viernes
        Andrés: Mmm, viernes is risky, el reset no va a estar en TestFlight todavía si lo termino el jueves.
        Kim: Then we send it el lunes and include the reset. ## T: lunes
        Valeria: Okay, sounds good, vamos el lunes con todo. ## D:testflight | T: lunes
        Andrés: De acuerdo. ## D:testflight
        Kim: Agreed.
        Valeria: Lo último, el diseño nuevo de onboarding en Figma. Kim, did you see the comments from marketing?
        Kim: Yes, they want less text en la primera pantalla. I think they're right.
        Andrés: Yo no estoy tan seguro, the text explains the permissions, if we remove it la gente rechaza las notificaciones.
        Kim: We could test both versions, an A/B test?
        Valeria: ¿Tenemos tiempo para un A/B test antes del launch? ## Q:ab-test
        Andrés: No sé, depende de marketing.
        Kim: Let's ask them.
        Valeria: Andrés, ¿le escribes tú a marketing? ## A:marketing; owner=Kim; due=-
        Andrés: Uf, esta semana no puedo, estoy con el reset.
        Kim: I'll write to them, no problem. ## A:marketing
        Valeria: Thanks. Ah, y una cosa: el demo day es el 3 de junio at 4 pm. ## T: 3 de junio, 16:00
        Kim: Noted.
        Valeria: Bueno, that's it for today. Gracias a todos, nos vemos. ## S
        """;

    // Held back in round 2: written after the rules were fixed at commit bd101bc and not used to change them.
    public const string Planning = """
        Title: Q3 conference planning
        Vocabulary: Hana, Marek, Odile, Riverside, Lyon
        Languages: en
        Topics: venue = venue, Riverside; speakers = speakers, keynote; registration = registration, tickets; catering = catering, lunch
        Hana: Hi both. I've got a hard stop at 3:30, so let's be quick. ## S | T: 3:30
        Marek: Sure. Hi. ## S
        Odile: Hello hello. ## S
        Hana: So the conference is on October 14th and 15th, that's fixed. The question is everything in between. ## T: October 14
        Marek: The Riverside venue wants a deposit by August 1st, otherwise they release the hall. ## T: August 1
        Odile: How much is the deposit?
        Marek: Thirty percent, so roughly 4,800 euros.
        Hana: Okay, then we pay it. Marek, can you get the invoice to finance this week? ## D:deposit | A:invoice; owner=Marek; due=this week/tomorrow/this week tomorrow
        Marek: Yep, I'll send it tomorrow. ## D:deposit | A:invoice
        Odile: About speakers. We have eleven confirmed, we need fourteen.
        Hana: Who's still missing? The keynote?
        Odile: The keynote and two panel slots. The keynote speaker answers by Friday, she said. ## T: Friday
        Hana: And if she says no?
        Odile: Then I'd go with the backup, the robotics guy from Lyon. ## D:keynote-backup
        Marek: Fine by me. ## D:keynote-backup
        Hana: Yeah, same.
        Odile: I'll chase the two panel slots and report back on Monday the 21st. ## A:panel; owner=Odile; due=Monday | T: Monday
        Hana: Registration. When do we open it?
        Marek: I had planned July 1st. ## T: July 1
        Odile: Too early, the program isn't final until mid-July.
        Hana: Let's open on July 15th then, with early bird until August 31st. ## D:registration | T: July 15, August 31
        Marek: Agreed. ## D:registration
        Odile: What do the early bird tickets cost? Last year was 290. ## Q:early-bird
        Hana: Hmm, I'd have to look at the budget sheet. Not sure yet.
        Marek: Catering, quickly. The Riverside caterer can do lunch for 300 people at 38 euros a head.
        Odile: Is that with vegetarian options?
        Marek: Yes, half of it vegetarian by default.
        Hana: Okay. Do we need dinner on the first evening as well? ## Q:dinner
        Odile: Honestly, let's decide that after we know the numbers.
        Marek: One more date: the venue wants the floor plan by September 5th. ## T: September 5
        Hana: Odile, can you do the floor plan?
        Odile: Not before the 10th, I'm away the first week of September.
        Marek: I'll do it then, no problem. ## A:floor-plan; owner=Marek; due=September 5
        Hana: Thanks. Okay, I have to run. Let's continue Thursday at ten. ## D:follow-up | T: Thursday, 10:00
        Marek: Thursday ten works. ## D:follow-up | T: Thursday
        Odile: Bye! ## S
        """;

    public const string Escalation = """
        Title: Escalada cliente Ferrovía
        Vocabulary: Ferrovía, Rocío, Iván, Nadia, Grafana
        Languages: es,en
        Topics: caída = caída, outage, incidencia; causa = causa, root cause, base de datos; compensación = compensación, crédito; comunicación = comunicado, email
        Rocío: Gracias por entrar tan rápido. Ferrovía nos ha escalado la caída de esta mañana, están bastante enfadados.
        Iván: Sí, lo vi. Empezó a las 7:12 y volvimos a las 8:40 más o menos. ## T: 7:12, 8:40
        Nadia: Perdón, ¿me oís? Estoy en el coche. ## S
        Rocío: Te oímos, Nadia. ## S
        Iván: Bueno, la causa fue la base de datos, se llenó el disco de logs y—
        Nadia: Espera, espera, ¿otra vez el disco? Eso pasó en marzo también.
        Iván: Sí, otra vez. La alerta de Grafana estaba silenciada desde la migración.
        Rocío: ¿Quién la silenció? ## Q:silenced
        Iván: No lo sé, honestamente. Tengo que mirar el historial. ## A:historial; owner=Iván; due=-
        Rocío: Vale. Lo primero es el cliente. Nadia, ¿puedes llamar hoy a su responsable de operaciones? ## A:llamada; owner=Nadia; due=hoy/hoy 11:00/11:00
        Nadia: Sí, le llamo en cuanto llegue a la oficina, sobre las once. ## A:llamada | T: 11:00
        Rocío: Perfecto. Y necesitamos un informe escrito para ellos.
        Iván: El postmortem lo escribo yo, pero necesito hasta el jueves. ## A:postmortem; owner=Iván; due=jueves | T: jueves
        Rocío: El jueves es tarde, ellos lo quieren el miércoles. ## T: jueves, miércoles
        Iván: Vale, el miércoles por la tarde entonces. ## A:postmortem; owner=Iván; due=miércoles | T: miércoles
        Nadia: They will also ask about compensation, I'm sure. Do we give them a credit?
        Rocío: El contrato dice un diez por ciento de crédito si la disponibilidad baja del 99,9.
        Iván: Y bajó, con una hora y media de caída este mes estamos en 99,7.
        Rocío: Entonces les damos el crédito del diez por ciento, sin discutir. ## D:credito
        Nadia: De acuerdo. ## D:credito
        Iván: Totalmente.
        Nadia: ¿Y les ofrecemos algo más, tipo un mes gratis? ## Q:extra
        Rocío: Eso no lo decido yo, lo tengo que hablar con dirección.
        Iván: Una cosa técnica: propongo que activemos la alerta otra vez hoy mismo y que la revisemos cada semana. ## D:alerta | A:alerta; owner=Iván; due=hoy
        Rocío: Sí, claro, hazlo. ## D:alerta | A:alerta
        Nadia: Sorry, I lost you for a second, the tunnel. What did we decide on the credit?
        Rocío: El diez por ciento. ## D:credito
        Nadia: Okay, perfect.
        Rocío: ¿Mandamos también un comunicado a todos los clientes o solo a Ferrovía? ## Q:comunicado
        Iván: No sé, depende de cuántos se vieron afectados. Lo miro.
        Rocío: Bueno, lo vemos mañana. Gracias a los dos. ## T: mañana
        """;

    public const string StandUp = """
        Title: Daily stand-up platform
        Vocabulary: Lena, Tobias, Priya, Jenkins, Helm
        Languages: de,en
        Topics: Pipeline = Jenkins, Pipeline, build; Helm = Helm, chart; Urlaub = Urlaub, vacation
        Lena: Morgen zusammen, let's go round. Tobias? ## S
        Tobias: Gestern hab ich den Jenkins build gefixt, der war seit Freitag rot. Heute mach ich die Helm charts für staging fertig. ## A:helm; owner=Tobias; due=heute | T: Freitag
        Lena: Super. Blocker?
        Tobias: Ja, ich brauche noch Zugriff auf den Vault. Priya, kannst du mir den geben? ## A:vault; owner=Priya; due=-
        Priya: Sure, I'll add you after the stand-up. ## A:vault
        Lena: Priya, du?
        Priya: Yesterday I finished the load test report, the p95 is at 340 milliseconds now. Today I'm on the alerting rules.
        Lena: 340 is fine for now. Sollen wir das Ziel trotzdem auf 300 setzen? ## Q:ziel
        Tobias: Hm, ich weiß nicht, das ist eher eine Produktfrage.
        Priya: Agree, product should decide that.
        Lena: Okay, ich frag den Product Owner. ## A:product-owner; owner=Lena; due=-
        Lena: Von mir: ich bin ab Montag eine Woche im Urlaub. Wer übernimmt das Release am Mittwoch? ## T: Montag, Mittwoch
        Tobias: Ich kann das machen. ## A:release; owner=Tobias; due=Mittwoch/Mittwoch 14:00
        Priya: Wait, the release is Wednesday or Thursday? I had Thursday in my calendar. ## T: Wednesday, Thursday
        Lena: Mittwoch, 14 Uhr. Thursday was the old date. ## T: Mittwoch, 14:00, Thursday
        Priya: Okay, Wednesday then.
        Tobias: Und machen wir das Release diesmal mit dem neuen Helm chart oder noch mit dem alten?
        Lena: Gute Frage. Wenn staging bis Dienstag stabil läuft, dann mit dem neuen. ## D:chart | T: Dienstag
        Priya: Sounds good. ## D:chart
        Tobias: Passt.
        Lena: Gut. Sonst noch was? Nein? Dann bis morgen. ## S | T: morgen
        """;
}
