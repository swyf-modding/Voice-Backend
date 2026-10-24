using System;
using System.Collections.Generic;

namespace ScamWYF.Modding.Core.Content
{
    /// <summary>
    /// The rules a custom scam, app, product or sentinel must follow. Mirrors the checks the
    /// game itself runs when it loads its own files, so a spec rejected here would also have
    /// been rejected (or misbehaved) there.
    /// </summary>
    /// <remarks>
    /// Deliberately dependency-free: no Unity, no BepInEx, no JSON. The behaviour tests compile
    /// this file directly and run it on plain .NET, the same arrangement as the watcher and
    /// window geometry tests.
    ///
    /// Every method returns null when the spec is acceptable, or a single human-readable reason
    /// when it is not. The registry logs that reason with the owning mod's name; the game never
    /// sees the bad entry.
    /// </remarks>
    public static class ContentValidation
    {
        /// <summary>An id the game will accept: non-empty, no whitespace-only, under 128 chars.</summary>
        public static string CheckId(string value, string what)
        {
            if (string.IsNullOrEmpty(value)) return what + " has no id.";
            if (value.Trim().Length == 0) return what + " id is blank.";
            if (value.Length > 128) return what + " id '" + value + "' is over 128 characters.";
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c)) return what + " id '" + value + "' contains whitespace.";
            }
            return null;
        }

        public static string CheckScamObjective(ScamObjectiveSpec objective)
        {
            if (objective == null) return "objective is null.";
            var bad = CheckId(objective.Id, "objective");
            if (bad != null) return bad;
            if (string.IsNullOrEmpty(objective.Text) || objective.Text.Trim().Length == 0)
                return "objective '" + objective.Id + "' has no text.";
            if (string.IsNullOrEmpty(objective.Criteria) && string.IsNullOrEmpty(objective.Text))
                return "objective '" + objective.Id + "' has no criteria.";
            return null;
        }

        /// <summary>
        /// The game's own catalog rules: 2+ objectives, exactly one mandatory final, prerequisites
        /// name real objectives, no cycles, and the final requires every mandatory milestone.
        /// CallerPrompt is optional: current game files carry prompts in the app, not the scam.
        /// </summary>
        public static string CheckConversationScam(ConversationScamSpec spec)
        {
            if (spec == null) return "conversation scam is null.";
            var bad = CheckId(spec.Id, "conversation scam");
            if (bad != null) return bad;
            bad = CheckId(spec.ProductId, "conversation scam '" + spec.Id + "' product");
            if (bad != null) return bad;
            bad = CheckId(spec.AppId, "conversation scam '" + spec.Id + "' app");
            if (bad != null) return bad;
            if (string.IsNullOrEmpty(spec.Title) || spec.Title.Trim().Length == 0)
                return "conversation scam '" + spec.Id + "' has no title.";
            if (spec.Reward <= 0 || spec.Reward > 10000)
                return "conversation scam '" + spec.Id + "' reward " + spec.Reward +
                       " is outside 1-10000.";
            if (spec.RewardTiers != null)
            {
                if (spec.RewardTiers.Length == 0)
                    return "conversation scam '" + spec.Id + "' has empty reward tiers.";
                bool containsBase = false;
                foreach (int tier in spec.RewardTiers)
                {
                    if (tier == spec.Reward) containsBase = true;
                    if (tier < spec.Reward || tier > 10000)
                        return "conversation scam '" + spec.Id + "' has invalid reward tier " + tier + ".";
                }
                if (!containsBase)
                    return "conversation scam '" + spec.Id + "' reward tiers must contain the base reward.";
            }
            if (spec.Objectives == null || spec.Objectives.Length < 2)
                return "conversation scam '" + spec.Id + "' needs at least 2 objectives.";

            var ids = new Dictionary<string, ScamObjectiveSpec>(StringComparer.Ordinal);
            foreach (var objective in spec.Objectives)
            {
                bad = CheckScamObjective(objective);
                if (bad != null) return "conversation scam '" + spec.Id + "': " + bad;
                if (ids.ContainsKey(objective.Id))
                    return "conversation scam '" + spec.Id + "' has duplicate objective '" +
                           objective.Id + "'.";
                ids.Add(objective.Id, objective);
            }

            int finals = 0;
            foreach (var objective in spec.Objectives)
            {
                if (!objective.Final) continue;
                finals++;
                if (objective.Optional)
                    return "conversation scam '" + spec.Id + "' final objective '" + objective.Id +
                           "' must not be optional.";
            }
            if (finals != 1)
                return "conversation scam '" + spec.Id + "' needs exactly one mandatory final " +
                       "objective, found " + finals + ".";

            foreach (var objective in spec.Objectives)
            {
                var prereqs = objective.Prerequisites != null
                    ? objective.Prerequisites
                    : new string[0];
                foreach (var prereq in prereqs)
                {
                    if (prereq == objective.Id || !ids.ContainsKey(prereq))
                        return "conversation scam '" + spec.Id + "' objective '" + objective.Id +
                               "' has unknown or self prerequisite '" + prereq + "'.";
                }
                if (!string.IsNullOrEmpty(objective.RequiredSentinel) &&
                    !string.IsNullOrEmpty(spec.Sentinel) &&
                    !string.Equals(objective.RequiredSentinel, spec.Sentinel, StringComparison.Ordinal))
                    return "conversation scam '" + spec.Id + "' objective '" + objective.Id +
                           "' confirmation must use the scam's allowed sentinel.";
                if (objective.RequiresSubmission &&
                    (!objective.Final || string.IsNullOrEmpty(objective.RequiredSentinel) ||
                     spec.RewardTiers != null))
                    return "conversation scam '" + spec.Id + "' objective '" + objective.Id +
                           "': manual entry requires a final objective, a caller token and a fixed reward.";
            }

            foreach (var objective in spec.Objectives)
            {
                if (HasCycle(objective, ids, new Dictionary<string, bool>(StringComparer.Ordinal)))
                    return "conversation scam '" + spec.Id + "' has cyclic objectives at '" +
                           objective.Id + "'.";
            }

            ScamObjectiveSpec final = null;
            foreach (var objective in spec.Objectives)
            {
                if (objective.Final) final = objective;
            }
            if (final != null)
            {
                var required = new Dictionary<string, bool>(StringComparer.Ordinal);
                var finalPrereqs = final.Prerequisites != null
                    ? final.Prerequisites
                    : new string[0];
                foreach (var prereq in finalPrereqs) required[prereq] = true;
                foreach (var objective in spec.Objectives)
                {
                    if (objective.Final || objective.Optional) continue;
                    if (!required.ContainsKey(objective.Id))
                        return "conversation scam '" + spec.Id +
                               "': final objective must require all mandatory milestones " +
                               "(missing '" + objective.Id + "').";
                }
            }

            return null;
        }

        private static bool HasCycle(ScamObjectiveSpec current,
            Dictionary<string, ScamObjectiveSpec> ids, Dictionary<string, bool> visiting)
        {
            if (visiting.ContainsKey(current.Id)) return true;
            visiting[current.Id] = true;
            try
            {
                var prereqs = current.Prerequisites != null
                    ? current.Prerequisites
                    : new string[0];
                foreach (var prereq in prereqs)
                {
                    ScamObjectiveSpec next;
                    if (!ids.TryGetValue(prereq, out next)) continue;
                    if (HasCycle(next, ids, visiting)) return true;
                }
                return false;
            }
            finally
            {
                visiting.Remove(current.Id);
            }
        }

        /// <summary>
        /// The game's scam-type rules: every sentinel in the prompt must be listed, every listed
        /// FICTIONAL_* tag in the prompt must exist, and the AI configuration must be complete.
        /// </summary>
        public static string CheckScamType(ScamTypeSpec spec)
        {
            if (spec == null) return "scam type is null.";
            var bad = CheckId(spec.Id, "scam type");
            if (bad != null) return bad;
            if (string.IsNullOrEmpty(spec.DisplayName) || spec.DisplayName.Trim().Length == 0)
                return "scam type '" + spec.Id + "' has no display name.";
            bad = CheckId(spec.AppId, "scam type '" + spec.Id + "' app");
            if (bad != null) return bad;
            if (string.IsNullOrEmpty(spec.TestOpeningPlayerLine) ||
                spec.TestOpeningPlayerLine.Trim().Length == 0)
                return "scam type '" + spec.Id + "' has no testOpeningPlayerLine.";
            if (spec.RelevantSentinels == null || spec.RelevantSentinels.Length == 0)
                return "scam type '" + spec.Id + "' has no relevantSentinels.";
            if (string.IsNullOrEmpty(spec.CallerPrompt) || spec.CallerPrompt.Trim().Length == 0)
                return "scam type '" + spec.Id + "' has no callerPrompt.";
            foreach (var sentinel in spec.RelevantSentinels)
            {
                if (string.IsNullOrEmpty(sentinel) || sentinel.Trim().Length == 0)
                    return "scam type '" + spec.Id + "' lists a blank sentinel.";
                if (spec.CallerPrompt.IndexOf("[[" + sentinel + "]]", StringComparison.Ordinal) < 0)
                    return "scam type '" + spec.Id + "' does not map [[" + sentinel +
                           "]] in callerPrompt.";
            }
            return null;
        }

        public static string CheckApp(OsAppSpec spec)
        {
            if (spec == null) return "app is null.";
            var bad = CheckId(spec.Id, "app");
            if (bad != null) return bad;
            if (string.IsNullOrEmpty(spec.Title) || spec.Title.Trim().Length == 0)
                return "app '" + spec.Id + "' has no title.";
            if (string.IsNullOrEmpty(spec.ContentType) || spec.ContentType.Trim().Length == 0)
                return "app '" + spec.Id + "' has no contentType.";
            if (spec.Access != null && !string.IsNullOrEmpty(spec.Access.Mode) &&
                string.Equals(spec.Access.Mode, "catalog", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(spec.Access.ProductId) ||
                 spec.Access.ProductId.Trim().Length == 0))
                return "app '" + spec.Id + "' uses catalog access with no productId.";
            if (spec.Content != null && spec.Content.Binding != null &&
                string.Equals(spec.Content.Binding.Kind, "scenarioClaim", StringComparison.Ordinal) &&
                (string.IsNullOrEmpty(spec.Content.Binding.CompletionSource) ||
                 spec.Content.Binding.CompletionSource.Trim().Length == 0))
                return "app '" + spec.Id + "' scenarioClaim binding has no completionSource.";
            return null;
        }

        public static string CheckProduct(StoreProductSpec spec)
        {
            if (spec == null) return "product is null.";
            var bad = CheckId(spec.Id, "product");
            if (bad != null) return bad;
            bad = CheckId(spec.AppId, "product '" + spec.Id + "' app");
            if (bad != null) return bad;
            if (string.IsNullOrEmpty(spec.Title) || spec.Title.Trim().Length == 0)
                return "product '" + spec.Id + "' has no title.";
            if (spec.Price < 0) return "product '" + spec.Id + "' has a negative price.";
            return null;
        }

        public static string CheckSentinel(SentinelSpec spec)
        {
            if (spec == null) return "sentinel is null.";
            if (string.IsNullOrEmpty(spec.Tag) || spec.Tag.Trim().Length == 0)
                return "sentinel has no tag.";
            if (!spec.Tag.StartsWith("FICTIONAL_", StringComparison.Ordinal))
                return "sentinel tag '" + spec.Tag + "' must start with FICTIONAL_.";
            if (string.IsNullOrEmpty(spec.Label) || spec.Label.Trim().Length == 0)
                return "sentinel '" + spec.Tag + "' has no label.";
            if (spec.ValueEach < 0 || spec.ValueEach > 10000)
                return "sentinel '" + spec.Tag + "' valueEach is outside 0-10000.";
            if (string.IsNullOrEmpty(spec.AppCompletionSource) ||
                spec.AppCompletionSource.Trim().Length == 0)
                return "sentinel '" + spec.Tag + "' has no appCompletionSource.";
            return null;
        }
    }
}
