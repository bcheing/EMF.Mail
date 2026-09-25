using Cheing;
using Cheing.Net.Ai;
using EMF.Mail.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EMF.Mail.Services
{
    public class CommandService(ClaudeService claude)
    {
        // Scoped to APPROVE/REJECT only for this pass -- ATTACH/CANCEL/STATUS/CREATE/IGNORE/ROUTE deferred.
        // candidateName is the party Claude proposed at classify time, if any -- lets a bare "approve" resolve without repeating the name.
        public async Task<Result<CmdResult>> InterpretApprovalReplyAsync(string subject, string body, string partyTpName, string? candidateName)
        {
            var label = partyTpName.ToLower();

            var content = new List<ClaudeContentPart>
    {
        new("text/plain", Text: $"Subject: {subject}\n\n{body}")
    };

            var context = candidateName is null
                ? $"No {label} could be guessed for this sender -- the administrator was asked to name one."
                : $"The administrator was told this sender looks like it may be from the {label} \"{candidateName}\" and asked to confirm or correct it.";

            var decisionInstructions = candidateName is null
                ? $"""
        Decide whether this reply approves or rejects the link. Since no candidate {label} was proposed, there is nothing to
        bare-confirm -- if the reply simply names a {label} with no rejection language, treat that as approving the link to
        that {label}.
        If the reply doesn't name a {label} and doesn't clearly approve or reject, leave CmdCode null -- do not guess.
        """
                : $"""
        Decide whether this reply approves or rejects the link.
        If approving without naming a different {label}, use the candidate name given above.
        If the reply names a different {label}, use the name given in the reply instead.
        If the reply doesn't clearly approve or reject, leave CmdCode null -- do not guess.
        """;

            var prompt = $$"""
        An administrator is replying to a request to authorize a new email sender to submit on behalf of a {{label}}.

        {{context}}

        The text given is only the administrator's own new reply -- any quoted prior thread has already been stripped out.

        {{decisionInstructions}}

        Respond with ONLY a JSON object (no other text) in this exact shape:
        {"CmdCode": "APPROVE" | "REJECT" | null, "PartyName": string | null}
        """;

            return await claude.AskClaude<CmdResult>(content, prompt, []);
        }
    }
}
