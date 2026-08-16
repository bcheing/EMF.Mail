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
        // candidateVendName is the vendor Claude proposed at classify time (from MsgContext), if any -- lets
        // a bare "yes"/"approve" resolve without needing the admin's reply to repeat the vendor name.
        public async Task<Result<CmdResult>> InterpretApprovalReplyAsync(string subject, string body, string? candidateVendName)
        {
            var content = new List<ClaudeContentPart>
            {
                new("text/plain", Text: $"Subject: {subject}\n\n{body}")
            };

            var context = candidateVendName is null
                ? "No vendor could be guessed for this sender -- the administrator was asked to name one."
                : $"The administrator was told this sender looks like it may be from \"{candidateVendName}\" and asked to confirm or correct it.";

            // No candidate exists to bare-confirm when candidateVendName is null -- the email only ever asked
            // for a vendor name (or REJECT), so a reply that's just a name, with no approve/reject language at
            // all, is the expected shape of an approval and must resolve as one, not fall through to null.
            var decisionInstructions = candidateVendName is null
                ? """
                Decide whether this reply approves or rejects the link. Since no candidate vendor was proposed, there is nothing to
                bare-confirm -- if the reply simply names a vendor with no rejection language, treat that as approving the link to
                that vendor.
                If the reply doesn't name a vendor and doesn't clearly approve or reject, leave CmdCode null -- do not guess.
                """
                : """
                Decide whether this reply approves or rejects the link.
                If approving without naming a different vendor, use the candidate vendor name given above.
                If the reply names a different vendor, use the name given in the reply instead.
                If the reply doesn't clearly approve or reject, leave CmdCode null -- do not guess.
                """;

            var prompt = $$"""
                An administrator is replying to a request to link a new email sender to a vendor for an invoice-processing mailbox.

                {{context}}

                The text given is only the administrator's own new reply -- any quoted prior thread has already been stripped out.

                {{decisionInstructions}}

                Respond with ONLY a JSON object (no other text) in this exact shape:
                {"CmdCode": "APPROVE" | "REJECT" | null, "VendorName": string | null}
                """;

            return await claude.AskClaude<CmdResult>(content, prompt, []);
        }
    }
}
