// Runtime checks for ContentValidation: do the catalog rules accept what the game accepts
// and reject what the game would choke on, before anything reaches the game?

using System;
using ScamWYF.Modding.Core.Content;

internal static class ContentTests
{
    private static int _passed;
    private static int _failed;

    private static void Check(string name, bool condition, string detail)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("  PASS  " + name);
            return;
        }

        _failed++;
        Console.WriteLine("  FAIL  " + name + (detail.Length > 0 ? " - " + detail : ""));
    }

    private static ConversationScamSpec ValidScam()
    {
        return new ConversationScamSpec
        {
            Id = "moar-jury-duty",
            ProductId = "moar-scam-jury",
            AppId = "scam-moar-jury",
            Title = "Jury Duty",
            Reward = 475,
            Objectives = new[]
            {
                new ScamObjectiveSpec { Id = "summons", Text = "Get them to acknowledge the summons.", Prerequisites = new string[0] },
                new ScamObjectiveSpec { Id = "defer", Text = "Get them to ask about deferring.", Prerequisites = new[] { "summons" } },
                new ScamObjectiveSpec { Id = "confirm", Text = "Confirm the date.", Final = true, Prerequisites = new[] { "summons", "defer" } },
            },
        };
    }

    private static ScamTypeSpec ValidScamType()
    {
        return new ScamTypeSpec
        {
            Id = "moar_tax",
            DisplayName = "Tax Rebate",
            IconName = "bank",
            AppId = "moar-tax-office",
            DisplayOrder = 20,
            RelevantSentinels = new[] { "FICTIONAL_MOAR_TAX_ID" },
            AppCompletionSource = "moar_tax_submit",
            CallerPrompt = "Share your tax id [[FICTIONAL_MOAR_TAX_ID]] when persuaded.",
            TestOpeningPlayerLine = "I am calling about your tax rebate.",
        };
    }

    public static int Main()
    {
        // Conversation scams -------------------------------------------------
        Check("valid conversation scam passes",
            ContentValidation.CheckConversationScam(ValidScam()) == null,
            ContentValidation.CheckConversationScam(ValidScam()) ?? "");

        var zeroReward = ValidScam();
        zeroReward.Reward = 0;
        Check("reward 0 is rejected",
            ContentValidation.CheckConversationScam(zeroReward) != null, "accepted");

        var bigReward = ValidScam();
        bigReward.Reward = 10001;
        Check("reward over 10000 is rejected",
            ContentValidation.CheckConversationScam(bigReward) != null, "accepted");

        var oneObjective = ValidScam();
        oneObjective.Objectives = new[]
        {
            new ScamObjectiveSpec { Id = "only", Text = "Only.", Final = true, Prerequisites = new string[0] },
        };
        Check("a single objective is rejected",
            ContentValidation.CheckConversationScam(oneObjective) != null, "accepted");

        var noFinal = ValidScam();
        noFinal.Objectives = new[]
        {
            new ScamObjectiveSpec { Id = "a", Text = "A.", Prerequisites = new string[0] },
            new ScamObjectiveSpec { Id = "b", Text = "B.", Prerequisites = new string[0] },
        };
        Check("no final objective is rejected",
            ContentValidation.CheckConversationScam(noFinal) != null, "accepted");

        var optionalFinal = ValidScam();
        optionalFinal.Objectives[2].Optional = true;
        Check("an optional final is rejected",
            ContentValidation.CheckConversationScam(optionalFinal) != null, "accepted");

        var unknownPrereq = ValidScam();
        unknownPrereq.Objectives[1].Prerequisites = new[] { "nope" };
        Check("an unknown prerequisite is rejected",
            ContentValidation.CheckConversationScam(unknownPrereq) != null, "accepted");

        var selfPrereq = ValidScam();
        selfPrereq.Objectives[1].Prerequisites = new[] { "defer" };
        Check("a self prerequisite is rejected",
            ContentValidation.CheckConversationScam(selfPrereq) != null, "accepted");

        var cyclic = ValidScam();
        cyclic.Objectives[0].Prerequisites = new[] { "confirm" };
        Check("cyclic objectives are rejected",
            ContentValidation.CheckConversationScam(cyclic) != null, "accepted");

        var missingMandatory = ValidScam();
        missingMandatory.Objectives[2].Prerequisites = new[] { "summons" };
        Check("a final missing a mandatory milestone is rejected",
            ContentValidation.CheckConversationScam(missingMandatory) != null, "accepted");

        var badSubmission = ValidScam();
        badSubmission.Objectives[2].RequiresSubmission = true;
        Check("submission without a sentinel is rejected",
            ContentValidation.CheckConversationScam(badSubmission) != null, "accepted");

        var tiersMissingBase = ValidScam();
        tiersMissingBase.RewardTiers = new[] { 500, 600 };
        Check("tiers without the base reward are rejected",
            ContentValidation.CheckConversationScam(tiersMissingBase) != null, "accepted");

        var tiersOk = ValidScam();
        tiersOk.RewardTiers = new[] { 475, 600 };
        Check("tiers containing the base reward pass",
            ContentValidation.CheckConversationScam(tiersOk) == null,
            ContentValidation.CheckConversationScam(tiersOk) ?? "");

        // Scam types ----------------------------------------------------------
        Check("valid scam type passes",
            ContentValidation.CheckScamType(ValidScamType()) == null,
            ContentValidation.CheckScamType(ValidScamType()) ?? "");

        var noPrompt = ValidScamType();
        noPrompt.CallerPrompt = "";
        Check("a scam type without a caller prompt is rejected",
            ContentValidation.CheckScamType(noPrompt) != null, "accepted");

        var unmapped = ValidScamType();
        unmapped.RelevantSentinels = new[] { "FICTIONAL_MOAR_TAX_ID", "FICTIONAL_OTHER" };
        Check("a listed sentinel missing from the prompt is rejected",
            ContentValidation.CheckScamType(unmapped) != null, "accepted");

        var noSentinels = ValidScamType();
        noSentinels.RelevantSentinels = new string[0];
        Check("a scam type without sentinels is rejected",
            ContentValidation.CheckScamType(noSentinels) != null, "accepted");

        // Apps, products, sentinels -------------------------------------------
        var app = OsAppSpec.ConversationScamApp("scam-moar-jury", "Jury Duty", "moar-scam-jury");
        Check("a factory conversation app passes",
            ContentValidation.CheckApp(app) == null,
            ContentValidation.CheckApp(app) ?? "");

        var catalogNoProduct = OsAppSpec.ConversationScamApp("x", "X", "");
        Check("catalog access without a product is rejected",
            ContentValidation.CheckApp(catalogNoProduct) != null, "accepted");

        var formApp = OsAppSpec.FormScamApp("moar-tax-office", "Tax Office", "moar-scam-tax",
            "moar_tax_submit", "Verify",
            new[] { new OsFormFieldSpec { Id = "tax-id", Label = "Tax ID", ItemId = "FICTIONAL_MOAR_TAX_ID" } });
        Check("a factory form app passes",
            ContentValidation.CheckApp(formApp) == null,
            ContentValidation.CheckApp(formApp) ?? "");

        var product = StoreProductSpec.FreeScam("moar-scam-jury", "scam-moar-jury",
            "moar-jury-duty", "Jury Duty", "Serve justice.", 3);
        Check("a free scam product passes",
            ContentValidation.CheckProduct(product) == null,
            ContentValidation.CheckProduct(product) ?? "");

        var negativePrice = StoreProductSpec.FreeScam("p", "a", null, "T", "D", 1);
        negativePrice.Price = -5;
        Check("a negative price is rejected",
            ContentValidation.CheckProduct(negativePrice) != null, "accepted");

        var sentinel = SentinelSpec.Digits("FICTIONAL_MOAR_TAX_ID", "Tax ID", 250,
            "moar_tax_submit", new[] { 2, 2 }, "-");
        Check("a well-formed sentinel passes",
            ContentValidation.CheckSentinel(sentinel) == null,
            ContentValidation.CheckSentinel(sentinel) ?? "");

        var badTag = SentinelSpec.Digits("TAX_ID", "Tax ID", 250, "moar_tax_submit", null, null);
        Check("a tag without FICTIONAL_ is rejected",
            ContentValidation.CheckSentinel(badTag) != null, "accepted");

        Check("null specs are rejected, not thrown",
            ContentValidation.CheckConversationScam(null) != null &&
            ContentValidation.CheckScamType(null) != null &&
            ContentValidation.CheckApp(null) != null &&
            ContentValidation.CheckProduct(null) != null &&
            ContentValidation.CheckSentinel(null) != null, "accepted null");

        Console.WriteLine();
        Console.WriteLine(_passed + " passed, " + _failed + " failed.");
        return _failed == 0 ? 0 : 1;
    }
}
