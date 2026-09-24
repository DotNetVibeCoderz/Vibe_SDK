using TypeSafeSdk;

namespace TypeSafe.JevGallery;

/// <summary>Domain use case yang dipamerkan galeri.</summary>
public enum SpecimenDomain { Game, Education, Work, Science, Simulation }

/// <summary>
/// Satu use case SDK yang bisa dijalankan langsung. <see cref="Questions"/> adalah schema
/// TypeSafe asli, dan <see cref="Code"/> adalah kode yang persis menghasilkan schema tersebut.
/// </summary>
public sealed record Specimen(
    string Accession,
    SpecimenDomain Domain,
    string Title,
    string Blurb,
    string SampleInput,
    Func<string, object> State,
    IReadOnlyDictionary<string, object> Questions,
    string Code)
{
    /// <summary>Huruf domain untuk nomor akses, misalnya <c>G-01</c>.</summary>
    public static char Letter(SpecimenDomain domain) => domain switch
    {
        SpecimenDomain.Game => 'G', SpecimenDomain.Education => 'E', SpecimenDomain.Work => 'W',
        SpecimenDomain.Science => 'S', _ => 'M'
    };
}

/// <summary>Katalog use case SDK TypeSafe lintas domain.</summary>
public static class SpecimenCatalog
{
    /// <summary>Seluruh specimen, terurut per domain.</summary>
    public static IReadOnlyList<Specimen> All { get; } = Build();

    /// <summary>Jumlah specimen per domain untuk badge navigasi.</summary>
    public static IReadOnlyDictionary<SpecimenDomain, int> Counts { get; } =
        All.GroupBy(s => s.Domain).ToDictionary(g => g.Key, g => g.Count());

    private static List<Specimen> Build()
    {
        var specimens = new List<Specimen>
        {
            // ── Game ────────────────────────────────────────────────────────────────
            Make(SpecimenDomain.Game, "NPC intent router",
                "Membaca kalimat pemain dan memilih satu handler dialog NPC. Deskripsi kriteria yang kaya membuat jawaban jauh lebih tajam daripada label telanjang.",
                "I need a blade that can cut iron chains — what will you sell me for these bandit trophies?",
                text => new { player_line = text, npc = "Blacksmith", location = "Forge" },
                new Dictionary<string, object>
                {
                    ["intent"] = Choice.Create(new Dictionary<string, string?>
                    {
                        ["trade"] = "Wants to buy, sell, barter, or price goods, weapons, and tools",
                        ["quest"] = "Asks for a task, job, bounty, rumour, or where to go next",
                        ["lore"] = "Asks about history, legends, families, or places",
                        ["farewell"] = "Is leaving, saying goodbye, or ending the conversation"
                    }, "Which dialogue handler should answer this line?")
                },
                """
                var response = await client.SystemOneAsync(
                    new { player_line = line, npc = "Blacksmith", location = "Forge" },
                    new Dictionary<string, object>
                    {
                        ["intent"] = Choice.Create(new Dictionary<string, string?>
                        {
                            ["trade"]    = "Wants to buy, sell, barter, or price goods, weapons, and tools",
                            ["quest"]    = "Asks for a task, job, bounty, rumour, or where to go next",
                            ["lore"]     = "Asks about history, legends, families, or places",
                            ["farewell"] = "Is leaving, saying goodbye, or ending the conversation"
                        }, "Which dialogue handler should answer this line?")
                    });

                var handler = response.Choices["intent"].Choice;
                var confidence = response.Choices["intent"].Confidence;
                if (confidence < 0.55) handler = "clarify";   // ask again instead of guessing
                Dialogue.Route(handler);
                """),

            Make(SpecimenDomain.Game, "Chat toxicity gate",
                "Noul mengembalikan nilai 0..1, bukan boolean. Ambang bisa diatur per server tanpa melatih ulang apa pun.",
                "you are useless, uninstall the game and never come back",
                text => new { message = text, channel = "team-chat" },
                new Dictionary<string, object>
                {
                    ["toxic"] = new NoulSchema("Is this message abusive toward another player?",
                        "Insults, harassment, slurs, or targeted hostility", "Trash talk, banter, or ordinary frustration")
                },
                """
                var response = await client.SystemOneAsync(
                    new { message = text, channel = "team-chat" },
                    new Dictionary<string, object>
                    {
                        ["toxic"] = new NoulSchema(
                            "Is this message abusive toward another player?",
                            True:  "Insults, harassment, slurs, or targeted hostility",
                            False: "Trash talk, banter, or ordinary frustration")
                    });

                var noul = response.Nouls["toxic"].Noul;
                if (noul >= 0.85) await Moderation.MuteAsync(playerId);
                else if (noul >= 0.60) await Moderation.QueueForReviewAsync(messageId);
                """),

            Make(SpecimenDomain.Game, "Quest difficulty tier",
                "Score mengembalikan nilai harapan pada skala berurutan, jadi tier bisa dipakai langsung sebagai angka.",
                "Escort the caravan through the swamp at night while bandits track the road",
                text => new { quest = text, party_level = 12 },
                new Dictionary<string, object>
                {
                    ["difficulty"] = new ScoreSchema("How hard is this quest for a level 12 party?",
                        "trivial", "steady", "demanding", "brutal")
                },
                """
                var response = await client.SystemOneAsync(
                    new { quest = description, party_level = 12 },
                    new Dictionary<string, object>
                    {
                        ["difficulty"] = new ScoreSchema(
                            "How hard is this quest for a level 12 party?",
                            "trivial", "steady", "demanding", "brutal")
                    });

                var score = response.Scores["difficulty"];
                quest.RewardMultiplier = 1.0 + score.Score * 0.35;   // 0..3 -> 1.00..2.05
                quest.TierLabel = score.NearestLabel;
                """),

            // ── Education ───────────────────────────────────────────────────────────
            Make(SpecimenDomain.Education, "Essay rubric band",
                "Satu panggilan menilai beberapa dimensi rubrik sekaligus, sehingga umpan balik konsisten antar pengajar.",
                "The industrial revolution changed cities. Factories grew. People moved. It was good and bad. I think it was mostly good because of jobs.",
                text => new { essay = text, prompt = "Assess the impact of the industrial revolution", grade = 9 },
                new Dictionary<string, object>
                {
                    ["argument"] = new ScoreSchema("How well is the argument developed?", "absent", "emerging", "adequate", "strong"),
                    ["evidence"] = new ScoreSchema("How well is the claim supported by evidence?", "none", "thin", "sufficient", "rich"),
                    ["needs_revision"] = new NoulSchema("Should this draft be returned for revision before grading?")
                },
                """
                var response = await client.SystemOneAsync(
                    new { essay = text, prompt = assignmentPrompt, grade = 9 },
                    new Dictionary<string, object>
                    {
                        ["argument"]       = new ScoreSchema("How well is the argument developed?",
                                                 "absent", "emerging", "adequate", "strong"),
                        ["evidence"]       = new ScoreSchema("How well is the claim supported by evidence?",
                                                 "none", "thin", "sufficient", "rich"),
                        ["needs_revision"] = new NoulSchema("Should this draft be returned for revision before grading?")
                    });

                // One request, three independent judgements.
                report.Argument = response.Scores["argument"].Score;
                report.Evidence = response.Scores["evidence"].Score;
                report.Returned = response.Nouls["needs_revision"].IsTrue;
                """),

            Make(SpecimenDomain.Education, "Student question triage",
                "Mengarahkan pertanyaan ke tutor mata pelajaran yang tepat sebelum antrian manusia tersentuh.",
                "I keep getting a negative discriminant when I solve for x, is the equation wrong?",
                text => new { question = text, course = "Algebra II" },
                new Dictionary<string, object>
                {
                    ["subject"] = Choice.Create("algebra", "geometry", "statistics", "study-skills", "administrative"),
                    ["blocked"] = new NoulSchema("Is the student stuck rather than merely curious?")
                },
                """
                var response = await client.SystemOneAsync(
                    new { question = text, course = "Algebra II" },
                    new Dictionary<string, object>
                    {
                        ["subject"] = Choice.Create("algebra", "geometry", "statistics",
                                                    "study-skills", "administrative"),
                        ["blocked"] = new NoulSchema("Is the student stuck rather than merely curious?")
                    });

                var queue = response.Choices["subject"].Choice;
                var priority = response.Nouls["blocked"].Noul >= 0.7 ? Priority.High : Priority.Normal;
                await Tutoring.EnqueueAsync(queue, priority, studentId);
                """),

            Make(SpecimenDomain.Education, "Reading level fit",
                "Menilai apakah sebuah bacaan cocok untuk jenjang tertentu, berguna saat menyusun bahan ajar otomatis.",
                "The mitochondrion synthesises adenosine triphosphate via oxidative phosphorylation across the inner membrane.",
                text => new { passage = text, target_grade = 6 },
                new Dictionary<string, object>
                {
                    ["level"] = new ScoreSchema("What reading level does this passage demand?",
                        "early primary", "upper primary", "lower secondary", "upper secondary", "tertiary"),
                    ["too_hard"] = new NoulSchema("Is this passage too advanced for a grade 6 reader?")
                },
                """
                var response = await client.SystemOneAsync(
                    new { passage = text, target_grade = 6 },
                    new Dictionary<string, object>
                    {
                        ["level"]    = new ScoreSchema("What reading level does this passage demand?",
                                           "early primary", "upper primary", "lower secondary",
                                           "upper secondary", "tertiary"),
                        ["too_hard"] = new NoulSchema("Is this passage too advanced for a grade 6 reader?")
                    });

                if (response.Nouls["too_hard"].IsTrue)
                    passage = await Library.FindSimplerAsync(topic, response.Scores["level"].Score);
                """),

            // ── Work ────────────────────────────────────────────────────────────────
            Make(SpecimenDomain.Work, "Support ticket triage",
                "Use case kanonik SDK: satu tiket, satu antrian, plus penanda urgensi untuk SLA.",
                "I was charged twice for my subscription this month and the refund never arrived.",
                text => new { document = text, customer_tier = "pro" },
                new Dictionary<string, object>
                {
                    ["category"] = Choice.Create("billing", "technical", "other"),
                    ["urgent"] = new NoulSchema("Does this ticket need a response within one hour?")
                },
                """
                var response = await client.SystemOneAsync(
                    new { document = ticket.Body, customer_tier = customer.Tier },
                    new Dictionary<string, object>
                    {
                        ["category"] = Choice.Create("billing", "technical", "other"),
                        ["urgent"]   = new NoulSchema("Does this ticket need a response within one hour?")
                    });

                ticket.Queue = response.Choices["category"].Choice;
                ticket.Sla   = response.Nouls["urgent"].IsTrue ? Sla.OneHour : Sla.NextDay;
                ticket.RequestId = response.RequestId;   // keep for observability
                """),

            Make(SpecimenDomain.Work, "Meeting action items",
                "Memeriksa apakah sebuah catatan rapat berisi komitmen nyata sebelum dibuatkan task.",
                "Rina will send the revised budget to finance before Friday and Adi will book the venue.",
                text => new { note = text, meeting = "Weekly planning" },
                new Dictionary<string, object>
                {
                    ["has_commitment"] = new NoulSchema("Does this note contain a commitment someone must act on?",
                        "A named person owes a specific deliverable", "Discussion, opinion, or status only"),
                    ["owner_named"] = new NoulSchema("Is the responsible person explicitly named?")
                },
                """
                var response = await client.SystemOneAsync(
                    new { note = line, meeting = meeting.Title },
                    new Dictionary<string, object>
                    {
                        ["has_commitment"] = new NoulSchema(
                            "Does this note contain a commitment someone must act on?",
                            True:  "A named person owes a specific deliverable",
                            False: "Discussion, opinion, or status only"),
                        ["owner_named"]    = new NoulSchema("Is the responsible person explicitly named?")
                    });

                if (response.Nouls["has_commitment"].IsTrue)
                    await Tasks.CreateAsync(line, assignUnowned: !response.Nouls["owner_named"].IsTrue);
                """),

            Make(SpecimenDomain.Work, "CV screening signal",
                "Skor terstruktur untuk pra-saring lamaran. CV ini sengaja ambigu: perhatikan confidence rendah pada fit, dan lihat kode contoh yang melemparnya ke peninjau manusia.",
                "Six years building distributed payment systems in C# and Go, led a team of four, no formal degree.",
                text => new { resume = text, role = "Senior backend engineer" },
                new Dictionary<string, object>
                {
                    ["seniority"] = new ScoreSchema("What seniority does this experience indicate?",
                        "junior", "mid", "senior", "staff"),
                    ["fit"] = Choice.Create("advance", "hold", "decline")
                },
                """
                var response = await client.SystemOneAsync(
                    new { resume = text, role = posting.Title },
                    new Dictionary<string, object>
                    {
                        ["seniority"] = new ScoreSchema("What seniority does this experience indicate?",
                                            "junior", "mid", "senior", "staff"),
                        ["fit"]       = Choice.Create("advance", "hold", "decline")
                    });

                var fit = response.ChoiceAnswers["fit"];
                // A low-confidence decline always goes to a human.
                application.Stage = fit.Choice == "decline" && fit.Confidence < 0.8
                    ? Stage.HumanReview
                    : Stage.From(fit.Choice);
                """),

            // ── Science ─────────────────────────────────────────────────────────────
            Make(SpecimenDomain.Science, "Abstract method tagging",
                "Memberi label metodologi pada abstrak untuk membangun indeks literatur yang bisa ditelusuri.",
                "We randomly assigned 240 participants to treatment and placebo arms and measured outcomes at 12 weeks.",
                text => new { abstract_text = text, field = "clinical medicine" },
                new Dictionary<string, object>
                {
                    ["method"] = Choice.Create(new Dictionary<string, string?>
                    {
                        ["randomised-trial"] = "Participants assigned to arms by randomisation",
                        ["observational"] = "Cohort, case-control, or cross-sectional without assignment",
                        ["simulation"] = "Computational or in-silico modelling",
                        ["review"] = "Systematic review or meta-analysis"
                    }, "Which study design does this abstract describe?"),
                    ["has_control"] = new NoulSchema("Does the study include a control or comparison group?")
                },
                """
                var response = await client.SystemOneAsync(
                    new { abstract_text = text, field = "clinical medicine" },
                    new Dictionary<string, object>
                    {
                        ["method"] = Choice.Create(new Dictionary<string, string?>
                        {
                            ["randomised-trial"] = "Participants assigned to arms by randomisation",
                            ["observational"]    = "Cohort, case-control, or cross-sectional without assignment",
                            ["simulation"]       = "Computational or in-silico modelling",
                            ["review"]           = "Systematic review or meta-analysis"
                        }, "Which study design does this abstract describe?"),
                        ["has_control"] = new NoulSchema("Does the study include a control or comparison group?")
                    });

                paper.Method = response.Choices["method"].Choice;
                paper.Controlled = response.Nouls["has_control"].IsTrue;
                """),

            Make(SpecimenDomain.Science, "Field observation coding",
                "Mengubah catatan lapangan bebas menjadi kode kategori yang bisa diagregasi.",
                "Three adult herons feeding in the shallows at dawn, no nesting behaviour observed.",
                text => new { observation = text, site = "Estuary transect 4" },
                new Dictionary<string, object>
                {
                    ["behaviour"] = Choice.Create(new Dictionary<string, string?>
                    {
                        ["feeding"] = "Foraging, hunting, or feeding in water or on land",
                        ["nesting"] = "Nesting, brooding, or tending young",
                        ["resting"] = "Roosting, perched, or stationary without foraging",
                        ["in-flight"] = "Flying, circling, or passing overhead",
                        ["absent"] = "No individuals recorded at the site"
                    }, "What behaviour was recorded?"),
                    ["record_quality"] = new ScoreSchema("How usable is this record for analysis?",
                        "unusable", "partial", "complete")
                },
                """
                var response = await client.SystemOneAsync(
                    new { observation = note, site = transect.Name },
                    new Dictionary<string, object>
                    {
                        ["behaviour"] = Choice.Create(new Dictionary<string, string?>
                        {
                            ["feeding"]   = "Foraging, hunting, or feeding in water or on land",
                            ["nesting"]   = "Nesting, brooding, or tending young",
                            ["resting"]   = "Roosting, perched, or stationary without foraging",
                            ["in-flight"] = "Flying, circling, or passing overhead",
                            ["absent"]    = "No individuals recorded at the site"
                        }, "What behaviour was recorded?"),
                        ["record_quality"] = new ScoreSchema("How usable is this record for analysis?",
                                                 "unusable", "partial", "complete")
                    });

                // Drop weak records before they reach the aggregate.
                if (response.Scores["record_quality"].Score >= 1.0)
                    dataset.Add(transect, response.Choices["behaviour"].Choice);
                """),

            Make(SpecimenDomain.Science, "Anomaly severity",
                "Menilai keparahan anomali instrumen agar peringatan berisik tidak membangunkan siapa pun tengah malam.",
                "Detector channel 7 drifted 4 sigma above baseline for 90 seconds then returned to normal.",
                text => new { anomaly = text, instrument = "Detector array" },
                new Dictionary<string, object>
                {
                    ["severity"] = new ScoreSchema("How severe is this anomaly?", "noise", "notable", "serious", "critical"),
                    ["page_oncall"] = new NoulSchema("Should this wake the on-call scientist right now?")
                },
                """
                var response = await client.SystemOneAsync(
                    new { anomaly = summary, instrument = instrument.Name },
                    new Dictionary<string, object>
                    {
                        ["severity"]    = new ScoreSchema("How severe is this anomaly?",
                                              "noise", "notable", "serious", "critical"),
                        ["page_oncall"] = new NoulSchema("Should this wake the on-call scientist right now?")
                    });

                var severity = response.Scores["severity"];
                await Alerts.RecordAsync(severity.Score, severity.NearestLabel);
                if (response.Nouls["page_oncall"].Noul >= 0.8) await Pager.RaiseAsync();
                """),

            // ── Simulation ──────────────────────────────────────────────────────────
            Make(SpecimenDomain.Simulation, "Legal move selection",
                "Pola yang dipakai TypeSafe Board Games: model hanya pernah melihat daftar langkah legal, sehingga langkah ilegal mustahil. Confidence rendah di simulator justru jujur — yang dijamin di sini adalah legalitas, bukan kualitas langkah.",
                "Board: X at centre, O at top-left. Legal moves: top-right, middle-left, middle-right, bottom-left, bottom-right.",
                text => new { board = text, side = "O" },
                new Dictionary<string, object>
                {
                    ["move"] = Choice.Create("top-right", "middle-left", "middle-right", "bottom-left", "bottom-right")
                },
                """
                // Only legal moves ever become criteria, so an illegal answer cannot exist.
                var legal = board.LegalMoves(Side.O);
                var response = await client.SystemOneAsync(
                    new { board = board.Describe(), side = "O" },
                    new Dictionary<string, object> { ["move"] = Choice.Create([.. legal]) });

                var move = response.Choices["move"].Choice;
                board.Apply(legal.Contains(move) ? move : Tactics.Fallback(board));
                """),

            Make(SpecimenDomain.Simulation, "Traffic incident dispatch",
                "Memilih unit yang dikirim dari laporan bebas, dipakai dalam simulator operasional kota.",
                "Two cars blocking the left lane on the ring road, one driver reports chest pain, fuel leaking onto the asphalt.",
                text => new { report = text, road = "Ring road km 14" },
                new Dictionary<string, object>
                {
                    ["dispatch"] = Choice.Create(new Dictionary<string, string?>
                    {
                        ["medical"] = "Injury or medical emergency is reported",
                        ["fire"] = "Fire, fuel, or hazardous material is involved",
                        ["traffic-police"] = "Obstruction or collision without injury or hazard",
                        ["tow-only"] = "Disabled vehicle with no other risk"
                    }, "Which unit should be dispatched first?"),
                    ["lane_closure"] = new NoulSchema("Does this require closing a lane?")
                },
                """
                var response = await client.SystemOneAsync(
                    new { report = call.Transcript, road = call.Location },
                    new Dictionary<string, object>
                    {
                        ["dispatch"] = Choice.Create(new Dictionary<string, string?>
                        {
                            ["medical"]        = "Injury or medical emergency is reported",
                            ["fire"]           = "Fire, fuel, or hazardous material is involved",
                            ["traffic-police"] = "Obstruction or collision without injury or hazard",
                            ["tow-only"]       = "Disabled vehicle with no other risk"
                        }, "Which unit should be dispatched first?"),
                        ["lane_closure"] = new NoulSchema("Does this require closing a lane?")
                    });

                // Probabilities expose multi-unit incidents that a single label would hide.
                foreach (var (unit, p) in response.ChoiceAnswers["dispatch"].Probabilities)
                    if (p >= 0.25) await Dispatch.SendAsync(unit, call.Location);
                """),

            Make(SpecimenDomain.Simulation, "Sensor state machine",
                "Menerjemahkan telemetri mentah menjadi state mesin yang bisa dipakai kontroler simulasi.",
                "Pump 3: pressure 1.8 bar falling, vibration within limits, inlet temperature rising steadily for 20 minutes.",
                text => new { telemetry = text, asset = "Pump 3" },
                new Dictionary<string, object>
                {
                    ["state"] = Choice.Create(new Dictionary<string, string?>
                    {
                        ["nominal"] = "Readings steady and within limits, no trend",
                        ["degrading"] = "Readings drifting: pressure falling or temperature rising over time",
                        ["fault"] = "Readings breach safe limits or an alarm is active",
                        ["offline"] = "No telemetry received from the asset"
                    }, "What state is this asset in?"),
                    ["maintenance_window"] = new NoulSchema("Should this asset be scheduled for maintenance this week?")
                },
                """
                var response = await client.SystemOneAsync(
                    new { telemetry = window.Describe(), asset = asset.Name },
                    new Dictionary<string, object>
                    {
                        ["state"] = Choice.Create(new Dictionary<string, string?>
                        {
                            ["nominal"]   = "Readings steady and within limits, no trend",
                            ["degrading"] = "Readings drifting: pressure falling or temperature rising over time",
                            ["fault"]     = "Readings breach safe limits or an alarm is active",
                            ["offline"]   = "No telemetry received from the asset"
                        }, "What state is this asset in?"),
                        ["maintenance_window"] = new NoulSchema(
                            "Should this asset be scheduled for maintenance this week?")
                    });

                simulation.Transition(asset, response.Choices["state"].Choice);
                if (response.Nouls["maintenance_window"].IsTrue) planner.Reserve(asset, DateTime.Today.AddDays(3));
                """)
        };

        return [.. specimens.OrderBy(s => s.Domain).ThenBy(s => s.Accession, StringComparer.Ordinal)];
    }

    private static int _counter;
    private static SpecimenDomain _lastDomain = (SpecimenDomain)(-1);

    private static Specimen Make(SpecimenDomain domain, string title, string blurb, string sampleInput,
        Func<string, object> state, Dictionary<string, object> questions, string code)
    {
        if (domain != _lastDomain) { _counter = 0; _lastDomain = domain; }
        var accession = $"{Specimen.Letter(domain)}-{++_counter:00}";
        return new Specimen(accession, domain, title, blurb, sampleInput, state, questions, code.Trim());
    }
}
